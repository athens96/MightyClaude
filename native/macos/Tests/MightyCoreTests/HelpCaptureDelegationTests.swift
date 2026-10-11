import Foundation
import Testing
@testable import MightyCore

/// A host with no pane open: loading a delegation file reaches no pane.
private final class NoPanesHost: DelegationHost, @unchecked Sendable {
    func createPane(_ pane: DelegationChildPane) async -> Bool { false }
    func startRun(sessionId: String, input: String) async -> String? { nil }
    func deliver(_ input: String, to sessionId: String, route: DeliveryRoute) async -> String? { nil }
    func paneState(sessionId: String) async -> DelegationPaneState? { nil }
    func stopRun(sessionId: String) async {}
}

/// The help capture's agent-delegation demo (the delegation shots): the records it
/// loads into the app's coordinator, and what the sidebar, the children's cards and
/// the parent's queued list then show, checked without launching the app.
struct HelpCaptureDelegationTests {
    static let parentId = "parent-pane"
    static let closedParentId = "closed-parent-pane"
    static let checkout = "/projects/shop-api"
    static let worktrees = ChildWorktreeMaker(root: URL(fileURLWithPath: "/worktrees", isDirectory: true))

    /// The demo's records, with stand-in session ids for its parents.
    static func file(_ profile: DemoProfile) throws -> DelegationFile {
        let demo = profile.delegation
        var parents: [String: String] = [:]
        for pane in profile.panes where pane.workspace == demo.workspace && demo.child(pane.role) == nil { parents[pane.role] = parentId }
        for role in demo.closedParents { parents[role] = closedParentId }
        return try HelpDemoDelegation.file(profile, parents: parents, worktrees: worktrees, checkout: checkout, now: Date(timeIntervalSince1970: 1_800_000_000))
    }

    /// The demo's panes as the capture opens them, replayed: the parent, then each
    /// child linked to it or to the closed parent.
    static func panes(_ profile: DemoProfile) -> [(session: RunSession, events: [RunEvent])] {
        let demo = profile.delegation
        return profile.panes.filter { $0.workspace == demo.workspace }.map { pane in
            let child = demo.child(pane.role)
            let replayed = HelpCaptureTests.replay(pane, id: child?.id ?? parentId)
            var session = replayed.session
            session.workspaceId = demo.workspace
            session.parentSessionId = child.map { demo.closedParents.contains($0.parent) ? closedParentId : parentId }
            return (session, replayed.events)
        }
    }

    /// The tool calls named `tool` in a run's frames, with their inputs.
    static func toolInputs(_ run: DemoProfile.Run, named tool: String) throws -> [[String: Any]] {
        try run.frames.flatMap { frame -> [[String: Any]] in
            let object = try #require(try JSONSerialization.jsonObject(with: Data(frame.utf8)) as? [String: Any])
            let content = (object["message"] as? [String: Any])?["content"] as? [[String: Any]] ?? []
            return content.filter { $0["type"] as? String == "tool_use" && $0["name"] as? String == tool }.compactMap { $0["input"] as? [String: Any] }
        }
    }

    @Test func theDelegationShotsHaveTheirOwnSectionAndComeLast() throws {
        let list = try HelpShotList.decode(Data(contentsOf: HelpCaptureTests.shotsURL))
        let approvals = try #require(list.sections.firstIndex(of: "approvals"))
        #expect(list.sections[approvals + 1] == "delegation")
        let shots = list.shots.filter { $0.section == "delegation" }
        #expect(shots.map(\.id) == ["delegation-tree", "delegation-held", "delegation-parent-closed"])
        #expect(shots.map(\.screen) == [.delegationTree, .delegationHeld, .delegationParentClosed])
        #expect(shots.allSatisfy { $0.window == HelpShotSize(width: 1440, height: 900) && $0.themes == ["light", "dark"] })
        // Their demo is seeded when the first of them is taken, so no other shot comes after them.
        #expect(list.shots.suffix(shots.count).map(\.id) == shots.map(\.id))
        let raw = try #require(try JSONSerialization.jsonObject(with: Data(contentsOf: HelpCaptureTests.shotsURL)) as? [String: Any])
        let notes = try #require(raw["shots"] as? [[String: Any]]).filter { $0["section"] as? String == "delegation" }.map { $0["note"] as? String ?? "" }
        #expect(notes.count == 3 && notes.allSatisfy { !$0.isEmpty })
        #expect(HelpScreen.delegationTree.paneRole == "parent" && HelpScreen.delegationHeld.paneRole == "parent")
        #expect(HelpScreen.delegationParentClosed.paneRole == "child-of-closed")
    }

