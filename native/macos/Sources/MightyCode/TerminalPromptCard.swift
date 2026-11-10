import SwiftUI
import MightyCore

/// The card of a command in the pane's terminal that waits for input
/// (``TerminalPrompt``): the question as the terminal shows it, and the answers
/// its kind takes. The answer is typed into the command's terminal; the
/// terminal pane itself still takes typing as before.
struct TerminalPromptCard: View {
    @EnvironmentObject private var store: AppStore
    let sessionId: String
    let prompt: TerminalPrompt
    @ViewState private var text = ""

    var body: some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
            HStack(spacing: DesignMetrics.Spacing.sm) {
                PaneWaitBadge(systemImage: "terminal.fill")
                Text(L("terminalPrompt.title")).font(.ui(size: 13, weight: .bold)).foregroundStyle(Palette.ink).lineLimit(2)
                Spacer(minLength: 0)
                Button(L("terminalPrompt.showTerminal")) { store.selectSession(AgentIOPaneRegistry.shared.terminalPaneId(for: sessionId)) }
                    .buttonStyle(.plain).font(.ui(size: 11)).foregroundStyle(Palette.accent)
                    .accessibilityIdentifier("terminal-prompt-show-\(sessionId)")
            }
            Text(prompt.text).font(.code(size: 12)).foregroundStyle(Palette.ink).textSelection(.enabled)
                .fixedSize(horizontal: false, vertical: true).frame(maxWidth: .infinity, alignment: .leading)
                .padding(.horizontal, DesignMetrics.Spacing.sm).padding(.vertical, DesignMetrics.Spacing.sm)
                .background(Palette.raised, in: RoundedRectangle(cornerRadius: 8, style: .continuous))
            if prompt.kind == .choice {
                VStack(alignment: .leading, spacing: DesignMetrics.Spacing.xxs) {
                    ForEach(Array(prompt.options.enumerated()), id: \.offset) { _, option in
                        Text(option).font(.code(size: 11)).lineLimit(1)
                            .foregroundStyle(TerminalPromptDetector.isMarked(option) ? Palette.accent : Palette.ink2)
                    }
                }
            }
            answers
            Text(L("terminalPrompt.help")).font(.ui(size: 10)).foregroundStyle(Palette.ink2).fixedSize(horizontal: false, vertical: true)
        }
        .paneWaitCard()
        .padding(.horizontal, DesignMetrics.Spacing.lg).padding(.top, DesignMetrics.Spacing.md)
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("terminal-prompt-\(sessionId)")
    }

    @ViewBuilder private var answers: some View {
        switch prompt.kind {
        case .yesNo:
            HStack(spacing: DesignMetrics.Spacing.sm) {
                Spacer(minLength: 0)
                Button(label(L("terminalPrompt.no"), isDefault: prompt.defaultYes == false)) { answer(.no) }
                    .buttonStyle(PaneCardButtonStyle(accent: prompt.defaultYes == false)).accessibilityIdentifier("terminal-prompt-no")
                Button(label(L("terminalPrompt.yes"), isDefault: prompt.defaultYes == true)) { answer(.yes) }
                    .buttonStyle(PaneCardButtonStyle(accent: prompt.defaultYes != false)).accessibilityIdentifier("terminal-prompt-yes")
            }
        case .secret, .text:
            HStack(spacing: DesignMetrics.Spacing.sm) {
                Group {
                    if prompt.kind == .secret {
                        SecureField(L("terminalPrompt.secretPlaceholder"), text: $text)
                    } else {
                        TextField(L("terminalPrompt.textPlaceholder"), text: $text)
                    }
                }
                .textFieldStyle(.roundedBorder).font(.ui(size: 12)).onSubmit(send)
                .accessibilityIdentifier("terminal-prompt-field")
                if prompt.kind == .text {
                    Button(L("terminalPrompt.enterOnly")) { answer(.key(.enter)) }.buttonStyle(PaneCardButtonStyle())
                }
                Button(L("terminalPrompt.send"), action: send).buttonStyle(PaneCardButtonStyle(accent: true))
                    .disabled(text.isEmpty).accessibilityIdentifier("terminal-prompt-send")
            }
        case .choice:
            HStack(spacing: DesignMetrics.Spacing.sm) {
                Spacer(minLength: 0)
                Button(L("terminalPrompt.up")) { answer(.key(.up)) }.buttonStyle(PaneCardButtonStyle())
                Button(L("terminalPrompt.down")) { answer(.key(.down)) }.buttonStyle(PaneCardButtonStyle())
                Button(L("terminalPrompt.select")) { answer(.key(.enter)) }.buttonStyle(PaneCardButtonStyle(accent: true))
                    .accessibilityIdentifier("terminal-prompt-select")
            }
        }
    }

    private func label(_ word: String, isDefault: Bool) -> String { isDefault ? word + " (" + L("terminalPrompt.default") + ")" : word }

    private func send() {
        guard !text.isEmpty else { return }
        let typed = text
        text = ""  // a secret never stays in the field
        answer(.text(typed))
    }

    private func answer(_ answer: TerminalPromptAnswer) {
        store.answerTerminalPrompt(sessionId: sessionId, promptId: prompt.id, answer: answer)
    }
}
