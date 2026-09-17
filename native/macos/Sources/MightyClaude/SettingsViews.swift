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
    private var running: Bool { currentSession.status == "running" }
    private var unsupportedSettings: Bool {
        (fastMode && !runtime.capabilities.fastMode) || (webSearch != "default" && !runtime.capabilities.webSearch) || (networkAccess && !runtime.capabilities.networkAccess)
    }

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Text("실행 설정").font(.system(size: 14, weight: .semibold))
                Spacer()
                Text(ProviderOptions.label(session.provider)).font(.system(size: 11)).foregroundStyle(.secondary)
            }.padding(16)
            Divider()
            ScrollView {
                VStack(alignment: .leading, spacing: 15) {
                    VStack(alignment: .leading, spacing: 6) {
                        Text(selectedModel?.displayName ?? session.model).font(.system(size: 13, weight: .medium))
                        if let description = selectedModel?.description, !description.isEmpty { Text(description).font(.system(size: 11)).foregroundStyle(.secondary).textSelection(.enabled) }
                        Label(runtime.modelCatalog.source == "cli" ? "CLI에서 확인한 모델" : "기본 모델 목록 · 사용 가능 여부는 계정에 따름", systemImage: runtime.modelCatalog.source == "cli" ? "checkmark.circle" : "info.circle")
                            .font(.system(size: 10)).foregroundStyle(.secondary)
                    }
                    Divider()
                    VStack(alignment: .leading, spacing: 6) {
                        Text(permissionLabel(currentSession.settings.permissionMode, provider: session.provider)).font(.system(size: 12, weight: .medium))
                        Text(permissionDescription(currentSession.settings.permissionMode, provider: session.provider)).font(.system(size: 11)).foregroundStyle(.secondary)
                        if let reference = store.snapshot.workspaces.first(where: { $0.id == session.workspaceId })?.remote {
                            Text("\(reference.hostName)의 계정 권한으로 실행합니다.").font(.system(size: 11)).foregroundStyle(.secondary)
                        }
                        if currentSession.settings.permissionMode != "fullAccess" {
                            if session.provider == "claude", store.snapshot.workspaces.first(where: { $0.id == session.workspaceId })?.remote == nil {
                                Text("추가 권한이 필요한 작업은 실행 창에서 이번만 허용하거나 거부할 수 있습니다. CLI에 명시된 차단 규칙은 유지됩니다.").font(.system(size: 11)).foregroundStyle(.secondary)
                            } else {
                                Text("이 실행기는 앱 내 추가 승인을 지원하지 않습니다. 추가 승인이 필요한 작업은 CLI가 거부할 수 있습니다.").font(.system(size: 11)).foregroundStyle(.secondary)
                            }
                        }
                    }
                    if runtime.capabilities.fastMode {
                        Text("Fast는 사고 강도와 별개입니다. 지원 모델·계정에서 사용할 수 있으며 사용량이 증가합니다.").font(.system(size: 11)).foregroundStyle(.secondary)
                    }
                    if runtime.capabilities.webSearch {
                        Divider()
                        VStack(alignment: .leading, spacing: 7) {
                            Picker("웹 검색", selection: $webSearch) {
                                Text("CLI 기본값").tag("default")
                                Text("끔").tag("disabled")
                                Text("캐시 검색").tag("cached")
                                Text("실시간 검색").tag("live")
                            }.pickerStyle(.menu).accessibilityLabel("웹 검색")
                            Text(webSearchDescription(webSearch)).font(.system(size: 11)).foregroundStyle(.secondary)
                        }
                    }
                    if runtime.capabilities.networkAccess {
                        VStack(alignment: .leading, spacing: 7) {
                            Toggle("Shell 네트워크", isOn: $networkAccess).toggleStyle(.switch).controlSize(.small)
                                .disabled(currentSession.settings.permissionMode != "acceptEdits").accessibilityLabel("Shell 네트워크")
                            Text(currentSession.settings.permissionMode == "fullAccess" ? "전체 접근에서는 이 스위치가 네트워크를 제한하지 않습니다." : currentSession.settings.permissionMode == "acceptEdits" ? "작업 폴더에서 실행하는 Shell 명령의 네트워크 접근입니다. 웹 검색 설정과는 별개입니다." : "작업 폴더 권한을 선택하면 Shell 네트워크를 설정할 수 있습니다.")
                                .font(.system(size: 11)).foregroundStyle(.secondary)
                        }
                    }
                    if runtime.capabilities.maxTurns || runtime.capabilities.maxBudgetUsd {
                        Divider()
                        VStack(alignment: .leading, spacing: 9) {
                            Text("실행 한도").font(.system(size: 12, weight: .medium))
                            if runtime.capabilities.maxTurns {
                                VStack(alignment: .leading, spacing: 5) {
                                    Text("최대 턴 수").font(.system(size: 11))
                                    TextField("최대 턴 수", text: $maxTurns, prompt: Text("제한 없음")).textFieldStyle(.roundedBorder).accessibilityLabel("최대 턴 수")
                                }
                            }
                            if runtime.capabilities.maxBudgetUsd {
                                VStack(alignment: .leading, spacing: 5) {
                                    Text("비용 한도 (USD)").font(.system(size: 11))
                                    TextField("비용 한도 (USD)", text: $maxBudget, prompt: Text("제한 없음")).textFieldStyle(.roundedBorder).accessibilityLabel("비용 한도 (USD)")
                                }
                            }
                            Text("비우면 별도 한도를 전달하지 않습니다. 턴 수는 1–1,000, 비용은 0보다 크고 10,000 USD 이하입니다.").font(.system(size: 11)).foregroundStyle(.secondary)
                        }
                    }
                    if unsupportedSettings {
                        Divider()
                        Text("이 실행기가 지원하지 않는 추가 설정이 저장되어 있습니다.").font(.system(size: 11)).foregroundStyle(.secondary)
                        Button("미지원 설정 해제") {
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
                Text("다음 요청에 적용").font(.system(size: 10)).foregroundStyle(.secondary)
                Spacer()
                Button("취소") { dismiss() }.keyboardShortcut(.cancelAction)
                Button("적용") { save() }.buttonStyle(.borderedProminent).keyboardShortcut(.defaultAction).disabled(running)
            }.controlSize(.small).padding(14)
        }.frame(width: 360, height: runtime.capabilities.webSearch ? 500 : 445)
    }

    private func save() {
        guard !running else { return }
        var settings = currentSession.settings
        settings.fastMode = fastMode
        settings.webSearch = webSearch
        settings.networkAccess = currentSession.settings.permissionMode == "acceptEdits" && networkAccess
        let turns = maxTurns.trimmingCharacters(in: .whitespacesAndNewlines)
        let budget = maxBudget.trimmingCharacters(in: .whitespacesAndNewlines)
        if runtime.capabilities.maxTurns, !turns.isEmpty {
            guard let value = Int(turns), (1...1000).contains(value) else { validationError = "최대 턴 수는 1–1,000 사이의 정수로 입력하세요."; return }
            settings.maxTurns = value
        } else { settings.maxTurns = nil }
        if runtime.capabilities.maxBudgetUsd, !budget.isEmpty {
            guard let value = Double(budget), value.isFinite, value > 0, value <= 10_000 else { validationError = "비용 한도는 0보다 크고 10,000 이하인 금액으로 입력하세요."; return }
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
            SheetHeading(title: "MightyClaude 설정", subtitle: "macOS · SwiftUI", systemImage: "gearshape") { dismiss() }
            Form {
                Section("화면") {
                    Picker("테마", selection: $store.snapshot.theme) { Text("다크").tag("dark"); Text("라이트").tag("light") }.pickerStyle(.segmented)
                }
                Section("원격 연결") {
                    Button { store.settingsShowsRemote = true } label: {
                        Label("Tailscale 원격 연결 설정…", systemImage: "network")
                    }
                    .accessibilityIdentifier("settings-remote")
                    Text("다른 컴퓨터의 워크스페이스에 연결하거나 이 Mac의 워크스페이스를 공유합니다.")
                        .font(.system(size: 11)).foregroundStyle(.secondary)
                }
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
                                Text(provider.available ? "실행 준비됨" : "설정 필요").font(.system(size: 10)).foregroundStyle(provider.available ? .green : .orange)
                            }
                            if let version = provider.version { Text(version).font(.system(size: 10, design: .monospaced)).foregroundStyle(.secondary) }
                            Text(provider.detail).font(.system(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true).textSelection(.enabled)
                        }.padding(.vertical, 4)
                    }
                    HStack {
                        Text("각 CLI의 로그인은 터미널에서 진행하세요.").font(.system(size: 11)).foregroundStyle(.secondary)
                        Spacer()
                        Button(store.isRefreshingRuntime ? "확인 중…" : "다시 확인") { Task { await store.refreshRuntime() } }.disabled(store.isRefreshingRuntime)
                    }
                } header: { Text("이 Mac의 CLI") }
                if let mods = store.runtime?.mods {
                    Section("Claude Mods") {
                        Text(mods.detail).font(.system(size: 12)).foregroundStyle(.secondary).textSelection(.enabled)
                        LabeledContent("앱 호환 기준", value: mods.minimumVersion)
                    }
                }
                Section("앱 정보") {
                    LabeledContent("버전", value: store.runtime?.appVersion ?? "0.1.0")
                    Text("SwiftUI · AppKit · 네이티브 프로세스 실행").font(.system(size: 11)).foregroundStyle(.secondary)
                    LabeledContent("상태 저장 위치") {
                        Button("Finder에서 열기") { NSWorkspace.shared.selectFile(nil, inFileViewerRootedAtPath: store.dataDirectory.path) }
                    }
                    Text(store.dataDirectory.path).font(.system(size: 10, design: .monospaced)).foregroundStyle(.secondary).textSelection(.enabled)
                }
            }.formStyle(.grouped)
            Divider()
            HStack { Spacer(); Button("닫기") { dismiss() }.keyboardShortcut(.cancelAction) }.padding(18)
        }.frame(width: 570, height: 700)
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
            Button(action: dismiss) { Image(systemName: "xmark.circle.fill").font(.system(size: 17)).foregroundStyle(.tertiary) }.buttonStyle(.plain).accessibilityLabel("닫기")
        }.padding(22).frame(maxWidth: .infinity, alignment: .leading).overlay(alignment: .bottom) { Divider() }
    }
}

