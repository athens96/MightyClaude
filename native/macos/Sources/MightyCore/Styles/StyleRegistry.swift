import CryptoKit
import Foundation

public enum StyleSource: String, Codable, Sendable, Equatable, CaseIterable {
    case bundled, user, workspace
    public var precedence: Int {
        switch self { case .bundled: return 0; case .user: return 1; case .workspace: return 2 }
    }
}

/// One candidate file, read exactly once: the bytes shown on the approval card
/// are the bytes hashed, copied and decoded (§4.4).
public struct DiscoveredStyleFile: Sendable, Equatable {
    public var source: StyleSource
    public var url: URL
    public var workspacePath: String?
    public var hash: String
    public var data: Data
    public init(source: StyleSource, url: URL, workspacePath: String?, data: Data) {
        self.source = source; self.url = url; self.workspacePath = workspacePath
        self.data = data; self.hash = StyleHash.of(data)
    }
}

public struct RegisteredStyle: Sendable, Identifiable {
    public var manifest: StyleManifest
    public var source: StyleSource
    public var path: String
    public var workspacePath: String?
    public var hash: String
    public var approval: StyleApprovalState
    public var id: String { manifest.id }
    /// Built once per manifest: the evaluator precomputes its lookups.
    public var evaluator: StyleEvaluator { cached }
    private let cached: StyleEvaluator
    public init(manifest: StyleManifest, source: StyleSource, path: String, workspacePath: String?, hash: String, approval: StyleApprovalState) {
        self.manifest = manifest; self.source = source; self.path = path; self.workspacePath = workspacePath
        self.hash = hash; self.approval = approval; self.cached = StyleEvaluator(manifest)
    }
    /// Bundled styles are pre-approved; the rest must have been said yes to.
    public var isRunnable: Bool { approval == .preApproved || approval == .approved }
}

public struct StyleRejection: Sendable, Equatable {
    public var path: String
    public var source: StyleSource
    public var error: StyleManifestError
    public init(path: String, source: StyleSource, error: StyleManifestError) { self.path = path; self.source = source; self.error = error }
}

/// A path string alone cannot tell a remote host's `/home/ubuntu/proj` from
/// the Mac's own (§3.1), so the workspace travels as a pair.
public struct StyleWorkspaceRef: Sendable, Equatable, Hashable {
    public var path: String
    public var isRemote: Bool
    public init(path: String, isRemote: Bool) { self.path = path; self.isRemote = isRemote }
}

/// The whole file's bytes, not the parsed result: a changed comment or space
/// is a changed manifest and asks again (§4.1).
public enum StyleHash {
    public static func of(_ data: Data) -> String { SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined() }
}

/// The only place styles are read off disk.
public enum StyleSourceScanner {
    public static func bundled() -> [DiscoveredStyleFile] {
        for directory in BundledStyleSource.directories() {
            let files = read(directory: directory, source: .bundled, workspacePath: nil)
            if !files.isEmpty { return files }
        }
        return []
    }

    public static func user(directory: URL) -> [DiscoveredStyleFile] { read(directory: directory, source: .user, workspacePath: nil) }

    /// Called only for a workspace with `remote == nil`; a linked folder or a
    /// linked `.claude` puts the directory outside the repo and yields nothing.
    public static func workspace(path: String) -> [DiscoveredStyleFile] {
        let workspace = URL(fileURLWithPath: path, isDirectory: true).resolvingSymlinksInPath()
        let directory = URL(fileURLWithPath: path, isDirectory: true)
            .appendingPathComponent(".claude/mighty-styles", isDirectory: true).resolvingSymlinksInPath()
        guard StylePathBoundary.contains(parent: workspace, child: directory) else { return [] }
        return read(directory: directory, source: .workspace, workspacePath: workspace.path)
    }

