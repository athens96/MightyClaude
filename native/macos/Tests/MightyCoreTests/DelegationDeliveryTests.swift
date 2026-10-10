import Foundation
import Testing
@testable import MightyCore

/// The app side as delivery sees it: panes a test sets up, which take an item
/// the way a pane does. A steer reaches a running pane's run; a queued item or
/// a started run starts a new run in a pane that is not running. Every hand-over
/// is recorded with the delegation file as it was on disk at that moment. A
/// test may have the next hand-overs refused, the pane moving on as they are.
private final class DeliveryHost: DelegationHost, @unchecked Sendable {
    /// One hand-over: by `route`, or a started run when nil.
    struct Handed: Sendable {
        var sessionId: String
        var route: DeliveryRoute?
        var input: String
        var onDisk: DelegationFile?
        var runId: String?
    }

    let store: DelegationFileStore
    private let lock = NSLock()
    private var panes: [String: DelegationPaneState] = [:]
    private var log: [Handed] = []
    private var runs = 0
    private var refusals: [DelegationPaneActivity] = []

    init(store: DelegationFileStore) { self.store = store }

    var handed: [Handed] { lock.withLock { log } }
    /// The inputs that reached a pane.
    var taken: [Handed] { handed.filter { $0.runId != nil } }

    func set(_ sessionId: String, _ activity: DelegationPaneActivity, runId: String? = nil, provider: String = "claude") {
        lock.withLock { panes[sessionId] = DelegationPaneState(sessionId: sessionId, provider: provider, permissionMode: "auto", folder: "/tmp", runId: runId, activity: activity) }
    }
    /// The pane as the app's host reads it (``DelegationPanes/paneState(of:in:runId:activity:startRefused:)``).
    func put(_ pane: DelegationPaneState) { lock.withLock { panes[pane.sessionId] = pane } }
    func close(_ sessionId: String) { lock.withLock { _ = panes.removeValue(forKey: sessionId) } }
    func pane(_ sessionId: String) -> DelegationPaneState? { lock.withLock { panes[sessionId] } }
    /// Refuses the next hand-overs, one per entry; each leaves its pane in that activity.
    func refuseNext(_ after: [DelegationPaneActivity]) { lock.withLock { refusals = after } }

    func createPane(_ pane: DelegationChildPane) async -> Bool { false }

    func startRun(sessionId: String, input: String) async -> String? { hand(input, to: sessionId, route: nil) }

    func deliver(_ input: String, to sessionId: String, route: DeliveryRoute) async -> String? { hand(input, to: sessionId, route: route) }

    func paneState(sessionId: String) async -> DelegationPaneState? { pane(sessionId) }
    func stopRun(sessionId: String) async {}

    private func hand(_ input: String, to sessionId: String, route: DeliveryRoute?) -> String? {
        let onDisk = try? store.load()
        return lock.withLock {
            var runId: String?
            if !refusals.isEmpty {
                panes[sessionId]?.activity = refusals.removeFirst()
            } else if var pane = panes[sessionId] {
                if route == .steer {
                    runId = pane.activity == .running ? pane.runId : nil
                } else if pane.activity != .running {
                    runs += 1
                    pane.runId = "run-\(runs)"; pane.activity = .running; panes[sessionId] = pane
                    runId = pane.runId
                }
            }
            log.append(Handed(sessionId: sessionId, route: route, input: input, onDisk: onDisk, runId: runId))
            return runId
        }
    }
}

/// A profile in a temp folder with the delegation file, and a coordinator
/// over it. The pane "parent" has the running children c1, c2 and c3, each
/// on its first run "r-<id>", which delegate started. No child has a
/// REPORT.md, so each run's end sends one ended_without_report notice.
private struct Fixture {
    let base: URL
    let store: DelegationFileStore
    let host: DeliveryHost
    let coordinator: DelegationCoordinator