func permissionLabel(_ mode: String, provider: String = "claude") -> String {
    if provider == "claude" {
        switch mode { case "plan": return "Plan mode"; case "acceptEdits": return "Accept file edits"; case "auto": return "Auto mode"; case "fullAccess": return "Bypass"; default: return "Always ask" }
    }
    switch mode { case "plan": return "계획"; case "acceptEdits": return provider == "codex" ? "작업 폴더" : "수정 허용"; case "fullAccess": return "전체 접근"; default: return provider == "codex" ? "읽기 전용" : "기본 권한" }
}

func permissionDescription(_ mode: String, provider: String) -> String {
    if provider == "claude" {
        switch mode {
        case "plan": return "먼저 읽고 계획합니다. 소스 변경 전에 승인을 요청하며, 탐색 명령에는 CLI의 계획 모드 정책을 적용합니다."
        case "acceptEdits": return "파일 수정과 일반 파일 작업은 자동 승인합니다. 그 밖의 작업은 기존 CLI 규칙과 1회 승인을 따릅니다."
        case "auto": return "Claude가 작업 위험을 자동 판단해 반복 승인을 줄입니다. 명시적 확인 규칙이나 보호된 작업은 승인이 필요할 수 있습니다. 모델·제공자·관리자 정책에 따라 사용할 수 없거나 수동 확인으로 전환될 수 있습니다."
        case "fullAccess": return "일반 권한 확인을 생략합니다. 프로젝트 밖 파일과 명령에도 접근할 수 있습니다. 명시적 거부 규칙과 CLI가 반드시 확인하는 작업은 계속 적용됩니다."
        default: return "Claude의 수동 확인 모드입니다. 읽기와 기존에 허용된 작업은 실행하고, 추가 권한이 필요한 작업은 1회 승인을 요청합니다."
        }
    }
    if mode == "fullAccess" { return "승인 확인 없이 실행합니다. 프로젝트 밖의 파일을 수정하거나 명령을 실행할 수 있습니다." }
    if provider == "codex" { return mode == "acceptEdits" ? "작업 폴더에 쓸 수 있는 샌드박스에서 실행합니다. 추가 승인 요청은 허용하지 않습니다." : "읽기 전용 샌드박스에서 실행합니다. 추가 승인 요청은 허용하지 않습니다." }
    switch mode {
    case "plan": return "코드를 수정하기 전에 읽기와 계획 작업을 진행합니다. CLI의 계획 모드를 사용합니다."
    case "acceptEdits": return "CLI의 파일 수정 허용 모드를 사용합니다. 그 밖의 권한은 CLI 설정을 따릅니다."
    default: return "CLI의 기본 권한 정책을 따릅니다. 기존 CLI 설정에서 허용된 작업을 수행할 수 있습니다."
    }
}

private func webSearchDescription(_ value: String) -> String {
    switch value {
    case "disabled": return "웹 검색 도구를 사용하지 않습니다."
    case "cached": return "웹 검색 도구가 캐시된 결과를 사용합니다."
    case "live": return "웹 검색 도구가 실시간 결과를 사용합니다."
    default: return "CLI에 저장된 웹 검색 설정을 따릅니다."
    }
}
