import Foundation
import CryptoKit

public final class CLIStreamParser {
    private let provider: String
    private let log: (String, String) -> Void
    private let resume: (String) -> Void
    private let activity: ((AgentActivity) -> Void)?
    private let control: ((Data) -> Void)?
    private let result: (() -> Void)?
    private let usageTracker: SessionUsageTracker
    private let graphTracker: ExecutionGraphTracker?
    private let activityNamespace: String
    private let activityClock: () -> TimeInterval
    private var lines: LineSplitter
    private var seen: [String] = []
    private var assistantSeen = false
    private var lastResume: String?
    private var pendingText = ""
    private var pendingTextTruncated = false
    private var toolActivities: [String: AgentActivity] = [:]
    private var activityStarts: [String: TimeInterval] = [:]
    private var activityOrder: [String] = []
    private var modSequences: [String: Int] = [:]
    private var permissionStates: [String: String] = [:]
    private var lastTurn: AgentActivity?
    public private(set) var failed = false
    /// The run's latest failure was a lost sign-in (`CLIAuthFailure`). Each
    /// later failure or result replaces it, so a 401 the CLI recovered from
    /// does not mark a run that failed for another reason.
    public private(set) var authFailure = false
    private var stderrTail = ""
    /// One stdout line larger than this is dropped with a notice. Lines carry
    /// whole base64 pictures (twice, for Claude's tool_use_result echo), so
    /// the cap leaves room for a picture at `AgentImageSupport.maximumImageBytes`.
    public static let maximumLineBytes = 64 * 1_048_576
    /// Gemini's stream never carries pictures, so its lines get a smaller cap.
    /// Codex approval runs use `CodexApprovalChannel.maximumFrameBytes`.
    public static func maximumLineBytes(provider: String) -> Int { provider == "gemini" ? 8 * 1_048_576 : maximumLineBytes }
    private let maximumLineBytes: Int
    /// Where pictures from tool results are kept; nil drops them, as before.
    private let imageCache: AgentImageCache?
    /// The workspace, for pictures an item names only by path.
    private let imageRoot: URL?
    private let imageEntry: ((LogEntry) -> Void)?
    private var storedImages = 0
    private var imageLimitNoted = false
    /// Pictures the line being consumed carried, prepared off the actor.
    private var preparedImages: AgentPreparedImages = [:]