    static func make(parent: DelegationPaneActivity?, file: DelegationFile? = nil) throws -> Fixture {
        let base = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-delivery-\(UUID().uuidString)", isDirectory: true)
        let store = DelegationFileStore(directory: base.appendingPathComponent("profile", isDirectory: true))
        try store.save(file ?? DelegationFile(children: ["c1", "c2", "c3"].map { running($0, base: base) }))
        let host = DeliveryHost(store: store)
        if let parent { host.set("parent", parent, runId: parent == .idle ? nil : "p-run") }
        return try Fixture(base: base, store: store, host: host, coordinator: coordinator(store, host, base))
    }

    static func coordinator(_ store: DelegationFileStore, _ host: DeliveryHost, _ base: URL) throws -> DelegationCoordinator {
        let worktrees = ChildWorktreeMaker(root: base.appendingPathComponent("worktrees", isDirectory: true), freeBytes: { _ in 50_000_000_000 })
        return try DelegationCoordinator(store: store, host: host, worktrees: worktrees, isSwitchOn: { true })
    }

    static func running(_ id: String, base: URL, state: ChildState = .running) -> ChildRecord {
        var child = ChildRecord(id: id, parentSessionId: "parent", worktreePath: base.appendingPathComponent("worktrees/\(id)").path, parentBranch: "main",
                                baseCommit: String(repeating: "a", count: 40), startingMode: "acceptEdits", requestKey: "key-\(id)", state: state)
        child.runId = "r-\(id)"; child.awaitedRunId = "r-\(id)"
        return child
    }

    func remove() { try? FileManager.default.removeItem(at: base) }

    /// The child's run "r-<id>" finished: its notice, as it was made.
    func end(_ id: String) async throws -> Notice {
        try #require(await coordinator.childRunEnded(id, runId: "r-\(id)", end: .finished))
    }

    func saved(_ notice: Notice) throws -> Notice? { try store.load().notices.first { $0.id == notice.id } }
    func held(_ notice: Notice) -> Notice { var held = notice; held.lane = .held; return held }

    func followUp(_ child: String, _ text: String) async -> DelegationResponse {
        let location = PaneMCPServerLocation(socketPath: base.appendingPathComponent("io.sock").path, executable: URL(fileURLWithPath: "/usr/bin/true"))
        let binding = PaneMCPBinding(agentPaneId: "parent", token: "token-parent", server: location, workspaceId: "ws-1", workspacePath: base.path, provider: "claude", delegation: true)
        return await coordinator.handle(DelegationRequest(tool: "follow_up", arguments: ["child": child, "text": text]), binding: binding)
    }

    /// Every one of `ids` reached a pane in exactly one input.
    func expectEachTakenOnce(_ ids: [String]) {
        for id in ids { #expect(host.taken.filter { $0.input.contains(id) }.count == 1, "\(id)") }
    }
}

@Suite(.serialized) struct DelegationDeliveryTests {
    @Test func aPaneGetsItemsOnItsOwnOnlyWhileItRunsClaudeOrIsIdleAfterANormalFinish() {
        func pane(_ activity: DelegationPaneActivity, kind: String = SessionKind.claude, provider: String = "claude") -> DelegationPaneState {
            DelegationPaneState(sessionId: "p", kind: kind, provider: provider, permissionMode: "auto", folder: "/tmp", runId: "p-run", activity: activity)
        }
        #expect(DelegationCoordinator.automaticRoute(to: pane(.running)) == .steer)
        #expect(DelegationCoordinator.automaticRoute(to: pane(.finished)) == .queue)
        // Stopped, errored, or no run since this launch.
        #expect(DelegationCoordinator.automaticRoute(to: pane(.idle)) == nil)
        // Only a running Claude pane is steered into; a closed pane gets nothing.
        #expect(DelegationCoordinator.automaticRoute(to: pane(.running, provider: "codex")) == nil)
        #expect(DelegationCoordinator.automaticRoute(to: pane(.running, kind: "terminal")) == nil)
        #expect(DelegationCoordinator.automaticRoute(to: nil) == nil)
    }

