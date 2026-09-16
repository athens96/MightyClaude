import Foundation
import Testing
import Darwin
@testable import MightyCore

@Suite(.serialized)
final class CLIUpdateTests {
    private var directories: [URL] = []
    deinit { for directory in directories { try? FileManager.default.removeItem(at: directory) } }

    private func fixture() throws -> URL {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-cli-update-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: root.appendingPathComponent("bin"), withIntermediateDirectories: true)
        directories.append(root)
        try Data("1.0.0\n".utf8).write(to: root.appendingPathComponent("version"))
        return root
    }
    private func script(_ source: String, at path: URL) throws {
        try FileManager.default.createDirectory(at: path.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data(("#!/bin/sh\n" + source).utf8).write(to: path)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: path.path)
    }
    private func link(_ target: URL, at path: URL) throws {
        try FileManager.default.createDirectory(at: path.deletingLastPathComponent(), withIntermediateDirectories: true)
        try FileManager.default.createSymbolicLink(at: path, withDestinationURL: target)
    }
    private func environment(_ root: URL, extraBins: [URL] = []) -> [String: String] {
        ["PATH": ([root.appendingPathComponent("bin").path] + extraBins.map(\.path) + ["/usr/bin", "/bin"]).joined(separator: ":"),
         "HOME": root.path, "FIXTURE_VERSION": root.appendingPathComponent("version").path,
         "FIXTURE_LOG": root.appendingPathComponent("arguments").path,
         "FIXTURE_PID": root.appendingPathComponent("pid").path,
         "FIXTURE_READY": root.appendingPathComponent("ready").path]
    }
    private let versionScript = "if [ \"$1\" = '--version' ]; then /bin/cat \"$FIXTURE_VERSION\"; exit 0; fi\n"
    private let successfulUpdate = "printf '<%s>\\n' \"$@\" > \"$FIXTURE_LOG\"\nprintf '2.0.0\\n' > \"$FIXTURE_VERSION\"\n"
    private func native(_ root: URL, update: String? = nil) throws -> URL {
        let binary = root.appendingPathComponent(".local/share/claude/versions/1.0.0")
        try script(versionScript + (update ?? successfulUpdate), at: binary)
        let launcher = root.appendingPathComponent("bin/claude"); try link(binary, at: launcher)
        return launcher
    }
    private func package(_ root: URL, name: String, binary: String, entry: String, version: String = "1.0.0") throws {
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let value: [String: Any] = ["name": name, "version": version, "bin": [binary: entry]]
        try JSONSerialization.data(withJSONObject: value).write(to: root.appendingPathComponent("package.json"))
    }
    private func npmFixture(_ root: URL, provider: String, packageName: String, version: String = "1.0.0") throws -> URL {
        // Spaces and literal shell metacharacters must survive as one --prefix
        // argument. A shell interpolating this would create the SENTINEL file.
        let prefix = root.appendingPathComponent("npm prefix $(touch SENTINEL)")
        let packageRoot = prefix.appendingPathComponent("lib/node_modules/" + packageName)
        try package(packageRoot, name: packageName, binary: provider, entry: "bin/entry.js", version: version)
        let binary = packageRoot.appendingPathComponent("bin/entry.js")
        try script(versionScript + "exit 91\n", at: binary)
        try link(binary, at: root.appendingPathComponent("bin/" + provider))
        let npmRoot = root.appendingPathComponent("node installation/lib/node_modules/npm")
        try package(npmRoot, name: "npm", binary: "npm", entry: "bin/npm-cli.js")
        let npm = npmRoot.appendingPathComponent("bin/npm-cli.js")
        try script(successfulUpdate, at: npm)
        try link(npm, at: root.appendingPathComponent("bin/npm"))
        try script("exec /bin/sh \"$@\"\n", at: root.appendingPathComponent("bin/node"))
        return prefix
    }
    private func waitForFile(_ url: URL) async throws {
        for _ in 0..<200 {
            if FileManager.default.fileExists(atPath: url.path) { return }
            try await Task.sleep(for: .milliseconds(10))
        }
        throw MightyError("Fixture process did not start")
    }

    @Test func nativeClaudeUsesLauncherAndReportsFreshVersion() async throws {
        let root = try fixture(); let launcher = try native(root)
        let service = CLIUpdateService(environment: environment(root), homeDirectory: root)
        let inspected = await service.inspect(provider: "claude")
        #expect(inspected.method == "native" && inspected.canUpdate)
        #expect(inspected.executablePath == launcher.path)
        let result = await service.update(provider: "claude")
        #expect(result.status == "updated"); #expect(result.beforeVersion == "1.0.0"); #expect(result.afterVersion == "2.0.0")
        #expect(try String(contentsOf: root.appendingPathComponent("arguments"), encoding: .utf8) == "<update>\n")
        let same = await service.update(provider: "claude")
        #expect(same.status == "current" && same.beforeVersion == same.afterVersion)
        #expect(!same.detail.contains("최신"))
        await service.shutdown()
    }

    @Test func homebrewUsesActualCaskOrFormulaAndOnlyNamedPackage() async throws {
        for (container, provider, package) in [("Caskroom", "codex", "codex"), ("Cellar", "gemini", "gemini-cli")] {
            let root = try fixture(); let brewRoot = root.appendingPathComponent("brew installation")
            let binary = brewRoot.appendingPathComponent("\(container)/\(package)/1.0.0/bin/\(provider)")
            try script(versionScript + "exit 91\n", at: binary)
            try link(binary, at: root.appendingPathComponent("bin/" + provider))
            try script(successfulUpdate + "printf '%s|%s|%s' \"$HOMEBREW_NO_INSTALL_CLEANUP\" \"$HOMEBREW_NO_INSTALLED_DEPENDENTS_CHECK\" \"$HOMEBREW_NO_AUTO_UPDATE\" >> \"$FIXTURE_LOG\"\n", at: brewRoot.appendingPathComponent("bin/brew"))
            let service = CLIUpdateService(environment: environment(root), homeDirectory: root)
            let inspected = await service.inspect(provider: provider)
            let cask = container == "Caskroom"
            #expect(inspected.method == (cask ? "brew-cask" : "brew-formula"))
            let result = await service.update(provider: provider)
            #expect(result.status == "updated")
            let log = try String(contentsOf: root.appendingPathComponent("arguments"), encoding: .utf8)
            #expect(log == "<upgrade>\n<\(cask ? "--cask" : "--formula")>\n<\(package)>\n1|1|")
            await service.shutdown()
        }
    }

    @Test func npmTargetsOfficialPackageAndOriginalPrefixWithPairedNode() async throws {
        for (provider, name) in [("claude", "@anthropic-ai/claude-code"), ("codex", "@openai/codex"), ("gemini", "@google/gemini-cli")] {
            let root = try fixture(); let prefix = try npmFixture(root, provider: provider, packageName: name)
            var env = environment(root); env["npm_config_prefix"] = root.appendingPathComponent("wrong prefix").path
            let service = CLIUpdateService(environment: env, homeDirectory: root)
            let inspected = await service.inspect(provider: provider)
            #expect(inspected.method == "npm" && inspected.canUpdate)
            let result = await service.update(provider: provider)
            #expect(result.status == "updated")
            let log = try String(contentsOf: root.appendingPathComponent("arguments"), encoding: .utf8)
            #expect(log == "<install>\n<--global>\n<--prefix>\n<\(prefix.path)>\n<\(name)@latest>\n<--no-audit>\n<--no-fund>\n")
            #expect(!FileManager.default.fileExists(atPath: root.appendingPathComponent("SENTINEL").path))
            await service.shutdown()
        }
    }

    @Test func missingUnknownAndUnhealthyInstallationsAreNeverInstalled() async throws {
        let root = try fixture(); let service = CLIUpdateService(environment: environment(root), homeDirectory: root)
        let missing = await service.update(provider: "claude")
        #expect(missing.status == "skipped" && missing.method == "missing")
        try script(versionScript + "printf 'MUST_NOT_RUN' > \"$FIXTURE_LOG\"\n", at: root.appendingPathComponent("bin/claude"))
        let manual = await service.update(provider: "claude")
        #expect(manual.status == "skipped" && manual.method == "unknown")
        try script("exit 7\n", at: root.appendingPathComponent("bin/codex"))
        let unhealthy = await service.update(provider: "codex")
        #expect(unhealthy.status == "skipped")
        let unsupported = await service.update(provider: "other")
        #expect(unsupported.status == "skipped")
        #expect(!FileManager.default.fileExists(atPath: root.appendingPathComponent("arguments").path))
        await service.shutdown()
    }

    @Test func npmRejectsWrongManifestAndPreservesPrereleaseChannels() async throws {
        for version in ["0.44.0-preview.1", "0.44.0-nightly.20260916", "not-semver"] {
            let root = try fixture(); _ = try npmFixture(root, provider: "gemini", packageName: "@google/gemini-cli", version: version)
            let service = CLIUpdateService(environment: environment(root), homeDirectory: root)
            let result = await service.update(provider: "gemini")
            #expect(result.status == "skipped" && result.method == "npm")
            #expect(!FileManager.default.fileExists(atPath: root.appendingPathComponent("arguments").path))
            await service.shutdown()
        }
        let root = try fixture(); let prefix = try npmFixture(root, provider: "gemini", packageName: "@google/gemini-cli")
        let packageRoot = prefix.appendingPathComponent("lib/node_modules/@google/gemini-cli")
        try package(packageRoot, name: "unrelated-package", binary: "gemini", entry: "bin/entry.js")
        let service = CLIUpdateService(environment: environment(root), homeDirectory: root)
        #expect(await service.update(provider: "gemini").status == "skipped")
        try package(packageRoot, name: "@google/gemini-cli", binary: "gemini", entry: "../outside.js")
        #expect(await service.update(provider: "gemini").status == "skipped")
        #expect(!FileManager.default.fileExists(atPath: root.appendingPathComponent("arguments").path))
        await service.shutdown()
    }

    @Test func failureDoesNotBlockSubsequentProviderUpdate() async throws {
        let root = try fixture(); _ = try native(root, update: "printf 'network unavailable' >&2\nexit 23\n")
        _ = try npmFixture(root, provider: "gemini", packageName: "@google/gemini-cli")
        let service = CLIUpdateService(environment: environment(root), homeDirectory: root)
        let failed = await service.update(provider: "claude")
        #expect(failed.status == "failed" && failed.beforeVersion == "1.0.0" && failed.afterVersion == nil)
        #expect(failed.detail.contains("23")); #expect(failed.output.contains("network unavailable"))
        #expect(await service.update(provider: "gemini").status == "updated")
        await service.shutdown()
    }

    @Test func cancellationStopsUpdaterAndRejectsConcurrentOrClosingRequests() async throws {
        let root = try fixture()
        _ = try native(root, update: "printf '%s' \"$$\" > \"$FIXTURE_PID\"\n/bin/sleep 30\n")
        let service = CLIUpdateService(environment: environment(root), homeDirectory: root)
        let operation = Task { await service.update(provider: "claude") }
        defer { operation.cancel() }
        try await waitForFile(root.appendingPathComponent("pid"))
        let pid = try #require(Int32(String(contentsOf: root.appendingPathComponent("pid"), encoding: .utf8)))
        #expect(await service.update(provider: "gemini").status == "busy")
        await service.cancel()
        #expect(await operation.value.status == "cancelled")
        #expect(Darwin.kill(pid, 0) != 0)
        await service.shutdown()
        #expect(await service.update(provider: "claude").status == "cancelled")
    }

    @Test func updaterTimeoutIsBoundedAndLeavesNoRunningProcess() async throws {
        let root = try fixture()
        _ = try native(root, update: "printf '%s' \"$$\" > \"$FIXTURE_PID\"\n/bin/sleep 30\n")
        let service = CLIUpdateService(environment: environment(root), homeDirectory: root, updateTimeout: 0.08)
        let began = Date(); let result = await service.update(provider: "claude")
        #expect(result.status == "failed"); #expect(Date().timeIntervalSince(began) < 3)
        let pid = try #require(Int32(String(contentsOf: root.appendingPathComponent("pid"), encoding: .utf8)))
        #expect(Darwin.kill(pid, 0) != 0)
        await service.shutdown()
    }

    @Test func providerCacheInvalidationReadsNewVersionImmediately() async throws {
        let root = try fixture(); let launcher = try native(root)
        let providers = ProviderService(binaryOverrides: ["claude": launcher], environment: environment(root))
        #expect(await providers.command(provider: "claude")?.version == "1.0.0")
        let updater = CLIUpdateService(environment: environment(root), homeDirectory: root)
        #expect(await updater.update(provider: "claude").status == "updated")
        #expect(await providers.command(provider: "claude")?.version == "1.0.0")
        await providers.invalidateCaches()
        #expect(await providers.command(provider: "claude")?.version == "2.0.0")
        await updater.shutdown(); await providers.shutdown()
    }

    @Test func invalidationCancelsAndJoinsAnOldVersionProbe() async throws {
        let root = try fixture(); let launcher = root.appendingPathComponent("bin/claude")
        try script("if [ -e \"$FIXTURE_READY\" ]; then printf '2.0.0\\n'; exit 0; fi\nprintf '%s' \"$$\" > \"$FIXTURE_PID\"\n/bin/sleep 30\n", at: launcher)
        let providers = ProviderService(binaryOverrides: ["claude": launcher], environment: environment(root))
        let pending = Task { await providers.command(provider: "claude") }
        defer { pending.cancel() }
        try await waitForFile(root.appendingPathComponent("pid"))
        let pid = try #require(Int32(String(contentsOf: root.appendingPathComponent("pid"), encoding: .utf8)))
        await providers.invalidateCaches()
        #expect(await pending.value == nil); #expect(Darwin.kill(pid, 0) != 0)
        try Data().write(to: root.appendingPathComponent("ready"))
        #expect(await providers.command(provider: "claude")?.version == "2.0.0")
        await providers.shutdown()
    }
}