    public init(provider: String, log: @escaping (String, String) -> Void, resume: @escaping (String) -> Void, activityNamespace: String = UUID().uuidString, activity: ((AgentActivity) -> Void)? = nil, control: ((Data) -> Void)? = nil, result: (() -> Void)? = nil, activityClock: (() -> TimeInterval)? = nil, usage: ((SessionUsage) -> Void)? = nil, graph: ((ExecutionGraphNode) -> Void)? = nil, graphInput: String? = nil, configuredModel: String? = nil,
                images: AgentImageCache? = nil, imageRoot: URL? = nil, imageEntry: ((LogEntry) -> Void)? = nil, maximumLineBytes: Int = CLIStreamParser.maximumLineBytes) {
        self.provider = provider; self.log = log; self.resume = resume
        self.imageCache = images; self.imageRoot = imageRoot; self.imageEntry = imageEntry; self.maximumLineBytes = maximumLineBytes
        lines = LineSplitter(maximumLineBytes: maximumLineBytes)
        self.activityNamespace = activityNamespace; self.activity = activity; self.control = control; self.result = result
        usageTracker = SessionUsageTracker(provider: provider, callback: usage)
        graphTracker = MightyGraphSupport.providers.contains(provider) ? graph.map { ExecutionGraphTracker(runID: activityNamespace, input: graphInput, provider: provider, configuredModel: configuredModel, emit: $0) } : nil
        let origin = ContinuousClock.now
        self.activityClock = activityClock ?? {
            let elapsed = origin.duration(to: .now).components
            return Double(elapsed.seconds) + Double(elapsed.attoseconds) / 1e18
        }
    }
    public func push(_ string: String) { push(Data(string.utf8)) }
    public func push(_ data: Data) {
        for item in lines.push(data) {
            switch item {
            case .line(let line): consume(AgentOutputLine(line))
            case .tooLong: lineTooLong()
            }
        }
    }
    public func flush() {
        if let line = lines.finish() { consume(AgentOutputLine(line)) }
        flushText()
    }
    /// The CLI's stderr. Only Gemini reports a lost sign-in there ("Please set
    /// an Auth method", "Error authenticating: …"). Each complete line is
    /// matched on its own (`CLIAuthFailure.geminiConsole`); an unfinished last
    /// line waits for the rest of it, or for `finishStderr`.
    public func receiveStderr(_ text: String) {
        guard provider == "gemini", !text.isEmpty else { return }
        var lines = (stderrTail + text).split(separator: "\n", omittingEmptySubsequences: false)
        // Only the start of a line is matched, so a runaway line keeps just that.
        stderrTail = String(lines.removeLast().prefix(16_384))
        if lines.contains(where: { CLIAuthFailure.geminiConsole(line: String($0)) }) { authFailure = true }
    }
    /// The process ended: its last stderr line is complete.
    public func finishStderr() {
        guard provider == "gemini", !stderrTail.isEmpty else { return }
        if CLIAuthFailure.geminiConsole(line: stderrTail) { authFailure = true }
        stderrTail = ""
    }
    /// A line the runner already cut and parsed (`AgentOutputLines`).
    func receive(_ line: AgentOutputLine) { consume(line) }
    func lineTooLong() { log("system", "너무 긴 출력 한 줄을 생략했습니다.") }
    /// A Codex app-server event the approval channel mapped to the exec shape.
    func receive(object: [String: Any]) { consume(data: nil, object: object) }
    /// Lets the approval channel's mapped events use the pictures their frame
    /// carried.
    func withPreparedImages(_ images: AgentPreparedImages, _ body: () -> Void) {
        preparedImages = images; defer { preparedImages = [:] }
        body()
    }
    private func flushText() {
        if !pendingText.isEmpty { log("assistant", pendingText) }
        if pendingTextTruncated { log("system", "응답 한 메시지가 128 KiB를 넘어 뒷부분을 생략했습니다.") }
        pendingText = ""; pendingTextTruncated = false
    }
    private func errorText(_ value: Any?, fallback: String) -> String {
        if let text = value as? String { return String(text.prefix(32_768)) }
        if let value = value as? [String: Any], let text = value["message"] as? String { return String(text.prefix(32_768)) }
        return fallback
    }
    private func presentClaudeFailure(_ value: [String: Any], type: String) -> [String: Any] {
        var presented = value
        if type == "result", value["is_error"] as? Bool == true || (value["subtype"] as? String ?? "").hasPrefix("error") {
            if let errors = value["errors"] as? [String] {
                presented["errors"] = errors.map { BedrockAuthDiagnostics.runtimeFailureGuidance($0) ?? $0 }
            }
            if let text = value["result"] as? String, let guidance = BedrockAuthDiagnostics.runtimeFailureGuidance(text) {
                presented["result"] = guidance
            }
        } else if type == "assistant", let error = value["error"] as? String, !error.isEmpty,
                  var message = value["message"] as? [String: Any], let blocks = message["content"] as? [[String: Any]] {
            message["content"] = blocks.map { block in
                guard block["type"] as? String == "text", let text = block["text"] as? String,
                      let guidance = BedrockAuthDiagnostics.runtimeFailureGuidance(text) else { return block }
                var block = block; block["text"] = guidance; return block
            }
            presented["message"] = message
        }
        return presented
    }
    private func resumeIfValid(_ value: Any?) {
        guard let id = value as? String, CoreValidation.identifier(id), id != lastResume else { return }
        lastResume = id; resume(id)
    }
    private func emitUnique(_ content: String, id: String) {
        guard !content.isEmpty else { return }
        let hash = SHA256.hash(data: Data(content.utf8)).map { String(format: "%02x", $0) }.joined()
        let key = id + ":" + hash
        guard !seen.contains(key) else { return }
        seen.append(key); if seen.count > 512 { seen.removeFirst() }
        assistantSeen = true; log("assistant", content)
    }

