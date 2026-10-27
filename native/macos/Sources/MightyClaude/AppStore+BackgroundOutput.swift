import Foundation
import MightyCore

/// A background run's last output lines, by pane and graph block id. Kept apart from
/// `AppStore`'s published state: a line that changes every second must redraw the one
/// pane showing it (its `BackgroundPaneOutput`), never the whole window.
final class BackgroundOutputModel {
    private(set) var byPane: [String: [String: [String]]] = [:]
    private var panes: [String: BackgroundPaneOutput] = [:]

    var isEmpty: Bool { byPane.isEmpty }

    /// The lines of one pane's blocks, by block id.
    func lines(forPane id: String) -> [String: [String]] { byPane[id] ?? [:] }

    /// The object a pane's views observe; made on first ask.
    func pane(_ id: String) -> BackgroundPaneOutput {
        if let pane = panes[id] { return pane }
        let pane = BackgroundPaneOutput()
        pane.lines = lines(forPane: id)
        panes[id] = pane
        return pane
    }

    /// Sets a block's lines, or drops them with nil; true when anything changed. Only
    /// that pane's object publishes.
    func set(_ lines: [String]?, block: String, pane: String) -> Bool {
        guard byPane[pane]?[block] != lines else { return false }
        var slice = byPane[pane] ?? [:]
        slice[block] = lines
        byPane[pane] = slice.isEmpty ? nil : slice
        panes[pane]?.lines = slice
        return true
    }

    /// Keeps only the blocks in `blocks` and the pane objects of `panes`; true when a
    /// block's lines went.
    func retain(blocks: Set<String>, panes live: Set<String>) -> Bool {
        var changed = false
        for (pane, slice) in byPane {
            let kept = slice.filter { blocks.contains($0.key) }
            guard kept.count != slice.count else { continue }
            byPane[pane] = kept.isEmpty ? nil : kept
            panes[pane]?.lines = kept
            changed = true
        }
        panes = panes.filter { live.contains($0.key) }
        return changed
    }
}

/// One pane's background output lines, by block id: what its 흐름 blocks, 파이프라인
/// cards and strip observe.
final class BackgroundPaneOutput: ObservableObject {
    @Published fileprivate(set) var lines: [String: [String]] = [:]
}

/// A background run's live output: once a second the last lines of every running task
/// block's output file are read off the main actor into `backgroundOutput`. The 흐름
/// blocks, the 파이프라인 cards, the strip above the composer and the phone read them.
/// They are never saved: they can move every second, and the state file must not.
///
/// A block that ended is read once more and then left alone; a block redrawn from history
/// (or a save) that still has its file gets one read. A file that is missing, unreadable,
/// not a regular file, or on another computer (a remote workspace's pane) gives no lines
/// and no error.
extension AppStore {
    static let backgroundOutputInterval: Duration = .seconds(1)
    /// A batch of reads still out after this is given up on, so one hung read (a stalled
    /// network volume) never stops the polling.
    static let backgroundOutputReadTimeout: Duration = .seconds(10)

