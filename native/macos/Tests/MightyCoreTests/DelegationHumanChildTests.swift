import Foundation
import Testing
@testable import MightyCore

/// The app side as a human's new worktree sees it: the panes a test sets up,
/// every child pane it is asked for, and the runs the host itself would
/// start (none: the human's send starts the child's first run).
private final class HumanChildHost: DelegationHost, @unchecked Sendable {
    private let lock = NSLock()
    private var panes: [String: DelegationPaneState] = [:]
    private var made: [DelegationChildPane] = []
    private var hostRuns: [String] = []
    private var paneFails = false
    private var shown: [(sessionId: String, text: String)] = []

    func set(_ pane: DelegationPaneState) { lock.withLock { panes[pane.sessionId] = pane } }
    func failPanes() { lock.withLock { paneFails = true } }
    var createdPanes: [DelegationChildPane] { lock.withLock { made } }
    var hostStartedRuns: [String] { lock.withLock { hostRuns } }
    var notes: [(sessionId: String, text: String)] { lock.withLock { shown } }

    func createPane(_ pane: DelegationChildPane) async -> Bool {
        lock.withLock {
            made.append(pane)
            guard !paneFails else { return false }
            panes[pane.sessionId] = DelegationPaneState(sessionId: pane.sessionId, provider: pane.provider, permissionMode: pane.mode, folder: pane.folder, parentSessionId: pane.parentSessionId)
            return true
        }
    }

    func startRun(sessionId: String, input: String) async -> String? { lock.withLock { hostRuns.append(sessionId); return "host-run-of-\(sessionId)" } }
    func deliver(_ input: String, to sessionId: String, route: DeliveryRoute) async -> String? { "run-1" }
    func paneState(sessionId: String) async -> DelegationPaneState? { lock.withLock { panes[sessionId] } }
    func stopRun(sessionId: String) async {}
    func note(_ text: String, in sessionId: String) async { lock.withLock { shown.append((sessionId, text)) } }
}

/// The runs the human's send started in child panes, with their first run,
/// and the children whose start failed after the send was answered.
private final class SendStarts: @unchecked Sendable {
    private let lock = NSLock()
    private var runs: [(childId: String, run: HumanChildFirstRun)] = []
    private var failures: [String] = []
    private var runFails = false
    var started: [(childId: String, input: String)] { lock.withLock { runs.map { ($0.childId, $0.run.input) } } }
    var firstRuns: [HumanChildFirstRun] { lock.withLock { runs.map(\.run) } }
    var failed: [String] { lock.withLock { failures } }
    func failRuns() { lock.withLock { runFails = true } }

    func start(_ childId: String, _ run: HumanChildFirstRun) -> String? {
        lock.withLock { runs.append((childId, run)); return runFails ? nil : "send-run-of-\(childId)" }
    }
    func fail(_ childId: String) { lock.withLock { failures.append(childId) } }
}

/// A temp repository with a parent Claude pane "parent" running in it, the
/// worktree root and the profile with the delegation file.
private struct Fixture {
    let base: URL
    let host: HumanChildHost
    let store: DelegationFileStore
    let coordinator: DelegationCoordinator
    let sends = SendStarts()
    var repo: URL { base.appendingPathComponent("repo", isDirectory: true) }
    var root: URL { base.appendingPathComponent("worktrees", isDirectory: true) }

    /// `commits` nil leaves the repository folder without git.
    static func make(commits: Int? = 1, file: DelegationFile = DelegationFile(), parentMode: String = "acceptEdits", provider: String = "claude",
                     freeBytes: Int64 = 50_000_000_000, switchOn: Bool = true) async throws -> Fixture {
        let base = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-human-child-\(UUID().uuidString)", isDirectory: true)
        let store = DelegationFileStore(directory: base.appendingPathComponent("profile", isDirectory: true))
        try store.save(file)
        let host = HumanChildHost()
        let maker = ChildWorktreeMaker(root: base.appendingPathComponent("worktrees", isDirectory: true), freeBytes: { _ in freeBytes })
        let fixture = Fixture(base: base, host: host, store: store, coordinator: try DelegationCoordinator(store: store, host: host, worktrees: maker, isSwitchOn: { switchOn }))
        try FileManager.default.createDirectory(at: fixture.repo, withIntermediateDirectories: true)
        if let commits {
            try await fixture.git(["init", "-q", "-b", "main"])
            for index in 0 ..< commits {
                try Data("\(index)\n".utf8).write(to: fixture.repo.appendingPathComponent("file-\(index).txt"))
                try await fixture.git(["add", "-A"]); try await fixture.git(["commit", "-q", "-m", "commit \(index)"])
            }
        }
        host.set(DelegationPaneState(sessionId: "parent", provider: provider, permissionMode: parentMode, folder: fixture.repo.path, runId: "run-1", activity: .running))
        return fixture
    }

    func remove() { try? FileManager.default.removeItem(at: base) }

    /// The human's second send of `text` in the pane `parent`.
    func send(_ text: String, from parent: String = "parent", planFirst: Bool = false) async -> HumanChildStart {
        let sends = sends
        return await coordinator.startHumanChild(text, parentId: parent, planFirst: planFirst,
                                                 starter: HumanChildStarter(start: { sends.start($0, $1) }, failed: { sends.fail($0) }))
    }

