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
}

public enum StylePanelProjection {
    public static func make(style: RegisteredStyle,
                            prompts: [String],
                            selectedGroupId: String?,
                            capabilityStates: [String: String],
                            attachments: [StyleAttachmentItem],
                            prerequisites: StylePrerequisiteResult) -> StylePanel {
        let manifest = style.manifest
        let evaluator = style.evaluator
        let phase = evaluator.currentPhase(prompts: prompts)
        let ordered = manifest.orderedPhases
        let selected = selectedGroupId.flatMap { manifest.group($0) } ?? evaluator.initialGroup(capabilityStates: capabilityStates)
        let startActions = evaluator.startActions(phase: phase)
        let next = startActions.isEmpty ? evaluator.nextActions(phase: phase, group: selected) : startActions
        let nextIds = next.map(\.id)

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
                          recommended: evaluator.recommendedAction(capabilityStates: capabilityStates, group: selected),
                          attachments: attachments.map { StylePanel.Attachment(id: $0.id, title: $0.title, detail: $0.detail, readOnly: $0.readOnly) },
                          setup: StylePanel.Setup(ready: prerequisites.ready, missing: prerequisites.missing,
                                                  hint: prerequisites.hint, installCommand: manifest.install?.command),
                          guidance: evaluator.guidanceLine(phase: phase, running: false),
                          presentation: StylePanel.Presentation(headerTitle: headerTitle, source: style.source,
                                                                icon: manifest.presentation.icon?.rawValue, tint: manifest.presentation.tint))
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
