import Foundation
import Testing
@testable import MightyCore

// MARK: - Fake executor

/// Records every argv it receives; each call pops the front of `responses`.
/// Returns `defaultResponse` when the queue is empty.
final class FakeRunnerExecutor: ToolkitRunnerExecutor, @unchecked Sendable {
    private var responses: [ToolkitCommandOutput]
    private let defaultResponse: ToolkitCommandOutput
    private(set) var calls: [[String]] = []

    init(responses: [ToolkitCommandOutput] = [],
         default defaultResponse: ToolkitCommandOutput = .success) {
        self.responses = responses
        self.defaultResponse = defaultResponse
    }

    func run(_ argv: [String]) -> ToolkitCommandOutput {
        calls.append(argv)
        if responses.isEmpty { return defaultResponse }
        return responses.removeFirst()
    }
}

// MARK: - Helpers

private func tempDir(_ label: String) -> URL {
    let url = FileManager.default.temporaryDirectory
        .appendingPathComponent("toolkit-runner-\(label)-\(UUID().uuidString)", isDirectory: true)
    try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
    return url
}

private func touch(_ url: URL) throws {
    try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
    try Data().write(to: url)
}

private func writeJSON(_ value: Any, to url: URL) throws {
    try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
    try JSONSerialization.data(withJSONObject: value).write(to: url)
}

private func makeContext(home: URL, appData: URL? = nil, brewPrefixes: [String] = []) -> ToolkitProbeContext {
    ToolkitProbeContext(home: home, environment: [:],
                        appDataDir: appData ?? home.appendingPathComponent("appdata"),
                        brewPrefixes: brewPrefixes)
}

private let fakeSHA = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2"

private func networkOutput(_ detail: String = "") -> ToolkitCommandOutput {
    .init(exitCode: 1, output: "fatal: Could not resolve host: github.com \(detail)")
}

// MARK: - Suite

@Suite struct ToolkitRunnerTests {

    // MARK: – Plan: confirmation sheet content

    @Test func planListsMissingApprovedEntryWithCommands() async throws {
        let dir = tempDir("plan-approved")
        defer { try? FileManager.default.removeItem(at: dir) }
        let home = dir.appendingPathComponent("home")
        let store = ToolkitStore(directory: dir.appendingPathComponent("data"))
        let entry = ToolkitEntry(entryId: "my-pkg", displayName: "My Pkg", source: .user,
                                 install: .package(manager: .brew, name: "ripgrep"))
        try await store.addEntry(entry)
        let fakeExec = FakeToolkitExecutor(responses: [])
        try await store.approve(entryId: "my-pkg", executor: fakeExec)
        let runner = ToolkitRunner(store: store, probeContext: makeContext(home: home))
        let items = await runner.plan()
        #expect(items.count == 2) // bundled + user
        let userItem = items.first { $0.entry.entryId == "my-pkg" }!
        if case .run(let cmds) = userItem.action {
            #expect(cmds == [["brew", "install", "ripgrep"]])
        } else {
            Issue.record("Expected .run for approved entry")
        }
    }

    @Test func planMarksUnapprovedEntryAsSkip() async throws {
        let dir = tempDir("plan-unapproved")
        defer { try? FileManager.default.removeItem(at: dir) }
        let home = dir.appendingPathComponent("home")
        let store = ToolkitStore(directory: dir.appendingPathComponent("data"))
        let entry = ToolkitEntry(entryId: "my-pkg", displayName: "My Pkg", source: .user,
                                 install: .package(manager: .npm, name: "typescript"))
        try await store.addEntry(entry)
        let runner = ToolkitRunner(store: store, probeContext: makeContext(home: home))
        let items = await runner.plan()
        let userItem = items.first { $0.entry.entryId == "my-pkg" }!
        #expect(userItem.action == .skip)
    }

    @Test func planExcludesAlreadyInstalledEntry() async throws {
        let dir = tempDir("plan-installed")
        defer { try? FileManager.default.removeItem(at: dir) }
        let home = dir.appendingPathComponent("home")
        // Pre-create the brew opt dir so the probe says installed
        let fakePrefix = dir.appendingPathComponent("brew")
        try FileManager.default.createDirectory(
            at: fakePrefix.appendingPathComponent("opt/ripgrep"), withIntermediateDirectories: true)
        let store = ToolkitStore(directory: dir.appendingPathComponent("data"))
        let entry = ToolkitEntry(entryId: "my-pkg", displayName: "My Pkg", source: .user,
                                 install: .package(manager: .brew, name: "ripgrep"))
        try await store.addEntry(entry)
        let fakeExec = FakeToolkitExecutor(responses: [])
        try await store.approve(entryId: "my-pkg", executor: fakeExec)
        let ctx = ToolkitProbeContext(home: home, environment: [:], appDataDir: home,
                                      brewPrefixes: [fakePrefix.path])
        let runner = ToolkitRunner(store: store, probeContext: ctx)
        let items = await runner.plan()
        // User entry is already installed → not in plan
        #expect(items.allSatisfy { $0.entry.entryId != "my-pkg" })
    }

