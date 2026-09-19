import Foundation

/// An older phone knows `mighty.ouroboros` and `mighty.paperthin` and nothing
/// else, so the two bundled styles keep sending them. Built from the same
/// projection the new panel comes from, so the two cannot drift (§7.4).
public enum MobileLegacyStyleAdapter {
    public static func payloads(style: RegisteredStyle, panel: StylePanel, casebook: StyleCasebook?) -> (ouroboros: MobileOuroboros?, paperthin: MobilePaperthin?) {
        switch style.id {
        case MightyStyleIDs.ouroboros: return (ouroboros(style: style, panel: panel), nil)
        case MightyStyleIDs.paperthin: return (nil, paperthin(style: style, panel: panel, casebook: casebook))
        default: return (nil, nil)
        }
    }

    static func ouroboros(style: RegisteredStyle, panel: StylePanel) -> MobileOuroboros {
        let actions = style.manifest.actions
        let byId = Dictionary(actions.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
        return MobileOuroboros(phase: panel.phase?.id ?? style.manifest.orderedPhases.first?.id ?? "",
                               ready: panel.setup.ready,
                               takesText: actions.filter(\.takesText).map(\.id),
                               next: panel.next.compactMap { byId[$0] }.map(skill),
                               all: actions.map(skill))
    }

    private static func skill(_ action: StyleAction) -> MobileGuidedSkill {
        MobileGuidedSkill(skill: action.id, title: action.title, help: action.help)
    }

    static func paperthin(style: RegisteredStyle, panel: StylePanel, casebook: StyleCasebook?) -> MobilePaperthin {
        let byId = Dictionary(style.manifest.actions.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
        let domains = style.manifest.groups.map { group in
            MobilePaperthinDomain(id: group.id, title: group.title, axis: group.axis ?? "", question: group.question ?? "",
                                  skills: group.actions.compactMap { byId[$0] }.map { action in
                                      MobilePaperthinSkill(name: action.id, emoji: action.glyph ?? "", summary: action.help,
                                                           scope: action.scope ?? "",
                                                           userInvoked: action.flags.contains(.userInvoked),
                                                           readOnly: action.flags.contains(.readOnly))
                                  })
        }
        let clean = casebook.map(StyleCapabilities.normalised)
        return MobilePaperthin(installed: panel.setup.ready, recommended: panel.recommended, domains: domains,
                               casebook: clean.map { MobilePaperthinCasebook(name: $0.name, weight: $0.weight, files: $0.files) })
    }
}
