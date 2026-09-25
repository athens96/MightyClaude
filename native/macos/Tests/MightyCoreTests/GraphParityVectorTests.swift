import Foundation
import Testing
@testable import MightyCore

/// native/contracts/graph-vectors.json is macOS truth for the execution graph:
/// this suite re-derives every committed expectation from the committed inputs
/// by running the Swift implementation, so the file the Windows port reads can
/// never drift from what macOS actually produces.
///
/// Regenerating the expectations (never run during verification):
///   MIGHTY_GRAPH_VECTORS_WRITE=1 bash scripts/test-native-macos.sh \
///     --scratch-path /tmp/graph-vectors --filter GraphParityVectorTests
/// That run rewrites every `expected` from the Swift implementation and leaves
/// the authored inputs alone. Review the diff before committing it.
///
/// The suite is serialized because the capsule group reads localized copy and
/// the locale cache is process-wide shared state.
@Suite(.serialized)
struct GraphParityVectorTests {
    private static let regenerating = ProcessInfo.processInfo.environment["MIGHTY_GRAPH_VECTORS_WRITE"] == "1"

    private func document() throws -> [String: Any] {
        let data = try Data(contentsOf: GraphVectors.fixtureURL)
        return try #require(try JSONSerialization.jsonObject(with: data) as? [String: Any])
    }

    /// Every case runs with the Korean locale: the committed display strings
    /// are the ko copy the Windows port must reproduce from its locale files.
    private func withKoreanLocale<T>(_ body: () throws -> T) rethrows -> T {
        let previous = UserDefaults.standard.string(forKey: "language")
        UserDefaults.standard.set("ko", forKey: "language")
        resetLocaleCache()
        defer {
            if let previous { UserDefaults.standard.set(previous, forKey: "language") }
            else { UserDefaults.standard.removeObject(forKey: "language") }
            resetLocaleCache()
        }
        return try body()
    }

    @Test func theFileMeetsEveryMinimumCount() throws {
        let doc = try document()
        #expect(doc["version"] as? Int == 1)
        for (group, minimum) in GraphVectors.minimumCounts {
            let cases = try #require(doc[group] as? [[String: Any]], "missing group \(group)")
            #expect(cases.count >= minimum, "group \(group) has \(cases.count) cases, needs \(minimum)")
            var names = Set<String>()
            for value in cases {
                let name = try #require(value["name"] as? String, "a \(group) case has no name")
                #expect(names.insert(name).inserted, "duplicate \(group) case name \(name)")
                #expect(value["expected"] != nil, "\(group)/\(name) has no expected value")
            }
        }
    }

    @Test func swiftReproducesEveryCommittedExpectation() throws {
        var doc = try document()
        var mismatches: [String] = []
        var checked = 0
        try withKoreanLocale {
            for (group, _) in GraphVectors.minimumCounts {
                guard var cases = doc[group] as? [[String: Any]] else { continue }
                for index in cases.indices {
                    let name = cases[index]["name"] as? String ?? "#\(index)"
                    let produced = try GraphVectors.expected(group: group, case: cases[index])
                    if Self.regenerating {
                        cases[index]["expected"] = produced
                        continue
                    }
                    checked += 1
                    let committed = try GraphVectors.canonical(cases[index]["expected"] ?? NSNull())
                    let fresh = try GraphVectors.canonical(produced)
                    if committed != fresh {
                        mismatches.append("\(group)/\(name)\n  committed: \(committed.prefix(600))\n  swift:     \(fresh.prefix(600))")
                    }
                }
                doc[group] = cases
            }
        }
        if Self.regenerating {
            let data = try JSONSerialization.data(withJSONObject: doc, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes])
            try (data + Data("\n".utf8)).write(to: GraphVectors.fixtureURL)
            print("regenerated \(GraphVectors.fixtureURL.lastPathComponent)")
            return
        }
        let report = mismatches.joined(separator: "\n")
        #expect(mismatches.isEmpty, "vectors no longer match the Swift implementation:\n\(report)")
        // The file is only truth if it actually exercised the implementation.
        #expect(checked >= 50, "only \(checked) cases were checked")
    }

    // A handful of named expectations so the suite still says something
    // concrete if the file were ever replaced wholesale.
    @Test func namedExpectationsPinTheContract() throws {
        let doc = try document()
        let claude = try #require(doc["claudeStream"] as? [[String: Any]])
        let usageCase = try #require(claude.first { $0["name"] as? String == "main-usage-split-across-events-counted-once" })
        let nodes = try #require((usageCase["expected"] as? [String: Any])?["nodes"] as? [[String: Any]])
        let main = try #require(nodes.first)
        #expect(main["kind"] as? String == "main")
        #expect(main["output"] as? String == "The repo builds two native apps.")
        // One message is re-sent with a larger figure; it is counted once.
        let usage = try #require(main["usage"] as? [String: Any])
        #expect(usage["inputTokens"] as? Int == 150)
        #expect(usage["outputTokens"] as? Int == 60)
        #expect((main["responseRecords"] as? [[String: Any]])?.count == 2)

        let layout = try #require(doc["layout"] as? [[String: Any]])
        let draftOnly = try #require(layout.first { $0["name"] as? String == "empty-graph-shows-only-the-draft-block" })
        let draftNodes = try #require((draftOnly["expected"] as? [String: Any])?["nodes"] as? [[String: Any]])
        #expect(draftNodes.count == 1)
        #expect(draftNodes[0]["id"] as? String == MightyGraphCamera.pendingNodeID)

        let capsule = try #require(doc["capsule"] as? [[String: Any]])
        let configured = try #require(capsule.first { $0["name"] as? String == "node-model-label-marks-a-configured-name" })
        #expect(configured["expected"] as? String == "claude-opus-5 · 설정")

        let files = try #require(doc["resultFiles"] as? [[String: Any]])
        let dedup = try #require(files.first { $0["name"] as? String == "duplicates-and-paths-outside-the-workspace-are-dropped" })
        #expect((dedup["expected"] as? [String: Any])?["paths"] as? [String] == ["docs/a.md"])
    }

    // MARK: - Suite marker

    @Test func markerGraphParityVectorsOK() {
        print("Suite GraphParityVectorTests passed")
    }
}