    @Test func planListsBothApprovedAndUnapprovedEntries() async throws {
        let dir = tempDir("plan-mixed")
        defer { try? FileManager.default.removeItem(at: dir) }
        let home = dir.appendingPathComponent("home")
        let store = ToolkitStore(directory: dir.appendingPathComponent("data"))
        let approved = ToolkitEntry(entryId: "approved", displayName: "A", source: .user,
                                     install: .package(manager: .brew, name: "jq"))
        let unapproved = ToolkitEntry(entryId: "unapproved", displayName: "B", source: .user,
                                       install: .package(manager: .npm, name: "typescript"))
        try await store.addEntry(approved)
        try await store.addEntry(unapproved)
        let fakeExec = FakeToolkitExecutor(responses: [])
        try await store.approve(entryId: "approved", executor: fakeExec)
        let runner = ToolkitRunner(store: store, probeContext: makeContext(home: home))
        let items = await runner.plan()
        let approvedItem = items.first { $0.entry.entryId == "approved" }!
        let unapprovedItem = items.first { $0.entry.entryId == "unapproved" }!
        if case .run(let cmds) = approvedItem.action {
            #expect(cmds == [["brew", "install", "jq"]])
        } else {
            Issue.record("Expected .run for approved entry")
        }
        #expect(unapprovedItem.action == .skip)
    }

    // MARK: – Run: order

    @Test func commandsRunInPlanOrder() async throws {
        let dir = tempDir("run-order")
        defer { try? FileManager.default.removeItem(at: dir) }
        let home = dir.appendingPathComponent("home")
        let store = ToolkitStore(directory: dir.appendingPathComponent("data"))
        let e1 = ToolkitEntry(entryId: "a", displayName: "A", source: .user,
                               install: .package(manager: .brew, name: "alpha"))
        let e2 = ToolkitEntry(entryId: "b", displayName: "B", source: .user,
                               install: .package(manager: .brew, name: "beta"))
        try await store.addEntry(e1); try await store.addEntry(e2)
        let fakeExec = FakeToolkitExecutor(responses: [])
        try await store.approve(entryId: "a", executor: fakeExec)
        try await store.approve(entryId: "b", executor: fakeExec)
        let runner = ToolkitRunner(store: store, probeContext: makeContext(home: home))
        let items = await runner.plan()
        let executor = FakeRunnerExecutor()
        _ = await runner.run(plan: items, executor: executor)
        // First call should be for "a", second for "b"
        let aIdx = executor.calls.firstIndex(of: ["brew", "install", "alpha"])!
        let bIdx = executor.calls.firstIndex(of: ["brew", "install", "beta"])!
        #expect(aIdx < bIdx)
    }

    // MARK: – Run: failure continuation

    @Test func failingItemDoesNotStopLaterItems() async throws {
        let dir = tempDir("run-continue")
        defer { try? FileManager.default.removeItem(at: dir) }
        let home = dir.appendingPathComponent("home")
        let store = ToolkitStore(directory: dir.appendingPathComponent("data"))
        let e1 = ToolkitEntry(entryId: "a", displayName: "A", source: .user,
                               install: .package(manager: .brew, name: "alpha"))
        let e2 = ToolkitEntry(entryId: "b", displayName: "B", source: .user,
                               install: .package(manager: .brew, name: "beta"))
        try await store.addEntry(e1); try await store.addEntry(e2)
        let fakeExec = FakeToolkitExecutor(responses: [])
        try await store.approve(entryId: "a", executor: fakeExec)
        try await store.approve(entryId: "b", executor: fakeExec)
        let runner = ToolkitRunner(store: store, probeContext: makeContext(home: home))
        let plan = await runner.plan()
        // First item's command fails (non-zero, no probe file)
        let executor = FakeRunnerExecutor(responses: [.failure(output: "install failed")],
                                           default: .failure(output: "install failed"))
        let results = await runner.run(plan: plan, executor: executor)
        // Both user entries should appear in results
        let ids = results.map(\.entryId)
        #expect(ids.contains("a"))
        #expect(ids.contains("b"))
        // Both are failed (no probe files created)
        #expect(results.allSatisfy { $0.entryId == "mighty-styles" || $0.verdict == .failed })
    }

