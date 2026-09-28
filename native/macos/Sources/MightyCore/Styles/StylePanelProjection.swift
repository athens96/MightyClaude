import Foundation

/// The one shape the Mac panel and the phone payload are both drawn from
/// (§5.6, §7.3). `Codable` is used here and nowhere else in the engine,
/// because this is the only thing that is ever serialised.
public struct StylePanel: Codable, Sendable, Equatable {
    public struct Style: Codable, Sendable, Equatable {
        public var id, name: String
        public var source: StyleSource
        public var icon: String?
        public var tint: StyleTint?
    }
    public struct Phase: Codable, Sendable, Equatable {
        public var id, title: String
        public var index, count: Int
    }
    public struct Group: Codable, Sendable, Equatable {
        public var id, title: String
        public var axis, question: String?
        public var selected: Bool
        public var actions: [String]
    }
    public struct Action: Codable, Sendable, Equatable {
        public var id, title: String
        public var icon, glyph: String?
        public var help: String
        public var scope: String?
        public var takesText, requiresText: Bool
        public var flags: [String]
        public var prominent: Bool
    }
    public struct Attachment: Codable, Sendable, Equatable {
        public var id, title: String
        public var detail: String?
        public var readOnly: Bool
    }
    public struct Setup: Codable, Sendable, Equatable {
        public var ready: Bool
        public var missing: [String]
        public var hint: String?
        public var installCommand: String?
    }
    public struct Presentation: Codable, Sendable, Equatable {
        public var headerTitle: String
        public var source: StyleSource
        public var icon: String?
        public var tint: StyleTint?
    }
    /// §1.14: one computed widget value, matching the three closed kinds.
    public enum Widget: Codable, Sendable, Equatable {
        case progressBar(value: Double, total: Int?)
        case list(items: [String])
        case label(text: String)

        enum CodingKeys: String, CodingKey { case kind, value, total, items, text }
        public init(from decoder: Decoder) throws {
            let c = try decoder.container(keyedBy: CodingKeys.self)
            let kind = try c.decode(String.self, forKey: .kind)
            switch kind {
            case "progressBar":
                let v = try c.decode(Double.self, forKey: .value)
                let t = try c.decodeIfPresent(Int.self, forKey: .total)
                self = .progressBar(value: v, total: t)
            case "list":
                self = .list(items: try c.decode([String].self, forKey: .items))
            case "label":
                self = .label(text: try c.decode(String.self, forKey: .text))
            default:
                throw DecodingError.dataCorruptedError(forKey: .kind, in: c, debugDescription: "unknown widget kind: \(kind)")
            }
        }
        public func encode(to encoder: Encoder) throws {
            var c = encoder.container(keyedBy: CodingKeys.self)
            switch self {
            case .progressBar(let v, let t):
                try c.encode("progressBar", forKey: .kind); try c.encode(v, forKey: .value)
                if let t { try c.encode(t, forKey: .total) }
            case .list(let items):
                try c.encode("list", forKey: .kind); try c.encode(items, forKey: .items)
            case .label(let text):
                try c.encode("label", forKey: .kind); try c.encode(text, forKey: .text)
            }
        }
    }
    public var style: Style
    public var phase: Phase?
    public var groups: [Group]
    public var actions: [Action]
    public var next: [String]
    public var recommended: String?
    public var attachments: [Attachment]
    public var setup: Setup
    public var guidance: String?
    public var presentation: Presentation
    public var widgets: [Widget]?
}

public enum StylePanelProjection {
    public static func make(style: RegisteredStyle,
                            prompts: [String],
                            selectedGroupId: String?,
                            capabilityStates: [String: String],
                            attachments: [StyleAttachmentItem],
                            prerequisites: StylePrerequisiteResult,
                            running: Bool = false,
                            session: RunSession? = nil,
                            fileSourceStates: [Int: StyleFileSourceState] = [:],
                            widgets: [StylePanel.Widget] = []) -> StylePanel {
        let manifest = style.manifest
        let evaluator = style.evaluator
        let phase = fileSourceStates.isEmpty
            ? evaluator.currentPhase(prompts: prompts)
            : evaluator.currentPhase(prompts: prompts, fileSourceStates: fileSourceStates)
        let jobOpen = session.map { evaluator.isJobOpen(session: $0) } ?? false
        let ordered = manifest.orderedPhases
        let selected = selectedGroupId.flatMap { manifest.group($0) } ?? evaluator.initialGroup(capabilityStates: capabilityStates)
        let nextIds = evaluator.visibleActions(phase: phase, group: selected, running: running, jobOpen: jobOpen).map(\.id)

        let panelPhase = phase.flatMap { value -> StylePanel.Phase? in
            guard let index = ordered.firstIndex(where: { $0.id == value.id }) else { return nil }
            return StylePanel.Phase(id: value.id, title: value.title, index: index, count: ordered.count)
        }
        let groups = manifest.groups.map {
            StylePanel.Group(id: $0.id, title: $0.title, axis: $0.axis, question: $0.question,
                             selected: $0.id == selected?.id, actions: $0.actions)
        }
        // The catalogue travels whole; what is drawn is decided by groups and next.
        let actions = manifest.actions.map { action in
            StylePanel.Action(id: action.id, title: action.title, icon: action.icon?.rawValue, glyph: action.glyph,
                              help: action.help, scope: action.scope, takesText: action.takesText,
                              requiresText: action.requiresText,
                              flags: StyleActionFlag.allCases.filter(action.flags.contains).map(\.rawValue),
                              prominent: action.id == nextIds.first)
        }
        let headerTitle = phase.map { manifest.name + " \u{00B7} " + $0.title } ?? manifest.name
        return StylePanel(style: StylePanel.Style(id: manifest.id, name: manifest.name, source: style.source,
                                                  icon: manifest.presentation.icon?.rawValue, tint: manifest.presentation.tint),
                          phase: panelPhase,
                          groups: groups,
                          actions: actions,
                          next: nextIds,
                          recommended: evaluator.recommendedAction(capabilityStates: capabilityStates),
                          attachments: attachments.map { StylePanel.Attachment(id: $0.id, title: $0.title, detail: $0.detail, readOnly: $0.readOnly) },
                          setup: StylePanel.Setup(ready: prerequisites.ready, missing: prerequisites.missing,
                                                  hint: prerequisites.hint, installCommand: manifest.install?.command),
                          guidance: evaluator.guidanceLine(phase: phase, running: running, jobOpen: jobOpen),
                          presentation: StylePanel.Presentation(headerTitle: headerTitle, source: style.source,
                                                                icon: manifest.presentation.icon?.rawValue, tint: manifest.presentation.tint),
                          widgets: widgets.isEmpty ? nil : widgets)
    }

    /// Frozen with the tag so a golden stays byte-stable: UTF-8, keys sorted,
    /// two-space indent, no `\/` escaping, one trailing newline (§8.4).
    public static func serialise(_ panel: StylePanel) throws -> Data {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys, .prettyPrinted, .withoutEscapingSlashes]
        var data = try encoder.encode(panel)
        data.append(0x0A)
        return data
    }
}
