import Foundation

/// Splits a byte stream into newline-terminated lines. Newlines are found
/// with `memchr` and whole runs are appended at once; a finished line is
/// handed over and the buffer starts empty, so one huge line does not keep
/// its capacity for the rest of the run. A line longer than the limit is
/// reported once and skipped up to its newline.
struct LineSplitter {
    enum Item { case line(Data), tooLong }
    let maximumLineBytes: Int
    private var buffer = Data()
    private var dropping = false

    init(maximumLineBytes: Int) { self.maximumLineBytes = maximumLineBytes }

    /// Bytes of the unfinished line held until its newline arrives.
    var heldBytes: Int { buffer.count }

    /// Complete, non-empty lines (without their newline) in arrival order.
    mutating func push(_ data: Data) -> [Item] {
        var items: [Item] = []
        data.withUnsafeBytes { (raw: UnsafeRawBufferPointer) in
            guard let base = raw.baseAddress else { return }
            var start = 0
            while start < raw.count {
                let found = memchr(base + start, 10, raw.count - start)
                let end = found.map { base.distance(to: UnsafeRawPointer($0)) } ?? raw.count
                if !dropping {
                    if buffer.count + (end - start) > maximumLineBytes {
                        buffer = Data(); dropping = true; items.append(.tooLong)
                    } else if end > start {
                        buffer.append(base.advanced(by: start).assumingMemoryBound(to: UInt8.self), count: end - start)
                    }
                }
                guard found != nil else { break }
                if !dropping, !buffer.isEmpty { items.append(.line(buffer)) }
                buffer = Data(); dropping = false
                start = end + 1
            }
        }
        return items
    }

    /// The unterminated last line at end of stream, if any.
    mutating func finish() -> Data? {
        defer { buffer = Data(); dropping = false }
        return dropping || buffer.isEmpty ? nil : buffer
    }
}

/// Pictures a line carried, already decoded, checked and cached, keyed by
/// the marker that replaced their base64 in the parsed value.
typealias AgentPreparedImages = [String: Result<AgentImagePrepared, AgentImageError>]

/// One stdout line of a CLI's JSON stream, parsed before it reaches the
/// runner's actor (`AgentOutputLines`).
final class AgentOutputLine: @unchecked Sendable {
    let data: Data
    /// The parsed JSON value; nil when the line is not JSON.
    let object: Any?
    let images: AgentPreparedImages

    init(data: Data, object: Any?, images: AgentPreparedImages = [:]) {
        self.data = data; self.object = object; self.images = images
    }

    /// Parses on the calling thread, without preparing pictures.
    convenience init(_ data: Data) { self.init(data: data, object: try? JSONSerialization.jsonObject(with: data)) }
}

/// Stdout of a parser-driven run, cut into lines and parsed off the runner's
/// actor. Base64 pictures in large lines are decoded, checked, hashed and
/// written to the cache here too, so one 30 MB screenshot line never holds
/// the actor every pane shares; the parser later only attributes them.
struct AgentOutputLines {
    enum Item { case line(AgentOutputLine), tooLong }
    /// Lines below this are left for the parser to handle as before.
    static let preparedLineBytes = 65_536
    /// Stands in for a picture's base64 after it was prepared.
    static let marker = "mighty-prepared-image:" + UUID().uuidString + ":"

    private var splitter: LineSplitter
    private let cache: AgentImageCache?

    init(maximumLineBytes: Int, cache: AgentImageCache?) {
        splitter = LineSplitter(maximumLineBytes: maximumLineBytes); self.cache = cache
    }

    mutating func push(_ data: Data) -> [Item] { splitter.push(data).map(prepare) }
    /// Bytes of the unfinished line; counted against `ChildOutputAllowance`.
    var heldBytes: Int { splitter.heldBytes }
    mutating func finish() -> [Item] { splitter.finish().map { [prepare(.line($0))] } ?? [] }

