import Foundation
import Testing
@testable import MightyCore

struct ReferenceLinkTests {
    private func paths(_ text: String) -> [String] { ReferenceLinkSupport.matches(in: text).filter { !$0.isWeb }.map(\.path) }
    private func webs(_ text: String) -> [String] { ReferenceLinkSupport.matches(in: text).filter(\.isWeb).map(\.path) }

    @Test func detectsRepoPathsAndAddressesButNotLookalikes() {
        #expect(paths("문서: docs/mighty-mode.md, 계약은 `native/contracts/README.md`를 보세요.") == ["docs/mighty-mode.md", "native/contracts/README.md"])
        #expect(paths("다이어그램 /Users/me/Work/App/artifacts/reports/graph.html (열기)") == ["/Users/me/Work/App/artifacts/reports/graph.html"])
        #expect(paths("relative ./scripts/build.sh and ../shared/types.ts.") == ["./scripts/build.sh", "../shared/types.ts"])
        #expect(paths("archive at data/a.md.bak") == ["data/a.md.bak"])
        let lined = ReferenceLinkSupport.matches(in: "see src/App.tsx:42 now")
        #expect(lined.map(\.path) == ["src/App.tsx"]); #expect(lined.first?.line == 42)
        #expect(paths("version 1.2.3, and/or, 50/50, README.md alone, http://x.io/docs/a.md").isEmpty)
        #expect(webs("http://x.io/docs/a.md and https://example.com/path?q=1. Done") == ["http://x.io/docs/a.md", "https://example.com/path?q=1"])
        #expect(webs("markdown (https://example.com/a) [x](https://example.com/b)") == ["https://example.com/a", "https://example.com/b"])
        let ordered = ReferenceLinkSupport.matches(in: "b/second.md then https://a.io then a/first.md")
        #expect(ordered.map(\.path) == ["b/second.md", "https://a.io", "a/first.md"])
        #expect(ReferenceLinkSupport.matches(in: "docs/" + String(repeating: "x", count: 1_100) + ".md").isEmpty)
    }

    @Test func markdownLinkPathsAreLocalOnlyForRelativeAndFileURLs() {
        #expect(ReferenceLinkSupport.localPath(URL(string: "docs/mighty-mode.md")!) == "docs/mighty-mode.md")
        #expect(ReferenceLinkSupport.localPath(URL(string: "file:///tmp/report.html")!) == "/tmp/report.html")
        #expect(ReferenceLinkSupport.localPath(URL(string: "https://example.com/a.md")!) == nil)
        #expect(ReferenceLinkSupport.localPath(URL(string: "javascript:alert(1)")!) == nil)
        #expect(ReferenceLinkSupport.localPath(URL(string: "file://host/share/a.md")!) == nil)
    }

    @Test func resolutionStaysInsideTheWorkspaceRoot() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("reference-root-" + UUID().uuidString)
        let outside = FileManager.default.temporaryDirectory.appendingPathComponent("reference-outside-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: root.appendingPathComponent("docs"), withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: outside, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root); try? FileManager.default.removeItem(at: outside) }
        try Data("# hi".utf8).write(to: root.appendingPathComponent("docs/a.md"))
        try Data("secret".utf8).write(to: outside.appendingPathComponent("secret.txt"))
        try FileManager.default.createSymbolicLink(at: root.appendingPathComponent("docs/escape.txt"), withDestinationURL: outside.appendingPathComponent("secret.txt"))
        let resolvedRoot = root.standardizedFileURL.resolvingSymlinksInPath()
        #expect(ReferenceLinkSupport.resolve("docs/a.md", root: root)?.path == resolvedRoot.appendingPathComponent("docs/a.md").path)
        #expect(ReferenceLinkSupport.resolve("./docs/../docs/a.md", root: root) != nil)
        #expect(ReferenceLinkSupport.resolve(resolvedRoot.appendingPathComponent("docs/a.md").path, root: root) != nil)
        #expect(ReferenceLinkSupport.resolve("docs", root: root) == nil)
        #expect(ReferenceLinkSupport.resolve("docs/missing.md", root: root) == nil)
        #expect(ReferenceLinkSupport.resolve("../" + outside.lastPathComponent + "/secret.txt", root: root) == nil)
        #expect(ReferenceLinkSupport.resolve(outside.appendingPathComponent("secret.txt").path, root: root) == nil)
        #expect(ReferenceLinkSupport.resolve("docs/escape.txt", root: root) == nil)
        #expect(ReferenceLinkSupport.resolve("docs/a.md", root: nil) == nil)
        #expect(ReferenceLinkSupport.resolve("", root: root) == nil)
    }

    @Test func previewKindsAndInAppLinkRoundTrip() throws {
        #expect(ReferenceLinkSupport.kind(of: URL(fileURLWithPath: "/a/b.MD")) == "markdown")
        #expect(ReferenceLinkSupport.kind(of: URL(fileURLWithPath: "/a/graph.html")) == "html")
        #expect(ReferenceLinkSupport.kind(of: URL(fileURLWithPath: "/a/icon.png")) == "image")
        #expect(ReferenceLinkSupport.kind(of: URL(fileURLWithPath: "/a/main.swift")) == "text")
        let url = try #require(ReferenceLinkSupport.referenceURL(path: "docs/한글 문서.md", line: 7))
        #expect(url.scheme == "mighty-transcript")
        let parsed = try #require(ReferenceLinkSupport.parseReferenceURL(url))
        #expect(parsed.path == "docs/한글 문서.md"); #expect(parsed.line == 7)
        #expect(ReferenceLinkSupport.parseReferenceURL(URL(string: "mighty-transcript://activity?id=1")!) == nil)
        #expect(ReferenceLinkSupport.parseReferenceURL(URL(string: "https://example.com/?path=a.md")!) == nil)
    }
}