    @Test func aNoticeIsSteeredIntoTheParentsRunningClaudeRunOnceAndSavedDeliveredBeforeTheRunGetsIt() async throws {
        let fixture = try Fixture.make(parent: .running)
        defer { fixture.remove() }
        let notice = try await fixture.end("c1")

        let handed = try #require(fixture.host.handed.first)
        #expect(fixture.host.handed.count == 1)
        #expect(handed.sessionId == "parent" && handed.route == .steer && handed.runId == "p-run")
        #expect(handed.input == DelegationCoordinator.text(of: notice))
        #expect(handed.input.contains(notice.id) && handed.input.contains("c1") && handed.input.contains("child_status"))
        // On disk before the run got it: delivered, by steer into the running run.
        let written = try #require(handed.onDisk?.notices.first { $0.id == notice.id })
        #expect(written.lane == .delivered && written.receipt?.route == .steer && written.receipt?.runId == "p-run")

        let saved = try #require(try fixture.saved(notice))
        #expect(saved.lane == .delivered && saved.receipt?.route == .steer && saved.receipt?.runId == "p-run" && saved.receipt?.time.isEmpty == false)
        #expect(await fixture.coordinator.file == (try fixture.store.load()))
        // Never twice: the same end again, or another pass, hands nothing more.
        #expect(await fixture.coordinator.childRunEnded("c1", runId: "r-c1", end: .finished) == nil)
        await fixture.coordinator.deliverPending()
        #expect(await fixture.coordinator.send("Go on.", in: "parent") == .nothingHeld)
        #expect(fixture.host.handed.count == 1)
    }

    @Test func aNoticeStartsTheNextRunOfAParentIdleAfterANormalFinishAndTheNextIsSteeredIntoIt() async throws {
        let fixture = try Fixture.make(parent: .finished)
        defer { fixture.remove() }
        let first = try await fixture.end("c1")
        let second = try await fixture.end("c2")

        let handed = fixture.host.handed
        #expect(handed.map(\.route) == [.queue, .steer])
        #expect(handed.map(\.runId) == ["run-1", "run-1"])
        #expect(handed.map(\.input) == [DelegationCoordinator.text(of: first), DelegationCoordinator.text(of: second)])
        let firstWritten = try #require(handed[0].onDisk?.notices.first { $0.id == first.id })
        #expect(firstWritten.lane == .delivered && firstWritten.receipt?.route == .queue)
        #expect(try fixture.saved(first)?.receipt?.runId == "run-1" && fixture.saved(first)?.lane == .delivered)
        #expect(try fixture.saved(second)?.receipt == DeliveryReceipt(time: try #require(try fixture.saved(second)?.receipt?.time), route: .steer, runId: "run-1"))
        fixture.expectEachTakenOnce([first.id, second.id])
    }

    @Test func aPendingNoticeForAParentThatFinishedNormallyButCannotStartARunNowIsHeldAndNoRunIsAskedFor() async throws {
        let fixture = try Fixture.make(parent: .running)
        defer { fixture.remove() }
        // The parent's run is still starting: both steers are refused and the notice stays pending.
        fixture.host.refuseNext([.running, .running])
        let first = try await fixture.end("c1")
        #expect(try fixture.saved(first) == first)
        let asked = fixture.host.handed.count

        // Its run then finished normally, but the app's start would refuse a new one now (a CLI
        // update or a Claude model reset holds it, or its request does not validate).
        let parent = RunSession(id: "parent", workspaceId: "ws", title: "Parent", settings: RunSettings(permissionMode: "auto"))
        let workspace = Workspace(id: "ws", name: "repo", path: fixture.base.path)
        let refused = DelegationPanes.paneState(of: parent, in: workspace, runId: "p-run", activity: .finished, startRefused: true)
        #expect(refused.activity == .idle && refused.runId == "p-run")
        fixture.host.put(refused)
        // The app offers it again as the run ends: it is held, a row of the parent's queued list,
        // and the host is asked for no steer and no run. So is a notice made meanwhile.
        await fixture.coordinator.deliverPending()
        let second = try await fixture.end("c2")
        #expect(fixture.host.handed.count == asked)
        #expect(try fixture.store.load().notices == [fixture.held(first), fixture.held(second)])
        #expect(await fixture.coordinator.heldItems(for: "parent").map(\.id) == [first.id, second.id])

        // Once a run can start again, the human's next send releases both, oldest first, in one run.
        fixture.host.put(DelegationPanes.paneState(of: parent, in: workspace, runId: "p-run", activity: .finished, startRefused: false))
        #expect(await fixture.coordinator.send("Go on.", in: "parent") == .released(runId: "run-1", itemIds: [first.id, second.id]))
        fixture.expectEachTakenOnce([first.id, second.id])
    }

