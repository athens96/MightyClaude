import Foundation

// The decisions the Mac's guided surfaces make, kept here as pure values so
// they can be asserted: the app module has no test target of its own.

/// The strings the app composes around a manifest's own (§1.5, §1.10).
public enum StyleChrome {
    /// The graph header's own word, which no manifest can write.
    public static let mightyLabel = "마이티"
    public static let separator = "\u{00B7}"

    /// `<prefix> · 요청 N · <provider>`. The tail is always the app's, so a
    /// manifest string alone cannot imitate a block the app wrote (§1.10).
    public static func requestTitle(prefix: String?, ordinal: Int, providerLabel: String) -> String {
        let tail = "요청 \(ordinal) " + separator + " " + providerLabel
        guard let prefix, !prefix.isEmpty else { return tail }
        return prefix + " " + separator + " " + tail
    }

    /// `마이티` / `마이티 · <name>` / `마이티 · <name> · <단계>`. `headerSummary`
    /// follows this unchanged.
    public static func graphHeader(styleName: String?, phaseTitle: String?) -> String {
        guard let styleName, !styleName.isEmpty else { return mightyLabel }
        var value = mightyLabel + " " + separator + " " + styleName
        if let phaseTitle, !phaseTitle.isEmpty { value += " " + separator + " " + phaseTitle }
        return value
    }

    /// The install pane's title. The app opens its own sign-in panes through
    /// the same mechanism, so the style's name is appended to tell them apart —
    /// and the badge travels with the name here as everywhere else (§1.10),
    /// because this is the one surface that ends in a prefilled shell command.
    public static func installPaneTitle(_ paneTitle: String, styleName: String, source: StyleSource) -> String {
        var value = paneTitle + " " + separator + " " + styleName
        if let badge = sourceBadge(source) { value += " " + separator + " " + badge }
        return value
    }

    /// The badge that follows a non-bundled name everywhere it appears (§1.10).
    public static func sourceBadge(_ source: StyleSource) -> String? {
        switch source {
        case .bundled: return nil
        case .user: return "사용자 등록"
        case .workspace: return "저장소에서 발견됨"
        }
    }

    /// The first twelve characters of the hash, as the card and settings show it.
    public static func hashPrefix(_ hash: String) -> String { String(hash.prefix(12)) }
}

/// The chip row of §6.1's seventh block, and the reset chip beside it.
public enum StyleResetChip: Sendable, Equatable {
    case none
    /// `rules.start.resetTitle`: pressing it shows the entry phase again.
    case reset(String)
    /// The entry phase is only being shown because reset was pressed.
    case cancel
}

public struct StyleChipList: Sendable, Equatable {
    public var actions: [StyleAction]
    /// The first chip of a phase or start row; `byGroup` has none (§6.1).
    public var prominentId: String?
    public var recommendedId: String?
    public var reset: StyleResetChip
    /// The phase the row was built for, which `startingNew` may have moved.
    public var phaseId: String?
    /// A catalogue drawn under a group map is a grid; a phase row is one line.
    public var grid: Bool
    /// The row is replaced by a spinner: a sequence has no next step mid-run.
    public var progress: Bool
    public init(actions: [StyleAction], prominentId: String?, recommendedId: String?, reset: StyleResetChip,
                phaseId: String?, grid: Bool = false, progress: Bool = false) {
        self.actions = actions; self.prominentId = prominentId; self.recommendedId = recommendedId
        self.reset = reset; self.phaseId = phaseId; self.grid = grid; self.progress = progress
    }
}

