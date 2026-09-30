import CoreGraphics
import Darwin
import Foundation
import ImageIO
import Testing
@testable import MightyCore

/// A Mac with one workspace at `root`. Nothing but the state is used by the
/// file routes; `svg` stands in for the app's AppKit drawing.
private final class FilesHost: MobileHostDelegate, @unchecked Sendable {
    let root: URL
    let svg: MobileWorkspaceFiles.SVGRasterizer?
    init(root: URL, svg: MobileWorkspaceFiles.SVGRasterizer? = nil) { self.root = root; self.svg = svg }
    func mobileState() async -> MobileState {
        MobileState(revision: 1, hostName: "Files Mac", workspaces: [MobileWorkspace(id: "workspace-1", name: "Repo", path: root.path)], sessions: [])
    }
    var mobileSVGRasterizer: MobileWorkspaceFiles.SVGRasterizer? { svg }
    func mobileSession(id: String) async -> MobileSessionDetail? { nil }
    func mobileSubmit(sessionId: String, text: String, mode: String?, attachments: [RunAttachment]) async throws -> String { "started" }
    func mobileGuided(sessionId: String, style: String, skill: String, text: String) async throws -> String { "started" }
    func mobileStop(sessionId: String) async throws -> Bool { false }
    func mobilePermission(sessionId: String, requestId: String, runId: String, allow: Bool) async throws {}
    func mobileAnswers(sessionId: String, requestId: String, runId: String, answers: [String: UserQuestionAnswer]) async throws {}
    func mobileCreateSession(workspaceId: String, kind: String, provider: String) async throws -> String { "new" }
    func mobileRemoveQueued(sessionId: String, itemId: String) async throws {}
    func mobileRunNextQueued(sessionId: String) async throws {}
    func mobileRename(sessionId: String, title: String, titleMode: String?) async throws {}
    func mobileClose(sessionId: String) async throws {}
    func mobileEntries(sessionId: String, before: String, limit: Int) async throws -> MobileEntriesPage { MobileEntriesPage(entries: [], hasMore: false) }
    func mobileApplySettings(sessionId: String, request: MobileSettingsRequest) async throws {}
    func mobileCommands(sessionId: String) async throws -> [MobileCommand] { [] }
    func mobilePerformCommand(sessionId: String, action: String) async throws -> String? { nil }
}

/// An svg drawing that reports whatever size a test sets, counting calls.
private final class SizedSVG: @unchecked Sendable {
    private let lock = NSLock()
    private var size = CGSize(width: 32, height: 16), count = 0
    var points: CGSize { get { lock.lock(); defer { lock.unlock() }; return size } set { lock.lock(); size = newValue; lock.unlock() } }
    var calls: Int { lock.lock(); defer { lock.unlock() }; return count }
    var rasterizer: MobileWorkspaceFiles.SVGRasterizer {
        { [self] _, maximumPixels in
            lock.lock(); count += 1; let points = size; lock.unlock()
            guard let context = MobileWorkspaceFiles.context(width: min(64, maximumPixels), height: 32, opaque: false), let image = context.makeImage() else { return nil }
            return (image, points)
        }
    }
}

/// An svg drawing that holds its first call until the test opens it, so a
/// preview can be kept inside the slot.
private final class PreviewGate: @unchecked Sendable {
    private let entered = DispatchSemaphore(value: 0)
    private let gate = DispatchSemaphore(value: 0)
    private let lock = NSLock()
    private var count = 0
    var calls: Int { lock.lock(); defer { lock.unlock() }; return count }
    func open() { gate.signal() }
    func waitUntilEntered() async {
        await withCheckedContinuation { (done: CheckedContinuation<Void, Never>) in
            DispatchQueue.global().async { [entered] in _ = entered.wait(timeout: .now() + 20); done.resume() }
        }
    }
    var rasterizer: MobileWorkspaceFiles.SVGRasterizer {
        { [self] _, maximumPixels in
            lock.lock(); count += 1; let first = count == 1; lock.unlock()
            if first { entered.signal(); _ = gate.wait(timeout: .now() + 20) }
            guard let context = MobileWorkspaceFiles.context(width: min(64, maximumPixels), height: 32, opaque: false), let image = context.makeImage() else { return nil }
            return (image, CGSize(width: 32, height: 16))
        }
    }
}

struct MobileWorkspaceFilesTests {
    private struct Fixture {
        let root: URL, outside: URL, data: URL
        let host: FilesHost
        let service: MobileRemoteService
        func remove() { for url in [root, outside, data] { try? FileManager.default.removeItem(at: url) } }
    }