    // MARK: – Run: retry logic

    @Test func fetchStepRetriedOnceOnNetworkError() async throws {
        let dir = tempDir("run-retry")
        defer { try? FileManager.default.removeItem(at: dir) }
        let home = dir.appendingPathComponent("home")
        // Pre-install bundled mighty-styles so it is not in the plan
        try writeJSON(
            ["version": 2, "plugins": ["mighty-styles@mighty-styles": [["installPath": "/x", "scope": "user"]]]],
            to: home.appendingPathComponent(".claude/plugins/installed_plugins.json"))
        let store = ToolkitStore(directory: dir.appendingPathComponent("data"))
        let entry = ToolkitEntry(entryId: "pkg", displayName: "Pkg", source: .user,
                                  install: .package(manager: .brew, name: "ripgrep"))
        try await store.addEntry(entry)
        let fakeExec = FakeToolkitExecutor(responses: [])
        try await store.approve(entryId: "pkg", executor: fakeExec)
        let runner = ToolkitRunner(store: store, probeContext: makeContext(home: home))
        let plan = await runner.plan()
        // First call: network error → should retry; second call: also fails (no probe file)
        let executor = FakeRunnerExecutor(
            responses: [networkOutput(), .failure(output: "still failed")],
            default: .success)
        _ = await runner.run(plan: plan, executor: executor)
        let brewCalls = executor.calls.filter { $0 == ["brew", "install", "ripgrep"] }
        #expect(brewCalls.count == 2) // exactly one retry
    }

    @Test func fetchStepNotRetriedOnNonNetworkError() async throws {
        let dir = tempDir("run-no-retry")
        defer { try? FileManager.default.removeItem(at: dir) }
        let home = dir.appendingPathComponent("home")
        let store = ToolkitStore(directory: dir.appendingPathComponent("data"))
        let entry = ToolkitEntry(entryId: "pkg", displayName: "Pkg", source: .user,
                                  install: .package(manager: .brew, name: "ripgrep"))
        try await store.addEntry(entry)
        let fakeExec = FakeToolkitExecutor(responses: [])
        try await store.approve(entryId: "pkg", executor: fakeExec)
        let runner = ToolkitRunner(store: store, probeContext: makeContext(home: home))
        let plan = await runner.plan()
        // Non-network failure → no retry
        let executor = FakeRunnerExecutor(
            responses: [.failure(output: "permission denied")],
            default: .success)
        _ = await runner.run(plan: plan, executor: executor)
        let brewCalls = executor.calls.filter { $0 == ["brew", "install", "ripgrep"] }
        #expect(brewCalls.count == 1) // no retry
    }

    @Test func networkRetryHappensAtMostOnce() async throws {
        let dir = tempDir("run-retry-once")
        defer { try? FileManager.default.removeItem(at: dir) }
        let home = dir.appendingPathComponent("home")
        let store = ToolkitStore(directory: dir.appendingPathComponent("data"))
        let entry = ToolkitEntry(entryId: "pkg", displayName: "Pkg", source: .user,
                                  install: .package(manager: .npm, name: "typescript"))
        try await store.addEntry(entry)
        let fakeExec = FakeToolkitExecutor(responses: [])
        try await store.approve(entryId: "pkg", executor: fakeExec)
        let runner = ToolkitRunner(store: store, probeContext: makeContext(home: home))
        let plan = await runner.plan()
        // Both calls return network error; should not retry more than once
        let executor = FakeRunnerExecutor(
            responses: [networkOutput(), networkOutput()],
            default: networkOutput())
        _ = await runner.run(plan: plan, executor: executor)
        let npmCalls = executor.calls.filter { $0 == ["npm", "install", "-g", "typescript"] }
        #expect(npmCalls.count == 2) // original + exactly one retry
    }

