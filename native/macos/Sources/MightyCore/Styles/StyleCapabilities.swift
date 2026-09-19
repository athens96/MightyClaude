import Foundation

/// The app-side features a manifest may name. Frozen with the tag (§1.8).
public enum StyleCapabilityID {
    public static let casebook = "paperthin.casebook"
    public static let all: Set<String> = [casebook]
    public static func states(of name: String) -> [String] {
        name == casebook ? ["absent", "open", "complete"] : []
    }
    public static func emptyDetail(of name: String) -> String? {
        name == casebook ? "열린 사이클이 없습니다. re0-plan으로 케이스북을 여세요." : nil
    }
}

/// A read-only chip a capability offers. `openPath` is the Mac's alone: the
/// phone has no way to open a file, so the payload never carries a path.
public struct StyleAttachmentItem: Codable, Sendable, Equatable, Identifiable {
    public var id, title: String
    public var detail: String?
    public var readOnly: Bool
    public var openPath: String?
    public init(id: String, title: String, detail: String? = nil, readOnly: Bool = true, openPath: String? = nil) {
        self.id = id; self.title = title; self.detail = detail; self.readOnly = readOnly; self.openPath = openPath
    }
}

/// The newest iteration folder the coil skills wrote: `.re0/iteration/<version>-<workname>/`
/// with `DESIGN`/`WORKFLOW`/`EVIDENCE`/`RETRO` `.local.md` and flat `REF-*.local.md`.
public struct StyleCasebook: Sendable, Equatable {
    public var name: String
    public var path: String
    public var files: [String]
    public var modifiedAt: Date
    /// `full` cycles carry a DESIGN; `lightweight` ones only a RETRO note.
    public var weight: String { files.contains("DESIGN.local.md") ? "full" : "lightweight" }
    public init(name: String, path: String, files: [String], modifiedAt: Date) {
        self.name = name; self.path = path; self.files = files; self.modifiedAt = modifiedAt
    }

    public static let knownOrder = ["DESIGN.local.md", "WORKFLOW.local.md", "EVIDENCE.local.md", "RETRO.local.md"]

    public static func latest(workspacePath: String) -> StyleCasebook? {
        let workspace = URL(fileURLWithPath: workspacePath, isDirectory: true).resolvingSymlinksInPath()
        let root = URL(fileURLWithPath: workspacePath, isDirectory: true)
            .appendingPathComponent(".re0/iteration", isDirectory: true).resolvingSymlinksInPath()
        // A linked `.re0` could point anywhere; only a folder inside the
        // workspace is walked at all (§1.8).
        guard StylePathBoundary.contains(parent: workspace, child: root) else { return nil }
        let keys: [URLResourceKey] = [.isDirectoryKey, .isSymbolicLinkKey, .contentModificationDateKey, .linkCountKey, .isRegularFileKey]
        guard let listing = try? FileManager.default.contentsOfDirectory(at: root, includingPropertiesForKeys: keys, options: [.skipsHiddenFiles]) else { return nil }
        // Real folders only, newest first, so the scan stays small however long
        // the history is. The 24 are taken *after* the sort: directory order is
        // arbitrary, so capping first would drop the newest cycles at random.
        let folders = listing.compactMap { url -> (URL, Date)? in
            guard StylePathBoundary.isPlainDirectory(url) else { return nil }
            let modified = (try? url.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate) ?? .distantPast
            return (url, modified)
        }.sorted { $0.1 > $1.1 }.prefix(StyleLimits.maximumCasebookFolders).map(\.0)
        var best: StyleCasebook?
        for folder in folders {
            let entries = ((try? FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: keys, options: [.skipsHiddenFiles])) ?? [])
                .filter { $0.lastPathComponent.hasSuffix(".local.md") && StylePathBoundary.isPlainFile($0) }
            guard !entries.isEmpty else { continue }
            let newest = entries.compactMap { try? $0.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate }.max() ?? .distantPast
            let names = entries.map(\.lastPathComponent)
            let ordered = knownOrder.filter(names.contains) + names.filter { !knownOrder.contains($0) }.sorted()
            let candidate = StyleCasebook(name: String(folder.lastPathComponent.prefix(120)), path: folder.path,
                                          files: Array(ordered.prefix(StyleLimits.maximumCasebookFiles)), modifiedAt: newest)
            if best == nil || candidate.modifiedAt > best!.modifiedAt { best = candidate }
        }
        return best
    }
}

public enum StyleCapabilities {
    /// Reads every named capability once, on a worker. There is no approval
    /// gate on this path — Paperthin is bundled — so a folder named with a
    /// direction control must not reach either surface intact (§1.8).
    public static func evaluate(_ names: [String], workspacePath: String) -> (states: [String: String], attachments: [StyleAttachmentItem]) {
        var states: [String: String] = [:]
        var attachments: [StyleAttachmentItem] = []
        for name in names where name == StyleCapabilityID.casebook {
            guard let raw = StyleCasebook.latest(workspacePath: workspacePath) else { states[name] = "absent"; continue }
            let complete = raw.files.contains("DESIGN.local.md") && raw.files.contains("RETRO.local.md")
            states[name] = complete ? "complete" : "open"
            let clean = normalised(raw)
            let detail = StyleText.normalised(clean.name + " \u{00B7} " + clean.weight, limit: StyleLimits.maximumCapabilityString)
            attachments += zip(raw.files, clean.files).map { original, file in
                StyleAttachmentItem(id: file, title: StyleText.normalised(title(of: file), limit: StyleLimits.maximumCapabilityString),
                                    detail: detail, readOnly: true,
                                    openPath: openPath(casebook: raw, file: original, workspacePath: workspacePath))
            }
        }
        return (states, attachments)
    }

    static func title(of file: String) -> String {
        file.hasSuffix(".local.md") ? String(file.dropLast(".local.md".count)) : file
    }

    static func normalised(_ casebook: StyleCasebook) -> StyleCasebook {
        StyleCasebook(name: StyleText.normalised(casebook.name), path: casebook.path,
                      files: casebook.files.map { StyleText.normalised($0) }, modifiedAt: casebook.modifiedAt)
    }

    private static func openPath(casebook: StyleCasebook, file: String, workspacePath: String) -> String? {
        let workspace = URL(fileURLWithPath: workspacePath, isDirectory: true).resolvingSymlinksInPath()
        let url = URL(fileURLWithPath: casebook.path, isDirectory: true).appendingPathComponent(file).resolvingSymlinksInPath()
        return StylePathBoundary.contains(parent: workspace, child: url) ? url.path : nil
    }
}
