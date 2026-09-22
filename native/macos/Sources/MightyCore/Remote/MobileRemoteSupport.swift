import Foundation

/// Pure rules of the m1 extension: what a value must look like, which page of
/// the transcript answers a `before` cursor, and how the app's own types turn
/// into wire shapes. Kept out of the app so the contract can be unit-tested
/// without a store.
public enum MobileRemoteSupport {
    /// The invisible scalars a name the phone chose has no honest use for:
    /// the bidirectional overrides and isolates, the zero-width marks and the
    /// byte-order mark. "invoice\u{202E}gnp.exe" reads as "invoice.png" on
    /// screen while still ending in .exe, so they go before anything else.
    private static let invisibleScalars: [ClosedRange<UInt32>] = [
        0x200B...0x200F, 0x202A...0x202E, 0x2066...0x2069, 0xFEFF...0xFEFF,
    ]
    public static func stripInvisibles(_ value: String) -> String {
        var result = String.UnicodeScalarView()
        for scalar in value.unicodeScalars where !invisibleScalars.contains(where: { $0.contains(scalar.value) }) {
            result.append(scalar)
        }
        return String(result)
    }

    /// A pane title the phone may set: trimmed, 1…80 characters, no control
    /// characters. Nil when the value is outside those bounds (400).
    public static func renameTitle(_ raw: String) -> String? {
        let title = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !title.isEmpty, title.count <= MobileWire.maximumTitle else { return nil }
        guard !title.unicodeScalars.contains(where: { $0.properties.generalCategory == .control }) else { return nil }
        return title
    }

    /// The page older than `before`, newest last. A cursor the host no longer
    /// holds (the entry was evicted) answers empty rather than restarting from
    /// the oldest entry, so the phone does not silently jump in time.
    public static func page(entries: [LogEntry], before: String, limit: Int) -> (entries: [LogEntry], hasMore: Bool) {
        guard let index = entries.firstIndex(where: { $0.id == before }) else { return ([], false) }
        let older = entries[..<index]
        let page = Array(older.suffix(max(1, limit)))
        return (page, older.count > page.count)
    }

    /// Whether the detail's newest-80 window leaves anything behind it.
    public static func hasOlder(entryCount: Int) -> Bool { entryCount > MobileSessionDetail.maximumEntries }

    /// The pane's status line as the phone paints it. Nil when the command
    /// produced nothing, so the field is simply absent.
    public static func statusLine(_ lines: [[ANSISegment]]) -> MobileStatusLine? {
        let rows = lines.prefix(StatusLineSupport.maximumLines).map { line in line.map(segment) }
        guard rows.contains(where: { !$0.isEmpty }) else { return nil }
        return MobileStatusLine(lines: Array(rows))
    }

    static func segment(_ value: ANSISegment) -> MobileStatusSegment {
        MobileStatusSegment(text: value.text, fg: value.foreground.flatMap(ANSIWireColor.hex), bold: value.bold ? true : nil)
    }

    /// The account windows a running session last reported. Nil when none are
    /// measurable, so the phone shows no limits rather than empty ones.
    public static func rateLimits(_ limits: [SessionRateLimit]) -> [MobileRateLimit]? {
        let mapped = limits.compactMap { limit -> MobileRateLimit? in
            guard let percent = limit.percentUsed, percent.isFinite else { return nil }
            return MobileRateLimit(label: RateLimitWindowLabel.label(limit.kind), usedPercent: min(100, max(0, percent)), resetsAt: limit.resetsAt)
        }
        return mapped.isEmpty ? nil : mapped
    }

