import Foundation
import Testing
@testable import MightyCore

/// The app side of one launch, as follow-up delivery sees it: the panes a
/// test sets up. A steer reaches a running pane's run; a queued item or a
/// started run starts a new run "<launch>-run-<n>" in a pane that is not
/// running. Every hand-over is recorded with the delegation file as it was
/// on disk at that moment. A test may have the next hand-overs refused. The
/// host reports no run events, so what the coordinator records about a run
/// comes from delivery itself.
private final class FollowUpHost: DelegationHost, @unchecked Sendable {
    struct Handed: Sendable {
        var sessionId: String
        /// By steer or queue, or a started run when nil.
        var route: DeliveryRoute?
        var input: String
        var onDisk: DelegationFile?
        var runId: String?
    }

    let store: DelegationFileStore
    let launch: String
    private let lock = NSLock()
    private var panes: [String: DelegationPaneState] = [:]
    private var log: [Handed] = []
    private var runs = 0
    private var refusals = 0

    init(store: DelegationFileStore, launch: String) { self.store = store; self.launch = launch }

    var handed: [Handed] { lock.withLock { log } }
    /// The inputs that reached a pane.
    var taken: [Handed] { handed.filter { $0.runId != nil } }

    func set(_ sessionId: String, _ activity: DelegationPaneActivity, runId: String? = nil) {
        lock.withLock { panes[sessionId] = DelegationPaneState(sessionId: sessionId, permissionMode: "auto", folder: "/tmp", runId: runId, activity: activity) }
    }
    /// The next `count` hand-overs are not taken; the panes stay as they are.
    func refuseNext(_ count: Int) { lock.withLock { refusals = count } }

    func createPane(_ pane: DelegationChildPane) async -> Bool { false }
    func startRun(sessionId: String, input: String) async -> String? { hand(input, to: sessionId, route: nil) }
    func deliver(_ input: String, to sessionId: String, route: DeliveryRoute) async -> String? { hand(input, to: sessionId, route: route) }
    func paneState(sessionId: String) async -> DelegationPaneState? { lock.withLock { panes[sessionId] } }
    func stopRun(sessionId: String) async {}

    private func hand(_ input: String, to sessionId: String, route: DeliveryRoute?) -> String? {
        let onDisk = try? store.load()
        return lock.withLock {
            var runId: String?
            if refusals > 0 {
                refusals -= 1
            } else if var pane = panes[sessionId] {
                if route == .steer {
                    runId = pane.activity == .running ? pane.runId : nil
                } else if pane.activity != .running {
                    runs += 1
                    pane.runId = "\(launch)-run-\(runs)"; pane.activity = .running; panes[sessionId] = pane
                    runId = pane.runId
                }
            }
            log.append(Handed(sessionId: sessionId, route: route, input: input, onDisk: onDisk, runId: runId))
            return runId
        }
    }
}

/// A profile in a temp folder holding the delegation file. Each launch of
/// the app over it is a fresh host, whose panes have had no run since that
/// launch, and a coordinator that loads the file from disk. The pane
/// "parent" has the child c1, which reported revision 1 on its run "r-c1".
private struct Profile {
    let base: URL
    let store: DelegationFileStore

    static func make() throws -> Profile {
        let base = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-follow-up-\(UUID().uuidString)", isDirectory: true)
        let store = DelegationFileStore(directory: base.appendingPathComponent("profile", isDirectory: true))
        var child = ChildRecord(id: "c1", parentSessionId: "parent", worktreePath: base.appendingPathComponent("worktrees/c1").path, parentBranch: "main",
                                baseCommit: String(repeating: "a", count: 40), startingMode: "acceptEdits", requestKey: "key-c1",
                                reportRevision: 1, reportHead: String(repeating: "b", count: 40), state: .reported)
        child.runId = "r-c1"; child.awaitedRunId = "r-c1"; child.reportDigest = String(repeating: "c", count: 64)
        try store.save(DelegationFile(children: [child]))
        return Profile(base: base, store: store)
    }

    func remove() { try? FileManager.default.removeItem(at: base) }

    /// One launch: its host, with the parent's and c1's panes open and idle
    /// as after any launch, and its coordinator, which runs the launch's sweep.
    func launch(_ name: String) async throws -> (FollowUpHost, DelegationCoordinator) {
        let host = FollowUpHost(store: store, launch: name)
        host.set("parent", .idle)
        host.set("c1", .idle)
        let worktrees = ChildWorktreeMaker(root: base.appendingPathComponent("worktrees", isDirectory: true), freeBytes: { _ in 50_000_000_000 })
        let coordinator = try DelegationCoordinator(store: store, host: host, worktrees: worktrees, isSwitchOn: { true })
        await coordinator.interruptRunsKilledByQuit()
        return (host, coordinator)
    }

