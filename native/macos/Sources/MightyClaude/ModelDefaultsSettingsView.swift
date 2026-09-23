import SwiftUI
import MightyCore

struct ModelDefaultsSettingsSection: View {
    @EnvironmentObject private var store: AppStore

    var body: some View {
        Section {
            Text(L("settings.modelDefaults.windowsDescription"))
                .font(.system(size: 11)).foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            Text(L("settings.modelDefaults.configuredNote", ["configuredSuffix": L("graph.nodeModel.configuredSuffix")]))
                .font(.system(size: 11)).foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            ProviderDefaultsRows(provider: "claude").environmentObject(store)
            ProviderDefaultsRows(provider: "codex").environmentObject(store)
        } header: {
            Text(L("settings.modelDefaults.sectionTitle"))
        }
    }
}

private struct ProviderDefaultsRows: View {
    @EnvironmentObject private var store: AppStore
    let provider: String
    @ViewState private var addName = ""
    @ViewState private var addSupportsEffort = false
    @ViewState private var addEffortLevels: Set<String> = []
    @ViewState private var validationError: String?

    private var registered: [RegisteredModelEntry] {
        provider == "codex"
            ? (store.snapshot.modelDefaults?.codex.registeredModels ?? [])
            : (store.snapshot.modelDefaults?.claude.registeredModels ?? [])
    }
    private var catalog: ModelCatalog {
        store.providerRuntime(provider, workspaceId: store.snapshot.activeWorkspaceId ?? "").modelCatalog
    }
    private var modes: [String] { ProviderOptions.permissionModes(provider: provider) }

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(ProviderOptions.label(provider)).font(.system(size: 12, weight: .medium))
            ForEach(modes, id: \.self) { mode in
                modeRow(mode)
            }
            Divider()
            Text(L("settings.modelDefaults.registeredTitle"))
                .font(.system(size: 11, weight: .medium))
            ForEach(registered, id: \.name) { entry in
                registeredRow(entry)
            }
            addRow
            if let err = validationError {
                Text(err)
                    .font(.system(size: 11)).foregroundStyle(.red)
                    .fixedSize(horizontal: false, vertical: true)
                    .accessibilityIdentifier("modelDefaults-error-\(provider)")
            }
        }
        .padding(.vertical, 4)
    }

    private func modeRow(_ mode: String) -> some View {
        HStack(spacing: 8) {
            Text(permissionLabel(mode, provider: provider)).font(.system(size: 11))
            Spacer(minLength: 4)
            Picker("", selection: Binding(
                get: {
                    let d = provider == "codex"
                        ? store.snapshot.modelDefaults?.codex
                        : store.snapshot.modelDefaults?.claude
                    return d?.modeDefaults[mode] ?? "default"
                },
                set: { store.setAppModeDefault(provider: provider, mode: mode, model: $0) }
            )) {
                Text(L("settings.modelDefaults.defaultOption")).tag("default")
                ForEach(catalog.models.filter { $0.value != "default" }, id: \.value) { opt in
                    Text(opt.displayName).tag(opt.value)
                }
                if !registered.isEmpty {
                    Divider()
                    ForEach(registered, id: \.name) { entry in
                        Text(verbatim: entry.name).tag(entry.name)
                    }
                }
            }
            .labelsHidden()
            .pickerStyle(.menu)
            .frame(maxWidth: 200)
            .accessibilityIdentifier("modelDefaults-\(provider)-\(mode)")
        }
    }

    private func registeredRow(_ entry: RegisteredModelEntry) -> some View {
        HStack {
            VStack(alignment: .leading, spacing: 2) {
                Text(verbatim: entry.name).font(.system(size: 11, design: .monospaced))
                if entry.supportsEffort {
                    Text(L("settings.modelDefaults.supportsEffortLabel"))
                        .font(.system(size: 10)).foregroundStyle(.secondary)
                }
            }
            Spacer(minLength: 0)
            Button(L("settings.modelDefaults.deleteButton"), role: .destructive) {
                store.removeRegisteredModel(provider: provider, name: entry.name)
            }
            .controlSize(.small)
        }
        .accessibilityIdentifier("modelDefaults-registered-\(provider)-\(entry.name)")
    }

    private var addRow: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: 6) {
                TextField(L("settings.modelDefaults.addPlaceholder"), text: $addName)
                    .textFieldStyle(.roundedBorder)
                    .font(.system(size: 11, design: .monospaced))
                    .onSubmit { addModel() }
                    .accessibilityIdentifier("modelDefaults-add-\(provider)")
                Button(L("settings.modelDefaults.addButton")) { addModel() }
                    .controlSize(.small)
                    .accessibilityIdentifier("modelDefaults-addButton-\(provider)")
            }
            Toggle(L("settings.modelDefaults.supportsEffortLabel"), isOn: $addSupportsEffort)
                .font(.system(size: 11))
                .accessibilityIdentifier("modelDefaults-addEffort-\(provider)")
                .onChange(of: addSupportsEffort) { _, on in if !on { addEffortLevels = [] } }
            if addSupportsEffort {
                VStack(alignment: .leading, spacing: 2) {
                    Text(L("settings.modelDefaults.effortLevelsLabel"))
                        .font(.system(size: 10)).foregroundStyle(.secondary)
                    HStack(spacing: 4) {
                        ForEach(ProviderOptions.efforts, id: \.self) { level in
                            let selected = addEffortLevels.contains(level)
                            Button(level) {
                                if selected { addEffortLevels.remove(level) } else { addEffortLevels.insert(level) }
                            }
                            .controlSize(.mini)
                            .buttonStyle(.borderedProminent)
                            .opacity(selected ? 1.0 : 0.4)
                            .accessibilityIdentifier("modelDefaults-addLevel-\(provider)-\(level)")
                        }
                    }
                }
            }
        }
    }

    private func addModel() {
        do {
            let entry = try CoreValidation.validateRegistration(
                name: addName, provider: provider, existingEntries: registered,
                supportsEffort: addSupportsEffort,
                supportedEffortLevels: ProviderOptions.efforts.filter { addEffortLevels.contains($0) }
            )
            store.addRegisteredModel(provider: provider, entry: entry)
            addName = ""
            addSupportsEffort = false
            addEffortLevels = []
            validationError = nil
        } catch {
            validationError = error.localizedDescription
        }
    }
}
