import MightyCore
import SwiftUI

/// Settings section listing what the app needs on this Mac and offering the
/// next step for each item (install, launch, login, update).
struct ComponentsSettingsSection: View {
    @EnvironmentObject private var store: AppStore

    var body: some View {
        Section {
            ForEach(store.components) { component in row(component) }
            if let message = store.componentMessage {
                HStack(alignment: .top, spacing: 6) {
                    Image(systemName: store.componentMessageIsError ? "exclamationmark.triangle.fill" : "checkmark.circle").foregroundStyle(store.componentMessageIsError ? Color.orange : Color.green).padding(.top, 1)
                    Text(message).font(.system(size: 11)).foregroundStyle(store.componentMessageIsError ? .primary : .secondary).fixedSize(horizontal: false, vertical: true).textSelection(.enabled)
                }
                .accessibilityIdentifier("components-message")
            }
            HStack {
                Text(L("settings.components.sectionDescription")).font(.system(size: 11)).foregroundStyle(.secondary)
                Spacer()
                Button(store.componentsRefreshing ? L("settings.components.checkingButton") : L("settings.components.recheckButton")) { Task { await store.refreshComponents() } }
                    .disabled(store.componentsRefreshing || store.componentAction != nil).accessibilityIdentifier("components-refresh")
            }
        } header: { Text(L("settings.components.sectionTitle")) }
        .task {
            // Re-check whenever the sheet opens, then keep polling while
            // something still needs the user (an App Store install lands
            // outside the app, so nothing else would notice it).
            await store.refreshComponents()
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(5))
                guard !Task.isCancelled, store.componentAction == nil, !store.componentsRefreshing,
                      store.components.contains(where: { $0.state != "installed" }) else { continue }
                await store.refreshComponents()
            }
        }
    }

    private func row(_ component: ComponentStatus) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 8) {
                icon(component)
                Text(component.title).font(.system(size: 13, weight: .medium))
                if let version = component.version { Text(version).font(.system(size: 10, design: .monospaced)).foregroundStyle(.secondary).lineLimit(1) }
                Spacer()
                stateLabel(component.state)
            }
            Text(component.detail).font(.system(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true).textSelection(.enabled)
            if !component.actions.isEmpty {
                HStack(spacing: 8) {
                    ForEach(component.actions) { action in
                        let running = store.componentAction == component.id + ":" + action.id
                        Button {
                            store.performComponentAction(component: component.id, action: action.id)
                        } label: {
                            HStack(spacing: 5) {
                                if running { ProgressView().controlSize(.mini) }
                                Text(running ? L("settings.components.inProgressLabel") : action.title)
                            }
                        }
                        .controlSize(.small).buttonStyle(action.primary ? AnyPrimitiveButtonStyle(.borderedProminent) : AnyPrimitiveButtonStyle(.bordered))
                        .disabled(store.componentAction != nil)
                        .accessibilityIdentifier("component-\(component.id)-\(action.id)")
                    }
                }
            }
        }
        .padding(.vertical, 4)
        .accessibilityElement(children: .contain).accessibilityIdentifier("component-\(component.id)")
    }

    @ViewBuilder private func icon(_ component: ComponentStatus) -> some View {
        if ProviderOptions.ids.contains(component.id) { ProviderIcon(provider: component.id, size: 13) }
        else { Image(systemName: "puzzlepiece.extension").font(.system(size: 13)) }
    }

    private func stateLabel(_ state: String) -> some View {
        let (text, color): (String, Color) = switch state {
        case "installed": (L("settings.components.statusInstalled"), .green)
        case "missing": (L("settings.components.statusMissing"), .orange)
        case "attention": (L("settings.components.statusAttention"), .orange)
        case "unsupported": (L("settings.components.statusUnsupported"), .red)
        default: (L("settings.components.statusChecking"), .secondary)
        }
        return Text(text).font(.system(size: 10, weight: .medium)).foregroundStyle(color)
    }
}

/// Lets one `ForEach` pick between prominent and plain bordered buttons.
struct AnyPrimitiveButtonStyle: PrimitiveButtonStyle {
    private let make: (Configuration) -> AnyView
    init<S: PrimitiveButtonStyle>(_ style: S) { make = { AnyView(style.makeBody(configuration: $0)) } }
    func makeBody(configuration: Configuration) -> some View { make(configuration) }
}
