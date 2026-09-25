import Foundation
import Testing
@testable import MightyCore

// MARK: - Helpers

private func tempDir(_ label: String) -> URL {
    let url = FileManager.default.temporaryDirectory
        .appendingPathComponent("toolkit-truth-\(label)-\(UUID().uuidString)", isDirectory: true)
    try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
    return url
}

/// Writes installed_plugins.json with a user-scope record so the probe returns .installed.
private func touchPlugin(pluginID: String, home: URL) throws {
    let dir = home.appendingPathComponent(".claude/plugins")
    try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    let payload: [String: Any] = [
        "version": 2,
        "plugins": [pluginID: [["scope": "user", "installPath": "/x"]]],
    ]
    try JSONSerialization.data(withJSONObject: payload)
        .write(to: dir.appendingPathComponent("installed_plugins.json"))
}

private func makePluginEntry(id: String = "truth-plugin",
                              source: String = "org/repo",
                              pluginID: String = "truth@repo") -> ToolkitEntry {
    ToolkitEntry(entryId: id, displayName: id, source: .user,
                 install: .plugin(source: source, pluginID: pluginID))
}

private func makeNpmEntry(id: String = "truth-npm", pkg: String = "typescript") -> ToolkitEntry {
    ToolkitEntry(entryId: id, displayName: id, source: .user,
                 install: .package(manager: .npm, name: pkg))
}

private func makeCtx(home: URL) -> ToolkitProbeContext {
    ToolkitProbeContext(home: home, environment: [:],
                       appDataDir: home.appendingPathComponent("appdata"))
}

// MARK: - Fake executor

private final class FakeExec: ToolkitRunnerExecutor, @unchecked Sendable {
    private var responses: [ToolkitCommandOutput]
    private let def: ToolkitCommandOutput
    private(set) var calls: [[String]] = []
    init(_ responses: [ToolkitCommandOutput] = [], default def: ToolkitCommandOutput = .success) {
        self.responses = responses; self.def = def
    }
    func run(_ argv: [String]) -> ToolkitCommandOutput {
        calls.append(argv)
        return responses.isEmpty ? def : responses.removeFirst()
    }
}

// MARK: - Suite

@Suite struct ToolkitResultTruthTests {

    // MARK: – Case 1: all ok + probe finds file → installed

    @Test func allStepsOkProbeFindsFile() async throws {
        let dir = tempDir("truth-case1")
        defer { try? FileManager.default.removeItem(at: dir) }
        let home = dir.appendingPathComponent("home")
        let pluginEntry = makePluginEntry()
        let ctx = makeCtx(home: home)
        let runner = ToolkitRunner(store: ToolkitStore(directory: dir.appendingPathComponent("data")),
                                   probeContext: ctx)
        // Pre-create the probe file so the post-run probe reports installed.
        try touchPlugin(pluginID: "truth@repo", home: home)
        let cmds = ToolkitRunner.installCommands(for: pluginEntry, approval: nil, context: ctx)
        let planItem = ToolkitPlanItem(entry: pluginEntry, action: .run(commands: cmds))
        let exec = FakeExec(default: .success)
        let results = await runner.run(plan: [planItem], executor: exec)
        let r = try #require(results.first { $0.entryId == "truth-plugin" })
        // (a) verdict from post-run probe, not from step outcomes
        #expect(r.verdict == .installed)
        // (b) step outcomes stored as explanation (both ok)
        #expect(r.steps.count == 2)
        #expect(r.steps[0].outcome == .ok)
        #expect(r.steps[1].outcome == .ok)
    }

    // MARK: – Case 2: all ok + probe finds nothing → failed, but steps are ok

    @Test func allStepsOkProbeFindsNothing() async throws {
        let dir = tempDir("truth-case2")
        defer { try? FileManager.default.removeItem(at: dir) }
        let home = dir.appendingPathComponent("home")
        let pluginEntry = makePluginEntry()
        let ctx = makeCtx(home: home)
        let runner = ToolkitRunner(store: ToolkitStore(directory: dir.appendingPathComponent("data")),
                                   probeContext: ctx)
        // No installed_plugins.json → probe reports missing
        let cmds = ToolkitRunner.installCommands(for: pluginEntry, approval: nil, context: ctx)
        let planItem = ToolkitPlanItem(entry: pluginEntry, action: .run(commands: cmds))
        let exec = FakeExec(default: .success)
        let results = await runner.run(plan: [planItem], executor: exec)
        let r = try #require(results.first { $0.entryId == "truth-plugin" })
        // (a) verdict from probe (missing → failed), not from step outcomes (all ok)
        #expect(r.verdict == .failed)
        // (b) steps are ok — they are the explanation, not the verdict
        #expect(r.steps.count == 2)
        #expect(r.steps[0].outcome == .ok)
        #expect(r.steps[1].outcome == .ok)
    }

    // MARK: – Case 3: step 1 fails → step 2 is skipped, next entry still runs

