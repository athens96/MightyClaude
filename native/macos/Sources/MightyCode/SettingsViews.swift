import SwiftUI
import AppKit
import MightyCore

struct RunSettingsView: View {
    @EnvironmentObject private var store: AppStore
    @Environment(\.dismiss) private var dismiss
    let session: RunSession
    @ViewState private var maxTurns: String
    @ViewState private var maxBudget: String
    @ViewState private var fastMode: Bool
    @ViewState private var webSearch: String
    @ViewState private var networkAccess: Bool
    @ViewState private var validationError: String?

    init(session: RunSession) {
        self.session = session
        _maxTurns = State(initialValue: session.settings.maxTurns.map(String.init) ?? "")
        _maxBudget = State(initialValue: session.settings.maxBudgetUsd.map { String($0) } ?? "")
        _fastMode = State(initialValue: session.settings.fastMode)
        _webSearch = State(initialValue: session.settings.webSearch)
        _networkAccess = State(initialValue: session.settings.networkAccess)
    }

    private var runtime: ProviderRuntime { store.providerRuntime(session.provider, workspaceId: session.workspaceId) }
    private var selectedModel: ModelOption? { runtime.modelCatalog.models.first { $0.value == session.model } }
    private var currentSession: RunSession { store.snapshot.sessions.first { $0.id == session.id } ?? session }
    private var running: Bool { currentSession.status == "running" || store.pendingRuns.contains(session.id) }
    private var unsupportedSettings: Bool {
        (fastMode && !runtime.capabilities.fastMode) || (webSearch != "default" && !runtime.capabilities.webSearch) || (networkAccess && !runtime.capabilities.networkAccess)
    }

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Text(L("settings.run.title")).font(.ui(size: 14, weight: .semibold))
                Spacer()
                Text(ProviderOptions.label(session.provider)).font(.ui(size: 11)).foregroundStyle(.secondary)
                if ProviderOptions.isBeta(session.provider) { BetaBadge() }
            }.padding(.horizontal, DesignMetrics.Inset.popover).padding(.vertical, DesignMetrics.Spacing.md)
            Divider()
            ScrollView {
                VStack(alignment: .leading, spacing: DesignMetrics.Spacing.md) {
                    VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xs) {
                        Text(store.modelLabel(for: currentSession)).font(.ui(size: 13, weight: .semibold))
                        if let description = selectedModel?.description, !description.isEmpty { Text(description).font(.ui(size: 11)).foregroundStyle(.secondary).textSelection(.enabled) }
                        Label(runtime.modelCatalog.source == "cli" ? L("settings.run.modelSourceCli") : L("settings.run.modelSourceDefault"), systemImage: "info.circle")
                            .font(.ui(size: 10)).foregroundStyle(.secondary)
                    }
                    Divider()
                    VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xs) {
                        Text(permissionLabel(currentSession.settings.permissionMode, provider: session.provider)).font(.ui(size: 13, weight: .semibold))
                        Text(permissionDescription(currentSession.settings.permissionMode, provider: session.provider)).font(.ui(size: 11)).foregroundStyle(.secondary)
                        if let reference = store.snapshot.workspaces.first(where: { $0.id == session.workspaceId })?.remote {
                            Text(L("settings.remote.runOnHost", ["host": reference.hostName])).font(.ui(size: 11)).foregroundStyle(.secondary)
                        }
                        if currentSession.settings.permissionMode != "fullAccess" {
                            if ProviderOptions.asksInPane(provider: session.provider, mode: currentSession.settings.permissionMode) {
                                Text(L("settings.run.approvalInApp")).font(.ui(size: 11)).foregroundStyle(.secondary)
                            } else {
                                Text(L("settings.run.approvalUnsupported")).font(.ui(size: 11)).foregroundStyle(.secondary)
                            }
                        }
                    }
                    if runtime.capabilities.fastMode {
                        Text(L("settings.run.fastModeNote")).font(.ui(size: 11)).foregroundStyle(.secondary)
                    }
                    if runtime.capabilities.webSearch {
                        Divider()
                        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xs) {
                            Picker(L("settings.run.webSearchLabel"), selection: $webSearch) {
                                Text(L("settings.run.webSearchDefault")).tag("default")
                                Text(L("settings.run.webSearchOff")).tag("disabled")
                                Text(L("settings.run.webSearchCached")).tag("cached")
                                Text(L("settings.run.webSearchLive")).tag("live")
                            }.pickerStyle(.menu).accessibilityLabel(L("settings.run.webSearchLabel"))
                            Text(webSearchDescription(webSearch)).font(.ui(size: 11)).foregroundStyle(.secondary)
                        }
                    }
                    if runtime.capabilities.networkAccess {
                        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xs) {
                            Toggle(L("settings.run.shellNetworkToggle"), isOn: $networkAccess).toggleStyle(.switch)
                                .disabled(!["acceptEdits", "onRequest"].contains(currentSession.settings.permissionMode)).accessibilityLabel(L("settings.run.shellNetworkToggle"))
                            Text(currentSession.settings.permissionMode == "fullAccess" ? L("settings.run.shellNetworkFullAccess") : ["acceptEdits", "onRequest"].contains(currentSession.settings.permissionMode) ? L("settings.run.shellNetworkAcceptEdits") : L("settings.run.shellNetworkLocked"))
                                .font(.ui(size: 11)).foregroundStyle(.secondary)
                        }
                    }
                    if runtime.capabilities.maxTurns || runtime.capabilities.maxBudgetUsd {
                        Divider()
                        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
                            Text(L("settings.run.limitsTitle")).font(.ui(size: 13, weight: .semibold))
                            if runtime.capabilities.maxTurns {
                                VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
                                    Text(L("settings.run.maxTurnsLabel")).font(.ui(size: 11))
                                    TextField(L("settings.run.maxTurnsLabel"), text: $maxTurns, prompt: Text(L("settings.run.unlimitedPlaceholder"))).textFieldStyle(.roundedBorder).accessibilityLabel(L("settings.run.maxTurnsLabel"))
                                }
                            }
                            if runtime.capabilities.maxBudgetUsd {
                                VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
                                    Text(L("settings.run.maxBudgetLabel")).font(.ui(size: 11))
                                    TextField(L("settings.run.maxBudgetLabel"), text: $maxBudget, prompt: Text(L("settings.run.unlimitedPlaceholder"))).textFieldStyle(.roundedBorder).accessibilityLabel(L("settings.run.maxBudgetLabel"))
                                }
                            }
                            Text(L("settings.run.limitsNote")).font(.ui(size: 11)).foregroundStyle(.secondary)
                        }
                    }
                    if unsupportedSettings {
                        Divider()
                        Text(L("settings.run.unsupportedNotice")).font(.ui(size: 11)).foregroundStyle(.secondary)
                        Button(L("settings.run.unsupportedResetButton")) {
                            if !runtime.capabilities.fastMode { fastMode = false }
                            if !runtime.capabilities.webSearch { webSearch = "default" }
                            if !runtime.capabilities.networkAccess { networkAccess = false }
                        }
                    }
                }
                .font(.ui(size: 12)).fixedSize(horizontal: false, vertical: true).padding(DesignMetrics.Inset.popover).padding(.bottom, DesignMetrics.Spacing.md).disabled(running)
            }
            if let validationError { Text(validationError).font(.ui(size: 11)).foregroundStyle(Palette.errText).frame(maxWidth: .infinity, alignment: .leading).padding(.horizontal, DesignMetrics.Inset.popover).padding(.bottom, DesignMetrics.Spacing.md) }
            Divider()
            HStack {
                Text(L("settings.run.appliesNextRequest")).font(.ui(size: 10)).foregroundStyle(.secondary)
                Spacer()
                Button(L("settings.run.cancelButton")) { dismiss() }.keyboardShortcut(.cancelAction)
                Button(L("settings.run.applyButton")) { save() }.buttonStyle(.borderedProminent).keyboardShortcut(.defaultAction).disabled(running)
            }.padding(.horizontal, DesignMetrics.Inset.popover).padding(.vertical, DesignMetrics.Spacing.md)
        }.frame(width: DesignMetrics.Dialog.popoverWidthL, height: runtime.capabilities.webSearch ? 440 : 390)
    }

    private func save() {
        guard !running else { return }
        var settings = currentSession.settings
        settings.fastMode = fastMode
        settings.webSearch = webSearch
        settings.networkAccess = ["acceptEdits", "onRequest"].contains(currentSession.settings.permissionMode) && networkAccess
        let turns = maxTurns.trimmingCharacters(in: .whitespacesAndNewlines)
        let budget = maxBudget.trimmingCharacters(in: .whitespacesAndNewlines)
        if runtime.capabilities.maxTurns, !turns.isEmpty {
            guard let value = Int(turns), (1...1000).contains(value) else { validationError = L("settings.run.maxTurnsError"); return }
            settings.maxTurns = value
        } else { settings.maxTurns = nil }
        if runtime.capabilities.maxBudgetUsd, !budget.isEmpty {
            guard let value = Double(budget), value.isFinite, value > 0, value <= 10_000 else { validationError = L("settings.run.maxBudgetError"); return }
            settings.maxBudgetUsd = value
        } else { settings.maxBudgetUsd = nil }
        do {
            try CoreValidation.validateCapabilities(StartRunRequest(sessionId: session.id, workspaceId: session.workspaceId, input: "settings validation", provider: session.provider, settings: settings), capabilities: runtime.capabilities)
        } catch { validationError = error.localizedDescription; return }
        store.saveSettings(session.id, settings: settings)
        dismiss()
    }
}