    /// Every field the phone sent must be one of the pane's own choices. An
    /// empty request is a 400 too: "change nothing" is not a settings change.
    /// `options` must already describe the pane **after** the request's view
    /// mode is applied, so one POST can turn Mighty on and pick a style.
    public static func validate(_ request: MobileSettingsRequest, options: MobileSettingsOptions) throws {
        let fields = [request.model, request.permissionMode, request.effort, request.agentViewMode, request.mightyStyle, request.styleId]
        guard fields.contains(where: { $0 != nil }) else { throw MobileHostError.badRequest("바꿀 설정을 하나 이상 보내세요.") }
        try require(request.model, in: options.models, field: "model")
        try require(request.permissionMode, in: options.permissionModes, field: "permissionMode")
        try require(request.effort, in: options.efforts ?? [], field: "effort")
        // The host itself sends `mightyStyle: "cli"` beside an open `styleId`,
        // so the phone must be able to hand the pair straight back (§7.2).
        if let styleId = request.styleId {
            guard options.styles.contains(where: { $0.id == styleId }) else { throw MobileHostError.badRequest(unknownStyleMessage) }
        } else {
            try require(request.mightyStyle, in: options.mightyStyles, field: "mightyStyle")
        }
        if let mode = request.agentViewMode, !MobileWire.agentViewModes.contains(mode) {
            throw MobileHostError.badRequest("agentViewMode는 plain 또는 mighty여야 합니다.")
        }
    }

    private static func require(_ value: String?, in options: [MobileOption], field: String) throws {
        guard let value else { return }
        guard options.contains(where: { $0.id == value }) else { throw MobileHostError.badRequest("\(field) 값이 이 실행 창의 선택지에 없습니다.") }
    }

    /// The Mac stores nil or "default" for the plain transcript; the wire says "plain".
    public static func viewMode(_ raw: String?) -> String { raw == "mighty" ? "mighty" : MobileWire.plainViewMode }

    /// The wire word for the style a pane actually runs. Only the two bundled
    /// ids have one of their own; every other registered style travels as
    /// "cli" here and carries its truth in `styleId` (§7.2).
    public static func style(_ raw: String?) -> String {
        guard let raw, MobileWire.mightyStyles.contains(raw) else { return MobileWire.cliStyle }
        return raw
    }

    /// Whether a pane's detail carries a `mighty` payload at all. Only a pane
    /// the Mac itself is drawing as a graph has one; everywhere else the phone
    /// shows the transcript, so the field is absent rather than empty.
    public static func sendsMighty(kind: String, agentViewMode: String?) -> Bool {
        kind != "shell" && viewMode(agentViewMode) == "mighty"
    }

    /// Whether the pane may carry a guided style — the same rule
    /// `AppStore.guidedStyle(_:)` applies, Mighty view included. `viewMode` is
    /// the view the pane will be in, so a POST that enables Mighty and picks a
    /// style in one go is judged against the state it is switching to.
    public static func guidedStylesAvailable(kind: String, provider: String, localWorkspace: Bool, viewMode: String) -> Bool {
        kind == "claude" && provider == "claude" && localWorkspace && viewMode == "mighty"
    }

    /// `options.styles`: the CLI plus the styles this pane may really pick.
    public static func styleOptions(_ styles: [RegisteredStyle]) -> [MobileStyleOption] {
        [MobileStyleOption(id: MobileWire.cliStyle, label: MobileWire.cliStyle)]
            + styles.filter(\.isRunnable).map { MobileStyleOption(id: $0.id, label: $0.manifest.name, source: $0.source) }
    }

    /// One string for "not registered" and "not approved" alike (§4.5).
    public static let unknownStyleMessage = "알 수 없는 스타일입니다."

    /// What `POST /guided` does with a request, decided from the registry
    /// already in memory. Unregistered and unapproved answer alike and neither
    /// reads the disk, so the two cannot be told apart by timing either (§4.5).
    public enum GuidedDecision: Sendable, Equatable {
        case unknownStyle
        case otherPane(styleId: String)
        case unknownAction
        case send(prompt: String)
    }

    public static func guidedDecision(registry: StyleRegistry, workspace: StyleWorkspaceRef?, pane: RegisteredStyle?,
                                      styleId: String, actionId: String, text: String) -> GuidedDecision {
        guard registry.applicable(workspace: workspace).first(where: { $0.id == styleId })?.isRunnable == true else { return .unknownStyle }
        guard let pane, pane.id == styleId else { return .otherPane(styleId: styleId) }
        guard let prompt = MobileMightySupport.guidedPrompt(pane, actionId: actionId, text: text) else { return .unknownAction }
        return .send(prompt: prompt)
    }

