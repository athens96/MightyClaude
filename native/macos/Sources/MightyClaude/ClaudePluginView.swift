import Foundation
import MightyCore
import SwiftUI

/// UI state only. The owning AppStore supplies guarded service operations and
/// owns CLI lifetime; a fixture can supply these same closures without a CLI.
@MainActor
final class ClaudePluginBrowserModel: ObservableObject, Identifiable {
    let id = UUID()
    let workspace: Workspace
    let provider: String
    @Published var tab = "installed"
    @Published var search = ""
    @Published var marketplaceFilter = ""
    @Published var scope = "local"
    @Published private(set) var snapshot: ClaudePluginSnapshot?
    @Published private(set) var phase = "idle"
    @Published private(set) var activePluginID: String?
    @Published private(set) var isCancelling = false
    @Published private(set) var lastResult: ClaudePluginOperationResult?
    private let loadAction: @MainActor () async -> ClaudePluginSnapshot
    private let installAction: @MainActor (String, String) async -> ClaudePluginOperationResult
    private let refreshAction: @MainActor (String) async -> ClaudePluginOperationResult
    private let mutationBlockedReason: @MainActor () -> String?
    private var task: Task<Void, Never>?
    private var loaded = false
    private var closed = false

    init(workspace: Workspace, provider: String = "claude",
         load: @escaping @MainActor () async -> ClaudePluginSnapshot,
         install: @escaping @MainActor (String, String) async -> ClaudePluginOperationResult,
         refresh: @escaping @MainActor (String) async -> ClaudePluginOperationResult,
         mutationBlockedReason: @escaping @MainActor () -> String? = { nil }) {
        self.workspace = workspace; self.provider = provider
        self.scope = provider == "codex" ? "user" : "local"
        loadAction = load; installAction = install
        refreshAction = refresh; self.mutationBlockedReason = mutationBlockedReason
    }
    var providerLabel: String { ProviderOptions.label(provider) }
    var supportedScopes: [String] { provider == "codex" ? ["user"] : ["local", "project", "user"] }
    var isBusy: Bool { phase != "idle" || isCancelling }
    var isMutating: Bool { ["installing", "refreshing"].contains(phase) || isCancelling }
    var blockedReason: String? {
        return mutationBlockedReason()
    }
    var marketplaces: [String] {
        Array(Set((snapshot?.marketplaces.map(\.name) ?? []) + (snapshot?.available.map(\.marketplace) ?? []) + (snapshot?.installed.compactMap(\.marketplace) ?? []))).sorted()
    }
    /// Refresh uses registered sources; Codex can upgrade only Git-backed ones.
    /// Keep the broader catalog marketplace list available for search/filtering.
    var refreshableMarketplaces: [String] {
        (snapshot?.marketplaces ?? []).filter {
            (provider != "codex" || $0.sourceKind == "git")
                && (marketplaceFilter.isEmpty || $0.name == marketplaceFilter)
        }.map(\.name)
    }
    var installed: [ClaudeInstalledPlugin] {
        (snapshot?.installed ?? []).filter {
            matches(name: $0.name, description: $0.description, marketplace: $0.marketplace)
        }.sorted { ($0.name, $0.scope, $0.id) < ($1.name, $1.scope, $1.id) }
    }
    var available: [ClaudeCatalogPlugin] {
        (snapshot?.available ?? []).filter {
            matches(name: $0.name, description: $0.description, marketplace: $0.marketplace)
        }.sorted { ($0.name, $0.marketplace) < ($1.name, $1.marketplace) }
    }
    private func matches(name: String, description: String, marketplace: String?) -> Bool {
        let query = search.trimmingCharacters(in: .whitespacesAndNewlines)
        return (marketplaceFilter.isEmpty || marketplaceFilter == marketplace)
            && (query.isEmpty || name.localizedCaseInsensitiveContains(query) || description.localizedCaseInsensitiveContains(query))
    }
    func installedInSelectedScope(_ pluginID: String) -> Bool {
        (snapshot?.installed ?? []).contains { entry in
            guard entry.pluginID == pluginID, entry.scope == scope else { return false }
            return scope == "user" || entry.projectPath == nil
                || URL(fileURLWithPath: entry.projectPath!).resolvingSymlinksInPath().standardizedFileURL.path == URL(fileURLWithPath: workspace.path).resolvingSymlinksInPath().standardizedFileURL.path
        }
    }
    func loadIfNeeded() {
        guard !loaded, !closed else { return }
        reload()
    }
    func reload() {
        guard !closed, !isBusy else { return }
        loaded = true; phase = "loading"
        task = Task { [weak self] in
            guard let self else { return }
            defer { self.phase = "idle"; self.task = nil }
            let value = await self.loadAction()
            guard !Task.isCancelled, !self.closed else { return }
            self.snapshot = value
            self.reconcileFilter()
        }
    }
    func install(pluginID: String) {
        guard !closed, !isBusy else { return }
        guard canMutate() else { return }
        guard supportedScopes.contains(scope),
              snapshot?.available.contains(where: { $0.id == pluginID }) == true else {
            lastResult = ClaudePluginOperationResult(status: "failed", detail: L("plugins.selectPluginAndScopeAgain")); return
        }
        guard !installedInSelectedScope(pluginID) else { return }
        let selectedScope = scope
        phase = "installing"; activePluginID = pluginID; lastResult = nil
        task = Task { [weak self] in
            guard let self else { return }
            defer { self.phase = "idle"; self.activePluginID = nil; self.task = nil }
            let result = await self.installAction(pluginID, selectedScope)
            guard !Task.isCancelled, !self.closed else { return }
            self.lastResult = result
            let value = await self.loadAction()
            guard !Task.isCancelled, !self.closed else { return }
            self.snapshot = value
            self.reconcileFilter()
        }
    }
    func refreshMarketplaces() {
        guard !closed, !isBusy else { return }
        guard canMutate() else { return }
        let names = refreshableMarketplaces
        guard !names.isEmpty else {
            lastResult = ClaudePluginOperationResult(status: "skipped", detail: provider == "codex"
                ? L("plugins.codex.noGitMarketplaces")
                : L("plugins.noRefreshableMarketplaces"))
            return
        }
        phase = "refreshing"; lastResult = nil
        task = Task { [weak self] in
            guard let self else { return }
            defer { self.phase = "idle"; self.task = nil }
            var results: [ClaudePluginOperationResult] = []
            for name in names {
                guard !Task.isCancelled, !self.closed else { return }
                let result = await self.refreshAction(name)
                results.append(result)
                if result.status != "succeeded" { break }
            }
            guard !Task.isCancelled, !self.closed else { return }
            let failed = results.first { $0.status != "succeeded" }
            self.lastResult = failed ?? ClaudePluginOperationResult(status: "succeeded", detail: L("plugins.marketplacesRefreshed", ["count": "\(names.count)"]))
            let value = await self.loadAction()
            guard !Task.isCancelled, !self.closed else { return }
            self.snapshot = value
            self.reconcileFilter()
        }
    }
    private func canMutate() -> Bool {
        if let reason = blockedReason {
            lastResult = ClaudePluginOperationResult(status: "busy", detail: reason); return false
        }
        guard snapshot?.status == "ready" else {
            lastResult = ClaudePluginOperationResult(status: "failed", detail: L("plugins.loadListFirst")); return false
        }
        return true
    }
    private func reconcileFilter() {
        if !marketplaceFilter.isEmpty, !marketplaces.contains(marketplaceFilter) { marketplaceFilter = "" }
    }
    func cancelOperation() async {
        guard isMutating, !isCancelling, !closed else { return }
        isCancelling = true
        let running = task
        running?.cancel()
        await running?.value
        guard !closed else { isCancelling = false; return }
        lastResult = ClaudePluginOperationResult(status: "cancelled", detail: L("plugins.operation.cancelledByUser"))
        isCancelling = false
        reload()
    }
    func shutdown() async {
        closed = true
        let running = task
        running?.cancel()
        await running?.value
        task = nil; phase = "idle"; activePluginID = nil
    }
}

