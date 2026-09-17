import Foundation
import Testing
import Darwin
@testable import MightyCore

struct ResultFileReferenceTests {
    private final class Fixture {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("result-files-" + UUID().uuidString)
        let outside = FileManager.default.temporaryDirectory.appendingPathComponent("result-files-outside-" + UUID().uuidString)
        init() throws {
            try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
            try FileManager.default.createDirectory(at: outside, withIntermediateDirectories: true)
        }
        deinit { try? FileManager.default.removeItem(at: root); try? FileManager.default.removeItem(at: outside) }
        @discardableResult func file(_ path: String, data: Data = Data("fixture".utf8)) throws -> URL {
            let url = root.appendingPathComponent(path)
            try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
            try data.write(to: url); return url
        }
        func files(_ texts: String...) -> [ReferenceFile] { ReferenceLinkSupport.resultFiles(in: texts, root: root) }
    }

    @Test func canonicalAliasesAndLinesDeduplicateInFirstMentionOrderAcrossResults() throws {
        let fixture = try Fixture()
        let first = try fixture.file("docs/first.md")
        let second = try fixture.file("src/second.swift")
        try FileManager.default.createSymbolicLink(at: fixture.root.appendingPathComponent("docs/alias.md"), withDestinationURL: first)
        let files = fixture.files("먼저 src/second.swift:42, 이어서 docs/alias.md:7.",
                                  "\(second.path):12 [first](docs/first.md) ./docs/first.md:80 \(first.path)")
        #expect(files.map(\.path) == ["src/second.swift", "docs/first.md"])
        #expect(files.map(\.line) == [42, 7])
        #expect(files[0].url == ReferenceLinkSupport.resolve("src/second.swift", root: fixture.root))
        #expect(files[1].id == ReferenceLinkSupport.resolve("docs/first.md", root: fixture.root)?.path)
        #expect(Set(files.map(\.id)).count == 2)
    }

    @Test func explicitMarkdownLinksSupportBareNamesSpacesEncodingFileURLsAndReferenceSyntax() throws {
        let fixture = try Fixture()
        try fixture.file("README.md")
        try fixture.file("docs/한글 문서.md")
        let image = try fixture.file("images/local image.png", data: Data([0, 1, 2, 3]))
        try fixture.file("docs/other.md")
        try fixture.file("docs/parentheses(1).md")
        let source = """
        [README](README.md)
        [문서](<docs/한글 문서.md>)
        [이미지](\(image.absoluteString))
        [encoded duplicate](docs/%ED%95%9C%EA%B8%80%20%EB%AC%B8%EC%84%9C.md)
        [reference label][ref]
        [parentheses](docs/parentheses\\(1\\).md)

        [ref]: docs/other.md "reference title"
        """
        #expect(fixture.files(source).map(\.path) == ["README.md", "docs/한글 문서.md", "images/local image.png", "docs/other.md", "docs/parentheses(1).md"])
    }

    @Test func markdownLabelsWebLinksAndImageDestinationsDoNotCreateFalseLocalFiles() throws {
        let fixture = try Fixture()
        try fixture.file("docs/label.md"); try fixture.file("docs/destination.md"); try fixture.file("docs/image.png")
        let source = """
        [docs/label.md](https://example.com/docs/destination.md)
        [label](https://example.com/docs/label.md)
        ![Illustration](docs/image.png)
        [docs/label.md](docs/destination.md)
        """
        #expect(fixture.files(source).map(\.path) == ["docs/destination.md"])
    }

    @Test func existingRegularReadableFilesOnlyAndWorkspaceBoundaryAreEnforced() throws {
        let fixture = try Fixture()
        try fixture.file("docs/valid.md")
        try fixture.file("docs/unreadable.md")
        try FileManager.default.setAttributes([.posixPermissions: 0o000], ofItemAtPath: fixture.root.appendingPathComponent("docs/unreadable.md").path)
        let secret = fixture.outside.appendingPathComponent("secret.md")
        try Data("outside".utf8).write(to: secret)
        try FileManager.default.createSymbolicLink(at: fixture.root.appendingPathComponent("docs/escape.md"), withDestinationURL: secret)
        let pipe = fixture.root.appendingPathComponent("docs/pipe.md")
        #expect(mkfifo(pipe.path, 0o600) == 0)
        let source = "[directory](docs) docs/missing.md docs/escape.md docs/pipe.md docs/unreadable.md \(secret.path) [host](file://server/docs/valid.md) [script](javascript:alert(1)) docs/valid.md"
        #expect(fixture.files(source).map(\.path) == ["docs/valid.md"])
        #expect(ReferenceLinkSupport.resolve("docs/pipe.md", root: fixture.root) == nil)
        #expect(ReferenceLinkSupport.resultFiles(in: [source], root: nil).isEmpty)
        #expect(ReferenceLinkSupport.resultFiles(in: [source], root: URL(string: "https://example.com/workspace")).isEmpty)
    }

    @Test func markdownLineVariantsShareIdentityButLiteralColonFilenameWins() throws {
        let fixture = try Fixture()
        try fixture.file("docs/report.md")
        try fixture.file("docs/literal.md:12")
        let files = fixture.files("[report](docs/report.md:25) docs/report.md:50 [same](docs/report.md#section) [colon](docs/literal.md:12)")
        #expect(files.map(\.path) == ["docs/report.md", "docs/literal.md:12"])
        #expect(files.map(\.line) == [25, nil])
    }

    @Test func extractionBudgetsCapFilesAndTextWithoutReadingFileContents() throws {
        let fixture = try Fixture()
        for index in 0..<(ReferenceLinkSupport.maximumResultFiles + 5) { try fixture.file("docs/file\(index).md") }
        let text = (0..<(ReferenceLinkSupport.maximumResultFiles + 5)).map { "docs/file\($0).md" }.joined(separator: " ")
        let files = fixture.files(text)
        #expect(files.count == ReferenceLinkSupport.maximumResultFiles)
        #expect(files.first?.path == "docs/file0.md")
        #expect(files.last?.path == "docs/file99.md")
        #expect(fixture.files(String(repeating: "x", count: ReferenceLinkSupport.maximumResultTextBytes), "docs/file0.md").isEmpty)
        let emptyTexts = Array(repeating: "", count: ReferenceLinkSupport.maximumResultTexts) + ["docs/file0.md"]
        #expect(ReferenceLinkSupport.resultFiles(in: emptyTexts, root: fixture.root).isEmpty)
        try fixture.file("docs/large.bin", data: Data(repeating: 0, count: ReferenceLinkSupport.maximumFileBytes + 1))
        #expect(fixture.files("docs/large.bin").map(\.path) == ["docs/large.bin"])
    }
}
