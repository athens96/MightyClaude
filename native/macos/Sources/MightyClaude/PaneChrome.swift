import SwiftUI
import AppKit
import MightyCore

/// The pane chrome: the one-line header's figures over an agent pane, the slim ink
/// header over terminal, browser and files panes, and the card buttons the question
/// and permission cards share.
extension Palette {
    /// A tone's solid fill, the ink for an idle pane: the graph's result strip and
    /// timeline nodes.
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

    /// The words and glyphs on a `heroFill`: the amber takes only its own ink, the rest white.
    static func heroInk(_ tone: DesignTone) -> Color {
        tone == .wait ? onWait : onStatus
    }

    /// The header's 기본 | 마이티 switch: its track and its chosen side (`DesignPalette`).
    static let segmentTrack = token(\.segmentTrack)
    static let segmentOn = token(\.segmentOn)
}

/// The pane's status as an outlined pill on the slim ink header.
struct HeroStatusPill: View {
    let text: String
    let ink: Color

    var body: some View {
        Text(text)
            .font(.system(size: 11, weight: .bold)).lineLimit(1)
            .foregroundStyle(ink)
            .padding(.horizontal, DesignMetrics.Spacing.md).frame(height: 18)
            .overlay { Capsule().strokeBorder(ink, lineWidth: 1.5) }
            .fixedSize()
    }
}

/// The figures on an agent pane's one-line header, in mono: `02:14 · 41% · $0.38 · 도구 12`.
/// They give way at the right (faded) on a narrow pane; the tooltip and the
/// accessibility label carry every value with its name, and the provider and model.
struct PaneHeaderFigures: View {
    let sessionID: String
    let figures: [PaneHero.Figure]
    let provider: String
    let model: String?
    let date: Date

    var body: some View {
        let full = (figures.map { L("pane.hero.figure", ["label": label($0), "value": value($0)]) }
            + [ProviderOptions.label(provider)] + (model.map { [$0] } ?? [])).joined(separator: " · ")
        let timing = figures.lazy.compactMap { if case .elapsed(let timing) = $0 { timing } else { nil } }.first
        Text(figures.map { if case .tools = $0 { L("pane.hero.figure", ["label": label($0), "value": value($0)]) } else { value($0) } }.joined(separator: " · "))
            .font(.system(size: 11, design: .monospaced)).foregroundStyle(Palette.ink2)
            .lineLimit(1).fixedSize()
            .frame(minWidth: 0, maxWidth: .infinity, alignment: .leading)
            .clipped()
            // Clipped figures fade out over the last 18pt rather than end in an ellipsis.
            .mask(HStack(spacing: 0) {
                Rectangle()
                LinearGradient(colors: [.black, .clear], startPoint: .leading, endPoint: .trailing).frame(width: 18)
            })
            .help(timing.map { full + "\n" + AgentElapsedView.help($0) } ?? full)
            .accessibilityElement(children: .ignore)
            .accessibilityLabel(full)
            .accessibilityIdentifier(timing != nil ? "agent-elapsed-\(sessionID)" : "pane-figures-\(sessionID)")
    }

    /// A figure's bare value: `02:14`, `41%`, `$0.38`, `12`.
    private func value(_ figure: PaneHero.Figure) -> String {
        switch figure {
        case .elapsed(let timing): DashboardText.clock(timing, at: date)
        case .context(let percent): "\(percent)%"
        case .cost(let cost): PaneHero.cost(cost)
        case .tools(let count): "\(count)"
        }
    }

    private func label(_ figure: PaneHero.Figure) -> String {
        switch figure {
        case .elapsed: L("phone.session.hero.elapsed")
        case .context: L("phone.session.hero.context")
        case .cost: L("phone.session.hero.cost")
        case .tools: L("phone.session.hero.tools")
        }
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
        HStack(spacing: DesignMetrics.Spacing.md) {
            Image(systemName: paneSymbol(kind)).font(.system(size: 11, weight: .semibold)).accessibilityHidden(true)
            Text(title).font(.system(size: 13, weight: .bold)).lineLimit(1).truncationMode(.tail)
                .accessibilityAddTraits(.isHeader)
            Text(subtitle).font(.system(size: 11.5)).lineLimit(1).truncationMode(.tail)
            Spacer(minLength: DesignMetrics.Spacing.sm)
            if let status { HeroStatusPill(text: status, ink: Palette.onStatus) }
            trailing()
        }
        .foregroundStyle(Palette.onStatus)
        .padding(.horizontal, DesignMetrics.Inset.paneHeaderLeading).frame(height: DesignMetrics.Layout.paneHeader)
        .background(Palette.idle, in: RoundedRectangle(cornerRadius: 11, style: .continuous))
        .padding(.horizontal, DesignMetrics.Spacing.xs).padding(.top, DesignMetrics.Spacing.xs).padding(.bottom, DesignMetrics.Spacing.xxs)
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
        SlimPaneHeader(kind: session.kind, title: session.title, subtitle: DashboardText.kind(card))
            .contentShape(Rectangle())
            .simultaneousGesture(TapGesture().onEnded { store.selectSession(session.id) })
    }
}

/// The buttons on the question and permission cards: the ink one goes forward, the
/// quiet one cancels or goes back.
struct PaneCardButtonStyle: ButtonStyle {
    var prominent = false
    /// The card's one primary choice, on the accent (a plan's 승인하고 실행).
    var accent = false

    func makeBody(configuration: Configuration) -> some View {
        CardButton(configuration: configuration, prominent: prominent || accent, accent: accent)
    }

    private struct CardButton: View {
        let configuration: ButtonStyleConfiguration
        let prominent: Bool
        var accent = false
        @Environment(\.isEnabled) private var enabled

        var body: some View {
            let shape = RoundedRectangle(cornerRadius: 9, style: .continuous)
            configuration.label
                .font(.system(size: 12, weight: .bold)).lineLimit(1)
                .foregroundStyle(accent ? Palette.onAccent : prominent ? Palette.panel : Palette.ink)
                .padding(.horizontal, DesignMetrics.Spacing.lg).frame(minHeight: 24)
                .background(accent ? Palette.accent : prominent ? Palette.ink : Palette.raised, in: shape)
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
        return padding(DesignMetrics.Spacing.md)
            .background(Palette.panel, in: shape)
            .overlay { shape.strokeBorder(Palette.wait, lineWidth: 2).allowsHitTesting(false) }
    }
}