    /// Listing stops at 1 024 entries so a folder with a million files cannot
    /// stall the scan, and only the first 32 `.json` by name are read (§3.1).
    static func read(directory: URL, source: StyleSource, workspacePath: String?) -> [DiscoveredStyleFile] {
        let keys: [URLResourceKey] = [.isRegularFileKey, .isSymbolicLinkKey, .linkCountKey]
        guard let enumerated = try? FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: keys, options: [.skipsHiddenFiles]) else { return [] }
        let listing = enumerated.prefix(StyleLimits.maximumDirectoryEntries).sorted { $0.lastPathComponent < $1.lastPathComponent }
        var files: [DiscoveredStyleFile] = []
        for url in listing where url.pathExtension == "json" {
            guard files.count < StyleLimits.maximumFilesPerSource else { break }
            guard StylePathBoundary.isPlainFile(url) else { continue }
            guard let data = CLIAccountSupport.boundedData(url, maximumBytes: StyleLimits.maximumBytes + 1) else { continue }
            files.append(DiscoveredStyleFile(source: source, url: url, workspacePath: workspacePath, data: data))
        }
        return files
    }
}

/// The link and boundary checks §1.8 and §3.1 share. A hard link looks exactly
/// like a plain file, so `linkCount` is the only thing that catches it.
public enum StylePathBoundary {
    public static func contains(parent: URL, child: URL) -> Bool {
        let base = parent.standardizedFileURL.path
        let path = child.standardizedFileURL.path
        return path == base || path.hasPrefix(base.hasSuffix("/") ? base : base + "/")
    }

    public static func isPlainFile(_ url: URL) -> Bool {
        guard let values = try? url.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey, .linkCountKey]) else { return false }
        return values.isSymbolicLink != true && values.isRegularFile == true && (values.linkCount ?? 0) == 1
    }

    /// Directories always carry a link count above one on this file system, so
    /// only the link and kind checks apply to them (docs/mighty-styles-deviations.md).
    public static func isPlainDirectory(_ url: URL) -> Bool {
        guard let values = try? url.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey]) else { return false }
        return values.isSymbolicLink != true && values.isDirectory == true
    }
}

public struct StyleRegistry: Sendable {
    public private(set) var styles: [RegisteredStyle]
    public init(styles: [RegisteredStyle] = []) { self.styles = styles }

    /// Higher precedence wins a clashing id and the loser is refused, not
    /// shadowed: approval is bound to bytes and is never inherited (§3.3).
    public static func make(files: [DiscoveredStyleFile], approvals: [StyleApprovalRecord]) -> (styles: [RegisteredStyle], rejections: [StyleRejection]) {
        let ordered = files.enumerated().sorted { left, right in
            if left.element.source.precedence != right.element.source.precedence { return left.element.source.precedence < right.element.source.precedence }
            if left.element.url.lastPathComponent != right.element.url.lastPathComponent { return left.element.url.lastPathComponent < right.element.url.lastPathComponent }
            return left.offset < right.offset
        }.map(\.element)
        var styles: [RegisteredStyle] = []
        var rejections: [StyleRejection] = []
        for file in ordered {
            do {
                let manifest = try StyleManifestDecoder.decode(file.data, source: file.source)
                if let winner = styles.first(where: { $0.id == manifest.id }) {
                    rejections.append(StyleRejection(path: file.url.path, source: file.source, error: StyleErrors.idCollision(manifest.id, winner.source)))
                    continue
                }
                let approval = StyleTrustStore.state(for: file, in: approvals)
                styles.append(RegisteredStyle(manifest: manifest, source: file.source, path: file.url.path,
                                              workspacePath: file.workspacePath, hash: file.hash, approval: approval))
            } catch let error as StyleManifestError {
                rejections.append(StyleRejection(path: file.url.path, source: file.source, error: error))
            } catch {
                rejections.append(StyleRejection(path: file.url.path, source: file.source, error: StyleErrors.notJSON))
            }
        }
        return (styles, rejections)
    }

    public func resolve(_ id: String) -> RegisteredStyle? { styles.first { $0.id == id } }

    public func applicable(workspace: StyleWorkspaceRef?) -> [RegisteredStyle] {
        styles.filter { style in
            guard style.source == .workspace else { return true }
            guard let workspace, !workspace.isRemote else { return false }
            return style.workspacePath == workspace.path
        }
    }

    /// What a pane may actually run: approved or bundled, and — for everything
    /// but a bundle — bound to the hash the pane last chose (§3.4).
    public func runnable(_ id: String, workspace: StyleWorkspaceRef?, hash: String?) -> RegisteredStyle? {
        guard let style = applicable(workspace: workspace).first(where: { $0.id == id }), style.isRunnable else { return nil }
        guard style.source != .bundled else { return style }
        guard let hash, hash == style.hash else { return nil }
        return style
    }

