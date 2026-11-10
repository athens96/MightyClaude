import CoreServices
import Foundation

/// §1.16: tells the store that something changed under a workspace, so a
/// style's file sources are read again on a file change and never on a timer.
/// Directory-level events are enough: a file written inside a folder is
/// reported as that folder, and `StyleStateEngine.affects` filters the rest.
final class StyleStateWatcher {
    /// What the stream's callback reaches. The stream retains it through the
    /// context's retain/release pair, so a callback already queued when the
    /// watcher goes away still finds a live box, never a freed watcher.
    private final class Box: @unchecked Sendable {
        let changed: @Sendable ([String]) -> Void
        init(_ changed: @escaping @Sendable ([String]) -> Void) { self.changed = changed }
    }

    private let stream: FSEventStreamRef
    private let queue = DispatchQueue(label: "mighty.style-state-watcher", qos: .utility)

    init?(path: String, changed: @escaping @Sendable ([String]) -> Void) {
        let box = Box(changed)
        var context = FSEventStreamContext(
            version: 0, info: Unmanaged.passUnretained(box).toOpaque(),
            retain: { info in
                guard let info else { return nil }
                _ = Unmanaged<Box>.fromOpaque(info).retain()
                return info
            },
            release: { info in
                guard let info else { return }
                Unmanaged<Box>.fromOpaque(info).release()
            },
            copyDescription: nil)
        let callback: FSEventStreamCallback = { _, info, _, paths, _, _ in
            guard let info else { return }
            let box = Unmanaged<Box>.fromOpaque(info).takeUnretainedValue()
            box.changed(Unmanaged<CFArray>.fromOpaque(paths).takeUnretainedValue() as? [String] ?? [])
        }
        let flags = FSEventStreamCreateFlags(kFSEventStreamCreateFlagUseCFTypes)
        guard let stream = FSEventStreamCreate(nil, callback, &context, [path] as CFArray,
                                               FSEventStreamEventId(kFSEventStreamEventIdSinceNow), 0.5, flags) else { return nil }
        self.stream = stream
        FSEventStreamSetDispatchQueue(stream, queue)
        // A failed start still runs deinit (every stored property is set), which
        // stops, invalidates and releases the stream exactly once.
        guard FSEventStreamStart(stream) else { return nil }
    }

    /// Stopped on its own queue, so no callback is running while the stream
    /// is torn down; releasing the stream releases the box.
    deinit {
        let stream = stream
        queue.sync {
            FSEventStreamStop(stream)
            FSEventStreamInvalidate(stream)
        }
        FSEventStreamRelease(stream)
    }
}