    @Test(arguments: HelpCaptureTests.languages)
    func theProfileCarriesTheDelegationDemo(language: String) throws {
        let profile = try HelpCaptureTests.profile(language)
        let demo = profile.delegation
        let workspace = try #require(profile.workspaces.first { $0.id == demo.workspace })
        #expect(workspace.name == "shop-api" && demo.workspace != profile.activeWorkspace)
        let panes = profile.panes.filter { $0.workspace == demo.workspace }
        #expect(panes.map(\.role) == ["parent", "child-merged", "child-reported", "child-running", "child-waiting", "child-of-closed"])
        #expect(panes.allSatisfy { $0.kind == "claude" && $0.provider == "claude" && $0.view == "default" && $0.permissionMode == "acceptEdits" })
        #expect(demo.children.map(\.role) == Array(panes.map(\.role).dropFirst()))
        #expect(demo.children.map(\.state) == [.merged, .reported, .running, .running, .reported])
        #expect(demo.children.map(\.parent) == ["parent", "parent", "parent", "parent", "closed-parent"])
        #expect(demo.closedParents == ["closed-parent"] && profile.pane("closed-parent") == nil)
        for child in demo.children {
            #expect(UUID(uuidString: child.id) != nil && child.id == child.id.lowercased(), "\(child.role)")
            // Its first run carries its task; the pane's title is its own.
            let pane = try #require(profile.pane(child.role))
            #expect(!child.task.isEmpty && pane.runs.first?.input == child.task && !pane.title.isEmpty, "\(child.role)")
        }
        #expect(demo.notices.allSatisfy { UUID(uuidString: $0.id) != nil })
        let layout = try #require(profile.layouts.first { $0.workspace == demo.workspace })
        #expect(layout.preset == "tabs" && layout.active == "parent")
    }

    @Test(arguments: HelpCaptureTests.languages)
    func theSidebarListsFourChildrenUnderTheParentAndOneUnderTheClosedParent(language: String) throws {
        let profile = try HelpCaptureTests.profile(language)
        let demo = profile.delegation
        let file = try Self.file(profile)
        let panes = Self.panes(profile)
        let rows = DelegationSidebar.rows(file)
        #expect(rows.map(\.id) == demo.children.map(\.id))
        let workspaces = [Workspace(id: demo.workspace, name: "shop-api", path: Self.checkout)]
        let tree = DelegationSidebar.tree(workspaceId: demo.workspace, workspaces: workspaces, sessions: panes.map(\.session), children: rows)
        #expect(tree.top.map(\.id) == [Self.parentId])
        let children = try #require(tree.children[Self.parentId])
        #expect(children.map(\.id) == demo.children.prefix(4).map(\.id))
        // The waiting child's run asks a permission only a human can answer, so its row reads needs you.
        func pending(_ id: String) -> ToolPermissionRequest? {
            panes.first { $0.session.id == id }?.events.compactMap(\.permission).last.flatMap { $0.state == "pending" ? $0 : nil }
        }
        #expect(children.map { DelegationSidebar.shownState($0.state, asksHuman: pending($0.id) != nil) } == [.merged, .reported, .running, .waiting])
        let migration = try #require(pending(demo.children[3].id))
        #expect(migration.canAllow && migration.toolName == "Bash" && migration.summary == "npm run db:migrate")
        #expect(panes.first { $0.session.id == demo.children[2].id }?.session.status == "running")
        // Merged offers undo, reported offers merge, and every child offers discard.
        #expect(children.map(\.cardActions) == [[.undo, .discard], [.merge, .discard], [.discard], [.discard]])
        let merge = try #require(file.merges.first)
        #expect(file.merges.count == 1 && file.undoableMerge(of: demo.children[0].id) == merge)
        #expect(merge.kind == .toolFastForward && merge.preMergeCommit == demo.baseCommit && merge.childHead == demo.children[0].reportHead && merge.mergedCommit == merge.childHead)
        #expect(children.map(\.task) == demo.children.prefix(4).map { PaneTitle.shortened($0.task) })
        // The closed parent's child, under its own node, counts its held notice.
        let node = try #require(tree.parentClosed.first)
        #expect(tree.parentClosed.count == 1 && node.id == Self.closedParentId && node.parentBranch == demo.parentBranch)
        #expect(node.children.map(\.id) == [demo.children[4].id])
        #expect(node.children.first?.heldNotices == 1 && node.children.first?.cardActions == [.merge, .discard])
        #expect(panes.allSatisfy { $0.session.status != "error" && !$0.events.contains { $0.entry?.kind == "error" } })
    }

