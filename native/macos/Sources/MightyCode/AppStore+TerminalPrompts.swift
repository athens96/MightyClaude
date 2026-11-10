import Foundation
import MightyCore

/// A command in an agent pane's terminal that waits for input (a yes/no
/// question, a password, a line, a list) becomes a card in that pane and on the
/// phone; the user's answer is typed into the terminal as they would have typed
/// it there. Nothing is typed without an answer.
extension AppStore {
    /// How often the running commands' output is looked at.
    static let terminalPromptPollInterval: Duration = .milliseconds(400)

    /// Starts following the agent terminals for prompts, unless it already does.
    /// It stops by itself once no agent terminal runs a command.
    func watchTerminalPrompts() {
        guard terminalPromptWatch == nil, !ending else { return }
        terminalPromptWatch = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: AppStore.terminalPromptPollInterval)
                guard let self, !self.ending else { return }
                if !self.pollTerminalPrompts() { self.terminalPromptWatch = nil; return }
            }
        }
    }

    /// One look at every agent terminal; whether any command still runs.
    private func pollTerminalPrompts() -> Bool {
        var found: [String: TerminalPrompt] = [:], running = false
        for id in AgentProcessRegistry.shared.activePaneIds {
            guard let pane = AgentProcessRegistry.shared.terminalPane(forAgentPane: id) else { continue }
            if !pane.runningHandles().isEmpty { running = true }
            let monitor = terminalPromptMonitors[id] ?? TerminalPromptMonitor()
            terminalPromptMonitors[id] = monitor
            if let prompt = monitor.poll(pane) { found[id] = prompt }
        }
        if found != terminalPrompts {
            let changed = Set(found.keys).union(terminalPrompts.keys).filter { found[$0] != terminalPrompts[$0] }
            terminalPrompts = found
            for id in changed { mobileTerminalPromptChanged(id) }
        }
        return running
    }

    /// The user answered the card `promptId` of the agent pane `sessionId`: the
    /// answer goes to the waiting command, and the card goes until it asks again.
    /// An answer to a card no longer shown types nothing; whether it was typed.
    @discardableResult
    func answerTerminalPrompt(sessionId: String, promptId: String, answer: TerminalPromptAnswer) -> Bool {
        guard let prompt = terminalPrompts[sessionId], prompt.id == promptId,
              let pane = AgentProcessRegistry.shared.terminalPane(forAgentPane: sessionId), pane.isRunning(handle: prompt.handle) else { return false }
        pane.appendUserTyped(text: answer.input, handle: prompt.handle)
        terminalPromptMonitors[sessionId]?.answered(prompt)
        terminalPrompts[sessionId] = nil
        mobileTerminalPromptChanged(sessionId)
        return true
    }

    /// The phone reads the pane again when its card appears, changes or goes.
    private func mobileTerminalPromptChanged(_ id: String) {
        guard mobileBridge != nil, snapshot.mobileRemote?.enabled == true, mobileTracking.summaries[id] != nil else { return }
        mobileTracking.sessionRevisions[id, default: 0] += 1
        let revision = mobileTracking.sessionRevisions[id, default: 1]
        mobileTracking.summaries[id]?.revision = revision
        Task { await mobileRemote.notify(scope: "session:" + id, revision: revision) }
    }
}