struct ClaudePluginView: View {
    @ObservedObject var model: ClaudePluginBrowserModel
    let onClose: () -> Void
    @ViewState private var showsOutput = false

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack(alignment: .top, spacing: 12) {
                Image(systemName: "puzzlepiece.extension").font(.system(size: 25)).foregroundStyle(Palette.accent)
                VStack(alignment: .leading, spacing: 4) {
                    Text(L("plugins.titleTemplate", ["provider": model.providerLabel])).font(.system(size: 18, weight: .semibold))
                    Text(model.workspace.name).font(.system(size: 12)).foregroundStyle(.secondary).lineLimit(1)
                    Text(model.workspace.path).font(.system(size: 10, design: .monospaced)).foregroundStyle(.tertiary).lineLimit(1).truncationMode(.middle).help(model.workspace.path)
                }
                Spacer(minLength: 12)
                if let version = model.snapshot?.cliVersion { Text(version).font(.system(size: 10, design: .monospaced)).foregroundStyle(.secondary) }
            }
            browser
            HStack {
                if model.isBusy {
                    ProgressView().controlSize(.small)
                    Text(progressLabel).font(.system(size: 11)).foregroundStyle(.secondary)
                        .accessibilityIdentifier("\(model.provider)-plugin-progress")
                }
                Spacer()
                if model.isMutating {
                    Button(model.isCancelling ? L("plugins.button.cancelling") : L("plugins.button.cancelOperation")) { Task { await model.cancelOperation() } }
                        .disabled(model.isCancelling).accessibilityIdentifier("\(model.provider)-plugin-cancel")
                }
                Button(L("settings.closeButton"), action: onClose).keyboardShortcut(.cancelAction).disabled(model.isMutating)
                    .accessibilityIdentifier("\(model.provider)-plugin-close")
            }
        }
        .padding(20).frame(width: 760, height: 620)
        .accessibilityElement(children: .contain).accessibilityIdentifier("\(model.provider)-plugin-browser")
        .onAppear { model.loadIfNeeded() }
    }

    private var browser: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 6) {
                tab(L("settings.toolkit.verdictInstalled"), value: "installed", count: model.snapshot?.installed.count ?? 0)
                tab(L("plugins.tab.marketplace"), value: "marketplace", count: model.snapshot?.available.count ?? 0)
                Spacer()
                Button { model.reload() } label: { Label(L("plugins.button.reload"), systemImage: "arrow.clockwise") }
                    .disabled(model.isBusy).accessibilityIdentifier("\(model.provider)-plugin-reload")
            }
            HStack(spacing: 10) {
                HStack(spacing: 7) {
                    Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                    TextField(L("plugins.searchPlaceholder"), text: $model.search).textFieldStyle(.plain)
                        .accessibilityIdentifier("\(model.provider)-plugin-search")
                }.padding(9).background(Palette.subtle, in: RoundedRectangle(cornerRadius: 8))
                Picker(L("plugins.tab.marketplace"), selection: $model.marketplaceFilter) {
                    Text(L("plugins.filterAll")).tag("")
                    ForEach(model.marketplaces, id: \.self) { Text($0).tag($0) }
                }
                .frame(width: 230).accessibilityIdentifier("\(model.provider)-plugin-marketplace-filter")
            }
            if model.tab == "marketplace" {
                HStack(spacing: 12) {
                    Picker(L("plugins.scopePicker.label"), selection: $model.scope) {
                        if model.provider != "codex" {
                            Text(L("plugins.scopePicker.local")).tag("local")
                            Text(L("plugins.scopePicker.project")).tag("project")
                        }
                        Text(L("plugins.scopePicker.user")).tag("user")
                    }
                    .frame(maxWidth: .infinity, alignment: .leading).disabled(model.isBusy)
                    .accessibilityIdentifier("\(model.provider)-plugin-scope")
                    Button(L("plugins.button.marketplaceRefresh")) { model.refreshMarketplaces() }
                        .disabled(model.isBusy || model.blockedReason != nil || model.snapshot?.status != "ready" || model.refreshableMarketplaces.isEmpty)
                        .accessibilityIdentifier("\(model.provider)-plugin-refresh-marketplaces")
                }
                Text(scopeExplanation).font(.system(size: 10)).foregroundStyle(.secondary)
                if model.provider == "codex", model.refreshableMarketplaces.isEmpty {
                    Text(L("plugins.codex.refreshGitOnly"))
                        .font(.system(size: 10)).foregroundStyle(.secondary)
                        .accessibilityIdentifier("codex-plugin-refresh-unavailable")
                }
            }
            if !model.isMutating, let reason = model.blockedReason {
                Label(reason, systemImage: "info.circle").font(.system(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                    .accessibilityIdentifier("\(model.provider)-plugin-blocked")
            }
            if let snapshot = model.snapshot, snapshot.status != "ready" || (model.provider == "codex" && !snapshot.detail.isEmpty) {
                Text(snapshot.detail).font(.system(size: 11))
                    .foregroundStyle(snapshot.status != "ready" || !snapshot.diagnosticOutput.isEmpty ? Palette.waitText : Color.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                    .accessibilityIdentifier("\(model.provider)-plugin-load-status")
            }
            if let result = model.lastResult {
                Label(result.detail, systemImage: result.status == "succeeded" ? "checkmark.circle.fill" : "info.circle")
                    .font(.system(size: 11)).foregroundStyle(result.status == "succeeded" ? Palette.doneText : Palette.waitText)
                    .fixedSize(horizontal: false, vertical: true).textSelection(.enabled)
                    .accessibilityIdentifier("\(model.provider)-plugin-status")

            }
            if !diagnosticOutput.isEmpty {
                DisclosureGroup(L("plugins.diagnosticsDisclosure"), isExpanded: $showsOutput) {
                    ScrollView { Text(String(diagnosticOutput.suffix(16_384))).font(.system(size: 10, design: .monospaced)).textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading) }
                        .frame(height: 80)
                }.font(.system(size: 10))
            }
            Text(model.provider == "codex"
                 ? L("plugins.codex.footerNoteMac")
                 : L("plugins.claude.footerNote"))
                .font(.system(size: 10)).foregroundStyle(.secondary)
            Divider()
            GeometryReader { viewport in
                ScrollView(.vertical) {
                    LazyVStack(alignment: .leading, spacing: 10) {
                        if model.tab == "installed" {
                            ForEach(model.installed) { installedRow($0) }
                            if model.installed.isEmpty { emptyList }
                        } else {
                            ForEach(model.available) { catalogRow($0) }
                            if model.available.isEmpty { emptyList }
                        }
                    }
                    .frame(width: max(0, viewport.size.width - 16), alignment: .leading).padding(.trailing, 16)
                }
            }
        }
    }

    private func tab(_ title: String, value: String, count: Int) -> some View {
        Button { model.tab = value } label: {
            Text("\(title) \(count)").font(.system(size: 12, weight: model.tab == value ? .semibold : .regular))
                .padding(.horizontal, 12).padding(.vertical, 7)
                .background(model.tab == value ? Palette.accent.opacity(0.17) : Palette.subtle, in: RoundedRectangle(cornerRadius: 7))
        }.buttonStyle(.plain).accessibilityIdentifier("\(model.provider)-plugin-tab-\(value)")
    }
    private func installedRow(_ plugin: ClaudeInstalledPlugin) -> some View {
        VStack(alignment: .leading, spacing: 7) {
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                Text(plugin.name).font(.system(size: 13, weight: .semibold)).lineLimit(2)
                if let version = plugin.version { Text(version).font(.system(size: 10, design: .monospaced)).foregroundStyle(.secondary) }
                Spacer(minLength: 8)
                Text(plugin.enabled.map { $0 ? L("plugins.state.enabled") : L("plugins.state.disabled") } ?? L("plugins.state.unknown"))
                    .font(.system(size: 10, weight: .medium)).foregroundStyle(plugin.enabled == true ? Palette.doneText : Color.secondary)
            }
            if !plugin.description.isEmpty { Text(plugin.description).font(.system(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true).lineLimit(3) }
            Text("\(plugin.marketplace ?? L("plugins.directInstall")) · \(scopeLabel(plugin.scope))").font(.system(size: 10)).foregroundStyle(.secondary)
            if let path = plugin.projectPath { Text(path).font(.system(size: 10, design: .monospaced)).foregroundStyle(.tertiary).lineLimit(1).truncationMode(.middle).help(path) }
            ForEach(Array(plugin.errors.enumerated()), id: \.offset) { _, error in Text(error).font(.system(size: 10)).foregroundStyle(Palette.waitText).fixedSize(horizontal: false, vertical: true) }
            ForEach(Array(plugin.notes.enumerated()), id: \.offset) { _, note in Text(note).font(.system(size: 10)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true) }
        }
        .padding(12).frame(maxWidth: .infinity, alignment: .leading).background(Palette.subtle, in: RoundedRectangle(cornerRadius: 9))
        .accessibilityElement(children: .contain).accessibilityIdentifier("\(model.provider)-plugin-row-\(plugin.id)")
    }
    private func catalogRow(_ plugin: ClaudeCatalogPlugin) -> some View {
        let installed = model.installedInSelectedScope(plugin.id)
        return HStack(alignment: .top, spacing: 14) {
            VStack(alignment: .leading, spacing: 6) {
                HStack(spacing: 8) {
                    Text(plugin.name).font(.system(size: 13, weight: .semibold)).lineLimit(2)
                    if let version = plugin.version { Text(version).font(.system(size: 10, design: .monospaced)).foregroundStyle(.secondary) }
                }
                Text(plugin.description.isEmpty ? L("plugins.noDescription") : plugin.description)
                    .font(.system(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true).lineLimit(3)
                Text("\(plugin.marketplace) · \(plugin.sourceKind)").font(.system(size: 10)).foregroundStyle(.tertiary)
            }.frame(maxWidth: .infinity, alignment: .leading)
            Button(model.activePluginID == plugin.id ? L("settings.toolkit.installingButton") : installed ? L("settings.toolkit.verdictInstalled") : L("settings.toolkit.installButton")) { model.install(pluginID: plugin.id) }
                .disabled(installed || model.isBusy || model.blockedReason != nil || model.snapshot?.status != "ready")
                .accessibilityIdentifier("\(model.provider)-plugin-install-\(plugin.id)")
        }
        .padding(12).frame(maxWidth: .infinity, alignment: .leading).background(Palette.subtle, in: RoundedRectangle(cornerRadius: 9))
        .accessibilityElement(children: .contain).accessibilityIdentifier("\(model.provider)-plugin-row-\(plugin.id)")
    }
    private var emptyList: some View {
        VStack(spacing: 10) {
            Text(emptyMessage).font(.system(size: 12)).foregroundStyle(.secondary)
            if model.snapshot?.status == "ready", model.tab == "marketplace", model.snapshot?.marketplaces.isEmpty == true {
                if model.provider == "codex" {
                    Text(L("plugins.codex.marketplaceHelp"))
                        .font(.system(size: 11)).foregroundStyle(.secondary)
                } else {
                    Link(L("plugins.marketplaceHelpLink"), destination: URL(string: "https://code.claude.com/docs/en/discover-plugins#add-marketplaces")!)
                        .font(.system(size: 11))
                }
            }
        }.frame(maxWidth: .infinity, alignment: .center).padding(.vertical, 28)
    }
    private var emptyMessage: String {
        if model.phase == "loading" { return L("plugins.empty.loading") }
        if model.snapshot?.status != "ready" { return L("plugins.empty.failed") }
        if !model.search.isEmpty || !model.marketplaceFilter.isEmpty { return L("plugins.empty.filtered") }
        return model.tab == "installed" ? L("plugins.empty.installed") : L("plugins.empty.available")
    }
    private var diagnosticOutput: String {
        let snapshotOutput = model.snapshot?.diagnosticOutput ?? ""
        if model.snapshot?.status != "ready", !snapshotOutput.isEmpty { return snapshotOutput }
        let operationOutput = model.lastResult?.output ?? ""
        if model.provider == "codex" {
            return [operationOutput, snapshotOutput].filter { !$0.isEmpty }.joined(separator: "\n\n")
        }
        return operationOutput
    }
    private var scopeExplanation: String {
        switch model.scope {
        case "project": L("plugins.scopeNote.project")
        case "user": L("plugins.scopeNote.userMac")
        default: L("plugins.scopeNote.local")
        }
    }
    private func scopeLabel(_ scope: String) -> String {
        switch scope { case "local": L("plugins.scope.local"); case "project": L("plugins.scope.project"); case "user": L("plugins.scope.user"); case "managed": L("plugins.scope.managed"); default: scope }
    }
    private var progressLabel: String {
        if model.isCancelling { return L("plugins.progress.cancelling") }
        return switch model.phase { case "installing": L("plugins.progress.installing"); case "refreshing": L("plugins.progress.refreshing"); default: L("plugins.progress.loading") }
    }
}
