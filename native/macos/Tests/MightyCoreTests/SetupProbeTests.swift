import Foundation
import Testing
@testable import MightyCore

/// What the setup screen reads from this Mac, and what it shows and does with it.
struct SetupProbeTests {
    private func temporary() throws -> URL {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("setup-probe-" + UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }

    private func tool(_ name: String, in directory: URL, prints version: String?) throws {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let body = version.map { "[ \"$1\" = --version ] && printf '%s\\n' '\($0)'" } ?? "exit 1"
        let url = directory.appendingPathComponent(name)
        try Data("#!/bin/sh\n\(body)\n".utf8).write(to: url)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: url.path)
    }

    private func prober(_ root: URL, bin: URL, tools: String? = nil) -> SetupProber {
        SetupProber(environment: ["PATH": bin.path + ":/usr/bin:/bin", "HOME": root.path], home: root, versionTimeout: 10, commandLineToolsPath: { tools })
    }

    @Test func executablesAreReadFromTheGivenPathWithTheirVersion() async throws {
        let root = try temporary(); defer { try? FileManager.default.removeItem(at: root) }
        let bin = root.appendingPathComponent("bin")
        try tool("mighty-fixture-cli", in: bin, prints: "2.1.300 (Claude Code)")
        let prober = prober(root, bin: bin)
        #expect(await prober.probe(.executable(name: "mighty-fixture-cli", minimumVersion: nil)) == .installed(version: "2.1.300 (Claude Code)"))
        #expect(await prober.probe(.executable(name: "mighty-fixture-cli", minimumVersion: "2.1.271")) == .installed(version: "2.1.300 (Claude Code)"))
        #expect(await prober.probe(.executable(name: "mighty-not-there", minimumVersion: nil)) == .missing)
    }

    /// Bug (b): a Claude below the minimum is "needs update", not "needs install".
    @Test func anOldVersionIsOutdatedNotMissing() async throws {
        let root = try temporary(); defer { try? FileManager.default.removeItem(at: root) }
        let bin = root.appendingPathComponent("bin")
        try tool("mighty-old-cli", in: bin, prints: "2.1.199 (Claude Code)")
        try tool("mighty-silent-cli", in: bin, prints: nil)
        let prober = prober(root, bin: bin)
        let old = await prober.probe(.executable(name: "mighty-old-cli", minimumVersion: "2.1.271"))
        #expect(old == .outdated(version: "2.1.199 (Claude Code)", minimum: "2.1.271"))
        #expect(SetupRowState.derive(old, inRun: false, runFinished: true, finishesOutside: false) == .outdated)
        // There but silent about its version: it cannot prove the minimum.
        #expect(await prober.probe(.executable(name: "mighty-silent-cli", minimumVersion: "1.0")) == .outdated(version: nil, minimum: "1.0"))
        #expect(await prober.probe(.executable(name: "mighty-silent-cli", minimumVersion: nil)) == .installed(version: nil))
    }

    /// Bug (a): every probe reads again; nothing is cached between checks.
    @Test func aToolInstalledBetweenTwoChecksIsSeenByTheSecond() async throws {
        let root = try temporary(); defer { try? FileManager.default.removeItem(at: root) }
        let bin = root.appendingPathComponent("bin")
        let prober = prober(root, bin: bin)
        #expect(await prober.probe(.executable(name: "mighty-late-cli", minimumVersion: nil)) == .missing)
        try tool("mighty-late-cli", in: bin, prints: "0.9.0")
        #expect(await prober.probe(.executable(name: "mighty-late-cli", minimumVersion: nil)) == .installed(version: "0.9.0"))
    }

    @Test func versionsCompareByNumber() {
        #expect(SetupProber.versionNumbers("codex-cli 0.153.4") == [0, 153, 4])
        #expect(SetupProber.versionNumbers("v1.3") == [1, 3])
        #expect(SetupProber.versionNumbers("no digits") == nil)
        #expect(SetupProber.isAtLeast([2, 1, 271], [2, 1, 271]))
        #expect(SetupProber.isAtLeast([2, 2], [2, 1, 271]))
        #expect(!SetupProber.isAtLeast([2, 1, 99], [2, 1, 271]))
        #expect(!SetupProber.isAtLeast([1, 3], [1, 3, 3]))
    }