    private func prepare(_ item: LineSplitter.Item) -> Item {
        guard case .line(let data) = item else { return .tooLong }
        guard let object = try? JSONSerialization.jsonObject(with: data) else { return .line(AgentOutputLine(data: data, object: nil)) }
        guard let cache, data.count >= Self.preparedLineBytes else { return .line(AgentOutputLine(data: data, object: object)) }
        var images: AgentPreparedImages = [:]
        let replaced = Self.prepareImages(object, cache: cache, images: &images)
        return .line(AgentOutputLine(data: data, object: replaced, images: images))
    }

    static func isMarker(_ value: String) -> Bool { value.hasPrefix(marker) }

    /// Replaces each picture's base64 with a marker naming its prepared
    /// result, only where the parser reads pictures: image blocks of a Claude
    /// `assistant` message and the `content` of a `user` message's
    /// `tool_result` (as `AgentImageSupport.payloads` walks it), and the
    /// `result` of a completed Codex `mcp_tool_call` or `image_generation`
    /// item, in `codex exec` events and in app-server `item/completed`
    /// notifications. Tool `input`/`arguments`, app-server requests (which
    /// carry an `id`) and Claude's `tool_use_result` echo stay as they are.
    private static func prepareImages(_ value: Any, cache: AgentImageCache, images: inout AgentPreparedImages) -> Any {
        guard var record = value as? [String: Any] else { return value }
        func prepared(_ base64: String, _ mediaType: String) -> String {
            let key = marker + "\(images.count)"
            do { images[key] = .success(try cache.prepare(base64: base64, mediaType: mediaType)) }
            catch { images[key] = .failure(error as? AgentImageError ?? .undecodable) }
            return key
        }
        /// Mirrors `AgentImageSupport.payloads(in:depth:)`.
        func payloads(_ value: Any, depth: Int) -> Any {
            guard depth < 4, images.count < AgentImageSupport.maximumImagesPerRun else { return value }
            if var block = value as? [String: Any] {
                if block["type"] as? String == "image" {
                    if var source = block["source"] as? [String: Any], source["type"] as? String == "base64",
                       let data = source["data"] as? String, let mediaType = source["media_type"] as? String {
                        source["data"] = prepared(data, mediaType); block["source"] = source
                    } else if let data = block["data"] as? String, let mediaType = (block["mimeType"] ?? block["mime_type"] ?? block["media_type"]) as? String {
                        block["data"] = prepared(data, mediaType)
                    }
                    return block
                }
                if let content = block["content"] { block["content"] = payloads(content, depth: depth + 1) }
                return block
            }
            guard var blocks = value as? [Any] else { return value }
            for index in blocks.indices.prefix(64) { blocks[index] = payloads(blocks[index], depth: depth + 1) }
            return blocks
        }
        func item(_ value: Any?, mcp: String, generation: String) -> Any? {
            guard var item = value as? [String: Any], let type = item["type"] as? String else { return value }
            if type == mcp, let result = item["result"] { item["result"] = payloads(result, depth: 0) }
            if type == generation, let result = item["result"] as? String, !result.isEmpty, images.count < AgentImageSupport.maximumImagesPerRun {
                item["result"] = prepared(result, "image/png")
            }
            return item
        }
        switch record["type"] as? String {
        case "assistant", "user":
            let type = record["type"] as? String
            guard var message = record["message"] as? [String: Any], var blocks = message["content"] as? [Any] else { return record }
            for index in blocks.indices {
                guard var block = blocks[index] as? [String: Any] else { continue }
                if type == "assistant", block["type"] as? String == "image" { blocks[index] = payloads(block, depth: 0) }
                else if type == "user", block["type"] as? String == "tool_result", let content = block["content"] {
                    block["content"] = payloads(content, depth: 0); blocks[index] = block
                }
            }
            message["content"] = blocks; record["message"] = message
        case "item.completed":
            if let value = item(record["item"], mcp: "mcp_tool_call", generation: "image_generation") { record["item"] = value }
        default:
            if record["method"] as? String == "item/completed", record["id"] == nil, var params = record["params"] as? [String: Any] {
                if let value = item(params["item"], mcp: "mcpToolCall", generation: "imageGeneration") { params["item"] = value }
                record["params"] = params
            }
        }
        return record
    }
}
