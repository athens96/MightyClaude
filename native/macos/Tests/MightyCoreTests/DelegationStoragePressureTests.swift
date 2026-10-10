import Foundation
import Testing
@testable import MightyCore

/// The panes open now, as the coordinator asks the app for them.
private struct OpenPanes: DelegationHost {
    let open: Set<String>
    func createPane(_ pane: DelegationChildPane) async -> Bool { false }
    func startRun(sessionId: String, input: String) async -> String? { nil }
    func deliver(_ input: String, to sessionId: String, route: DeliveryRoute) async -> String? { nil }
    func paneState(sessionId: String) async -> DelegationPaneState? {
        open.contains(sessionId) ? DelegationPaneState(sessionId: sessionId, permissionMode: "auto", folder: "/tmp/w") : nil
    }
    func stopRun(sessionId: String) async {}
}

/// Delegation under storage pressure (macOS only): a nearly full
/// workspace-state.json beside the delegation file, the delegation file near
/// its own 4 MiB cap, and reports longer than a 64 KiB copy.
@Suite(.delegationLane) struct DelegationStoragePressureTests {
    private static let stateBytes = 7 * 1024 * 1024 + 512 * 1024
    private let cap = DelegationFileStore.maximumFileBytes
    private let fullCopy = DelegationFileStore.maximumCopyBytes

