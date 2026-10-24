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
    /// Antigravity streamed this turn's text as deltas, so its result's full
    /// `response` repeats it.
    private var antigravityStreamed = false
    /// The conversation an Antigravity run was asked to continue; agy starts a
    /// new one when it cannot find it, and says so once.
    private let requestedResume: String?
    private var newConversationNoted = false
    /// The turn's refused actions, for the pane's card; nil keeps the older
    /// notice that tells how to add an allow rule by hand.
    private let antigravityPermission: ((AntigravityPermissionRequest) -> Void)?
    private var denials = AntigravityDenialCollector()
    /// The text of the latest Claude message the CLI marked as a lost sign-in,
    /// until a result ends its turn (`CLIAuthFailure.claude(result:repeating:)`).
    private var authFailureText: String?
    private var stderrTail = ""
    /// One stdout line larger than this is dropped with a notice. Lines carry
    /// whole base64 pictures (twice, for Claude's tool_use_result echo), so
    /// the cap leaves room for a picture at `AgentImageSupport.maximumImageBytes`.
    public static let maximumLineBytes = 64 * 1_048_576
    /// Antigravity's stream never carries pictures, so its lines get a smaller cap.
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
    /// Claude's execution checklist and background tasks (`ClaudePlanMode.swift`).
    private let todos: ((TodoProgress) -> Void)?
    private let background: ((BackgroundWork) -> Void)?
    private let todoTracker: TodoProgressTracker
    private let backgroundTracker: BackgroundTaskTracker
    /// A point where the CLI may have nothing left to do after the request's
    /// result: a task notification's own result, or `session_state_changed: idle`.
    private let settled: (() -> Void)?
    /// A plan the user cancelled interrupts the turn; that interruption is not a failure.
    private var interruptedResultExpected = false
    /// A background task the CLI reported as started is still running.
    public var backgroundRunning: Bool { !backgroundTracker.work.running.isEmpty }
    func suppressInterruptedResult(_ value: Bool = true) { interruptedResultExpected = value }

    public init(provider: String, log: @escaping (String, String) -> Void, resume: @escaping (String) -> Void, activityNamespace: String = UUID().uuidString, activity: ((AgentActivity) -> Void)? = nil, control: ((Data) -> Void)? = nil, result: (() -> Void)? = nil, activityClock: (() -> TimeInterval)? = nil, usage: ((SessionUsage) -> Void)? = nil, graph: ((ExecutionGraphNode) -> Void)? = nil, graphInput: String? = nil, configuredModel: String? = nil,
                images: AgentImageCache? = nil, imageRoot: URL? = nil, imageEntry: ((LogEntry) -> Void)? = nil, maximumLineBytes: Int = CLIStreamParser.maximumLineBytes,
                todos: ((TodoProgress) -> Void)? = nil, background: ((BackgroundWork) -> Void)? = nil, backgroundClock: @escaping () -> Date = Date.init,
                savedTodos: TodoProgress? = nil, settled: (() -> Void)? = nil, requestedResume: String? = nil,
                antigravityPermission: ((AntigravityPermissionRequest) -> Void)? = nil) {
        self.provider = provider; self.log = log; self.resume = resume; self.requestedResume = requestedResume
        self.antigravityPermission = provider == "gemini" ? antigravityPermission : nil
        self.todos = provider == "claude" ? todos : nil; self.background = provider == "claude" ? background : nil
        todoTracker = TodoProgressTracker(progress: savedTodos); self.settled = settled
        backgroundTracker = BackgroundTaskTracker(clock: backgroundClock)
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
    /// The CLI's stderr. Only Antigravity reports a lost sign-in there
    /// ("Error: authentication required. Run 'agy' to log in, then retry.").
    /// Each complete line is matched on its own (`CLIAuthFailure.antigravity`);
    /// an unfinished last line waits for the rest of it, or for `finishStderr`.
    public func receiveStderr(_ text: String) {
        guard provider == "gemini", !text.isEmpty else { return }
        var lines = (stderrTail + text).split(separator: "\n", omittingEmptySubsequences: false)
        // Only the start of a line is matched, so a runaway line keeps just that.
        stderrTail = String(lines.removeLast().prefix(16_384))
        for line in lines { antigravityStderr(String(line)) }
    }
    /// The process ended: its last stderr line is complete.
    public func finishStderr() {
        guard provider == "gemini", !stderrTail.isEmpty else { return }
        antigravityStderr(stderrTail)
        stderrTail = ""
    }
    /// One complete stderr line of agy: a lost sign-in, or the conversation it
    /// was asked to continue missing (`warning: conversation "…" not found`).
    private func antigravityStderr(_ line: String) {
        if CLIAuthFailure.antigravity(text: line) { authFailure = true }
        let lowered = line.prefix(1_024).lowercased()
        if lowered.hasPrefix("warning: conversation"), lowered.contains("not found") { noteNewConversation() }
    }
    private func noteNewConversation() {
        guard !newConversationNoted else { return }
        newConversationNoted = true; log("system", L("run.notice.antigravityNewConversation"))
    }
    /// A line the runner already cut and parsed (`AgentOutputLines`).
    func receive(_ line: AgentOutputLine) { consume(line) }
    func lineTooLong() { log("system", L("run.notice.lineTooLong")) }
    /// A Codex app-server event the approval channel mapped to the exec shape.
    func receive(object: [String: Any]) { consume(data: nil, object: object) }
    /// Lets the approval channel's mapped events use the pictures their frame
    /// carried.
    func withPreparedImages(_ images: AgentPreparedImages, _ body: () -> Void) {
        preparedImages = images; defer { preparedImages = [:] }
        body()
    }
    /// Antigravity's streamed reply; the latest one is also the graph's answer.
    private func flushText() {
        let text = Self.trailingTrimmed(pendingText)
        if !text.isEmpty { log("assistant", text); graphTracker?.antigravityAnswer(text) }
        if pendingTextTruncated { log("system", L("run.notice.messageTruncated")) }
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
        if value.event == "turn.start" { turn(L("run.turn.generating", ["name": "Claude"])); return }
        if value.event == "turn.complete" {
            // A subagent or model turn ending is not the CLI process ending.
            if value.agentId == nil { turn(L("run.turn.finishing", ["name": "Claude"])) }
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
            value.output = value.output ?? L("run.tool.endedBeforeResult")
            value.durationMs = finishDuration(id: id, previous: value)
            toolActivities[id] = value
            if graphTracker?.activity(value) != true { activity?(value) }
        }
    }
    /// Called once after draining stdout and settling tools, before the runner
    /// publishes its terminal status. Child turns never invoke this themselves.
    public func finishGraph(state: String) { graphTracker?.finish(state: state) }
    /// The process ended: a background task the CLI never reported ending is `unknown`.
    public func finishBackground() { if let background, let work = backgroundTracker.finish() { background(work) } }
    /// The Codex root thread once `thread.started` named it.
    var codexRootThread: String? { graphTracker?.codexRootThread }
    /// Subagents read from Codex's own session records.
    func receiveCodexSessions(_ agents: [CodexSessionAgent]) { for agent in agents { graphTracker?.codexSession(agent) } }
    /// The Antigravity conversation the run continues or started.
    var antigravityConversation: String? { graphTracker?.antigravityConversation }
    /// Sub-agents an Antigravity run started, oldest first.
    var antigravitySubagents: [AntigravitySubagent] { graphTracker?.antigravitySubagents ?? [] }
    /// Sub-agents read from their own Antigravity transcripts.
    func receiveAntigravitySessions(_ agents: [CodexSessionAgent]) { for agent in agents { graphTracker?.antigravitySession(agent) } }
    /// A follow-up the runner wrote to Claude's stdin during this turn.
    public func steer(id: String, text: String) { graphTracker?.steer(id: id, text: text) }
    /// A steer opened a new turn after the request's result: background work no longer stands alone.
    public func reopenTurn() { if let background, let work = backgroundTracker.reopen() { background(work) } }
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
            // Antigravity prints a lost sign-in as a plain line, never as JSON;
            // a cut-off JSON line may quote anything, so it never counts.
            if provider == "gemini", !text.drop(while: \.isWhitespace).hasPrefix("{"), CLIAuthFailure.antigravity(text: text) { authFailure = true }
            log("output", String(text.prefix(32_768))); return
        }
        if provider == "gemini" {
            guard let value = object as? [String: Any], let event = value["event"] as? String else { return }
            usageTracker.consume(value)
            graphTracker?.consume(value)
            antigravity(value, event: event)
            return
        }
        guard var value = object as? [String: Any], let type = value["type"] as? String else { return }
        // Read before the Bedrock rewrite below, which would hide the original text.
        if provider == "claude", ExecutionGraphTracker.parentToolID(value) == nil, !ClaudeStream.isNotificationResult(value),
           type == "result" || (type == "assistant" && (value["error"] as? String)?.isEmpty == false) {
            authFailure = CLIAuthFailure.claude(value) || CLIAuthFailure.claude(result: value, repeating: authFailureText)
            authFailureText = CLIAuthFailure.claudeFailureText(value)
        }
        // Normalize confirmed provider failures before both transcript and graph
        // consume them. Ordinary assistant text and tool results stay intact.
        if provider == "claude" { value = presentClaudeFailure(value, type: type) }
        let claudeChild = provider == "claude" && ExecutionGraphTracker.parentToolID(value) != nil
        if !claudeChild { usageTracker.consume(value) }
        graphTracker?.consume(value)
        if let todos, let progress = todoTracker.consume(value) { todos(progress) }
        if let background, let work = backgroundTracker.consume(value) { background(work) }
        switch provider {
        case "claude":
            if ["control_request", "control_response", "control_cancel_request"].contains(type) {
                if let data { control?(data) }
                return
            }
            if !claudeChild { resumeIfValid(value["session_id"]) }
            if type == "system", value["subtype"] as? String == "permission_denied" {
                tool(id: value["tool_use_id"] as? String, name: value["tool_name"] as? String, state: "error", output: errorText(value["message"], fallback: L("run.tool.claudeDenied")))
            } else if type == "system", value["subtype"] as? String == "session_state_changed" {
                if value["state"] as? String == "idle" { settled?() }
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
                guard !ClaudeStream.isNotificationResult(value) else { settled?(); return }
                if interruptedResultExpected, value["subtype"] as? String == "error_during_execution" {
                    // The user cancelled the plan; the CLI reports its own interruption.
                } else if value["is_error"] as? Bool == true || (value["subtype"] as? String ?? "").hasPrefix("error") {
                    failed = true
                    let errors = (value["errors"] as? [String])?.joined(separator: "\n")
                    log("error", errors?.isEmpty == false ? errors! : errorText(value["result"], fallback: L("run.error.providerRunFailed", ["name": "Claude"])))
                } else if !assistantSeen, let text = value["result"] as? String { emitUnique(text, id: "result") }
                if let background, let work = backgroundTracker.turnEnded() { background(work) }
                turn(L("run.turn.finishing", ["name": "Claude"]))
                result?()
            }
        case "codex":
            if type == "thread.started" { resumeIfValid(value["thread_id"]) }
            if type == "turn.started" { turn(L("run.turn.generating", ["name": "Codex"])) }
            if type == "turn.completed" { turn(L("run.turn.finishing", ["name": "Codex"])) }
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
                let text = errorText(value["error"] ?? value["message"], fallback: L("run.error.providerRunFailed", ["name": "Codex"]))
                // A "Reconnecting…" notice is not the run's failure; it keeps the previous one.
                if type == "turn.failed" || !CLIAuthFailure.isCodexRetryNotice(text) { authFailure = CLIAuthFailure.codex(text: text) }
                log("error", text)
            }
            if type == "turn.completed" { authFailure = false }
        default: break
        }
    }

    /// Antigravity CLI (`agy --output-format stream-json`): each line names its
    /// `event` and carries the payload under that same key. `init` opens the
    /// session, `step_update` streams the turn's steps (the request, the
    /// agent's text as deltas, tool calls, sub-agents started, checkpoints) and
    /// `result` closes it. What a sub-agent does is not in this stream; the
    /// graph reads it from the sub-agent's own transcript.
    private func antigravity(_ value: [String: Any], event: String) {
        let payload = value[event] as? [String: Any] ?? [:]
        let name = ProviderOptions.label(provider)
        switch event {
        case "init":
            if let requestedResume, let id = value["conversation_id"] as? String, !id.isEmpty, id != requestedResume { noteNewConversation() }
            resumeIfValid(value["conversation_id"]); turn(L("run.turn.generating", ["name": name]))
        case "step_update":
            let state = (payload["state"] as? String ?? "").uppercased()
            switch payload["step_type"] as? String {
            case "agent_response":
                // A long answer arrives as ACTIVE deltas before its DONE step.
                if let text = payload["text_delta"] as? String, !text.isEmpty {
                    let remaining = max(0, 131_072 - pendingText.utf8.count)
                    if text.utf8.count > remaining { pendingTextTruncated = true }
                    pendingText += ActivitySupport.prefixUTF8(text, maximumBytes: remaining)
                    antigravityStreamed = true
                }
                if state != "ACTIVE" { flushText() }
            case "tool":
                flushText()
                guard let index = payload["step_index"] as? Int else { return }
                let info = payload["tool_info"] as? [String: Any]
                let tool = info?["name"] as? String ?? payload["tool_name"] as? String ?? "Tool"
                let failure = info?["error"].flatMap { $0 is NSNull ? nil : $0 }.map { errorText($0, fallback: L("run.tool.antigravityFailed")) }
                let toolState = ["CANCELED", "CANCELLED", "INTERRUPTED"].contains(state) ? "stopped" : state == "ERROR" || (state == "DONE" && failure != nil) ? "error" : state == "DONE" ? "completed" : "running"
                let key = "step:\(index)"
                let started = toolActivities[ActivitySupport.id(namespace: activityNamespace, key: key)] != nil
                let ended = toolState != "running"
                denials.observe(step: payload)
                self.tool(id: key, name: tool, input: Self.antigravityParameters(info?["parameters"]), state: toolState, output: ended ? failure ?? info?["output"] as? String : nil)
                if activity == nil {
                    if !started, !ended { log("system", L("run.tool.started", ["name": String(tool.prefix(160))])) }
                    if ended, let text = info?["output"] as? String, !text.isEmpty { log("output", text) }
                    if let failure { log("system", failure) }
                }
            case "subagent":
                // Started sub-agents: one row here, one block each in the graph.
                flushText()
                guard let index = payload["step_index"] as? Int else { return }
                let specs = (payload["subagent_info"] as? [String: Any])?["subagents"] as? [[String: Any]] ?? []
                let names = specs.prefix(8).compactMap { AntigravityRecords.title(role: $0["role"], typeName: $0["type_name"]) }
                let tool = payload["tool_name"] as? String ?? "invoke_subagent"
                let stopped = ["CANCELED", "CANCELLED", "INTERRUPTED"].contains(state)
                self.tool(id: "step:\(index)", name: tool, state: stopped ? "stopped" : state == "ERROR" ? "error" : state == "DONE" ? "completed" : "running",
                          summary: names.isEmpty ? tool : names.joined(separator: ", "))
            default: break
            }
        case "result":
            flushText()
            resumeIfValid(payload["conversation_id"])
            // The full text of the turn repeats what the deltas already showed.
            if !antigravityStreamed, let text = (payload["response"] as? String).map(Self.trailingTrimmed), !text.isEmpty {
                log("assistant", text); graphTracker?.antigravityAnswer(text)
            }
            antigravityStreamed = false
            // What headless mode could not ask about was declined and the turn
            // went on; the pane asks now, with a card (`AntigravityPermissions.swift`).
            let refused = denials.finish(result: payload)
            if let antigravityPermission, !refused.isEmpty {
                let card = AntigravityPermissionRequest(runId: activityNamespace, actions: refused)
                log("system", AntigravityPermissionFlow.transcriptLine(card))
                antigravityPermission(card)
            } else {
                let denied = (payload["denied_actions"] as? [[String: Any]] ?? []).prefix(16).compactMap { ($0["display_name"] ?? $0["action"]) as? String }
                if !denied.isEmpty { log("system", L("run.notice.antigravityDenied", ["actions": ActivitySupport.clean(denied.joined(separator: ", "), maximumBytes: 512, singleLine: true)])) }
            }
            let status = (payload["status"] as? String ?? "").uppercased()
            switch status {
            case "SUCCESS": authFailure = false
            case "ERROR", "INVALID", "CANCELED", "CANCELLED", "INTERRUPTED":
                // A stop the user asked for ends the run as stopped in the runner;
                // agy ending the turn by itself is a failure.
                failed = true
                let fallback = status == "ERROR" || status == "INVALID" ? L("run.error.providerRunFailed", ["name": name]) : L("run.error.antigravityEnded", ["status": status])
                let text = errorText(payload["error"], fallback: fallback)
                authFailure = CLIAuthFailure.antigravity(text: text)
                log("error", text)
            default:
                // WAITING, RUNNING or a status this app does not know yet.
                log("system", L("run.notice.antigravityStatus", ["status": ActivitySupport.clean(status.isEmpty ? "?" : status, maximumBytes: 64, singleLine: true)]))
            }
            turn(L("run.turn.finishing", ["name": name]))
        // A read-only slash command's own payload; its result carries the same text.
        case "command_result": break
        default:
            // An event this app does not read yet is shown, not dropped.
            if let data = try? JSONSerialization.data(withJSONObject: value, options: [.sortedKeys]) { log("output", String(decoding: data.prefix(32_768), as: UTF8.self)) }
        }
    }

    /// agy ends a reply with a newline; a reply's start (an indented code
    /// block, say) is kept as written.
    static func trailingTrimmed(_ text: String) -> String {
        var end = text.endIndex
        while end > text.startIndex, text[text.index(before: end)].isWhitespace { end = text.index(before: end) }
        return String(text[..<end])
    }

    /// agy names its tool parameters in PascalCase (`AbsolutePath`,
    /// `CommandLine`, `TargetFile`); the activity summary reads snake_case.
    static func antigravityParameters(_ value: Any?) -> [String: Any]? {
        guard let fields = value as? [String: Any] else { return nil }
        var result: [String: Any] = [:]
        for (key, field) in fields.prefix(64) {
            var snake = ""
            for character in key {
                if character.isUppercase, !snake.isEmpty, snake.last != "_" { snake.append("_") }
                snake += character.lowercased()
            }
            result[snake] = field
        }
        return result
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
