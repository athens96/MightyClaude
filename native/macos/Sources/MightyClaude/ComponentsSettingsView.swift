import AppKit
import MightyCore
import SwiftUI
import UniformTypeIdentifiers

/// Settings section listing what the app needs on this Mac: the setup
/// screen's tools (필요한 도구) and any plugin the app requires for an
/// installed agent.
struct ComponentsSettingsSection: View {
    @EnvironmentObject private var store: AppStore

    var body: some View {
        // No title of its own: it would only repeat the page's.
        SettingsGroup {
            // The card's side inset above it too (the card pads its top by `Spacing.sm`), as a row's text sits.
            VStack(alignment: .leading, spacing: DesignMetrics.Spacing.lg) { SetupToolsSettingsContent() }
                .padding(.top, DesignMetrics.Settings.cardPaddingX - DesignMetrics.Spacing.sm)
            ForEach(store.components) { component in row(component) }
            if let message = store.componentMessage {
                HStack(alignment: .top, spacing: DesignMetrics.Spacing.sm) {
                    Image(systemName: store.componentMessageIsError ? "exclamationmark.triangle.fill" : "checkmark.circle").foregroundStyle(store.componentMessageIsError ? Palette.waitText : Palette.doneText).padding(.top, 1)
                    Text(message).font(.ui(size: 11)).foregroundStyle(store.componentMessageIsError ? .primary : .secondary).fixedSize(horizontal: false, vertical: true).textSelection(.enabled)
                }
                .accessibilityIdentifier("components-message")
            }
        } header: { EmptyView() }
        .task { await store.refreshComponents() }
    }

