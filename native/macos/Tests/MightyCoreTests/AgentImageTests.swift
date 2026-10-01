import CoreGraphics
import Foundation
import ImageIO
import Testing
@testable import MightyCore

/// Pictures agents show: kept from stream-json / exec JSON as references in a
/// content-addressed cache, Markdown pictures under the path rule, the graph's
/// preview block, and the phone's payload without them.
@Suite(.serialized)
final class AgentImageTests {
    private var directories: [URL] = []
    deinit { for directory in directories { try? FileManager.default.removeItem(at: directory) } }
    private func temporary(_ name: String = "dir") throws -> URL {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-images-\(name)-" + UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true); directories.append(url)
        return WorkspaceFiles.realRoot(url)
    }

    private func png(width: Int = 8, height: Int = 6, red: CGFloat = 1) -> Data {
        let context = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
                                space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        context.setFillColor(red: red, green: 0.4, blue: 0.2, alpha: 1)
        context.fill(CGRect(x: 0, y: 0, width: width, height: height))
        let data = NSMutableData()
        let destination = CGImageDestinationCreateWithData(data, "public.png" as CFString, 1, nil)!
        CGImageDestinationAddImage(destination, context.makeImage()!, nil)
        CGImageDestinationFinalize(destination)
        return data as Data
    }

    /// A PNG whose header claims `width` × `height` pixels; ImageIO reads the
    /// size from IHDR without decoding the (absent) pixels.
    private func pngHeader(width: UInt32, height: UInt32) -> Data {
        func crc32(_ bytes: [UInt8]) -> UInt32 {
            var crc: UInt32 = 0xFFFF_FFFF
            for byte in bytes {
                crc ^= UInt32(byte)
                for _ in 0..<8 { crc = crc & 1 == 1 ? (crc >> 1) ^ 0xEDB8_8320 : crc >> 1 }
            }
            return ~crc
        }
        func be(_ value: UInt32) -> [UInt8] { [UInt8(value >> 24), UInt8(value >> 16 & 0xFF), UInt8(value >> 8 & 0xFF), UInt8(value & 0xFF)] }
        func chunk(_ type: String, _ body: [UInt8]) -> [UInt8] {
            let typed = Array(type.utf8) + body
            return be(UInt32(body.count)) + typed + be(crc32(typed))
        }
        let header = be(width) + be(height) + [8, 2, 0, 0, 0]
        return Data([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A] + chunk("IHDR", header) + chunk("IDAT", [0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01]) + chunk("IEND", []))
    }

    private func send(_ value: [String: Any], to parser: CLIStreamParser) throws {
        var data = try JSONSerialization.data(withJSONObject: value)
        data.append(10); parser.push(data)
    }
    private func claudeImage(_ data: Data, type: String = "image/png") -> [String: Any] {
        ["type": "image", "source": ["type": "base64", "media_type": type, "data": data.base64EncodedString()]]
    }

    // MARK: Stream parsing