    @Test func otherwiseItIsHeldUntilOneSendReleasesAllOfThemOldestFirstAheadOfTheHumansTextInOneRun() async throws {
        // Stopped, errored, or no run since this launch.
        let fixture = try Fixture.make(parent: .idle)
        defer { fixture.remove() }
        let first = try await fixture.end("c1")
        let second = try await fixture.end("c2")
        #expect(fixture.host.handed.isEmpty)
        #expect(try fixture.store.load().notices == [fixture.held(first), fixture.held(second)])
        #expect(await fixture.coordinator.heldItems(for: "parent").map(\.id) == [first.id, second.id])
        #expect(await fixture.coordinator.heldItems(for: "parent").map(\.kind) == [.notice, .notice])

        let release = await fixture.coordinator.send("이제 합쳐 줘.", in: "parent")
        #expect(release == .released(runId: "run-1", itemIds: [first.id, second.id]))
        let run = try #require(fixture.host.handed.first)
        #expect(fixture.host.handed.count == 1 && run.route == nil && run.sessionId == "parent")
        #expect(run.input == [DelegationCoordinator.text(of: first), DelegationCoordinator.text(of: second), "이제 합쳐 줘."].joined(separator: "\n\n"))
        // Both saved delivered before the run got them.
        #expect(run.onDisk?.notices.map(\.lane) == [.delivered, .delivered])
        let saved = try fixture.store.load().notices
        #expect(saved.map(\.lane) == [.delivered, .delivered])
        #expect(saved.map { $0.receipt?.route } == [.queue, .queue] && saved.map { $0.receipt?.runId } == ["run-1", "run-1"])
        #expect(await fixture.coordinator.heldItems(for: "parent").isEmpty)
        // The next send has nothing to release: the caller sends it as before.
        #expect(await fixture.coordinator.send("Thanks.", in: "parent") == .nothingHeld)
        #expect(await fixture.coordinator.runNext(in: "parent") == .nothingHeld)
        #expect(fixture.host.handed.count == 1)
        fixture.expectEachTakenOnce([first.id, second.id])
    }

    @Test func runNextReleasesOnlyTheHeldItemsInOneRunAndTheHumansQueuedRowsFollowInLaterRuns() async throws {
        let fixture = try Fixture.make(parent: .idle)
        defer { fixture.remove() }
        let first = try await fixture.end("c1")
        let second = try await fixture.end("c2")
        // The app's run next: held items first; with none, its own next queued row.
        var rows = ["Human row 1", "Human row 2"]
        func runNext() async {
            if await fixture.coordinator.runNext(in: "parent") == .nothingHeld { _ = await fixture.host.startRun(sessionId: "parent", input: rows.removeFirst()) }
            fixture.host.set("parent", .finished, runId: fixture.host.pane("parent")?.runId)
        }

        await runNext()
        await runNext()
        await runNext()
        let inputs = fixture.host.handed.map(\.input)
        #expect(inputs == [DelegationCoordinator.text(of: first) + "\n\n" + DelegationCoordinator.text(of: second), "Human row 1", "Human row 2"])
        #expect(fixture.host.handed.map(\.runId) == ["run-1", "run-2", "run-3"])
        #expect(try fixture.store.load().notices.map { $0.receipt?.runId } == ["run-1", "run-1"])
        fixture.expectEachTakenOnce([first.id, second.id])
    }