public enum StyleChips {
    /// `phase` is the pane's own phase; `startingNew` is the reset chip having
    /// been pressed, which shows the start rule's row instead.
    public static func make(_ evaluator: StyleEvaluator, phase: StylePhase?, group: StyleGroup?,
                            startingNew: Bool, capabilityStates: [String: String] = [:],
                            running: Bool = false) -> StyleChipList {
        let manifest = evaluator.manifest
        var effective = phase
        if startingNew, case .actions(let startPhase, _, _) = manifest.rules.start { effective = manifest.phase(startPhase) }
        let start = evaluator.startActions(phase: effective)
        let actions = evaluator.visibleActions(phase: effective, group: group, running: running)
        // `byGroup` has no prominent chip: the recommendation plays that role.
        var prominent = actions.first?.id
        if start.isEmpty, case .byGroup = manifest.rules.next { prominent = nil }
        // A sequence in flight has no next step, so its whole row goes and a
        // spinner takes its place; a catalogue keeps its chips (§6.1).
        let waiting = running && evaluator.drawsPhaseProgress
        let reset: StyleResetChip
        if waiting { reset = .none }
        else if startingNew { reset = .cancel }
        else if start.isEmpty, let title = evaluator.resetTitle { reset = .reset(title) }
        else { reset = .none }
        return StyleChipList(actions: actions, prominentId: prominent,
                             recommendedId: evaluator.recommendedAction(capabilityStates: capabilityStates),
                             reset: reset, phaseId: effective?.id,
                             // Whenever the group map is drawn the row under it
                             // is a catalogue, and a catalogue is a grid (§6.1).
                             grid: evaluator.drawsGroupMap(), progress: waiting)
    }

    /// The chip's tooltip: `help · 범위: scope · 호출자 · 읽기 전용` (§6.1).
    /// Kept out of the view builder, where a chained conditional is slow.
    public static func help(_ action: StyleAction) -> String {
        var parts: [String] = []
        if !action.help.isEmpty { parts.append(action.help) }
        if let scope = action.scope, !scope.isEmpty { parts.append("범위: " + scope) }
        if action.flags.contains(.userInvoked) { parts.append("사람만 부를 수 있는 스킬") }
        if action.flags.contains(.readOnly) { parts.append("읽기 전용") }
        return parts.joined(separator: " " + StyleChrome.separator + " ")
    }
}

/// What Enter does in a guided pane's composer. The order is the composer's,
/// not the manifest's: a waiting question always wins, and only then does the
/// style's own Enter rule get a say (§1.6, §6.1).
public enum StyleComposerEnter: Sendable, Equatable {
    case answerQuestion
    case rewrite(actionId: String)
    case verbatim
}

public enum StyleComposer {
    public static func enter(_ evaluator: StyleEvaluator, draft: String, phase: StylePhase?, answering: Bool,
                             hasAttachments: Bool, running: Bool, hasRequests: Bool, startingNew: Bool) -> StyleComposerEnter {
        guard !answering else { return .answerQuestion }
        switch evaluator.enterBehaviour(draft: draft, phase: phase, hasAttachments: hasAttachments,
                                        running: running, hasRequests: hasRequests, startingNew: startingNew) {
        case .rewrite(let actionId): return .rewrite(actionId: actionId)
        case .verbatim: return .verbatim
        }
    }
}

/// One row of the style menu: `cli` first, then every style this pane may see.
public struct StyleMenuRow: Sendable, Equatable, Identifiable {
    public var id: String
    public var label: String
    /// Absent for `cli`, which belongs to no source.
    public var source: StyleSource?
    public var approval: StyleApprovalState?
    public var actionCount: Int
    public var autoAllowCount: Int
    /// The row may be chosen straight away.
    public var selectable: Bool
    /// Tapping it opens the approval sheet instead of switching the pane (§4.5).
    public var opensApproval: Bool
    public var badge: String?
    /// The second line: the pending scale, or why the row cannot be chosen.
    public var detail: String?
    public var summary: String
}

public enum StyleMenu {
    public static let cliLabel = "CLI"
    public static let cliSummary = "지금처럼 자유롭게 요청"

    public static func rows(_ styles: [RegisteredStyle]) -> [StyleMenuRow] {
        let ordered = styles.sorted {
            $0.source.precedence != $1.source.precedence ? $0.source.precedence < $1.source.precedence : $0.id < $1.id
        }
        var rows = [StyleMenuRow(id: MightyStyleIDs.cli, label: cliLabel, source: nil, approval: nil,
                                 actionCount: 0, autoAllowCount: 0, selectable: true, opensApproval: false,
                                 badge: nil, detail: nil, summary: cliSummary)]
        for style in ordered { rows.append(row(style)) }
        return rows
    }

