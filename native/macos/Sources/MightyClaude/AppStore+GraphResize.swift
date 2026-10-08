import MightyCore

extension AppStore {
    /// Persist once at the end of a drag; live geometry stays in the graph view.
    func setGraphBlockSize(_ sessionID: String, nodeID: String, size: MightyGraphBlockSize?) {
        updateSession(sessionID) { session in
            var sizes = session.graphBlockSizes ?? [:]
            if let size = size?.normalized(nodeID: nodeID) { sizes[nodeID] = size }
            else { sizes.removeValue(forKey: nodeID) }
            session.graphBlockSizes = sizes.isEmpty ? nil : sizes
        }
    }

    /// Store the dragged size of the latest result card as the per-session shared
    /// result size. Pass nil to clear and return to auto-fit.
    func setGraphResultSize(_ sessionID: String, size: MightyGraphBlockSize?) {
        updateSession(sessionID) { session in
            session.graphResultSize = size?.normalized
        }
    }

    /// Store the dragged size of the pending plan block, the pane's one plan
    /// size. Pass nil to clear and return to the window fit.
    func setGraphPlanSize(_ sessionID: String, size: MightyGraphBlockSize?) {
        updateSession(sessionID) { session in
            session.graphPlanSize = size?.normalized
        }
    }

    /// The Mighty view's "다이어그램 | 타임라인" choice, kept per pane. The diagram is
    /// saved as nothing at all, so a pane that never chose stays as it was saved.
    func setGraphViewMode(_ sessionID: String, mode: MightyGraphViewMode) {
        updateSession(sessionID) { session in
            guard session.kind == "claude" else { return }
            session.graphViewMode = mode == .diagram ? nil : mode
        }
    }
}