    @Test func pluginsSkillsToolsAndAppsAreReadFromDisk() async throws {
        let root = try temporary(); defer { try? FileManager.default.removeItem(at: root) }
        let registry = root.appendingPathComponent(".claude/plugins/installed_plugins.json")
        try FileManager.default.createDirectory(at: registry.deletingLastPathComponent(), withIntermediateDirectories: true)
        try JSONSerialization.data(withJSONObject: ["plugins": [
            "ouroboros@ouroboros": [["scope": "user"]],
            "superpowers@claude-community": [["scope": "project"]],
        ]]).write(to: registry)
        let skill = root.appendingPathComponent(".claude/skills/gstack/SKILL.md")
        try FileManager.default.createDirectory(at: skill.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data().write(to: skill)
        let app = root.appendingPathComponent("Fixture.app")
        try FileManager.default.createDirectory(at: app, withIntermediateDirectories: true)
        let prober = prober(root, bin: root, tools: root.path)
        #expect(await prober.probe(.claudePlugin(prefix: "ouroboros@")) == .installed(version: nil))
        // Only a user-scope record counts, and the prefix names a whole plugin.
        #expect(await prober.probe(.claudePlugin(prefix: "superpowers@")) == .missing)
        #expect(await prober.probe(.claudePlugin(prefix: "ouro@")) == .missing)
        #expect(await prober.probe(.skill(name: "gstack")) == .installed(version: nil))
        #expect(await prober.probe(.skill(name: "re0")) == .missing)
        #expect(await prober.probe(.commandLineTools) == .installed(version: nil))
        #expect(await self.prober(root, bin: root, tools: nil).probe(.commandLineTools) == .missing)
        #expect(await self.prober(root, bin: root, tools: root.appendingPathComponent("gone").path).probe(.commandLineTools) == .missing)
        #expect(await prober.probe(.appBundle(path: app.path)) == .installed(version: nil))
        #expect(await prober.probe(.appBundle(path: root.appendingPathComponent("Missing.app").path)) == .missing)
    }

    @Test func rowStatesFollowTheProbeAndTheRun() {
        #expect(SetupRowState.derive(nil, inRun: false, runFinished: false, finishesOutside: false) == .checking)
        #expect(SetupRowState.derive(.installed(version: nil), inRun: true, runFinished: false, finishesOutside: false) == .installed)
        #expect(SetupRowState.derive(.missing, inRun: false, runFinished: false, finishesOutside: false) == .missing)
        #expect(SetupRowState.derive(.missing, inRun: true, runFinished: false, finishesOutside: false) == .installing)
        // The script ended and the probe still does not see it: that is a failure.
        #expect(SetupRowState.derive(.missing, inRun: true, runFinished: true, finishesOutside: false) == .failed)
        // A system dialog finishes it, so it is still being installed.
        #expect(SetupRowState.derive(.missing, inRun: true, runFinished: true, finishesOutside: true) == .installing)
        #expect(SetupRowState.derive(.outdated(version: "1", minimum: "2"), inRun: true, runFinished: false, finishesOutside: false) == .installing)
    }

    @Test func theScreenOpensByItselfOnlyWhenNoAgentRunsOrARequiredItemIsMissing() throws {
        let catalog = try SetupPlanTests.catalog()
        var all: [String: SetupProbeResult] = [:]
        for item in catalog.items { all[item.id] = .installed(version: nil) }
        #expect(!SetupFirstRun.shouldPresent(catalog: catalog, results: all, platform: .macos, neverShow: false))
        // Optional tools missing alone do not open it.
        var optional = all; optional["glab"] = .missing; optional["tailscale"] = .missing; optional["codex"] = .missing
        #expect(!SetupFirstRun.shouldPresent(catalog: catalog, results: optional, platform: .macos, neverShow: false))
        // A required item missing or out of date does.
        var git = all; git["git"] = .missing
        #expect(SetupFirstRun.shouldPresent(catalog: catalog, results: git, platform: .macos, neverShow: false))
        var old = all; old["claude"] = .outdated(version: "2.0.0", minimum: "2.1.271")
        #expect(SetupFirstRun.shouldPresent(catalog: catalog, results: old, platform: .macos, neverShow: false))
        // No agent CLI at all.
        var none = all; none["claude"] = .missing; none["codex"] = .missing; none["agy"] = .missing
        #expect(SetupFirstRun.shouldPresent(catalog: catalog, results: none, platform: .macos, neverShow: false))
        #expect(SetupFirstRun.needsAttention(catalog: catalog, results: none, platform: .macos))
        // "Don't show again" wins; results not read yet never open it.
        #expect(!SetupFirstRun.shouldPresent(catalog: catalog, results: none, platform: .macos, neverShow: true))
        #expect(!SetupFirstRun.shouldPresent(catalog: catalog, results: [:], platform: .macos, neverShow: false))
    }

    /// Bug (a): when the probe sees an agent CLI the cached runtime does not, the runtime is read again.
    @Test func aChangedAgentMakesTheCachedRuntimeStale() throws {
        let catalog = try SetupPlanTests.catalog()
        var claude = ProviderOptions.fallbackRuntime("claude"); claude.version = "2.1.300 (Claude Code)"
        let codex = ProviderOptions.fallbackRuntime("codex"), gemini = ProviderOptions.fallbackRuntime("gemini")
        let runtime = [claude, codex, gemini]
        let same: [String: SetupProbeResult] = ["claude": .installed(version: "2.1.300 (Claude Code)"), "codex": .missing, "agy": .missing, "git": .missing]
        #expect(!SetupRuntimeSync.isStale(runtime: runtime, catalog: catalog, results: same))
        var installed = same; installed["codex"] = .installed(version: "codex-cli 0.160.0")
        #expect(SetupRuntimeSync.isStale(runtime: runtime, catalog: catalog, results: installed))
        var updated = same; updated["claude"] = .installed(version: "2.1.301 (Claude Code)")
        #expect(SetupRuntimeSync.isStale(runtime: runtime, catalog: catalog, results: updated))
        var removed = same; removed["claude"] = .missing
        #expect(SetupRuntimeSync.isStale(runtime: runtime, catalog: catalog, results: removed))
    }
}
