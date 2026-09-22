import AppKit
import Foundation
import MightyCore

/// Slash-command completion catalogue per provider and workspace. Scanning
/// touches the filesystem, so it runs off the main actor and is cached for
/// a short while; the composer asks again whenever the palette opens.
/// Built-ins are answered in the app because the CLIs' headless modes do
/// not implement their interactive commands.
extension AppStore {
    struct SlashCatalogEntry { var commands: [SlashCommand]; var scannedAt: Date }

    static func slashCatalogKey(provider: String, workspacePath: String?) -> String { provider + "|" + (workspacePath ?? "") }

    func slashCommands(for session: RunSession) -> [SlashCommand] {
        guard session.kind != "shell" else { return [] }
        let workspace = snapshot.workspaces.first { $0.id == session.workspaceId }
        let path = workspace?.remote == nil ? workspace?.path : nil
        return slashCatalogs[Self.slashCatalogKey(provider: session.provider, workspacePath: path)]?.commands ?? []
    }

    /// What the palette lists for `draft`: built-ins and scanned commands
    /// while a name is being typed, or a built-in's choices after `/name `.
    func slashPalette(for session: RunSession, draft: String) -> [SlashCommand] {
        guard session.kind != "shell" else { return [] }
        let builtins = SlashCommandCatalog.builtins(provider: session.provider)
        if let query = SlashCommandCatalog.query(from: draft) {
            // A scanned command with a built-in's name would collide on id; the built-in wins.
            let names = Set(builtins.map(\.invocation))
            return SlashCommandCatalog.filter(builtins + slashCommands(for: session).filter { !names.contains($0.invocation) }, query: query)
        }
        guard let parsed = SlashCommandCatalog.argumentQuery(from: draft),
              let argument = builtins.first(where: { $0.invocation == parsed.command })?.argument else { return [] }
        return SlashCommandCatalog.filter(slashArgumentChoices(argument, command: parsed.command, session: session), query: parsed.command + " " + parsed.query)
    }

    /// The model list the composer's model menu shows: the runtime catalogue,
    /// the CLI default, and the pane's saved model when it is not in the catalogue.
    func modelOptions(for session: RunSession) -> [ModelOption] {
        var options = providerRuntime(session.provider, workspaceId: session.workspaceId).modelCatalog.models
        if !options.contains(where: { $0.value == "default" }) { options.insert(ModelOption(value: "default", displayName: "CLI 기본값"), at: 0) }
        if !options.contains(where: { $0.value == session.model }) { options.append(ModelOption(value: session.model, displayName: "\(session.model) · 저장된 모델")) }
        return options
    }

    /// The permission modes the composer's permission menu shows; a runtime
    /// that advertises none falls back to the provider's known modes.
    func permissionModes(for session: RunSession) -> [String] {
        let advertised = providerRuntime(session.provider, workspaceId: session.workspaceId).capabilities.permissionModes
        let modes = advertised.isEmpty ? ProviderOptions.permissionModes(provider: session.provider, includeAuto: false, includeOnRequest: false) : advertised
        let remote = snapshot.workspaces.first(where: { $0.id == session.workspaceId })?.remote != nil
        return remote ? modes.filter { $0 != "onRequest" } : modes
    }

    private func slashArgumentChoices(_ argument: SlashArgument, command: String, session: RunSession) -> [SlashCommand] {
        switch argument {
        case .model:
            return modelOptions(for: session).map { option in
                SlashCommand(invocation: command + " " + option.value, description: option.displayName + (option.value == session.model ? " · 현재" : ""), source: SlashCommandCatalog.modelSource, origin: .app, action: .setModel(option.value))
            }
        case .permission:
            return permissionModes(for: session).map { mode in
                SlashCommand(invocation: command + " " + mode, description: permissionLabel(mode, provider: session.provider) + (mode == session.settings.permissionMode ? " · 현재" : ""),
                             source: SlashCommandCatalog.permissionSource, origin: .app, action: .setPermission(mode))
            }
        }
    }