    static func row(_ style: RegisteredStyle) -> StyleMenuRow {
        let actions = style.manifest.actions.count
        let autoAllow = style.manifest.autoAllow.count
        let detail: String?
        switch style.approval {
        // The scale is shown before the card is opened, so the user knows what
        // they are about to read (§4.4).
        case .pending: detail = "확인 필요 " + StyleChrome.separator + " 행동 \(actions) " + StyleChrome.separator + " 자동 허용 \(autoAllow)"
        case .revoked: detail = "차단됨 " + StyleChrome.separator + " 설정에서 다시 허용할 수 있습니다"
        case .approved, .preApproved: detail = nil
        }
        return StyleMenuRow(id: style.id, label: style.manifest.name, source: style.source, approval: style.approval,
                            actionCount: actions, autoAllowCount: autoAllow,
                            selectable: style.isRunnable, opensApproval: style.approval == .pending,
                            badge: StyleChrome.sourceBadge(style.source), detail: detail,
                            summary: style.manifest.summary)
    }
}

/// One row of Settings › 마이티 스타일.
public struct StyleSettingsRow: Sendable, Equatable, Identifiable {
    /// The file's path: two styles never share one, an id can be refused twice.
    public var id: String
    public var styleId: String
    public var name: String
    public var source: StyleSource
    public var path: String
    public var hashPrefix: String
    public var stateLabel: String
    public var badge: String?
    public var actionCount: Int
    public var autoAllowCount: Int
    public var canApprove: Bool
    public var canRevoke: Bool
    public var canAllowAgain: Bool
    /// Only a user-registered file is the app's to delete.
    public var canRemove: Bool
}

public enum StyleSettingsList {
    public static func stateLabel(_ state: StyleApprovalState) -> String {
        switch state {
        case .preApproved: return "내장"
        case .pending: return "확인 필요"
        case .approved: return "허용됨"
        case .revoked: return "차단됨"
        }
    }

    /// A locked trust store answers no decision at all, so every button that
    /// would write one is off (§4.3).
    public static func rows(_ styles: [RegisteredStyle], locked: Bool) -> [StyleSettingsRow] {
        styles.sorted {
            $0.source.precedence != $1.source.precedence ? $0.source.precedence < $1.source.precedence : $0.id < $1.id
        }.map { style in
            let bundled = style.source == .bundled
            return StyleSettingsRow(id: style.path, styleId: style.id, name: style.manifest.name, source: style.source,
                                    path: style.path, hashPrefix: StyleChrome.hashPrefix(style.hash),
                                    stateLabel: stateLabel(style.approval), badge: StyleChrome.sourceBadge(style.source),
                                    actionCount: style.manifest.actions.count, autoAllowCount: style.manifest.autoAllow.count,
                                    canApprove: !locked && !bundled && style.approval == .pending,
                                    canRevoke: !locked && !bundled && style.approval == .approved,
                                    canAllowAgain: !locked && !bundled && style.approval == .revoked,
                                    canRemove: !locked && style.source == .user)
        }
    }

    public static func lockedMessage(_ path: String) -> String { "신뢰 기록을 읽을 수 없습니다: " + path }
}

/// One block of the approval card. The order is §4.4's risk order, not the
/// schema's: the auto-allow list comes first and is never folded away.
public struct StyleApprovalSection: Sendable, Equatable, Identifiable {
    public var id: String
    public var title: String
    public var lines: [String]
    /// The bytes that will be sent or run, shown in a fixed-width face.
    public var monospaced: Bool
    /// Folded by default; the card offers one control that opens them all.
    public var foldable: Bool
}

public enum StyleApprovalCard {
    public static func autoAllowCount(_ style: RegisteredStyle) -> Int { style.manifest.autoAllow.count }
    public static func requiresSecondConfirmation(_ style: RegisteredStyle) -> Bool { !style.manifest.autoAllow.isEmpty }
    public static func secondConfirmation(count: Int) -> String {
        "이 스타일은 도구 \(count)개를 권한 창 없이 실행할 수 있게 됩니다."
    }
    public static let installNotice = "누르면 터미널 창에 채워지기만 하고, 실행은 직접 Enter를 눌러야 합니다."

