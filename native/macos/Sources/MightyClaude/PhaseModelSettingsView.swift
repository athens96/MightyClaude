import SwiftUI
import MightyCore

private let mixedSentinel = "__mixed__"

struct PhaseModelSettingsSection: View {
    @EnvironmentObject private var store: AppStore

    /// One card per agent, so the line between the two comes from the cards, not a divider.
    /// The two intro paragraphs start where the cards' titles and rows do (the card's side inset).
    var body: some View {
        Text(L("settings.phaseModels.description"))
            .font(.ui(size: DesignMetrics.Settings.rowCaption)).foregroundStyle(Palette.ink2)
            .fixedSize(horizontal: false, vertical: true)
            .padding(.horizontal, DesignMetrics.Settings.cardPaddingX)
        Text(L("settings.phaseModels.effortNote"))
            .font(.ui(size: DesignMetrics.Settings.rowCaption)).foregroundStyle(Palette.ink2)
            .fixedSize(horizontal: false, vertical: true)
            .padding(.horizontal, DesignMetrics.Settings.cardPaddingX)
        ForEach(["claude", "codex"], id: \.self) { provider in
            SettingsGroup {
                PhaseModelProviderBlock(provider: provider).environmentObject(store)
            } header: {
                HStack(spacing: DesignMetrics.Spacing.sm) {
                    Text(L("settings.phaseModels.sectionTitle") + " · " + ProviderOptions.label(provider))
                    if ProviderOptions.isBeta(provider) { BetaBadge() }
                }
            }
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

    /// The rows of one agent's card: its phases (a model and an effort side by side), the CLI's
    /// knobs, the models registered by hand, and the row that adds one. Each is a `SettingsRow`, so
    /// the lines between them are the card's.
    var body: some View {
        if provider == "claude" { claudePhaseRows } else { codexPhaseRows }
        if provider == "claude" { claudeKnobDetails } else { codexKnobDetails }
        ForEach(registered, id: \.name) { entry in
            registeredRow(entry)
        }
        addRow
        if let err = validationError {
            Text(err).font(.ui(size: 12)).foregroundStyle(Palette.errText)
                .fixedSize(horizontal: false, vertical: true)
                .accessibilityIdentifier("phaseModels-error-\(provider)")
        }
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
    /// Two 160pt selects on one row (decision 6): the model, then the effort.
    private func phaseRow(_ phase: PhaseModelRouting.Phase, model: ModelCell?, effort: EffortCell?) -> some View {
        SettingsRow(phaseTitle(phase)) {
            HStack(spacing: DesignMetrics.Spacing.sm) {
                if let model {
                    Picker("", selection: Binding(get: { model.selection }, set: { if $0 != mixedSentinel { model.onPick($0) } })) {
                        if model.selection == mixedSentinel { Text(L("settings.phaseModels.mixed")).tag(mixedSentinel) }
                        modelOptions(current: model.selection)
                    }
                    .labelsHidden().pickerStyle(.menu).frame(width: DesignMetrics.Settings.pairSelectWidth)
                    .accessibilityIdentifier("phaseModels-model-\(provider)-\(phase.rawValue)")
                } else {
                    Text(L("settings.phaseModels.paneModel")).font(.ui(size: 12)).foregroundStyle(.secondary)
                        .frame(width: DesignMetrics.Settings.pairSelectWidth, alignment: .trailing)
                }
                if let effort {
                    Picker("", selection: Binding(get: { effort.selection }, set: { effort.onPick($0) })) {
                        Text(L("settings.phaseModels.defaultOption")).tag("default")
                        ForEach(effortLevels(including: effort.selection), id: \.self) { Text(verbatim: $0).tag($0) }
                    }
                    .labelsHidden().pickerStyle(.menu).frame(width: DesignMetrics.Settings.pairSelectWidth)
                    .accessibilityIdentifier("phaseModels-effort-\(provider)-\(phase.rawValue)")
                } else {
                    // Ends at the row's trailing edge like a select would, so every row shares one right edge.
                    Text(L("settings.phaseModels.effortUnsupported")).font(.ui(size: 12)).foregroundStyle(.secondary)
                        .frame(width: DesignMetrics.Settings.pairSelectWidth, alignment: .trailing)
                }
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
                let name = ModelLabel.option(option)
                entries.append(VersionEntry(value: option.value, label: resolved == nil ? name : L("settings.phaseModels.latestTemplate", ["name": name])))
            }
            if let resolved, seen.insert(resolved).inserted { entries.append(VersionEntry(value: resolved, label: ModelLabel.text(resolved))) }
        }
        if !seen.contains(current) { entries.append(VersionEntry(value: current, label: ModelLabel.text(current))) }
        return entries
    }

    @ViewBuilder
    private func modelOptions(current: String) -> some View {
        Text(L("settings.phaseModels.defaultOption")).tag("default")
        ForEach(versionEntries(current: current), id: \.self) { entry in Text(verbatim: entry.label).tag(entry.value) }
        if !registered.isEmpty {
            Divider()
            ForEach(registered, id: \.name) { e in Text(verbatim: ModelLabel.text(e.name)).tag(e.name) }
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
        SettingsRow(label) {
            Picker("", selection: Binding(
                get: { phaseConfig[keyPath: keyPath] },
                set: { store.setPhaseModelKnob(keyPath, value: $0) }
            )) {
                modelOptions(current: phaseConfig[keyPath: keyPath])
            }
            .pickerStyle(.menu).settingsSelect()
        }
    }

    // MARK: - Registered model management

    private func registeredRow(_ entry: RegisteredModelEntry) -> some View {
        SettingsRow(entry.name, caption: entry.supportsEffort ? L("settings.phaseModels.supportsEffortLabel") : nil) {
            Button(L("settings.phaseModels.deleteButton"), role: .destructive) {
                store.removeRegisteredModel(provider: provider, name: entry.name)
            }
        }
        .accessibilityIdentifier("phaseModels-registered-\(provider)-\(entry.name)")
    }

    /// A wide row: the model ID across the row with 추가 beside it, then whether it takes an effort and which.
    @ViewBuilder private var addRow: some View {
        SettingsRow(L("settings.phaseModels.registeredTitle"), wide: true) {
            HStack(spacing: DesignMetrics.Spacing.sm) {
                TextField(L("settings.phaseModels.addPlaceholder"), text: $addName)
                    .textFieldStyle(.roundedBorder)
                    .font(.ui(size: 12, design: .monospaced))
                    .onSubmit { addModel() }
                    .accessibilityIdentifier("phaseModels-add-\(provider)")
                Button(L("settings.phaseModels.addButton")) { addModel() }
                    .accessibilityIdentifier("phaseModels-addButton-\(provider)")
            }
        }
        Toggle(L("settings.phaseModels.supportsEffortLabel"), isOn: $addSupportsEffort)
            .accessibilityIdentifier("phaseModels-addEffort-\(provider)")
            .onChange(of: addSupportsEffort) { _, on in if !on { addEffortLevels = [] } }
        if addSupportsEffort {
            SettingsRow(L("settings.phaseModels.effortLevelsLabel"), wide: true) {
                HStack(spacing: DesignMetrics.Spacing.xs) {
                    ForEach(ProviderOptions.efforts, id: \.self) { level in
                        let selected = addEffortLevels.contains(level)
                        Button(level) {
                            if selected { addEffortLevels.remove(level) } else { addEffortLevels.insert(level) }
                        }
                        .buttonStyle(selected ? AnyPrimitiveButtonStyle(.borderedProminent) : AnyPrimitiveButtonStyle(.bordered))
                        .accessibilityAddTraits(selected ? .isSelected : [])
                        .accessibilityIdentifier("phaseModels-addLevel-\(provider)-\(level)")
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