    /// Keep each picture a tool or message returned and show it after the
    /// tool's row: a child's in its graph block, the request's in the log.
    /// The base64 never leaves this function; only references do.
    private func images(_ payloads: [(mediaType: String, base64: String)], key: String, toolID: String?, parentToolID: String? = nil, child: Bool) {
        guard let imageCache, !payloads.isEmpty else { return }
        var refs: [AgentImageRef] = []
        var failure: AgentImageError?
        let previous = toolID.flatMap { toolActivities[ActivitySupport.id(namespace: activityNamespace, key: $0)] }
        let source = [previous?.toolName, previous?.summary].compactMap { $0 }.filter { !$0.isEmpty }.reduce(into: [String]()) { if !$0.contains($1) { $0.append($1) } }.joined(separator: " · ")
        let readPath = ["read", "read_file"].contains(previous?.toolName?.lowercased() ?? "") && previous?.summary.hasPrefix("/") == true ? previous?.summary : nil
        for payload in payloads.prefix(AgentImageSupport.maximumImagesPerEntry) {
            guard storedImages < AgentImageSupport.maximumImagesPerRun else {
                if !imageLimitNoted, !child { imageLimitNoted = true; log("system", L("images.tooMany", ["count": "\(AgentImageSupport.maximumImagesPerRun)"])) }
                break
            }
            do {
                let picture = try prepared(payload.base64)?.get() ?? imageCache.prepare(base64: payload.base64, mediaType: payload.mediaType)
                refs.append(picture.ref(source: source.isEmpty ? L("images.source.agent") : source, path: readPath))
                storedImages += 1
            } catch { failure = error as? AgentImageError ?? .undecodable }
        }
        if let failure, !child { log("system", Self.imageFailure(failure)) }
        emitImages(refs, key: key, toolID: toolID, parentToolID: parentToolID, source: source)
    }
    /// The prepared result a marker stands for; nil for ordinary base64.
    private func prepared(_ base64: String) -> Result<AgentImagePrepared, AgentImageError>? {
        AgentOutputLines.isMarker(base64) ? preparedImages[base64] ?? .failure(.undecodable) : nil
    }
    private func emitImages(_ refs: [AgentImageRef], key: String, toolID: String?, parentToolID: String? = nil, source: String) {
        guard !refs.isEmpty else { return }
        let entry = LogEntry(id: ActivitySupport.id(namespace: activityNamespace, key: "image:" + key + ":" + refs.map(\.hash).joined()),
                             kind: "image", text: AgentImageSupport.entryText(refs, source: source.isEmpty ? L("images.source.agent") : source),
                             provider: provider, images: refs)
        if graphTracker?.images(entry, toolID: toolID, parentToolID: parentToolID) == true { return }
        imageEntry?(entry)
    }
    static func imageFailure(_ error: AgentImageError) -> String {
        switch error {
        case .tooLarge: return L("images.failed.tooLarge", ["limit": "\(AgentImageSupport.maximumImageBytes / 1_048_576)"])
        case .externalSVG: return L("images.failed.externalSVG")
        case .tooManyPixels: return L("images.failed.tooManyPixels")
        case .unsupportedType: return L("images.failed.unsupported")
        case .empty, .undecodable, .invalidEncoding: return L("images.failed.undecodable")
        }
    }
    /// Codex `image_generation`: the payload for `images` (a marker standing
    /// for the prepared picture), or nil when the base64 itself is unusable.
    private func generatedImage(_ result: String) -> String? {
        if let prepared = prepared(result) {
            if case .failure(let error) = prepared, [.invalidEncoding, .tooLarge, .empty].contains(error) { return nil }
            return result
        }
        guard let imageCache, let data = try? AgentImageSupport.decodeBase64(result) else { return nil }
        let key = AgentOutputLines.marker + "generated"
        do { preparedImages[key] = .success(try imageCache.prepare(data, mediaType: "image/png")) }
        catch { preparedImages[key] = .failure(error as? AgentImageError ?? .undecodable) }
        return key
    }
    /// A picture an item names only by path (Codex `image_view`): read under
    /// the Markdown path rule, never from anywhere else.
    private func pathImage(_ path: String?, key: String) {
        guard let imageCache, let path, !path.isEmpty else { return }
        guard storedImages < AgentImageSupport.maximumImagesPerRun else { return }
        guard case .file(let url, let root) = AgentImagePaths.locate(path, workspaceRoot: imageRoot) else {
            log("system", L("images.failed.outside", ["path": ActivitySupport.clean(path, maximumBytes: 512, singleLine: true)])); return
        }
        do {
            let data = try AgentImagePaths.read(url, root: root)
            let ref = try imageCache.store(data, mediaType: AgentImageSupport.mediaType(forFileName: url.lastPathComponent) ?? "image/png", source: url.lastPathComponent, path: url.path)
            storedImages += 1
            emitImages([ref], key: key, toolID: nil, source: url.lastPathComponent)
        } catch { log("system", Self.imageFailure(error as? AgentImageError ?? .undecodable)) }
    }

