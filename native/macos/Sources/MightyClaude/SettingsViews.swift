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
                Text(L("settings.run.title")).font(.system(size: 14, weight: .semibold))
                Spacer()
                Text(ProviderOptions.label(session.provider)).font(.system(size: 11)).foregroundStyle(.secondary)
            }.padding(16)
            Divider()
            ScrollView {
                VStack(alignment: .leading, spacing: 15) {
                    VStack(alignment: .leading, spacing: 6) {
                        Text(selectedModel?.displayName ?? session.model).font(.system(size: 13, weight: .medium))
                        if let description = selectedModel?.description, !description.isEmpty { Text(description).font(.system(size: 11)).foregroundStyle(.secondary).textSelection(.enabled) }
                        Label(runtime.modelCatalog.source == "cli" ? L("settings.run.modelSourceCli") : L("settings.run.modelSourceDefault"), systemImage: "info.circle")
                            .font(.system(size: 10)).foregroundStyle(.secondary)
                    }
                    Divider()
                    VStack(alignment: .leading, spacing: 6) {
                        Text(permissionLabel(currentSession.settings.permissionMode, provider: session.provider)).font(.system(size: 12, weight: .medium))
                        Text(permissionDescription(currentSession.settings.permissionMode, provider: session.provider)).font(.system(size: 11)).foregroundStyle(.secondary)
                        if let reference = store.snapshot.workspaces.first(where: { $0.id == session.workspaceId })?.remote {
                            Text(L("settings.run.remoteAccountTemplate", ["host": reference.hostName])).font(.system(size: 11)).foregroundStyle(.secondary)
                        }
                        if currentSession.settings.permissionMode != "fullAccess" {
                            if (session.provider == "claude" || (session.provider == "codex" && currentSession.settings.permissionMode == "onRequest")), store.snapshot.workspaces.first(where: { $0.id == session.workspaceId })?.remote == nil {
                                Text(L("settings.run.approvalInApp")).font(.system(size: 11)).foregroundStyle(.secondary)
                            } else {
                                Text(L("settings.run.approvalUnsupported")).font(.system(size: 11)).foregroundStyle(.secondary)
                            }
                        }
                    }
                    if runtime.capabilities.fastMode {
                        Text(L("settings.run.fastModeNote")).font(.system(size: 11)).foregroundStyle(.secondary)
                    }
                    if runtime.capabilities.webSearch {
                        Divider()
                        VStack(alignment: .leading, spacing: 7) {
                            Picker(L("settings.run.webSearchLabel"), selection: $webSearch) {
                                Text(L("settings.run.webSearchDefault")).tag("default")
                                Text(L("settings.run.webSearchOff")).tag("disabled")
                                Text(L("settings.run.webSearchCached")).tag("cached")
                                Text(L("settings.run.webSearchLive")).tag("live")
                            }.pickerStyle(.menu).accessibilityLabel(L("settings.run.webSearchLabel"))
                            Text(webSearchDescription(webSearch)).font(.system(size: 11)).foregroundStyle(.secondary)
                        }
                    }
                    if runtime.capabilities.networkAccess {
                        VStack(alignment: .leading, spacing: 7) {
                            Toggle(L("settings.run.shellNetworkToggle"), isOn: $networkAccess).toggleStyle(.switch).controlSize(.small)
                                .disabled(!["acceptEdits", "onRequest"].contains(currentSession.settings.permissionMode)).accessibilityLabel(L("settings.run.shellNetworkToggle"))
                            Text(currentSession.settings.permissionMode == "fullAccess" ? L("settings.run.shellNetworkFullAccess") : ["acceptEdits", "onRequest"].contains(currentSession.settings.permissionMode) ? L("settings.run.shellNetworkAcceptEdits") : L("settings.run.shellNetworkLocked"))
                                .font(.system(size: 11)).foregroundStyle(.secondary)
                        }
                    }
                    if runtime.capabilities.maxTurns || runtime.capabilities.maxBudgetUsd {
                        Divider()
                        VStack(alignment: .leading, spacing: 9) {
                            Text(L("settings.run.limitsTitle")).font(.system(size: 12, weight: .medium))
                            if runtime.capabilities.maxTurns {
                                VStack(alignment: .leading, spacing: 5) {
                                    Text(L("settings.run.maxTurnsLabel")).font(.system(size: 11))
                                    TextField(L("settings.run.maxTurnsLabel"), text: $maxTurns, prompt: Text(L("settings.run.unlimitedPlaceholder"))).textFieldStyle(.roundedBorder).accessibilityLabel(L("settings.run.maxTurnsLabel"))
                                }
                            }
                            if runtime.capabilities.maxBudgetUsd {
                                VStack(alignment: .leading, spacing: 5) {
                                    Text(L("settings.run.maxBudgetLabel")).font(.system(size: 11))
                                    TextField(L("settings.run.maxBudgetLabel"), text: $maxBudget, prompt: Text(L("settings.run.unlimitedPlaceholder"))).textFieldStyle(.roundedBorder).accessibilityLabel(L("settings.run.maxBudgetLabel"))
                                }
                            }
                            Text(L("settings.run.limitsNote")).font(.system(size: 11)).foregroundStyle(.secondary)
                        }
                    }
                    if unsupportedSettings {
                        Divider()
                        Text(L("settings.run.unsupportedNotice")).font(.system(size: 11)).foregroundStyle(.secondary)
                        Button(L("settings.run.unsupportedResetButton")) {
                            if !runtime.capabilities.fastMode { fastMode = false }
                            if !runtime.capabilities.webSearch { webSearch = "default" }
                            if !runtime.capabilities.networkAccess { networkAccess = false }
                        }
                    }
                }
                .font(.system(size: 12)).fixedSize(horizontal: false, vertical: true).padding(16).disabled(running)
            }
            if let validationError { Text(validationError).font(.system(size: 11)).foregroundStyle(.red).frame(maxWidth: .infinity, alignment: .leading).padding(.horizontal, 16).padding(.bottom, 10) }
            Divider()
            HStack {
                Text(L("settings.run.appliesNextRequest")).font(.system(size: 10)).foregroundStyle(.secondary)
                Spacer()
                Button(L("settings.run.cancelButton")) { dismiss() }.keyboardShortcut(.cancelAction)
                Button(L("settings.run.applyButton")) { save() }.buttonStyle(.borderedProminent).keyboardShortcut(.defaultAction).disabled(running)
            }.controlSize(.small).padding(14)
        }.frame(width: 360, height: runtime.capabilities.webSearch ? 500 : 445)
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

