import Foundation

/// The two bundled manifests, decoded once. Nothing here traps: a packaging
/// slip leaves the cache empty and every pane falls back to the plain CLI (§3.2).
public final class BundledStyles: @unchecked Sendable {
    public static let shared = BundledStyles()
    private let lock = NSLock()
    private var loaded: [RegisteredStyle]?

    public func styles() -> [RegisteredStyle] {
        lock.lock()
        defer { lock.unlock() }
        if let loaded { return loaded }
        let made = StyleRegistry.make(files: StyleSourceScanner.bundled(), approvals: []).styles
        loaded = made
        return made
    }

    public func style(_ id: String) -> RegisteredStyle? { styles().first { $0.id == id } }
    public func evaluator(_ id: String) -> StyleEvaluator? { style(id)?.evaluator }
    public func manifest(_ id: String) -> StyleManifest? { style(id)?.manifest }
    /// Only the tests need to re-read after writing a fixture bundle.
    func reset() { lock.lock(); loaded = nil; lock.unlock() }
}