    @Test func nonFetchStepNotRetried() async throws {
        let dir = tempDir("run-no-retry-mcp")
        defer { try? FileManager.default.removeItem(at: dir) }
        let home = dir.appendingPathComponent("home")
        let store = ToolkitStore(directory: dir.appendingPathComponent("data"))
        let entry = ToolkitEntry(entryId: "srv", displayName: "Srv", source: .user,
                                  install: .mcp(name: "my-mcp", executable: "node", args: ["server.js"]))
        try await store.addEntry(entry)
        let fakeExec = FakeToolkitExecutor(responses: [])
        try await store.approve(entryId: "srv", executor: fakeExec)
        let runner = ToolkitRunner(store: store, probeContext: makeContext(home: home))
        let plan = await runner.plan()
        // claude mcp add is not a fetch step — network error still means no retry
        let executor = FakeRunnerExecutor(
            responses: [networkOutput()],
            default: .success)
        _ = await runner.run(plan: plan, executor: executor)
        let mcpCalls = executor.calls.filter { $0.starts(with: ["claude", "mcp", "add"]) }
        #expect(mcpCalls.count == 1) // no retry
    }

    // MARK: – Run: probe-based verdict

    @Test func exitZeroWithMissingProbeYieldsFailedVerdict() async throws {
        let dir = tempDir("run-exit0-missing")
        defer { try? FileManager.default.removeItem(at: dir) }
        let home = dir.appendingPathComponent("home")
        let store = ToolkitStore(directory: dir.appendingPathComponent("data"))
        let entry = ToolkitEntry(entryId: "pkg", displayName: "Pkg", source: .user,
                                  install: .package(manager: .brew, name: "ripgrep"))
        try await store.addEntry(entry)
        let fakeExec = FakeToolkitExecutor(responses: [])
        try await store.approve(entryId: "pkg", executor: fakeExec)
        let runner = ToolkitRunner(store: store, probeContext: makeContext(home: home))
        let plan = await runner.plan()
        // Executor always returns exit 0, but no probe file created
        let executor = FakeRunnerExecutor(default: .success)
        let results = await runner.run(plan: plan, executor: executor)
        let userResult = results.first { $0.entryId == "pkg" }!
        #expect(userResult.verdict == .failed)
    }

    @Test func nonZeroExitWithInstalledProbeYieldsInstalledVerdict() async throws {
        let dir = tempDir("run-nonzero-installed")
        defer { try? FileManager.default.removeItem(at: dir) }
        let home = dir.appendingPathComponent("home")
        let fakePrefix = dir.appendingPathComponent("brew")
        // Pre-create the brew opt dir BEFORE the run (simulate already-present after non-zero)
        try FileManager.default.createDirectory(
            at: fakePrefix.appendingPathComponent("opt/ripgrep"), withIntermediateDirectories: true)
        let store = ToolkitStore(directory: dir.appendingPathComponent("data"))
        let entry = ToolkitEntry(entryId: "pkg", displayName: "Pkg", source: .user,
                                  install: .package(manager: .brew, name: "ripgrep"))
        try await store.addEntry(entry)
        let fakeExec = FakeToolkitExecutor(responses: [])
        try await store.approve(entryId: "pkg", executor: fakeExec)
        let ctx = ToolkitProbeContext(home: home, environment: [:], appDataDir: home,
                                      brewPrefixes: [fakePrefix.path])
        let runner = ToolkitRunner(store: store, probeContext: ctx)
        // Entry is already installed, so plan() should return empty for user entry
        // To test non-zero+installed, manually build a plan item and run it
        let planItem = ToolkitPlanItem(entry: entry, action: .run(commands: [["brew", "install", "ripgrep"]]))
        // Executor returns non-zero (e.g. "already installed" path), but probe says installed
        let executor = FakeRunnerExecutor(default: .failure(output: "already installed"))
        let results = await runner.run(plan: [planItem], executor: executor)
        let userResult = results.first { $0.entryId == "pkg" }!
        #expect(userResult.verdict == .installed)
    }

    // MARK: – Run: skipped verdict

    @Test func unapprovedEntryYieldsSkippedVerdict() async throws {
        let dir = tempDir("run-skip")
        defer { try? FileManager.default.removeItem(at: dir) }
        let home = dir.appendingPathComponent("home")
        let store = ToolkitStore(directory: dir.appendingPathComponent("data"))
        let entry = ToolkitEntry(entryId: "pkg", displayName: "Pkg", source: .user,
                                  install: .package(manager: .brew, name: "ripgrep"))
        try await store.addEntry(entry)
        let runner = ToolkitRunner(store: store, probeContext: makeContext(home: home))
        let plan = await runner.plan()
        let executor = FakeRunnerExecutor(default: .success)
        let results = await runner.run(plan: plan, executor: executor)
        let userResult = results.first { $0.entryId == "pkg" }!
        #expect(userResult.verdict == .skipped)
        // No commands should have been run for the skipped entry
        #expect(executor.calls.filter { $0 == ["brew", "install", "ripgrep"] }.isEmpty)
    }

