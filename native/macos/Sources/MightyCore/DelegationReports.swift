import CryptoKit
import Darwin
import Foundation

/// How a run in a child's pane ended, as the host saw it (macOS only).
public enum ChildRunEnd: String, Sendable, CaseIterable {
    /// It finished normally.
    case finished
    /// A human stopped it.
    case stopped
    /// It ended with an error.
    case errored
    /// Quitting the app killed it.
    case quit
}

/// A child's REPORT.md as a run left it (macOS only): the start its copy
/// keeps, its whole size and the SHA-256 of all of it.
struct ChildReportFile: Sendable, Equatable {
    var start: Data
    var totalBytes: Int
    var digest: String

    /// The report at `path`, or nil when there is none: no regular file
    /// there (a link is not followed), it could not be read, or the part a
    /// copy keeps holds no text. Reads the file in pieces, so a huge one is
    /// never loaded whole.
    static func read(_ path: String) -> ChildReportFile? {
        let descriptor = open(path, O_RDONLY | O_NOFOLLOW | O_NONBLOCK)
        guard descriptor >= 0 else { return nil }
        let handle = FileHandle(fileDescriptor: descriptor, closeOnDealloc: true)
        var info = stat()
        guard fstat(descriptor, &info) == 0, info.st_mode & S_IFMT == S_IFREG else { return nil }
        let keep = DelegationFileStore.maximumCopyBytes + 1
        var hasher = SHA256(), start = Data(), total = 0
        do {
            while let piece = try handle.read(upToCount: 1 << 16), !piece.isEmpty {
                hasher.update(data: piece); total += piece.count
                if start.count < keep { start.append(piece.prefix(keep - start.count)) }
            }
        } catch { return nil }
        guard String(decoding: start, as: UTF8.self).contains(where: { !$0.isWhitespace }) else { return nil }
        return ChildReportFile(start: start, totalBytes: total, digest: hasher.finalize().map { String(format: "%02x", $0) }.joined())
    }
}

extension ChildRecord {
    /// Brings the record to its run `runId` going, as a new run: any new run
    /// sets running and clears reported, and a child's first run is the one
    /// its parent awaits. When the run going had a different id, its end was
    /// never reported; the new one carries on what the parent awaited. False,
    /// changing nothing, when this run's end is recorded already or the child
    /// takes no runs (failed, closed or discarded).
    mutating func noteRun(_ runId: String) -> Bool {
        if self.runId == runId { return state == .running || state == .waiting }
        switch state {
        case .creating:
            guard apply(.startRun) else { return false }
            awaitedRunId = runId
        case .running, .waiting:
            if state == .waiting { apply(.answerPermission) }
            if let last = self.runId, awaitedRunId == last { awaitedRunId = runId }
        default:
            guard apply(.startRun) else { return false }
        }
        self.runId = runId
        return true
    }
}

/// Report revisions (macOS only): the host tells the coordinator each run
/// that starts and ends in a child's pane, in order, and the coordinator
/// records what the run left for the parent. These run events take turns in
/// the order they came, so one never sees another half recorded. Notices are
/// saved pending in the delegation file, and then delivery offers them to the
/// parent's pane.
extension DelegationCoordinator {
    /// A run `runId` started in the child `id`'s pane: the child is running
    /// it, which clears reported, so a merge waits for the next report. A
    /// run already recorded, or a child that takes no runs, changes nothing.
    public func childRunStarted(_ id: String, runId: String) async {
        await takeRunTurn()
        defer { passRunTurn() }
        guard let found = file.children.first(where: { $0.id == id }), found.runId != runId else { return }
        let context = await pruneContext()
        // Nothing below suspends until the start is saved.
        guard let index = file.children.firstIndex(where: { $0.id == id }) else { return }
        var next = file
        guard next.children[index].runId != runId, next.children[index].noteRun(runId) else { return }
        keep(next, context: context)
    }

    /// The run `runId` in the child `id`'s pane ended. The first rule that
    /// applies records it:
    ///
    /// 1. Quitting the app killed it: the child is interrupted, with no notice.
    /// 2. REPORT.md holds text that differs from the current revision's: the
    ///    next revision is recorded at the head the child has checked out,
    ///    with a copy of the report, and the parent gets one reported notice.
    /// 3. Otherwise the child has ended, and when the run was the one its
    ///    parent awaits (delegate's first run, or the run a follow-up went
    ///    to) the parent gets one ended_without_report notice.
    ///
    /// Only the run going now is recorded, or the child's first run, whose
    /// start the host may not have reported yet. A second report of the same
    /// end, or the end of any other run, changes nothing. The notice sent, if
    /// any, as it was made: delivery then offers it to the parent's pane.
    @discardableResult public func childRunEnded(_ id: String, runId: String, end: ChildRunEnd) async -> Notice? {
        let notice = await recordRunEnd(id, runId: runId, end: end)
        if notice != nil { await deliverPending() }
        return notice
    }