    /// Its own 1 s loop: `terminalTick` runs only once a terminal exists, and a Claude
    /// pane needs none. A tick with no background run costs one walk over the graphs.
    func startBackgroundOutputPolling() {
        guard backgroundOutputLoop == nil else { return }
        backgroundOutputLoop = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: AppStore.backgroundOutputInterval)
                guard !Task.isCancelled, let self, !self.ending else { return }
                self.backgroundOutputTick()
            }
        }
    }

    /// The panes whose files this Mac can read: Claude panes in local workspaces.
    private func backgroundOutputSessions() -> [RunSession] {
        let local = Set(snapshot.workspaces.filter { !$0.isRemote }.map(\.id))
        return snapshot.sessions.filter { $0.kind == "claude" && local.contains($0.workspaceId) }
    }

    func backgroundOutputTick() {
        guard !ending else { return }
        if backgroundOutputReading {
            guard let since = backgroundOutputReadingSince, ContinuousClock.now - since > Self.backgroundOutputReadTimeout else { return }
            backgroundOutputReading = false
        }
        let sessions = backgroundOutputSessions()
        // The graphs' shape: a pane, a request or a block that came or went.
        var shape = Hasher()
        var queued = Set<String>()
        var reads: [(id: String, pane: String, path: String)] = []
        for session in sessions {
            shape.combine(session.id)
            let held = backgroundOutput.lines(forPane: session.id)
            for runs in [graphHistory[session.id]?.runs ?? [], session.graphRuns ?? []] {
                shape.combine(runs.count)
                for run in runs {
                    shape.combine(run.id); shape.combine(run.agents.count)
                    for agent in run.agents {
                        guard agent.isTask, let path = agent.outputFile, !queued.contains(agent.id) else { continue }
                        if !MightyGraphLayout.terminal(agent.status) {
                            backgroundOutputPolling.insert(agent.id)
                        } else if backgroundOutputPolling.remove(agent.id) != nil || (!backgroundOutputRead.contains(agent.id) && held[agent.id] == nil) {
                            // Its last read once it ended, or its one read when redrawn.
                            backgroundOutputRead.insert(agent.id)
                        } else {
                            continue
                        }
                        queued.insert(agent.id)
                        reads.append((agent.id, session.id, path))
                    }
                }
            }
        }
        let shapeKey = shape.finalize()
        if shapeKey != backgroundOutputShape {
            backgroundOutputShape = shapeKey
            pruneBackgroundOutput(sessions)
        }
        guard !reads.isEmpty else { return }
        backgroundOutputBatch &+= 1
        let batchID = backgroundOutputBatch
        backgroundOutputReading = true
        backgroundOutputReadingSince = .now
        let batch = reads
        Task.detached(priority: .utility) { [weak self] in
            let results = batch.map { (id: $0.id, pane: $0.pane, lines: AppStore.readBackgroundOutput($0.path)) }
            await self?.applyBackgroundOutput(results, batch: batchID)
        }
    }

    /// Drops what belongs to blocks no longer in any graph: a closed pane, a trimmed
    /// request. Run only when the graphs' shape moved.
    private func pruneBackgroundOutput(_ sessions: [RunSession]) {
        guard !backgroundOutput.isEmpty || !backgroundOutputPolling.isEmpty || !backgroundOutputRead.isEmpty else { return }
        var present = Set<String>()
        for session in sessions {
            for runs in [graphHistory[session.id]?.runs ?? [], session.graphRuns ?? []] {
                for run in runs { for agent in run.agents where agent.isTask { present.insert(agent.id) } }
            }
        }
        backgroundOutputPolling.formIntersection(present)
        backgroundOutputRead.formIntersection(present)
        if backgroundOutput.retain(blocks: present, panes: Set(snapshot.sessions.map(\.id))) { mobileObserve() }
    }

    /// The file's last lines, or nil. Only a regular file is opened: a FIFO or a device
    /// would block `open(2)`. A link is followed first, so where it points is what is judged.
    nonisolated static func readBackgroundOutput(_ path: String) -> [String]? {
        let target = URL(fileURLWithPath: path).resolvingSymlinksInPath().path
        guard let type = (try? FileManager.default.attributesOfItem(atPath: target))?[.type] as? FileAttributeType,
              type == .typeRegular else { return nil }
        return BackgroundOutputTail.read(path: target)
    }

    /// Publishes only what changed. A file that gave nothing keeps the lines read before
    /// it: the last lines stay once a run ended, and a missing file shows none. A batch
    /// given up on (`backgroundOutputReadTimeout`) is dropped when it finally answers.
    func applyBackgroundOutput(_ results: [(id: String, pane: String, lines: [String]?)], batch: Int) {
        guard batch == backgroundOutputBatch else { return }
        backgroundOutputReading = false
        backgroundOutputReadingSince = nil
        guard !ending else { return }
        var changed = false
        for result in results {
            guard let lines = result.lines, !lines.isEmpty,
                  // A block dropped while its read was out stays dropped.
                  backgroundOutputPolling.contains(result.id) || backgroundOutputRead.contains(result.id) else { continue }
            if backgroundOutput.set(lines, block: result.id, pane: result.pane) { changed = true }
        }
        // The phone hears of it as of a graph change: `mobileMightyDigest` hashes the pane's lines.
        if changed { mobileObserve() }
    }
}
