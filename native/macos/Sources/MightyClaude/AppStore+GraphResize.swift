import MightyCore

extension AppStore {
    /// Persist once at the end of a drag; live geometry stays in the graph view.
    func setGraphBlockSize(_ sessionID: String, nodeID: String, size: MightyGraphBlockSize?) {
        updateSession(sessionID) { session in
            var sizes = session.graphBlockSizes ?? [:]
            if let size = size?.normalized { sizes[nodeID] = size }
            else { sizes.removeValue(forKey: nodeID) }
            session.graphBlockSizes = sizes.isEmpty ? nil : sizes
        }
    }
}
