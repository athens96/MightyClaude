import SwiftUI
import MightyCore

struct CLIAccountsSettingsSection: View {
    @EnvironmentObject private var store: AppStore
    @ViewState private var confirming: Pending?

    private struct Pending: Identifiable { let provider: String; let relogin: Bool; var id: String { provider + (relogin ? "+" : "-") } }

    var body: some View {
        Section(L("settings.cliAccounts.sectionTitle")) {
            ForEach(ProviderOptions.ids, id: \.self) { provider in row(provider) }
            Text(L("settings.cliAccounts.sectionDescriptionMac"))
                .font(.system(size: 11)).foregroundStyle(.secondary)
                .task { store.refreshCLIAccounts() }
                .confirmationDialog(confirming.map { title($0) } ?? "", isPresented: Binding(get: { confirming != nil }, set: { if !$0 { confirming = nil } }), presenting: confirming) { pending in
                    Button(pending.relogin ? L("settings.cliAccounts.macReloginButton") : L("settings.cliAccounts.buttonLogout"), role: .destructive) {
                        store.logoutCLI(pending.provider, thenLogin: pending.relogin ? .account : nil)
                    }
                    Button(L("settings.cliAccounts.buttonCancel"), role: .cancel) {}
                } message: { pending in
                    Text(L("settings.cliAccounts.confirmMessageTemplate", ["provider": ProviderOptions.label(pending.provider)]))
                }
        }
    }

    private func title(_ pending: Pending) -> String {
        let label = ProviderOptions.label(pending.provider)
        return L(pending.relogin ? "settings.cliAccounts.confirmChangeTitleTemplate" : "settings.cliAccounts.confirmLogoutTitleTemplate", ["provider": label])
    }

    @ViewBuilder private func row(_ provider: String) -> some View {
        let status = store.cliAccounts[provider]
        let busy = store.cliAccountBusy.contains(provider)
        let pending = store.cliLoginPending.contains(provider)
        HStack(alignment: .top, spacing: 8) {
            ProviderIcon(provider: provider, size: 14).frame(width: 18).padding(.top, 1)
            VStack(alignment: .leading, spacing: 2) {
                Text(ProviderOptions.label(provider)).font(.system(size: 12, weight: .medium))
                Text(status?.summary ?? L("settings.cliAccounts.statusChecking")).font(.system(size: 11)).foregroundStyle(status?.loggedIn == false ? Color.orange : Color.secondary).lineLimit(1).textSelection(.enabled)
                    .accessibilityIdentifier("cli-account-status-\(provider)")
                if let status, !status.canSignOut, !status.detail.isEmpty, status.detail != status.summary {
                    Text(status.detail).font(.system(size: 10)).foregroundStyle(.tertiary).fixedSize(horizontal: false, vertical: true)
                }
                if pending { Text(L("settings.cliAccounts.statusPending")).font(.system(size: 10)).foregroundStyle(.tertiary) }
                if let message = store.cliAccountMessages[provider] {
                    Label(message, systemImage: "exclamationmark.triangle").font(.system(size: 10)).foregroundStyle(.orange).fixedSize(horizontal: false, vertical: true)
                        .accessibilityIdentifier("cli-account-message-\(provider)")
                }
            }
            Spacer()
            if busy || store.cliAccountRefreshing.contains(provider) { ProgressView().controlSize(.small) }
            if status?.installed == false {
                Text(L("settings.cliAccounts.statusNotInstalled")).font(.system(size: 11)).foregroundStyle(.secondary)
            } else if pending {
                Button(L("settings.cliAccounts.buttonCancelWait")) { store.endCLILogin(provider); store.refreshCLIAccounts([provider]) }
            } else if status?.loggedIn == true {
                if status?.canSignOut != false {
                    Button(L("settings.cliAccounts.buttonChange")) { confirming = Pending(provider: provider, relogin: true) }.disabled(busy).accessibilityIdentifier("cli-account-change-\(provider)")
                    Button(L("settings.cliAccounts.buttonLogout")) { confirming = Pending(provider: provider, relogin: false) }.disabled(busy)
                }
            } else if provider == "claude" {
                Menu(L("settings.cliAccounts.buttonLogin")) {
                    Button(L("settings.cliAccounts.buttonLoginClaude")) { store.startCLILogin(provider, option: .account) }
                    Button(L("settings.cliAccounts.buttonLoginConsole")) { store.startCLILogin(provider, option: .console) }
                }.fixedSize().disabled(busy)
            } else {
                Button(L("settings.cliAccounts.buttonLogin")) { store.startCLILogin(provider) }.disabled(busy).accessibilityIdentifier("cli-account-login-\(provider)")
            }
            if provider == "claude", status?.installed != false {
                Button(L("settings.cliAccounts.resetModelsButton")) { store.resetClaudeModels() }
                    .disabled(busy || pending || store.claudeModelResetInProgress)
                    .help(L("settings.cliAccounts.resetModelsHelp"))
                    .accessibilityIdentifier("cli-account-reset-models-claude")
                Button(L("settings.cliAccounts.bedrockButton")) { store.startCLILogin(provider, option: .bedrock) }
                    .disabled(busy || pending)
                    .help(L("settings.cliAccounts.bedrockHelp"))
                    .accessibilityIdentifier("cli-account-bedrock-claude")
            }
            Button { store.cliAccountMessages[provider] = nil; store.refreshCLIAccounts([provider]) } label: { Image(systemName: "arrow.clockwise") }.buttonStyle(.plain).disabled(busy).help(L("settings.cliAccounts.refreshTooltip"))
        }
    }
}