    public static func sections(_ style: RegisteredStyle) -> [StyleApprovalSection] {
        let manifest = style.manifest
        var sections: [StyleApprovalSection] = []
        var origin = [StyleChrome.sourceBadge(style.source) ?? "앱 내장", style.path, "해시 " + StyleChrome.hashPrefix(style.hash)]
        if let workspacePath = style.workspacePath { origin.insert("워크스페이스 " + workspacePath, at: 1) }
        sections.append(StyleApprovalSection(id: "origin", title: "출처", lines: origin, monospaced: false, foldable: false))
        // The membership rule of §1.9 keeps out other servers but not the write
        // tools inside this one, so this block is first and never folds.
        let auto = manifest.autoAllow.isEmpty ? ["자동 허용 없음"] : manifest.autoAllow.map(\.wireName)
        sections.append(StyleApprovalSection(id: "autoAllow", title: "자동 허용", lines: auto, monospaced: true, foldable: false))
        if let install = manifest.install {
            sections.append(StyleApprovalSection(id: "install", title: "설치 명령",
                                                 lines: [install.command, installNotice], monospaced: true, foldable: false))
        }
        sections.append(StyleApprovalSection(id: "enter", title: "Enter 규칙", lines: enterLines(manifest), monospaced: true, foldable: false))
        sections.append(StyleApprovalSection(id: "identity", title: "스타일",
                                             lines: ["이름 " + manifest.name, "id " + manifest.id, manifest.summary, manifest.subtitle],
                                             monospaced: false, foldable: false))
        sections.append(StyleApprovalSection(id: "structure", title: "그룹 " + StyleChrome.separator + " 단계 " + StyleChrome.separator + " 별칭 " + StyleChrome.separator + " 인식",
                                             lines: structureLines(manifest), monospaced: false, foldable: true))
        sections.append(StyleApprovalSection(id: "rules", title: "규칙", lines: ruleLines(manifest), monospaced: false, foldable: true))
        sections.append(StyleApprovalSection(id: "actions", title: "행동 \(manifest.actions.count)개",
                                             lines: manifest.actions.flatMap(actionLines), monospaced: true, foldable: true))
        sections.append(StyleApprovalSection(id: "presentation", title: "표시", lines: presentationLines(manifest), monospaced: false, foldable: true))
        return sections
    }

    /// The prompt is quoted verbatim: a plain-language summary cannot tell the
    /// user which bytes the first Enter will send (§4.4).
    static func enterLines(_ manifest: StyleManifest) -> [String] {
        switch manifest.rules.enter {
        case .verbatim:
            return ["입력창의 글을 그대로 요청으로 보냅니다."]
        case .rewriteBareDraftTo(let action, let phase):
            let prompt = manifest.action(action)?.prompt ?? action
            let title = manifest.phase(phase)?.title ?? phase
            return ["이 실행 창의 첫 Enter는 다음 프롬프트의 {text} 자리에 들어갑니다 (단계: " + title + ")", prompt]
        }
    }

    static func structureLines(_ manifest: StyleManifest) -> [String] {
        var lines: [String] = []
        for phase in manifest.orderedPhases { lines.append("단계 " + phase.id + " " + StyleChrome.separator + " " + phase.title) }
        for group in manifest.groups {
            var line = "그룹 " + group.id + " " + StyleChrome.separator + " " + group.title
            if let axis = group.axis, !axis.isEmpty { line += " " + StyleChrome.separator + " " + axis }
            if let question = group.question, !question.isEmpty { line += " " + StyleChrome.separator + " " + question }
            lines.append(line + " " + StyleChrome.separator + " 행동 \(group.actions.count)")
        }
        for alias in manifest.aliases { lines.append("별칭 " + alias.name + " \u{2192} " + alias.phase) }
        lines.append("인식 접두사 " + manifest.recognition.prefixes.joined(separator: " , ")
                     + (manifest.recognition.lowercase ? " (소문자로 맞춤)" : ""))
        return lines
    }