    /// A delegation tool call from `caller`, as its pane token resolves.
    func call(_ tool: String, _ arguments: [String: String] = [:], from caller: String = "parent") async -> DelegationResponse {
        let location = PaneMCPServerLocation(socketPath: base.appendingPathComponent("io.sock").path, executable: URL(fileURLWithPath: "/usr/bin/true"))
        let binding = PaneMCPBinding(agentPaneId: caller, token: "token-\(caller)", server: location, workspaceId: "ws-1", workspacePath: repo.path, provider: "claude", delegation: true)
        return await coordinator.handle(DelegationRequest(tool: tool, arguments: arguments), binding: binding)
    }

    /// The child is in `state` with its start over.
    func waitFor(_ id: String, _ state: ChildState) async -> Bool {
        await waitUntil {
            let reached = await coordinator.file.children.first(where: { $0.id == id })?.state == state
            let starting = await coordinator.starting.contains(id)
            return reached && !starting
        }
    }

    /// A refused send made nothing: the same file, no worktree root, no
    /// `mighty/*` branch, and no pane or run asked for.
    func expectNothingMade(since before: Data, git hasGit: Bool = true) async throws {
        #expect(try Data(contentsOf: store.fileURL) == before)
        #expect(!FileManager.default.fileExists(atPath: root.path))
        if hasGit { #expect(try await git(["for-each-ref", "--format=%(refname)", "refs/heads/mighty"]).isEmpty) }
        #expect(host.createdPanes.isEmpty)
        #expect(sends.started.isEmpty && host.hostStartedRuns.isEmpty)
    }

    /// Fixture git's exit code, for a command that may fail.
    func gitExitCode(_ arguments: [String], in directory: String) async throws -> Int32 {
        let executable = try #require(DelegationGit.executable)
        var environment = ProcessInfo.processInfo.environment.filter { !$0.key.hasPrefix("GIT_") }
        environment["GIT_CONFIG_GLOBAL"] = "/dev/null"; environment["GIT_CONFIG_NOSYSTEM"] = "1"
        for key in ["GIT_AUTHOR", "GIT_COMMITTER"] { environment[key + "_NAME"] = "Fixture"; environment[key + "_EMAIL"] = "fixture@example.invalid" }
        return try await ProcessCapture.run(executable: executable, arguments: ["-C", directory] + arguments, environment: environment, cwd: URL(fileURLWithPath: directory), timeout: 120).exitCode
    }

    /// The repository's own identity, so the app's git (the user's settings)
    /// can commit in a child's worktree on any machine.
    func setIdentity() async throws {
        try await git(["config", "user.name", "Fixture"]); try await git(["config", "user.email", "fixture@example.invalid"])
    }

    /// Fixture git, kept away from the user's and the system's settings.
    @discardableResult
    func git(_ arguments: [String], in directory: String? = nil) async throws -> String {
        let executable = try #require(DelegationGit.executable)
        var environment = ProcessInfo.processInfo.environment.filter { !$0.key.hasPrefix("GIT_") }
        environment["GIT_CONFIG_GLOBAL"] = "/dev/null"; environment["GIT_CONFIG_NOSYSTEM"] = "1"; environment["GIT_OPTIONAL_LOCKS"] = "0"
        for key in ["GIT_AUTHOR", "GIT_COMMITTER"] { environment[key + "_NAME"] = "Fixture"; environment[key + "_EMAIL"] = "fixture@example.invalid" }
        let folder = directory ?? repo.path
        let result = try await ProcessCapture.run(executable: executable, arguments: ["-C", folder] + arguments, environment: environment, cwd: URL(fileURLWithPath: folder), timeout: 120)
        #expect(result.exitCode == 0, "git \(arguments.joined(separator: " ")): \(String(decoding: result.stderr, as: UTF8.self))")
        return DelegationGit.line(result.stdout)
    }
}

/// Polls a condition on real time; the bound only ends a test that broke.
private func waitUntil(timeout: TimeInterval = 60, _ condition: () async -> Bool) async -> Bool {
    let deadline = Date().addingTimeInterval(timeout)
    while Date() < deadline {
        if await condition() { return true }
        try? await Task.sleep(nanoseconds: 10_000_000)
    }
    return await condition()
}

private func openChild(_ id: String, byHuman: Bool = false) -> ChildRecord {
    ChildRecord(id: id, parentSessionId: "parent", worktreePath: "/nonexistent/worktrees/\(id)", parentBranch: "main", baseCommit: String(repeating: "a", count: 40),
                startingMode: "plan", requestKey: "key-\(id)", state: .running, startedByHuman: byHuman ? true : nil)
}

/// The composer's second send on a busy pane, and the default-on switch.
@Suite(.delegationLane) struct ComposerSecondSendTests {
    @Test func aClaudePaneThatMayHaveChildrenInAUsableCheckoutOpensANewWorktree() {
        #expect(ComposerSecondSend.choose(kind: SessionKind.claude, provider: "claude", mode: "acceptEdits", isChild: false, delegationOn: true, canHostWorktree: true, steerable: true) == .worktree)
        // Whether its running turn could take the text does not matter: a new worktree takes files too.
        #expect(ComposerSecondSend.choose(kind: SessionKind.claude, provider: "claude", mode: "acceptEdits", isChild: false, delegationOn: true, canHostWorktree: true, steerable: false) == .worktree)
    }

