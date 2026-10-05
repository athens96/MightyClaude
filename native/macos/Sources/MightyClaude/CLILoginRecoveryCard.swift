import SwiftUI
import MightyCore

/// Shown in a pane whose last run lost its CLI sign-in. With automatic sign-in
/// on (Settings → CLI accounts) the sign-in has already started; otherwise
/// nothing opens until the button is pressed. Then the CLI signs in in the
/// background (Gemini: in a terminal pane beside this one) and the failed
/// request goes out again on its own.
struct CLILoginRecoveryCard: View {
    @EnvironmentObject private var store: AppStore
    let provider: String
    let sessionID: String
    @ViewState private var code = ""

    private var state: BackgroundLoginState? { store.backgroundLogins[provider] }
    /// Gemini signs in in a terminal pane already, so it has no second terminal button.
    private var signsInInTerminal: Bool { provider == "gemini" }

    var body: some View {
        HStack(alignment: .top, spacing: 7) {
            Image(systemName: "person.crop.circle.badge.exclamationmark").foregroundStyle(Palette.waitText).padding(.top, 1)
            VStack(alignment: .leading, spacing: 4) {
                Text(L("loginRecovery.title", ["provider": ProviderOptions.label(provider)])).fontWeight(.medium)
                if let note = store.loginCardNotes[sessionID] {
                    Text(L("loginRecovery.resendBlockedTemplate", ["reason": note])).foregroundStyle(Palette.waitText).fixedSize(horizontal: false, vertical: true)
                }
                switch state?.phase {
                case nil:
                    Text(store.cliLoginPending.contains(provider) ? L("loginRecovery.terminalPending") : L("loginRecovery.body"))
                        .foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                    if let message = store.cliAccountMessages[provider] {
                        Text(message).foregroundStyle(Palette.waitText).fixedSize(horizontal: false, vertical: true)
                    }
                case .starting?:
                    automaticNote
                    progress(L("loginRecovery.starting"))
                case .waiting?:
                    automaticNote
                    progress(state?.terminalSessionId != nil ? L("loginRecovery.geminiTerminal") : L("loginRecovery.waiting"))
                    if let url = state?.url {
                        Link(L("loginRecovery.openLink"), destination: url).help(url.absoluteString)
                            .accessibilityIdentifier("login-link-\(sessionID)")
                    }
                    if state?.asksForCode == true {
                        Text(L("loginRecovery.codePrompt")).foregroundStyle(.secondary)
                        HStack(spacing: 6) {
                            SecureField(L("loginRecovery.codePlaceholder"), text: $code)
                                .textFieldStyle(.roundedBorder).frame(maxWidth: 240).onSubmit(submitCode)
                            Button(L("loginRecovery.codeSubmit"), action: submitCode).controlSize(.small)
                                .disabled(code.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                        }
                    }
                case .failed(let message)?:
                    Text(message).foregroundStyle(Palette.waitText).fixedSize(horizontal: false, vertical: true)
                }
            }
            Spacer(minLength: 4)
            switch state?.phase {
            case .starting?, .waiting?:
                Button(L("loginRecovery.cancel")) { store.cancelBackgroundLogin(provider) }.controlSize(.small)
                if !signsInInTerminal { Button(L("loginRecovery.terminalButton")) { store.startTerminalLoginFallback(provider) }.controlSize(.small) }
            case .failed?:
                Button(L("loginRecovery.loginButton")) { store.startBackgroundLogin(provider, sessionId: sessionID) }.controlSize(.small)
                if !signsInInTerminal { Button(L("loginRecovery.terminalButton")) { store.startTerminalLoginFallback(provider) }.controlSize(.small) }
                resendButton
            case nil:
                Button(L("loginRecovery.loginButton")) { store.startBackgroundLogin(provider, sessionId: sessionID) }.controlSize(.small)
                    .disabled(store.cliLoginPending.contains(provider))
                resendButton
            }
            Button { store.dismissLoginRequired(sessionID) } label: { Image(systemName: "xmark").font(.system(size: 9)).frame(width: 18, height: 16) }
                .buttonStyle(.plain).accessibilityLabel(L("loginRecovery.dismiss"))
        }
        .font(.system(size: 11)).lineSpacing(2).padding(.horizontal, 12)
        .accessibilityIdentifier("login-required-\(sessionID)")
    }

    /// For a sign-in the app could not see: the user's own terminal, or a
    /// terminal pane whose account already looked signed in.
    private var resendButton: some View {
        Button(L("loginRecovery.resendButton")) { store.resendLoginRequestNow(sessionID) }.controlSize(.small)
            .help(L("loginRecovery.resendHelp"))
            .disabled(store.loginRetries.requests[sessionID] == nil)
    }

    @ViewBuilder private var automaticNote: some View {
        if state?.automatic == true {
            Text(L("loginRecovery.autoStarted")).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
        }
    }

    private func progress(_ text: String) -> some View {
        HStack(spacing: 6) {
            ProgressView().controlSize(.mini)
            Text(text).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
        }
    }

    /// The code goes to the sign-in process and is cleared from the field.
    private func submitCode() {
        guard !code.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return }
        store.sendBackgroundLoginCode(provider, code: code)
        code = ""
    }
}