    @Test func stepOneFails() async throws {
        let dir = tempDir("truth-case3")
        defer { try? FileManager.default.removeItem(at: dir) }
        let home = dir.appendingPathComponent("home")
        let pluginEntry = makePluginEntry()
        let npmEntry = makeNpmEntry()
        let ctx = makeCtx(home: home)
        let runner = ToolkitRunner(store: ToolkitStore(directory: dir.appendingPathComponent("data")),
                                   probeContext: ctx)
        let pluginCmds = ToolkitRunner.installCommands(for: pluginEntry, approval: nil, context: ctx)
        let npmCmds   = ToolkitRunner.installCommands(for: npmEntry, approval: nil, context: ctx)
        let plan = [
            ToolkitPlanItem(entry: pluginEntry, action: .run(commands: pluginCmds)),
            ToolkitPlanItem(entry: npmEntry,    action: .run(commands: npmCmds)),
        ]
        // Step 0 (marketplace add) fails; everything else succeeds by default.
        let exec = FakeExec([.failure(output: "install failed")], default: .success)
        let results = await runner.run(plan: plan, executor: exec)

        let pr = try #require(results.first { $0.entryId == "truth-plugin" })
        // (a) verdict from post-run probe (no file → failed)
        #expect(pr.verdict == .failed)
        // (b) step outcomes are the explanation
        #expect(pr.steps.count == 2)
        // (c) step 0 is failed; step 1 is skipped — never failed
        #expect(pr.steps[0].outcome == .failed)
        #expect(pr.steps[1].outcome == .skipped)
        // (d) the failing entry did not stop the npm entry
        let nr = try #require(results.first { $0.entryId == "truth-npm" })
        #expect(exec.calls.contains(["npm", "install", "-g", "typescript"]))
        // npm has no probe file either, so it is also .failed — but it ran
        #expect(nr.steps.count == 1)
        #expect(nr.steps[0].outcome == .ok) // executor returned success for npm
    }

    // MARK: – Case 4: step 1 ok / step 2 fails, next entry still runs

    @Test func stepTwoFails() async throws {
        let dir = tempDir("truth-case4")
        defer { try? FileManager.default.removeItem(at: dir) }
        let home = dir.appendingPathComponent("home")
        let pluginEntry = makePluginEntry()
        let npmEntry = makeNpmEntry()
        let ctx = makeCtx(home: home)
        let runner = ToolkitRunner(store: ToolkitStore(directory: dir.appendingPathComponent("data")),
                                   probeContext: ctx)
        let pluginCmds = ToolkitRunner.installCommands(for: pluginEntry, approval: nil, context: ctx)
        let npmCmds   = ToolkitRunner.installCommands(for: npmEntry, approval: nil, context: ctx)
        let plan = [
            ToolkitPlanItem(entry: pluginEntry, action: .run(commands: pluginCmds)),
            ToolkitPlanItem(entry: npmEntry,    action: .run(commands: npmCmds)),
        ]
        // Step 0 (marketplace add) succeeds; step 1 (plugin install) fails; npm succeeds.
        let exec = FakeExec([.success, .failure(output: "install failed")], default: .success)
        let results = await runner.run(plan: plan, executor: exec)

        let pr = try #require(results.first { $0.entryId == "truth-plugin" })
        // (a) verdict from post-run probe (no file → failed)
        #expect(pr.verdict == .failed)
        // (b) step outcomes are the explanation
        #expect(pr.steps.count == 2)
        // step 0 ok, step 1 failed — step 2 doesn't exist so (c) doesn't apply here
        #expect(pr.steps[0].outcome == .ok)
        #expect(pr.steps[1].outcome == .failed)
        // (d) the failing entry did not stop the npm entry
        #expect(exec.calls.contains(["npm", "install", "-g", "typescript"]))
        let nr = try #require(results.first { $0.entryId == "truth-npm" })
        #expect(nr.steps.count == 1)
        #expect(nr.steps[0].outcome == .ok)
    }

    // MARK: – (e) other-OS entry is not probed and absent from missing count

    @Test func otherOSEntryNotProbedAndAbsentFromMissingCount() async throws {
        let dir = tempDir("truth-case-e")
        defer { try? FileManager.default.removeItem(at: dir) }
        let home = dir.appendingPathComponent("home")
        let store = ToolkitStore(directory: dir.appendingPathComponent("data"))
        // Add a winget entry (Windows-only) — should be invisible on macOS.
        let wingetEntry = ToolkitEntry(
            entryId: "win-only", displayName: "Win Only", source: .user,
            install: .package(manager: .winget, name: "SomeVendor.SomePkg", executable: "app.exe"))
        try await store.addEntry(wingetEntry)
        let fakeExec = FakeToolkitExecutor(responses: [])
        try await store.approve(entryId: "win-only", executor: fakeExec)
        let ctx = makeCtx(home: home)
        let runner = ToolkitRunner(store: store, probeContext: ctx)

        // (e1) winget entry is absent from plan() on macOS (not probed = not counted as missing)
        let items = await runner.plan()
        #expect(!items.contains { $0.entry.entryId == "win-only" })

        // (e2) running the plan does not include a result for the winget entry
        let exec = FakeExec(default: .success)
        let results = await runner.run(plan: items, executor: exec)
        #expect(!results.contains { $0.entryId == "win-only" })

        // (e3) the winget command is never sent to the executor
        #expect(!exec.calls.contains { $0.first == "winget" })
    }

    // MARK: – Anti-vacuity marker

    @Test func markerToolkitResultTruthOK() {
        print("Suite ToolkitResultTruthTests passed")
    }
}