    /// The parent pane's call of `tool`.
    func call(_ tool: String, _ arguments: [String: String], on coordinator: DelegationCoordinator) async -> DelegationResponse {
        let location = PaneMCPServerLocation(socketPath: base.appendingPathComponent("io.sock").path, executable: URL(fileURLWithPath: "/usr/bin/true"))
        let binding = PaneMCPBinding(agentPaneId: "parent", token: "token-parent", server: location, workspaceId: "ws-1", workspacePath: base.path, provider: "claude", delegation: true)
        return await coordinator.handle(DelegationRequest(tool: tool, arguments: arguments), binding: binding)
    }

    func followUp(_ text: String, on coordinator: DelegationCoordinator) async -> DelegationResponse {
        await call("follow_up", ["child": "c1", "text": text], on: coordinator)
    }

    /// The follow-up as c1's run reads it.
    func text(of id: String, in coordinator: DelegationCoordinator) async throws -> String {
        let followUp = try #require(await coordinator.file.followUps.first { $0.id == id })
        return DelegationCoordinator.text(of: followUp, reportFile: ChildWorktree.reportFile(worktreePath: base.appendingPathComponent("worktrees/c1").path))
    }

    func child(_ coordinator: DelegationCoordinator) async throws -> ChildRecord {
        try #require(await coordinator.file.children.first { $0.id == "c1" })
    }
}

@Suite(.serialized, .delegationLane) struct DelegationFollowUpTests {
    @Test func aFollowUpToAReportedChildIdleAfterANormalFinishStartsItsNextRunOnceAndClearsReported() async throws {
        let profile = try Profile.make()
        defer { profile.remove() }
        let (host, coordinator) = try await profile.launch("launch1")
        host.set("parent", .running, runId: "p-run")
        // c1's run "r-c1" finished normally since this launch, with its report.
        host.set("c1", .finished, runId: "r-c1")

        let answer = try #require(await profile.followUp("Also add tests.", on: coordinator).followUp)
        #expect(answer.lane == .delivered && answer.remaining == 1 && answer.child == "c1")
        let run = try #require(host.handed.first)
        #expect(host.handed.count == 1)
        #expect(run.sessionId == "c1" && run.route == .queue && run.runId == "launch1-run-1")
        let text = try await profile.text(of: answer.id, in: coordinator)
        #expect(run.input == text)
        #expect(run.input.contains(answer.id) && run.input.contains("Also add tests."))
        // Saved delivered before c1's run got it.
        #expect(run.onDisk?.followUps.first { $0.id == answer.id }?.lane == .delivered)

        // c1 runs the follow-up: reported is cleared and its run is the one the parent awaits.
        let child = try await profile.child(coordinator)
        #expect(child.state == .running && child.runId == "launch1-run-1" && child.awaitedRunId == "launch1-run-1")
        #expect(child.reportRevision == 1 && child.followUpCount == 1)
        let saved = try profile.store.load()
        #expect(await coordinator.file == saved)
        let followUp = try #require(saved.followUps.first)
        #expect(followUp.lane == .delivered && followUp.receipt?.route == .queue && followUp.receipt?.runId == "launch1-run-1")
        // The parent sees it too.
        let listed = await profile.call("list_children", [:], on: coordinator).children
        #expect(listed?.map(\.state) == [.running])

        // Never twice: another pass, a send or run next in c1 hands nothing more.
        await coordinator.deliverPending()
        #expect(await coordinator.send("Thanks.", in: "c1") == .nothingHeld)
        #expect(await coordinator.runNext(in: "c1") == .nothingHeld)
        #expect(host.taken.filter { $0.input.contains(answer.id) }.count == 1)

        // The follow-up's run ends with no new report: the parent gets one notice.
        let notice = try #require(await coordinator.childRunEnded("c1", runId: "launch1-run-1", end: .finished))
        #expect(notice.kind == .endedWithoutReport && notice.reportRevision == 1)
        let ended = try await profile.child(coordinator)
        #expect(ended.state == .ended)
        #expect(host.taken.filter { $0.input.contains(answer.id) }.count == 1)
    }

