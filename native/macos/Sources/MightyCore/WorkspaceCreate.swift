import Foundation

/// The rules for adding a workspace from a new folder or a Git address
/// (docs/superpowers/specs/2026-10-26-workspace-create-design.md), shared with
/// MightyClaude.Core WorkspaceCreate.cs through native/contracts/fixtures/workspace-create.json.
public enum WorkspaceCreate {
    public enum NameProblem: String, Sendable { case empty, badCharacter, reserved, trailingDotOrSpace, dots, tooLong }
    public enum GitErrorKind: String, Sendable { case gitMissing, invalidURL, auth, notFound, network, other }

    public struct CloneProgress: Equatable, Sendable {
        public enum Phase: String, Sendable { case starting, receiving, resolving, checkout }
        public let phase: Phase; public let percent: Int; public let fraction: Double
        public init(phase: Phase, percent: Int, fraction: Double) { self.phase = phase; self.percent = percent; self.fraction = fraction }
    }

    private static let schemes = ["https://", "http://", "ssh://", "git://", "file://"]

    public static func looksLikeGitURL(_ raw: String) -> Bool {
        let url = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !url.isEmpty, !url.contains(where: \.isWhitespace) else { return false }
        if let scheme = schemes.first(where: { url.lowercased().hasPrefix($0) }) { return url.count > scheme.count }
        if url.range(of: #"^[^/\s@]+@[^/\s:]+:\S+$"#, options: .regularExpression) != nil { return true }
        if url.hasPrefix("/") || url.hasPrefix("\\\\") { return true }
        return url.range(of: #"^[A-Za-z]:[\\/]"#, options: .regularExpression) != nil
    }

    public static func repoName(_ raw: String) -> String? {
        guard looksLikeGitURL(raw) else { return nil }
        var url = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        if let cut = url.firstIndex(where: { $0 == "?" || $0 == "#" }) { url = String(url[..<cut]) }
        while url.hasSuffix("/") || url.hasSuffix("\\") { url.removeLast() }
        if url.lowercased().hasSuffix(".git") { url.removeLast(4) }
        let afterScheme = schemes.first(where: { url.lowercased().hasPrefix($0) }).map { String(url.dropFirst($0.count)) }
        let path = afterScheme.map { rest in rest.firstIndex(of: "/").map { String(rest[$0...]) } ?? "" } ?? url
        let name = path.split(whereSeparator: { $0 == "/" || $0 == "\\" || (afterScheme == nil && $0 == ":") }).last.map(String.init) ?? ""
        return name.isEmpty ? nil : name
    }

    private static let reservedNames: Set<String> = Set(["CON", "PRN", "AUX", "NUL"] + (1...9).map { "COM\($0)" } + (1...9).map { "LPT\($0)" })

    public static func checkName(_ raw: String, windows: Bool = false) -> NameProblem? {
        let name = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        if name.isEmpty { return .empty }
        if name == "." || name == ".." { return .dots }
        if name.utf8.count > 255 { return .tooLong }
        if name.contains(where: { "/\\:".contains($0) }) || name.unicodeScalars.contains(where: { $0.properties.generalCategory == .control }) { return .badCharacter }
        guard windows else { return nil }
        if name.contains(where: { "<>\"|?*".contains($0) }) { return .badCharacter }
        let base = String(name.split(separator: ".", maxSplits: 1, omittingEmptySubsequences: false).first ?? "").uppercased()
        if reservedNames.contains(base) { return .reserved }
        if name.hasSuffix(".") || name.hasSuffix(" ") { return .trailingDotOrSpace }
        return nil
    }

    public static func errorKind(stderr: String) -> GitErrorKind {
        let text = stderr.lowercased()
        let table: [(GitErrorKind, [String])] = [
            (.auth, ["authentication failed", "could not read username", "could not read password", "permission denied", "access denied", "error: 401", "error: 403"]),
            (.notFound, ["not found", "error: 404", "does not appear to be a git repository"]),
            (.network, ["could not resolve host", "could not resolve hostname", "connection refused", "timed out", "network is unreachable", "failed to connect", "connection reset"]),
            (.invalidURL, ["is not supported", "not a valid", "invalid url"]),
        ]
        return table.first(where: { $0.1.contains(where: { text.contains($0) }) })?.0 ?? .other
    }

    public static func progress(line raw: String) -> CloneProgress? {
        let line = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        func percent() -> Int? {
            guard let range = line.range(of: #"\d{1,3}(?=%)"#, options: .regularExpression) else { return nil }
            return min(100, Int(line[range]) ?? 0)
        }
        func make(_ phase: CloneProgress.Phase, _ base: Double, _ span: Double) -> CloneProgress? {
            guard let p = percent() else { return nil }
            return CloneProgress(phase: phase, percent: p, fraction: ((base + span * Double(p) / 100) * 1000).rounded() / 1000)
        }
        if line.hasPrefix("Receiving objects:") { return make(.receiving, 0, 0.7) }
        if line.hasPrefix("Resolving deltas:") { return make(.resolving, 0.7, 0.2) }
        if line.hasPrefix("Updating files:") || line.hasPrefix("Checking out files:") { return make(.checkout, 0.9, 0.1) }
        if line.hasPrefix("Cloning into") || line.hasPrefix("remote:") { return CloneProgress(phase: .starting, percent: 0, fraction: 0) }
        return nil
    }

    /// What the create sheets say about a folder name that cannot be used.
    public static func message(for problem: NameProblem) -> String {
        switch problem {
        case .empty: return L("workspace.name.error.empty")
        case .badCharacter: return L("workspace.name.error.badCharacter")
        case .reserved: return L("workspace.name.error.reserved")
        case .trailingDotOrSpace: return L("workspace.name.error.trailingDotOrSpace")
        case .dots: return L("workspace.name.error.dots")
        case .tooLong: return L("workspace.name.error.tooLong")
        }
    }

    /// What the Git sheet says about a failed check or clone; `detail` is git's own last line, shown for `other`.
    public static func message(for kind: GitErrorKind, detail: String) -> String {
        switch kind {
        case .gitMissing: return L("workspace.git.error.gitMissing")
        case .invalidURL: return L("workspace.git.error.invalidURL")
        case .auth: return L("workspace.git.error.auth")
        case .notFound: return L("workspace.git.error.notFound")
        case .network: return L("workspace.git.error.network")
        case .other: return L("workspace.git.error.other", ["message": detail])
        }
    }

    /// The clone's progress line.
    public static func message(for progress: CloneProgress) -> String {
        let percent = String(progress.percent)
        switch progress.phase {
        case .starting: return L("workspace.git.progress.starting")
        case .receiving: return L("workspace.git.progress.receiving", ["percent": percent])
        case .resolving: return L("workspace.git.progress.resolving", ["percent": percent])
        case .checkout: return L("workspace.git.progress.checkout", ["percent": percent])
        }
    }

    public static func target(location: String, name: String) -> String {
        (location as NSString).appendingPathComponent(name.trimmingCharacters(in: .whitespacesAndNewlines))
    }

    public static func defaultLocation(last: String?, activeWorkspacePath: String?, home: String) -> String {
        var isFolder: ObjCBool = false
        if let last, FileManager.default.fileExists(atPath: last, isDirectory: &isFolder), isFolder.boolValue { return last }
        if let active = activeWorkspacePath {
            let parent = ((active as NSString).deletingLastPathComponent as NSString).standardizingPath
            if FileManager.default.fileExists(atPath: parent, isDirectory: &isFolder), isFolder.boolValue { return parent }
        }
        return home
    }
}
