import SwiftUI
import MightyCore

/// The Settings sheet's page in the One UI layout: a large title over its sections, given way to a small title in the
/// pane's bar as the page scrolls (`OneUIPane`). A grouped `Form` pads every section by about 20pt and every row by about
/// 10pt, and on macOS 14 neither `contentMargins` nor `listRowInsets` reaches into it, so Settings lays its sections out
/// itself (`SettingsGroup`), with switches and labelled values drawn as dense rows.
struct SettingsPage<Content: View, Trailing: View>: View {
    let title: String
    @ViewBuilder let trailing: Trailing
    @ViewBuilder let content: Content

    var body: some View {
        OneUIPane(title: title, subtitle: nil, trailing: { trailing }) {
            VStack(alignment: .leading, spacing: DesignMetrics.Spacing.lg) { content }
                .frame(maxWidth: .infinity, alignment: .leading)
        }
        .toggleStyle(SettingsSwitchStyle())
        .labeledContentStyle(SettingsLabeledStyle())
    }
}

/// The One UI Settings' own metrics (Windows' native.css draws the same): the gap between cards, a card's row inset,
/// a pane's side inset, the large title's inset and the bar's leading inset, and the switch knob's inset off and on.
/// The sizes the popup standard names come from `DesignMetrics.Settings` (fixture `metrics.settings`).
enum OneUIMetrics {
    static let cardGap: CGFloat = 14
    static let rowInset = DesignMetrics.Settings.cardPaddingX
    static let paneInset: CGFloat = 14
    static let titleInset: CGFloat = 10
    static let barLeading: CGFloat = 24
    static let bottomInset: CGFloat = 24
    static let dividerLeading = DesignMetrics.Settings.navDividerInset
    static let knobInsetOff: CGFloat = 5
    static let knobInsetOn: CGFloat = 3
}

/// How far a One UI pane's content has scrolled under its large title.
private struct OneUIScrollKey: PreferenceKey {
    static let defaultValue: CGFloat = 0
    static func reduce(value: inout CGFloat, nextValue: () -> CGFloat) { value = nextValue() }
}

/// One pane of the One UI Settings: on the page colour, a 48pt bar on top (its small title shown once the large one has
/// scrolled under it, and `trailing` at its end), then a scroll view that opens on the large title.
struct OneUIPane<Content: View, Trailing: View>: View {
    let title: String
    let subtitle: String?
    @ViewBuilder let trailing: Trailing
    @ViewBuilder let content: Content
    /// 0 at rest, 1 once the large title has gone under the bar; written only when it changes.
    @ViewState private var progress: CGFloat = 0

    static var barHeight: CGFloat { DesignMetrics.Dialog.headerHeight }
    static var heroHeight: CGFloat { DesignMetrics.Settings.heroHeight }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: OneUIMetrics.cardGap) {
                VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xs) {
                    Text(title).font(.ui(size: DesignMetrics.Settings.largeTitle, weight: .semibold)).lineLimit(2).minimumScaleFactor(0.8)
                        .accessibilityAddTraits(.isHeader)
                    if let subtitle { Text(subtitle).font(.ui(size: 12)).foregroundStyle(.secondary) }
                }
                .frame(maxWidth: .infinity, minHeight: Self.heroHeight, alignment: .bottomLeading)
                .padding(.horizontal, OneUIMetrics.titleInset).padding(.bottom, DesignMetrics.Spacing.md)
                .opacity(1 - progress)
                .background(GeometryReader { proxy in
                    Color.clear.preference(key: OneUIScrollKey.self, value: Self.barHeight - proxy.frame(in: .named("oneUIPane")).minY)
                })
                content
            }
            .padding(.horizontal, OneUIMetrics.paneInset).padding(.top, Self.barHeight).padding(.bottom, OneUIMetrics.bottomInset)
        }
        // The system's scroller: with "Show scroll bars: Always" (or a mouse) it is the thing to
        // drag, so it is never hidden, even though a legacy one takes a gutter on pages that scroll.
        .scrollIndicators(.automatic)
        .coordinateSpace(name: "oneUIPane")
        .onPreferenceChange(OneUIScrollKey.self) { offset in
            let next = min(1, max(0, offset / Self.heroHeight))
            if next != progress { progress = next }
        }
        .overlay(alignment: .top) {
            HStack(spacing: DesignMetrics.Spacing.sm) {
                Text(title).font(.ui(size: DesignMetrics.Settings.barTitle, weight: .semibold)).lineLimit(1)
                    .opacity(progress > 0.85 ? 1 : 0).animation(.easeOut(duration: 0.12), value: progress > 0.85)
                    .accessibilityHidden(true)
                Spacer(minLength: 0)
                trailing
            }
            .padding(.leading, OneUIMetrics.barLeading).padding(.trailing, OneUIMetrics.titleInset)
            .frame(height: Self.barHeight)
            .background(Palette.canvas)
        }
        .background(Palette.canvas)
    }
}

