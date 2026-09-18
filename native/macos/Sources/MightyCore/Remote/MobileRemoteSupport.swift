import Foundation

/// Pure rules of the m1 extension: what a value must look like, which page of
/// the transcript answers a `before` cursor, and how the app's own types turn
/// into wire shapes. Kept out of the app so the contract can be unit-tested
/// without a store.
public enum MobileRemoteSupport {
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
        let fields = [request.model, request.permissionMode, request.effort, request.agentViewMode, request.mightyStyle]
        guard fields.contains(where: { $0 != nil }) else { throw MobileHostError.badRequest("바꿀 설정을 하나 이상 보내세요.") }
        try require(request.model, in: options.models, field: "model")
        try require(request.permissionMode, in: options.permissionModes, field: "permissionMode")
        try require(request.effort, in: options.efforts ?? [], field: "effort")
        try require(request.mightyStyle, in: options.mightyStyles, field: "mightyStyle")
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

    /// The Mac stores nil for the plain CLI style; the wire says "cli".
    public static func style(_ raw: String?) -> String { MightyStyles.normalized(raw) ?? MobileWire.cliStyle }

    /// Whether the pane may carry a guided style — the same rule
    /// `AppStore.guidedStyle(_:)` applies, Mighty view included. `viewMode` is
    /// the view the pane will be in, so a POST that enables Mighty and picks a
    /// style in one go is judged against the state it is switching to.
    public static func guidedStylesAvailable(kind: String, provider: String, localWorkspace: Bool, viewMode: String) -> Bool {
        kind == "claude" && provider == "claude" && localWorkspace && viewMode == "mighty"
    }

    /// The style choices for such a pane: outside Mighty view only "cli" exists.
    public static func styleOptionIds(guided: Bool) -> [String] {
        guided ? [MobileWire.cliStyle] + MightyStyles.all : [MobileWire.cliStyle]
    }

    /// The Mac disables its composer pickers while a run is live or pending;
    /// the phone must grey out the same ones or it offers a guaranteed 409.
    public static func editable(status: String, pendingRun: Bool) -> Bool { status != "running" && !pendingRun }

    /// What the phone is told when a submit was dropped rather than accepted.
    public static let droppedMessage = "요청을 전달하지 못했습니다. 실행 창 상태를 확인하고 다시 보내 주세요."
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