    private func turn(_ summary: String, state: String = "running") {
        let value = AgentActivity(id: activityNamespace, provider: provider, kind: "turn", state: state, summary: summary)
        guard value != lastTurn else { return }; lastTurn = value; activity?(value)
    }

    private func tool(id rawID: String?, name: String? = nil, input: Any? = nil, state: String, output: String? = nil, summary: String? = nil) {
        guard let rawID, !rawID.isEmpty, rawID.utf8.count <= 512 else { return }
        let id = ActivitySupport.id(namespace: activityNamespace, key: rawID)
        let previous = toolActivities[id]
        if previous?.state == "waiting", state == "running", permissionStates[id] == "waiting" { return }
        // Mods and stream-json can describe the same call in either order.
        // A late start/wait event must never resurrect a settled call.
        if let previous, ["completed", "error", "stopped"].contains(previous.state), ["running", "waiting"].contains(state) { return }
        if let previous, previous.state == "error", state == "completed" { return }
        let toolName = name ?? previous?.toolName ?? "Tool"
        let selectedSummary = summary ?? (input == nil ? previous?.summary : nil) ?? ActivitySupport.summary(tool: toolName, input: input)
        let terminal = ["completed", "error", "stopped"].contains(state)
        if !terminal, activityStarts[id] == nil {
            let now = activityClock()
            if now.isFinite, now >= 0 { activityStarts[id] = now }
        }
        let duration = terminal ? finishDuration(id: id, previous: previous) : nil
        guard let value = ActivitySupport.normalized(AgentActivity(id: id, provider: provider, kind: ActivitySupport.kind(tool: toolName), state: state, toolName: toolName, summary: selectedSummary, output: output ?? previous?.output, durationMs: duration)), value != previous else { return }
        if previous == nil {
            activityOrder.append(id)
            if activityOrder.count > 512 { let expired = activityOrder.removeFirst(); toolActivities.removeValue(forKey: expired); activityStarts.removeValue(forKey: expired); modSequences.removeValue(forKey: expired); permissionStates.removeValue(forKey: expired) }
        }
        toolActivities[id] = value
        if graphTracker?.activity(value, toolID: rawID) != true { activity?(value) }
    }

    private func finishDuration(id: String, previous: AgentActivity?) -> Double? {
        if let value = previous?.durationMs { return value }
        guard let started = activityStarts.removeValue(forKey: id) else { return nil }
        let milliseconds = (activityClock() - started) * 1_000
        return ActivitySupport.validDuration(milliseconds) ? milliseconds : nil
    }

    /// Consume the authenticated Mods envelope through the same identities and
    /// state machine as stdout, instead of scanning the desktop's own logs.
    public func receiveMod(_ value: ModMetadata) {
        guard provider == "claude" else { return }
        graphTracker?.receiveMod(value)
        usageTracker.consumeMod(value)
        if value.event == "session.usage" { return }
        if value.event == "turn.start" { turn("Claude 응답 생성 중"); return }
        if value.event == "turn.complete" {
            // A subagent or model turn ending is not the CLI process ending.
            if value.agentId == nil { turn("Claude 응답 마무리 중") }
            return
        }
        guard let rawID = value.toolUseId, let name = value.tool else { return }
        let id = ActivitySupport.id(namespace: activityNamespace, key: rawID)
        if let sequence = value.sequence {
            if let previous = modSequences[id], previous >= sequence { return }
            modSequences[id] = sequence
        }
        switch value.event {
        case "tool.call": tool(id: rawID, name: name, state: name == "AskUserQuestion" ? "waiting" : "running", summary: value.summary)
        case "tool.waiting":
            // The stdio approval owns this call's waiting state once observed.
            // A delayed best-effort Mod must not undo an explicit UI response.
            if permissionStates[id] == nil { tool(id: rawID, name: name, state: "waiting", summary: value.summary) }
        case "tool.complete": tool(id: rawID, name: name, state: value.isError == true ? "error" : "completed", output: value.output, summary: value.summary)
        default: break
        }
    }