    static func ruleLines(_ manifest: StyleManifest) -> [String] {
        var lines: [String] = []
        switch manifest.rules.start {
        case .none: lines.append("시작 " + StyleChrome.separator + " 진입 단계에 따로 버튼이 없습니다.")
        case .actions(let phase, let actions, let resetTitle):
            var line = "시작 " + StyleChrome.separator + " " + phase + " 단계에서 " + actions.joined(separator: ", ")
            if let resetTitle { line += " (되돌리기 칩: " + resetTitle + ")" }
            lines.append(line)
        }
        switch manifest.rules.phase {
        case .none: lines.append("단계 " + StyleChrome.separator + " 단계 개념이 없습니다.")
        case .lastRecognisedAction(let fallback): lines.append("단계 " + StyleChrome.separator + " 마지막으로 인식된 행동, 없으면 " + fallback)
        }
        switch manifest.rules.next {
        case .byPhase(let map):
            for phase in manifest.orderedPhases {
                lines.append("다음 " + StyleChrome.separator + " " + phase.id + " \u{2192} " + (map[phase.id] ?? []).joined(separator: ", "))
            }
        case .byGroup: lines.append("다음 " + StyleChrome.separator + " 고른 그룹의 행동")
        }
        switch manifest.rules.recommend {
        case .none: lines.append("추천 " + StyleChrome.separator + " 없음")
        case .capability(let name, let map, let group):
            let pairs = map.keys.sorted().map { $0 + " \u{2192} " + (map[$0] ?? "") }.joined(separator: ", ")
            lines.append("추천 " + StyleChrome.separator + " " + name + " " + StyleChrome.separator + " " + pairs
                         + (group.map { " (그룹 " + $0 + ")" } ?? ""))
        }
        switch manifest.rules.initialGroup {
        case .fixed(let group): lines.append("처음 그룹 " + StyleChrome.separator + " " + group)
        case .capabilityState(let name, let map):
            let pairs = map.keys.sorted().map { $0 + " \u{2192} " + (map[$0] ?? "") }.joined(separator: ", ")
            lines.append("처음 그룹 " + StyleChrome.separator + " " + name + " " + StyleChrome.separator + " " + pairs)
        }
        if !manifest.capabilities.isEmpty { lines.append("내장 기능 " + StyleChrome.separator + " " + manifest.capabilities.joined(separator: ", ")) }
        return lines
    }

    /// Every action, whole: title, help, scope, flags and the prompt template
    /// exactly as written. A hundred actions means a hundred entries (§4.4).
    static func actionLines(_ action: StyleAction) -> [String] {
        var head = action.title + " (" + action.id + ")"
        if action.takesText { head += " " + StyleChrome.separator + " 입력 글 받음" }
        if action.requiresText { head += " " + StyleChrome.separator + " 입력 글 필요" }
        for flag in StyleActionFlag.allCases where action.flags.contains(flag) { head += " " + StyleChrome.separator + " " + flag.rawValue }
        if let match = action.match { head += " " + StyleChrome.separator + " match " + match }
        var lines = [head]
        if !action.help.isEmpty { lines.append(action.help) }
        if let scope = action.scope, !scope.isEmpty { lines.append("범위: " + scope) }
        lines.append(action.prompt)
        return lines
    }

    static func presentationLines(_ manifest: StyleManifest) -> [String] {
        // The icon name travels as text too: what was read and what was drawn
        // must not be able to differ (§2).
        var lines = ["아이콘 " + (manifest.presentation.icon?.rawValue ?? "없음"),
                     "색 " + (manifest.presentation.tint?.rawValue ?? "accent")]
        lines.append("입력창 " + StyleChrome.separator + " " + manifest.placeholders.idle)
        if let initial = manifest.placeholders.initial { lines.append("입력창(시작) " + StyleChrome.separator + " " + initial) }
        if let running = manifest.placeholders.running { lines.append("입력창(실행 중) " + StyleChrome.separator + " " + running) }
        lines.append("입력창(답변) " + StyleChrome.separator + " " + manifest.placeholders.answering)
        for (label, value) in [("시작", manifest.guidance.start), ("다음", manifest.guidance.next), ("실행 중", manifest.guidance.running)] {
            if let value, !value.isEmpty { lines.append("안내(" + label + ") " + StyleChrome.separator + " " + value) }
        }
        return lines
    }
}
