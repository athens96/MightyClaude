import Darwin
import Foundation

/// Runtime assets required by Ghostty's exec backend.
///
/// The package owns these assets and always points libghostty at this immutable
/// bundle location before the C runtime initializes. User-level Ghostty
/// resources and configuration are never consulted.
public enum GhosttyRuntimeResources {
    /// The package-bundled Ghostty resource directory.
    ///
    /// Ghostty expects shell integration below this directory and its compiled
    /// terminfo database in a sibling `terminfo` directory.
    public static var directoryURL: URL? {
        resourceBundle?.url(forResource: "Ghostty", withExtension: nil)
    }

    /// The compiled terminfo database exported to child shells by Ghostty.
    public static var terminfoDirectoryURL: URL? {
        resourceBundle?.url(forResource: "terminfo", withExtension: nil)
    }

    static func configureEnvironment() {
        guard let path = directoryURL?.path else { return }
        setenv("GHOSTTY_RESOURCES_DIR", path, 1)
    }

    // MightyCode patch: SwiftPM's generated `Bundle.module` only looks beside the
    // .app (not in Contents/Resources, where a signed app must keep it) and at the
    // absolute build folder of the machine that compiled it, then traps. A shipped
    // app built on CI has neither, so the first terminal killed it. Search the
    // places an app or a `swift run` binary actually keeps the bundle, and return
    // nil rather than trap when it is missing.
    private final class BundleMarker {}

    static let resourceBundleName = "GhosttyKit_GhosttyTerminal.bundle"

    static let resourceBundle: Bundle? = {
        let marker = Bundle(for: BundleMarker.self)
        let roots: [URL?] = [
            Bundle.main.resourceURL,
            marker.resourceURL,
            Bundle.main.bundleURL,
            marker.bundleURL,
            Bundle.main.executableURL?.resolvingSymlinksInPath().deletingLastPathComponent(),
        ]
        for case let root? in roots {
            if let bundle = Bundle(url: root.appendingPathComponent(resourceBundleName)) {
                return bundle
            }
        }
        return nil
    }()
}