    /// Missing tool results remain explicit; process exit is not proof that an
    /// individual tool succeeded. The runner emits the final turn separately.
    public func finishActivities(stopped: Bool) {
        for id in activityOrder {
            guard var value = toolActivities[id], ["running", "waiting"].contains(value.state) else { continue }
            value.state = stopped ? "stopped" : "error"
            value.output = value.output ?? "도구 결과를 받기 전에 실행이 종료되었습니다."
            value.durationMs = finishDuration(id: id, previous: value)
            toolActivities[id] = value
            if graphTracker?.activity(value) != true { activity?(value) }
        }
    }
    /// Called once after draining stdout and settling tools, before the runner
    /// publishes its terminal status. Child turns never invoke this themselves.
    public func finishGraph(state: String) { graphTracker?.finish(state: state) }
    /// The Codex root thread once `thread.started` named it.
    var codexRootThread: String? { graphTracker?.codexRootThread }
    /// Subagents read from Codex's own session records.
    func receiveCodexSessions(_ agents: [CodexSessionAgent]) { for agent in agents { graphTracker?.codexSession(agent) } }
    /// A follow-up the runner wrote to Claude's stdin during this turn.
    public func steer(id: String, text: String) { graphTracker?.steer(id: id, text: text) }
    func permissionActivity(_ request: ToolPermissionRequest, state: String) {
        permissionStates[ActivitySupport.id(namespace: activityNamespace, key: request.toolUseId)] = state
        tool(id: request.toolUseId, name: request.toolName, state: state, summary: request.summary)
    }
    private func consume(_ line: AgentOutputLine) {
        guard !line.data.isEmpty else { return }
        preparedImages = line.images; defer { preparedImages = [:] }
        consume(data: line.data, object: line.object)
    }
    /// `data` is the raw line, nil for an event mapped from another shape.
    private func consume(data: Data?, object: Any?) {
        guard let object else {
            // Only the shown prefix is decoded, however long the line is.
            guard let data else { return }
            let head = data.prefix(32_771)
            var text = String(decoding: head, as: UTF8.self)
            if head.count < data.count, text.last == "\u{FFFD}" { text.removeLast() }
            // Gemini prints its sign-in consent prompt as a plain line, never as JSON;
            // a cut-off JSON line may quote anything, so it never counts.
            if provider == "gemini", !text.drop(while: \.isWhitespace).hasPrefix("{"), CLIAuthFailure.geminiConsole(line: text) { authFailure = true }
            log("output", String(text.prefix(32_768))); return
        }
        guard var value = object as? [String: Any], let type = value["type"] as? String else { return }
        // Read before the Bedrock rewrite below, which would hide the original text.
        if provider == "claude", ExecutionGraphTracker.parentToolID(value) == nil, !ClaudeStream.isNotificationResult(value),
           type == "result" || (type == "assistant" && (value["error"] as? String)?.isEmpty == false) {
            authFailure = CLIAuthFailure.claude(value)
        }
        // Normalize confirmed provider failures before both transcript and graph
        // consume them. Ordinary assistant text and tool results stay intact.
        if provider == "claude" { value = presentClaudeFailure(value, type: type) }
        let claudeChild = provider == "claude" && ExecutionGraphTracker.parentToolID(value) != nil
        if !claudeChild { usageTracker.consume(value) }
        graphTracker?.consume(value)
        switch provider {
        case "claude":
            if ["control_request", "control_response", "control_cancel_request"].contains(type) {
                if let data { control?(data) }
                return
            }
            if !claudeChild { resumeIfValid(value["session_id"]) }
            if type == "system", value["subtype"] as? String == "permission_denied" {
                tool(id: value["tool_use_id"] as? String, name: value["tool_name"] as? String, state: "error", output: errorText(value["message"], fallback: "Claude 권한 규칙 또는 선택한 모드에서 거부했습니다."))
            } else if type == "system", value["subtype"] as? String == "compact_boundary" {
                if !claudeChild { log("system", ContextCompaction.title + " · " + ContextCompaction.claudeSummary(value["compact_metadata"])) }
            } else if type == "assistant", let message = value["message"] as? [String: Any], let blocks = message["content"] as? [[String: Any]] {
                let content = blocks.filter { $0["type"] as? String == "text" }.compactMap { $0["text"] as? String }.joined(separator: "\n")
                if !claudeChild { emitUnique(content, id: (value["uuid"] as? String) ?? (message["id"] as? String) ?? "message") }
                for block in blocks where block["type"] as? String == "tool_use" {
                    let name = block["name"] as? String
                    tool(id: block["id"] as? String, name: name, input: block["input"], state: name == "AskUserQuestion" ? "waiting" : "running")
                }
                images(AgentImageSupport.payloads(in: blocks.filter { $0["type"] as? String == "image" }), key: (value["uuid"] as? String) ?? (message["id"] as? String) ?? "message",
                       toolID: nil, parentToolID: ExecutionGraphTracker.parentToolID(value), child: claudeChild)
            } else if type == "user", let message = value["message"] as? [String: Any], let blocks = message["content"] as? [[String: Any]] {
                for block in blocks where block["type"] as? String == "tool_result" {
                    tool(id: block["tool_use_id"] as? String, state: block["is_error"] as? Bool == true ? "error" : "completed", output: ActivitySupport.output(block["content"]))
                    if let toolID = block["tool_use_id"] as? String, !toolID.isEmpty, toolID.utf8.count <= 512 {
                        images(AgentImageSupport.payloads(in: block["content"]), key: toolID, toolID: toolID,
                               parentToolID: ExecutionGraphTracker.parentToolID(value), child: claudeChild)
                    }
                }
            } else if type == "result" {
                guard !claudeChild else { return }
                // Not this request's result: see `ClaudeStream.isNotificationResult`.
                guard !ClaudeStream.isNotificationResult(value) else { return }
                if value["is_error"] as? Bool == true || (value["subtype"] as? String ?? "").hasPrefix("error") {
                    failed = true
                    let errors = (value["errors"] as? [String])?.joined(separator: "\n")
                    log("error", errors?.isEmpty == false ? errors! : errorText(value["result"], fallback: "Claude 실행 중 오류가 발생했습니다."))
                } else if !assistantSeen, let text = value["result"] as? String { emitUnique(text, id: "result") }
                turn("Claude 응답 마무리 중")
                result?()
            }
        case "codex":
            if type == "thread.started" { resumeIfValid(value["thread_id"]) }
            if type == "turn.started" { turn("Codex 응답 생성 중") }
            if type == "turn.completed" { turn("Codex 응답 마무리 중") }
            if ["item.started", "item.updated", "item.completed"].contains(type), let item = value["item"] as? [String: Any], let itemType = item["type"] as? String {
                let ended = type == "item.completed"
                let state = item["status"] as? String == "failed" ? "error" : ended ? "completed" : "running"
                switch itemType {
                case "agent_message": if ended, let text = item["text"] as? String { emitUnique(text, id: item["id"] as? String ?? "message") }
                case "command_execution":
                    let output = item["aggregated_output"] as? String
                    tool(id: item["id"] as? String, name: "command_execution", input: item, state: state, output: ended ? output : nil)
                    if activity == nil, ended, let output, !output.isEmpty { log("output", output) }
                case "file_change": tool(id: item["id"] as? String, name: "file_change", input: item, state: state)
                case "web_search": tool(id: item["id"] as? String, name: "web_search", input: item, state: state)
                case "context_compaction": if ended { log("system", ContextCompaction.title + " · " + ContextCompaction.codexSummary) }
                case "collab_tool_call", "collab_agent_tool_call":
                    if let collaboration = CodexCollaborationItem(item) {
                        tool(id: collaboration.id, name: collaboration.tool, state: state,
                             output: collaboration.output, summary: collaboration.summary)
                    }
                case "mcp_tool_call":
                    let name = [item["server"] as? String, item["tool"] as? String].compactMap { $0 }.joined(separator: ".")
                    // `codex exec --json` writes `"error": null` beside a result:
                    // JSON null is NSNull, which `??` would take as present.
                    let error = item["error"].flatMap { $0 is NSNull ? nil : $0 }
                    tool(id: item["id"] as? String, name: name.isEmpty ? "MCP" : name, input: item["arguments"], state: state, output: ActivitySupport.output(error ?? item["result"]))
                    if ended, let id = item["id"] as? String, !id.isEmpty, id.utf8.count <= 512 { images(AgentImageSupport.payloads(in: item["result"]), key: id, toolID: id, child: false) }
                case "image_view":
                    // The agent looked at a local picture; only its path is reported.
                    if ended, let id = item["id"] as? String { pathImage(item["path"] as? String, key: id) }
                case "image_generation":
                    if ended, let id = item["id"] as? String, !id.isEmpty, id.utf8.count <= 512 {
                        // `result` is the picture in base64, decoded once; a saved
                        // file is the fallback when it is not.
                        if let result = item["result"] as? String, !result.isEmpty, imageCache != nil, let picture = generatedImage(result) {
                            images([("image/png", picture)], key: id, toolID: nil, child: false)
                        } else { pathImage((item["saved_path"] ?? item["savedPath"]) as? String, key: id) }
                    }
                default: break // Reasoning text is deliberately not copied.
                }
            }
            if type == "turn.failed" || type == "error" {
                failed = true
                let text = errorText(value["error"] ?? value["message"], fallback: "Codex 실행 중 오류가 발생했습니다.")
                // A "Reconnecting…" notice is not the run's failure; it keeps the previous one.
                if type == "turn.failed" || !CLIAuthFailure.isCodexRetryNotice(text) { authFailure = CLIAuthFailure.codex(text: text) }
                log("error", text)
            }
            if type == "turn.completed" { authFailure = false }
        case "gemini":
            if type == "init" { resumeIfValid(value["session_id"]); turn("Gemini 응답 생성 중") }
            if type == "message", value["role"] as? String == "assistant", let text = value["content"] as? String {
                if value["delta"] as? Bool == true {
                    let remaining = max(0, 131_072 - pendingText.utf8.count)
                    if text.utf8.count > remaining { pendingTextTruncated = true }
                    pendingText += ActivitySupport.prefixUTF8(text, maximumBytes: remaining)
                }
                else { flushText(); if !text.isEmpty { log("assistant", text) } }
            } else if type == "tool_use" {
                flushText()
                if let name = value["tool_name"] as? String {
                    tool(id: value["tool_id"] as? String, name: name, input: value["parameters"], state: "running")
                    if activity == nil { log("system", "도구 실행 · \(name.prefix(160))") }
                }
            } else if type == "tool_result" {
                flushText()
                let failed = value["status"] as? String == "error"
                tool(id: value["tool_id"] as? String, state: failed ? "error" : "completed", output: failed ? errorText(value["error"], fallback: "Gemini 도구 실행이 실패했습니다.") : value["output"] as? String)
                if activity == nil {
                    if let text = value["output"] as? String, !text.isEmpty { log("output", text) }
                    if failed { log("system", errorText(value["error"], fallback: "Gemini 도구 실행이 실패했습니다.")) }
                }
            } else if type == "error" {
                flushText(); let warning = value["severity"] as? String == "warning"
                let text = errorText(value["message"], fallback: "Gemini 실행 중 오류가 발생했습니다.")
                if !warning { failed = true; authFailure = CLIAuthFailure.gemini(text: text) }
                log(warning ? "system" : "error", text)
            } else if type == "result" {
                flushText()
                if value["status"] as? String == "error" {
                    failed = true
                    let text = errorText(value["error"], fallback: "Gemini 실행 중 오류가 발생했습니다.")
                    authFailure = CLIAuthFailure.gemini(text: text)
                    log("error", text)
                } else { authFailure = false }
                turn("Gemini 응답 마무리 중")
            }
        default: break
        }
    }
}