    @Test func aChildASwitchedOffPaneOrAFolderWithNoUsableCheckoutSteersAsBefore() {
        #expect(ComposerSecondSend.choose(kind: SessionKind.claude, provider: "claude", mode: "acceptEdits", isChild: true, delegationOn: true, canHostWorktree: true, steerable: true) == .steer)
        #expect(ComposerSecondSend.choose(kind: SessionKind.claude, provider: "claude", mode: "acceptEdits", isChild: false, delegationOn: false, canHostWorktree: true, steerable: true) == .steer)
        #expect(ComposerSecondSend.choose(kind: SessionKind.claude, provider: "claude", mode: "acceptEdits", isChild: false, delegationOn: true, canHostWorktree: false, steerable: true) == .steer)
        // A Claude run still starting cannot take it yet: it queues.
        #expect(ComposerSecondSend.choose(kind: SessionKind.claude, provider: "claude", mode: "acceptEdits", isChild: false, delegationOn: true, canHostWorktree: false, steerable: false) == .queue)
    }

    @Test func codexAndAntigravityPanesOpenANewWorktreeTooAndOtherwiseQueueAsBefore() {
        for provider in ["codex", "gemini"] {
            #expect(ComposerSecondSend.choose(kind: SessionKind.claude, provider: provider, mode: "acceptEdits", isChild: false, delegationOn: true, canHostWorktree: true, steerable: false) == .worktree, "\(provider)")
            // A child, the switch off or no usable checkout: their runs take no text, so it queues.
            #expect(ComposerSecondSend.choose(kind: SessionKind.claude, provider: provider, mode: "acceptEdits", isChild: true, delegationOn: true, canHostWorktree: true, steerable: false) == .queue, "\(provider)")
            #expect(ComposerSecondSend.choose(kind: SessionKind.claude, provider: provider, mode: "acceptEdits", isChild: false, delegationOn: false, canHostWorktree: true, steerable: false) == .queue, "\(provider)")
            #expect(ComposerSecondSend.choose(kind: SessionKind.claude, provider: provider, mode: "acceptEdits", isChild: false, delegationOn: true, canHostWorktree: false, steerable: false) == .queue, "\(provider)")
        }
        #expect(ComposerSecondSend.choose(kind: SessionKind.shell, provider: "claude", mode: "acceptEdits", isChild: false, delegationOn: true, canHostWorktree: true, steerable: false) == .queue)
    }

    @Test func aCodexOrAntigravityPaneInAModeItsChildCouldNotWriteInQueuesAsBefore() {
        for (provider, mode, worktree) in [("codex", "manual", false), ("codex", "acceptEdits", true), ("codex", "onRequest", true), ("codex", "fullAccess", true),
                                           ("gemini", "manual", false), ("gemini", "plan", false), ("gemini", "acceptEdits", true), ("gemini", "fullAccess", true)] {
            let chosen = ComposerSecondSend.choose(kind: SessionKind.claude, provider: provider, mode: mode, isChild: false, delegationOn: true, canHostWorktree: true, steerable: false)
            #expect(chosen == (worktree ? .worktree : .queue), "\(provider) \(mode)")
        }
        // Claude's child asks the human in any mode, so every Claude mode opens one.
        for mode in DelegationProviders.startingModes(provider: "claude") {
            #expect(ComposerSecondSend.choose(kind: SessionKind.claude, provider: "claude", mode: mode, isChild: false, delegationOn: true, canHostWorktree: true, steerable: true) == .worktree, "\(mode)")
        }
    }

    @Test func aTextNoNewChildRunsSteersWhenItCanAndIsOtherwiseQueuedNeverDropped() {
        #expect(ComposerSecondSend.fallback(steerable: true) == .steer)
        #expect(ComposerSecondSend.fallback(steerable: false) == .queue)
    }