    @Test func heldFollowUpsReachTheChildOnceAfterARelaunchFromTheSameProfileAndClearReportedThen() async throws {
        let profile = try Profile.make()
        defer { profile.remove() }
        let (host, coordinator) = try await profile.launch("launch1")
        host.set("parent", .running, runId: "p-run")
        // A human stopped c1's run after it reported.
        host.set("c1", .idle, runId: "r-c1")

        let first = try #require(await profile.followUp("Also add tests.", on: coordinator).followUp)
        let second = try #require(await profile.followUp("그리고 문서도 고쳐 줘.", on: coordinator).followUp)
        #expect(first.lane == .held && second.lane == .held && second.remaining == 0)
        #expect(host.handed.isEmpty)
        // Held, so c1 has not run them: it is still reported.
        let waiting = try await profile.child(coordinator)
        #expect(waiting.state == .reported)
        #expect(await coordinator.heldItems(for: "c1").map(\.id) == [first.id, second.id])
        let firstText = try await profile.text(of: first.id, in: coordinator)
        let secondText = try await profile.text(of: second.id, in: coordinator)

        // The app quits and opens again from the same profile.
        let (relaunchedHost, relaunched) = try await profile.launch("launch2")
        relaunchedHost.set("parent", .running, runId: "p-run-2")
        #expect(await relaunched.file == (try profile.store.load()))
        #expect(await relaunched.file.followUps.map(\.lane) == [.held, .held])
        #expect(await relaunched.heldItems(for: "c1").map(\.id) == [first.id, second.id])
        #expect(await relaunched.heldItems(for: "c1").map(\.kind) == [.followUp, .followUp])
        // Nothing reaches c1 on its own, even when it is idle after a normal finish.
        relaunchedHost.set("c1", .finished, runId: "launch2-run-0")
        await relaunched.deliverPending()
        #expect(relaunchedHost.handed.isEmpty)
        let restored = try await profile.child(relaunched)
        #expect(restored.state == .reported)
        // A third follow-up is refused and changes nothing.
        let before = try profile.store.load()
        #expect(await profile.followUp("하나 더.", on: relaunched).refused == .followUpLimit)
        #expect(try profile.store.load() == before)

        // The human's next send in c1 releases both, oldest first, ahead of the text, in one run.
        relaunchedHost.set("c1", .idle, runId: "launch2-run-0")
        let release = await relaunched.send("계속해 줘.", in: "c1")
        #expect(release == .released(runId: "launch2-run-1", itemIds: [first.id, second.id]))
        let run = try #require(relaunchedHost.handed.first)
        #expect(relaunchedHost.handed.count == 1 && run.sessionId == "c1" && run.route == nil)
        #expect(run.input == [firstText, secondText, "계속해 줘."].joined(separator: "\n\n"))
        #expect(run.onDisk?.followUps.map(\.lane) == [.delivered, .delivered])
        // c1 runs them now: reported is cleared.
        let child = try await profile.child(relaunched)
        #expect(child.state == .running && child.runId == "launch2-run-1" && child.awaitedRunId == "launch2-run-1")
        let saved = try profile.store.load()
        #expect(saved.followUps.map(\.lane) == [.delivered, .delivered])
        #expect(saved.followUps.map { $0.receipt?.runId } == ["launch2-run-1", "launch2-run-1"])

        // Opened once more: they are delivered, so nothing goes again.
        let (thirdHost, third) = try await profile.launch("launch3")
        thirdHost.set("c1", .finished, runId: "launch3-run-0")
        #expect(await third.heldItems(for: "c1").isEmpty)
        await third.deliverPending()
        #expect(await third.send("Hello?", in: "c1") == .nothingHeld)
        #expect(await third.runNext(in: "c1") == .nothingHeld)
        #expect(thirdHost.handed.isEmpty)
        #expect(try profile.store.load().followUps == saved.followUps)
        // The relaunch's sweep found c1 running the follow-ups when the app quit.
        let swept = try await profile.child(third)
        #expect(swept.state == .interrupted)
        for id in [first.id, second.id] {
            #expect((host.taken + relaunchedHost.taken + thirdHost.taken).filter { $0.input.contains(id) }.count == 1, "\(id)")
        }
    }

