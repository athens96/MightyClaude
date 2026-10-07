import SwiftUI
import MightyCore

/// The Settings sheet's page: its sections stacked on the spacing scale inside one scroll view.
/// A grouped `Form` pads every section by about 20pt and every row by about 10pt, and on macOS 14
/// neither `contentMargins` nor `listRowInsets` reaches into it, so Settings lays its sections
/// out itself (`SettingsGroup`), with switches and labelled values drawn as dense rows.
struct SettingsPage<Content: View>: View {
    @ViewBuilder let content: Content

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: DesignMetrics.Spacing.lg) { content }
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(DesignMetrics.Inset.sheet)
        }
        .toggleStyle(SettingsSwitchStyle())
        .labeledContentStyle(SettingsLabeledStyle())
    }
}

/// One settings section: its title over a card of rows. Stands in for `Section` on a `SettingsPage`
/// and keeps its two initialisers (a title, or a header view).
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
                .font(.system(size: 12, weight: .semibold)).foregroundStyle(.secondary)
                .padding(.leading, DesignMetrics.Spacing.xs)
                .accessibilityAddTraits(.isHeader)
            VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) { content }
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.horizontal, DesignMetrics.Spacing.md).padding(.vertical, DesignMetrics.Spacing.sm)
                .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(Palette.subtle))
                .overlay(RoundedRectangle(cornerRadius: 8, style: .continuous).strokeBorder(Palette.border, lineWidth: 0.5))
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
            Toggle(isOn: configuration.$isOn) { configuration.label }
                .labelsHidden().toggleStyle(.switch).controlSize(.mini)
        }
        .frame(minHeight: DesignMetrics.Layout.hitTarget)
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(.isToggle)
        .accessibilityAction { if isEnabled { configuration.isOn.toggle() } }
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
