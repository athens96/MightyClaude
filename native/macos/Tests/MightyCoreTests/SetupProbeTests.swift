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

    private final class Calls: @unchecked Sendable { var git = 0 }

    private func prober(_ root: URL, path: [URL], tools: String? = nil, git: (Int32, String)? = (0, "git version 2.50.1"), calls: Calls = Calls()) -> SetupProber {
        SetupProber(environment: ["PATH": (path.map(\.path) + ["/usr/bin", "/bin"]).joined(separator: ":"), "HOME": root.path], home: root, versionTimeout: 10,
                    commandLineToolsPath: { tools }, gitVersion: { calls.git += 1; return git.map { (exitCode: $0.0, output: $0.1) } })
    }

    @Test func executablesAreReadFromTheGivenPathWithTheirVersion() async throws {
        let root = try temporary(); defer { try? FileManager.default.removeItem(at: root) }
        let bin = root.appendingPathComponent("bin")
        try tool("mighty-fixture-cli", in: bin, prints: "2.1.300 (Claude Code)")
        let prober = prober(root, path: [bin])
        #expect(await prober.probe(.executable(name: "mighty-fixture-cli", minimumVersion: nil)) == .installed(version: "2.1.300 (Claude Code)"))
        #expect(await prober.probe(.executable(name: "mighty-fixture-cli", minimumVersion: "2.1.271")) == .installed(version: "2.1.300 (Claude Code)"))
        #expect(await prober.probe(.executable(name: "mighty-not-there", minimumVersion: nil)) == .missing)
    }

    /// Bug (b): a Claude below the minimum is "needs update", not "needs install".
    @Test func anOldVersionIsOutdatedNotMissing() async throws {
        let root = try temporary(); defer { try? FileManager.default.removeItem(at: root) }
        let bin = root.appendingPathComponent("bin")
        try tool("mighty-old-cli", in: bin, prints: "2.1.199 (Claude Code)")
        try tool("mighty-beta-cli", in: bin, prints: "2.1.300-beta.1 (Claude Code)")
        let prober = prober(root, path: [bin])
        let old = await prober.probe(.executable(name: "mighty-old-cli", minimumVersion: "2.1.271"))
        #expect(old == .outdated(version: "2.1.199 (Claude Code)", minimum: "2.1.271"))
        #expect(SetupRowState.derive(old, inRun: false, runFinished: true) == .outdated)
        // A prerelease never meets the minimum, as ProviderService.supportsMods reads it.
        #expect(await prober.probe(.executable(name: "mighty-beta-cli", minimumVersion: "2.1.271")) == .outdated(version: "2.1.300-beta.1 (Claude Code)", minimum: "2.1.271"))
        #expect(!SetupProber.meets("2.1.300-beta", minimum: "2.1.271") && SetupProber.meets("2.1.300", minimum: "2.1.271"))
        #expect(ProviderService.supportsMods("2.1.300-beta") == SetupProber.meets("2.1.300-beta", minimum: "2.1.271"))
    }

    /// A copy that will not say its version is passed over for the next one on the
    /// PATH; with none that says it, the item counts as there, its version unchecked.
    @Test func aSilentCopyFallsBackToTheNextOneOnThePath() async throws {
        let root = try temporary(); defer { try? FileManager.default.removeItem(at: root) }
        let first = root.appendingPathComponent("first"), second = root.appendingPathComponent("second")
        try tool("mighty-two-cli", in: first, prints: nil)
        try tool("mighty-two-cli", in: second, prints: "2.2.0")
        try tool("mighty-silent-cli", in: first, prints: nil)
        let prober = prober(root, path: [first, second])
        #expect(await prober.probe(.executable(name: "mighty-two-cli", minimumVersion: "2.1.271")) == .installed(version: "2.2.0"))
        let silent = await prober.probe(.executable(name: "mighty-silent-cli", minimumVersion: "1.0"))
        #expect(silent == .unverified(minimum: "1.0") && silent.isInstalled)
        #expect(SetupRowState.derive(silent, inRun: false, runFinished: true) == .installed)
        #expect(await prober.probe(.executable(name: "mighty-silent-cli", minimumVersion: nil)) == .installed(version: nil))
    }

    /// Bug (a): every probe reads again; nothing is cached between checks.
    @Test func aToolInstalledBetweenTwoChecksIsSeenByTheSecond() async throws {
        let root = try temporary(); defer { try? FileManager.default.removeItem(at: root) }
        let bin = root.appendingPathComponent("bin")
        let prober = prober(root, path: [bin])
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

    /// The command line tools count only when git answers; an unaccepted Xcode
    /// license needs the user. Without the tools, git is never asked (its stub
    /// would offer to install them).
    @Test func theCommandLineToolsCountOnlyWhenGitAnswers() async throws {
        let root = try temporary(); defer { try? FileManager.default.removeItem(at: root) }
        let none = Calls()
        #expect(await prober(root, path: [], tools: nil, calls: none).probe(.commandLineTools) == .missing)
        #expect(await prober(root, path: [], tools: root.appendingPathComponent("gone").path, calls: none).probe(.commandLineTools) == .missing)
        #expect(none.git == 0)
        #expect(await prober(root, path: [], tools: root.path).probe(.commandLineTools) == .installed(version: nil))
        let license = await prober(root, path: [], tools: root.path, git: (69, "You have not agreed to the Xcode license agreements.")).probe(.commandLineTools)
        #expect(license == .needsAttention(.xcodeLicense) && !license.isInstalled)
        #expect(SetupRowState.derive(license, inRun: false, runFinished: true) == .attention)
        #expect(await prober(root, path: [], tools: root.path, git: nil).probe(.commandLineTools) == .needsAttention(.gitUnusable))
    }

    @Test func pluginsSkillsAndAppsAreReadFromDisk() async throws {
        let root = try temporary(); defer { try? FileManager.default.removeItem(at: root) }
        let registry = root.appendingPathComponent(".claude/plugins/installed_plugins.json")
        try FileManager.default.createDirectory(at: registry.deletingLastPathComponent(), withIntermediateDirectories: true)
        try JSONSerialization.data(withJSONObject: ["plugins": [
            "ouroboros@ouroboros": [["scope": "user"]],
            "oh-my-claudecode@omc": [["scope": "project"]],
        ]]).write(to: registry)
        let skill = root.appendingPathComponent(".claude/skills/gstack/SKILL.md")
        try FileManager.default.createDirectory(at: skill.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data().write(to: skill)
        let appName = "MightyFixture-\(UUID().uuidString.prefix(8)).app"
        let prober = prober(root, path: [root.appendingPathComponent("bin")])
        #expect(await prober.probe(.claudePlugin(prefix: "ouroboros@")) == .installed(version: nil))
        // Only a user-scope record counts, and the prefix names a whole plugin.
        #expect(await prober.probe(.claudePlugin(prefix: "oh-my-claudecode@")) == .missing)
        #expect(await prober.probe(.claudePlugin(prefix: "ouro@")) == .missing)
        #expect(await prober.probe(.skill(name: "gstack")) == .installed(version: nil))
        #expect(await prober.probe(.skill(name: "re0")) == .missing)
        // An app in ~/Applications, or its command on the PATH, counts too.
        #expect(await prober.probe(.appBundle(name: appName, executable: "mighty-fixture-ts")) == .missing)
        try FileManager.default.createDirectory(at: root.appendingPathComponent("Applications/" + appName), withIntermediateDirectories: true)
        #expect(await prober.probe(.appBundle(name: appName, executable: nil)) == .installed(version: nil))
        try tool("mighty-fixture-ts", in: root.appendingPathComponent("bin"), prints: "1.0")
        #expect(await prober.probe(.appBundle(name: "Other-\(appName)", executable: "mighty-fixture-ts")) == .installed(version: nil))
    }

    /// A skill the setup screen placed counts only once its setup script finished;
    /// one the user put there themselves counts as it is.
    @Test func anAppPlacedSkillCountsOnlyAfterItsSetupFinished() throws {
        let root = try temporary(); defer { try? FileManager.default.removeItem(at: root) }
        let folder = root.appendingPathComponent("gstack", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        #expect(!SetupProber.skillInstalled(folder))
        try Data().write(to: folder.appendingPathComponent("SKILL.md"))
        #expect(SetupProber.skillInstalled(folder))
        try Data().write(to: folder.appendingPathComponent(SetupProber.skillInstallingMarker))
        #expect(!SetupProber.skillInstalled(folder))
        try Data().write(to: folder.appendingPathComponent(SetupProber.skillDoneMarker))
        #expect(SetupProber.skillInstalled(folder))
    }

    @Test func rowStatesFollowTheProbeAndTheRun() throws {
        #expect(SetupRowState.derive(nil, inRun: false, runFinished: false) == .checking)
        #expect(SetupRowState.derive(.installed(version: nil), inRun: true, runFinished: false) == .installed)
        #expect(SetupRowState.derive(.missing, inRun: false, runFinished: false) == .missing)
        #expect(SetupRowState.derive(.missing, inRun: true, runFinished: false) == .installing)
        // The script ended and the probe still does not see it: that is a failure,
        // also for the command line tools, whose window the script waited for.
        #expect(SetupRowState.derive(.missing, inRun: true, runFinished: true) == .failed)
        #expect(SetupRowState.derive(.outdated(version: "1", minimum: "2"), inRun: true, runFinished: false) == .installing)
        let catalog = try SetupPlanTests.catalog()
        let git = try #require(catalog.item("git")), bun = try #require(catalog.item("bun")), claude = try #require(catalog.item("claude")), tailscale = try #require(catalog.item("tailscale"))
        // A failed item can be picked again; an out-of-date tool re-runs its recipe; an out-of-date agent CLI updates instead.
        #expect(SetupRowState.isSelectable(.failed, item: git, platform: .macos))
        #expect(SetupRowState.isSelectable(.outdated, item: bun, platform: .macos))
        #expect(!SetupRowState.isSelectable(.outdated, item: claude, platform: .macos))
        #expect(!SetupRowState.isSelectable(.attention, item: git, platform: .macos))
        #expect(!SetupRowState.isSelectable(.missing, item: tailscale, platform: .macos))
    }

    @Test func theScreenOpensByItselfOnlyWhenNoAgentRunsOrGitIsNotReady() throws {
        let catalog = try SetupPlanTests.catalog()
        var all: [String: SetupProbeResult] = [:]
        for item in catalog.items { all[item.id] = .installed(version: nil) }
        #expect(!SetupFirstRun.shouldPresent(catalog: catalog, results: all, platform: .macos, neverShow: false))
        // Claude is not required: one working agent CLI is enough.
        var codexOnly = all; codexOnly["claude"] = .missing; codexOnly["agy"] = .missing; codexOnly["glab"] = .missing
        #expect(!SetupFirstRun.shouldPresent(catalog: catalog, results: codexOnly, platform: .macos, neverShow: false))
        var oldClaude = codexOnly; oldClaude["claude"] = .outdated(version: "2.0.0", minimum: "2.1.271")
        #expect(!SetupFirstRun.shouldPresent(catalog: catalog, results: oldClaude, platform: .macos, neverShow: false))
        // The command line tools missing, or git refusing to run, opens it.
        var git = all; git["git"] = .missing
        #expect(SetupFirstRun.shouldPresent(catalog: catalog, results: git, platform: .macos, neverShow: false))
        var license = all; license["git"] = .needsAttention(.xcodeLicense)
        #expect(SetupFirstRun.shouldPresent(catalog: catalog, results: license, platform: .macos, neverShow: false))
        // No agent CLI at all (an out-of-date one cannot run).
        var none = all; none["claude"] = .outdated(version: "2.0.0", minimum: "2.1.271"); none["codex"] = .missing; none["agy"] = .missing
        #expect(SetupFirstRun.shouldPresent(catalog: catalog, results: none, platform: .macos, neverShow: false))
        #expect(SetupFirstRun.needsAttention(catalog: catalog, results: none, platform: .macos))
        // "Don't show again" wins; results not read yet never open it.
        #expect(!SetupFirstRun.shouldPresent(catalog: catalog, results: none, platform: .macos, neverShow: true))
        #expect(!SetupFirstRun.shouldPresent(catalog: catalog, results: [:], platform: .macos, neverShow: false))
    }

    @Test func theFirstShowingTicksMissingRequiredItemsAndClaudeWhenNoAgentRuns() throws {
        let catalog = try SetupPlanTests.catalog()
        var results: [String: SetupProbeResult] = [:]
        for item in catalog.items { results[item.id] = .missing }
        #expect(SetupFirstRun.preselection(catalog: catalog, results: results, platform: .macos) == ["git", "claude"])
        results["codex"] = .installed(version: "codex-cli 0.160.0"); results["git"] = .installed(version: nil)
        #expect(SetupFirstRun.preselection(catalog: catalog, results: results, platform: .macos).isEmpty)
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
