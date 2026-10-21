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

    /// The test-only manifests of `styles/fixtures/`, shared byte for byte with
    /// Windows Core (StyleFixtures.cs) and recorded like the bundled styles
    /// (§8.4). No bundled style declares file or run-event sources or a
    /// catalogue fed by a built-in feature since v7, so the tests that need one
    /// read these. Both are decoded as an approved user style under
    /// `testCapabilities`, the only vocabulary `deck` decodes under.
    static func fixtureData(_ id: String) -> Data {
        let url = StyleGolden.fixturesDirectory.appendingPathComponent(id + ".json")
        do { return try Data(contentsOf: url) } catch { fatalError("fixture manifest unreadable: \(url.path): \(error)") }
    }

    static func fixtureStyle(_ id: String) -> RegisteredStyle {
        do { return try registered(fixtureData(id), path: "/tmp/\(id).json", capabilities: testCapabilities) }
        catch { fatalError("fixture '\(id)' refused: \(error)") }
    }

    /// `styles/fixtures/loop.json`: a four-phase loop that reads its own state
    /// (§1.16) — the newest current checklist under `docs/plans/` as a bar,
    /// `subagent.start` as a count, and two state overrides that move the phase.
    static var statefulStyle: RegisteredStyle { fixtureStyle("loop") }

    /// `styles/fixtures/deck.json`: a catalogue — no phases, two groups drawn
    /// as a map, Enter verbatim, a recommendation and first group read from a
    /// built-in feature that exists only in `testCapabilities` (§1.8), and one
    /// action carrying both flags.
    static var catalogueStyle: RegisteredStyle { fixtureStyle("deck") }

    /// The app's own vocabulary of built-in features is empty since v7 (§1.8).
    /// The engine still reads the `capabilities` field and the two rules that
    /// name a feature, so their checks run against this one instead.
    static let testCapabilities: [String: [String]] = ["acme.thing": ["absent", "open", "complete"]]

    static func manifest(_ base: [(String, String)] = flat, _ overrides: [String: String] = [:],
                         source: StyleSource = .user,
                         capabilities: [String: [String]] = StyleCapabilityID.vocabulary) throws -> StyleManifest {
        try StyleManifestDecoder.decode(data(base, overrides), source: source, capabilities: capabilities)
    }

    /// The code a manifest is refused with, or nil when it decodes.
    static func code(_ data: Data, source: StyleSource = .user,
                     capabilities: [String: [String]] = StyleCapabilityID.vocabulary) -> String? {
        do { _ = try StyleManifestDecoder.decode(data, source: source, capabilities: capabilities); return nil }
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
                           workspacePath: String? = nil, approval: StyleApprovalState = .approved,
                           capabilities: [String: [String]] = StyleCapabilityID.vocabulary) throws -> RegisteredStyle {
        let manifest = try StyleManifestDecoder.decode(data, source: source, capabilities: capabilities)
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
