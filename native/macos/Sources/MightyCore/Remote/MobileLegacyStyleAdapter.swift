import Foundation

/// An older phone knows `mighty.ouroboros` and nothing else, so the bundled
/// Ouroboros keeps sending it. Built from the same projection the new panel
/// comes from, so the two cannot drift (§7.4).
public enum MobileLegacyStyleAdapter {
    public static func payload(style: RegisteredStyle, panel: StylePanel) -> MobileOuroboros? {
        style.id == MightyStyleIDs.ouroboros ? ouroboros(style: style, panel: panel) : nil
    }

    static func ouroboros(style: RegisteredStyle, panel: StylePanel) -> MobileOuroboros {
        let actions = style.manifest.actions
        // The old field is the phase map alone: at `goal` it was empty and the
        // older phone fell back to `all`. `panel.next` now also carries the
        // start rule's buttons, which that phone never saw (§7.4).
        let phase = panel.phase.flatMap { style.manifest.phase($0.id) }
        let next = style.evaluator.nextActions(phase: phase, group: nil)
        return MobileOuroboros(phase: panel.phase?.id ?? style.manifest.orderedPhases.first?.id ?? "",
                               ready: panel.setup.ready,
                               takesText: actions.filter(\.takesText).map(\.id),
                               next: next.map(skill),
                               all: actions.map(skill))
    }

    private static func skill(_ action: StyleAction) -> MobileGuidedSkill {
        MobileGuidedSkill(skill: action.id, title: action.title, help: action.help)
    }
}