    @Test func aFollowUpLeftPendingAtQuitIsHeldAtLaunchAndRunNextHandsItOverOnce() async throws {
        let profile = try Profile.make()
        defer { profile.remove() }
        let (host, coordinator) = try await profile.launch("launch1")
        host.set("parent", .running, runId: "p-run")
        host.set("c1", .finished, runId: "r-c1")
        // c1's pane does not take it, twice running: it stays pending.
        host.refuseNext(2)
        let answer = try #require(await profile.followUp("Also add tests.", on: coordinator).followUp)
        #expect(answer.lane == .pending)
        #expect(host.handed.count == 2 && host.taken.isEmpty)
        #expect(try profile.store.load().followUps.map(\.lane) == [.pending])
        let notRun = try await profile.child(coordinator)
        #expect(notRun.state == .reported)

        // At the next launch it is held, and run next in c1 hands it over alone, once.
        let (relaunchedHost, relaunched) = try await profile.launch("launch2")
        #expect(try profile.store.load().followUps.map(\.lane) == [.held])
        #expect(await relaunched.heldItems(for: "c1").map(\.id) == [answer.id])
        #expect(relaunchedHost.handed.isEmpty)
        let release = await relaunched.runNext(in: "c1")
        #expect(release == .released(runId: "launch2-run-1", itemIds: [answer.id]))
        let text = try await profile.text(of: answer.id, in: relaunched)
        #expect(relaunchedHost.handed.map(\.input) == [text])
        let child = try await profile.child(relaunched)
        #expect(child.state == .running && child.runId == "launch2-run-1" && child.awaitedRunId == "launch2-run-1")
        #expect(await relaunched.runNext(in: "c1") == .nothingHeld)
        #expect((host.taken + relaunchedHost.taken).filter { $0.input.contains(answer.id) }.count == 1)
    }

    @Test func aFollowUpSavedDeliveredJustBeforeTheAppQuitIsNeverHandedOverAgain() async throws {
        let profile = try Profile.make()
        defer { profile.remove() }
        // Saved delivered by queue, and the app quit before c1's run started.
        var file = try profile.store.load()
        file.children[0].followUpCount = 1
        file.followUps = [FollowUp(id: "f-written", childId: "c1", text: "Also add tests.", lane: .delivered,
                                   receipt: DeliveryReceipt(time: "2026-10-09T01:00:00Z", route: .queue, runId: ""))]
        try profile.store.save(file)

        let (host, coordinator) = try await profile.launch("launch1")
        host.set("c1", .finished, runId: "r-c1")
        #expect(await coordinator.heldItems(for: "c1").isEmpty)
        await coordinator.deliverPending()
        #expect(await coordinator.send("Go on.", in: "c1") == .nothingHeld)
        #expect(await coordinator.runNext(in: "c1") == .nothingHeld)
        #expect(host.handed.isEmpty)
        #expect(try profile.store.load() == file)
    }

    @Test func twoHeldFollowUpsAtTheMostAFollowUpMayTakeFitTheOneRunThatRunNextReleasesThemIn() async throws {
        let profile = try Profile.make()
        defer { profile.remove() }
        let (host, coordinator) = try await profile.launch("launch1")
        host.set("parent", .running, runId: "p-run")
        // c1's pane is idle after a stop, so both follow-ups are held; each is as long as follow_up takes.
        #expect(DelegationCoordinator.maximumFollowUpBytes == 49_152)
        let longest = String(repeating: "x", count: DelegationCoordinator.maximumFollowUpBytes)
        let first = try #require(await profile.followUp(longest, on: coordinator).followUp)
        let second = try #require(await profile.followUp(longest, on: coordinator).followUp)
        #expect(first.lane == .held && second.lane == .held && second.remaining == 0)
        #expect(host.handed.isEmpty)

        // Run next releases both, oldest first, in one run whose input the app's start accepts.
        let release = await coordinator.runNext(in: "c1")
        #expect(release == .released(runId: "launch1-run-1", itemIds: [first.id, second.id]))
        let run = try #require(host.handed.first)
        #expect(host.handed.count == 1 && run.sessionId == "c1" && run.route == nil)
        let texts = [try await profile.text(of: first.id, in: coordinator), try await profile.text(of: second.id, in: coordinator)]
        #expect(run.input == texts.joined(separator: "\n\n"))
        let request = StartRunRequest(sessionId: "c1", workspaceId: "ws-1", kind: SessionKind.claude, input: run.input, provider: "claude",
                                      settings: RunSettings(permissionMode: "acceptEdits"))
        #expect(throws: Never.self) { try CoreValidation.validate(request) }
        #expect(await coordinator.heldItems(for: "c1").isEmpty)
    }
}