    @Test func claudeReadAndMcpScreenshotResultsBecomeImageEntriesWithoutBase64() throws {
        let cache = AgentImageCache(directory: try temporary("cache"))
        var entries: [LogEntry] = []; var logs: [(String, String)] = []; var activities: [AgentActivity] = []
        let parser = CLIStreamParser(provider: "claude", log: { logs.append(($0, $1)) }, resume: { _ in }, activityNamespace: "run-images",
                                     activity: { activities.append($0) }, images: cache, imageEntry: { entries.append($0) })
        let shot = png(width: 12, height: 9)
        let screenshot = png(width: 20, height: 10, red: 0.2)
        try send(["type": "assistant", "uuid": "a1", "message": ["id": "m1", "content": [
            ["type": "tool_use", "id": "read-1", "name": "Read", "input": ["file_path": "/w/shot.png"]],
            ["type": "tool_use", "id": "mcp-1", "name": "mcp__playwright__browser_take_screenshot", "input": [:]],
        ]]], to: parser)
        // Claude also echoes the file in `tool_use_result`; only the content block counts.
        try send(["type": "user", "tool_use_result": ["type": "image", "file": ["base64": shot.base64EncodedString()]], "message": ["content": [
            ["type": "tool_result", "tool_use_id": "read-1", "content": [claudeImage(shot)]],
        ]]], to: parser)
        try send(["type": "user", "message": ["content": [
            ["type": "tool_result", "tool_use_id": "mcp-1", "content": [["type": "text", "text": "Took a screenshot"], claudeImage(screenshot)]],
        ]]], to: parser)
        parser.flush()

        #expect(entries.count == 2)
        let read = try #require(entries.first)
        #expect(read.kind == "image"); #expect(read.provider == "claude")
        let readRef = try #require(read.images?.first)
        #expect(readRef.hash == AgentImageSupport.sha256(shot))
        #expect(readRef.mediaType == "image/png"); #expect(readRef.width == 12); #expect(readRef.height == 9)
        #expect(readRef.bytes == shot.count); #expect(readRef.path == "/w/shot.png")
        #expect(readRef.source.contains("Read"))
        #expect(cache.data(for: readRef) == shot)
        let mcp = try #require(entries.last?.images?.first)
        #expect(mcp.source.contains("mcp__playwright__browser_take_screenshot"))
        #expect(mcp.width == 20)
        // Neither the entries nor the tool rows carry the picture's bytes.
        let encoded = String(decoding: try JSONEncoder().encode(entries), as: UTF8.self) + String(decoding: try JSONEncoder().encode(activities), as: UTF8.self)
        #expect(!encoded.contains(shot.base64EncodedString().prefix(40)))
        #expect(!encoded.contains(screenshot.base64EncodedString().prefix(40)))
        #expect(activities.last(where: { $0.toolName == "mcp__playwright__browser_take_screenshot" })?.output == "Took a screenshot")
        #expect(logs.isEmpty)
    }