    private func fixture(svg: MobileWorkspaceFiles.SVGRasterizer? = nil) async throws -> Fixture {
        let temp = FileManager.default.temporaryDirectory
        let root = temp.appendingPathComponent("phone-files-" + UUID().uuidString, isDirectory: true)
        let outside = temp.appendingPathComponent("phone-outside-" + UUID().uuidString, isDirectory: true)
        let data = temp.appendingPathComponent("phone-data-" + UUID().uuidString, isDirectory: true)
        for url in [root, outside, data] { try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true) }
        let host = FilesHost(root: root, svg: svg)
        let service = MobileRemoteService(dataDirectory: data, hostName: "Files Mac", watchesNetwork: false)
        await service.attach(host)
        return Fixture(root: root, outside: outside, data: data, host: host, service: service)
    }

    private func write(_ data: Data, _ path: String, in root: URL) throws {
        let url = root.appendingPathComponent(path)
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try data.write(to: url)
    }

    private func get(_ service: MobileRemoteService, _ path: String, device: String = "cGhvbmUtb25lLTAwMDAwMDA") async -> (status: Int, body: [String: Any], bytes: Int) {
        let reply = await service.route(method: "GET", path: path, body: nil, deviceId: device)
        return (reply.status, (try? JSONSerialization.jsonObject(with: reply.body) as? [String: Any]) ?? [:], reply.body.count)
    }

    private func query(_ path: String) -> String { path.addingPercentEncoding(withAllowedCharacters: .alphanumerics) ?? "" }

    @Test func listsAFolderLikeTheMacPaneWithKindsSizesAndNoise() async throws {
        let f = try await fixture(); defer { f.remove() }
        try write(Data("hello".utf8), "file10.txt", in: f.root)
        try write(Data("hi".utf8), "file2.txt", in: f.root)
        try write(Data("x".utf8), "src/App.swift", in: f.root)
        try FileManager.default.createDirectory(at: f.root.appendingPathComponent("node_modules"), withIntermediateDirectories: true)
        try FileManager.default.createSymbolicLink(at: f.root.appendingPathComponent("link.txt"), withDestinationURL: f.root.appendingPathComponent("file2.txt"))
        try FileManager.default.createSymbolicLink(at: f.root.appendingPathComponent("code"), withDestinationURL: f.root.appendingPathComponent("src"))

        let (status, body, _) = await get(f.service, "/m1/workspaces/workspace-1/files")
        #expect(status == 200)
        #expect(body["protocol"] as? Int == 1)
        #expect(body["path"] as? String == "")
        #expect(body["truncated"] as? Bool == false)
        let entries = body["entries"] as? [[String: Any]] ?? []
        #expect(entries.map { $0["name"] as? String } == ["code", "node_modules", "src", "file2.txt", "file10.txt", "link.txt"])
        #expect(entries.map { $0["kind"] as? String } == ["symlink-folder", "folder", "folder", "file", "file", "symlink-file"])
        #expect(entries.map { $0["noise"] as? Bool } == [false, true, false, false, false, false])
        #expect(entries[3]["size"] as? Int == 2)
        #expect(entries[5]["size"] as? Int == 2, "a link's size is its target's")
        #expect(entries[1]["size"] == nil, "folders carry no size")
        #expect((entries[3]["modified"] as? String).flatMap { ISO8601DateFormatter().date(from: $0) } != nil)

        let nested = await get(f.service, "/m1/workspaces/workspace-1/files?path=src")
        #expect((nested.body["entries"] as? [[String: Any]])?.map { $0["relativePath"] as? String } == ["src/App.swift"])
        let viaLink = await get(f.service, "/m1/workspaces/workspace-1/files?path=code")
        #expect((viaLink.body["entries"] as? [[String: Any]])?.map { $0["relativePath"] as? String } == ["code/App.swift"])

        let info = await get(f.service, "/m1/info")
        #expect((info.body["capabilities"] as? [String])?.contains("files") == true)
    }

    @Test func refusesPathsOutsideTheWorkspaceUnknownWorkspacesAndBadQueries() async throws {
        let f = try await fixture(); defer { f.remove() }
        try write(Data("secret".utf8), "secret.txt", in: f.outside)
        try write(Data("inside".utf8), "docs/readme.md", in: f.root)
        try FileManager.default.createSymbolicLink(at: f.root.appendingPathComponent("escape.txt"), withDestinationURL: f.outside.appendingPathComponent("secret.txt"))
        try FileManager.default.createSymbolicLink(at: f.root.appendingPathComponent("outdir"), withDestinationURL: f.outside)

        let listing = await get(f.service, "/m1/workspaces/workspace-1/files")
        #expect((listing.body["entries"] as? [[String: Any]])?.map { $0["name"] as? String } == ["docs"], "escaping links are not listed")

        func refused(_ path: String, _ status: Int, _ code: String, sourceLocation: SourceLocation = #_sourceLocation) async {
            let reply = await get(f.service, path)
            #expect(reply.status == status, sourceLocation: sourceLocation)
            #expect(reply.body["code"] as? String == code, sourceLocation: sourceLocation)
            #expect(reply.body["error"] is String, sourceLocation: sourceLocation)
            #expect(reply.body["text"] == nil && reply.body["entries"] == nil, sourceLocation: sourceLocation)
        }
        await refused("/m1/workspaces/workspace-1/file?path=escape.txt", 403, "outsideWorkspace")
        await refused("/m1/workspaces/workspace-1/files?path=outdir", 403, "outsideWorkspace")
        // Under a link that leaves the root nothing is looked up: what exists
        // out there and what does not answer alike.
        await refused("/m1/workspaces/workspace-1/file?path=outdir/secret.txt", 404, "notFound")
        let present = await get(f.service, "/m1/workspaces/workspace-1/file?path=outdir/secret.txt")
        let absent = await get(f.service, "/m1/workspaces/workspace-1/file?path=outdir/nothing.txt")
        #expect(present.status == absent.status && present.bytes == absent.bytes)
        #expect(NSDictionary(dictionary: present.body) == NSDictionary(dictionary: absent.body))
        try FileManager.default.createDirectory(at: f.outside.appendingPathComponent("deeper"), withIntermediateDirectories: true)
        let presentFolder = await get(f.service, "/m1/workspaces/workspace-1/files?path=outdir/deeper")
        let absentFolder = await get(f.service, "/m1/workspaces/workspace-1/files?path=outdir/shallower")
        #expect(presentFolder.status == 404 && NSDictionary(dictionary: presentFolder.body) == NSDictionary(dictionary: absentFolder.body))
        await refused("/m1/workspaces/workspace-1/file?path=" + query("../" + f.outside.lastPathComponent + "/secret.txt"), 403, "outsideWorkspace")
        await refused("/m1/workspaces/workspace-1/file?path=" + query("docs/../../x"), 403, "outsideWorkspace")
        await refused("/m1/workspaces/workspace-1/file?path=" + query(f.outside.appendingPathComponent("secret.txt").path), 403, "outsideWorkspace")
        await refused("/m1/workspaces/workspace-1/file?path=docs%00readme.md", 400, "badPath")
        await refused("/m1/workspaces/workspace-1/file?path=docs//readme.md", 400, "badPath")
        await refused("/m1/workspaces/workspace-1/file?path=./docs/readme.md", 400, "badPath")
        await refused("/m1/workspaces/workspace-1/file", 400, "badPath")
        await refused("/m1/workspaces/workspace-1/file?path=nothing.txt", 404, "notFound")
        await refused("/m1/workspaces/workspace-1/files?path=docs/readme.md", 400, "notDirectory")
        await refused("/m1/workspaces/workspace-2/files", 404, "workspaceNotFound")
        await refused("/m1/workspaces/..%2F/files", 404, "workspaceNotFound")

        let unknownQuery = await get(f.service, "/m1/workspaces/workspace-1/files?depth=3")
        #expect(unknownQuery.status == 400)
        let twice = await get(f.service, "/m1/workspaces/workspace-1/files?path=docs&path=docs")
        #expect(twice.status == 400)
        #expect(await get(f.service, "/m1/workspaces/workspace-1/file?path=docs/readme.md").status == 200)
        #expect(await f.service.route(method: "POST", path: "/m1/workspaces/workspace-1/files", body: Data("{}".utf8), deviceId: "x").status == 404)

        #expect(throws: MobileFileError.outsideWorkspace) { try MobileWorkspaceFiles.validatedPath("/etc/passwd", allowRoot: true) }
        #expect(throws: MobileFileError.badPath) { try MobileWorkspaceFiles.validatedPath(String(repeating: "a", count: 4_097), allowRoot: true) }
        #expect(try MobileWorkspaceFiles.validatedPath(nil, allowRoot: true) == "")
        #expect(try MobileWorkspaceFiles.validatedPath("a/b c/한글.md", allowRoot: false) == "a/b c/한글.md")
    }

    @Test func textIsCappedAndCutFurtherWhenEscapingWouldOverflowTheFrame() async throws {
        let f = try await fixture(); defer { f.remove() }
        try write(Data(String(repeating: "abcdefghij\n", count: 60_000).utf8), "big.log", in: f.root)
        let big = await get(f.service, "/m1/workspaces/workspace-1/file?path=big.log")
        #expect(big.status == 200)
        #expect(big.body["type"] as? String == "source")
        #expect(big.body["language"] as? String == "plain")
        #expect(big.body["truncated"] as? Bool == true)
        #expect((big.body["text"] as? String)?.utf8.count == MobileWorkspaceFiles.maximumTextBytes)
        #expect(big.body["lineCount"] as? Int == MobileWorkspaceFiles.maximumTextBytes / 11 + 1)
        #expect(big.body["size"] as? Int == 660_000)

        // Every control character escapes to six bytes: 500 KB of them would be 3 MB of JSON.
        try write(Data(String(repeating: "\u{01}", count: 500_000).utf8), "controls.txt", in: f.root)
        let escaped = await get(f.service, "/m1/workspaces/workspace-1/file?path=controls.txt")
        #expect(escaped.status == 200)
        #expect(escaped.bytes <= MobileWorkspaceFiles.maximumReplyBytes)
        #expect(escaped.body["truncated"] as? Bool == true)
        #expect(((escaped.body["text"] as? String)?.count ?? 0) > 50_000)

        try write(Data("let x = 1\n".utf8), "Sources/App.swift", in: f.root)
        let small = await get(f.service, "/m1/workspaces/workspace-1/file?path=Sources/App.swift")
        #expect(small.body["language"] as? String == "swift")
        #expect(small.body["truncated"] as? Bool == false)
        #expect(small.body["lineCount"] as? Int == 2)
        #expect(small.body["name"] as? String == "App.swift")
        try write(Data("# Title\n\nbody".utf8), "README.md", in: f.root)
        let markdown = await get(f.service, "/m1/workspaces/workspace-1/file?path=README.md")
        #expect(markdown.body["type"] as? String == "markdown")
        #expect(markdown.body["language"] == nil)
        #expect(markdown.body["text"] as? String == "# Title\n\nbody")
    }

    @Test func koreanAndUTF16TextArriveDecodedWithTheirEncodingNamed() async throws {
        let f = try await fixture(); defer { f.remove() }
        let korean = "안녕하세요, 파일 창입니다.\n"
        try write(try #require(korean.data(using: TextEncoding.cp949Encoding)), "legacy.txt", in: f.root)
        try write(Data([0xFF, 0xFE]) + (try #require(korean.data(using: .utf16LittleEndian))), "wide.txt", in: f.root)
        let legacy = await get(f.service, "/m1/workspaces/workspace-1/file?path=legacy.txt")
        #expect(legacy.body["encoding"] as? String == "CP949 (EUC-KR)")
        #expect(legacy.body["text"] as? String == korean)
        let wide = await get(f.service, "/m1/workspaces/workspace-1/file?path=wide.txt")
        #expect(wide.body["encoding"] as? String == "UTF-16 LE")
        #expect(wide.body["text"] as? String == korean)
        let named = await get(f.service, "/m1/workspaces/workspace-1/file?path=" + query("wide.txt"))
        #expect(named.body["path"] as? String == "wide.txt")
    }

    /// Random pixels: no encoder can make them small, so the ladder must step down.
    private func noisePNG(width: Int, height: Int) throws -> Data {
        let context = try #require(MobileWorkspaceFiles.context(width: width, height: height, opaque: true))
        let pixels = try #require(context.data).assumingMemoryBound(to: UInt32.self)
        var state: UInt32 = 0x9E3779B9
        for index in 0..<(context.bytesPerRow / 4 * height) {
            state ^= state << 13; state ^= state >> 17; state ^= state << 5
            pixels[index] = state | 0xFF00_0000
        }
        let image = try #require(context.makeImage())
        let output = NSMutableData()
        let destination = try #require(CGImageDestinationCreateWithData(output, "public.png" as CFString, 1, nil))
        CGImageDestinationAddImage(destination, image, nil)
        #expect(CGImageDestinationFinalize(destination))
        return output as Data
    }

    @Test func imagesComeAsThumbnailsUnderThePixelAndByteCaps() async throws {
        let f = try await fixture(); defer { f.remove() }
        try write(try noisePNG(width: 2_600, height: 1_300), "noise.png", in: f.root)
        let reply = await get(f.service, "/m1/workspaces/workspace-1/file?path=noise.png")
        #expect(reply.status == 200)
        #expect(reply.body["type"] as? String == "image")
        #expect(reply.body["mime"] as? String == "image/jpeg")
        #expect(reply.body["width"] as? Int == 2_600)
        #expect(reply.body["height"] as? Int == 1_300)
        let data = try #require((reply.body["data"] as? String).flatMap { Data(base64Encoded: $0) })
        #expect(data.count <= MobileWorkspaceFiles.maximumThumbnailBytes)
        #expect(reply.bytes <= MobileWorkspaceFiles.maximumReplyBytes)
        let thumbnailWidth = try #require(reply.body["thumbnailWidth"] as? Int), thumbnailHeight = try #require(reply.body["thumbnailHeight"] as? Int)
        #expect(thumbnailWidth <= MobileWorkspaceFiles.maximumThumbnailPixels && thumbnailWidth < 2_600)
        #expect(abs(thumbnailWidth - 2 * thumbnailHeight) <= 1)
        let decoded = try #require(CGImageSourceCreateWithData(data as CFData, nil).flatMap { CGImageSourceCreateImageAtIndex($0, 0, nil) })
        #expect(decoded.width == thumbnailWidth && decoded.height == thumbnailHeight)

        // A small transparent image keeps its size and its alpha.
        let clear = try #require(MobileWorkspaceFiles.context(width: 40, height: 20, opaque: false))
        clear.setFillColor(CGColor(srgbRed: 1, green: 0, blue: 0, alpha: 0.5)); clear.fill(CGRect(x: 0, y: 0, width: 20, height: 20))
        let output = NSMutableData()
        let destination = try #require(CGImageDestinationCreateWithData(output, "public.png" as CFString, 1, nil))
        CGImageDestinationAddImage(destination, try #require(clear.makeImage()), nil)
        _ = CGImageDestinationFinalize(destination)
        try write(output as Data, "clear.png", in: f.root)
        let small = await get(f.service, "/m1/workspaces/workspace-1/file?path=clear.png")
        #expect(small.body["mime"] as? String == "image/png")
        #expect(small.body["thumbnailWidth"] as? Int == 40 && small.body["thumbnailHeight"] as? Int == 20)
    }

    @Test func pdfShowsItsFirstPageAndSvgNeedsTheAppsDrawing() async throws {
        let drawn: MobileWorkspaceFiles.SVGRasterizer = { _, maximumPixels in
            guard let context = MobileWorkspaceFiles.context(width: min(64, maximumPixels), height: 32, opaque: false), let image = context.makeImage() else { return nil }
            return (image, CGSize(width: 32, height: 16))
        }
        let f = try await fixture(svg: drawn); defer { f.remove() }
        let pdf = NSMutableData()
        var box = CGRect(x: 0, y: 0, width: 300, height: 150)
        let consumer = try #require(CGDataConsumer(data: pdf))
        let document = try #require(CGContext(consumer: consumer, mediaBox: &box, nil))
        document.beginPDFPage(nil); document.setFillColor(CGColor(srgbRed: 0, green: 0, blue: 1, alpha: 1)); document.fill(CGRect(x: 10, y: 10, width: 50, height: 50)); document.endPDFPage()
        document.closePDF()
        try write(pdf as Data, "doc.pdf", in: f.root)
        let page = await get(f.service, "/m1/workspaces/workspace-1/file?path=doc.pdf")
        #expect(page.body["type"] as? String == "image")
        #expect(page.body["mime"] as? String == "image/jpeg")
        #expect(page.body["width"] as? Int == 300 && page.body["height"] as? Int == 150)
        #expect(page.body["thumbnailWidth"] as? Int == 600 && page.body["thumbnailHeight"] as? Int == 300)

        let svg = Data(#"<svg xmlns="http://www.w3.org/2000/svg" width="32" height="16"><rect width="16" height="16" fill="red"/></svg>"#.utf8)
        try write(svg, "icon.svg", in: f.root)
        let icon = await get(f.service, "/m1/workspaces/workspace-1/file?path=icon.svg")
        #expect(icon.body["type"] as? String == "image")
        #expect(icon.body["mime"] as? String == "image/png")
        #expect(icon.body["width"] as? Int == 32 && icon.body["height"] as? Int == 16)

        let plain = try await fixture(); defer { plain.remove() }
        try write(svg, "icon.svg", in: plain.root)
        let undrawn = await get(plain.service, "/m1/workspaces/workspace-1/file?path=icon.svg")
        #expect(undrawn.body["type"] as? String == "unsupported")
        #expect(undrawn.body["reason"] as? String == "undecodable")
    }

    @Test func binaryPipesFoldersAndHugeImagesAreUnsupportedWithTheirDetails() async throws {
        let f = try await fixture(); defer { f.remove() }
        try write(Data([0x00, 0x01, 0x02, 0xFF, 0x00]), "blob.bin", in: f.root)
        let blob = await get(f.service, "/m1/workspaces/workspace-1/file?path=blob.bin")
        #expect(blob.status == 200)
        #expect(blob.body["type"] as? String == "unsupported")
        #expect(blob.body["reason"] as? String == "binary")
        #expect(blob.body["size"] as? Int == 5)
        #expect(blob.body["modified"] is String)
        #expect(blob.body["data"] == nil && blob.body["text"] == nil)

        #expect(mkfifo(f.root.appendingPathComponent("pipe").path, 0o644) == 0)
        let pipe = await get(f.service, "/m1/workspaces/workspace-1/file?path=pipe")
        #expect(pipe.body["reason"] as? String == "notRegularFile")
        try FileManager.default.createDirectory(at: f.root.appendingPathComponent("folder"), withIntermediateDirectories: true)
        let folder = await get(f.service, "/m1/workspaces/workspace-1/file?path=folder")
        #expect(folder.body["reason"] as? String == "notRegularFile")

        // A sparse file: over the limit without writing 50 MB, and never read.
        let huge = f.root.appendingPathComponent("huge.png")
        #expect(FileManager.default.createFile(atPath: huge.path, contents: nil))
        let handle = try FileHandle(forWritingTo: huge)
        try handle.truncate(atOffset: UInt64(FilePreviewClassifier.maximumImageBytes + 1)); try handle.close()
        let tooLarge = await get(f.service, "/m1/workspaces/workspace-1/file?path=huge.png")
        #expect(tooLarge.body["type"] as? String == "unsupported")
        #expect(tooLarge.body["reason"] as? String == "tooLarge")
    }

    @Test func aListingOfLongPathsIsCutToFitTheFrame() throws {
        let long = String(repeating: "d", count: 900)
        let entries = (0..<2_000).map { MobileFileEntry(name: "f\($0)", relativePath: long + "/f\($0)", kind: "file", size: 1, modified: nil, noise: false) }
        let data = MobileWorkspaceFiles.encoded(MobileFileListing(workspaceId: "w", path: long, entries: entries, truncated: false))
        #expect(data.count <= MobileWorkspaceFiles.maximumReplyBytes)
        let decoded = try JSONDecoder().decode(MobileFileListing.self, from: data)
        #expect(decoded.truncated)
        #expect(decoded.entries.count > 100 && decoded.entries.count < 2_000)
        #expect(decoded.entries.first?.name == "f0")
    }

    @Test func aFolderOverTheCapIsTruncated() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("phone-many-" + UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        for index in 0..<(MobileWorkspaceFiles.maximumEntries + 5) {
            #expect(FileManager.default.createFile(atPath: root.appendingPathComponent("f\(index)").path, contents: nil))
        }
        let listing = try MobileWorkspaceFiles.listing(workspaceId: "w", path: "", root: root)
        #expect(listing.entries.count == MobileWorkspaceFiles.maximumEntries)
        #expect(listing.truncated)
        #expect(listing.entries.first?.name == "f0" && listing.entries[1].name == "f1" && listing.entries[10].name == "f10")
    }

    /// A one-page pdf with the given MediaBox numbers, written by hand so the
    /// box can be one no CoreGraphics context would make.
    private func handWrittenPDF(side: String) -> Data {
        let objects = ["<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 \(side) \(side)] >>"]
        var text = "%PDF-1.4\n", offsets: [Int] = []
        for (index, object) in objects.enumerated() { offsets.append(text.utf8.count); text += "\(index + 1) 0 obj\n\(object)\nendobj\n" }
        let xref = text.utf8.count
        text += "xref\n0 \(objects.count + 1)\n0000000000 65535 f \n" + offsets.map { String(format: "%010d 00000 n \n", $0) }.joined()
        text += "trailer\n<< /Size \(objects.count + 1) /Root 1 0 R >>\nstartxref\n\(xref)\n%%EOF\n"
        return Data(text.utf8)
    }

    @Test func absurdSizesArePreviewsThatCannotBeDrawnNeverACrash() async throws {
        let sizes = SizedSVG()
        let f = try await fixture(svg: sizes.rasterizer); defer { f.remove() }
        let svg = Data(#"<svg xmlns="http://www.w3.org/2000/svg" width="1e30" height="10"><rect width="5" height="5"/></svg>"#.utf8)
        try write(svg, "wide.svg", in: f.root)
        for points in [CGSize(width: 1e30, height: 10), CGSize(width: CGFloat.infinity, height: 10), CGSize(width: CGFloat.nan, height: 10), CGSize(width: 1e7 + 1, height: 1)] {
            sizes.points = points
            let reply = await get(f.service, "/m1/workspaces/workspace-1/file?path=wide.svg")
            #expect(reply.status == 200 && reply.body["type"] as? String == "unsupported" && reply.body["reason"] as? String == "undecodable", "\(points)")
        }
        sizes.points = CGSize(width: 1e7, height: 1)
        #expect(await get(f.service, "/m1/workspaces/workspace-1/file?path=wide.svg").body["width"] as? Int == 10_000_000)

        try write(handWrittenPDF(side: "100000000000000000000000000000"), "huge.pdf", in: f.root)
        let huge = await get(f.service, "/m1/workspaces/workspace-1/file?path=huge.pdf")
        #expect(huge.status == 200 && huge.body["reason"] as? String == "undecodable")
        try write(handWrittenPDF(side: "300"), "fine.pdf", in: f.root)
        let fine = await get(f.service, "/m1/workspaces/workspace-1/file?path=fine.pdf")
        #expect(fine.body["type"] as? String == "image" && fine.body["width"] as? Int == 300)

        #expect(FilePreviewClassifier.pixelCount(width: Int.max, height: 2) == nil)
        #expect(FilePreviewClassifier.pixelCount(width: 0, height: 2) == nil)
        #expect(!FilePreviewClassifier.isDrawable(width: -.infinity, height: 1))
    }

    /// ImageIO itself refuses a header that claims far more pixels than its
    /// bytes could hold, so the budget is shown on a real image and a small one.
    @Test func bitmapsOverThePixelBudgetAreNeverDecoded() throws {
        let png = try noisePNG(width: 40, height: 30)
        #expect(MobileWorkspaceFiles.thumbnail(png, name: "a.png", svg: nil, maximumPixels: 1_199) == .tooLarge)
        if case .image = MobileWorkspaceFiles.thumbnail(png, name: "a.png", svg: nil, maximumPixels: 1_200) {} else { Issue.record("at the budget it decodes") }
        #expect(FilePreviewClassifier.maximumFullPixels < FilePreviewClassifier.maximumDecodePixels)
    }

    @Test func svgThatReachesOutsideItselfIsNeverDrawn() async throws {
        let sizes = SizedSVG()
        let f = try await fixture(svg: sizes.rasterizer); defer { f.remove() }
        try write(try noisePNG(width: 8, height: 8), "secret.png", in: f.outside)
        let outside = f.outside.appendingPathComponent("secret.png").path
        let head = #"<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="10" height="10">"#
        let external = [
            #"<image href="file://\#(outside)" width="10" height="10"/>"#,
            #"<image xlink:href="\#(outside)" width="10" height="10"/>"#,
            #"<image href="secret.png" width="10" height="10"/>"#,
            #"<image href="../\#(f.outside.lastPathComponent)/secret.png"/>"#,
            #"<IMAGE XLINK:HREF = ' file:///etc/hosts ' />"#,
            "<image href\n=\n\"\(outside)\"/>",
            #"<image href=\#(outside) />"#,
            #"<image href="&#102;ile://\#(outside)"/>"#,
            #"<image href="&#x66;ile://\#(outside)"/>"#,
            #"<image href="data:image/svg+xml;base64,PHN2Zy8+"/>"#,
            #"<image href=""/>"#,
            #"<rect style="fill:url(other.svg#paint)" width="5" height="5"/>"#,
            #"<rect fill="URL( 'file:///x.svg#p' )" width="5" height="5"/>"#,
            #"<rect style="fill:&#x75;rl(x.svg#p)" width="5" height="5"/>"#,
            #"<style>rect { fill: \75 rl(x.svg#p) }</style>"#,
            #"<style>@import "x.css";</style>"#,
            ##"<g xml:base="file:///etc/"><use href="#a"/></g>"##,
            #"<use href="other.svg#icon"/>"#,
            #"<script src="x.js"/>"#,
            #"<g xmlns:q="http://www.w3.org/1999/xlink"><image q:href="\#(outside)"/></g>"#,
            #"<filter id="f"><feImage href="\#(outside)"/></filter>"#,
            #"<foreignObject width="5" height="5"><img xmlns="http://www.w3.org/1999/xhtml" src="\#(outside)"/></foreignObject>"#,
        ]
        let prologues = [
            #"<?xml version="1.0"?><!DOCTYPE svg [<!ENTITY e SYSTEM "file:///etc/hosts">]>"#,
            #"<!DOCTYPE svg SYSTEM "file:///etc/x.dtd">"#,
            #"<!DOCTYPE svg PUBLIC "-//W3C//DTD SVG 1.1//EN" "http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd">"#,
            #"<?xml version="1.0" encoding="UTF-7"?>"#,
            #"<?xml-stylesheet href="file:///etc/x.css"?>"#,
        ]
        let documents = external.map { head + $0 + "</svg>" } + prologues.map { $0 + head + "</svg>" }
        for (index, document) in documents.enumerated() {
            #expect(FilePreviewClassifier.svgLoadsExternalContent(Data(document.utf8)), "\(document)")
            try write(Data(document.utf8), "bad\(index).svg", in: f.root)
            let reply = await get(f.service, "/m1/workspaces/workspace-1/file?path=bad\(index).svg")
            #expect(reply.body["type"] as? String == "unsupported" && reply.body["reason"] as? String == "undecodable", "\(document)")
        }
        // UTF-16 hides nothing: it is decoded before it is read.
        let wide = try #require((head + #"<image href="\#(outside)"/></svg>"#).data(using: .utf16))
        #expect(FilePreviewClassifier.svgLoadsExternalContent(wide))
        #expect(FilePreviewClassifier.svgLoadsExternalContent(Data([0x3C, 0x00, 0x73, 0x00, 0x76, 0x00, 0x67, 0x00])), "undecodable bytes count as external")
        #expect(sizes.calls == 0, "the drawing was never asked for")

        let local = [
            ##"<defs><linearGradient id="g"/></defs><rect fill="url(#g)" width="5" height="5"/><use xlink:href="#g"/>"##,
            #"<image href="data:image/png;base64,iVBORw0KGgo=" width="1" height="1"/>"#,
            #"<rect style="fill: url( '#g' )" width="5" height="5"/><text>src and href are just words &amp; &#160;</text>"#,
        ]
        for document in local.map({ #"<?xml version="1.0" encoding="UTF-8"?><!DOCTYPE svg>"# + head + $0 + "</svg>" }) {
            #expect(!FilePreviewClassifier.svgLoadsExternalContent(Data(document.utf8)), "\(document)")
        }
        try write(Data((head + local[0] + "</svg>").utf8), "local.svg", in: f.root)
        #expect(await get(f.service, "/m1/workspaces/workspace-1/file?path=local.svg").body["type"] as? String == "image")
        #expect(sizes.calls == 1)
    }

    @Test func imagePreviewsQueueOnTheirOwnSlotNewestFirstAndNeverHoldUpSubmits() async throws {
        let gate = PreviewGate()
        let f = try await fixture(svg: gate.rasterizer); defer { f.remove() }
        let svg = Data(#"<svg xmlns="http://www.w3.org/2000/svg" width="32" height="16"/>"#.utf8)
        try write(svg, "slow.svg", in: f.root)
        try write(try noisePNG(width: 16, height: 16), "a.png", in: f.root)
        try write(try noisePNG(width: 16, height: 16), "b.png", in: f.root)
        try write(Data("let x = 1\n".utf8), "App.swift", in: f.root)
        let service = f.service
        let phone = "cGhvbmUtdHdvLTAwMDAwMDA"

        // Another phone's svg holds the only slot while it is drawn.
        let first = Task { await get(service, "/m1/workspaces/workspace-1/file?path=slow.svg", device: "cGhvbmUtdGhyZWUtMDAwMDA") }
        await gate.waitUntilEntered()
        let older = Task { await get(service, "/m1/workspaces/workspace-1/file?path=a.png", device: phone) }
        #expect(await waitForQueue(service, count: 1))
        let newer = Task { await get(service, "/m1/workspaces/workspace-1/file?path=b.png", device: phone) }
        let dropped = await older.value
        #expect(dropped.status == 409 && dropped.body["code"] as? String == "superseded")
        #expect(await waitForQueue(service, count: 1))

        // Text and submits go on while the slot is taken.
        let text = await get(service, "/m1/workspaces/workspace-1/file?path=App.swift", device: phone)
        #expect(text.status == 200 && text.body["type"] as? String == "source")
        let submit = await service.route(method: "POST", path: "/m1/sessions/session-1/submit",
                                         body: try JSONSerialization.data(withJSONObject: ["text": "hi", "attachments": ["upload-1"]]), deviceId: phone)
        #expect(submit.status != 200 && submit.status != 202, "answered (the upload is unknown) instead of waiting")
        #expect(await service.waitingPreviews == 1)

        // A waiting preview whose phone gave up is not decoded once the slot frees.
        let abandoned = Task { await get(service, "/m1/workspaces/workspace-1/file?path=slow.svg", device: "cGhvbmUtZm91ci0wMDAwMDA") }
        #expect(await waitForQueue(service, count: 2))
        abandoned.cancel()
        gate.open()
        #expect(await first.value.body["type"] as? String == "image")
        #expect(await newer.value.body["type"] as? String == "image")
        #expect(await abandoned.value.status != 200)
        #expect(gate.calls == 1, "only the first svg was drawn")
        #expect(await service.waitingPreviews == 0)
    }

    private func waitForQueue(_ service: MobileRemoteService, count: Int) async -> Bool {
        for _ in 0..<400 { // 20 s at most; ends as soon as the queue settles
            if await service.waitingPreviews == count { return true }
            try? await Task.sleep(for: .milliseconds(50))
        }
        return false
    }

    @Test func aCancelledDecodeStopsBetweenSteps() throws {
        let png = try noisePNG(width: 64, height: 64)
        #expect(MobileWorkspaceFiles.thumbnail(png, name: "x.png", svg: nil, isCancelled: { true }) == .cancelled)
        if case .image = MobileWorkspaceFiles.thumbnail(png, name: "x.png", svg: nil) {} else { Issue.record("decodes when not cancelled") }
    }

    /// The phone draws source with a port of `SourceHighlighter` (mobile/src/lib/highlight.ts);
    /// both are held to the same expected tokens.
    @Test func theHighlighterMatchesThePhonesPortOnTheSharedFixture() throws {
        struct Token: Decodable { let kind: String; let location: Int; let length: Int }
        struct Case: Decodable { let language: String; let text: String; let tokens: [Token] }
        var url = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { url.deleteLastPathComponent() }
        let cases = try JSONDecoder().decode([Case].self, from: Data(contentsOf: url.appendingPathComponent("native/contracts/fixtures/source-highlight.json")))
        #expect(cases.count >= 20)
        #expect(Set(cases.map(\.language)) == Set(SourceLanguage.allCases.map(\.rawValue)), "every language has a case")
        for item in cases {
            let language = try #require(SourceLanguage(rawValue: item.language))
            let actual = SourceHighlighter.tokens(item.text, language: language).map { "\($0.kind.rawValue)@\($0.location)+\($0.length)" }
            #expect(actual == item.tokens.map { "\($0.kind)@\($0.location)+\($0.length)" }, "\(item.language): \(item.text)")
        }
    }
}
