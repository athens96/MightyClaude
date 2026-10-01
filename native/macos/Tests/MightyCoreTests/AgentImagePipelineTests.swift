import CoreGraphics
import Foundation
import ImageIO
import Testing
@testable import MightyCore

private final class PipelineRecorder: @unchecked Sendable {
    private let lock = NSLock()
    private var stored: [RunEvent] = []
    func append(_ event: RunEvent) { lock.lock(); stored.append(event); lock.unlock() }
    func values() -> [RunEvent] { lock.lock(); defer { lock.unlock() }; return stored }
}

/// Lines carrying whole screenshots go from a real child process through the
/// runner intact: cut without scanning byte by byte, parsed and their
/// pictures cached off the runner's actor, in order with what follows.
@Suite(.serialized)
final class AgentImagePipelineTests {
    private var directories: [URL] = []
    deinit { for directory in directories { try? FileManager.default.removeItem(at: directory) } }
    private func temporary(_ name: String) throws -> URL {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-pipeline-\(name)-" + UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true); directories.append(url)
        return WorkspaceFiles.realRoot(url)
    }
    private func wait(_ predicate: () -> Bool) async throws {
        let deadline = Date().addingTimeInterval(180)
        while !predicate() {
            guard Date() < deadline else { throw MightyError("Timed out waiting for the pipeline fixture") }
            try await Task.sleep(for: .milliseconds(20))
        }
    }
    /// Random pixels do not compress: a PNG of about `width * height * 3` bytes.
    private func noisePNG(width: Int, height: Int) -> Data {
        var bytes = [UInt8](repeating: 0, count: width * height * 4)
        arc4random_buf(&bytes, bytes.count)
        let provider = CGDataProvider(data: Data(bytes) as CFData)!
        let image = CGImage(width: width, height: height, bitsPerComponent: 8, bitsPerPixel: 32, bytesPerRow: width * 4, space: CGColorSpaceCreateDeviceRGB(),
                            bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.noneSkipLast.rawValue), provider: provider, decode: nil, shouldInterpolate: false, intent: .defaultIntent)!
        let data = NSMutableData()
        let destination = CGImageDestinationCreateWithData(data, "public.png" as CFString, 1, nil)!
        CGImageDestinationAddImage(destination, image, nil); CGImageDestinationFinalize(destination)
        return data as Data
    }
    private func json(_ value: [String: Any]) throws -> Data {
        var data = try JSONSerialization.data(withJSONObject: value); data.append(10); return data
    }

    @Test func aThirtyMegabyteScreenshotLineAndTheLineAfterItArriveIntact() async throws {
        let directory = try temporary("run"), cacheDirectory = try temporary("cache")
        // A 3 MB screenshot and 25 MB of other result data on the same line.
        let picture = noisePNG(width: 1_000, height: 1_000)
        #expect(picture.count > 2_000_000)
        let shot: [String: Any] = ["type": "item.completed", "item": [
            "id": "mcp-1", "type": "mcp_tool_call", "server": "browser", "tool": "screenshot", "status": "completed", "error": NSNull(),
            "result": ["content": [["type": "text", "text": "Took a screenshot"], ["type": "image", "data": picture.base64EncodedString(), "mimeType": "image/png"]],
                       "_meta": ["pad": String(repeating: "x", count: 25_000_000)]],
        ]]
        var stream = try json(["type": "turn.started"])
        stream += try json(["type": "item.started", "item": ["id": "mcp-1", "type": "mcp_tool_call", "server": "browser", "tool": "screenshot", "arguments": [:], "status": "in_progress"]])
        let line = try json(shot)
        #expect(line.count > 28_000_000)
        stream += line
        stream += try json(["type": "item.completed", "item": ["id": "msg-1", "type": "agent_message", "text": "after the picture"]])
        stream += try json(["type": "turn.completed", "usage": ["input_tokens": 1, "cached_input_tokens": 0, "output_tokens": 1]])
        try stream.write(to: directory.appendingPathComponent("stream.jsonl"))
        let binary = directory.appendingPathComponent("codex")
        let source = """
        #!/bin/sh
        if [ "$1" = "--version" ]; then printf 'codex-cli 0.153.4\\n'; exit 0; fi
        for argument in "$@"; do if [ "$argument" = app-server ]; then exit 3; fi; done
        /bin/cat > /dev/null
        /bin/cat '\(directory.appendingPathComponent("stream.jsonl").path)'
        """
        try Data(source.utf8).write(to: binary)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: binary.path)

        let cache = AgentImageCache(directory: cacheDirectory)
        let service = ProviderService(binaryOverrides: ["codex": binary])
        let events = PipelineRecorder()
        // A slow consumer: the reader has to wait for it rather than drop chunks.
        let runner = ProcessRunner(providerService: service, pluginDirectory: directory, imageCache: cache, onEvent: { events.append($0); usleep(150) })
        let workspace = Workspace(id: "workspace", name: "Fixture", path: directory.path)
        do {
            try await runner.start(request: StartRunRequest(sessionId: "pane", workspaceId: workspace.id, input: "look", provider: "codex"), workspace: workspace)
            try await wait { events.values().contains { $0.type == "status" && ["completed", "error", "stopped"].contains($0.status ?? "") } }
            let values = events.values()
            let image = try #require(values.firstIndex { $0.entry?.kind == "image" })
            let ref = try #require(values[image].entry?.images?.first)
            #expect(ref.hash == AgentImageSupport.sha256(picture))
            #expect(ref.width == 1_000); #expect(ref.source.contains("browser.screenshot"))
            #expect(cache.data(for: ref) == picture)
            let after = try #require(values.firstIndex { $0.entry?.kind == "assistant" && $0.entry?.text == "after the picture" })
            #expect(image < after)
            #expect(!values.contains { $0.entry?.text.contains("너무 긴") == true })
            #expect(values.last(where: { $0.type == "status" })?.status == "completed")
        } catch { await runner.shutdown(); await service.shutdown(); throw error }
        await runner.shutdown(); await service.shutdown()
    }

    @Test func theSplitterCutsAcrossChunksAndSkipsOverlongLines() {
        var splitter = LineSplitter(maximumLineBytes: 8)
        func lines(_ items: [LineSplitter.Item]) -> [String] {
            items.map { if case .line(let data) = $0 { return String(decoding: data, as: UTF8.self) } else { return "<long>" } }
        }
        #expect(lines(splitter.push(Data("ab".utf8))).isEmpty)
        #expect(lines(splitter.push(Data("c\n\nde\nf".utf8))) == ["abc", "de"])
        #expect(lines(splitter.push(Data("ghijklmno".utf8))) == ["<long>"])
        // The rest of the long line is skipped up to its newline, once.
        #expect(lines(splitter.push(Data("pq\nok\n".utf8))) == ["ok"])
        // Exactly the limit is kept.
        #expect(lines(splitter.push(Data("12345678\ntail".utf8))) == ["12345678"])
        #expect(splitter.finish().map { String(decoding: $0, as: UTF8.self) } == "tail")
        #expect(splitter.finish() == nil)
    }

    @Test func preparedLinesCarryMarkersInsteadOfBase64AndKeepTheEcho() throws {
        let cache = AgentImageCache(directory: try temporary("cache"))
        let picture = noisePNG(width: 160, height: 120)
        let claude: [String: Any] = ["type": "user", "tool_use_result": ["type": "image", "data": picture.base64EncodedString(), "mimeType": "image/png"],
                                     "message": ["content": [["type": "tool_result", "tool_use_id": "read-1",
                                                              "content": [["type": "image", "source": ["type": "base64", "media_type": "image/png", "data": picture.base64EncodedString()]]]]]]]
        var lines = AgentOutputLines(maximumLineBytes: CLIStreamParser.maximumLineBytes, cache: cache)
        let items = lines.push(try json(claude))
        guard case .line(let line) = try #require(items.first) else { Issue.record("expected a line"); return }
        #expect(line.images.count == 1)
        #expect(try line.images.values.first?.get().hash == AgentImageSupport.sha256(picture))
        let object = try #require(line.object as? [String: Any])
        let block = try #require((((object["message"] as? [String: Any])?["content"] as? [[String: Any]])?.first?["content"] as? [[String: Any]])?.first)
        #expect(((block["source"] as? [String: Any])?["data"] as? String).map(AgentOutputLines.isMarker) == true)
        // The echo the parser never reads is not decoded.
        #expect((object["tool_use_result"] as? [String: Any])?["data"] as? String == picture.base64EncodedString())

        var entries: [LogEntry] = []
        let parser = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in }, images: cache, imageEntry: { entries.append($0) })
        parser.receive(line)
        #expect(entries.first?.images?.first?.hash == AgentImageSupport.sha256(picture))
    }

    /// Only the places the parser reads pictures from are rewritten: never a
    /// tool's input or arguments, nor an app-server request's params.
    @Test func onlyPicturesTheParserReadsAreReplaced() throws {
        let cache = AgentImageCache(directory: try temporary("cache"))
        let base64 = noisePNG(width: 160, height: 120).base64EncodedString()
        let image: [String: Any] = ["type": "image", "data": base64, "mimeType": "image/png"]
        let pad = String(repeating: "x", count: AgentOutputLines.preparedLineBytes)
        func prepared(_ value: [String: Any]) throws -> AgentOutputLine {
            var value = value; value["pad"] = pad
            var lines = AgentOutputLines(maximumLineBytes: CLIStreamParser.maximumLineBytes, cache: cache)
            guard case .line(let line) = try #require(lines.push(try json(value)).first) else { throw MightyError("expected a line") }
            return line
        }
        func isMarker(_ value: Any?) -> Bool { (value as? String).map(AgentOutputLines.isMarker) == true }

        // Claude: the message's image block is read, a tool_use input is not.
        let claude = try prepared(["type": "assistant", "message": ["content": [
            ["type": "tool_use", "id": "t1", "name": "Upload", "input": ["file": image, "content": [image]]], image]]])
        let blocks = try #require(((claude.object as? [String: Any])?["message"] as? [String: Any])?["content"] as? [[String: Any]])
        let input = try #require(blocks[0]["input"] as? [String: Any])
        #expect((input["file"] as? [String: Any])?["data"] as? String == base64)
        #expect(((input["content"] as? [[String: Any]])?.first)?["data"] as? String == base64)
        #expect(isMarker(blocks[1]["data"]))
        #expect(claude.images.count == 1)

        // Codex exec: a completed MCP call's result is read, its arguments are not.
        let exec = try prepared(["type": "item.completed", "item": ["id": "m1", "type": "mcp_tool_call", "arguments": ["content": [image]], "result": ["content": [image]]]])
        let item = try #require((exec.object as? [String: Any])?["item"] as? [String: Any])
        #expect((((item["arguments"] as? [String: Any])?["content"] as? [[String: Any]])?.first)?["data"] as? String == base64)
        #expect(isMarker((((item["result"] as? [String: Any])?["content"] as? [[String: Any]])?.first)?["data"]))
        // A started item is not read for pictures.
        let started = try prepared(["type": "item.started", "item": ["id": "m1", "type": "mcp_tool_call", "result": ["content": [image]]]])
        #expect(started.images.isEmpty)

        // App-server: an item/completed notification is read, a request is not.
        let notification = try prepared(["method": "item/completed", "params": ["item": ["id": "g1", "type": "imageGeneration", "result": base64]]])
        #expect(isMarker((((notification.object as? [String: Any])?["params"] as? [String: Any])?["item"] as? [String: Any])?["result"]))
        let request = try prepared(["id": 7, "method": "item/completed", "params": ["item": ["id": "g2", "type": "imageGeneration", "result": base64], "extra": image]])
        let params = try #require((request.object as? [String: Any])?["params"] as? [String: Any])
        #expect((params["item"] as? [String: Any])?["result"] as? String == base64)
        #expect((params["extra"] as? [String: Any])?["data"] as? String == base64)
        #expect(request.images.isEmpty)
    }

    @Test func aLongNonJSONLineIsShownByItsPrefixOnly() {
        var logs: [(String, String)] = []
        let parser = CLIStreamParser(provider: "codex", log: { logs.append(($0, $1)) }, resume: { _ in })
        parser.push(Data((String(repeating: "가", count: 100_000) + "\n").utf8))
        #expect(logs.count == 1); #expect(logs.first?.0 == "output")
        let text = logs.first?.1 ?? ""
        #expect(!text.isEmpty); #expect(text.count <= 32_768)
        #expect(text.allSatisfy { $0 == "가" })
    }
}