    /// A pane keeps its earlier request blocks when its style changes, so the
    /// title comes from every runnable style, not just this pane's (§1.10).
    public func runnableInPrecedence(workspace: StyleWorkspaceRef?) -> [RegisteredStyle] {
        applicable(workspace: workspace).filter(\.isRunnable).sorted {
            $0.source.precedence != $1.source.precedence ? $0.source.precedence < $1.source.precedence : $0.id < $1.id
        }
    }

    public func requestTitle(forInput input: String, workspace: StyleWorkspaceRef?) -> String? {
        StyleRequestTitles(styles: runnableInPrecedence(workspace: workspace)).prefix(input)
    }

    public func requestIcon(forInput input: String, workspace: StyleWorkspaceRef?) -> StyleIcon? {
        StyleRequestTitles(styles: runnableInPrecedence(workspace: workspace)).icon(input)
    }

    public func requestTint(forInput input: String, workspace: StyleWorkspaceRef?) -> StyleTint {
        StyleRequestTitles(styles: runnableInPrecedence(workspace: workspace)).tint(input)
    }
}

/// The precedence sweep of §1.10 over an already-ordered list. A graph redraws
/// every visible block on every keystroke, so the list is built once per render
/// rather than three times per block.
public struct StyleRequestTitles: Sendable {
    private let styles: [RegisteredStyle]
    public init(styles: [RegisteredStyle] = []) { self.styles = styles }

    public func prefix(_ input: String) -> String? {
        for style in styles {
            if let title = style.evaluator.requestTitle(forInput: input) { return title }
        }
        return nil
    }

    public func icon(_ input: String) -> StyleIcon? {
        for style in styles where style.evaluator.recognised(inPrompt: input) != nil {
            return style.evaluator.requestIcon(forInput: input)
        }
        return nil
    }

    public func tint(_ input: String) -> StyleTint {
        for style in styles where style.evaluator.recognised(inPrompt: input) != nil {
            return style.evaluator.requestTint(forInput: input)
        }
        return .accent
    }
}

/// `Bundle.module` traps when the bundle is missing, which would kill a user's
/// app over a packaging slip; this search simply returns nothing (§3.2).
final class BundledStyleMarker {}

public enum BundledStyleSource {
    public static let bundleName = "MightyClaude_MightyCore.bundle"
    public static let directoryName = "Styles"

    /// The places the resource bundle sits: beside the app's own resources in
    /// an assembled `.app`, and beside the test runner under `swift test`.
    /// The directory that *contains* the `.app` is deliberately not among
    /// them — anything dropped there would be read as a pre-approved bundled
    /// style, and no correct layout ever needs it.
    static func searchRoots() -> [URL] {
        let marker = Bundle(for: BundledStyleMarker.self)
        var roots: [URL?] = [Bundle.main.resourceURL, marker.resourceURL, marker.bundleURL, Bundle.main.bundleURL,
                             Bundle.main.executableURL?.resolvingSymlinksInPath().deletingLastPathComponent()]
        // Under `swift test` the resource bundle is copied inside the `.xctest`
        // bundle by recent SwiftPM releases and only placed *beside* it by older
        // ones (the CI runner's toolchain). The parent of a test bundle is added
        // for that layout alone; an `.app`'s parent stays excluded (see above).
        for bundle in [Bundle.main.bundleURL, marker.bundleURL] where bundle.pathExtension == "xctest" {
            roots.append(bundle.deletingLastPathComponent())
        }
        return roots.compactMap { $0 }
    }

    public static func directories() -> [URL] {
        var found: [URL] = []
        for root in searchRoots() {
            let bundle = root.appendingPathComponent(bundleName, isDirectory: true)
            // SwiftPM builds a macOS bundle on this platform; the flat layout
            // is kept as the second candidate so a plain copy also works.
            for directory in [bundle.appendingPathComponent("Contents/Resources/" + directoryName, isDirectory: true),
                              bundle.appendingPathComponent(directoryName, isDirectory: true)] {
                var isDirectory: ObjCBool = false
                guard FileManager.default.fileExists(atPath: directory.path, isDirectory: &isDirectory), isDirectory.boolValue else { continue }
                guard !found.contains(where: { $0.standardizedFileURL == directory.standardizedFileURL }) else { continue }
                found.append(directory)
            }
        }
        return found
    }
}