    private func makeProfile() throws -> URL {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("DelegationStoragePressureTests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        return directory
    }

    /// A coordinator reading the profile's delegation file, as at launch.
    private func relaunch(_ profile: URL, open: Set<String> = []) throws -> DelegationCoordinator {
        let unused = ChildWorktreeMaker(root: profile.appendingPathComponent("unused-worktrees", isDirectory: true), freeBytes: { _ in nil })
        return try DelegationCoordinator(store: DelegationFileStore(directory: profile), host: OpenPanes(open: open), worktrees: unused, isSwitchOn: { true })
    }

    private func child(_ id: String, parent: String, state: ChildState) -> ChildRecord {
        ChildRecord(id: id, parentSessionId: parent, worktreePath: "/tmp/worktrees/\(id)", parentBranch: "main", baseCommit: String(repeating: "a", count: 40),
                    startingMode: "acceptEdits", requestKey: "key-\(id)", state: state)
    }

    private func report(_ id: String, bytes: Int = DelegationFileStore.maximumCopyBytes) -> DelegationCopy {
        DelegationCopy(childId: id, kind: .report, revision: 1, contents: Data(String(repeating: "r", count: bytes).utf8))
    }

    private func notice(_ id: String, _ lane: DeliveryLane) -> Notice { Notice(id: "n-\(id)", childId: id, reportRevision: 1, kind: .reported, lane: lane) }

    private func merge(_ id: String) -> MergeRecord {
        MergeRecord(childId: id, kind: .cardMergeCommit, parentBranch: "main", preMergeCommit: String(repeating: "a", count: 40), mergedCommit: "m-\(id)", childHead: "h-\(id)")
    }

    private func size(_ file: DelegationFile) throws -> Int { try DelegationFileStore.encode(file).count }

    /// `file` plus open children, three to a pane, each with a full report
    /// copy, until one more would leave less than `room` bytes under the cap.
    private func filled(_ file: DelegationFile, leavingAtLeast room: Int) throws -> DelegationFile {
        var file = file, index = 0
        var bytes = try size(file)
        while true {
            let id = String(format: "open%03d", index)
            let record = child(id, parent: String(format: "pane%03d", index / 3), state: .running), copy = report(id)
            let more = try DelegationFile.elementBytes(record) + DelegationFile.elementBytes(copy)
            guard bytes + more + room <= cap else { return file }
            file.children.append(record); file.setCopy(copy); bytes += more; index += 1
        }
    }

    /// `file` without anything pruning may drop: the report copies of closed
    /// records, and closed records whose items are all delivered (a held notice
    /// of a closed parent counts) and whose merge cannot be undone.
    private func everythingPrunableDropped(_ file: DelegationFile, _ context: DelegationPruneContext) -> DelegationFile {
        var file = file
        let closed = file.children.filter { $0.state == .closed || $0.state == .discarded }
        file.copies.removeAll { copy in copy.kind == .report && closed.contains { $0.id == copy.childId } }
        let gone = Set(closed.filter { record in
            file.notices.allSatisfy { $0.childId != record.id || $0.lane == .delivered || ($0.lane == .held && !context.openPaneIds.contains(record.parentSessionId)) }
                && file.followUps.allSatisfy { $0.childId != record.id || $0.lane == .delivered }
                && !file.merges.contains { $0.childId == record.id && context.canUndo($0) }
        }.map(\.id))
        file.children.removeAll { gone.contains($0.id) }; file.notices.removeAll { gone.contains($0.childId) }
        file.followUps.removeAll { gone.contains($0.childId) }; file.merges.removeAll { gone.contains($0.childId) }
        file.copies.removeAll { gone.contains($0.childId) }
        return file
    }

    /// A workspace-state.json of exactly 7.5 MiB as the app's encoder writes
    /// it: a parent pane, a child pane (with its two keys when `linked`) used
    /// last, and an old pane's long history that fills the rest.
    private func nearlyFullState(_ workspace: Workspace, child childId: String, folder: String, linked: Bool) throws -> Data {
        let parent = RunSession(id: "parent", workspaceId: workspace.id, title: "Claude", createdAt: "2026-10-09T00:00:00Z")
        var pane = RunSession(id: childId, workspaceId: workspace.id, title: "Claude", model: "claude-opus-5", settings: RunSettings(effort: "high", permissionMode: "acceptEdits"),
                              logs: [LogEntry(id: "c-1", kind: "user", text: "고쳐 줘", timestamp: "2026-10-09T10:00:00Z")], createdAt: "2026-10-09T00:00:01Z")
        if linked { pane.parentSessionId = "parent"; pane.workingFolder = folder }
        var history = RunSession(id: "history", workspaceId: workspace.id, title: "Claude", createdAt: "2026-10-01T00:00:00Z")
        history.logs = (0 ..< 62).map { LogEntry(id: "h\($0)", kind: "assistant", text: String(repeating: "x", count: 120 * 1024), timestamp: "2026-10-01T00:00:00Z") }
        var snapshot = AppSnapshot(workspaces: [workspace], sessions: [parent, pane, history], activeWorkspaceId: workspace.id)
        // ASCII text encodes byte for byte, so the last entry takes up the difference.
        let short = try Self.stateBytes - JSONEncoder().encode(snapshot).count
        try #require(short > 0)
        snapshot.sessions[2].logs[61].text += String(repeating: "x", count: short)
        let data = try JSONEncoder().encode(snapshot)
        try #require(data.count == Self.stateBytes)
        return data
    }

    @Test func delegationRecordsSaveAndRestoreBesideAStateFileFilledTo7Point5MiB() async throws {
        let profile = try makeProfile(); defer { try? FileManager.default.removeItem(at: profile) }
        let workspace = Workspace(id: "w", name: "W", path: profile.path)
        let worktree = profile.appendingPathComponent("worktrees/c1", isDirectory: true).path
        let state = try nearlyFullState(workspace, child: "c1", folder: worktree, linked: true)
        #expect(state.count == Self.stateBytes); #expect(state.count < StateRepository.maximumStateBytes)
        let stateURL = profile.appendingPathComponent("workspace-state.json")
        try state.write(to: stateURL)

        // Every kind of record, then open children up to near the 4 MiB cap.
        var c1 = child("c1", parent: "parent", state: .running)
        c1.worktreePath = worktree; c1.recordReport(head: "b2")
        let receipt = DeliveryReceipt(time: "2026-10-09T01:02:03Z", route: .steer, runId: "run-7")
        var records = DelegationFile(
            children: [c1, child("c2", parent: "parent", state: .merged)],
            notices: [Notice(id: "n1", childId: "c1", reportRevision: 1, kind: .reported, lane: .held), Notice(id: "n2", childId: "c2", reportRevision: 1, kind: .reported, lane: .delivered, receipt: receipt)],
            followUps: [FollowUp(id: "f1", childId: "c1", text: "테스트도 추가해 줘", lane: .held)],
            merges: [merge("c2")])
        records.setCopy(DelegationCopy(childId: "c1", kind: .task, revision: 0, contents: Data("# 할 일\n파서를 고쳐라".utf8)))
        records.setCopy(DelegationCopy(childId: "c1", kind: .report, revision: 1, contents: Data(String(repeating: "보고서 ", count: 20_000).utf8)))
        let file = try filled(records, leavingAtLeast: 1)
        let open = Set(["parent"] + file.children.map(\.parentSessionId))
        let context = DelegationPruneContext(openPaneIds: open) { _ in true }

        let store = DelegationFileStore(directory: profile)
        #expect(try store.save(file, pruning: context) == file)
        let delegationBytes = try Data(contentsOf: store.fileURL).count, childRoom = try DelegationFile.newChildRoom()
        #expect(delegationBytes > cap - childRoom); #expect(delegationBytes <= cap)
        // Together more than one capped state file could ever hold, and the state file is untouched.
        #expect(state.count + delegationBytes > StateRepository.maximumStateBytes)
        #expect(try Data(contentsOf: stateURL) == state)

        // A relaunch restores both: the snapshot's two keys and every record.
        let repository = StateRepository(directory: profile, legacyStateURL: nil)
        let snapshot = try await repository.load()
        let pane = try #require(snapshot.sessions.first { $0.id == "c1" })
        #expect(pane.parentSessionId == "parent"); #expect(pane.workingFolder == worktree)
        let coordinator = try relaunch(profile, open: open)
        let restored = await coordinator.file
        #expect(restored == file)
        let record = try #require(restored.children.first { $0.id == "c1" })
        #expect(record.parentSessionId == pane.parentSessionId); #expect(record.worktreePath == pane.workingFolder)
        #expect(record.state == .reported); #expect(record.reportHead == "b2")
        #expect(restored.notices.map(\.lane) == [.held, .delivered]); #expect(restored.followUps.map(\.lane) == [.held])
        #expect(restored.merges == [merge("c2")]); #expect(restored.copy(childId: "c1", kind: .report)?.truncated == true)

        // The app saves its state again: only the two keys are in it, and the delegation file stays as it was.
        let delegationData = try Data(contentsOf: store.fileURL)
        try await repository.save(snapshot)
        let resaved = try Data(contentsOf: stateURL)
        #expect(resaved.count <= StateRepository.maximumStateBytes)
        let text = String(decoding: resaved, as: UTF8.self)
        for key in ["worktreePath", "requestKey", "reportRevision", "startingMode", "followUps", "mergedCommit", "truncated at 64 KiB", "보고서"] { #expect(!text.contains(key)) }
        #expect(try Data(contentsOf: store.fileURL) == delegationData)
        let again = try await StateRepository(directory: profile, legacyStateURL: nil).load()
        #expect(again.sessions.first { $0.id == "c1" }?.parentSessionId == "parent")
        #expect(try await relaunch(profile, open: open).file == file)
    }

    @Test func nearTheCapPruningFreesRoomBeforeAnyStoreFullAndNeverDropsWhatMustStay() throws {
        let profile = try makeProfile(); defer { try? FileManager.default.removeItem(at: profile) }
        let store = DelegationFileStore(directory: profile)
        // Closed records pruning may drop, oldest first: delivered ones of the open
        // pane "keeper", and ones of the closed pane "gone" whose notice is still held.
        var file = DelegationFile()
        for index in 0 ..< 6 {
            let id = "old-\(index)", parent = index.isMultiple(of: 2) ? "keeper" : "gone"
            file.children.append(child(id, parent: parent, state: index == 5 ? .discarded : .closed))
            file.notices.append(notice(id, parent == "gone" ? .held : .delivered))
            file.setCopy(DelegationCopy(childId: id, kind: .task, revision: 0, contents: Data("일 \(index)".utf8)))
            file.setCopy(report(id))
        }
        file.merges.append(merge("old-0"))
        // What must stay: keeper's three open children (one waiting on a human, one
        // reported with its notice held), a closed child whose notice is held for
        // keeper, one with a held follow-up, and one whose merge can still be undone.
        file.children += [child("open-1", parent: "keeper", state: .running), child("open-2", parent: "keeper", state: .waiting), child("open-3", parent: "keeper", state: .reported),
                          child("held-notice", parent: "keeper", state: .closed), child("held-follow-up", parent: "keeper", state: .closed), child("undoable", parent: "keeper", state: .closed)]
        file.notices += [notice("open-3", .held), notice("held-notice", .held), notice("held-follow-up", .delivered), notice("undoable", .delivered)]
        file.followUps.append(FollowUp(id: "f-held", childId: "held-follow-up", text: "마저 해 줘", lane: .held))
        file.merges.append(merge("undoable"))
        for id in ["open-1", "open-2", "open-3", "held-notice", "undoable"] { file.setCopy(report(id)) }
        file = try filled(file, leavingAtLeast: 10 * 1024)
        let childRoom = try DelegationFile.newChildRoom()
        #expect(try cap - size(file) < childRoom)

        let panes = Set(file.children.map(\.parentSessionId)).subtracting(["gone"])
        let context = DelegationPruneContext(openPaneIds: panes) { $0.childId == "undoable" }
        let mustStay = file.children.filter(\.state.isOpen).map(\.id) + ["held-notice", "held-follow-up", "undoable"]
        func expectKept(_ file: DelegationFile, also added: [String]) {
            for id in mustStay + added { #expect(file.children.contains { $0.id == id }, "\(id) was pruned") }
            for record in file.children where record.state.isOpen { #expect(file.copy(childId: record.id, kind: .report) != nil, "\(record.id) lost its report copy") }
            #expect(file.notices.contains(notice("open-3", .held))); #expect(file.notices.contains(notice("held-notice", .held)))
            #expect(file.followUps.map(\.id) == ["f-held"]); #expect(file.merges.contains(merge("undoable")))
        }

        // The first save already prunes so that one more child fits.
        var current = try store.save(file, pruning: context)
        #expect(current != file); expectKept(current, also: [])
        #expect(try size(current) + childRoom <= cap)
        var added: [String] = [], refusal: DelegationAdmission?
        for index in 0 ..< 40 {
            let id = String(format: "new%02d", index)
            let newChild = child(id, parent: "asker\(index)", state: .creating), task = DelegationCopy(childId: id, kind: .task, revision: 0, contents: Data("새 일 \(index)".utf8))
            let admission = try current.admitting(newChild, task: task, context: context)
            guard case .admitted(var next) = admission else {
                // store_full only when even dropping everything pruning may drop leaves no
                // room for the child and its report copy; the refusal changes nothing.
                refusal = admission
                var bare = everythingPrunableDropped(current, context)
                #expect(try current.pruned(leaving: cap - size(bare), context: context) == bare)
                expectKept(bare, also: added)
                bare.children.append(newChild); bare.setCopy(task)
                #expect(try size(bare) + DelegationFile.reportCopyRoom(childId: id) > cap)
                #expect(try store.load() == current)
                break
            }
            // The child starts and reports more than 64 KiB, kept as a cut copy.
            let position = try #require(next.children.firstIndex { $0.id == id })
            let started = next.children[position].apply(.startRun), reported = next.children[position].recordReport(head: "h-\(id)")
            #expect(started); #expect(reported)
            next.setCopy(report(id, bytes: 3 * fullCopy))
            added.append(id)
            current = try store.save(next, pruning: context)
            expectKept(current, also: added)
            #expect(try store.load() == current)
        }
        #expect(refusal == .refused(.storeFull))
        // With less than one child's room at the start, pruning made room for several
        // children before the first store_full: every closed report copy went.
        #expect(added.count >= 6)
        #expect(current.copies.allSatisfy { copy in copy.kind == .task || current.children.contains { $0.id == copy.childId && $0.state.isOpen } })
        #expect(try Data(contentsOf: store.fileURL).count <= cap)
    }

    @Test func aReportOver64KiBIsKeptAsAMarkedTruncatedCopyWhileTheWholeFileStays() async throws {
        let profile = try makeProfile(); defer { try? FileManager.default.removeItem(at: profile) }
        let worktree = profile.appendingPathComponent("worktrees/c1", isDirectory: true)
        try FileManager.default.createDirectory(at: worktree, withIntermediateDirectories: true)
        let reportURL = worktree.appendingPathComponent("REPORT.md")
        let body = Data((0 ..< 4_000).map { "\($0). 고친 곳: Sources/Parser.swift — 테스트 통과\n" }.joined().utf8)
        try body.write(to: reportURL)
        #expect(body.count > 3 * fullCopy)

        let copy = try DelegationCopy.read(childId: "c1", kind: .report, revision: 2, from: reportURL)
        #expect(copy.truncated); #expect(copy.originalBytes == body.count)
        #expect(copy.text.hasSuffix(DelegationCopy.truncationMarker)); #expect(copy.text.utf8.count <= fullCopy)
        let kept = String(copy.text.dropLast(DelegationCopy.truncationMarker.count)), whole = String(decoding: body, as: UTF8.self)
        #expect(whole.hasPrefix(kept))
        // Cut by the room it takes in the file, escapes included: one more character would not fit.
        let budget = fullCopy - DelegationCopy.truncationMarker.utf8.count
        #expect(DelegationCopy.storedBytes(kept) <= budget)
        #expect(DelegationCopy.storedBytes(kept + String(whole[whole.index(whole.startIndex, offsetBy: kept.count)])) > budget)

        // A report full of escapes (quotes, backslashes, colour codes) is cut by the
        // room its copy takes in the file, so it never needs more than a child's room.
        let escapes = worktree.appendingPathComponent("ESCAPES.md")
        let noisy = Data((0 ..< 9_000).map { "{\"test\":\"Parser\\\\\($0)\",\"ok\":true} \u{1B}[32mPASS\u{1B}[0m\n" }.joined().utf8)
        try noisy.write(to: escapes)
        let escaped = try DelegationCopy.read(childId: "c1", kind: .report, revision: 2, from: escapes)
        #expect(escaped.truncated); #expect(escaped.originalBytes == noisy.count); #expect(escaped.text.hasSuffix(DelegationCopy.truncationMarker))
        let keptNoisy = String(escaped.text.dropLast(DelegationCopy.truncationMarker.count)), wholeNoisy = String(decoding: noisy, as: UTF8.self)
        #expect(wholeNoisy.hasPrefix(keptNoisy)); #expect(DelegationCopy.storedBytes(keptNoisy) <= budget)
        #expect(DelegationCopy.storedBytes(keptNoisy + String(wholeNoisy[wholeNoisy.index(wholeNoisy.startIndex, offsetBy: keptNoisy.count)])) > budget)
        let copyRoom = try DelegationFile.reportCopyRoom(childId: "c1")
        #expect(try DelegationFile.elementBytes(escaped) <= copyRoom)
        #expect(try DelegationFile.elementBytes(copy) <= copyRoom)

        // With a child's room left (its record and a full report copy), the child is
        // admitted and its cut report saves without pruning and restores marked and cut.
        let store = DelegationFileStore(directory: profile)
        let childRoom = try DelegationFile.newChildRoom()
        let near = try filled(DelegationFile(), leavingAtLeast: childRoom)
        #expect(try cap - size(near) < childRoom + fullCopy + 1_024)
        let context = DelegationPruneContext(openPaneIds: Set(near.children.map(\.parentSessionId) + ["parent"])) { _ in true }
        guard case .admitted(var file) = try near.admitting(child("c1", parent: "parent", state: .creating), context: context) else {
            Issue.record("a file with a child's room left should admit it"); return
        }
        let position = try #require(file.children.firstIndex { $0.id == "c1" })
        file.children[position].apply(.startRun); file.children[position].recordReport(head: "b2"); file.children[position].apply(.startRun); file.children[position].recordReport(head: "b3")
        file.setCopy(copy)
        try store.save(file)
        let restored = try await relaunch(profile).file
        let stored = try #require(restored.copy(childId: "c1", kind: .report))
        #expect(stored == copy); #expect(stored.truncated); #expect(stored.originalBytes == body.count); #expect(stored.revision == 2)
        #expect(stored.text.hasSuffix(DelegationCopy.truncationMarker))
        // The whole report stays in the worktree.
        #expect(try Data(contentsOf: reportURL) == body)
    }

    @Test func anOldStateFileLoadsUnchangedAndLoadingMakesNoDelegationFile() async throws {
        let profile = try makeProfile(); defer { try? FileManager.default.removeItem(at: profile) }
        let workspace = Workspace(id: "w", name: "W", path: profile.path)
        // Written before the keys existed, and nearly full.
        let old = try nearlyFullState(workspace, child: "c1", folder: "/unused", linked: false)
        #expect(!String(decoding: old, as: UTF8.self).contains("parentSessionId"))
        let stateURL = profile.appendingPathComponent("workspace-state.json")
        try old.write(to: stateURL)

        let loaded = try await StateRepository(directory: profile, legacyStateURL: nil).load()
        #expect(loaded == StateRepository.decodeSnapshot(old))
        #expect(loaded.sessions.map(\.id) == ["parent", "c1", "history"])
        #expect(loaded.sessions.allSatisfy { $0.parentSessionId == nil && $0.workingFolder == nil })
        let pane = try #require(loaded.sessions.first { $0.id == "c1" })
        #expect(pane.model == "claude-opus-5"); #expect(pane.settings.permissionMode == "acceptEdits"); #expect(pane.logs.map(\.text) == ["고쳐 줘"])
        // A relaunch with no delegation file starts empty and writes nothing.
        #expect(try await relaunch(profile, open: ["parent", "c1"]).file == DelegationFile())
        #expect(try FileManager.default.contentsOfDirectory(atPath: profile.path) == ["workspace-state.json"])
        #expect(try Data(contentsOf: stateURL) == old)
        let resaved = String(decoding: try JSONEncoder().encode(loaded), as: UTF8.self)
        #expect(!resaved.contains("parentSessionId")); #expect(!resaved.contains("workingFolder"))
    }

    @Test func aNewPaneNeverInheritsAParentLinkOrWorkingFolder() async throws {
        let profile = try makeProfile(); defer { try? FileManager.default.removeItem(at: profile) }
        let workspace = Workspace(id: "w", name: "W", path: profile.path)
        let worktree = profile.appendingPathComponent("worktrees/c1", isDirectory: true).path
        try nearlyFullState(workspace, child: "c1", folder: worktree, linked: true).write(to: profile.appendingPathComponent("workspace-state.json"))
        var record = child("c1", parent: "parent", state: .running)
        record.worktreePath = worktree
        try DelegationFileStore(directory: profile).save(DelegationFile(children: [record]))

        let repository = StateRepository(directory: profile, legacyStateURL: nil)
        var snapshot = try await repository.load()
        // As "Add Pane" does: the child is the Claude pane used last, so it is the template.
        let template = try #require(RunSession.template(kind: SessionKind.claude, provider: "claude", in: snapshot.sessions))
        #expect(template.id == "c1"); #expect(template.parentSessionId == "parent"); #expect(template.workingFolder == worktree)
        var pane = RunSession(workspaceId: workspace.id, title: ProviderOptions.label("claude"), kind: SessionKind.claude, provider: "claude")
        pane.inheritSettings(from: template)
        #expect(pane.model == template.model); #expect(pane.settings == template.settings)
        #expect(pane.parentSessionId == nil); #expect(pane.workingFolder == nil)
        snapshot.sessions.append(pane)
        try await repository.save(snapshot)

        let restored = try await StateRepository(directory: profile, legacyStateURL: nil).load()
        let fresh = try #require(restored.sessions.first { $0.id == pane.id })
        #expect(fresh.parentSessionId == nil); #expect(fresh.workingFolder == nil)
        #expect(restored.sessions.first { $0.id == "c1" }?.parentSessionId == "parent")
        // The delegation file, the authority, knows only the child.
        #expect(try await relaunch(profile, open: ["parent", "c1", pane.id]).file.children.map(\.id) == ["c1"])
    }
}
