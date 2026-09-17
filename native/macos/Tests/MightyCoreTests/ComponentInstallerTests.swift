import Foundation
import Testing
@testable import MightyCore

struct ComponentInstallerTests {
    private func root() throws -> URL {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("components-" + UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: url.appendingPathComponent("bin"), withIntermediateDirectories: true)
        return url
    }
    private func script(_ source: String, at path: URL) throws {
        try FileManager.default.createDirectory(at: path.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data(("#!/bin/sh\n" + source).utf8).write(to: path)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: path.path)
    }
    private func installer(_ root: URL, app: Bool, cli: Bool, brew: Bool) -> TailscaleInstaller {
        TailscaleInstaller(environment: ["PATH": root.appendingPathComponent("bin").path + ":/usr/bin:/bin", "HOME": root.path],
                           applicationPaths: [root.appendingPathComponent(app ? "Tailscale.app" : "Missing.app")],
                           cliCandidates: [root.appendingPathComponent(cli ? "bin/tailscale" : "bin/none")],
                           brewCandidates: [root.appendingPathComponent(brew ? "bin/brew" : "bin/nobrew")],
                           statusTimeout: 3, installTimeout: 5, loginTimeout: 3)
    }

    @Test func phasesFollowTheBackendStateAndInstallFallsBackToTheStore() async throws {
        let root = try root()
        defer { try? FileManager.default.removeItem(at: root) }
        // Nothing installed, no Homebrew: only the store is offered.
        #expect(await installer(root, app: false, cli: false, brew: false).inspect().phase == "missing")
        #expect(await installer(root, app: false, cli: false, brew: false).install() == .openStore)
        // App bundle without a working CLI: launch it.
        try FileManager.default.createDirectory(at: root.appendingPathComponent("Tailscale.app"), withIntermediateDirectories: true)
        #expect(await installer(root, app: true, cli: false, brew: false).inspect().phase == "needs-launch")
        // CLI driven by a mode file.
        try script("""
        case "$1" in
          version) echo "1.90.0"; exit 0;;
          status) mode=$(cat "\(root.path)/mode"); echo "{\\"BackendState\\":\\"$mode\\",\\"Self\\":{\\"DNSName\\":\\"young-mac.tail1234.ts.net.\\",\\"TailscaleIPs\\":[\\"100.64.9.9\\"]},\\"TailscaleIPs\\":[\\"100.64.9.9\\"]}"; exit 0;;
          login) echo "To authenticate, visit:"; echo ""; echo "  https://login.tailscale.com/a/abc123def"; sleep 2; exit 0;;
          up) echo "$@" > "\(root.path)/up.log"; exit 0;;
        esac
        exit 1
        """, at: root.appendingPathComponent("bin/tailscale"))
        for (mode, phase) in [("Running", "connected"), ("NeedsLogin", "needs-login"), ("Stopped", "needs-connect"), ("NoState", "needs-launch")] {
            try Data(mode.utf8).write(to: root.appendingPathComponent("mode"))
            let inspection = await installer(root, app: true, cli: true, brew: false).inspect()
            #expect(inspection.phase == phase, "\(mode) → \(inspection.phase)")
            if phase == "connected" { #expect(inspection.addresses == ["100.64.9.9"] && inspection.detail.contains("young-mac.tail1234.ts.net") && inspection.version == "1.90.0") }
        }
        let login = installer(root, app: true, cli: true, brew: false)
        #expect(await login.loginURL()?.absoluteString == "https://login.tailscale.com/a/abc123def")
        #expect(await login.connect() == nil)
        #expect(try String(contentsOf: root.appendingPathComponent("up.log"), encoding: .utf8).trimmingCharacters(in: .whitespacesAndNewlines) == "up")
    }

    @Test func homebrewInstallUsesTheCaskNonInteractivelyAndReportsFailures() async throws {
        let root = try root()
        defer { try? FileManager.default.removeItem(at: root) }
        try script("""
        echo "$@" > "\(root.path)/brew.log"; env | grep -E '^(NONINTERACTIVE|HOMEBREW_NO_AUTO_UPDATE)=' | sort >> "\(root.path)/brew.log"
        if [ -f "\(root.path)/fail" ]; then echo "Error: boom" >&2; exit 1; fi
        echo "🍺  tailscale was successfully installed!"
        """, at: root.appendingPathComponent("bin/brew"))
        let installer = installer(root, app: false, cli: false, brew: true)
        let inspection = await installer.inspect()
        #expect(inspection.phase == "missing" && inspection.brewAvailable)
        if case .installed(let output) = await installer.install() { #expect(output.contains("successfully")) } else { Issue.record("install should succeed") }
        let log = try String(contentsOf: root.appendingPathComponent("brew.log"), encoding: .utf8)
        #expect(log.hasPrefix("install --cask tailscale\n") && log.contains("NONINTERACTIVE=1") && log.contains("HOMEBREW_NO_AUTO_UPDATE=1"))
        try Data().write(to: root.appendingPathComponent("fail"))
        if case .failed(let message) = await installer.install() { #expect(message.contains("boom")) } else { Issue.record("install should fail") }
        #expect(ComponentCatalog.installCommand(provider: "claude") == "npm install -g @anthropic-ai/claude-code")
        #expect(ComponentCatalog.installCommand(provider: "browser") == nil)
    }
}