    /// The Mac disables its composer pickers while a run is live or pending;
    /// the phone must grey out the same ones or it offers a guaranteed 409.
    public static func editable(status: String, pendingRun: Bool) -> Bool { status != "running" && !pendingRun }

    /// What the phone is told when a submit was dropped rather than accepted.
    public static let droppedMessage = "요청을 전달하지 못했습니다. 실행 창 상태를 확인하고 다시 보내 주세요."
}

/// The Mighty graph as a phone reads it. Every string that reaches the wire is
/// one of the contract's fixed values: a core status or kind the table below
/// does not name is mapped to the nearest one deliberately, never forwarded.
public enum MobileMightySupport {
    /// The wire status of a block. The buckets are the Mac's own
    /// (`MightyGraphView.statusLabel`): failed is an error, cancelled and
    /// interrupted are stops, and anything still in motion — idle, starting,
    /// queued, or a word this build has never seen — reads as running.
    public static func status(_ raw: String) -> String {
        switch raw {
        case "completed": return "completed"
        case "error", "failed": return "error"
        case "stopped", "cancelled", "interrupted": return "stopped"
        case "waiting": return "waiting"
        default: return "running"
        }
    }

    /// `output` is bounded in characters, cut on a character boundary so a
    /// multi-byte glyph is never split in half.
    public static func output(_ raw: String?) -> String? {
        guard let raw else { return nil }
        let clean = ActivitySupport.clean(raw, maximumBytes: 4 * MobileWire.maximumBlockOutput)
        guard !clean.isEmpty else { return nil }
        return String(clean.prefix(MobileWire.maximumBlockOutput))
    }

    private static func summary(_ raw: String) -> String? {
        let clean = ActivitySupport.clean(raw, maximumBytes: MobileWire.maximumBlockSummary, singleLine: true)
        return clean.isEmpty ? nil : clean
    }

    /// How long the block's own records span, once it has settled. The host
    /// keeps no per-block clock, so this is the only honest figure available
    /// and a block still in motion reports none at all.
    static func duration(_ entries: [LogEntry], status: String) -> Double? {
        guard status == "completed" || status == "error" || status == "stopped", entries.count > 1,
              let first = AgentRunTiming.parseTimestamp(entries[0].timestamp),
              let last = AgentRunTiming.parseTimestamp(entries[entries.count - 1].timestamp) else { return nil }
        let milliseconds = last.timeIntervalSince(first) * 1_000
        guard ActivitySupport.validDuration(milliseconds), milliseconds > 0 else { return nil }
        return milliseconds.rounded()
    }

    /// The last answer a child block produced; `recordGraph` appends the node's
    /// output as an assistant entry, so that entry is the block's result.
    static func answer(_ entries: [LogEntry]) -> String? {
        entries.last(where: { $0.kind == "assistant" && !$0.text.isEmpty })?.text
    }

    /// The blocks of one run: the request itself, then its children in the
    /// order the graph recorded them.
    public static func blocks(_ run: MightyGraphRun, ordinal: Int) -> [MobileBlock] {
        let mainStatus = status(run.status)
        var result = [MobileBlock(id: run.id + ":main", kind: "main", title: "요청 \(ordinal)", status: mainStatus,
                                  summary: nil, output: output(run.finalOutput), durationMs: duration(run.rootEntries, status: mainStatus),
                                  nodeModelLabel: run.nodeModelLabel)]
        for agent in run.agents {
            let state = status(agent.status)
            result.append(MobileBlock(id: agent.id, kind: MightyGraphSupport.blockKind(agent), title: MightyGraphSupport.blockTitle(agent),
                                      status: state, summary: summary(agent.input), output: output(answer(agent.entries)),
                                      durationMs: duration(agent.entries, status: state)))
        }
        return result
    }

    /// The pane's newest runs, oldest first, as the phone lists them. `title`
    /// is the registry's prefix rule: a pane keeps blocks it made under an
    /// earlier style, so every runnable style is asked, not just this one
    /// (§1.10). The phone adds its own number and provider.
    public static func runs(_ values: [MightyGraphRun], title: (String) -> String? = { _ in nil }) -> [MobileMightyRun] {
        let window = values.suffix(MobileWire.mightyRuns)
        let offset = values.count - window.count
        return window.enumerated().map { index, run in
            MobileMightyRun(id: run.id, input: ActivitySupport.clean(run.input, maximumBytes: MobileWire.maximumText),
                            title: title(run.input),
                            status: status(run.status), blocks: blocks(run, ordinal: offset + index + 1))
        }
    }

