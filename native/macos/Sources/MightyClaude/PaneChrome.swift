import SwiftUI
import AppKit
import MightyCore

/// Concept D's pane chrome: the status-coloured hero over an agent pane, the slim ink
/// header over terminal, browser and files panes, and the card buttons the question
/// and permission cards share.
extension Palette {
    /// The hero's fill for a tone: the status colour, the ink for an idle pane.
    static func heroFill(_ tone: DesignTone) -> Color {
        switch tone {
        case .run: run
        case .wait: wait
        case .done: done
        case .err: err
        case .stop: stop
        case .idle: idle
        }
    }

    /// The words and glyphs on a hero: the amber takes only its own ink, the rest white.
    static func heroInk(_ tone: DesignTone) -> Color {
        tone == .wait ? onWait : onStatus
    }
}

/// The pane's status as an outlined pill on its hero or slim header.
struct HeroStatusPill: View {
    let text: String
    let ink: Color

    var body: some View {
        Text(text)
            .font(.system(size: 11, weight: .bold)).lineLimit(1)
            .foregroundStyle(ink)
            .padding(.horizontal, 8).frame(height: 20)
            .overlay { Capsule().strokeBorder(ink, lineWidth: 1.5) }
            .fixedSize()
    }
}

/// The hero's row of figures (`PaneHero.figures`); the clock ticks only while the pane runs.
struct PaneHeroFigures: View {
    let sessionID: String
    let figures: [PaneHero.Figure]
    let running: Bool

    var body: some View {
        HStack(alignment: .top, spacing: 20) {
            ForEach(Array(figures.enumerated()), id: \.offset) { _, figure in figureView(figure) }
        }
    }

    @ViewBuilder private func figureView(_ figure: PaneHero.Figure) -> some View {
        switch figure {
        case .elapsed(let timing):
            if running && timing.finishedAt == nil {
                TimelineView(.periodic(from: .now, by: 1)) { context in elapsed(timing, at: context.date) }
            } else {
                elapsed(timing, at: Date())
            }
        case .context(let percent):
            cell("\(percent)%", label: L("phone.session.hero.context"))
        case .cost(let cost):
            cell(PaneHero.cost(cost), label: L("phone.session.hero.cost"))
        case .tools(let count):
            cell("\(count)", label: L("phone.session.hero.tools"))
        }
    }

    private func elapsed(_ timing: AgentRunTiming, at date: Date) -> some View {
        cell(DashboardText.clock(timing, at: date), label: L("phone.session.hero.elapsed"))
            .help(AgentElapsedView.help(timing))
            .accessibilityIdentifier("agent-elapsed-\(sessionID)")
    }

    private func cell(_ value: String, label: String) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            Text(value).font(Palette.heading(19)).monospacedDigit().lineLimit(1)
            Text(label).font(.system(size: 11, weight: .semibold)).lineLimit(1)
        }
        .fixedSize()
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(L("pane.hero.figure", ["label": label, "value": value]))
    }
}

/// The slim ink bar over a pane that is not an agent's conversation: its glyph, its
/// title, what kind of pane it is, and (for a shell) its status and menu.
struct SlimPaneHeader<Trailing: View>: View {
    let kind: String
    let title: String
    let subtitle: String
    var status: String? = nil
    @ViewBuilder var trailing: () -> Trailing

    var body: some View {
        HStack(spacing: 8) {
            Image(systemName: paneSymbol(kind)).font(.system(size: 11, weight: .semibold)).accessibilityHidden(true)
            Text(title).font(.system(size: 13, weight: .bold)).lineLimit(1).truncationMode(.tail)
                .accessibilityAddTraits(.isHeader)
            Text(subtitle).font(.system(size: 11.5)).lineLimit(1).truncationMode(.tail)
            Spacer(minLength: 6)
            if let status { HeroStatusPill(text: status, ink: Palette.onStatus) }
            trailing()
        }
        .foregroundStyle(Palette.onStatus)
        .padding(.horizontal, 13).frame(height: 34)
        .background(Palette.idle, in: RoundedRectangle(cornerRadius: 11, style: .continuous))
        .padding(.horizontal, 8).padding(.top, 8).padding(.bottom, 2)
        .accessibilityElement(children: .contain)
    }
}

extension SlimPaneHeader where Trailing == EmptyView {
    init(kind: String, title: String, subtitle: String, status: String? = nil) {
        self.init(kind: kind, title: title, subtitle: subtitle, status: status) { EmptyView() }
    }
}

/// The slim header over a terminal, browser or files pane in a pane group.
struct PaneSlimHeader: View {
    @EnvironmentObject private var store: AppStore
    let session: RunSession

    var body: some View {
        let card = WorkDashboard.card(session, permissions: nil)
        SlimPaneHeader(kind: session.kind, title: session.title, subtitle: DashboardText.kind(card, localTerminal: false))
            .contentShape(Rectangle())
            .simultaneousGesture(TapGesture().onEnded { store.selectSession(session.id) })
    }
}

/// The buttons on the question and permission cards: the ink one goes forward, the
/// quiet one cancels or goes back.
struct PaneCardButtonStyle: ButtonStyle {
    var prominent = false

    func makeBody(configuration: Configuration) -> some View {
        CardButton(configuration: configuration, prominent: prominent)
    }

    private struct CardButton: View {
        let configuration: ButtonStyleConfiguration
        let prominent: Bool
        @Environment(\.isEnabled) private var enabled

        var body: some View {
            let shape = RoundedRectangle(cornerRadius: 9, style: .continuous)
            configuration.label
                .font(.system(size: 12, weight: .bold)).lineLimit(1)
                .foregroundStyle(prominent ? Palette.panel : Palette.ink)
                .padding(.horizontal, 13).frame(minHeight: 28)
                .background(prominent ? Palette.ink : Palette.raised, in: shape)
                .overlay { if !prominent { shape.strokeBorder(Palette.border, lineWidth: 1) } }
                .opacity(enabled ? (configuration.isPressed ? 0.8 : 1) : 0.45)
                .contentShape(shape)
        }
    }
}

/// The amber "?" or hand that heads a card waiting on the user.
struct PaneWaitBadge: View {
    let systemImage: String

    var body: some View {
        Circle().fill(Palette.wait).frame(width: 22, height: 22)
            .overlay { Image(systemName: systemImage).font(.system(size: 11, weight: .heavy)).foregroundStyle(Palette.onWait) }
            .accessibilityHidden(true)
    }
}

extension View {
    /// The card a question or permission request sits in: white, with the amber edge
    /// that says it waits on the user.
    func paneWaitCard() -> some View {
        let shape = RoundedRectangle(cornerRadius: 16, style: .continuous)
        return padding(14)
            .background(Palette.panel, in: shape)
            .overlay { shape.strokeBorder(Palette.wait, lineWidth: 2).allowsHitTesting(false) }
    }
}