/// Preserve a UTF-8 character split across two reads from a shell pipe.
final class UTF8StreamDecoder {
    private var pending = Data()
    func push(_ data: Data) -> String {
        pending.append(data)
        if let text = String(data: pending, encoding: .utf8) { pending.removeAll(keepingCapacity: true); return text }
        for trailing in 1...min(3, pending.count) {
            if let text = String(data: pending.dropLast(trailing), encoding: .utf8) { pending = Data(pending.suffix(trailing)); return text }
        }
        let text = String(decoding: pending, as: UTF8.self); pending.removeAll(); return text
    }
    func flush() -> String { defer { pending.removeAll() }; return String(decoding: pending, as: UTF8.self) }
}

public enum ClaudeStream {
    /// A resumed session whose earlier process left a background task behind
    /// first reports that task as stopped, and closes that report with a
    /// `result` of its own (`origin.kind == "task-notification"`) before it even
    /// reads the new request. It is not the request's result: treating it as one
    /// closes Claude's stdin early, and every approval or question in the real
    /// turn then fails with "Stream closed".
    public static func isNotificationResult(_ event: [String: Any]) -> Bool {
        guard event["type"] as? String == "result", let origin = event["origin"] as? [String: Any] else { return false }
        return origin["kind"] as? String == "task-notification"
    }
}
