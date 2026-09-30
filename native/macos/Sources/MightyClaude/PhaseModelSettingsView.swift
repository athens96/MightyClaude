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
            Text(L("settings.phaseModels.effortNote"))
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
            HStack(spacing: 6) {
                Text(ProviderOptions.label(provider)).font(.system(size: 12, weight: .medium))
                if ProviderOptions.isBeta(provider) { BetaBadge() }
            }
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
            phaseRow(.planning, model: ModelCell(selection: claudeRowValue(.planning)) { store.applyClaudePhaseRow(.planning, value: $0) }, effort: nil)
            phaseRow(.execution, model: ModelCell(selection: claudeRowValue(.execution)) { store.applyClaudePhaseRow(.execution, value: $0) },
                     effort: EffortCell(selection: phaseConfig.claudeMainEffort ?? "default") { store.setPhaseEffort(\.claudeMainEffort, value: $0) })
            phaseRow(.subagents, model: ModelCell(selection: claudeRowValue(.subagents)) { store.applyClaudePhaseRow(.subagents, value: $0) }, effort: nil)
        }
    }

    private func claudeRowValue(_ phase: PhaseModelRouting.Phase) -> String {
        let c = store.currentPhaseModelConfig()
        switch PhaseModelRouting.claudeRowState(phase: phase, config: c) {
        case .uniform(let v): return v
        case .mixed: return mixedSentinel
        case nil: return "default"
        }
    }

    // MARK: - Codex phase rows

    /// Codex's main model is chosen per pane, so planning and execution have
    /// an effort here and no model.
    private var codexPhaseRows: some View {
        Group {
            phaseRow(.planning, model: nil,
                     effort: EffortCell(selection: phaseConfig.codexPlanModeReasoningEffort) { store.setPhaseModelKnob(\.codexPlanModeReasoningEffort, value: $0) })
            phaseRow(.execution, model: nil,
                     effort: EffortCell(selection: phaseConfig.codexMainEffort ?? "default") { store.setPhaseEffort(\.codexMainEffort, value: $0) })
            phaseRow(.review, model: ModelCell(selection: codexRowValue(.review)) { store.applyCodexPhaseRow(.review, value: $0) }, effort: nil)
            phaseRow(.subagents, model: ModelCell(selection: codexRowValue(.subagents)) { store.applyCodexPhaseRow(.subagents, value: $0) },
                     effort: EffortCell(selection: phaseConfig.codexSubagentEffort ?? "default") { store.setPhaseEffort(\.codexSubagentEffort, value: $0) })
        }
    }

    private func codexRowValue(_ phase: PhaseModelRouting.Phase) -> String {
        let c = store.currentPhaseModelConfig()
        switch PhaseModelRouting.codexRowState(phase: phase, config: c) {
        case .uniform(let v): return v
        case .mixed: return mixedSentinel
        case nil: return "default"
        }
    }

    // MARK: - Phase row: version and effort

    private struct ModelCell { let selection: String; let onPick: (String) -> Void }
    private struct EffortCell { let selection: String; let onPick: (String) -> Void }

    private func phaseTitle(_ phase: PhaseModelRouting.Phase) -> String {
        switch phase {
        case .planning: L("settings.phaseModels.phase.planning")
        case .execution: L("settings.phaseModels.phase.execution")
        case .review: L("settings.phaseModels.phase.review")
        case .subagents: L("settings.phaseModels.phase.subagents")
        }
    }

    /// A phase with no knob for one of the two says so instead of offering a
    /// choice the CLI would ignore.
    private func phaseRow(_ phase: PhaseModelRouting.Phase, model: ModelCell?, effort: EffortCell?) -> some View {
        HStack(spacing: 8) {
            Text(phaseTitle(phase)).font(.system(size: 11))
            Spacer(minLength: 4)
            if let model {
                Picker("", selection: Binding(get: { model.selection }, set: { if $0 != mixedSentinel { model.onPick($0) } })) {
                    if model.selection == mixedSentinel { Text(L("settings.phaseModels.mixed")).tag(mixedSentinel) }
                    modelOptions(current: model.selection)
                }
                .labelsHidden().pickerStyle(.menu).frame(maxWidth: 220)
                .accessibilityIdentifier("phaseModels-model-\(provider)-\(phase.rawValue)")
            } else {
                Text(L("settings.phaseModels.paneModel")).font(.system(size: 11)).foregroundStyle(.secondary).frame(maxWidth: 220, alignment: .trailing)
            }
            if let effort {
                Picker("", selection: Binding(get: { effort.selection }, set: { effort.onPick($0) })) {
                    Text(L("settings.phaseModels.defaultOption")).tag("default")
                    ForEach(effortLevels(including: effort.selection), id: \.self) { Text(verbatim: $0).tag($0) }
                }
                .labelsHidden().pickerStyle(.menu).frame(width: 110)
                .accessibilityIdentifier("phaseModels-effort-\(provider)-\(phase.rawValue)")
            } else {
                Text(L("settings.phaseModels.effortUnsupported")).font(.system(size: 10)).foregroundStyle(.tertiary).frame(width: 110, alignment: .leading)
            }
        }
    }

    private struct VersionEntry: Hashable { let value: String; let label: String }

    /// An alias follows the newest model ("· 최신"); the version the CLI says it
    /// stands for today is listed beside it to pin that one instead. A saved
    /// version the CLI no longer lists stays selectable.
    private func versionEntries(current: String) -> [VersionEntry] {
        var seen = Set(["default", mixedSentinel] + registered.map(\.name))
        var entries: [VersionEntry] = []
        for option in catalog.models where option.value != "default" {
            let resolved = option.resolvedModel.flatMap { $0.isEmpty || $0 == option.value ? nil : $0 }
            if seen.insert(option.value).inserted {
                entries.append(VersionEntry(value: option.value, label: resolved == nil ? option.displayName : L("settings.phaseModels.latestTemplate", ["name": option.displayName])))
            }
            if let resolved, seen.insert(resolved).inserted { entries.append(VersionEntry(value: resolved, label: resolved)) }
        }
        if !seen.contains(current) { entries.append(VersionEntry(value: current, label: current)) }
        return entries
    }

    @ViewBuilder
    private func modelOptions(current: String) -> some View {
        Text(L("settings.phaseModels.defaultOption")).tag("default")
        ForEach(versionEntries(current: current), id: \.self) { entry in Text(verbatim: entry.label).tag(entry.value) }
        if !registered.isEmpty {
            Divider()
            ForEach(registered, id: \.name) { e in Text(verbatim: e.name).tag(e.name) }
        }
    }

    /// The levels the CLI names; for Codex the ones its models advertise.
    private func effortLevels(including current: String) -> [String] {
        var levels = ProviderOptions.efforts
        if provider == "codex" {
            let advertised = Set(catalog.models.flatMap { $0.supportedEffortLevels ?? [] })
            levels = advertised.isEmpty ? ["low", "medium", "high"] : ProviderOptions.efforts.filter(advertised.contains)
        }
        if current != "default", !levels.contains(current) { levels.append(current) }
        return levels
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
                modelOptions(current: phaseConfig[keyPath: keyPath])
            }
            .labelsHidden().pickerStyle(.menu).frame(maxWidth: 220)
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