extension OneUIPane where Trailing == EmptyView {
    init(title: String, subtitle: String?, @ViewBuilder content: () -> Content) {
        self.init(title: title, subtitle: subtitle, trailing: { EmptyView() }, content: content)
    }
}

/// One settings section in the One UI layout: its title in the accent over a rounded card of rows. Stands in for
/// `Section` on a `SettingsPage` and keeps its two initialisers (a title, or a header view).
struct SettingsGroup<Content: View, Header: View>: View {
    private let content: Content
    private let header: Header

    init(@ViewBuilder content: () -> Content, @ViewBuilder header: () -> Header) {
        self.content = content()
        self.header = header()
    }

    var body: some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xs) {
            header
                .font(.ui(size: DesignMetrics.Settings.sectionTitle, weight: .semibold)).foregroundStyle(Palette.accent)
                .padding(.leading, DesignMetrics.Settings.cardPaddingX).padding(.top, DesignMetrics.Spacing.sm)
                .accessibilityAddTraits(.isHeader)
            // Each row draws the line over itself (`SettingsRow`); the card's top hides the first one's.
            VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xs) { content }
                .frame(maxWidth: .infinity, alignment: .leading)
                .overlay(alignment: .top) {
                    Rectangle().fill(Palette.panel).frame(height: 1).allowsHitTesting(false).accessibilityHidden(true)
                }
                .padding(.horizontal, DesignMetrics.Settings.cardPaddingX).padding(.vertical, DesignMetrics.Spacing.sm)
                .background(RoundedRectangle(cornerRadius: DesignMetrics.Settings.cardRadius, style: .continuous).fill(Palette.panel))
        }
    }
}

extension SettingsGroup where Header == Text {
    init(_ title: String, @ViewBuilder content: () -> Content) {
        self.init(content: content) { Text(title) }
    }
}

/// A settings switch: its label on the leading side (a `SettingsRowLabel` for a name and caption),
/// the switch at the trailing edge, and the whole row (a `SettingsRow`'s height and line) a place to click. The row is one accessibility
/// element that toggles when pressed, so the identifier and label put on the `Toggle` name the switch.
struct SettingsSwitchStyle: ToggleStyle {
    func makeBody(configuration: Configuration) -> some View {
        SettingsSwitchRow(configuration: configuration)
    }
}

private struct SettingsSwitchRow: View {
    let configuration: ToggleStyleConfiguration
    @Environment(\.isEnabled) private var isEnabled

    var body: some View {
        HStack(alignment: .center, spacing: DesignMetrics.Spacing.lg) {
            configuration.label
                .font(.ui(size: DesignMetrics.Settings.rowName))
                .frame(maxWidth: .infinity, alignment: .leading)
                .contentShape(Rectangle())
                .onTapGesture { if isEnabled { configuration.isOn.toggle() } }
            OneUISwitch(isOn: configuration.$isOn)
        }
        .settingsRowFrame()
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(.isToggle)
        .accessibilityAction { if isEnabled { configuration.isOn.toggle() } }
    }
}

/// One UI's switch: a 42×24 track, ringed with a small knob when off, filled in the accent with a larger knob when on.
struct OneUISwitch: View {
    @Binding var isOn: Bool
    @Environment(\.isEnabled) private var isEnabled