    // MARK: – Second run

    @Test func secondRunPlansOnlyStillMissingItems() async throws {
        let dir = tempDir("run-second")
        defer { try? FileManager.default.removeItem(at: dir) }
        let home = dir.appendingPathComponent("home")
        let appData = dir.appendingPathComponent("appdata")
        let store = ToolkitStore(directory: dir.appendingPathComponent("data"))
        // Use a repoScript entry so the runner writes the marker on script exit 0
        let entry = ToolkitEntry(entryId: "setup", displayName: "Setup", source: .user,
                                  install: .repoScript(url: "https://github.com/x/setup.git",
                                                        ref: fakeSHA,
                                                        scriptPath: "install.sh"))
        try await store.addEntry(entry)
        // Approve with a 40-hex SHA ref (no ls-remote needed)
        let fakeApproveExec = FakeToolkitExecutor(responses: [])
        try await store.approve(entryId: "setup", executor: fakeApproveExec)
        let ctx = ToolkitProbeContext(home: home, environment: [:], appDataDir: appData)
        let runner = ToolkitRunner(store: store, probeContext: ctx)
        // First plan should include the entry
        let plan1 = await runner.plan()
        let setupItem1 = plan1.first { $0.entry.entryId == "setup" }
        #expect(setupItem1 != nil)
        // Run with exit 0 for all steps → runner writes marker → probe sees it
        let executor = FakeRunnerExecutor(default: .success)
        let results = await runner.run(plan: plan1, executor: executor)
        let setupResult = results.first { $0.entryId == "setup" }!
        #expect(setupResult.verdict == .installed)
        // Second plan: entry is now installed → not in plan
        let plan2 = await runner.plan()
        #expect(plan2.allSatisfy { $0.entry.entryId != "setup" })
    }

    // MARK: – isFetchStep classification

    @Test func isFetchStepIdentifiesFetchCommands() {
        #expect(ToolkitRunner.isFetchStep(["git", "clone", "--no-checkout", "https://github.com/x/y"]))
        #expect(ToolkitRunner.isFetchStep(["git", "ls-remote", "https://github.com/x/y"]))
        #expect(ToolkitRunner.isFetchStep(["brew", "install", "ripgrep"]))
        #expect(ToolkitRunner.isFetchStep(["npm", "install", "-g", "typescript"]))
        #expect(ToolkitRunner.isFetchStep(["claude", "plugin", "marketplace", "add", "o/r", "--name", "m"]))
        #expect(ToolkitRunner.isFetchStep(["claude", "plugin", "install", "p@m", "--scope", "user"]))
    }

    @Test func isFetchStepRejectsNonFetchCommands() {
        #expect(!ToolkitRunner.isFetchStep(["git", "-C", "/tmp", "checkout", "abc"]))
        #expect(!ToolkitRunner.isFetchStep(["claude", "mcp", "add", "--scope", "user", "n", "--", "node"]))
        #expect(!ToolkitRunner.isFetchStep(["/usr/local/repo/install.sh"]))
        #expect(!ToolkitRunner.isFetchStep([]))
    }

    @Test func isNetworkErrorMatchesKnownPatterns() {
        #expect(ToolkitRunner.isNetworkError("fatal: Could not resolve host: github.com"))
        #expect(ToolkitRunner.isNetworkError("Error: Connection refused"))
        #expect(ToolkitRunner.isNetworkError("curl: (35) SSL handshake failed"))
        #expect(ToolkitRunner.isNetworkError("operation timed out"))
        #expect(!ToolkitRunner.isNetworkError("permission denied"))
        #expect(!ToolkitRunner.isNetworkError("already installed"))
        #expect(!ToolkitRunner.isNetworkError(""))
    }

    // MARK: – Command shapes