    @Test func withoutACacheImagesAreLeftOutAsBefore() throws {
        var entries: [LogEntry] = []
        let parser = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in }, imageEntry: { entries.append($0) })
        try send(["type": "user", "message": ["content": [["type": "tool_result", "tool_use_id": "t", "content": [claudeImage(png())]]]]], to: parser)
        #expect(entries.isEmpty)
    }

    @Test func refusedPicturesLeaveANoticeAndNoEntry() throws {
        let cache = AgentImageCache(directory: try temporary("cache"))
        var entries: [LogEntry] = []; var logs: [String] = []
        let parser = CLIStreamParser(provider: "claude", log: { logs.append($1) }, resume: { _ in }, images: cache, imageEntry: { entries.append($0) })
        let svg = Data(#"<svg xmlns="http://www.w3.org/2000/svg"><image href="file:///etc/passwd"/></svg>"#.utf8)
        try send(["type": "user", "message": ["content": [["type": "tool_result", "tool_use_id": "svg", "content": [claudeImage(svg, type: "image/svg+xml")]]]]], to: parser)
        try send(["type": "user", "message": ["content": [["type": "tool_result", "tool_use_id": "text", "content": [claudeImage(Data("not a picture".utf8))]]]]], to: parser)
        #expect(entries.isEmpty)
        // Other suites switch the app language while this runs, so compare
        // the notices by count and position rather than by localized text.
        #expect(logs.count == 2)
        #expect(cache.totalBytes == 0)
    }

    @Test func aChildAgentsPicturesJoinItsGraphBlockNotTheTranscript() throws {
        let cache = AgentImageCache(directory: try temporary("cache"))
        var entries: [LogEntry] = []; var nodes: [ExecutionGraphNode] = []
        let parser = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in }, activityNamespace: "run-child",
                                     activity: { _ in }, graph: { nodes.append($0) }, graphInput: "look", images: cache, imageEntry: { entries.append($0) })
        try send(["type": "assistant", "uuid": "a1", "message": ["id": "m1", "content": [
            ["type": "tool_use", "id": "agent-1", "name": "Agent", "input": ["description": "Look", "prompt": "Look at the page"]]]]], to: parser)
        try send(["type": "assistant", "uuid": "a2", "parent_tool_use_id": "agent-1", "message": ["id": "m2", "content": [
            ["type": "tool_use", "id": "read-1", "name": "Read", "input": ["file_path": "/w/a.png"]]]]], to: parser)
        try send(["type": "user", "parent_tool_use_id": "agent-1", "message": ["content": [
            ["type": "tool_result", "tool_use_id": "read-1", "content": [claudeImage(png())]]]]], to: parser)
        #expect(entries.isEmpty)
        let childID = ExecutionGraphSupport.agentNodeID(runId: "run-child", toolUseId: "agent-1")
        let child = try #require(nodes.last(where: { $0.id == childID }))
        let image = try #require(child.entries.first(where: { $0.kind == "image" }))
        #expect(image.images?.first?.hash == AgentImageSupport.sha256(png()))
        // The child's tool row comes before its pictures.
        #expect(child.entries.firstIndex(where: { $0.activity?.toolName == "Read" })! < child.entries.firstIndex(where: { $0.kind == "image" })!)
    }

    @Test func aChildsToolPicturesStayOutOfTheTranscriptEvenWhenItsToolIsUnknown() throws {
        let cache = AgentImageCache(directory: try temporary("cache"))
        var entries: [LogEntry] = []; var nodes: [ExecutionGraphNode] = []
        let parser = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in }, activityNamespace: "run-unbound",
                                     activity: { _ in }, graph: { nodes.append($0) }, graphInput: "look", images: cache, imageEntry: { entries.append($0) })
        try send(["type": "assistant", "uuid": "a1", "message": ["id": "m1", "content": [
            ["type": "tool_use", "id": "agent-1", "name": "Agent", "input": ["description": "Look", "prompt": "Look at the page"]]]]], to: parser)
        // The child's tool call was never seen (evicted, or its row arrived elsewhere).
        try send(["type": "user", "parent_tool_use_id": "agent-1", "message": ["content": [
            ["type": "tool_result", "tool_use_id": "never-seen", "content": [claudeImage(png())]]]]], to: parser)
        #expect(entries.isEmpty)
        let childID = ExecutionGraphSupport.agentNodeID(runId: "run-unbound", toolUseId: "agent-1")
        #expect(nodes.last(where: { $0.id == childID })?.entries.contains { $0.kind == "image" } == true)
    }

    @Test func codexMcpResultsAndViewedPicturesAreKept() throws {
        let cache = AgentImageCache(directory: try temporary("cache"))
        let workspace = try temporary("workspace")
        let inside = workspace.appendingPathComponent("chart.png"); try png(width: 5, height: 5).write(to: inside)
        var entries: [LogEntry] = []; var logs: [String] = []
        let parser = CLIStreamParser(provider: "codex", log: { logs.append($1) }, resume: { _ in }, activity: { _ in },
                                     images: cache, imageRoot: workspace, imageEntry: { entries.append($0) })
        try send(["type": "item.completed", "item": ["id": "mcp", "type": "mcp_tool_call", "server": "browser", "tool": "screenshot", "status": "completed",
                                                      "error": NSNull(), "result": ["content": [["type": "image", "data": png(width: 3, height: 4).base64EncodedString(), "mimeType": "image/png"]]]]], to: parser)
        try send(["type": "item.completed", "item": ["id": "view", "type": "image_view", "path": inside.path]], to: parser)
        // Outside the workspace and the temporary folders (the path rule test
        // covers symlinks and `..`): a notice, never a read.
        try send(["type": "item.completed", "item": ["id": "view-2", "type": "image_view", "path": "/etc/hosts.png"]], to: parser)
        #expect(entries.count == 2)
        #expect(entries[0].images?.first?.source.contains("browser.screenshot") == true)
        #expect(entries[0].images?.first?.height == 4)
        #expect(entries[1].images?.first?.path == inside.path)
        #expect(logs.count == 1); #expect(logs.first?.contains("/etc/hosts.png") == true)
    }

    @Test func codexAppServerImageItemsMapToTheExecNames() throws {
        let cache = AgentImageCache(directory: try temporary("cache"))
        var entries: [LogEntry] = []
        let parser = CLIStreamParser(provider: "codex", log: { _, _ in }, resume: { _ in }, images: cache, imageEntry: { entries.append($0) })
        try send(["type": "item.completed", "item": ["id": "gen", "type": "image_generation", "status": "completed", "result": png(width: 7, height: 7).base64EncodedString()]], to: parser)
        #expect(entries.first?.images?.first?.width == 7)
    }

    @Test func aPictureLineLargerThanTheOldOneMebibyteCapStillArrives() throws {
        let cache = AgentImageCache(directory: try temporary("cache"))
        var entries: [LogEntry] = []
        let parser = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in }, images: cache, imageEntry: { entries.append($0) })
        // Uncompressible pixels: well over 1 MiB of base64 on one line.
        let width = 700, height = 700
        var bytes = [UInt8](repeating: 0, count: width * height * 4)
        var seed: UInt32 = 7
        for index in bytes.indices { seed = seed &* 1_664_525 &+ 1_013_904_223; bytes[index] = UInt8(seed >> 24) }
        let provider = CGDataProvider(data: Data(bytes) as CFData)!
        let image = CGImage(width: width, height: height, bitsPerComponent: 8, bitsPerPixel: 32, bytesPerRow: width * 4, space: CGColorSpaceCreateDeviceRGB(),
                            bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.noneSkipLast.rawValue), provider: provider, decode: nil, shouldInterpolate: false, intent: .defaultIntent)!
        let data = NSMutableData()
        let destination = CGImageDestinationCreateWithData(data, "public.png" as CFString, 1, nil)!
        CGImageDestinationAddImage(destination, image, nil); CGImageDestinationFinalize(destination)
        #expect(data.length * 4 / 3 > 1_048_576)
        try send(["type": "user", "message": ["content": [["type": "tool_result", "tool_use_id": "big", "content": [claudeImage(data as Data)]]]]], to: parser)
        #expect(entries.first?.images?.first?.width == width)
    }

    // MARK: Caps and cache

    @Test func inspectionEnforcesSizePixelAndFormatCaps() throws {
        #expect(throws: AgentImageError.tooLarge) { try AgentImageSupport.inspect(Data(count: AgentImageSupport.maximumImageBytes + 1), mediaType: "image/png") }
        #expect(throws: AgentImageError.tooManyPixels) { try AgentImageSupport.inspect(self.png(width: 8, height: 6), mediaType: "image/png", maximumPixels: 47) }
        #expect(try AgentImageSupport.inspect(png(width: 8, height: 6), mediaType: "image/png", maximumPixels: 48).width == 8)
        // A header claiming 400 megapixels is never decoded, whichever check refuses it.
        #expect(throws: AgentImageError.self) { try AgentImageSupport.inspect(self.pngHeader(width: 20_000, height: 20_000), mediaType: "image/png") }
        #expect(throws: AgentImageError.unsupportedType) { try AgentImageSupport.inspect(self.png(), mediaType: "application/pdf") }
        #expect(throws: AgentImageError.undecodable) { try AgentImageSupport.inspect(Data("plain".utf8), mediaType: "image/png") }
        #expect(throws: AgentImageError.externalSVG) {
            try AgentImageSupport.inspect(Data(#"<svg xmlns="http://www.w3.org/2000/svg"><use href="other.svg#a"/></svg>"#.utf8), mediaType: "image/svg+xml")
        }
        let svg = try AgentImageSupport.inspect(Data(#"<svg xmlns="http://www.w3.org/2000/svg" width="4" height="4"><rect width="4" height="4"/></svg>"#.utf8), mediaType: "image/svg+xml")
        #expect(svg.mediaType == "image/svg+xml"); #expect(svg.width == 0)
        // A mislabelled picture is stored as what its bytes are.
        let jpeg = NSMutableData()
        let destination = CGImageDestinationCreateWithData(jpeg, "public.jpeg" as CFString, 1, nil)!
        CGImageDestinationAddImage(destination, CGImageSourceCreateImageAtIndex(CGImageSourceCreateWithData(png() as CFData, nil)!, 0, nil)!, nil)
        CGImageDestinationFinalize(destination)
        #expect(try AgentImageSupport.inspect(jpeg as Data, mediaType: "image/png").mediaType == "image/jpeg")
        #expect(throws: AgentImageError.invalidEncoding) { try AgentImageSupport.decodeBase64("") }
    }

    @Test func agentPicturesAreCappedAt64MegapixelsAnd8000PixelsASide() throws {
        #expect(throws: AgentImageError.tooManyPixels) { try AgentImageSupport.inspect(self.pngHeader(width: 8_001, height: 10), mediaType: "image/png") }
        #expect(throws: AgentImageError.tooManyPixels) { try AgentImageSupport.inspect(self.pngHeader(width: 10, height: 8_001), mediaType: "image/png") }
        #expect(try AgentImageSupport.inspect(pngHeader(width: 8_000, height: 10), mediaType: "image/png").width == 8_000)
        // The side cap keeps every picture within 64 MP; the pixel cap is its own check.
        #expect(AgentImageSupport.maximumSide * AgentImageSupport.maximumSide <= AgentImageSupport.maximumPixels)
        #expect(throws: AgentImageError.tooManyPixels) { try AgentImageSupport.inspect(self.pngHeader(width: 300, height: 300), mediaType: "image/png", maximumPixels: 89_999) }
        // The refusal reaches the transcript as one notice and no picture
        // (compared by count: other suites switch the app language).
        var logs: [String] = []; var entries: [LogEntry] = []
        let parser = CLIStreamParser(provider: "claude", log: { logs.append($1) }, resume: { _ in }, images: AgentImageCache(directory: try temporary("cache")), imageEntry: { entries.append($0) })
        try send(["type": "user", "message": ["content": [["type": "tool_result", "tool_use_id": "wide", "content": [claudeImage(pngHeader(width: 9_000, height: 4))]]]]], to: parser)
        #expect(logs.count == 1); #expect(entries.isEmpty)
    }

    @Test func onlyAnSvgDocumentIsTakenAsSvg() throws {
        let prolog = Data("""
        \u{FEFF}<?xml version="1.0" encoding="UTF-8"?>
        <!-- drawn by a tool -->
        <!DOCTYPE svg>
        <svg xmlns="http://www.w3.org/2000/svg" width="4" height="4"><rect width="4" height="4"/></svg>
        """.utf8)
        #expect(try AgentImageSupport.inspect(prolog, mediaType: "image/svg+xml").mediaType == "image/svg+xml")
        for text in [#"<html><svg xmlns="http://www.w3.org/2000/svg"/></html>"#, "plain text", #"<svgfoo/>"#, "<!-- unterminated <svg/>"] {
            #expect(throws: AgentImageError.undecodable) { try AgentImageSupport.inspect(Data(text.utf8), mediaType: "image/svg+xml") }
        }
    }

    @Test func aDamagedCacheFileIsWrittenAgain() throws {
        let directory = try temporary("cache")
        let cache = AgentImageCache(directory: directory)
        let picture = png(width: 9, height: 9)
        let ref = try cache.store(picture, mediaType: "image/png", source: "a")
        let file = directory.appendingPathComponent(ref.hash + ".png")
        for damage in [Data("tampered".utf8), Data(repeating: 0, count: picture.count)] {
            try damage.write(to: file)
            #expect(cache.data(for: ref) == nil)
            _ = try cache.store(picture, mediaType: "image/png", source: "a")
            #expect(cache.data(for: ref) == picture)
        }
    }

    @Test func theCacheIsContentAddressedAndEvictsTheLeastRecentlyUsed() throws {
        let directory = try temporary("cache")
        let first = png(width: 30, height: 30, red: 0.1), second = png(width: 30, height: 30, red: 0.5), third = png(width: 30, height: 30, red: 0.9)
        let cache = AgentImageCache(directory: directory, maximumBytes: first.count + second.count + third.count - 1)
        let a = try cache.store(first, mediaType: "image/png", source: "a")
        let again = try cache.store(first, mediaType: "image/png", source: "a")
        #expect(a.hash == again.hash)
        #expect(try FileManager.default.contentsOfDirectory(atPath: directory.path) == [a.hash + ".png"])
        // Make `a` the oldest, then use it again: `b` becomes the least recently used.
        try FileManager.default.setAttributes([.modificationDate: Date(timeIntervalSinceNow: -60)], ofItemAtPath: directory.appendingPathComponent(a.hash + ".png").path)
        let b = try cache.store(second, mediaType: "image/png", source: "b")
        try FileManager.default.setAttributes([.modificationDate: Date(timeIntervalSinceNow: -30)], ofItemAtPath: directory.appendingPathComponent(b.hash + ".png").path)
        #expect(cache.url(for: a) != nil)
        let c = try cache.store(third, mediaType: "image/png", source: "c")
        #expect(cache.url(for: b) == nil)
        #expect(cache.data(for: a) == first); #expect(cache.data(for: c) == third)
        #expect(cache.totalBytes <= cache.maximumBytes)
        // A tampered file is not served under the hash it no longer has.
        try Data("tampered".utf8).write(to: directory.appendingPathComponent(c.hash + ".png"))
        #expect(cache.data(for: c) == nil)
    }

    @Test func savedAndReceivedReferencesAreNormalized() throws {
        let ref = AgentImageRef(hash: String(repeating: "a", count: 64), mediaType: "image/JPG", width: 10, height: 20, bytes: 100, source: "Read\nshot", path: "relative.png")
        let clean = try #require(AgentImageSupport.normalized(ref))
        #expect(clean.mediaType == "image/jpeg"); #expect(clean.source == "Read shot"); #expect(clean.path == nil)
        #expect(AgentImageSupport.normalized(AgentImageRef(hash: "../x", mediaType: "image/png", width: 1, height: 1, bytes: 1, source: "")) == nil)
        #expect(AgentImageSupport.normalized(AgentImageRef(hash: String(repeating: "b", count: 64), mediaType: "text/html", width: 1, height: 1, bytes: 1, source: "")) == nil)
        let entry = LogEntry(id: "image-1", kind: "image", text: "그림", provider: "claude", images: [ref])
        let decoded = try JSONDecoder().decode(LogEntry.self, from: JSONEncoder().encode(entry))
        #expect(decoded.images == [clean])
        // A saved profile keeps image entries and their references, and nothing else of them.
        let workspace = Workspace(id: "w", name: "W", path: "/tmp")
        var session = RunSession(id: "s", workspaceId: "w", title: "Claude", logs: [entry, LogEntry(id: "bad", kind: "system", text: "x", images: [ref])])
        session.status = "idle"
        let restored = StateRepository.normalize(AppSnapshot(workspaces: [workspace], sessions: [session]), restoring: true)
        #expect(restored.sessions.first?.logs.first?.images == [clean])
        #expect(restored.sessions.first?.logs.last?.images == nil)
    }

    // MARK: Markdown pictures and the path rule

    @Test func markdownPicturesAreExtractedOutsideCodeFences() {
        let text = """
        Here: ![chart](shots/chart.png) and ![remote](https://example.com/a.png "title")
        ```
        ![not](code.png)
        ```
        ![spaced](<my shot.png>) ![data](data:image/png;base64,AAAA)
        """
        let images = AgentMarkdownImages.extract(text)
        #expect(images.map(\.alt) == ["chart", "remote", "spaced", "data"])
        #expect(images.map(\.source) == ["shots/chart.png", "https://example.com/a.png", "my shot.png", "data:image/png;base64,AAAA"])
        #expect(AgentMarkdownImages.extract("no pictures here").isEmpty)
    }

    @Test func thePathRuleAllowsTheWorkspaceAndTemporaryFoldersOnly() throws {
        let workspace = try temporary("workspace"), scratch = try temporary("scratch"), outside = try temporary("outside")
        try FileManager.default.createDirectory(at: workspace.appendingPathComponent("shots"), withIntermediateDirectories: true)
        let inside = workspace.appendingPathComponent("shots/chart.png"); try png().write(to: inside)
        let temp = scratch.appendingPathComponent("screen.png"); try png().write(to: temp)
        let secret = outside.appendingPathComponent("secret.png"); try png().write(to: secret)
        let notes = workspace.appendingPathComponent("notes.txt"); try Data("x".utf8).write(to: notes)
        try FileManager.default.createSymbolicLink(at: workspace.appendingPathComponent("link.png"), withDestinationURL: secret)
        func locate(_ value: String) -> AgentImageLocation { AgentImagePaths.locate(value, workspaceRoot: workspace, temporaryRoots: [scratch]) }

        #expect(locate("shots/chart.png") == .file(inside, root: workspace))
        #expect(locate(inside.path) == .file(inside, root: workspace))
        #expect(locate("file://" + inside.path) == .file(inside, root: workspace))
        #expect(locate(temp.path) == .file(temp, root: scratch))
        // Outside both, through `..`, through a symlink, relative into temp, or not a picture: refused.
        #expect(locate(secret.path) == .refused)
        #expect(locate("../" + outside.lastPathComponent + "/secret.png") == .refused)
        #expect(locate("link.png") == .refused)
        #expect(locate("screen.png") == .refused)
        #expect(locate("notes.txt") == .refused)
        #expect(locate("~/Desktop/a.png") == .refused)
        #expect(locate("missing.png") == .refused)
        // Remote addresses are never fetched: they come back as links only.
        #expect(locate("https://example.com/a.png") == .remote(URL(string: "https://example.com/a.png")!))
        #expect(locate("data:image/png;base64,AAAA") == .inline(mediaType: "image/png", base64: "AAAA"))
        #expect(locate("data:text/html;base64,AAAA") == .refused)
        #expect(locate("data:image/png,raw") == .refused)
        // Reading goes through the workspace safe-open.
        #expect(try AgentImagePaths.read(inside, root: workspace) == png())
        #expect(throws: (any Error).self) { try AgentImagePaths.read(secret, root: workspace) }
    }

    @Test func percentEncodedAbsolutePathsFindKoreanAndSpacedFileNames() throws {
        let scratch = try temporary("scratch")
        for name in ["스크린샷 2026-10-01 오전 9.40.12.png", "with space.png", "100%.png"] {
            let file = scratch.appendingPathComponent(name); try png().write(to: file)
            let encoded = try #require(file.path.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed))
            #expect(AgentImagePaths.locate(file.path, workspaceRoot: nil, temporaryRoots: [scratch]) == .file(file, root: scratch))
            if encoded != file.path { #expect(AgentImagePaths.locate(encoded, workspaceRoot: nil, temporaryRoots: [scratch]) == .file(file, root: scratch)) }
            #expect(AgentImagePaths.locate("file://" + encoded, workspaceRoot: nil, temporaryRoots: [scratch]) == .file(file, root: scratch))
            // What Markdown hands over for `![shot](<path>)`, decoded as the transcript keeps it.
            let markdown = try AttributedString(markdown: "![shot](<\(file.path)>)")
            let image = try #require(markdown.runs.compactMap(\.imageURL).first)
            let kept = ReferenceLinkSupport.localPath(image).flatMap { $0.hasPrefix("/") ? $0 : nil } ?? image.absoluteString
            #expect(kept == file.path)
            #expect(AgentImagePaths.locate(image.absoluteString, workspaceRoot: nil, temporaryRoots: [scratch]) == .file(file, root: scratch))
        }
    }

    @Test func aClientMayPublishPictureEntries() {
        let ref = AgentImageRef(hash: String(repeating: "f", count: 64), mediaType: "image/png", width: 1, height: 1, bytes: 1, source: "Read")
        let event = RunEvent(sessionId: "s", type: "log", entry: LogEntry(id: "img", kind: "image", text: "그림", provider: "claude", images: [ref]))
        #expect(RemoteValidation.event(event, sessionId: "s"))
        #expect(!RemoteValidation.event(RunEvent(sessionId: "s", type: "log", entry: LogEntry(id: "x", kind: "bogus", text: "x")), sessionId: "s"))
    }

    // MARK: Graph history

    @Test func graphHistoryKeepsImageEntriesThroughRecordingAndSaving() throws {
        let ref = AgentImageRef(hash: String(repeating: "d", count: 64), mediaType: "image/png", width: 4, height: 4, bytes: 10, source: "MCP")
        var session = RunSession(id: "s", workspaceId: "w", title: "Claude")
        session.recordGraph(RunEvent(sessionId: "s", type: "log", entry: LogEntry(id: "u", kind: "user", text: "go")))
        session.recordGraph(RunEvent(sessionId: "s", type: "log", entry: LogEntry(id: "img", kind: "image", text: "그림", provider: "claude", images: [ref])))
        #expect(session.graphRuns?.last?.rootEntries.last?.images == [ref])
        var budget = 1_000_000
        let saved = MightyGraphSupport.normalized(session.graphRuns ?? [], restoring: true, budget: &budget)
        #expect(saved.last?.rootEntries.last?.images == [ref])
        let node = ExecutionGraphNode(id: "graph-a", runId: "run", parentId: "graph-main", kind: "agent", state: "completed", title: "A",
                                      entries: [LogEntry(id: "img", kind: "image", text: "그림", provider: "claude", images: [ref])])
        #expect(ExecutionGraphSupport.normalized(node)?.entries.first?.images == [ref])
    }

    // MARK: Phone

    @Test func thePhoneReceivesPictureEntriesAsPlainLinesWithoutReferences() throws {
        let ref = AgentImageRef(hash: String(repeating: "e", count: 64), mediaType: "image/png", width: 4, height: 4, bytes: 10, source: "Read", path: "/w/a.png")
        let picture = LogEntry(id: "img", kind: "image", text: AgentImageSupport.entryText([ref], source: "Read"), provider: "claude", images: [ref])
        let summary = MobileSessionSummary(id: "s", workspaceId: "w", title: "Claude", kind: "claude", provider: "claude", model: "default", status: "idle", revision: 1, updatedAt: mightyTimestamp())
        let detail = MobileSessionDetail(revision: 1, session: summary, entries: [LogEntry(id: "a", kind: "assistant", text: "hi"), picture])
        let page = MobileEntriesPage(entries: [picture], hasMore: false)
        for json in [String(decoding: try JSONEncoder().encode(detail), as: UTF8.self), String(decoding: try JSONEncoder().encode(page), as: UTF8.self)] {
            #expect(!json.contains("\"images\"")); #expect(!json.contains(ref.hash)); #expect(!json.contains("\"image\""))
        }
        #expect(detail.entries.last?.kind == "system"); #expect(detail.entries.last?.text == picture.text)
        #expect(detail.entries.first?.kind == "assistant")
    }
}