    @Test func twoSendsAtOnceReleaseEachHeldItemOnce() async throws {
        let fixture = try Fixture.make(parent: .idle)
        defer { fixture.remove() }
        let first = try await fixture.end("c1")
        let second = try await fixture.end("c2")

        async let mac = fixture.coordinator.send("From the Mac.", in: "parent")
        async let phone = fixture.coordinator.send("From the phone.", in: "parent")
        let results = await [mac, phone]
        #expect(results.filter { $0 == .nothingHeld }.count == 1)
        #expect(results.contains(.released(runId: "run-1", itemIds: [first.id, second.id])))
        #expect(fixture.host.handed.count == 1)
        fixture.expectEachTakenOnce([first.id, second.id])
    }

    @Test func aRunThatEndsAsANoticeIsSteeredInGetsItAsTheNextRunAndAPaneThatTakesNothingLeavesItPending() async throws {
        let fixture = try Fixture.make(parent: .running)
        defer { fixture.remove() }
        // The parent's run ends normally just as the notice is steered in.
        fixture.host.refuseNext([.finished])
        let first = try await fixture.end("c1")
        #expect(fixture.host.handed.map(\.route) == [.steer, .queue])
        #expect(fixture.host.handed.map(\.runId) == [nil, "run-1"])
        #expect(try fixture.saved(first)?.lane == .delivered && fixture.saved(first)?.receipt?.route == .queue && fixture.saved(first)?.receipt?.runId == "run-1")

        // A pane that takes nothing twice running: the notice stays pending, as it was.
        fixture.host.refuseNext([.running, .running])
        let second = try await fixture.end("c2")
        #expect(fixture.host.handed.count == 4)
        #expect(try fixture.saved(second) == second)
        // It is offered again with the next notice, and each reaches the parent once.
        let third = try await fixture.end("c3")
        #expect(try fixture.store.load().notices.map(\.lane) == [.delivered, .delivered, .delivered])
        #expect(try fixture.store.load().notices.map { $0.receipt?.route } == [.queue, .steer, .steer])
        fixture.expectEachTakenOnce([first.id, second.id, third.id])
    }

    @Test func everythingPendingAtLaunchIsHeldAndARebuiltCoordinatorStartsNoRunByItself() async throws {
        let base = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-delivery-\(UUID().uuidString)", isDirectory: true)
        let receipt = DeliveryReceipt(time: "2026-10-09T01:00:00Z", route: .steer, runId: "old-run")
        let old = DelegationFile(
            children: [Fixture.running("c1", base: base, state: .ended), Fixture.running("c2", base: base)],
            notices: [Notice(id: "n-old", childId: "c1", reportRevision: 0, kind: .endedWithoutReport),
                      Notice(id: "n-done", childId: "c1", reportRevision: 0, kind: .failedToStart, lane: .delivered, receipt: receipt)],
            followUps: [FollowUp(id: "f-old", childId: "c1", text: "테스트도 추가해 줘")])
        let store = DelegationFileStore(directory: base.appendingPathComponent("profile", isDirectory: true))
        try store.save(old)
        defer { try? FileManager.default.removeItem(at: base) }
        // Both panes would take an item on their own now.
        let host = DeliveryHost(store: store)
        host.set("parent", .finished, runId: "p-run")
        host.set("c1", .finished, runId: "r-c1")
        let coordinator = try Fixture.coordinator(store, host, base)

        #expect(host.handed.isEmpty)
        let restored = try store.load()
        #expect(restored.notices.map(\.lane) == [.held, .delivered] && restored.notices[1].receipt == receipt)
        #expect(restored.followUps.map(\.lane) == [.held])
        #expect(await coordinator.file == restored)
        #expect(await coordinator.heldItems(for: "parent").map(\.id) == ["n-old"])
        #expect(await coordinator.heldItems(for: "c1").map(\.id) == ["f-old"])

        // A new notice goes on its own; what launch held stays held.
        let fresh = try #require(await coordinator.childRunEnded("c2", runId: "r-c2", end: .finished))
        #expect(host.handed.map(\.input) == [DelegationCoordinator.text(of: fresh)])
        #expect(await coordinator.heldItems(for: "parent").map(\.id) == ["n-old"])
        // The human's next send releases it.
        host.set("parent", .finished, runId: "run-1")
        #expect(await coordinator.send("Go on.", in: "parent") == .released(runId: "run-2", itemIds: ["n-old"]))
        #expect(host.handed.last?.input == DelegationCoordinator.text(of: old.notices[0]) + "\n\nGo on.")
        #expect(host.handed.filter { $0.input.contains("n-done") }.isEmpty)
    }

