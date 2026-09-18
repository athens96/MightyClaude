import SwiftUI
import MightyCore

struct CLIAccountsSettingsSection: View {
    @EnvironmentObject private var store: AppStore
    @ViewState private var confirming: Pending?

    private struct Pending: Identifiable { let provider: String; let relogin: Bool; var id: String { provider + (relogin ? "+" : "-") } }

    var body: some View {
        Section("CLI 계정") {
            ForEach(ProviderOptions.ids, id: \.self) { provider in row(provider) }
            Text("로그인은 터미널 실행 창에서 진행됩니다. 앱이 명령을 실행해 두면 CLI가 브라우저를 엽니다. 다른 계정으로 바꿀 때는 브라우저에서 원하는 계정을 고르세요. 바꾼 계정은 다음 요청부터 적용됩니다.")
                .font(.system(size: 11)).foregroundStyle(.secondary)
                .task { store.refreshCLIAccounts() }
                .confirmationDialog(confirming.map { title($0) } ?? "", isPresented: Binding(get: { confirming != nil }, set: { if !$0 { confirming = nil } }), presenting: confirming) { pending in
                    Button(pending.relogin ? "로그아웃하고 다른 계정으로 로그인" : "로그아웃", role: .destructive) {
                        store.logoutCLI(pending.provider, thenLogin: pending.relogin ? .account : nil)
                    }
                    Button("취소", role: .cancel) {}
                } message: { pending in
                    Text("\(ProviderOptions.label(pending.provider)) CLI에 저장된 로그인 정보를 지웁니다. 터미널에서 직접 실행하는 \(ProviderOptions.label(pending.provider))에도 같이 적용됩니다.")
                }
        }
    }

    private func title(_ pending: Pending) -> String { "\(ProviderOptions.label(pending.provider)) " + (pending.relogin ? "계정을 바꿀까요?" : "에서 로그아웃할까요?") }

    @ViewBuilder private func row(_ provider: String) -> some View {
        let status = store.cliAccounts[provider]
        let busy = store.cliAccountBusy.contains(provider)
        let pending = store.cliLoginPending.contains(provider)
        HStack(alignment: .top, spacing: 8) {
            ProviderIcon(provider: provider, size: 14).frame(width: 18).padding(.top, 1)
            VStack(alignment: .leading, spacing: 2) {
                Text(ProviderOptions.label(provider)).font(.system(size: 12, weight: .medium))
                Text(status?.summary ?? "확인 중…").font(.system(size: 11)).foregroundStyle(status?.loggedIn == false ? Color.orange : Color.secondary).lineLimit(1).textSelection(.enabled)
                    .accessibilityIdentifier("cli-account-status-\(provider)")
                if let status, status.loggedIn == true, !status.canSignOut, !status.detail.isEmpty {
                    Text(status.detail).font(.system(size: 10)).foregroundStyle(.tertiary).fixedSize(horizontal: false, vertical: true)
                }
                if pending { Text("로그인 터미널을 열었습니다. 브라우저에서 로그인을 마치면 여기에 반영됩니다.").font(.system(size: 10)).foregroundStyle(.tertiary) }
                if let message = store.cliAccountMessages[provider] {
                    Label(message, systemImage: "exclamationmark.triangle").font(.system(size: 10)).foregroundStyle(.orange).fixedSize(horizontal: false, vertical: true)
                        .accessibilityIdentifier("cli-account-message-\(provider)")
                }
            }
            Spacer()
            if busy || store.cliAccountRefreshing.contains(provider) { ProgressView().controlSize(.small) }
            if status?.installed == false {
                Text("미설치").font(.system(size: 11)).foregroundStyle(.secondary)
            } else if pending {
                Button("대기 취소") { store.endCLILogin(provider); store.refreshCLIAccounts([provider]) }
            } else if status?.loggedIn == true {
                if status?.canSignOut != false {
                    Button("계정 변경") { confirming = Pending(provider: provider, relogin: true) }.disabled(busy).accessibilityIdentifier("cli-account-change-\(provider)")
                    Button("로그아웃") { confirming = Pending(provider: provider, relogin: false) }.disabled(busy)
                }
            } else if provider == "claude" {
                Menu("로그인") {
                    Button("Claude 구독으로 로그인") { store.startCLILogin(provider, option: .account) }
                    Button("Anthropic Console(API 과금)로 로그인") { store.startCLILogin(provider, option: .console) }
                }.fixedSize().disabled(busy)
            } else {
                Button("로그인") { store.startCLILogin(provider) }.disabled(busy).accessibilityIdentifier("cli-account-login-\(provider)")
            }
            Button { store.cliAccountMessages[provider] = nil; store.refreshCLIAccounts([provider]) } label: { Image(systemName: "arrow.clockwise") }.buttonStyle(.plain).disabled(busy).help("상태 다시 확인")
        }
    }
}
