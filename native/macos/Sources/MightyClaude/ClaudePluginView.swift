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
    var isRemote: Bool { workspace.remote != nil }
    var isBusy: Bool { phase != "idle" || isCancelling }
    var isMutating: Bool { ["installing", "refreshing"].contains(phase) || isCancelling }
    var blockedReason: String? {
        if isRemote { return "원격 컴퓨터의 MightyClaude에서 플러그인을 관리하세요." }
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
        guard !loaded, !closed, !isRemote else { return }
        reload()
    }
    func reload() {
        guard !closed, !isBusy, !isRemote else { return }
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
            lastResult = ClaudePluginOperationResult(status: "failed", detail: "목록에서 플러그인과 설치 범위를 다시 선택하세요."); return
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
                ? "갱신할 Git 마켓플레이스가 없습니다. 로컬·기본 제공 마켓플레이스는 목록 새로고침으로 확인하세요."
                : "새로고침할 등록된 마켓플레이스가 없습니다.")
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
            self.lastResult = failed ?? ClaudePluginOperationResult(status: "succeeded", detail: "\(names.count)개 마켓플레이스의 목록을 갱신했습니다.")
            let value = await self.loadAction()
            guard !Task.isCancelled, !self.closed else { return }
            self.snapshot = value
            self.reconcileFilter()
        }
    }
    private func canMutate() -> Bool {
        if let reason = blockedReason {
            lastResult = ClaudePluginOperationResult(status: isRemote ? "remote" : "busy", detail: reason); return false
        }
        guard snapshot?.status == "ready" else {
            lastResult = ClaudePluginOperationResult(status: "failed", detail: "플러그인 목록을 먼저 불러오세요."); return false
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
        lastResult = ClaudePluginOperationResult(status: "cancelled", detail: "작업을 취소했습니다. 이미 반영된 변경이 있을 수 있어 목록을 다시 확인합니다.")
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
                    Text("\(model.providerLabel) 플러그인").font(.system(size: 18, weight: .semibold))
                    Text(model.workspace.name).font(.system(size: 12)).foregroundStyle(.secondary).lineLimit(1)
                    Text(model.workspace.path).font(.system(size: 10, design: .monospaced)).foregroundStyle(.tertiary).lineLimit(1).truncationMode(.middle).help(model.workspace.path)
                }
                Spacer(minLength: 12)
                if let version = model.snapshot?.cliVersion { Text(version).font(.system(size: 10, design: .monospaced)).foregroundStyle(.secondary) }
            }
            if model.isRemote {
                VStack(spacing: 12) {
                    Image(systemName: "network.slash").font(.system(size: 30)).foregroundStyle(.secondary)
                    Text("원격 워크스페이스에서는 관리할 수 없습니다.").font(.system(size: 14, weight: .medium))
                    Text("원격 컴퓨터의 MightyClaude에서 플러그인을 관리하세요.").font(.system(size: 12)).foregroundStyle(.secondary)
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .accessibilityIdentifier("\(model.provider)-plugin-remote-unavailable")
            } else {
                browser
            }
            HStack {
                if model.isBusy {
                    ProgressView().controlSize(.small)
                    Text(progressLabel).font(.system(size: 11)).foregroundStyle(.secondary)
                        .accessibilityIdentifier("\(model.provider)-plugin-progress")
                }
                Spacer()
                if model.isMutating {
                    Button(model.isCancelling ? "취소 중…" : "작업 취소") { Task { await model.cancelOperation() } }
                        .disabled(model.isCancelling).accessibilityIdentifier("\(model.provider)-plugin-cancel")
                }
                Button("닫기", action: onClose).keyboardShortcut(.cancelAction).disabled(model.isMutating)
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
                tab("설치됨", value: "installed", count: model.snapshot?.installed.count ?? 0)
                tab("마켓플레이스", value: "marketplace", count: model.snapshot?.available.count ?? 0)
                Spacer()
                Button { model.reload() } label: { Label("목록 새로고침", systemImage: "arrow.clockwise") }
                    .disabled(model.isBusy).accessibilityIdentifier("\(model.provider)-plugin-reload")
            }
            HStack(spacing: 10) {
                HStack(spacing: 7) {
                    Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                    TextField("이름 또는 설명 검색", text: $model.search).textFieldStyle(.plain)
                        .accessibilityIdentifier("\(model.provider)-plugin-search")
                }.padding(9).background(Palette.subtle, in: RoundedRectangle(cornerRadius: 8))
                Picker("마켓플레이스", selection: $model.marketplaceFilter) {
                    Text("전체").tag("")
                    ForEach(model.marketplaces, id: \.self) { Text($0).tag($0) }
                }
                .frame(width: 230).accessibilityIdentifier("\(model.provider)-plugin-marketplace-filter")
            }
            if model.tab == "marketplace" {
                HStack(spacing: 12) {
                    Picker("설치 범위", selection: $model.scope) {
                        if model.provider != "codex" {
                            Text("로컬 · 이 워크스페이스, 나만").tag("local")
                            Text("프로젝트 · 팀과 공유").tag("project")
                        }
                        Text("사용자 · 모든 프로젝트").tag("user")
                    }
                    .frame(maxWidth: .infinity, alignment: .leading).disabled(model.isBusy)
                    .accessibilityIdentifier("\(model.provider)-plugin-scope")
                    Button("마켓플레이스 새로고침") { model.refreshMarketplaces() }
                        .disabled(model.isBusy || model.blockedReason != nil || model.snapshot?.status != "ready" || model.refreshableMarketplaces.isEmpty)
                        .accessibilityIdentifier("\(model.provider)-plugin-refresh-marketplaces")
                }
                Text(scopeExplanation).font(.system(size: 10)).foregroundStyle(.secondary)
                if model.provider == "codex", model.refreshableMarketplaces.isEmpty {
                    Text("마켓플레이스 갱신은 등록된 Git 소스만 지원합니다. 다른 소스는 목록 새로고침으로 확인하세요.")
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
                    .foregroundStyle(snapshot.status != "ready" || !snapshot.diagnosticOutput.isEmpty ? Color.orange : Color.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                    .accessibilityIdentifier("\(model.provider)-plugin-load-status")
            }
            if let result = model.lastResult {
                Label(result.detail, systemImage: result.status == "succeeded" ? "checkmark.circle.fill" : "info.circle")
                    .font(.system(size: 11)).foregroundStyle(result.status == "succeeded" ? Color.green : Color.orange)
                    .fixedSize(horizontal: false, vertical: true).textSelection(.enabled)
                    .accessibilityIdentifier("\(model.provider)-plugin-status")

            }
            if !diagnosticOutput.isEmpty {
                DisclosureGroup("명령 실행 상세", isExpanded: $showsOutput) {
                    ScrollView { Text(String(diagnosticOutput.suffix(16_384))).font(.system(size: 10, design: .monospaced)).textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading) }
                        .frame(height: 80)
                }.font(.system(size: 10))
            }
            Text(model.provider == "codex"
                 ? "이 Mac의 Codex 설치 목록과 마켓플레이스 목록입니다. 설치 후 새 Codex 세션을 시작하세요."
                 : "현재 폴더의 설정과 저장된 목록입니다. 새 설치는 다음 Claude 실행부터 적용됩니다.")
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
                Text(plugin.enabled.map { $0 ? "활성" : "비활성" } ?? "상태 미확인")
                    .font(.system(size: 10, weight: .medium)).foregroundStyle(plugin.enabled == true ? Color.green : Color.secondary)
            }
            if !plugin.description.isEmpty { Text(plugin.description).font(.system(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true).lineLimit(3) }
            Text("\(plugin.marketplace ?? "직접 설치") · \(scopeLabel(plugin.scope))").font(.system(size: 10)).foregroundStyle(.secondary)
            if let path = plugin.projectPath { Text(path).font(.system(size: 10, design: .monospaced)).foregroundStyle(.tertiary).lineLimit(1).truncationMode(.middle).help(path) }
            ForEach(Array(plugin.errors.enumerated()), id: \.offset) { _, error in Text(error).font(.system(size: 10)).foregroundStyle(.orange).fixedSize(horizontal: false, vertical: true) }
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
                Text(plugin.description.isEmpty ? "설명이 제공되지 않았습니다." : plugin.description)
                    .font(.system(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true).lineLimit(3)
                Text("\(plugin.marketplace) · \(plugin.sourceKind)").font(.system(size: 10)).foregroundStyle(.tertiary)
            }.frame(maxWidth: .infinity, alignment: .leading)
            Button(model.activePluginID == plugin.id ? "설치 중…" : installed ? "설치됨" : "설치") { model.install(pluginID: plugin.id) }
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
                    Text("Codex CLI에서 마켓플레이스를 등록한 뒤 목록을 새로고침하세요.")
                        .font(.system(size: 11)).foregroundStyle(.secondary)
                } else {
                    Link("마켓플레이스 추가 방법", destination: URL(string: "https://code.claude.com/docs/en/discover-plugins#add-marketplaces")!)
                        .font(.system(size: 11))
                }
            }
        }.frame(maxWidth: .infinity, alignment: .center).padding(.vertical, 28)
    }
    private var emptyMessage: String {
        if model.phase == "loading" { return "플러그인 목록을 불러오는 중…" }
        if model.snapshot?.status != "ready" { return "목록을 불러오지 못했습니다. 목록 새로고침으로 다시 확인하세요." }
        if !model.search.isEmpty || !model.marketplaceFilter.isEmpty { return "검색 조건에 맞는 플러그인이 없습니다." }
        return model.tab == "installed" ? "설치된 플러그인이 없습니다." : "등록된 마켓플레이스에서 제공한 플러그인이 없습니다."
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
        case "project": "프로젝트 설정에 기록해 팀과 같은 플러그인을 사용합니다."
        case "user": "이 Mac의 모든 프로젝트에서 사용하는 사용자 설정에 설치합니다."
        default: "현재 워크스페이스에만 적용하며 팀의 공유 설정은 바꾸지 않습니다."
        }
    }
    private func scopeLabel(_ scope: String) -> String {
        switch scope { case "local": "로컬 · 나만"; case "project": "프로젝트 · 공유"; case "user": "사용자 · 전체"; case "managed": "관리자 관리"; default: scope }
    }
    private var progressLabel: String {
        if model.isCancelling { return "작업을 취소하는 중…" }
        return switch model.phase { case "installing": "플러그인 설치 중…"; case "refreshing": "마켓플레이스 갱신 중…"; default: "목록을 불러오는 중…" }
    }
}