    @Test func heldNoticesForAClosedParentsChildrenStayOnTheChildsCard() async throws {
        let fixture = try Fixture.make(parent: .idle)
        defer { fixture.remove() }
        // Held while the parent's pane was open, and then the pane closed.
        let first = try await fixture.end("c1")
        fixture.host.close("parent")
        // Made after the parent's pane closed.
        let second = try await fixture.end("c2")
        #expect(fixture.host.handed.isEmpty)
        #expect(await fixture.coordinator.heldNotices(of: "c1") == [fixture.held(first)])
        #expect(await fixture.coordinator.heldNotices(of: "c2") == [fixture.held(second)])
        #expect(await fixture.coordinator.heldNotices(of: "c3").isEmpty)
        // The child rows the app's sidebar follows carry the count each card shows.
        var rows = fixture.coordinator.childRows.makeAsyncIterator()
        #expect(await rows.next()?.map(\.heldNotices) == [1, 1, 0])
        // Nothing releases them: the parent's pane takes no run.
        #expect(await fixture.coordinator.send("Hello?", in: "parent") == .notStarted)
        #expect(await fixture.coordinator.runNext(in: "parent") == .notStarted)
        #expect(fixture.host.handed.isEmpty)
        #expect(try fixture.store.load().notices == [fixture.held(first), fixture.held(second)])
        // Across a relaunch they stay on the cards.
        let relaunched = try Fixture.coordinator(fixture.store, fixture.host, fixture.base)
        #expect(await relaunched.heldNotices(of: "c1") == [fixture.held(first)])
        #expect(await relaunched.heldNotices(of: "c2") == [fixture.held(second)])
        var relaunchedRows = relaunched.childRows.makeAsyncIterator()
        #expect(await relaunchedRows.next()?.map(\.heldNotices) == [1, 1, 0])
        #expect(fixture.host.handed.isEmpty)
    }

    @Test func aFollowUpGoesToItsChildsPaneByTheSameRules() async throws {
        let base = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-delivery-\(UUID().uuidString)", isDirectory: true)
        var steered = Fixture.running("c1", base: base)
        steered.awaitedRunId = nil
        let fixture = try Fixture.make(parent: .running, file: DelegationFile(children: [steered, Fixture.running("c2", base: base, state: .ended)]))
        defer { fixture.remove(); try? FileManager.default.removeItem(at: base) }
        fixture.host.set("c1", .running, runId: "r-c1")
        // c2's run was stopped.
        fixture.host.set("c2", .idle, runId: "r-c2")

        let toRunning = try #require(await fixture.followUp("c1", "Also add tests.").followUp)
        #expect(toRunning.lane == .delivered)
        let steer = try #require(fixture.host.handed.first)
        #expect(steer.sessionId == "c1" && steer.route == .steer && steer.runId == "r-c1")
        #expect(steer.input.contains(toRunning.id) && steer.input.contains("Also add tests."))
        #expect(steer.onDisk?.followUps.first?.lane == .delivered)
        // The run that got it is the one c1's parent now awaits.
        #expect(await fixture.coordinator.file.children[0].awaitedRunId == "r-c1")

        let toStopped = try #require(await fixture.followUp("c2", "Rebase on main.").followUp)
        #expect(toStopped.lane == .held)
        #expect(fixture.host.handed.count == 1)
        #expect(await fixture.coordinator.heldItems(for: "c2").map(\.kind) == [.followUp])
        let release = await fixture.coordinator.send("Thanks.", in: "c2")
        #expect(release == .released(runId: "run-1", itemIds: [toStopped.id]))
        let run = try #require(fixture.host.handed.last)
        let followUp = try #require(await fixture.coordinator.file.followUps.first { $0.id == toStopped.id })
        let report = ChildWorktree.reportFile(worktreePath: base.appendingPathComponent("worktrees/c2").path)
        #expect(run.sessionId == "c2" && run.input == DelegationCoordinator.text(of: followUp, reportFile: report) + "\n\nThanks.")
        #expect(followUp.lane == .delivered && followUp.receipt?.runId == "run-1")
        #expect(await fixture.coordinator.file.children[1].awaitedRunId == "run-1")
        fixture.expectEachTakenOnce([toRunning.id, toStopped.id])
    }