/// One entry of the settings sidebar; the right side shows its sections.
enum SettingsPane: String, CaseIterable, Identifiable {
    case general, models, styles, tools, cli, mobile, remote, companion, about
    var id: String { rawValue }
    var title: String {
        switch self {
        case .general: L("settings.nav.general")
        case .models: L("settings.nav.models")
        case .styles: L("settings.nav.styles")
        case .tools: L("settings.nav.tools")
        case .cli: L("settings.nav.cli")
        case .mobile: L("settings.nav.mobile")
        case .remote: L("settings.remote.navTitle")
        case .companion: L("settings.nav.companion")
        case .about: L("settings.nav.about")
        }
    }
    var symbol: String {
        switch self {
        case .general: "gearshape"
        case .models: "cpu"
        case .styles: "paintpalette"
        case .tools: "shippingbox"
        case .cli: "terminal"
        case .mobile: "iphone"
        case .remote: "network"
        case .companion: "pawprint"
        case .about: "info.circle"
        }
    }
    /// The entry's icon tile in the One UI list, in one of the palette's own fills.
    var tone: Color {
        switch self {
        case .general: Palette.stop
        case .models: Palette.token(\.agent)
        case .styles: Palette.err
        case .tools: Palette.wait
        case .cli: Palette.token(\.ink3)
        case .mobile: Palette.done
        case .remote: Palette.token(\.task)
        case .companion: Palette.run
        case .about: Palette.accent
        }
    }
    /// The One UI list's cards: related entries share one rounded card.
    static let groups: [[SettingsPane]] = [[.general, .models, .styles], [.tools, .cli], [.mobile, .remote], [.companion, .about]]
}