struct AppSettingsView: View {
    @EnvironmentObject private var store: AppStore
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        Group {
            if store.settingsShowsRemote {
                RemoteConnectionView(onClose: { store.settingsShowsRemote = false })
            } else { generalSettings }
        }
    }

    private var generalSettings: some View {
        VStack(spacing: 0) {
            SheetHeading(title: L("settings.title"), subtitle: "macOS · SwiftUI", systemImage: "gearshape") { dismiss() }
            Form {
                Section(L("settings.display.sectionTitle")) {
                    Picker(L("settings.display.themeLabel"), selection: $store.snapshot.theme) { Text(L("settings.display.themeDarkMac")).tag("dark"); Text(L("settings.display.themeLightMac")).tag("light") }.pickerStyle(.segmented)
                    Picker(L("settings.display.languageLabel"), selection: Binding(
                        get: { AppLanguage(rawValue: UserDefaults.standard.string(forKey: "language") ?? "system") ?? .system },
                        set: { UserDefaults.standard.set($0.rawValue, forKey: "language") }
                    )) {
                        Text(L("settings.display.languageSystem")).tag(AppLanguage.system)
                        Text(L("settings.display.languageKorean")).tag(AppLanguage.ko)
                        Text(L("settings.display.languageEnglish")).tag(AppLanguage.en)
                    }.pickerStyle(.segmented).accessibilityIdentifier("settings-language")
                    Toggle(isOn: Binding(get: { store.statusLineEnabled }, set: { store.statusLineEnabled = $0 })) {
                        VStack(alignment: .leading, spacing: 2) {
                            Text(L("settings.display.statusLineToggle"))
                            Text(L("settings.display.statusLineDescription"))
                                .font(.system(size: 11)).foregroundStyle(.secondary)
                        }
                    }
                    .toggleStyle(.switch).accessibilityIdentifier("settings-status-line")
                    Toggle(isOn: Binding(
                        get: { CefBrowserEngine.isEnabledInSettings },
                        set: { store.objectWillChange.send(); CefBrowserEngine.isEnabledInSettings = $0 }
                    )) {
                        VStack(alignment: .leading, spacing: 2) {
                            Text(L("settings.display.browserToggle"))
                            Text(L("settings.display.browserDescription"))
                                .font(.system(size: 11)).foregroundStyle(.secondary)
                        }
                    }
                    .toggleStyle(.switch).accessibilityIdentifier("settings-browser-engine")
                }
                Section(L("settings.remote.sectionTitle")) {
                    Button { store.settingsShowsRemote = true } label: {
                        Label(L("settings.remote.tailscaleButton"), systemImage: "network")
                    }
                    .accessibilityIdentifier("settings-remote")
                    Text(L("settings.remote.description"))
                        .font(.system(size: 11)).foregroundStyle(.secondary)
                }
                PhaseModelSettingsSection().environmentObject(store)
                StyleSettingsSection().environmentObject(store)
                ComponentsSettingsSection().environmentObject(store)
                MobileRemoteSettingsSection().environmentObject(store)
                CLIUpdateSettingsSection().environmentObject(store)
                CompanionSettingsSection(companion: store.companion)
                Section {
                    ForEach(ProviderOptions.ids, id: \.self) { id in
                        let provider = store.runtime?.providers?.first { $0.id == id } ?? ProviderOptions.fallbackRuntime(id)
                        VStack(alignment: .leading, spacing: 6) {
                            HStack {
                                HStack(spacing: 6) { ProviderIcon(provider: id, size: 13); Text(provider.name) }.font(.system(size: 13, weight: .medium))
                                Spacer()
                                Text(provider.available ? L("settings.providers.statusReady") : L("settings.providers.statusNeedsSetup")).font(.system(size: 10)).foregroundStyle(provider.available ? .green : .orange)
                            }
                            if let version = provider.version { Text(version).font(.system(size: 10, design: .monospaced)).foregroundStyle(.secondary) }
                            Text(provider.detail).font(.system(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true).textSelection(.enabled)
                        }.padding(.vertical, 4)
                    }
                    HStack {
                        Text(L("settings.providers.loginNote")).font(.system(size: 11)).foregroundStyle(.secondary)
                        Spacer()
                        Button(store.isRefreshingRuntime ? L("settings.providers.checkingButton") : L("settings.providers.checkButton")) { Task { await store.refreshRuntime() } }.disabled(store.isRefreshingRuntime)
                    }
                } header: { Text(L("settings.providers.sectionTitleMac")) }
                CLIAccountsSettingsSection()
                if let mods = store.runtime?.mods {
                    Section("Claude Mods") {
                        Text(mods.detail).font(.system(size: 12)).foregroundStyle(.secondary).textSelection(.enabled)
                        LabeledContent(L("settings.claudeMods.compatLabel"), value: mods.minimumVersion)
                    }
                }
                AppUpdateSettingsSection()
                Section(L("settings.appInfo.sectionTitle")) {
                    LabeledContent(L("settings.appInfo.versionLabel"), value: store.runtime?.appVersion ?? "0.1.0")
                    Text(L("settings.appInfo.runtimeNote")).font(.system(size: 11)).foregroundStyle(.secondary)
                    LabeledContent(L("settings.appInfo.stateLocationLabel")) {
                        Button(L("settings.appInfo.openFinderButton")) { NSWorkspace.shared.selectFile(nil, inFileViewerRootedAtPath: store.dataDirectory.path) }
                    }
                    Text(store.dataDirectory.path).font(.system(size: 10, design: .monospaced)).foregroundStyle(.secondary).textSelection(.enabled)
                }
            }.formStyle(.grouped)
            Divider()
            HStack { Spacer(); Button(L("settings.closeButton")) { dismiss() }.keyboardShortcut(.cancelAction) }.padding(18)
        }
        .frame(width: 570, height: 700)
        // Opening Settings is one of the four moments the sources are re-read.
        .task { store.rescanStyles() }
    }
}

