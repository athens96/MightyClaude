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
enum OneUIMetrics {
    static let cardGap: CGFloat = 14
    static let rowInset: CGFloat = 18
    static let paneInset: CGFloat = 14
    static let titleInset: CGFloat = 10
    static let barLeading: CGFloat = 24
    static let bottomInset: CGFloat = 24
    static let dividerLeading: CGFloat = 64
    static let knobInsetOff: CGFloat = 5
    static let knobInsetOn: CGFloat = 3
}

/// How far a One UI pane's content has scrolled under its large title.
private struct OneUIScrollKey: PreferenceKey {
    static let defaultValue: CGFloat = 0
    static func reduce(value: inout CGFloat, nextValue: () -> CGFloat) { value = nextValue() }
}

/// One pane of the One UI Settings: on the page colour, a 56pt bar on top (its small title shown once the large one has
/// scrolled under it, and `trailing` at its end), then a scroll view that opens on the large title.
struct OneUIPane<Content: View, Trailing: View>: View {
    let title: String
    let subtitle: String?
    @ViewBuilder let trailing: Trailing
    @ViewBuilder let content: Content
    /// 0 at rest, 1 once the large title has gone under the bar; written only when it changes.
    @ViewState private var progress: CGFloat = 0

    static var barHeight: CGFloat { 56 }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: OneUIMetrics.cardGap) {
                VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xs) {
                    Text(title).font(.ui(size: 28, weight: .semibold)).lineLimit(2).minimumScaleFactor(0.8)
                        .accessibilityAddTraits(.isHeader)
                    if let subtitle { Text(subtitle).font(.ui(size: 12)).foregroundStyle(.secondary) }
                }
                .frame(maxWidth: .infinity, minHeight: 120, alignment: .bottomLeading)
                .padding(.horizontal, OneUIMetrics.titleInset).padding(.bottom, DesignMetrics.Spacing.md)
                .opacity(1 - progress)
                .background(GeometryReader { proxy in
                    Color.clear.preference(key: OneUIScrollKey.self, value: Self.barHeight - proxy.frame(in: .named("oneUIPane")).minY)
                })
                content
            }
            .padding(.horizontal, OneUIMetrics.paneInset).padding(.top, Self.barHeight).padding(.bottom, OneUIMetrics.bottomInset)
        }
        .coordinateSpace(name: "oneUIPane")
        .onPreferenceChange(OneUIScrollKey.self) { offset in
            let next = min(1, max(0, offset / 120))
            if next != progress { progress = next }
        }
        .overlay(alignment: .top) {
            HStack(spacing: DesignMetrics.Spacing.sm) {
                Text(title).font(.ui(size: 15, weight: .semibold)).lineLimit(1)
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
                .font(.ui(size: 12, weight: .semibold)).foregroundStyle(Palette.accent)
                .padding(.leading, OneUIMetrics.rowInset).padding(.top, DesignMetrics.Spacing.sm)
                .accessibilityAddTraits(.isHeader)
            VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) { content }
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.horizontal, OneUIMetrics.rowInset).padding(.vertical, DesignMetrics.Spacing.md)
                .background(RoundedRectangle(cornerRadius: 22, style: .continuous).fill(Palette.panel))
        }
    }
}

extension SettingsGroup where Header == Text {
    init(_ title: String, @ViewBuilder content: () -> Content) {
        self.init(content: content) { Text(title) }
    }
}

/// A settings switch: its label on the leading side, the switch at the trailing edge, and the
/// whole row (at least `Layout.hitTarget` tall) a place to click. The row is one accessibility
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
        HStack(alignment: .center, spacing: DesignMetrics.Spacing.md) {
            configuration.label
                .frame(maxWidth: .infinity, alignment: .leading)
                .contentShape(Rectangle())
                .onTapGesture { if isEnabled { configuration.isOn.toggle() } }
            OneUISwitch(isOn: configuration.$isOn)
        }
        .frame(minHeight: DesignMetrics.Layout.hitTarget)
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
            Capsule().strokeBorder(Palette.token(\.ink3), lineWidth: isOn ? 0 : 2)
            Circle().fill(isOn ? Palette.onAccent : Palette.token(\.ink3))
                .frame(width: isOn ? 18 : 14, height: isOn ? 18 : 14)
                .padding(.horizontal, isOn ? OneUIMetrics.knobInsetOn : OneUIMetrics.knobInsetOff)
        }
        .frame(width: 42, height: 24)
        .opacity(isEnabled ? 1 : 0.5)
        .contentShape(Capsule())
        .onTapGesture { if isEnabled { isOn.toggle() } }
        .animation(.easeOut(duration: 0.15), value: isOn)
        // Read out as the platform switch it stands in for, so the combined row keeps its on/off value.
        .accessibilityRepresentation { Toggle("", isOn: $isOn).toggleStyle(.switch) }
    }
}

/// A labelled value or control (and a labelled picker) on one line: the label leading, the value
/// trailing in the secondary ink, like a grouped form's row.
struct SettingsLabeledStyle: LabeledContentStyle {
    func makeBody(configuration: Configuration) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: DesignMetrics.Spacing.md) {
            configuration.label
            Spacer(minLength: DesignMetrics.Spacing.md)
            configuration.content.foregroundStyle(.secondary)
        }
    }
}
