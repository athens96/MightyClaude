import CoreGraphics
import Foundation
import Testing
@testable import MightyCore

/// native/contracts/fixtures/flow-vectors.json is the role view's layout (`MightyGraphLayout.make(fanOut:)`) for the same runs on every
/// platform that lays it out: the Windows Core (native/windows/MightyCode.Core.Tests/FlowVectorVerification.cs) and the Windows web page
/// (native/windows/web/src/mighty/flow.test.ts) assert these frames, and this suite runs the Mac's layout on the same cases, as they do:
/// the runs as written, `splitsExecution` on, no draft, nothing expanded, `opened` naming the node suffixes of cards the user opened.
/// Every node with its kind, frame and card, every edge, the advisor's dotted links, the canvas size and origin, within 0.001. A case that
/// differs fails with each difference named (a node's id and how far it moved, a missing or extra edge or link, the size or origin).
struct FlowVectorTests {
    private static let url = GraphAgentIdentityTests.root.appendingPathComponent("native/contracts/fixtures/flow-vectors.json")

    private static func cases() throws -> [[String: Any]] {
        let document = try #require(try JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? [String: Any])
        return try #require(document["cases"] as? [[String: Any]])
    }

    /// The vectors' name for what a node holds (the Windows layouts' `kind`).
    static func kind(_ content: MightyGraphLayout.Content) -> String {
        switch content {
        case .request: return "request"
        case .agent: return "agent"
        case .result: return "result"
        case .resultFiles: return "resultFiles"
        case .execution: return "execution"
        case .images: return "images"
        case .draft: return "draft"
        case .work: return "work"
        case .history: return "history"
        case .plan: return "plan"
        case .planRecord: return "planRecord"
        case .delegate: return "delegate"
        case .returning: return "return"
        case .advisor: return "advisor"
        }
    }

    private static func number(_ value: Any?) -> CGFloat { CGFloat((value as? NSNumber)?.doubleValue ?? .nan) }
    private static func near(_ actual: CGFloat, _ expected: CGFloat) -> Bool { abs(actual - expected) <= 0.001 }
    private static func signed(_ value: CGFloat) -> String { (value > 0 ? "+" : "") + String(format: "%g", Double(value)) }

    /// The Mac's layout of a case.
    static func layout(_ item: [String: Any]) throws -> MightyGraphLayout {
        let runs = try JSONDecoder().decode([MightyGraphRun].self, from: JSONSerialization.data(withJSONObject: item["runs"] ?? []))
        let opened = Set((item["opened"] as? [String] ?? []).flatMap { suffix in runs.map { MightyGraphLayout.nodeID($0, suffix: suffix) } })
        return MightyGraphLayout.make(runs: runs, draft: "", running: item["running"] as? Bool ?? false, expanded: [], splitsExecution: true,
                                      fanOut: .init(opened: opened))
    }

    /// How the Mac's layout of a case differs from its committed expectation, a line each; empty when it lays out the same.
    static func differences(_ item: [String: Any]) throws -> [String] {
        let layout = try layout(item)
        let expect = try #require(item["expect"] as? [String: Any])
        var found: [String] = []
        let wanted = expect["nodes"] as? [[String: Any]] ?? []
        let wantedIDs = wanted.compactMap { $0["id"] as? String }
        let laidOut = layout.nodes.map(\.id)
        if laidOut != wantedIDs {
            let missing = wantedIDs.filter { !laidOut.contains($0) }, extra = laidOut.filter { !wantedIDs.contains($0) }
            found.append("nodes: expected [\(wantedIDs.joined(separator: ", "))], laid out [\(laidOut.joined(separator: ", "))]"
                         + (missing.isEmpty ? "" : "; missing \(missing)") + (extra.isEmpty ? "" : "; extra \(extra)"))
        }
        for want in wanted {
            guard let id = want["id"] as? String, let node = layout.nodes.first(where: { $0.id == id }) else { continue }
            if let kind = want["kind"] as? String, Self.kind(node.content) != kind { found.append("\(id): kind \(kind) expected, \(Self.kind(node.content)) laid out") }
            if node.isCompact != (want["compact"] as? Bool ?? false) { found.append("\(id): card \(want["compact"] as? Bool ?? false) expected, \(node.isCompact) laid out") }
            let frame = node.frame
            let deltas = [("x", frame.minX, number(want["x"])), ("y", frame.minY, number(want["y"])), ("w", frame.width, number(want["w"])), ("h", frame.height, number(want["h"]))]
                .filter { !near($0.1, $0.2) }.map { "\($0.0) \(signed($0.1 - $0.2)) (\(String(format: "%g", Double($0.1))) for \(String(format: "%g", Double($0.2))))" }
            if !deltas.isEmpty { found.append("\(id): " + deltas.joined(separator: ", ")) }
        }
        let edges = (expect["edges"] as? [[String: Any]] ?? []).map { "\($0["source"] as? String ?? "")→\($0["target"] as? String ?? "")\(($0["joins"] as? Bool ?? false) ? " joins" : "")" }
        let laidEdges = layout.edges.map { "\($0.source)→\($0.target)\($0.joins ? " joins" : "")" }
        if laidEdges != edges {
            let missing = edges.filter { !laidEdges.contains($0) }, extra = laidEdges.filter { !edges.contains($0) }
            found.append("edges:" + (missing.isEmpty ? "" : " missing \(missing)") + (extra.isEmpty ? "" : " extra \(extra)") + (missing.isEmpty && extra.isEmpty ? " in another order" : ""))
        }
        let links = (expect["advisorLinks"] as? [[String: Any]] ?? []).map { "\($0["band"] as? String ?? "")⇢\($0["target"] as? String ?? "")" }
        let laidLinks = layout.advisorLinks.map { "\($0.band)⇢\($0.target)" }
        if laidLinks != links { found.append("advisor links: expected \(links), laid out \(laidLinks)") }
        let size = expect["size"] as? [String: Any] ?? [:]
        for (label, actual, expected) in [("width", layout.size.width, number(size["w"])), ("height", layout.size.height, number(size["h"])),
                                          ("origin x", layout.originX, number(expect["originX"])), ("origin y", layout.originY, number(expect["originY"]))]
        where !near(actual, expected) {
            found.append("\(label) \(signed(actual - expected)) (\(String(format: "%g", Double(actual))) for \(String(format: "%g", Double(expected))))")
        }
        return found
    }

    @Test func theMacLaysOutTheSharedCasesAsWindowsDoes() throws {
        let cases = try Self.cases()
        #expect(cases.count >= 7, "the fixture has its cases")
        try LocaleOverride.$language.withValue(.ko) { () throws in
            for item in cases {
                let found = try Self.differences(item)
                #expect(found.isEmpty, "\(item["name"] as? String ?? "?"):\n\(found.joined(separator: "\n"))")
            }
        }
    }
}