    @Test func planCommandsForPlugin() async throws {
        let dir = tempDir("plan-plugin-cmds")
        defer { try? FileManager.default.removeItem(at: dir) }
        let home = dir.appendingPathComponent("home")
        let store = ToolkitStore(directory: dir.appendingPathComponent("data"))
        let entry = ToolkitEntry(entryId: "my-plugin", displayName: "P", source: .user,
                                  install: .plugin(source: "owner/repo", pluginID: "my-plugin@repo"))
        try await store.addEntry(entry)
        let fakeExec = FakeToolkitExecutor(responses: [])
        try await store.approve(entryId: "my-plugin", executor: fakeExec)
        let runner = ToolkitRunner(store: store, probeContext: makeContext(home: home))
        let items = await runner.plan()
        let item = items.first { $0.entry.entryId == "my-plugin" }!
        if case .run(let cmds) = item.action {
            #expect(cmds[0] == ["claude", "plugin", "marketplace", "add", "owner/repo", "--name", "repo"])
            #expect(cmds[1] == ["claude", "plugin", "install", "my-plugin@repo", "--scope", "user", "--json"])
        } else {
            Issue.record("Expected .run for plugin entry")
        }
    }

    @Test func planCommandsForMcp() async throws {
        let dir = tempDir("plan-mcp-cmds")
        defer { try? FileManager.default.removeItem(at: dir) }
        let home = dir.appendingPathComponent("home")
        let store = ToolkitStore(directory: dir.appendingPathComponent("data"))
        let entry = ToolkitEntry(entryId: "srv", displayName: "Srv", source: .user,
                                  install: .mcp(name: "my-mcp", executable: "node", args: ["server.js"]))
        try await store.addEntry(entry)
        let fakeExec = FakeToolkitExecutor(responses: [])
        try await store.approve(entryId: "srv", executor: fakeExec)
        let runner = ToolkitRunner(store: store, probeContext: makeContext(home: home))
        let items = await runner.plan()
        let item = items.first { $0.entry.entryId == "srv" }!
        if case .run(let cmds) = item.action {
            #expect(cmds[0] == ["claude", "mcp", "add", "--scope", "user", "my-mcp", "--", "node", "server.js"])
        } else {
            Issue.record("Expected .run for mcp entry")
        }
    }

    @Test func planCommandsForSkillIncludeDestination() async throws {
        let dir = tempDir("plan-skill-cmds")
        defer { try? FileManager.default.removeItem(at: dir) }
        let home = dir.appendingPathComponent("home")
        let store = ToolkitStore(directory: dir.appendingPathComponent("data"))
        let entry = ToolkitEntry(entryId: "my-skill", displayName: "S", source: .user,
                                  install: .skill(url: "https://github.com/x/my-skill.git"))
        try await store.addEntry(entry)
        let fakeExec = FakeToolkitExecutor(responses: [])
        try await store.approve(entryId: "my-skill", executor: fakeExec)
        let runner = ToolkitRunner(store: store, probeContext: makeContext(home: home))
        let items = await runner.plan()
        let item = items.first { $0.entry.entryId == "my-skill" }!
        if case .run(let cmds) = item.action {
            let expected = ["git", "clone",
                            "https://github.com/x/my-skill.git",
                            home.appendingPathComponent(".claude/skills/my-skill").path]
            #expect(cmds[0] == expected)
        } else {
            Issue.record("Expected .run for skill entry")
        }
    }

    @Test func planCommandsForRepoScriptUseResolvedSHA() async throws {
        let dir = tempDir("plan-repo-cmds")
        defer { try? FileManager.default.removeItem(at: dir) }
        let home = dir.appendingPathComponent("home")
        let appData = dir.appendingPathComponent("appdata")
        let store = ToolkitStore(directory: dir.appendingPathComponent("data"))
        let entry = ToolkitEntry(entryId: "setup", displayName: "Setup", source: .user,
                                  install: .repoScript(url: "https://github.com/x/setup.git",
                                                        ref: fakeSHA,
                                                        scriptPath: "install.sh"))
        try await store.addEntry(entry)
        let fakeExec = FakeToolkitExecutor(responses: [])
        try await store.approve(entryId: "setup", executor: fakeExec)
        let ctx = ToolkitProbeContext(home: home, environment: [:], appDataDir: appData)
        let runner = ToolkitRunner(store: store, probeContext: ctx)
        let items = await runner.plan()
        let item = items.first { $0.entry.entryId == "setup" }!
        if case .run(let cmds) = item.action {
            let cloneDir = appData.appendingPathComponent("toolkit-clones/\(fakeSHA)").path
            #expect(cmds[0] == ["git", "clone", "--no-checkout", "https://github.com/x/setup.git", cloneDir])
            #expect(cmds[1] == ["git", "-C", cloneDir, "checkout", fakeSHA])
            #expect(cmds[2] == ["\(cloneDir)/install.sh"])
        } else {
            Issue.record("Expected .run for repoScript entry")
        }
    }
}
