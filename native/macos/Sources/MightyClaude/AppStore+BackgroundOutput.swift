import Foundation
import MightyCore

/// A background run's live output: once a second the last lines of every running task
/// block's output file are read off the main actor into `backgroundOutput`, by block id.
/// The 흐름 blocks, the 파이프라인 cards, the strip above the composer and the phone read
/// that value. It is never saved: it can move every second, and the state file must not.
///
/// A block that ended is read once more and then left alone; a block redrawn from history
/// (or a save) that still has its file gets one read. A file that is missing, unreadable,
/// not a regular file, or on another computer (a remote workspace's pane) gives no lines
/// and no error.
extension AppStore {
    static let backgroundOutputInterval: Duration = .seconds(1)

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

    func backgroundOutputTick() {
        // One batch at a time: a slow disk never stacks reads up.
        guard !ending, !backgroundOutputReading else { return }
        let pruning = !backgroundOutput.isEmpty || !backgroundOutputPolling.isEmpty || !backgroundOutputRead.isEmpty
        let local = Set(snapshot.workspaces.filter { !$0.isRemote }.map(\.id))
        var present = Set<String>()
        var reads: [(id: String, path: String)] = []
        for session in snapshot.sessions where session.kind == "claude" && local.contains(session.workspaceId) {
            for runs in [graphHistory[session.id]?.runs ?? [], session.graphRuns ?? []] {
                for run in runs {
                    for agent in run.agents {
                        if pruning { present.insert(agent.id) }
                        guard agent.isTask, let path = agent.outputFile else { continue }
                        if !MightyGraphSupport.terminal(agent.status) {
                            backgroundOutputPolling.insert(agent.id)
                            reads.append((agent.id, path))
                        } else if backgroundOutputPolling.remove(agent.id) != nil
                                    || (!backgroundOutputRead.contains(agent.id) && backgroundOutput[agent.id] == nil) {
                            // Its last read once it ended, or its one read when redrawn.
                            backgroundOutputRead.insert(agent.id)
                            reads.append((agent.id, path))
                        }
                    }
                }
            }
        }
        if pruning {
            // Blocks no longer in any graph: a closed pane, a trimmed request.
            backgroundOutputPolling.formIntersection(present)
            backgroundOutputRead.formIntersection(present)
            let gone = backgroundOutput.keys.filter { !present.contains($0) }
            if !gone.isEmpty {
                var next = backgroundOutput
                for id in gone { next.removeValue(forKey: id) }
                backgroundOutput = next
                backgroundOutputChanged()
            }
        }
        guard !reads.isEmpty else { return }
        backgroundOutputReading = true
        let batch = reads
        Task.detached(priority: .utility) { [weak self] in
            let results = batch.map { (id: $0.id, lines: AppStore.readBackgroundOutput($0.path)) }
            await self?.applyBackgroundOutput(results)
        }
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
    /// it: the last lines stay once a run ended, and a missing file shows none.
    func applyBackgroundOutput(_ results: [(id: String, lines: [String]?)]) {
        backgroundOutputReading = false
        guard !ending else { return }
        var next = backgroundOutput
        for result in results {
            guard let lines = result.lines, !lines.isEmpty,
                  // A block dropped while its read was out stays dropped.
                  backgroundOutputPolling.contains(result.id) || backgroundOutputRead.contains(result.id) else { continue }
            if next[result.id] != lines { next[result.id] = lines }
        }
        guard next != backgroundOutput else { return }
        backgroundOutput = next
        backgroundOutputChanged()
    }

    /// The phone hears of a change the way it hears of a graph's: `mobileMightyDigest`
    /// hashes these lines, so the pane's revision moves.
    private func backgroundOutputChanged() {
        mobileObserve()
    }

    /// The lines of `runs`' task blocks, for the phone's structure of one pane.
    func backgroundOutput(for runs: [MightyGraphRun]) -> [String: [String]] {
        guard !backgroundOutput.isEmpty else { return [:] }
        var held: [String: [String]] = [:]
        for run in runs {
            for agent in run.agents where agent.isTask { if let lines = backgroundOutput[agent.id] { held[agent.id] = lines } }
        }
        return held
    }
}