    @Test func heldRowsComeFirstInThePhonesQueuedArrayInItsItemShapeCannotBeRemovedAndKeepRunNextOnOffer() throws {
        let held = [DelegationDeliveryItem(id: "n-1", kind: .notice, childId: "c1", sessionId: "parent", text: "Notice 1"),
                    DelegationDeliveryItem(id: "n-2", kind: .notice, childId: "c2", sessionId: "parent", text: "Notice 2")]
        let queued = [QueuedInput(id: "q-1", text: "Human row")]
        let items = DelegationQueueRows.mobileItems(held: held, queued: queued)
        #expect(items == [MobileQueuedItem(id: "n-1", text: "Notice 1"), MobileQueuedItem(id: "n-2", text: "Notice 2"), MobileQueuedItem(id: "q-1", text: "Human row")])
        // The existing item shape, nothing more: the phone shows them as it is.
        let wire = try #require(try JSONSerialization.jsonObject(with: JSONEncoder().encode(items[0])) as? [String: Any])
        #expect(Set(wire.keys) == ["id", "text"])
        #expect(DelegationQueueRows.mobileItems(held: [], queued: queued) == [MobileQueuedItem(id: "q-1", text: "Human row")])

        // The remove route refuses a held row with exactly held_not_removable; a human row may go.
        #expect(DelegationQueueRows.removeRefusal(itemId: "n-2", held: held) == .heldNotRemovable)
        #expect(DelegationQueueRows.removeRefusal(itemId: "n-2", held: held)?.rawValue == "held_not_removable")
        #expect(DelegationQueueRows.removeRefusal(itemId: "q-1", held: held) == nil)
        #expect(DelegationQueueRows.removeRefusal(itemId: "n-2", held: []) == nil)

        // Run next shows whenever items are held, busy or not; otherwise only while idle.
        #expect(DelegationQueueRows.offersRunNext(busy: true, held: 1))
        #expect(DelegationQueueRows.offersRunNext(busy: false, held: 2))
        #expect(DelegationQueueRows.offersRunNext(busy: false, held: 0))
        #expect(!DelegationQueueRows.offersRunNext(busy: true, held: 0))
    }

    @Test func theHeldRowsTheAppShowsFollowTheFileAcrossARelaunch() async throws {
        let fixture = try Fixture.make(parent: .idle)
        defer { fixture.remove() }
        var rows = fixture.coordinator.heldRows.makeAsyncIterator()
        #expect(await rows.next() == [:])
        let first = try await fixture.end("c1")
        let second = try await fixture.end("c2")
        let expected = [first, second].map { DelegationDeliveryItem(id: $0.id, kind: .notice, childId: $0.childId, sessionId: "parent", text: DelegationCoordinator.text(of: $0)) }
        #expect(await rows.next() == ["parent": expected])
        #expect(await fixture.coordinator.heldItems(for: "parent") == expected)

        // Rebuilt from the same profile, the app's first map has them.
        let relaunched = try Fixture.coordinator(fixture.store, fixture.host, fixture.base)
        var relaunchedRows = relaunched.heldRows.makeAsyncIterator()
        #expect(await relaunchedRows.next() == ["parent": expected])
        // Released, they leave the queued list.
        #expect(await relaunched.send("Go on.", in: "parent") == .released(runId: "run-1", itemIds: [first.id, second.id]))
        #expect(await relaunchedRows.next() == [:])
    }

