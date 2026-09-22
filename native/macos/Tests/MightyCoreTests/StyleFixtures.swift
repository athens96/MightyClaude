import Foundation
@testable import MightyCore

/// Manifests are built as ordered key/value text rather than through
/// `JSONSerialization`, because §1.1 requires `schema` to be the first key and
/// a dictionary has no order to give.
enum StyleFixtures {
    static let flat: [(String, String)] = [
        ("schema", "1"),
        ("id", "\"flow\""),
        ("name", "\"Flow\""),
        ("summary", "\"덜어내는 스타일\""),
        ("subtitle", "\"flow\""),
        ("placeholders", "{\"idle\":\"i\",\"answering\":\"a\"}"),
        ("guidance", "{}"),
        ("prerequisites", "{\"mode\":\"all\",\"probes\":[]}"),
        ("phases", "[]"),
        ("groups", "[{\"id\":\"g\",\"title\":\"G\",\"actions\":[\"go\"]}]"),
        ("actions", "[{\"id\":\"go\",\"title\":\"Go\",\"help\":\"h\",\"prompt\":\"/go\",\"takesText\":false}]"),
        ("aliases", "[]"),
        ("recognition", "{\"prefixes\":[\"/\"],\"lowercase\":false}"),
        ("rules", "{\"start\":{\"kind\":\"none\"},\"phase\":{\"kind\":\"none\"},\"next\":{\"kind\":\"byGroup\"},\"enter\":{\"kind\":\"verbatim\"},\"recommend\":{\"kind\":\"none\"},\"initialGroup\":{\"kind\":\"fixed\",\"group\":\"g\"}}"),
        ("capabilities", "[]"),
        ("autoAllow", "[]"),
        ("presentation", "{}"),
    ]

    /// The same shape with two phases, for the rules that need them.
    static let phased: [(String, String)] = flat.map { key, value in
        switch key {
        case "phases": return (key, "[{\"id\":\"one\",\"title\":\"하나\",\"order\":0},{\"id\":\"two\",\"title\":\"둘\",\"order\":1}]")
        case "actions": return (key, "[{\"id\":\"go\",\"title\":\"Go\",\"help\":\"h\",\"prompt\":\"/go\",\"takesText\":false,\"phase\":\"one\"},{\"id\":\"say\",\"title\":\"Say\",\"help\":\"h\",\"prompt\":\"/say {text}\",\"takesText\":true,\"foldText\":\"oneLine\",\"phase\":\"two\"}]")
        case "groups": return (key, "[{\"id\":\"g\",\"title\":\"G\",\"actions\":[\"go\",\"say\"]}]")
        case "rules": return (key, "{\"start\":{\"kind\":\"none\"},\"phase\":{\"kind\":\"lastRecognisedAction\",\"default\":\"one\"},\"next\":{\"kind\":\"byPhase\",\"map\":{\"one\":[\"say\"],\"two\":[\"go\"]}},\"enter\":{\"kind\":\"verbatim\"},\"recommend\":{\"kind\":\"none\"},\"initialGroup\":{\"kind\":\"fixed\",\"group\":\"g\"}}")
        default: return (key, value)
        }
    }

    static func text(_ base: [(String, String)] = flat, _ overrides: [String: String] = [:],
                     drop: [String] = [], extra: [(String, String)] = []) -> String {
        let pairs = base.filter { !drop.contains($0.0) }.map { key, value in (key, overrides[key] ?? value) } + extra
        return "{" + pairs.map { "\"\($0.0)\":\($0.1)" }.joined(separator: ",") + "}"
    }

    static func data(_ base: [(String, String)] = flat, _ overrides: [String: String] = [:],
                     drop: [String] = [], extra: [(String, String)] = []) -> Data {
        Data(text(base, overrides, drop: drop, extra: extra).utf8)
    }

    static func manifest(_ base: [(String, String)] = flat, _ overrides: [String: String] = [:],
                         source: StyleSource = .user) throws -> StyleManifest {
        try StyleManifestDecoder.decode(data(base, overrides), source: source)
    }

    /// The code a manifest is refused with, or nil when it decodes.
    static func code(_ data: Data, source: StyleSource = .user) -> String? {
        do { _ = try StyleManifestDecoder.decode(data, source: source); return nil }
        catch let error as StyleManifestError { return error.code }
        catch { return "E_UNEXPECTED" }
    }

    static func message(_ data: Data, source: StyleSource = .user) -> String? {
        do { _ = try StyleManifestDecoder.decode(data, source: source); return nil }
        catch let error as StyleManifestError { return error.message }
        catch { return nil }
    }

    /// A throwaway directory that never touches the user's real data folder.
    static func temporaryDirectory(_ label: String) -> URL {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("\(label)-\(UUID().uuidString)", isDirectory: true)
        try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }

    static func write(_ data: Data, to url: URL) throws {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try data.write(to: url)
    }

    static func discovered(_ data: Data, source: StyleSource, url: URL, workspacePath: String? = nil) -> DiscoveredStyleFile {
        DiscoveredStyleFile(source: source, url: url, workspacePath: workspacePath, data: data)
    }

    static func registered(_ data: Data, source: StyleSource = .user, path: String = "/tmp/flow.json",
                           workspacePath: String? = nil, approval: StyleApprovalState = .approved) throws -> RegisteredStyle {
        let manifest = try StyleManifestDecoder.decode(data, source: source)
        return RegisteredStyle(manifest: manifest, source: source, path: path, workspacePath: workspacePath,
                               hash: StyleHash.of(data), approval: approval)
    }

    static func bundled(_ id: String) -> RegisteredStyle {
        if let style = BundledStyles.shared.style(id) { return style }
        // A trap here used to be the only CI evidence. Say what was searched
        // and what was rejected so the public annotation names the cause.
        let files = StyleSourceScanner.bundled()
        let rejections = StyleRegistry.make(files: files, approvals: []).rejections
        fatalError("bundled style '\(id)' missing: directories=\(BundledStyleSource.directories().map(\.path)) "
                   + "files=\(files.count) rejections=\(rejections)")
    }
}
