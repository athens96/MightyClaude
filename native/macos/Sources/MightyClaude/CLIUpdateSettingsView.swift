import SwiftUI
import MightyCore

struct CLIUpdateSettingsSection: View {
    @EnvironmentObject private var store: AppStore

    var body: some View {
        SettingsGroup(L("settings.cliUpdate.sectionTitle")) {
            Toggle(L("settings.cliUpdate.autoUpdateToggle"), isOn: Binding(
                get: { store.snapshot.autoUpdateCLIs == true },
                set: { store.snapshot.autoUpdateCLIs = $0 }
            ))
            .accessibilityIdentifier("cli-auto-update")
            Text(L("settings.cliUpdate.sectionDescriptionMac"))
                .font(.system(size: 11)).foregroundStyle(.secondary)
            Toggle(L("settings.cliUpdate.autoUpdatePluginsToggle"), isOn: Binding(
                get: { store.snapshot.autoUpdatePlugins != false },
                set: { store.snapshot.autoUpdatePlugins = $0 }
            ))
            .accessibilityIdentifier("plugin-auto-update")
            Text(L("settings.cliUpdate.autoUpdatePluginsDescription"))
                .font(.system(size: 11)).foregroundStyle(.secondary)
            HStack(spacing: DesignMetrics.Spacing.md) {
                if store.isUpdatingCLIs {
                    ProgressView().controlSize(.small)
                    Text(store.updatingCLI.map { L("settings.cliUpdate.progressProviderTemplate", ["provider": ProviderOptions.label($0)]) }
                         ?? store.updatingPluginsFor.map { L("settings.cliUpdate.progressPluginsTemplate", ["provider": ProviderOptions.label($0)]) }
                         ?? L("settings.cliUpdate.progressInspecting"))
                        .font(.system(size: 11)).accessibilityIdentifier("cli-update-progress")
                } else if let finished = store.cliUpdateFinishedAt {
                    Text(L("settings.cliUpdate.lastRunTemplate", ["time": finished.formatted(date: .omitted, time: .shortened)]))
                        .font(.system(size: 11)).foregroundStyle(.secondary)
                }
                Spacer()
                Button(store.isUpdatingCLIs ? L("settings.cliUpdate.updatingButton") : L("settings.cliUpdate.updateButton")) { store.startCLIUpdates() }
                    .disabled(store.isUpdatingCLIs || store.isManagingPlugins || !store.canManageCLIUpdates)
                    .accessibilityIdentifier("cli-update-now")
            }
            ForEach(ProviderOptions.ids, id: \.self) { provider in
                if let result = store.cliUpdateResults[provider] {
                    VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
                        HStack(spacing: DesignMetrics.Spacing.sm) {
                            Image(systemName: icon(result.status))
                                .foregroundStyle(result.status == "failed" ? Palette.waitText : Color.secondary)
                            Text(L("settings.cliUpdate.resultRowTemplate", ["provider": ProviderOptions.label(provider), "status": label(result.status)]))
                                .font(.system(size: 11, weight: .medium))
                            if ProviderOptions.isBeta(provider) { BetaBadge() }
                        }
                        if let before = result.beforeVersion, let after = result.afterVersion, before != after {
                            Text(L("settings.cliUpdate.versionChangeTemplate", ["before": before, "after": after])).font(.system(size: 10, design: .monospaced)).foregroundStyle(.secondary)
                        }
                        Text(result.detail).font(.system(size: 11)).foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true).textSelection(.enabled)
                    }
                    .accessibilityElement(children: .combine)
                    .accessibilityIdentifier("cli-update-result-\(provider)")
                }
                if let result = store.pluginUpdateResults[provider] {
                    VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
                        HStack(spacing: DesignMetrics.Spacing.sm) {
                            Image(systemName: icon(result.status == "succeeded" ? "updated" : result.status))
                                .foregroundStyle(result.status == "failed" ? Palette.waitText : Color.secondary)
                            Text(L("settings.cliUpdate.pluginRowTemplate", ["provider": ProviderOptions.label(provider), "status": label(result.status == "succeeded" ? "updated" : result.status)]))
                                .font(.system(size: 11, weight: .medium))
                            if ProviderOptions.isBeta(provider) { BetaBadge() }
                        }
                        Text(result.detail).font(.system(size: 11)).foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true).textSelection(.enabled)
                    }
                    .accessibilityElement(children: .combine)
                    .accessibilityIdentifier("plugin-update-result-\(provider)")
                }
            }
        }
    }

    private func label(_ status: String) -> String {
        switch status {
        case "updated": L("settings.cliUpdate.statusUpdated")
        case "current": L("settings.cliUpdate.statusCurrent")
        case "failed": L("settings.cliUpdate.statusFailed")
        case "cancelled": L("settings.cliUpdate.statusCancelled")
        case "busy": L("settings.cliUpdate.statusBusy")
        default: L("settings.cliUpdate.statusSkipped")
        }
    }
    private func icon(_ status: String) -> String {
        switch status {
        case "updated", "current": "checkmark.circle.fill"
        case "failed": "exclamationmark.circle"
        default: "info.circle"
        }
    }
}
