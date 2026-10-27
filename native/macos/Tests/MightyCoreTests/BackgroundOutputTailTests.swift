import Foundation
import Testing
@testable import MightyCore

/// The tail rule the Windows Core and the phone read from the same file: the
/// last lines of a background run's output file, as a host shows them.
struct BackgroundOutputTailTests {
    static let url: URL = {
        var url = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { url.deleteLastPathComponent() }
        return url.appendingPathComponent("native/contracts/fixtures/background-output.json")
    }()

    static func load() throws -> [String: Any] {
        try #require(JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? [String: Any])
    }

    @Test func theNumbersAreTheFixtures() throws {
        let fixture = try Self.load()
        #expect(BackgroundOutputTail.maxBytes == fixture["maxBytes"] as? Int)
        #expect(BackgroundOutputTail.maxLines == fixture["maxLines"] as? Int)
        #expect(BackgroundOutputTail.maxLineChars == fixture["maxLineChars"] as? Int)
    }

    @Test func everyCaseReadsAsTheFixtureSays() throws {
        let cases = try #require(try Self.load()["cases"] as? [[String: Any]])
        #expect(!cases.isEmpty)
        for item in cases {
            let name = item["name"] as? String ?? "unnamed"
            let base64 = try #require(item["base64"] as? String, "\(name)")
            let data = try #require(Data(base64Encoded: base64), "\(name)")
            let truncated = try #require(item["truncated"] as? Bool, "\(name)")
            let expected = try #require(item["lines"] as? [String], "\(name)")
            #expect(BackgroundOutputTail.lines(data, truncated: truncated) == expected, "\(name)")
        }
    }

    /// A file longer than the window: its cut first line is dropped and only the last lines stay.
    @Test func aLongFileReadsItsLastWholeLines() throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("mc-output-tail-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        let file = folder.appendingPathComponent("b1.output")
        // 1000 lines of 9 bytes: 9000 bytes, so the last 8192 start inside a line.
        let text = (1...1000).map { String(format: "row-%04d\n", $0) }.joined()
        #expect(text.utf8.count == 9000)
        try Data(text.utf8).write(to: file)
        let lines = try #require(BackgroundOutputTail.read(path: file.path))
        #expect(lines.count <= BackgroundOutputTail.maxLines)
        #expect(lines.allSatisfy { $0.count == 8 && $0.hasPrefix("row-") })
        #expect(lines == (995...1000).map { String(format: "row-%04d", $0) })
    }

    @Test func aMissingFileReadsNothing() {
        let path = FileManager.default.temporaryDirectory.appendingPathComponent("mc-output-tail-missing-\(UUID().uuidString).output").path
        #expect(BackgroundOutputTail.read(path: path) == nil)
    }
}