    /// Runs a built-in for the pane. Anything that cannot happen right now
    /// is explained in the pane's log rather than silently ignored.
    @discardableResult func performSlashAction(_ action: SlashCommandAction, sessionID id: String) -> Bool {
        guard !hasModal, let session = snapshot.sessions.first(where: { $0.id == id }), session.kind != "shell" else { return false }
        selectSession(id)
        switch action {
        case .openPlugins:
            openPluginBrowser(sessionID: id)
            if pluginBrowser == nil { slashNote(id, "지금은 플러그인 창을 열 수 없습니다. 워크스페이스 상태를 확인해 주세요.") }
        case .newConversation:
            if session.status == "running" || pendingRuns.contains(id) { slashNote(id, "실행이 끝난 뒤에 새 대화로 시작할 수 있습니다.") }
            else { resetConversation(id) }
        case .showUsage: sessionInfoSessionID = id
        case .openSettings: showSettings = true
        case .rename: beginRenameSession(id)
        case .help: slashNote(id, SlashCommandCatalog.helpText(provider: session.provider))
        case .setModel(let model):
            guard session.status != "running", !pendingRuns.contains(id) else { slashNote(id, "실행 중에는 모델을 바꿀 수 없습니다. 실행이 끝난 뒤 다시 고르세요."); return true }
            let name = modelOptions(for: session).first { $0.value == model }?.displayName ?? model
            guard session.model != model else { slashNote(id, "이미 \(name) 모델입니다."); return true }
            changeModel(id, to: model)
            slashNote(id, "모델을 \(name)\(koreanRo(name)) 바꿨습니다. 다음 요청부터 적용됩니다.")
        case .setPermission(let mode):
            let label = permissionLabel(mode, provider: session.provider)
            guard session.status != "running", !pendingRuns.contains(id) else { slashNote(id, "실행 중에는 작업 권한을 바꿀 수 없습니다. 실행이 끝난 뒤 다시 고르세요."); return true }
            guard session.settings.permissionMode != mode else { slashNote(id, "이미 \(label) 권한입니다."); return true }
            var settings = session.settings
            settings.permissionMode = mode
            saveSettings(id, settings: settings) // sets `error` itself when the runtime rejects the mode
            if snapshot.sessions.first(where: { $0.id == id })?.settings.permissionMode == mode {
                slashNote(id, "작업 권한을 \(label)\(koreanRo(label)) 바꿨습니다. 다음 요청부터 적용됩니다.")
            }
        }
        return true
    }

    private func koreanRo(_ word: String) -> String { KoreanParticle.ro(word) }

    /// `/model zzz` submitted with no matching choice.
    func noteSlashMismatch(sessionID id: String, command: String, argument: SlashArgument, query: String) {
        slashNote(id, "'\(query.prefix(80))'에 맞는 \(argument == .model ? "모델이" : "작업 권한 모드가") 없습니다. /\(command) 뒤에서 목록 중 하나를 고르세요.")
    }

    private func slashNote(_ id: String, _ text: String) {
        updateSession(id) { $0.logs.append(LogEntry(kind: "system", text: text)); $0.logs = TranscriptRetention.trimmed($0.logs) }
    }

    /// Waits, briefly, for this pane's first scan. A workspace nobody opened on
    /// the Mac has no cache yet, and answering a phone from an empty one would
    /// claim the pane has nothing but built-ins. A scan slower than `timeout`
    /// answers with whatever is cached; the next poll gets the rest.
    func awaitSlashCommands(for session: RunSession, timeout: TimeInterval) async {
        refreshSlashCommands(for: session)
        let workspace = snapshot.workspaces.first { $0.id == session.workspaceId }
        let path = workspace?.remote == nil ? workspace?.path : nil
        let key = Self.slashCatalogKey(provider: session.provider, workspacePath: path)
        let deadline = Date().addingTimeInterval(timeout)
        // The scan publishes through @Published, so polling is the cheapest way
        // to notice it without a second notification channel.
        while slashCatalogs[key] == nil, Date() < deadline, !ending {
            try? await Task.sleep(for: .milliseconds(25))
        }
    }

    /// Rescans when the cache is missing or older than 30 s.
    func refreshSlashCommands(for session: RunSession) {
        guard session.kind != "shell", !ending else { return }
        let workspace = snapshot.workspaces.first { $0.id == session.workspaceId }
        let path = workspace?.remote == nil ? workspace?.path : nil
        let key = Self.slashCatalogKey(provider: session.provider, workspacePath: path)
        if let entry = slashCatalogs[key], Date().timeIntervalSince(entry.scannedAt) < 30 { return }
        guard !slashScansInFlight.contains(key) else { return }
        slashScansInFlight.insert(key)
        let provider = session.provider
        Task.detached(priority: .userInitiated) { [weak self] in
            let commands = SlashCommandCatalog.commands(provider: provider, workspacePath: path)
            await MainActor.run {
                guard let self else { return }
                self.slashCatalogs[key] = SlashCatalogEntry(commands: commands, scannedAt: Date())
                self.slashScansInFlight.remove(key)
            }
        }
    }
}