    var body: some View {
        ZStack(alignment: isOn ? .trailing : .leading) {
            Capsule().fill(isOn ? Palette.accent : Color.clear)
            // Inset by half the line and a pixel more, so the ring's antialiased edge stays inside
            // the 42pt frame and draws as a whole pill instead of flat ends.
            Capsule().inset(by: 1.5).stroke(Palette.token(\.ink3), lineWidth: isOn ? 0 : 2)
            Circle().fill(isOn ? Palette.onAccent : Palette.token(\.ink3))
                .frame(width: isOn ? 18 : 14, height: isOn ? 18 : 14)
                .padding(.horizontal, isOn ? OneUIMetrics.knobInsetOn : OneUIMetrics.knobInsetOff)
        }
        .frame(width: DesignMetrics.Settings.switchWidth, height: DesignMetrics.Settings.switchHeight)
        .opacity(isEnabled ? 1 : 0.5)
        .contentShape(Capsule())
        .onTapGesture { if isEnabled { isOn.toggle() } }
        .animation(.easeOut(duration: 0.15), value: isOn)
        // Read out as the platform switch it stands in for, so the combined row keeps its on/off value.
        .accessibilityRepresentation { Toggle("", isOn: $isOn).toggleStyle(.switch) }
    }
}

/// A labelled value or control on one row: the label leading, the value trailing in the secondary
/// ink, with a `SettingsRow`'s height and line.
struct SettingsLabeledStyle: LabeledContentStyle {
    func makeBody(configuration: Configuration) -> some View {
        HStack(alignment: .center, spacing: DesignMetrics.Spacing.lg) {
            configuration.label.font(.ui(size: DesignMetrics.Settings.rowName))
            Spacer(minLength: DesignMetrics.Spacing.md)
            configuration.content.foregroundStyle(.secondary)
        }
        .settingsRowFrame()
    }
}

/// The one settings row (popup standard §2): the name (14) and an optional caption (12) on the
/// leading side, the control on the trailing side; `wide` puts the name over a control as wide as
/// the row (an address). At least 48pt tall, with a line over it that the card hides on its first row.
struct SettingsRow<Control: View>: View {
    let name: String
    var caption: String?
    var wide = false
    private let control: Control

    init(_ name: String, caption: String? = nil, wide: Bool = false, @ViewBuilder control: () -> Control) {
        self.name = name
        self.caption = caption
        self.wide = wide
        self.control = control()
    }

    var body: some View {
        Group {
            if wide {
                VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
                    SettingsRowLabel(name, caption: caption)
                    control.frame(maxWidth: .infinity, alignment: .leading)
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            } else {
                HStack(alignment: .center, spacing: DesignMetrics.Spacing.lg) {
                    SettingsRowLabel(name, caption: caption).frame(maxWidth: .infinity, alignment: .leading)
                    control
                }
            }
        }
        .settingsRowFrame()
        .accessibilityElement(children: .contain)
    }
}

/// A row's name and, under it, its caption in the secondary ink.
struct SettingsRowLabel: View {
    let name: String
    var caption: String?

    init(_ name: String, caption: String? = nil) {
        self.name = name
        self.caption = caption
    }

    var body: some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
            Text(name).font(.ui(size: DesignMetrics.Settings.rowName)).foregroundStyle(Palette.ink)
            if let caption, !caption.isEmpty {
                Text(caption).font(.ui(size: DesignMetrics.Settings.rowCaption)).foregroundStyle(Palette.ink2)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }
}

/// The line over a row: a hairline in `border`, as wide as the row (the card's 16pt side is its inset).
private struct SettingsRowLine: ViewModifier {
    @Environment(\.displayScale) private var displayScale

    func body(content: Content) -> some View {
        content.overlay(alignment: .top) {
            Rectangle().fill(Palette.border).frame(height: 1 / max(1, displayScale)).allowsHitTesting(false).accessibilityHidden(true)
        }
    }
}

extension View {
    /// A settings row's height and the line over it.
    func settingsRowFrame() -> some View {
        padding(.vertical, DesignMetrics.Spacing.sm)
            .frame(maxWidth: .infinity, minHeight: DesignMetrics.Settings.rowMinHeight, alignment: .leading)
            .modifier(SettingsRowLine())
    }

    /// A select (a menu picker) in a settings row: no label of its own, 160 to 240 wide.
    func settingsSelect() -> some View {
        labelsHidden().frame(minWidth: DesignMetrics.Settings.selectMinWidth, maxWidth: DesignMetrics.Settings.selectMaxWidth)
    }

    /// A text field in a settings row: 240 wide (an address takes a wide row instead).
    func settingsField() -> some View {
        frame(width: DesignMetrics.Settings.fieldWidth)
    }
}