    private func recordRunEnd(_ id: String, runId: String, end: ChildRunEnd) async -> Notice? {
        await takeRunTurn()
        defer { passRunTurn() }
        guard let found = file.children.first(where: { $0.id == id }), Self.mayEnd(found, runId: runId) else { return nil }
        var report: ChildReportFile?, head: String?
        if end != .quit {
            let path = ChildWorktree.reportFile(worktreePath: found.worktreePath)
            report = await Task.detached { ChildReportFile.read(path) }.value
            // A report whose head git cannot read counts as no new report; its
            // change is still there for the next run's end.
            if let report, report.digest != found.reportDigest { head = await ChildMerge.head(of: found.worktreePath) }
        }
        let context = await pruneContext()
        // Nothing below suspends until the end is saved, so the same end
        // reported twice finds it recorded.
        guard let index = file.children.firstIndex(where: { $0.id == id }), Self.mayEnd(file.children[index], runId: runId),
              file.children[index].reportDigest == found.reportDigest else { return nil }
        var next = file
        var child = next.children[index]
        guard child.noteRun(runId) else { return nil }
        // The run's end settles any permission request it was waiting on.
        if child.state == .waiting { child.apply(.answerPermission) }
        var notice: Notice?
        if end == .quit {
            child.apply(.quitApp)
        } else if let report, let head, report.digest != child.reportDigest, child.recordReport(head: head) {
            child.reportDigest = report.digest
            next.setCopy(DelegationCopy(childId: id, kind: .report, revision: child.reportRevision, contents: report.start, totalBytes: report.totalBytes))
            notice = Notice(id: UUID().uuidString.lowercased(), childId: id, reportRevision: child.reportRevision, kind: .reported)
        } else {
            child.apply(.endWithoutReport)
            if child.awaitedRunId == runId { notice = Notice(id: UUID().uuidString.lowercased(), childId: id, reportRevision: child.reportRevision, kind: .endedWithoutReport) }
        }
        next.children[index] = child
        if let notice { next.notices.append(notice) }
        keep(next, context: context)
        return notice
    }

    /// The run `runId` in the child `id`'s pane carries its parent's work: the
    /// run a follow-up went to, as its delivery learns it. Its end without a
    /// new report sends the parent one ended_without_report notice. When the
    /// host has reported that end already, the notice is sent now; a run
    /// awaited already, or one that ended with a report or by quitting,
    /// sends nothing more. A follow-up that started a new run in the child's
    /// idle pane (reported, ended, interrupted or merged) has the child
    /// running it at once, which clears reported, whether or not the host
    /// has reported that start yet; any other run's start and end stay the
    /// host's to report. The notice sent, if any, which delivery then offers
    /// to the parent's pane.
    @discardableResult func awaitRun(_ runId: String, of id: String) async -> Notice? {
        let notice = await recordAwait(runId, of: id)
        if notice != nil { await deliverPending() }
        return notice
    }

    private func recordAwait(_ runId: String, of id: String) async -> Notice? {
        await takeRunTurn()
        defer { passRunTurn() }
        guard let found = file.children.first(where: { $0.id == id }), found.awaitedRunId != runId else { return nil }
        let context = await pruneContext()
        // Nothing below suspends until the await is saved.
        guard let index = file.children.firstIndex(where: { $0.id == id }), file.children[index].awaitedRunId != runId else { return nil }
        var next = file
        var child = next.children[index]
        var notice: Notice?
        if child.runId == runId, child.state == .ended {
            let sent = Notice(id: UUID().uuidString.lowercased(), childId: id, reportRevision: child.reportRevision, kind: .endedWithoutReport)
            next.notices.append(sent); notice = sent
        } else if child.runId != runId, Self.isIdleBetweenRuns(child.state) {
            _ = child.noteRun(runId)
        }
        child.awaitedRunId = runId
        next.children[index] = child
        keep(next, context: context)
        return notice
    }

    /// At launch, before any pane runs again: every child still running or
    /// waiting had its run killed when the app quit. Each becomes
    /// interrupted, with no notice, and comes back stopped. The ids of those children.
    @discardableResult public func interruptRunsKilledByQuit() async -> [String] {
        await takeRunTurn()
        defer { passRunTurn() }
        let context = await pruneContext()
        var next = file, ids: [String] = []
        for index in next.children.indices where next.children[index].state == .running || next.children[index].state == .waiting {
            if next.children[index].state == .waiting { next.children[index].apply(.answerPermission) }
            next.children[index].apply(.quitApp)
            ids.append(next.children[index].id)
        }
        if !ids.isEmpty { keep(next, context: context) }
        return ids
    }

    /// Whether a child in `state` is idle in an open pane after a run, so a
    /// new run there moves it to running.
    static func isIdleBetweenRuns(_ state: ChildState) -> Bool { [.reported, .ended, .interrupted, .merged].contains(state) }

    /// Whether the end of the run `runId` may be recorded for `child`: it is
    /// the run going now, or the child's first run.
    static func mayEnd(_ child: ChildRecord, runId: String) -> Bool {
        child.runId == runId ? child.state == .running || child.state == .waiting : child.state == .creating
    }

    /// Saves `next`; should the disk refuse it, the app goes on with it and
    /// the next save writes it.
    private func keep(_ next: DelegationFile, context: DelegationPruneContext) {
        do { try commit(next, context: context) } catch { file = next }
    }

    /// Waits until no other run event is being recorded, then holds the turn.
    private func takeRunTurn() async {
        guard isRecordingRun else { isRecordingRun = true; return }
        // The turn is handed over with isRecordingRun still set.
        await withCheckedContinuation { runTurns.append($0) }
    }

    /// Hands the turn to the run event waiting longest, if any.
    private func passRunTurn() {
        if runTurns.isEmpty { isRecordingRun = false } else { runTurns.removeFirst().resume() }
    }
}