    private func row(_ component: ComponentStatus) -> some View {
        // The shared row: a fixed icon column, the name over its caption, the state trailing; actions under the caption.
        HStack(alignment: .top, spacing: DesignMetrics.Spacing.sm) {
            icon(component).frame(width: Self.iconWidth)
            VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
                HStack(spacing: DesignMetrics.Spacing.sm) {
                    Text(component.title).font(.ui(size: DesignMetrics.Settings.rowName)).foregroundStyle(Palette.ink)
                    if ProviderOptions.isBeta(component.id) { BetaBadge() }
                    if let version = component.version { Text(version).font(.ui(size: 11, design: .monospaced)).foregroundStyle(.secondary).lineLimit(1) }
                    Spacer()
                    stateLabel(component.state)
                }
                Text(component.detail).font(.ui(size: DesignMetrics.Settings.rowCaption)).foregroundStyle(Palette.ink2).fixedSize(horizontal: false, vertical: true).textSelection(.enabled)
                if !component.actions.isEmpty {
                    HStack(spacing: DesignMetrics.Spacing.sm) {
                        ForEach(component.actions) { action in
                            let running = store.componentAction == component.id + ":" + action.id
                            Button {
                                store.performComponentAction(component: component.id, action: action.id)
                            } label: {
                                HStack(spacing: DesignMetrics.Spacing.xs) {
                                    if running { ProgressView().controlSize(.mini) }
                                    Text(running ? L("settings.components.inProgressLabel") : action.title)
                                }
                            }
                            .buttonStyle(action.primary ? AnyPrimitiveButtonStyle(.borderedProminent) : AnyPrimitiveButtonStyle(.bordered))
                            .disabled(store.componentAction != nil)
                            .accessibilityIdentifier("component-\(component.id)-\(action.id)")
                        }
                    }
                }
            }
        }
        .settingsRowFrame()
        .accessibilityElement(children: .contain).accessibilityIdentifier("component-\(component.id)")
    }

    /// The icon column of a component or toolkit row, so every name starts at one x.
    static let iconWidth: CGFloat = 18

    @ViewBuilder private func icon(_ component: ComponentStatus) -> some View {
        if ProviderOptions.ids.contains(component.id) { ProviderIcon(provider: component.id, size: 13) }
        else { Image(systemName: "puzzlepiece.extension").font(.ui(size: 13)) }
    }

    private func stateLabel(_ state: String) -> some View {
        let (text, color): (String, Color) = switch state {
        case "installed": (L("settings.components.statusInstalled"), Palette.doneText)
        case "missing": (L("settings.components.statusMissing"), Palette.waitText)
        case "attention": (L("settings.components.statusAttention"), Palette.waitText)
        case "unsupported": (L("settings.components.statusUnsupported"), Palette.errText)
        default: (L("settings.components.statusChecking"), .secondary)
        }
        return Text(text).font(.ui(size: 10, weight: .medium)).foregroundStyle(color)
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
        SettingsGroup {
            if let err = store.toolkitFileError {
                HStack(alignment: .top, spacing: DesignMetrics.Spacing.sm) {
                    Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(Palette.waitText).padding(.top, 1)
                    Text(L("settings.toolkit.errorBanner") + " " + err).font(.ui(size: 11)).foregroundStyle(.primary).fixedSize(horizontal: false, vertical: true).textSelection(.enabled)
                }
            }
            ForEach(store.toolkitEntries, id: \.entryId) { entry in
                toolkitRow(entry)
            }
            if let results = store.toolkitRunResults {
                toolkitResultTable(results)
            }
            Text(L("settings.toolkit.sectionDescription")).font(.ui(size: DesignMetrics.Settings.rowCaption)).foregroundStyle(Palette.ink2).fixedSize(horizontal: false, vertical: true)
                .settingsRowFrame()
            HStack(spacing: DesignMetrics.Spacing.sm) {
                Spacer()
                Button(L("settings.toolkit.addButton")) { addEntryFromFile() }
                    .disabled(store.toolkitRunning)
                    .accessibilityIdentifier("settings-toolkit-add")
                Button(L("settings.toolkit.exportButton")) { exportEntriesToFile() }
                    .disabled(store.toolkitRunning)
                    .accessibilityIdentifier("settings-toolkit-export")
                Button(L("settings.toolkit.importButton")) { importEntriesFromFile() }
                    .disabled(store.toolkitRunning)
                    .accessibilityIdentifier("settings-toolkit-import")
                Button(store.toolkitRunning ? L("settings.toolkit.installingButton") : L("settings.toolkit.installButton")) {
                    store.planToolkitInstall()
                }
                .disabled(store.toolkitRunning)
                .accessibilityIdentifier("settings-toolkit-install")
            }
            .settingsRowFrame()
        } header: { Text(L("settings.toolkit.sectionTitle")) }
        .sheet(isPresented: $store.toolkitShowConfirmation) {
            toolkitConfirmationSheet()
        }
        .task { await store.refreshToolkit() }
    }

    private func toolkitRow(_ entry: ToolkitEntry) -> some View {
        let isInstalled = isEntryInstalled(entry)
        let approval = store.toolkitApprovals[entry.entryId]
        return HStack(alignment: .top, spacing: DesignMetrics.Spacing.sm) {
            Image(systemName: "wrench.and.screwdriver").font(.ui(size: 13)).frame(width: ComponentsSettingsSection.iconWidth)
            VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xs) {
                HStack(spacing: DesignMetrics.Spacing.sm) {
                    Text(entry.displayName).font(.ui(size: DesignMetrics.Settings.rowName)).foregroundStyle(Palette.ink)
                    if entry.source == .bundled {
                        Text(L("settings.toolkit.bundledBadge")).font(.ui(size: 9, weight: .medium)).foregroundStyle(.secondary).padding(.horizontal, DesignMetrics.Spacing.xs).padding(.vertical, 1).background(Color.secondary.opacity(0.15)).clipShape(RoundedRectangle(cornerRadius: 3))
                    } else if approval != nil {
                        Text(L("settings.toolkit.approvedBadge")).font(.ui(size: 9, weight: .medium)).foregroundStyle(Palette.doneText).padding(.horizontal, DesignMetrics.Spacing.xs).padding(.vertical, 1).background(Palette.doneSoft).clipShape(RoundedRectangle(cornerRadius: 3))
                    }
                    Spacer()
                    Text(isInstalled ? L("settings.toolkit.statusInstalled") : L("settings.toolkit.statusMissing"))
                        .font(.ui(size: 10, weight: .medium))
                        .foregroundStyle(isInstalled ? Palette.doneText : Palette.waitText)
                }
                if entry.source == .user {
                    HStack(spacing: DesignMetrics.Spacing.sm) {
                        if approval == nil {
                            Text(L("settings.toolkit.needsApproval")).font(.ui(size: 11)).foregroundStyle(.secondary)
                            Button(L("settings.toolkit.approveButton")) {
                                store.approveToolkitEntry(entry.entryId)
                            }
                            .buttonStyle(.bordered)
                            .disabled(store.toolkitRunning)
                            .accessibilityIdentifier("settings-toolkit-approve-\(entry.entryId)")
                        }
                        // Removing only delists the entry: nothing is uninstalled or deleted on disk.
                        Button(L("settings.toolkit.removeButton")) {
                            store.removeToolkitEntry(entry.entryId)
                        }
                        .buttonStyle(.bordered)
                        .disabled(store.toolkitRunning)
                        .accessibilityIdentifier("settings-toolkit-remove-\(entry.entryId)")
                    }
                }
            }
        }
        .settingsRowFrame()
        .accessibilityIdentifier("settings-toolkit-entry-\(entry.entryId)")
    }

    private func isEntryInstalled(_ entry: ToolkitEntry) -> Bool {
        let approval = store.toolkitApprovals[entry.entryId]
        return ToolkitProbe.probe(entry: entry, approval: approval, context: store.makeProbeContext(environment: store.toolkitProbeEnvironment)) == .installed
    }

    private func toolkitResultTable(_ results: [ToolkitRunItem]) -> some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
            ForEach(results, id: \.entryId) { item in
                let entry = store.toolkitEntries.first { $0.entryId == item.entryId }
                HStack(spacing: DesignMetrics.Spacing.sm) {
                    verdictIcon(item.verdict)
                    Text(entry?.displayName ?? item.entryId).font(.ui(size: 11))
                    Spacer()
                    Text(verdictLabel(item.verdict)).font(.ui(size: 10, weight: .medium)).foregroundStyle(verdictColor(item.verdict))
                }
            }
        }
        .padding(.vertical, DesignMetrics.Spacing.xxs)
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("settings-toolkit-results")
    }

    /// A form sheet (kind B): every command the run would start, then [취소] [설치].
    private func toolkitConfirmationSheet() -> some View {
        DialogScaffold(L("settings.toolkit.confirmTitle")) {
            VStack(alignment: .leading, spacing: DesignMetrics.Spacing.md) {
                Text(L("settings.toolkit.confirmDescription")).font(.ui(size: 12)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                ScrollView {
                    VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
                        ForEach(store.toolkitPlan, id: \.entry.entryId) { item in
                            confirmationPlanRow(item)
                        }
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                }
                .frame(maxHeight: 300)
            }
        } footer: {
            DialogFooter {
                Button(L("settings.toolkit.cancelButton")) { store.toolkitShowConfirmation = false }
                    .keyboardShortcut(.cancelAction)
                    .accessibilityIdentifier("settings-toolkit-cancel")
                Button(L("settings.toolkit.confirmInstall")) { store.runToolkitInstall() }
                    .keyboardShortcut(.defaultAction).buttonStyle(.borderedProminent)
                    .accessibilityIdentifier("settings-toolkit-confirm")
            }
        }
        .dialogSheetWidth(DesignMetrics.Dialog.sheetWidthL)
    }

    private func confirmationPlanRow(_ item: ToolkitPlanItem) -> some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xs) {
            Text(item.entry.displayName).font(.ui(size: 12, weight: .medium))
            switch item.action {
            case .skip:
                Text("→ " + L("settings.toolkit.verdictSkipped")).font(.ui(size: 11, design: .monospaced)).foregroundStyle(.secondary)
            case .run(commands: let commands):
                ForEach(Array(commands.enumerated()), id: \.offset) { _, cmd in
                    Text(cmd.joined(separator: " ")).font(.ui(size: 11, design: .monospaced)).foregroundStyle(.primary).textSelection(.enabled)
                }
            }
        }
        .padding(.vertical, DesignMetrics.Spacing.xxs)
    }

    @ViewBuilder private func verdictIcon(_ verdict: ToolkitRunItem.Verdict) -> some View {
        switch verdict {
        case .installed: Image(systemName: "checkmark.circle.fill").foregroundStyle(Palette.doneText).font(.ui(size: 11))
        case .failed: Image(systemName: "xmark.circle.fill").foregroundStyle(Palette.errText).font(.ui(size: 11))
        case .skipped: Image(systemName: "minus.circle").foregroundStyle(.secondary).font(.ui(size: 11))
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
        case .installed: Palette.doneText
        case .failed: Palette.errText
        case .skipped: .secondary
        }
    }

    // MARK: - File panels

    /// Adds one entry from a JSON file.  The closed decoder rejects anything
    /// that is not one of the five templates, so no free-form string gets in.
    private func addEntryFromFile() {
        guard let url = openJSONPanel(title: L("settings.toolkit.addButton")) else { return }
        store.addToolkitEntry(fromFile: url)
    }

    private func importEntriesFromFile() {
        guard let url = openJSONPanel(title: L("settings.toolkit.importButton")) else { return }
        store.importToolkit(from: url)
    }

    private func exportEntriesToFile() {
        let panel = NSSavePanel()
        panel.title = L("settings.toolkit.exportButton")
        panel.allowedContentTypes = [.json]
        panel.nameFieldStringValue = "toolkit.json"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        store.exportToolkit(to: url)
    }

    private func openJSONPanel(title: String) -> URL? {
        let panel = NSOpenPanel()
        panel.title = title
        panel.allowedContentTypes = [.json]
        panel.allowsMultipleSelection = false
        panel.canChooseDirectories = false
        return panel.runModal() == .OK ? panel.url : nil
    }
}