    /// A cheap digest of what the phone would see change: run and block
    /// identities with their statuses. Streaming text is deliberately absent —
    /// hashing it would wake every long poll on every token.
    /// Hashed rather than joined: this runs on every snapshot publish, which
    /// during a stream is every token, so it must allocate nothing.
    public static func digest(_ values: [MightyGraphRun]) -> Int {
        var hasher = Hasher()
        for run in values.suffix(MobileWire.mightyRuns) {
            hasher.combine(run.id); hasher.combine(run.status); hasher.combine(run.agents.count)
            for agent in run.agents { hasher.combine(agent.id); hasher.combine(agent.status); hasher.combine(agent.kind) }
        }
        return hasher.finalize()
    }

    /// The digest of the very runs `MobileMighty` would carry. A pane with no
    /// saved graph is grouped out of its transcript, so hashing an empty list
    /// there would leave a phone's blocks moving without a long poll ever
    /// waking — the payload and the digest have to read one source.
    public static func digest(session: RunSession) -> Int {
        if let saved = session.graphRuns { return digest(saved) }
        var hasher = Hasher()
        for run in legacyRunIdentities(session) {
            hasher.combine(run.id); hasher.combine(run.status); hasher.combine(0)
        }
        return hasher.finalize()
    }

    /// What the newest legacy runs are called and how they stand, without
    /// building them: `MightyGraphSupport.legacyRuns` copies every entry of
    /// every run, and this is read on every snapshot publish. Walked backwards
    /// and stopped at the window the payload sends, so a long transcript costs
    /// no more than a short one.
    static func legacyRunIdentities(_ session: RunSession) -> [(id: String, status: String)] {
        var ids: [String] = []
        var index = session.logs.count - 1
        while index >= 0, ids.count < MobileWire.mightyRuns {
            if session.logs[index].kind == "user" { ids.append(session.logs[index].id) }
            index -= 1
        }
        // The transcript opens with replies to a request the pane no longer
        // holds: those entries are grouped under one synthetic run.
        if index < 0, let first = session.logs.first, first.kind != "user", ids.count < MobileWire.mightyRuns {
            ids.append("history-" + session.id)
        }
        guard !ids.isEmpty else { return [] }
        ids.reverse()
        let last = session.status == "idle" ? "completed" : session.status
        return ids.enumerated().map { (id: $0.element, status: $0.offset == ids.count - 1 ? last : "completed") }
    }

    /// The prompt a guided request sends, built by the very evaluator the Mac's
    /// own buttons use. Nil means the action is not in that style's catalogue.
    ///
    /// The phone's text is folded to one line first, whatever the style's own
    /// `foldText` says. An action reads its argument up to the first line
    /// break, so a pasted paragraph would otherwise reach one catalogue whole
    /// and the other cut in half — the Mac's composer keeps its own behaviour,
    /// only this route normalises.
    public static func guidedPrompt(_ style: RegisteredStyle, actionId: String, text: String) -> String? {
        guard let action = style.manifest.action(actionId) else { return nil }
        return style.evaluator.prompt(actionId: actionId, text: action.takesText ? singleLine(text) : "")
    }

    public static func singleLine(_ text: String) -> String {
        text.split(whereSeparator: \.isNewline).map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }.joined(separator: " ")
    }
}