    @Test(arguments: HelpCaptureTests.languages)
    func theStoppedParentHoldsTwoNoticesAndItsTranscriptShowsTheDeliveredOne(language: String) throws {
        let profile = try HelpCaptureTests.profile(language)
        let demo = profile.delegation
        let file = try Self.file(profile)
        // Held for the stopped parent, oldest first: the reported child's and the
        // waiting child's, which cannot be removed and keep run next on offer.
        let held = try #require(file.heldItemsByPane()[Self.parentId])
        #expect(held.map(\.childId) == [demo.children[1].id, demo.children[3].id])
        #expect(held.allSatisfy { $0.kind == .notice && $0.text.hasPrefix("[Mighty Claude notice \($0.id)] Your child \($0.childId)") })
        #expect(DelegationQueueRows.offersRunNext(busy: false, held: held.count) && DelegationQueueRows.removeRefusal(itemId: held[0].id, held: held) == .heldNotRemovable)
        #expect(file.heldItemsByPane()[Self.closedParentId]?.map(\.childId) == [demo.children[4].id])
        // The parent delegated the four tasks, the delivered notice started its second run, and a Stop ended that run.
        let pane = try #require(profile.pane("parent"))
        let (session, events) = HelpCaptureTests.replay(pane, id: Self.parentId)
        #expect(session.status == "stopped" && pane.runs.map(\.finish) == ["completed", "stopped"])
        let tasks = try Self.toolInputs(pane.runs[0], named: "mcp__mighty-delegation__delegate").map { $0["task"] as? String }
        #expect(tasks == demo.children.prefix(4).map(\.task))
        let delivered = try #require(file.notices.first { $0.lane == .delivered })
        #expect(delivered.childId == demo.children[0].id && delivered.reportRevision == 1 && delivered.receipt?.route == .queue)
        let notice = DelegationCoordinator.text(of: delivered)
        #expect(pane.runs[1].input == notice && session.logs.contains { $0.kind == "user" && $0.text == notice })
        #expect(try Self.toolInputs(pane.runs[1], named: "mcp__mighty-delegation__merge").first?["expected_head"] as? String == demo.children[0].reportHead)
        // Each tool row: four delegate calls, child_status, merge, and the test run the Stop ended.
        let tools = Dictionary(grouping: events.compactMap(\.activity).filter { $0.kind != "turn" }, by: \.id).compactMap { $0.value.last }
        #expect(tools.filter { $0.toolName == "mcp__mighty-delegation__delegate" }.count == 4)
        #expect(tools.contains { $0.toolName == "Bash" && $0.state == "stopped" })
    }

    @Test func theCoordinatorLoadsTheRecordsAsTheSidebarAndTheQueuedListsReadThem() async throws {
        let profile = try HelpCaptureTests.profile("en")
        let demo = profile.delegation
        let file = try Self.file(profile)
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-help-delegation-" + UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        let store = DelegationFileStore(directory: folder)
        try store.save(file)
        let coordinator = try DelegationCoordinator(store: store, host: NoPanesHost(), worktrees: Self.worktrees, isSwitchOn: { false })
        // Nothing was pending, so loading holds nothing more and the file stays as written.
        #expect(await coordinator.file == file)
        var rows = coordinator.childRows.makeAsyncIterator()
        #expect(await rows.next() == DelegationSidebar.rows(file))
        var held = coordinator.heldRows.makeAsyncIterator()
        let heldIds = await held.next()?.mapValues { $0.map(\.id) }
        let expected = demo.notices.filter { $0.lane == .held }.map(\.id)
        #expect(heldIds == [Self.parentId: Array(expected.prefix(2)), Self.closedParentId: [expected[2]]])
    }

    @Test func aDemoWhoseRecordsNameWhatItLacksIsRefused() throws {
        let data = try Data(contentsOf: HelpCaptureTests.profileURL)
        func load(_ change: (inout [String: Any]) -> Void) throws {
            var root = try #require(try JSONSerialization.jsonObject(with: data) as? [String: Any])
            var demo = try #require(root["delegation"] as? [String: Any])
            change(&demo)
            root["delegation"] = demo
            _ = try DemoProfile.load(JSONSerialization.data(withJSONObject: root), language: "en")
        }
        func child(_ index: Int, _ change: @escaping (inout [String: Any]) -> Void) -> (inout [String: Any]) -> Void {
            { demo in
                var children = demo["children"] as? [[String: Any]] ?? []
                change(&children[index])
                demo["children"] = children
            }
        }
        try load { _ in }
        // The active workspace is seeded first; the demo needs its own.
        #expect(throws: (any Error).self) { try load { $0["workspace"] = "demo-my-app" } }
        #expect(throws: (any Error).self) { try load(child(0) { $0["parent"] = "nobody" }) }
        #expect(throws: (any Error).self) { try load(child(0) { $0["role"] = "mighty" }) }
        #expect(throws: (any Error).self) { try load(child(1) { $0.removeValue(forKey: "reportHead") }) }
        #expect(throws: (any Error).self) { try load(child(2) { $0["id"] = "../escape" }) }
        #expect(throws: (any Error).self) { try load(child(2) { $0["state"] = "napping" }) }
        #expect(throws: (any Error).self) {
            try load { demo in
                var notices = demo["notices"] as? [[String: Any]] ?? []
                notices[2]["revision"] = 3
                demo["notices"] = notices
            }
        }
    }
}