struct AppSettingsView: View {
    @EnvironmentObject private var store: AppStore
    @Environment(\.dismiss) private var dismiss
    @AppStorage("settingsPane") private var pane: SettingsPane = .general

    /// Two One UI panes (Windows' web Settings draws the same): the entries grouped into rounded cards with a tile each,
    /// and the entry's page, each opening on a large title that gives way to a small one in its bar as it scrolls.
    /// A panel (kind C): the page's ✕ (28pt, Esc), resizable from its corner and opening at its last size.
    var body: some View {
        HStack(spacing: 0) {
            sidebar.frame(width: DesignMetrics.Settings.listWidth)
            Divider()
            SettingsPage(title: pane.title) {
                DialogCloseButton(close: DialogClose(identifier: "settings-close") { dismiss() })
            } content: { detail(pane) }
                // A fresh page per entry starts at its top. A theme change rebuilds it too: a menu picker
                // (NSPopUpButton) made under one appearance keeps part of it (its title or its bezel)
                // when the appearance flips under it. The price: changing the theme on its page scrolls that
                // page back to the top; the theme control saves at once, so nothing typed is lost with it.
                .id("\(pane.rawValue)-\(store.snapshot.theme)")
                .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
        .background(Palette.canvas)
        .dialogPanelFrame(id: "settings", ideal: CGSize(width: DesignMetrics.Settings.width, height: DesignMetrics.Settings.height),
                          minimum: CGSize(width: DesignMetrics.Settings.minWidth, height: DesignMetrics.Settings.minHeight))
        // Opening Settings is one of the four moments the sources are re-read.
        .task { store.rescanStyles() }
    }

    private var sidebar: some View {
        OneUIPane(title: L("settings.title"), subtitle: nil) {
            VStack(spacing: OneUIMetrics.cardGap) {
                ForEach(Array(SettingsPane.groups.enumerated()), id: \.offset) { _, group in
                    VStack(spacing: 0) {
                        ForEach(Array(group.enumerated()), id: \.element) { index, item in
                            navRow(item, divided: index > 0)
                        }
                    }
                    // No inset above or below: the selected row's band reaches the card's edge and is
                    // rounded by the card's clip.
                    .background(RoundedRectangle(cornerRadius: DesignMetrics.Settings.cardRadius, style: .continuous).fill(Palette.panel))
                    .clipShape(RoundedRectangle(cornerRadius: DesignMetrics.Settings.cardRadius, style: .continuous))
                }
            }
        }
        // ↑ and ↓ step through the entries in the cards' order, as the sidebar list's selection did.
        .focusable()
        .focusEffectDisabled()
        .onMoveCommand { direction in
            let order = SettingsPane.groups.flatMap { $0 }
            guard let index = order.firstIndex(of: pane) else { return }
            switch direction {
            case .up: pane = order[max(0, index - 1)]
            case .down: pane = order[min(order.count - 1, index + 1)]
            default: break
            }
        }
    }

    private func navRow(_ item: SettingsPane, divided: Bool) -> some View {
        let selected = item == pane
        return Button { pane = item } label: {
            HStack(spacing: DesignMetrics.Spacing.lg) {
                Image(systemName: item.symbol).font(.ui(size: DesignMetrics.Settings.navGlyph, weight: .medium)).foregroundStyle(.white)
                    .frame(width: DesignMetrics.Settings.navTile, height: DesignMetrics.Settings.navTile)
                    .background(RoundedRectangle(cornerRadius: DesignMetrics.Settings.navTileRadius, style: .continuous).fill(item.tone))
                    .accessibilityHidden(true)
                Text(item.title).font(.ui(size: DesignMetrics.Settings.navText)).lineLimit(1).foregroundStyle(selected ? Palette.accent : Palette.ink)
                Spacer(minLength: 0)
            }
            .padding(.horizontal, OneUIMetrics.rowInset).frame(minHeight: DesignMetrics.Settings.navRowHeight)
            .background(selected ? Palette.accentSoft : Color.clear)
            .overlay(alignment: .top) {
                if divided { Rectangle().fill(Palette.border).frame(height: 0.5).padding(.leading, OneUIMetrics.dividerLeading).padding(.trailing, OneUIMetrics.rowInset) }
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(selected ? .isSelected : [])
        .accessibilityIdentifier("settings-nav-\(item.rawValue)")
    }

    /// The text size and fonts of the whole app (`TextSettings`); View → ⌘= / ⌘− / ⌘0 step the same size.
    private var textGroup: some View {
        let text = store.textAppearance
        let families = TextSettings.families
        return SettingsGroup(L("settings.text.sectionTitle")) {
            SettingsRow(L("settings.text.sizeLabel")) {
                Picker(L("settings.text.sizeLabel"), selection: Binding(
                    get: { text.scale },
                    set: { store.setTextAppearance(store.textAppearance.with(scale: $0)) }
                )) {
                    ForEach(TextAppearance.scales, id: \.self) { scale in
                        let percent = "\(Int((scale * 100).rounded()))"
                        Text(scale == 1 ? L("settings.text.sizeStandard", ["percent": percent]) : "\(percent)%").tag(scale)
                    }
                }
                .settingsSelect()
                .accessibilityIdentifier("settings-text-size")
            }
            // The three fonts, read out as one "Font" group.
            VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xs) {
                fontPicker(L("settings.text.uiFontLabel"), standard: L("settings.text.fontSystem"), families: families, selected: text.family,
                           preview: .ui(size: 13), identifier: "settings-text-font") { store.textAppearance.with(family: $0) }
                fontPicker(L("settings.text.transcriptFontLabel"), standard: L("settings.text.sameAsInterface"), families: families, selected: text.transcriptFamily,
                           preview: .transcript(size: 13), identifier: "settings-text-transcript-font") { store.textAppearance.with(transcriptFamily: $0) }
                fontPicker(L("settings.text.codeFontLabel"), standard: L("settings.text.codeDefault", ["font": "SF Mono · \(TextAppearance.terminalStandardFamily)"]), families: TextSettings.codeFamilies, selected: text.codeFamily,
                           preview: .code(size: 12), identifier: "settings-text-code-font") { store.textAppearance.with(codeFamily: $0) }
            }
            .accessibilityElement(children: .contain)
            .accessibilityLabel(L("settings.text.fontLabel"))
            SettingsRow(L("settings.text.reset"), caption: L("settings.text.note")) {
                Button(L("settings.text.reset")) { store.setTextAppearance(.standard) }
                    .disabled(text.isStandard)
                    .accessibilityIdentifier("settings-text-reset")
            }
        }
    }

    /// One of the text group's font pickers, `standard` (nil) first, then `families`: a row whose
    /// caption is a one-line preview in the font it picks.
    private func fontPicker(_ label: String, standard: String, families: [String], selected: String?, preview: Font, identifier: String,
                            change: @escaping (String?) -> TextAppearance) -> some View {
        HStack(alignment: .center, spacing: DesignMetrics.Spacing.lg) {
            VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
                Text(label).font(.ui(size: DesignMetrics.Settings.rowName)).foregroundStyle(Palette.ink)
                Text(L("settings.text.preview"))
                    .font(preview).foregroundStyle(Palette.ink2).lineLimit(2).truncationMode(.tail)
                    .accessibilityIdentifier(identifier + "-preview")
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            Picker(label, selection: Binding(
                get: { selected ?? "" },
                set: { store.setTextAppearance(change($0.isEmpty ? nil : $0)) }
            )) {
                Text(standard).tag("")
                Divider()
                // A saved family that is no longer installed stays listed; it draws as the standard font.
                ForEach(families + (selected.map { families.contains($0) ? [] : [$0] } ?? []), id: \.self) { Text(verbatim: $0).tag($0) }
            }
            .settingsSelect()
            .accessibilityIdentifier(identifier)
        }
        .settingsRowFrame()
    }

    @ViewBuilder
    private func detail(_ pane: SettingsPane) -> some View {
        switch pane {
        case .general:
            SettingsGroup(L("settings.display.sectionTitle")) {
                SettingsRow(L("settings.display.themeLabel")) {
                    Picker(L("settings.display.themeLabel"), selection: $store.snapshot.theme) { Text(L("settings.display.themeDarkMac")).tag("dark"); Text(L("settings.display.themeLightMac")).tag("light") }
                        .pickerStyle(.segmented).labelsHidden().fixedSize()
                }
                SettingsRow(L("settings.display.languageLabel"), caption: L("settings.display.languageMenuNote")) {
                    Picker(L("settings.display.languageLabel"), selection: Binding(
                        get: { AppLanguage(rawValue: UserDefaults.standard.string(forKey: "language") ?? "system") ?? .system },
                        set: { UserDefaults.standard.set($0.rawValue, forKey: "language"); AppLanguage.applyToSystemInterface() }
                    )) {
                        ForEach(AppLanguage.pickerChoices, id: \.self) { Text(L($0.labelKey)).tag($0) }
                    }
                    .pickerStyle(.menu).settingsSelect().accessibilityIdentifier("settings-language")
                }
                Toggle(isOn: Binding(get: { store.statusLineEnabled }, set: { store.statusLineEnabled = $0 })) {
                    SettingsRowLabel(L("settings.display.statusLineToggle"), caption: L("settings.display.statusLineDescription"))
                }
                .accessibilityIdentifier("settings-status-line")
                // Agent delegation is on unless turned off here (or by its defaults key).
                Toggle(isOn: Binding(
                    get: { DelegationSwitch.isOn() },
                    set: { store.objectWillChange.send(); UserDefaults.standard.set($0, forKey: DelegationSwitch.defaultsKey) }
                )) {
                    SettingsRowLabel(L("settings.delegation.toggle"), caption: L("settings.delegation.description"))
                }
                .accessibilityIdentifier("settings-delegation")
                Toggle(isOn: Binding(
                    get: { CefBrowserEngine.isEnabledInSettings },
                    set: { store.objectWillChange.send(); CefBrowserEngine.isEnabledInSettings = $0 }
                )) {
                    SettingsRowLabel(L("settings.display.browserToggle"), caption: L("settings.display.browserDescription"))
                }
                .accessibilityIdentifier("settings-browser-engine")
            }
            textGroup
            // Per-workspace destination for URLs the agent opens. Writes to the
            // same persisted store the open agent panes read (and the dialog's
            // "remember" writes), so a change here is in force on their very
            // next open_url call and survives a restart.
            SettingsGroup(WebOpenChoiceCopy.settingTitle) {
                SettingsRow(WebOpenChoiceCopy.settingTitle) {
                    Picker(WebOpenChoiceCopy.settingTitle, selection: Binding(
                        get: { WebOpenChoiceStore.shared.setting(forWorkspace: store.snapshot.activeWorkspaceId ?? "") },
                        set: { choice in
                            store.objectWillChange.send()
                            WebOpenChoiceStore.shared.applySetting(choice, forWorkspace: store.snapshot.activeWorkspaceId ?? "")
                        }
                    )) {
                        ForEach(WebOpenSetting.allCases, id: \.self) { option in
                            Text(WebOpenChoiceCopy.settingLabel(option)).tag(option)
                        }
                    }
                    .pickerStyle(.segmented).labelsHidden().fixedSize()
                    .accessibilityIdentifier("settings-web-open-choice")
                }
            }
        case .models:
            PhaseModelSettingsSection().environmentObject(store)
        case .styles:
            StyleSettingsSection().environmentObject(store)
        case .tools:
            ComponentsSettingsSection().environmentObject(store)
            ToolkitSettingsSection().environmentObject(store)
        case .cli:
            SettingsGroup {
                ForEach(ProviderOptions.ids, id: \.self) { id in
                    let provider = store.runtime?.providers?.first { $0.id == id } ?? ProviderOptions.fallbackRuntime(id)
                    HStack(alignment: .center, spacing: DesignMetrics.Spacing.lg) {
                        ProviderIcon(provider: id, size: 14)
                        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
                            HStack(spacing: DesignMetrics.Spacing.sm) {
                                Text(provider.name).font(.ui(size: DesignMetrics.Settings.rowName))
                                if ProviderOptions.isBeta(id) { BetaBadge() }
                                if let version = provider.version { Text(version).font(.ui(size: 11, design: .monospaced)).foregroundStyle(.secondary) }
                            }
                            Text(provider.detail).font(.ui(size: DesignMetrics.Settings.rowCaption)).foregroundStyle(Palette.ink2)
                                .fixedSize(horizontal: false, vertical: true).textSelection(.enabled)
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                        Text(provider.available ? L("settings.providers.statusReady") : L("settings.providers.statusNeedsSetup"))
                            .font(.ui(size: DesignMetrics.Settings.rowCaption)).foregroundStyle(provider.available ? Palette.doneText : Palette.waitText)
                    }
                    .settingsRowFrame()
                }
                SettingsRow(L("settings.providers.checkButton"), caption: L("settings.providers.loginNote")) {
                    Button(store.isRefreshingRuntime ? L("settings.providers.checkingButton") : L("settings.providers.checkButton")) { Task { await store.refreshRuntime() } }.disabled(store.isRefreshingRuntime)
                }
            } header: { Text(L("settings.providers.sectionTitleMac")) }
            CLIAccountsSettingsSection()
            if let mods = store.runtime?.mods {
                SettingsGroup("Claude Mods") {
                    SettingsRow(L("settings.claudeMods.compatLabel"), caption: mods.detail) {
                        Text(mods.minimumVersion).foregroundStyle(.secondary).textSelection(.enabled)
                    }
                }
            }
            CLIUpdateSettingsSection().environmentObject(store)
        case .mobile:
            MobileRemoteSettingsSection().environmentObject(store)
            ScreenShareSettingsSection().environmentObject(store)
        case .remote:
            RemoteSettingsSection().environmentObject(store)
        case .companion:
            CompanionSettingsSection(companion: store.companion)
        case .about:
            AppUpdateSettingsSection()
            SettingsGroup(L("settings.appInfo.sectionTitle")) {
                SettingsRow(L("settings.appInfo.versionLabel"), caption: L("settings.appInfo.runtimeNote")) {
                    Text(store.runtime?.appVersion ?? "0.2.0").foregroundStyle(.secondary)
                }
                LabeledContent(L("menu.help")) {
                    Button(L("settings.appInfo.openHelpButton")) { NSWorkspace.shared.open(HelpSite.current) }
                }
                SettingsRow(L("settings.appInfo.stateLocationLabel"), caption: store.dataDirectory.path) {
                    Button(L("settings.appInfo.openFinderButton")) { NSWorkspace.shared.selectFile(nil, inFileViewerRootedAtPath: store.dataDirectory.path) }
                }
            }
        }
    }
}

func permissionLabel(_ mode: String, provider: String = "claude") -> String {
    if provider == "claude" {
        switch mode { case "plan": return "Plan mode"; case "acceptEdits": return "Accept file edits"; case "auto": return "Auto mode"; case "fullAccess": return "Bypass"; default: return "Always ask" }
    }
    switch mode { case "plan": return L("permission.label.plan"); case "acceptEdits": return provider == "codex" ? L("permission.label.acceptEditsCodex") : L("permission.label.acceptEdits"); case "onRequest": return L("permission.label.onRequest"); case "fullAccess": return L("permission.label.fullAccess"); default: return provider == "codex" || provider == "pi" ? L("permission.label.defaultCodex") : L("permission.label.default") }
}

func permissionDescription(_ mode: String, provider: String) -> String {
    if provider == "claude" {
        switch mode {
        case "plan": return L("permission.claude.plan")
        case "acceptEdits": return L("permission.claude.acceptEdits")
        case "auto": return L("permission.claude.auto")
        case "fullAccess": return L("permission.claude.fullAccess")
        default: return L("permission.claude.default")
        }
    }
    // pi has no approval window: read-only tools, or every tool without asking.
    if provider == "pi" { return mode == "fullAccess" ? L("permission.pi.fullAccess") : L("permission.pi.default") }
    // opencode's own rules for the run; what they leave at "ask" comes to the pane as a card.
    if provider == "opencode" {
        switch mode {
        case "plan": return L("permission.opencode.plan")
        case "acceptEdits": return L("permission.opencode.acceptEdits")
        case "fullAccess": return L("permission.opencode.fullAccess")
        default: return L("permission.opencode.default")
        }
    }
    // Hermes's own approvals: edits by the session's edit mode, dangerous commands by its approvals setting.
    if provider == "hermes" {
        switch mode {
        case "acceptEdits": return L("permission.hermes.acceptEdits")
        case "fullAccess": return L("permission.hermes.fullAccess")
        default: return L("permission.hermes.default")
        }
    }
    if mode == "fullAccess" { return L("permission.other.fullAccess") }
    if provider == "codex", mode == "onRequest" { return L("permission.codex.onRequest") }
    if provider == "codex" { return mode == "acceptEdits" ? L("permission.codex.acceptEdits") : L("permission.codex.default") }
    // Headless Antigravity declines what it would ask about instead of asking.
    if provider == "gemini", mode == "manual" { return L("permission.antigravity.default") }
    if provider == "gemini", mode == "acceptEdits" { return L("permission.antigravity.acceptEdits") }
    switch mode {
    case "plan": return L("permission.other.plan")
    case "acceptEdits": return L("permission.other.acceptEdits")
    default: return L("permission.other.default")
    }
}

private func webSearchDescription(_ value: String) -> String {
    switch value {
    case "disabled": return L("settings.run.webSearchDescriptionDisabled")
    case "cached": return L("settings.run.webSearchDescriptionCached")
    case "live": return L("settings.run.webSearchDescriptionLive")
    default: return L("settings.run.webSearchDescriptionDefault")
    }
}
