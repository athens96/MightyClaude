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
                Text("Tailscale은 앱이 직접 설치·실행·로그인을 돕고, 에이전트 CLI는 상태와 설치 명령을 보여줍니다.").font(.system(size: 11)).foregroundStyle(.secondary)
                Spacer()
                Button(store.componentsRefreshing ? "확인 중…" : "다시 확인") { Task { await store.refreshComponents() } }
                    .disabled(store.componentsRefreshing || store.componentAction != nil).accessibilityIdentifier("components-refresh")
            }
        } header: { Text("구성 요소") }
        .task { if store.components.isEmpty { await store.refreshComponents() } }
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
                                Text(running ? "진행 중…" : action.title)
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
        else if component.id == "tailscale" { Image(systemName: "network").font(.system(size: 13)) }
        else { Image(systemName: "puzzlepiece.extension").font(.system(size: 13)) }
    }

    private func stateLabel(_ state: String) -> some View {
        let (text, color): (String, Color) = switch state {
        case "installed": ("준비됨", .green)
        case "missing": ("설치 필요", .orange)
        case "attention": ("조치 필요", .orange)
        case "unsupported": ("미지원", .red)
        default: ("확인 중", .secondary)
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