/// The terminal colours `StatusLineView` draws, as hex the phone can use.
/// The theme-following ones (default foreground, black and white) have no hex
/// and are sent without `fg` so each side keeps its own readable default.
public enum ANSIWireColor {
    public static func hex(_ value: ANSISegment.Color) -> String? {
        switch value {
        case .standard(let index):
            switch index % 8 {
            case 1: return hex(0.86, 0.30, 0.30)
            case 2: return hex(0.30, 0.66, 0.40)
            case 3: return hex(0.80, 0.62, 0.20)
            case 4: return hex(0.36, 0.55, 0.90)
            case 5: return hex(0.70, 0.45, 0.85)
            case 6: return hex(0.25, 0.65, 0.70)
            default: return nil
            }
        case .palette(let index):
            if index < 16 { return hex(.standard(index)) }
            if index >= 232 {
                let level = Double(index - 232) / 23
                let white = 0.25 + level * 0.6
                return hex(white, white, white)
            }
            let cube = index - 16
            let steps: [Double] = [0, 0.37, 0.53, 0.68, 0.84, 1]
            return hex(steps[cube / 36], steps[(cube / 6) % 6], steps[cube % 6])
        case .rgb(let red, let green, let blue):
            return String(format: "#%02X%02X%02X", clamp(red), clamp(green), clamp(blue))
        }
    }

    private static func hex(_ red: Double, _ green: Double, _ blue: Double) -> String {
        String(format: "#%02X%02X%02X", byte(red), byte(green), byte(blue))
    }
    private static func byte(_ value: Double) -> Int { clamp(Int((value * 255).rounded())) }
    private static func clamp(_ value: Int) -> Int { min(255, max(0, value)) }
}

/// The Mac's slash palette translated to the wire. Commands that only open a
/// Mac window are dropped: a phone cannot follow them.
public enum MobileCommandSupport {
    public static func wire(_ commands: [SlashCommand]) -> [MobileCommand] {
        commands.compactMap { command in
            guard listed(command) else { return nil }
            return MobileCommand(name: command.invocation, description: command.description, source: command.origin.rawValue,
                                 argumentHint: hint(command.argument), action: action(command))
        }
    }

    static func listed(_ command: SlashCommand) -> Bool {
        switch command.action {
        case .openPlugins, .openSettings: return false
        // Argument choices (`/model opus`) belong to a palette, not to a list.
        case .setModel, .setPermission: return false
        default: return true
        }
    }

    static func action(_ command: SlashCommand) -> String? {
        switch command.action {
        case .newConversation: return "clear"
        case .showUsage: return "usage"
        case .help: return "help"
        case .rename: return "rename"
        default: break
        }
        switch command.argument {
        case .model: return "model"
        case .permission: return "permission"
        case nil: return nil
        }
    }

    static func hint(_ argument: SlashArgument?) -> String? {
        switch argument {
        case .model: return "모델 이름"
        case .permission: return "권한 모드"
        case nil: return nil
        }
    }
}

/// The body `POST …/command {action:"usage"}` answers with. The Mac opens a
/// sheet for the same action, so the numbers are formatted here instead.
public enum MobileUsageText {
    public static func text(usage: MobileUsage?, model: String, elapsedSeconds: Double? = nil) -> String {
        var lines: [String] = []
        lines.append("모델 · " + (usage?.model ?? model))
        if let used = usage?.contextUsedTokens {
            var line = "컨텍스트 · " + tokens(used)
            if let window = usage?.contextWindowTokens { line += " / " + tokens(window) }
            if let percent = usage?.contextPercent { line += " (" + String(format: "%.1f", percent) + "%)" }
            lines.append(line)
        }
        if let total = usage?.totalTokens { lines.append("누적 토큰 · " + tokens(total)) }
        if let cost = usage?.costUSD, cost.isFinite, cost >= 0 { lines.append("비용 · $" + String(format: "%.4f", cost)) }
        if let elapsed = elapsedSeconds, elapsed > 0 { lines.append("경과 · \(Int(elapsed.rounded()))초") }
        guard lines.count > 1 else { return "이 실행 창에서 아직 측정된 토큰·비용이 없습니다." }
        return "토큰·비용\n" + lines.joined(separator: "\n")
    }

    /// One formatter, fixed locale: these numbers are read on the phone, so
    /// the Mac's region must not decide their grouping, and a usage sheet is
    /// many lines long.
    private static let decimal: NumberFormatter = {
        let formatter = NumberFormatter()
        formatter.numberStyle = .decimal
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.groupingSeparator = ","
        formatter.usesGroupingSeparator = true
        return formatter
    }()

    static func tokens(_ value: Int) -> String { decimal.string(from: NSNumber(value: value)) ?? String(value) }
}
