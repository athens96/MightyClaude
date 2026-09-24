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

// MARK: - Toolkit section

/// Settings → 구성 요소 › 내 작업 도구 모음
/// Merges bundled and user entries, drives the ONE confirmation sheet, and
/// shows the probe-based result table after each run.
struct ToolkitSettingsSection: View {
    @EnvironmentObject private var store: AppStore

    var body: some View {
        Section {
            if let err = store.toolkitFileError {
                HStack(alignment: .top, spacing: 6) {
                    Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(Color.orange).padding(.top, 1)
                    Text(L("settings.toolkit.errorBanner") + " " + err).font(.system(size: 11)).foregroundStyle(.primary).fixedSize(horizontal: false, vertical: true).textSelection(.enabled)
                }
            }
            ForEach(store.toolkitEntries, id: \.entryId) { entry in
                toolkitRow(entry)
            }
            if let results = store.toolkitRunResults {
                toolkitResultTable(results)
            }
            HStack {
                Text(L("settings.toolkit.sectionDescription")).font(.system(size: 11)).foregroundStyle(.secondary)
                Spacer()
                Button(store.toolkitRunning ? L("settings.toolkit.installingButton") : L("settings.toolkit.installButton")) {
                    store.planToolkitInstall()
                }
                .disabled(store.toolkitRunning)
                .accessibilityIdentifier("toolkit-install")
            }
        } header: { Text(L("settings.toolkit.sectionTitle")) }
        .sheet(isPresented: $store.toolkitShowConfirmation) {
            toolkitConfirmationSheet()
        }
        .task { await store.refreshToolkit() }
    }

    private func toolkitRow(_ entry: ToolkitEntry) -> some View {
        let isInstalled = isEntryInstalled(entry)
        let approval = store.toolkitApprovals[entry.entryId]
        return VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: 6) {
                Image(systemName: "wrench.and.screwdriver").font(.system(size: 12))
                Text(entry.displayName).font(.system(size: 13, weight: .medium))
                if entry.source == .bundled {
                    Text(L("settings.toolkit.bundledBadge")).font(.system(size: 9, weight: .medium)).foregroundStyle(.secondary).padding(.horizontal, 4).padding(.vertical, 1).background(Color.secondary.opacity(0.15)).clipShape(RoundedRectangle(cornerRadius: 3))
                } else if approval != nil {
                    Text(L("settings.toolkit.approvedBadge")).font(.system(size: 9, weight: .medium)).foregroundStyle(.green).padding(.horizontal, 4).padding(.vertical, 1).background(Color.green.opacity(0.12)).clipShape(RoundedRectangle(cornerRadius: 3))
                }
                Spacer()
                Text(isInstalled ? L("settings.toolkit.statusInstalled") : L("settings.toolkit.statusMissing"))
                    .font(.system(size: 10, weight: .medium))
                    .foregroundStyle(isInstalled ? Color.green : Color.orange)
            }
            if entry.source == .user && approval == nil {
                HStack(spacing: 6) {
                    Text(L("settings.toolkit.needsApproval")).font(.system(size: 11)).foregroundStyle(.secondary)
                    Button(L("settings.toolkit.approveButton")) {
                        store.approveToolkitEntry(entry.entryId)
                    }
                    .controlSize(.mini).buttonStyle(.bordered)
                    .disabled(store.toolkitRunning)
                    .accessibilityIdentifier("toolkit-approve-\(entry.entryId)")
                }
            }
        }
        .padding(.vertical, 2)
        .accessibilityIdentifier("toolkit-entry-\(entry.entryId)")
    }

    private func isEntryInstalled(_ entry: ToolkitEntry) -> Bool {
        let approval = store.toolkitApprovals[entry.entryId]
        let context = ToolkitProbeContext(
            home: FileManager.default.homeDirectoryForCurrentUser,
            environment: ProcessInfo.processInfo.environment,
            appDataDir: store.dataDirectory
        )
        return ToolkitProbe.probe(entry: entry, approval: approval, context: context) == .installed
    }

    private func toolkitResultTable(_ results: [ToolkitRunItem]) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            ForEach(results, id: \.entryId) { item in
                let entry = store.toolkitEntries.first { $0.entryId == item.entryId }
                HStack(spacing: 6) {
                    verdictIcon(item.verdict)
                    Text(entry?.displayName ?? item.entryId).font(.system(size: 11))
                    Spacer()
                    Text(verdictLabel(item.verdict)).font(.system(size: 10, weight: .medium)).foregroundStyle(verdictColor(item.verdict))
                }
            }
        }
        .padding(.vertical, 4)
    }

    private func toolkitConfirmationSheet() -> some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(L("settings.toolkit.confirmTitle")).font(.headline)
            Text(L("settings.toolkit.confirmDescription")).font(.system(size: 12)).foregroundStyle(.secondary)
            ScrollView {
                VStack(alignment: .leading, spacing: 8) {
                    ForEach(store.toolkitPlan, id: \.entry.entryId) { item in
                        confirmationPlanRow(item)
                    }
                }
            }
            .frame(maxHeight: 300)
            HStack {
                Spacer()
                Button(L("settings.toolkit.cancelButton")) { store.toolkitShowConfirmation = false }
                    .keyboardShortcut(.cancelAction)
                Button(L("settings.toolkit.confirmInstall")) { store.runToolkitInstall() }
                    .keyboardShortcut(.defaultAction).buttonStyle(.borderedProminent)
            }
        }
        .padding(20)
        .frame(minWidth: 480, maxWidth: 600)
    }

    private func confirmationPlanRow(_ item: ToolkitPlanItem) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(item.entry.displayName).font(.system(size: 12, weight: .medium))
            switch item.action {
            case .skip:
                Text("→ " + L("settings.toolkit.verdictSkipped")).font(.system(size: 11, design: .monospaced)).foregroundStyle(.secondary)
            case .run(commands: let commands):
                ForEach(Array(commands.enumerated()), id: \.offset) { _, cmd in
                    Text(cmd.joined(separator: " ")).font(.system(size: 11, design: .monospaced)).foregroundStyle(.primary).textSelection(.enabled)
                }
            }
        }
        .padding(.vertical, 2)
    }

    @ViewBuilder private func verdictIcon(_ verdict: ToolkitRunItem.Verdict) -> some View {
        switch verdict {
        case .installed: Image(systemName: "checkmark.circle.fill").foregroundStyle(.green).font(.system(size: 11))
        case .failed: Image(systemName: "xmark.circle.fill").foregroundStyle(.red).font(.system(size: 11))
        case .skipped: Image(systemName: "minus.circle").foregroundStyle(.secondary).font(.system(size: 11))
        }
    }

    private func verdictLabel(_ verdict: ToolkitRunItem.Verdict) -> String {
        switch verdict {
        case .installed: L("settings.toolkit.verdictInstalled")
        case .failed: L("settings.toolkit.verdictFailed")
        case .skipped: L("settings.toolkit.verdictSkipped")
        }
    }

    private func verdictColor(_ verdict: ToolkitRunItem.Verdict) -> Color {
        switch verdict {
        case .installed: .green
        case .failed: .red
        case .skipped: .secondary
        }
    }
}