struct SheetHeading: View {
    let title: String
    let subtitle: String
    let systemImage: String
    let dismiss: () -> Void

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: systemImage).font(.system(size: 21, weight: .light)).foregroundStyle(Palette.accent).frame(width: 31)
            VStack(alignment: .leading, spacing: 4) { Text(title).font(.system(size: 17, weight: .semibold)); Text(subtitle).font(.system(size: 11)).foregroundStyle(.secondary) }
            Spacer()
            Button(action: dismiss) { Image(systemName: "xmark.circle.fill").font(.system(size: 17)).foregroundStyle(.tertiary) }.buttonStyle(.plain).accessibilityLabel(L("settings.closeButton"))
        }.padding(22).frame(maxWidth: .infinity, alignment: .leading).overlay(alignment: .bottom) { Divider() }
    }
}

func permissionLabel(_ mode: String, provider: String = "claude") -> String {
    if provider == "claude" {
        switch mode { case "plan": return "Plan mode"; case "acceptEdits": return "Accept file edits"; case "auto": return "Auto mode"; case "fullAccess": return "Bypass"; default: return "Always ask" }
    }
    switch mode { case "plan": return L("permission.label.plan"); case "acceptEdits": return provider == "codex" ? L("permission.label.acceptEditsCodex") : L("permission.label.acceptEdits"); case "onRequest": return L("permission.label.onRequest"); case "fullAccess": return L("permission.label.fullAccess"); default: return provider == "codex" ? L("permission.label.defaultCodex") : L("permission.label.default") }
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
    if mode == "fullAccess" { return L("permission.other.fullAccess") }
    if provider == "codex", mode == "onRequest" { return L("permission.codex.onRequest") }
    if provider == "codex" { return mode == "acceptEdits" ? L("permission.codex.acceptEdits") : L("permission.codex.default") }
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
