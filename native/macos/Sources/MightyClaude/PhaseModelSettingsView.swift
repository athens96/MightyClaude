import SwiftUI
import MightyCore

private let mixedSentinel = "__mixed__"

struct PhaseModelSettingsSection: View {
    @EnvironmentObject private var store: AppStore

    var body: some View {
        Section {
            Text(L("settings.phaseModels.description"))
                .font(.system(size: 11)).foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            PhaseModelProviderBlock(provider: "claude").environmentObject(store)
            Divider()
            PhaseModelProviderBlock(provider: "codex").environmentObject(store)
        } header: {
            Text(L("settings.phaseModels.sectionTitle"))
        }
    }
}

private struct PhaseModelProviderBlock: View {
    @EnvironmentObject private var store: AppStore
    let provider: String
    @ViewState private var addName = ""
    @ViewState private var addSupportsEffort = false
    @ViewState private var addEffortLevels: Set<String> = []
    @ViewState private var validationError: String?

    private var phaseConfig: PhaseModelHardcodedConfig { store.snapshot.phaseModels ?? PhaseModelHardcodedConfig() }
    private var registered: [RegisteredModelEntry] {
        provider == "codex"
            ? (store.snapshot.modelDefaults?.codex.registeredModels ?? [])
            : (store.snapshot.modelDefaults?.claude.registeredModels ?? [])
    }
    private var catalog: ModelCatalog {
        store.providerRuntime(provider, workspaceId: store.snapshot.activeWorkspaceId ?? "").modelCatalog
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(ProviderOptions.label(provider)).font(.system(size: 12, weight: .medium))
            if provider == "claude" { claudePhaseRows } else { codexPhaseRows }
            Divider()
            if provider == "claude" { claudeKnobDetails } else { codexKnobDetails }
            Divider()
            Text(L("settings.phaseModels.registeredTitle"))
                .font(.system(size: 11, weight: .medium))
            ForEach(registered, id: \.name) { entry in
                registeredRow(entry)
            }
            addRow
            if let err = validationError {
                Text(err).font(.system(size: 11)).foregroundStyle(.red)
                    .fixedSize(horizontal: false, vertical: true)
                    .accessibilityIdentifier("phaseModels-error-\(provider)")
            }
        }
        .padding(.vertical, 4)
    }

    // MARK: - Claude phase rows

    private var claudePhaseRows: some View {
        Group {
            phaseRow(label: L("settings.phaseModels.phase.planning"),
                     selection: claudeRowValue(.planning),
                     onPick: { store.applyClaudePhaseRow(.planning, value: $0) })
            phaseRow(label: L("settings.phaseModels.phase.execution"),
                     selection: claudeRowValue(.execution),
                     onPick: { store.applyClaudePhaseRow(.execution, value: $0) })
            phaseRow(label: L("settings.phaseModels.phase.subagents"),
                     selection: claudeRowValue(.subagents),
                     onPick: { store.applyClaudePhaseRow(.subagents, value: $0) })
        }
    }

    private func claudeRowValue(_ phase: PhaseModelRouting.Phase) -> String {
        let c = phaseConfig.toPhaseModelConfig(omcAgents: nil, ouroborosKeys: nil)
        switch PhaseModelRouting.claudeRowState(phase: phase, config: c) {
        case .uniform(let v): return v
        case .mixed: return mixedSentinel
        case nil: return "default"
        }
    }

    // MARK: - Codex phase rows

    private var codexPhaseRows: some View {
        Group {
            phaseRow(label: L("settings.phaseModels.phase.review"),
                     selection: codexRowValue(.review),
                     onPick: { store.applyCodexPhaseRow(.review, value: $0) })
            phaseRow(label: L("settings.phaseModels.phase.subagents"),
                     selection: codexRowValue(.subagents),
                     onPick: { store.applyCodexPhaseRow(.subagents, value: $0) })
        }
    }

    private func codexRowValue(_ phase: PhaseModelRouting.Phase) -> String {
        let c = phaseConfig.toPhaseModelConfig(omcAgents: nil, ouroborosKeys: nil)
        switch PhaseModelRouting.codexRowState(phase: phase, config: c) {
        case .uniform(let v): return v
        case .mixed: return mixedSentinel
        case nil: return "default"
        }
    }

    // MARK: - Phase row picker

    private func phaseRow(label: String, selection: String, onPick: @escaping (String) -> Void) -> some View {
        HStack(spacing: 8) {
            Text(label).font(.system(size: 11))
            Spacer(minLength: 4)
            Picker("", selection: Binding(get: { selection }, set: { if $0 != mixedSentinel { onPick($0) } })) {
                Text(L("settings.phaseModels.defaultOption")).tag("default")
                if selection == mixedSentinel {
                    Text(L("settings.phaseModels.mixed")).tag(mixedSentinel)
                }
                ForEach(catalog.models.filter { $0.value != "default" }, id: \.value) { opt in
                    Text(opt.displayName).tag(opt.value)
                }
                if !registered.isEmpty {
                    Divider()
                    ForEach(registered, id: \.name) { e in Text(verbatim: e.name).tag(e.name) }
                }
            }
            .labelsHidden().pickerStyle(.menu).frame(maxWidth: 200)
        }
    }

    // MARK: - Knob detail rows

    private var claudeKnobDetails: some View {
        Group {
            knobRow(label: L("settings.phaseModels.knob.claudeMain"), keyPath: \.claudeMain)
            knobRow(label: L("settings.phaseModels.knob.claudeOpusAlias"), keyPath: \.claudeOpusAlias)
            knobRow(label: L("settings.phaseModels.knob.claudeSonnetAlias"), keyPath: \.claudeSonnetAlias)
            knobRow(label: L("settings.phaseModels.knob.claudeHaikuAlias"), keyPath: \.claudeHaikuAlias)
            knobRow(label: L("settings.phaseModels.knob.claudeSubagent"), keyPath: \.claudeSubagentDefault)
        }
    }

    private var codexKnobDetails: some View {
        Group {
            knobRow(label: L("settings.phaseModels.knob.codexReview"), keyPath: \.codexReviewModel)
            knobRow(label: L("settings.phaseModels.knob.codexSubagent"), keyPath: \.codexSubagentDefault)
            effortRow
        }
    }

    private func knobRow(label: String, keyPath: WritableKeyPath<PhaseModelHardcodedConfig, String>) -> some View {
        HStack(spacing: 8) {
            Text(label).font(.system(size: 11)).foregroundStyle(.secondary)
            Spacer(minLength: 4)
            Picker("", selection: Binding(
                get: { phaseConfig[keyPath: keyPath] },
                set: { store.setPhaseModelKnob(keyPath, value: $0) }
            )) {
                Text(L("settings.phaseModels.defaultOption")).tag("default")
                ForEach(catalog.models.filter { $0.value != "default" }, id: \.value) { opt in
                    Text(opt.displayName).tag(opt.value)
                }
                if !registered.isEmpty {
                    Divider()
                    ForEach(registered, id: \.name) { e in Text(verbatim: e.name).tag(e.name) }
                }
            }
            .labelsHidden().pickerStyle(.menu).frame(maxWidth: 200)
        }
    }

    private var effortRow: some View {
        HStack(spacing: 8) {
            Text(L("settings.phaseModels.knob.codexPlanEffort")).font(.system(size: 11)).foregroundStyle(.secondary)
            Spacer(minLength: 4)
            Picker("", selection: Binding(
                get: { phaseConfig.codexPlanModeReasoningEffort },
                set: { store.setPhaseModelKnob(\.codexPlanModeReasoningEffort, value: $0) }
            )) {
                Text(L("settings.phaseModels.defaultOption")).tag("default")
                ForEach(["low", "medium", "high"], id: \.self) { level in
                    Text(level).tag(level)
                }
            }
            .labelsHidden().pickerStyle(.menu).frame(maxWidth: 200)
        }
    }

    // MARK: - Registered model management

    private func registeredRow(_ entry: RegisteredModelEntry) -> some View {
        HStack {
            VStack(alignment: .leading, spacing: 2) {
                Text(verbatim: entry.name).font(.system(size: 11, design: .monospaced))
                if entry.supportsEffort {
                    Text(L("settings.phaseModels.supportsEffortLabel"))
                        .font(.system(size: 10)).foregroundStyle(.secondary)
                }
            }
            Spacer(minLength: 0)
            Button(L("settings.phaseModels.deleteButton"), role: .destructive) {
                store.removeRegisteredModel(provider: provider, name: entry.name)
            }
            .controlSize(.small)
        }
        .accessibilityIdentifier("phaseModels-registered-\(provider)-\(entry.name)")
    }

    private var addRow: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: 6) {
                TextField(L("settings.phaseModels.addPlaceholder"), text: $addName)
                    .textFieldStyle(.roundedBorder)
                    .font(.system(size: 11, design: .monospaced))
                    .onSubmit { addModel() }
                    .accessibilityIdentifier("phaseModels-add-\(provider)")
                Button(L("settings.phaseModels.addButton")) { addModel() }
                    .controlSize(.small)
                    .accessibilityIdentifier("phaseModels-addButton-\(provider)")
            }
            Toggle(L("settings.phaseModels.supportsEffortLabel"), isOn: $addSupportsEffort)
                .font(.system(size: 11))
                .accessibilityIdentifier("phaseModels-addEffort-\(provider)")
                .onChange(of: addSupportsEffort) { _, on in if !on { addEffortLevels = [] } }
            if addSupportsEffort {
                VStack(alignment: .leading, spacing: 2) {
                    Text(L("settings.phaseModels.effortLevelsLabel"))
                        .font(.system(size: 10)).foregroundStyle(.secondary)
                    HStack(spacing: 4) {
                        ForEach(ProviderOptions.efforts, id: \.self) { level in
                            let selected = addEffortLevels.contains(level)
                            Button(level) {
                                if selected { addEffortLevels.remove(level) } else { addEffortLevels.insert(level) }
                            }
                            .controlSize(.mini).buttonStyle(.borderedProminent)
                            .opacity(selected ? 1.0 : 0.4)
                            .accessibilityIdentifier("phaseModels-addLevel-\(provider)-\(level)")
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
            addName = ""; addSupportsEffort = false; addEffortLevels = []; validationError = nil
        } catch {
            validationError = error.localizedDescription
        }
    }
}