    @Test func theAppsSendStartsTheReleaseRunItselfAndRunNextSteersHeldItemsIntoARunningClaudeRun() async throws {
        let fixture = try Fixture.make(parent: .idle)
        defer { fixture.remove() }
        let first = try await fixture.end("c1")
        let second = try await fixture.end("c2")
        // The app starts the run with the human's attachments: the whole input reaches its starter once.
        let started = StartedInputs()
        let release = await fixture.coordinator.send("Merge what is ready.", in: "parent") { input in started.add(input); return "app-run-1" }
        #expect(release == .released(runId: "app-run-1", itemIds: [first.id, second.id]))
        #expect(started.inputs == [[DelegationCoordinator.text(of: first), DelegationCoordinator.text(of: second), "Merge what is ready."].joined(separator: "\n\n")])
        #expect(fixture.host.handed.isEmpty)
        let saved = try fixture.store.load().notices
        #expect(saved.map(\.lane) == [.delivered, .delivered] && saved.map { $0.receipt?.runId } == ["app-run-1", "app-run-1"])
        #expect(saved.map { $0.receipt?.route } == [.queue, .queue])

        // A starter that starts nothing leaves them held, as they were.
        let third = try await fixture.end("c3")
        #expect(await fixture.coordinator.send("Again.", in: "parent") { _ in nil } == .notStarted)
        #expect(try fixture.saved(third) == fixture.held(third))
        #expect(await fixture.coordinator.heldItems(for: "parent").map(\.id) == [third.id])

        // Run next while the parent runs Claude: steered into that run, nothing else.
        fixture.host.set("parent", .running, runId: "p-run")
        #expect(await fixture.coordinator.runNext(in: "parent") == .released(runId: "p-run", itemIds: [third.id]))
        let steer = try #require(fixture.host.handed.last)
        #expect(fixture.host.handed.count == 1 && steer.route == .steer && steer.runId == "p-run" && steer.input == DelegationCoordinator.text(of: third))
        #expect(steer.onDisk?.notices.first { $0.id == third.id }?.lane == .delivered)
        let steered = try #require(try fixture.saved(third))
        #expect(steered.lane == .delivered && steered.receipt?.route == .steer && steered.receipt?.runId == "p-run")
        #expect(await fixture.coordinator.runNext(in: "parent") == .nothingHeld)
        fixture.expectEachTakenOnce([third.id])
    }

    @Test func aNoticeARunCouldNotTakeWhileStartingOrEndingGoesOnWhenTheAppOffersItAgain() async throws {
        let fixture = try Fixture.make(parent: .running)
        defer { fixture.remove() }
        // The parent's run is still starting: both steers are refused and the notice stays pending.
        fixture.host.refuseNext([.running, .running])
        let first = try await fixture.end("c1")
        #expect(try fixture.saved(first) == first)
        #expect(await fixture.coordinator.heldItems(for: "parent").isEmpty)
        // Once the run is up the app offers it again: steered into that run.
        await fixture.coordinator.deliverPending()
        let steer = try #require(fixture.host.taken.last)
        #expect(steer.route == .steer && steer.runId == "p-run" && steer.input == DelegationCoordinator.text(of: first))
        #expect(try fixture.saved(first)?.receipt?.route == .steer && fixture.saved(first)?.lane == .delivered)

        // A run that ends normally while its notice is offered: the run end offers it again, as the next run.
        fixture.host.refuseNext([.finished, .finished])
        let second = try await fixture.end("c2")
        #expect(try fixture.saved(second) == second)
        await fixture.coordinator.deliverPending()
        let next = try #require(fixture.host.taken.last)
        #expect(next.route == .queue && next.runId == "run-1" && next.input == DelegationCoordinator.text(of: second))
        #expect(try fixture.saved(second)?.receipt?.runId == "run-1")
        fixture.expectEachTakenOnce([first.id, second.id])
    }
}

/// What a release's starter got, in order.
private final class StartedInputs: @unchecked Sendable {
    private let lock = NSLock()
    private var log: [String] = []
    var inputs: [String] { lock.withLock { log } }
    func add(_ input: String) { lock.withLock { log.append(input) } }
}