    @Test func aCheckoutCanHostAWorktreeWhenOnlyTheDiskIsShort() {
        let base = ChildWorktreeBase(repository: "/r", prefix: "", parentBranch: "main", baseCommit: String(repeating: "a", count: 40))
        #expect(ChildWorktreeCheck.ready(base).canHostWorktree)
        #expect(ChildWorktreeCheck.refused(.lowDisk).canHostWorktree)
        for reason in [DelegationReasonCode.notGit, .detachedHead, .unbornBranch] { #expect(!ChildWorktreeCheck.refused(reason).canHostWorktree, "\(reason)") }
    }

    @Test func aStoredKeyReadsAsDefaultsReadsABool() throws {
        let suite = "dev.mightyclaude.tests.delegation.\(UUID().uuidString)"
        let defaults = try #require(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        #expect(DelegationSwitch.isOn(defaults))
        // `defaults write … -string NO` or `-int 0` turn it off too.
        defaults.set("NO", forKey: DelegationSwitch.defaultsKey)
        #expect(!DelegationSwitch.isOn(defaults))
        defaults.set(0, forKey: DelegationSwitch.defaultsKey)
        #expect(!DelegationSwitch.isOn(defaults))
        defaults.set("YES", forKey: DelegationSwitch.defaultsKey)
        #expect(DelegationSwitch.isOn(defaults))
    }

    @Test func aRecordKeptBeforeTheMarkIsAnAgentsChildAndTheMarkIsStored() throws {
        let old = Data(#"{"id":"c1","parentSessionId":"p","worktreePath":"/w/c1","branch":"mighty/c1","parentBranch":"main","baseCommit":"abc","startingMode":"plan","requestKey":"k","followUpCount":0,"reportRevision":0,"state":"running"}"#.utf8)
        let decoded = try JSONDecoder().decode(ChildRecord.self, from: old)
        #expect(decoded.startedByHuman == nil && decoded.notifiesParent)
        #expect(!String(decoding: try JSONEncoder().encode(decoded), as: UTF8.self).contains("startedByHuman"))
        var human = decoded
        human.startedByHuman = true
        #expect(!human.notifiesParent)
        #expect(try JSONDecoder().decode(ChildRecord.self, from: JSONEncoder().encode(human)).startedByHuman == true)
    }

    @Test func aHumansChildPaneTakesItsParentsModelAndEffort() {
        var parent = RunSession(id: "parent", workspaceId: "ws", title: "Parent", kind: SessionKind.claude, provider: "claude", settings: RunSettings(effort: "high", permissionMode: "auto"))
        parent.model = "opus"
        let human = DelegationPanes.childSession(DelegationChildPane(sessionId: "c1", parentSessionId: "parent", mode: "auto", folder: "/w/c1", startedByHuman: true), workspaceId: "ws", title: "Fix it", parent: parent)
        #expect(human.model == "opus" && human.settings.effort == "high" && human.settings.permissionMode == "auto")
        #expect(human.parentSessionId == "parent" && human.workingFolder == "/w/c1")
        // A delegate call's child keeps Claude's default, as before.
        let agent = DelegationPanes.childSession(DelegationChildPane(sessionId: "c2", parentSessionId: "parent", mode: "plan", folder: "/w/c2"), workspaceId: "ws", title: "Fix it", parent: parent)
        #expect(agent.model == RunSession(workspaceId: "ws", title: "x").model && agent.settings.effort == "default")
    }

    @Test func everyChildShowsItsParentsViewAndAHumansChildTakesItsStyle() {
        var parent = RunSession(id: "parent", workspaceId: "ws", title: "Parent", kind: SessionKind.claude, provider: "claude", settings: RunSettings(permissionMode: "auto"))
        parent.agentViewMode = "mighty"
        parent.graphViewMode = .structure
        parent.mightyStyle = "superpowers"
        parent.mightyStyleHash = "h1"
        parent.mightyStyleSince = "2026-10-15T00:00:00Z"
        let human = DelegationPanes.childSession(DelegationChildPane(sessionId: "c1", parentSessionId: "parent", mode: "auto", folder: "/w/c1", startedByHuman: true), workspaceId: "ws", title: "Fix it", parent: parent)
        #expect(human.agentViewMode == "mighty" && human.mightyViewMode == .structure)
        #expect(human.mightyStyle == "superpowers" && human.mightyStyleHash == "h1" && human.mightyStyleSince == nil)
        // A delegate call's child shows the same view, without the style.
        let agent = DelegationPanes.childSession(DelegationChildPane(sessionId: "c2", parentSessionId: "parent", mode: "plan", folder: "/w/c2"), workspaceId: "ws", title: "Fix it", parent: parent)
        #expect(agent.agentViewMode == "mighty" && agent.mightyViewMode == .structure)
        #expect(agent.mightyStyle == nil && agent.mightyStyleHash == nil)
        // A parent in the basic view, or no parent, leaves the defaults.
        parent.agentViewMode = "default"
        parent.graphViewMode = nil
        let basic = DelegationPanes.childSession(DelegationChildPane(sessionId: "c3", parentSessionId: "parent", mode: "auto", folder: "/w/c3", startedByHuman: true), workspaceId: "ws", title: "t", parent: parent)
        #expect(basic.agentViewMode == "default" && basic.mightyViewMode == .diagram)
        let orphan = DelegationPanes.childSession(DelegationChildPane(sessionId: "c4", parentSessionId: "parent", mode: "auto", folder: "/w/c4"), workspaceId: "ws", title: "t")
        #expect(orphan.agentViewMode == nil && orphan.graphViewMode == nil)
    }
}

/// Real temp repositories and a fake host: the human's new worktree from the
/// parent's composer to the child's first run, and the parent left alone.
@Suite(.enabled(if: DelegationGit.executable != nil, "Requires a local Git executable"), .delegationLane)
struct DelegationHumanChildTests {
    @Test func theSendMakesAChildInTheParentsModeAndStartsTheTextThere() async throws {
        let fixture = try await Fixture.make()
        defer { fixture.remove() }
        let head = try await fixture.git(["rev-parse", "HEAD"])
        let text = "Try the other parser in a branch.\n"

        guard case .started(let child) = await fixture.send(text) else { Issue.record("The send made no child."); return }
        #expect(child.startedByHuman == true && !child.notifiesParent)
        #expect(child.state == .creating && child.parentSessionId == "parent")
        #expect(child.startingMode == "acceptEdits")
        #expect(child.parentBranch == "main" && child.baseCommit == head)
        #expect(await fixture.waitFor(child.id, .running))

        let saved = try fixture.store.load()
        #expect(saved.children.map(\.id) == [child.id] && saved.children.first?.startedByHuman == true)
        #expect(saved.copy(childId: child.id, kind: .task)?.text == text)
        #expect(saved.children.first?.runId == "send-run-of-\(child.id)" && saved.children.first?.awaitedRunId == "send-run-of-\(child.id)")
        #expect(fixture.host.createdPanes == [DelegationChildPane(sessionId: child.id, parentSessionId: "parent", mode: "acceptEdits", folder: child.worktreePath, startedByHuman: true)])
        // The send's own start runs it, the human's text first; the host starts nothing.
        #expect(fixture.host.hostStartedRuns.isEmpty)
        let run = try #require(fixture.sends.started.first)
        #expect(fixture.sends.started.count == 1 && run.childId == child.id)
        #expect(run.input.hasPrefix(text))
        #expect(fixture.sends.firstRuns.first?.followingNote == nil && run.input.contains("[Mighty Claude]"))
        #expect(run.input.contains(child.branch) && run.input.contains(ChildWorktree.reportFile(worktreePath: child.worktreePath)))
        #expect(try await fixture.git(["rev-parse", "refs/heads/" + child.branch]) == head)
        // The parent's checkout is untouched.
        #expect(try await fixture.git(["symbolic-ref", "--short", "HEAD"]) == "main")
        #expect(try await fixture.git(["status", "--porcelain", "--untracked-files=all"]).isEmpty)
        #expect(saved.notices.isEmpty)
    }

    @Test func aHumansChildNeverNotifiesItsParentButItsReportStillReachesItsCard() async throws {
        let fixture = try await Fixture.make()
        defer { fixture.remove() }
        guard case .started(let child) = await fixture.send("Write a note.") else { Issue.record("The send made no child."); return }
        #expect(await fixture.waitFor(child.id, .running))
        let runId = "send-run-of-\(child.id)"

        // Its awaited first run ends without a report: ended, and no notice.
        await fixture.coordinator.childRunEnded(child.id, runId: runId, end: .finished)
        #expect(await fixture.coordinator.file.children.first?.state == .ended)
        #expect(await fixture.coordinator.file.notices.isEmpty)

        // A later run ends with a committed report: reported, merge on its card, and still no notice.
        await fixture.coordinator.childRunStarted(child.id, runId: "run-2")
        try Data("Note\n".utf8).write(to: URL(fileURLWithPath: child.worktreePath).appendingPathComponent("note.txt"))
        try await fixture.git(["add", "note.txt"], in: child.worktreePath)
        try await fixture.git(["commit", "-q", "-m", "Note"], in: child.worktreePath)
        try Data("# Report\n\nWrote the note.\n".utf8).write(to: URL(fileURLWithPath: ChildWorktree.reportFile(worktreePath: child.worktreePath)))
        #expect(await fixture.coordinator.childRunEnded(child.id, runId: "run-2", end: .finished) == nil)
        let file = await fixture.coordinator.file
        #expect(file.children.first?.state == .reported && file.children.first?.reportRevision == 1)
        #expect(file.notices.isEmpty && file.heldItemsByPane().isEmpty)
        #expect(DelegationSidebar.rows(file).first?.cardActions == [.merge, .discard])
        #expect(try fixture.store.load().notices.isEmpty)
    }

    @Test func aFailedStartIsShownOnItsCardWithNoNoticeAndTheAppHearsOfIt() async throws {
        let fixture = try await Fixture.make()
        defer { fixture.remove() }
        fixture.host.failPanes()
        guard case .started(let child) = await fixture.send("Write a note.") else { Issue.record("The send made no child."); return }
        #expect(await fixture.waitFor(child.id, .failed))
        #expect(fixture.sends.started.isEmpty)
        #expect(fixture.sends.failed == [child.id])
        #expect(await fixture.coordinator.file.notices.isEmpty)
        #expect(DelegationSidebar.rows(await fixture.coordinator.file).first?.cardActions == [.discard])

        // So does a first run that does not start.
        let again = try await Fixture.make()
        defer { again.remove() }
        again.sends.failRuns()
        guard case .started(let second) = await again.send("Write a note.") else { Issue.record("The send made no child."); return }
        #expect(await again.waitFor(second.id, .failed))
        #expect(again.sends.started.map(\.childId) == [second.id] && again.sends.failed == [second.id])
        #expect(await again.coordinator.file.notices.isEmpty)
    }

    @Test func aSlashCommandRunsAloneAndTheNoteFollowsIt() async throws {
        let fixture = try await Fixture.make()
        defer { fixture.remove() }
        guard case .started(let child) = await fixture.send("/review the parser") else { Issue.record("The send made no child."); return }
        #expect(await fixture.waitFor(child.id, .running))
        let run = try #require(fixture.sends.firstRuns.first)
        #expect(run.input == "/review the parser")
        let note = try #require(run.followingNote)
        #expect(note.hasPrefix("[Mighty Claude]") && note.contains(child.branch) && note.contains(ChildWorktree.reportFile(worktreePath: child.worktreePath)))
    }

    @Test func aPlanFirstStyleStartsTheChildInPlanMode() async throws {
        let fixture = try await Fixture.make(parentMode: "fullAccess")
        defer { fixture.remove() }
        guard case .started(let child) = await fixture.send("Plan the parser.", planFirst: true) else { Issue.record("The send made no child."); return }
        #expect(child.startingMode == "plan")
        #expect(await fixture.waitFor(child.id, .running))
        #expect(fixture.host.createdPanes.first?.mode == "plan")
    }

    @Test func theParentsToolsNeitherSeeNorActOnAChildTheHumanStarted() async throws {
        let fixture = try await Fixture.make(file: DelegationFile(children: [openChild("agent"), openChild("human", byHuman: true)]))
        defer { fixture.remove() }
        let before = try Data(contentsOf: fixture.store.fileURL)
        #expect(await fixture.call("list_children").children == [DelegationChildSummary(openChild("agent"))])
        #expect(await fixture.call("child_status", ["child": "human"]) == .failure(DelegationCoordinator.notYourChildMessage("human")))
        #expect(await fixture.call("child_status", ["child": "agent"]).status?.id == "agent")
        #expect(await fixture.call("merge", ["child": "human", "expected_head": String(repeating: "b", count: 40)]) == .refusal(.humanStarted))
        #expect(await fixture.call("follow_up", ["child": "human", "text": "Also do this."]) == .refusal(.humanStarted))
        #expect(try Data(contentsOf: fixture.store.fileURL) == before)
    }

    @Test func humanAndAgentChildrenEachHaveTheirOwnCapOfThree() async throws {
        // Three open children the parent's delegate made leave room for the human's.
        let agents = try await Fixture.make(file: DelegationFile(children: ["a", "b", "c"].map { openChild($0) }))
        defer { agents.remove() }
        guard case .started(let child) = await agents.send("One of mine.") else { Issue.record("The human's send was refused."); return }
        #expect(await agents.waitFor(child.id, .running))
        #expect(await agents.call("delegate", ["task": "One more.", "mode": "plan"]) == .refusal(.widthCap))

        // Three the human started leave room for the parent's delegate, not for a fourth of the human's.
        let humans = try await Fixture.make(file: DelegationFile(children: ["a", "b", "c"].map { openChild($0, byHuman: true) }))
        defer { humans.remove() }
        #expect(await humans.send("One more.") == .refused(.widthCap))
        let delegated = try #require(await humans.call("delegate", ["task": "Split this.", "mode": "plan"]).child)
        #expect(await humans.waitFor(delegated.id, .running))
    }

    @Test func aFolderThatCannotHostAWorktreeIsRefusedAndNothingIsMade() async throws {
        let plain = try await Fixture.make(commits: nil)
        defer { plain.remove() }
        let before = try Data(contentsOf: plain.store.fileURL)
        #expect(await plain.send("Try it.") == .refused(.notGit))
        try await plain.expectNothingMade(since: before, git: false)

        let unborn = try await Fixture.make(commits: 0)
        defer { unborn.remove() }
        #expect(await unborn.send("Try it.") == .refused(.unbornBranch))

        let full = try await Fixture.make(freeBytes: 1_000_000_000)
        defer { full.remove() }
        let fullBefore = try Data(contentsOf: full.store.fileURL)
        #expect(await full.send("Try it.") == .refused(.lowDisk))
        try await full.expectNothingMade(since: fullBefore)
    }

    @Test func theLimitsAreDelegatesAndEachRefusalMakesNothing() async throws {
        // Three open children the human started already: width_cap, as for delegate.
        let capped = try await Fixture.make(file: DelegationFile(children: ["a", "b", "c"].map { openChild($0, byHuman: true) }))
        defer { capped.remove() }
        let before = try Data(contentsOf: capped.store.fileURL)
        #expect(await capped.send("One more.") == .refused(.widthCap))
        try await capped.expectNothingMade(since: before)

        let fixture = try await Fixture.make()
        defer { fixture.remove() }
        let empty = try Data(contentsOf: fixture.store.fileURL)
        // A child pane cannot have children, a pane that is no agent pane has none, a closed pane has no composer.
        fixture.host.set(DelegationPaneState(sessionId: "child", permissionMode: "auto", folder: fixture.repo.path, parentSessionId: "parent", runId: "run-c", activity: .running))
        fixture.host.set(DelegationPaneState(sessionId: "shell", kind: SessionKind.shell, permissionMode: "acceptEdits", folder: fixture.repo.path, runId: "run-x", activity: .running))
        #expect(await fixture.send("Split further.", from: "child") == .refused(.childCannotDelegate))
        #expect(await fixture.send("Try it.", from: "shell") == .refused(.claudeOnly))
        #expect(await fixture.send("Try it.", from: "gone") == .refused(.parentClosed))
        // No text to be a task: a failure, as is a switched-off delegation.
        #expect(await fixture.send("  \n") == .failed(L("delegation.human.error.invalidTask")))
        #expect(await fixture.send(String(repeating: "x", count: DelegationFileStore.maximumCopyBytes + 1)) == .failed(L("delegation.human.error.invalidTask")))
        try await fixture.expectNothingMade(since: empty)

        let off = try await Fixture.make(switchOn: false)
        defer { off.remove() }
        let offBefore = try Data(contentsOf: off.store.fileURL)
        #expect(await off.send("Try it.") == .failed(L("delegation.human.error.switchedOff")))
        try await off.expectNothingMade(since: offBefore)
    }

    @Test func aCodexOrAntigravityParentsChildRunsItsAgentAndLeavesItsCommitToTheApp() async throws {
        for provider in ["codex", "gemini"] {
            let fixture = try await Fixture.make(provider: provider)
            defer { fixture.remove() }
            guard case .started(let child) = await fixture.send("Try the other parser.") else { Issue.record("The send made no child."); return }
            #expect(child.provider == provider && child.agent == provider, "\(provider)")
            #expect(await fixture.waitFor(child.id, .running))
            #expect(fixture.host.createdPanes.map(\.provider) == [provider])
            #expect(try fixture.store.load().children.first?.provider == provider)
            // The app's note names no Claude pane and leaves the commit to the app.
            let run = try #require(fixture.sends.started.first)
            #expect(run.input.hasPrefix("Try the other parser.") && !run.input.contains("Claude pane") && !run.input.contains("commit your work"), "\(provider)")
            #expect(run.input.contains("Do not commit") && run.input.contains(ChildWorktree.reportFile(worktreePath: child.worktreePath)), "\(provider)")
        }
        // Codex has no plan mode: a plan-first style leaves its child in the parent's mode.
        let codex = try await Fixture.make(parentMode: "onRequest", provider: "codex")
        defer { codex.remove() }
        guard case .started(let child) = await codex.send("Plan it.", planFirst: true) else { Issue.record("The send made no child."); return }
        #expect(child.startingMode == "onRequest")
    }

    @Test func aCodexOrAntigravityPaneWhoseChildCouldNotWriteMakesNoChild() async throws {
        for (provider, mode) in [("codex", "manual"), ("gemini", "manual"), ("gemini", "plan")] {
            let fixture = try await Fixture.make(parentMode: mode, provider: provider)
            defer { fixture.remove() }
            let before = try Data(contentsOf: fixture.store.fileURL)
            #expect(await fixture.send("Try it.") == .failed(L("delegation.human.error.readOnlyMode")), "\(provider) \(mode)")
            try await fixture.expectNothingMade(since: before)
        }
    }

    @Test func theAppCommitsACodexChildsWorkWhenItsReportChangesAndTheCardMergesIt() async throws {
        let fixture = try await Fixture.make(provider: "codex")
        defer { fixture.remove() }
        try await fixture.setIdentity()
        try Data("*.log\n".utf8).write(to: fixture.repo.appendingPathComponent(".gitignore"))
        try await fixture.git(["add", ".gitignore"]); try await fixture.git(["commit", "-q", "-m", "ignore logs"])
        let base = try await fixture.git(["rev-parse", "HEAD"])
        guard case .started(let child) = await fixture.send("Add a note.") else { Issue.record("The send made no child."); return }
        #expect(await fixture.waitFor(child.id, .running))
        let worktree = URL(fileURLWithPath: child.worktreePath), report = URL(fileURLWithPath: ChildWorktree.reportFile(worktreePath: child.worktreePath))

        // A run that leaves changes but no report: nothing is committed.
        try Data("changed\n".utf8).write(to: worktree.appendingPathComponent("file-0.txt"))
        try Data("Note\n".utf8).write(to: worktree.appendingPathComponent("note.txt"))
        try Data("noise\n".utf8).write(to: worktree.appendingPathComponent("build.log"))
        await fixture.coordinator.childRunEnded(child.id, runId: "send-run-of-\(child.id)", end: .finished)
        #expect(try await fixture.git(["rev-parse", "HEAD"], in: child.worktreePath) == base)
        #expect(await fixture.coordinator.file.children.first?.state == .ended)

        // A run that writes its report but is stopped: nothing is committed and the report is not new yet.
        await fixture.coordinator.childRunStarted(child.id, runId: "run-stopped")
        try Data("# Report\n\nAdded the note.\n".utf8).write(to: report)
        await fixture.coordinator.childRunEnded(child.id, runId: "run-stopped", end: .stopped)
        #expect(try await fixture.git(["rev-parse", "HEAD"], in: child.worktreePath) == base)
        #expect(await fixture.coordinator.file.children.first?.reportRevision == 0)

        // The next run finishes normally: the app commits what .gitignore lets in, then records the report at that head.
        await fixture.coordinator.childRunStarted(child.id, runId: "run-2")
        #expect(await fixture.coordinator.childRunEnded(child.id, runId: "run-2", end: .finished) == nil)
        let head = try await fixture.git(["rev-parse", "HEAD"], in: child.worktreePath)
        #expect(head != base)
        #expect(try await fixture.git(["log", "-1", "--format=%s"], in: child.worktreePath) == ChildAutoCommit.message(childId: child.id, revision: 1))
        #expect(try await fixture.git(["rev-parse", "HEAD^"], in: child.worktreePath) == base)
        #expect(try await fixture.git(["diff", "--name-only", base, head], in: child.worktreePath).split(separator: "\n") == ["file-0.txt", "note.txt"])
        #expect(try await fixture.git(["status", "--porcelain"], in: child.worktreePath).isEmpty)
        let record = await fixture.coordinator.file.children.first
        #expect(record?.state == .reported && record?.reportRevision == 1 && record?.reportHead == head)
        #expect(fixture.host.notes.isEmpty)

        // The human merges it from its card into the parent's branch.
        guard case .merged = await fixture.coordinator.mergeFromCard(child.id) else { Issue.record("The card did not merge the child."); return }
        #expect(try await fixture.git(["rev-parse", "HEAD"]) == head)
        #expect(FileManager.default.fileExists(atPath: fixture.repo.appendingPathComponent("note.txt").path))

        // A run that ends with the same report commits nothing more.
        await fixture.coordinator.childRunStarted(child.id, runId: "run-3")
        try Data("more\n".utf8).write(to: worktree.appendingPathComponent("note.txt"))
        await fixture.coordinator.childRunEnded(child.id, runId: "run-3", end: .finished)
        #expect(try await fixture.git(["rev-parse", "HEAD"], in: child.worktreePath) == head)
    }

    @Test func newFilesThatLookSecretOrAreLargeStayUncommittedAndThePaneNamesThem() async throws {
        let fixture = try await Fixture.make(provider: "gemini")
        defer { fixture.remove() }
        try await fixture.setIdentity()
        guard case .started(let child) = await fixture.send("Add the config.") else { Issue.record("The send made no child."); return }
        #expect(await fixture.waitFor(child.id, .running))
        let worktree = URL(fileURLWithPath: child.worktreePath)
        try FileManager.default.createDirectory(at: worktree.appendingPathComponent("keys"), withIntermediateDirectories: true)
        let secrets = [".env.local", "keys/server.pem", "keys/deploy.key", "id_rsa", "credentials.json"]
        for name in secrets + ["config.json"] { try Data("x\n".utf8).write(to: worktree.appendingPathComponent(name)) }
        try Data(count: ChildAutoCommit.maximumNewFileBytes + 1).write(to: worktree.appendingPathComponent("big.bin"))
        try Data("changed\n".utf8).write(to: worktree.appendingPathComponent("file-0.txt"))
        try Data("# Report\n".utf8).write(to: URL(fileURLWithPath: ChildWorktree.reportFile(worktreePath: child.worktreePath)))
        await fixture.coordinator.childRunEnded(child.id, runId: "send-run-of-\(child.id)", end: .finished)
        let committed = try await fixture.git(["show", "--name-only", "--format=", "HEAD"], in: child.worktreePath)
        #expect(committed.split(separator: "\n").sorted() == ["config.json", "file-0.txt"])
        let left = try await fixture.git(["ls-files", "--others", "--exclude-standard"], in: child.worktreePath)
        #expect(Set(left.split(separator: "\n").map(String.init)) == Set(secrets + ["big.bin"]))
        let note = try #require(fixture.host.notes.last)
        #expect(note.sessionId == child.id && note.text.hasPrefix(L("delegation.autoCommit.leftOut", ["count": "6", "paths": ""])))
        for name in secrets + ["big.bin"] { #expect(note.text.contains(name), "\(name)") }
        #expect(await fixture.coordinator.file.children.first?.state == .reported)
        for name in [".env", ".ENV.production", "a/b/cert.pem", "x.key", "id_rsa.pub", "credentials"] { #expect(ChildAutoCommit.looksSecret(name), "\(name)") }
        for name in ["environment.txt", "keyboard.swift", "a/.envoy/config", "my-credentials.txt"] { #expect(!ChildAutoCommit.looksSecret(name), "\(name)") }
    }

    @Test func anUnresolvedConflictIsNeverCommittedAndThePaneSaysSo() async throws {
        let fixture = try await Fixture.make(provider: "codex")
        defer { fixture.remove() }
        try await fixture.setIdentity()
        try await fixture.git(["checkout", "-q", "-b", "other"])
        try Data("theirs\n".utf8).write(to: fixture.repo.appendingPathComponent("file-0.txt"))
        try await fixture.git(["commit", "-q", "-am", "theirs"]); try await fixture.git(["checkout", "-q", "main"])
        guard case .started(let child) = await fixture.send("Change file-0.") else { Issue.record("The send made no child."); return }
        #expect(await fixture.waitFor(child.id, .running))
        let worktree = URL(fileURLWithPath: child.worktreePath)
        try Data("ours\n".utf8).write(to: worktree.appendingPathComponent("file-0.txt"))
        try await fixture.git(["commit", "-q", "-am", "ours"], in: child.worktreePath)
        let ours = try await fixture.git(["rev-parse", "HEAD"], in: child.worktreePath)
        // The child brought the other branch in and left the conflict unresolved.
        #expect(try await fixture.gitExitCode(["merge", "-q", "other"], in: child.worktreePath) != 0)
        try Data("# Report\n\nMerged other.\n".utf8).write(to: URL(fileURLWithPath: ChildWorktree.reportFile(worktreePath: child.worktreePath)))
        await fixture.coordinator.childRunEnded(child.id, runId: "send-run-of-\(child.id)", end: .finished)
        #expect(try await fixture.git(["rev-parse", "HEAD"], in: child.worktreePath) == ours)
        let unmerged = try await fixture.git(["ls-files", "-u"], in: child.worktreePath)
        #expect(!unmerged.isEmpty)
        // No new report: nothing for the card to merge, and the child's pane says why.
        let record = await fixture.coordinator.file.children.first
        #expect(record?.state == .ended && record?.reportRevision == 0 && record?.reportDigest == nil)
        #expect(fixture.host.notes.map(\.sessionId) == [child.id] && fixture.host.notes.map(\.text) == [L("delegation.autoCommit.unmerged")])

        // Resolved, the next run's end commits the merge and records the report.
        try Data("both\n".utf8).write(to: worktree.appendingPathComponent("file-0.txt"))
        try await fixture.git(["add", "file-0.txt"], in: child.worktreePath)
        await fixture.coordinator.childRunStarted(child.id, runId: "run-2")
        await fixture.coordinator.childRunEnded(child.id, runId: "run-2", end: .finished)
        let head = try await fixture.git(["rev-parse", "HEAD"], in: child.worktreePath)
        let parents = try await fixture.git(["rev-list", "--parents", "-n", "1", "HEAD"], in: child.worktreePath)
        #expect(head != ours && parents.split(separator: " ").count == 3)
        #expect(await fixture.coordinator.file.children.first?.reportHead == head)
    }

    @Test func theAppNeverCommitsForAClaudeChild() async throws {
        let fixture = try await Fixture.make()
        defer { fixture.remove() }
        try await fixture.setIdentity()
        let base = try await fixture.git(["rev-parse", "HEAD"])
        guard case .started(let child) = await fixture.send("Add a note.") else { Issue.record("The send made no child."); return }
        #expect(await fixture.waitFor(child.id, .running))
        try Data("Note\n".utf8).write(to: URL(fileURLWithPath: child.worktreePath).appendingPathComponent("note.txt"))
        try Data("# Report\n".utf8).write(to: URL(fileURLWithPath: ChildWorktree.reportFile(worktreePath: child.worktreePath)))
        await fixture.coordinator.childRunEnded(child.id, runId: "send-run-of-\(child.id)", end: .finished)
        #expect(try await fixture.git(["rev-parse", "HEAD"], in: child.worktreePath) == base)
        #expect(try await fixture.git(["status", "--porcelain"], in: child.worktreePath) == "?? note.txt")
        #expect(await fixture.coordinator.file.children.first?.reportHead == base)
    }
}
