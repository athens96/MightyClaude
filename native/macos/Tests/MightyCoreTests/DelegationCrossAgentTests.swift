import Foundation
import Testing
@testable import MightyCore

/// A child of another agent than its parent's, without the app: which mode it
/// starts in, how the delegate tool names its agent, what the worktree
/// button's menu offers and the pane the child opens in.
@Suite(.delegationLane) struct DelegationCrossAgentRuleTests {
    @Test func eachAgentsModeHasTheCapabilitiesTheAppStartsItWith() {
        let all: Set<ModeCapability> = [.writesAsking, .writesUnasked, .runsUnasked, .fullAccess]
        let table: [String: [String: Set<ModeCapability>]] = [
            "claude": ["plan": [], "manual": [.writesAsking], "acceptEdits": [.writesUnasked], "auto": [.writesUnasked, .runsUnasked], "fullAccess": all],
            "codex": ["manual": [.runsUnasked], "acceptEdits": [.writesUnasked, .runsUnasked], "onRequest": [.writesUnasked], "fullAccess": all],
            "gemini": ["plan": [], "manual": [], "acceptEdits": [.writesUnasked], "fullAccess": all],
        ]
        for (provider, modes) in table {
            #expect(Set(modes.keys) == Set(DelegationProviders.startingModes(provider: provider)), "\(provider)")
            for (mode, capabilities) in modes { #expect(DelegationProviders.capabilities(provider: provider, mode: mode) == capabilities, "\(provider) \(mode)") }
        }
        #expect(DelegationProviders.capabilities(provider: "codex", mode: "auto").isEmpty)
    }

    @Test func anotherAgentsChildMayDoNothingItsParentsModeMayNot() {
        // Editing unasked covers editing after asking; everything else is a plain subset.
        #expect(DelegationProviders.isCovered([.writesAsking], by: [.writesUnasked]))
        #expect(!DelegationProviders.isCovered([.writesUnasked], by: [.writesAsking]))
        #expect(!DelegationProviders.isCovered([.runsUnasked], by: [.writesUnasked]) && DelegationProviders.isCovered([], by: []))
        // A Claude parent in plan or manual mode gets no Codex or Antigravity child at all.
        for parentMode in ["plan", "manual"] {
            for (child, modes) in [("codex", ["manual", "acceptEdits", "onRequest", "fullAccess"]), ("gemini", ["acceptEdits", "fullAccess"])] {
                for mode in modes { #expect(!DelegationProviders.isNoWider(mode, child: child, than: parentMode, parent: "claude"), "\(child) \(mode) under claude \(parentMode)") }
                #expect(DelegationProviders.widestChildMode(child: child, parent: "claude", parentMode: parentMode) == nil, "\(child) under claude \(parentMode)")
            }
        }
        // Claude acceptEdits edits unasked and asks before Bash.
        #expect(DelegationProviders.isNoWider("onRequest", child: "codex", than: "acceptEdits", parent: "claude"))
        #expect(DelegationProviders.isNoWider("acceptEdits", child: "gemini", than: "acceptEdits", parent: "claude"))
        #expect(!DelegationProviders.isNoWider("acceptEdits", child: "codex", than: "acceptEdits", parent: "claude"))
        // Claude auto also runs commands unasked.
        #expect(DelegationProviders.isNoWider("acceptEdits", child: "codex", than: "auto", parent: "claude"))
        #expect(!DelegationProviders.isNoWider("fullAccess", child: "codex", than: "auto", parent: "claude"))
        // A Codex onRequest parent edits unasked in its sandbox and asks for commands.
        #expect(DelegationProviders.isNoWider("acceptEdits", child: "claude", than: "onRequest", parent: "codex"))
        #expect(DelegationProviders.isNoWider("manual", child: "claude", than: "onRequest", parent: "codex"))
        #expect(!DelegationProviders.isNoWider("auto", child: "claude", than: "onRequest", parent: "codex"))
        #expect(DelegationProviders.isNoWider("auto", child: "claude", than: "acceptEdits", parent: "codex"))
        // A read-only Codex edits nothing: only a Claude plan child fits, and Claude's plan child may write once its plan is approved.
        #expect(DelegationProviders.isNoWider("plan", child: "claude", than: "manual", parent: "codex"))
        #expect(DelegationProviders.childCanWrite(provider: "claude", mode: "plan"))
        for mode in ["manual", "acceptEdits", "auto", "fullAccess"] { #expect(!DelegationProviders.isNoWider(mode, child: "claude", than: "manual", parent: "codex"), "\(mode)") }
        #expect(DelegationProviders.widestChildMode(child: "claude", parent: "codex", parentMode: "manual") == "plan")
        #expect(DelegationProviders.widestChildMode(child: "gemini", parent: "codex", parentMode: "manual") == nil)
        // A mode the agent lacks, on either side, is never no wider.
        #expect(!DelegationProviders.isNoWider("onRequest", child: "claude", than: "fullAccess", parent: "codex"))
        #expect(!DelegationProviders.isNoWider("acceptEdits", child: "codex", than: "onRequest", parent: "claude"))
    }

    @Test func oneAgentsModesCompareByItsOwnOrderExactlyAsBefore() {
        for provider in ["claude", "codex", "gemini"] {
            let modes = DelegationProviders.startingModes(provider: provider)
            for (own, stored) in modes.enumerated() {
                for (asked, mode) in modes.enumerated() {
                    #expect(DelegationProviders.isNoWider(mode, child: provider, than: stored, parent: provider) == (asked <= own), "\(provider) \(mode) under \(stored)")
                    #expect(DelegationProviders.isNoWider(mode, than: stored, provider: provider) == (asked <= own), "\(provider) \(mode) under \(stored)")
                }
            }
        }
    }

    @Test func theWidestChildModeOfEachAgentUnderEachParentMode() {
        let expected: [String: [String: [String: String?]]] = [
            // parent agent: parent mode: child agent → its mode
            "claude": [
                "plan": ["claude": "plan", "codex": nil, "gemini": nil],
                "manual": ["claude": "manual", "codex": nil, "gemini": nil],
                "acceptEdits": ["claude": "acceptEdits", "codex": "onRequest", "gemini": "acceptEdits"],
                "auto": ["claude": "auto", "codex": "acceptEdits", "gemini": "acceptEdits"],
                "fullAccess": ["claude": "fullAccess", "codex": "fullAccess", "gemini": "fullAccess"],
            ],
            "codex": [
                "manual": ["claude": "plan", "codex": nil, "gemini": nil],
                "acceptEdits": ["claude": "auto", "codex": "acceptEdits", "gemini": "acceptEdits"],
                "onRequest": ["claude": "acceptEdits", "codex": "onRequest", "gemini": "acceptEdits"],
                "fullAccess": ["claude": "fullAccess", "codex": "fullAccess", "gemini": "fullAccess"],
            ],
            "gemini": [
                "plan": ["claude": "plan", "codex": nil, "gemini": nil],
                "manual": ["claude": "plan", "codex": nil, "gemini": nil],
                "acceptEdits": ["claude": "acceptEdits", "codex": "onRequest", "gemini": "acceptEdits"],
                "fullAccess": ["claude": "fullAccess", "codex": "fullAccess", "gemini": "fullAccess"],
            ],
        ]
        for (parent, modes) in expected {
            #expect(Set(modes.keys) == Set(DelegationProviders.startingModes(provider: parent)), "\(parent)")
            for (parentMode, children) in modes {
                for (child, mode) in children {
                    #expect(DelegationProviders.widestChildMode(child: child, parent: parent, parentMode: parentMode) == mode, "\(child) under \(parent) \(parentMode)")
                    if let mode {
                        #expect(DelegationProviders.childCanWrite(provider: child, mode: mode) && DelegationProviders.isNoWider(mode, child: child, than: parentMode, parent: parent), "\(child) \(mode)")
                    }
                }
            }
        }
        #expect(DelegationProviders.widestChildMode(child: "codex", parent: "claude", parentMode: "sideways") == nil)
        #expect(DelegationProviders.widestChildMode(child: "other", parent: "claude", parentMode: "fullAccess") == nil)
    }

    @Test func theDelegateToolNamesAgentsByTheirOwnWordsAndAntigravityAlsoByItsId() {
        for (word, provider) in [("claude", "claude"), ("codex", "codex"), ("antigravity", "gemini"), ("gemini", "gemini"), ("Codex", "codex"), (" ANTIGRAVITY\n", "gemini")] {
            #expect(DelegationProviders.agent(named: word) == provider, "\(word)")
        }
        for word in ["", "gpt", "openai", "agy", "claude code", "codex-cli"] { #expect(DelegationProviders.agent(named: word) == nil, "\(word)") }
        #expect(["claude", "codex", "gemini"].map(DelegationProviders.agentName) == ["claude", "codex", "antigravity"])
    }

    @Test func theHumansChildOfTheParentsAgentStartsAsBeforeAndOfAnotherInItsWidestMode() {
        // The parent's agent: its mode, or plan first where the agent has it; nil where its child cannot write.
        #expect(DelegationProviders.humanChildMode(child: "claude", parent: "claude", parentMode: "auto", planFirst: false) == "auto")
        #expect(DelegationProviders.humanChildMode(child: "claude", parent: "claude", parentMode: "auto", planFirst: true) == "plan")
        #expect(DelegationProviders.humanChildMode(child: "codex", parent: "codex", parentMode: "onRequest", planFirst: true) == "onRequest")
        #expect(DelegationProviders.humanChildMode(child: "codex", parent: "codex", parentMode: "manual", planFirst: false) == nil)
        #expect(DelegationProviders.humanChildMode(child: "gemini", parent: "gemini", parentMode: "acceptEdits", planFirst: true) == nil)
        // Another agent: its widest mode; the parent's style does not go with it.
        #expect(DelegationProviders.humanChildMode(child: "codex", parent: "claude", parentMode: "auto", planFirst: true) == "acceptEdits")
        #expect(DelegationProviders.humanChildMode(child: "claude", parent: "codex", parentMode: "onRequest", planFirst: true) == "acceptEdits")
        #expect(DelegationProviders.humanChildMode(child: "claude", parent: "codex", parentMode: "manual", planFirst: false) == "plan")
        #expect(DelegationProviders.humanChildMode(child: "gemini", parent: "claude", parentMode: "plan", planFirst: false) == nil)
    }

    @Test func anAgentIsHeldBackOnlyByWhatTheAppLastLearnt() {
        #expect(DelegationProviders.availability(installed: nil, account: nil) == .ready)
        #expect(DelegationProviders.availability(installed: true, account: CLIAccountStatus(provider: "codex", loggedIn: true)) == .ready)
        // Sign-in status the app could not read holds nothing back.
        #expect(DelegationProviders.availability(installed: true, account: CLIAccountStatus(provider: "codex", loggedIn: nil)) == .ready)
        #expect(DelegationProviders.availability(installed: false, account: nil) == .notInstalled)
        #expect(DelegationProviders.availability(installed: true, account: CLIAccountStatus(provider: "codex", installed: false)) == .notInstalled)
        #expect(DelegationProviders.availability(installed: nil, account: CLIAccountStatus(provider: "gemini", loggedIn: false)) == .signedOut)
    }

    @Test func theWorktreeMenuOffersEveryAgentAndSaysWhyOneCannotRun() {
        let states: [String: DelegationAgentAvailability] = ["claude": .notInstalled, "codex": .signedOut, "gemini": .notInstalled]
        // A Claude parent in auto: Claude is its own agent, so its CLI is not asked about.
        let auto = DelegationProviders.humanChildChoices(parent: "claude", parentMode: "auto", planFirst: false, openHumanChildren: 0) { states[$0] ?? .ready }
        #expect(auto == [DelegationChildChoice(agent: "claude", mode: "auto", blocked: nil), DelegationChildChoice(agent: "codex", mode: "acceptEdits", blocked: .signedOut),
                         DelegationChildChoice(agent: "gemini", mode: "acceptEdits", blocked: .notInstalled)])
        let ready = DelegationProviders.humanChildChoices(parent: "claude", parentMode: "auto", planFirst: true, openHumanChildren: 2) { _ in .ready }
        #expect(ready.map(\.mode) == ["plan", "acceptEdits", "acceptEdits"] && ready.allSatisfy { $0.blocked == nil })
        // A Claude pane in manual mode asks before each edit: no Codex or Antigravity child fits.
        let manual = DelegationProviders.humanChildChoices(parent: "claude", parentMode: "manual", planFirst: false, openHumanChildren: 0) { _ in .ready }
        #expect(manual.map(\.blocked) == [nil, .noMode, .noMode])
        // A read-only Codex edits nothing: only a Claude plan child.
        let readOnly = DelegationProviders.humanChildChoices(parent: "codex", parentMode: "manual", planFirst: false, openHumanChildren: 0) { _ in .ready }
        #expect(readOnly == [DelegationChildChoice(agent: "claude", mode: "plan", blocked: nil), DelegationChildChoice(agent: "codex", mode: nil, blocked: .noMode),
                             DelegationChildChoice(agent: "gemini", mode: nil, blocked: .noMode)])
        // A Codex parent in onRequest offers a Claude or Antigravity child in acceptEdits.
        let codex = DelegationProviders.humanChildChoices(parent: "codex", parentMode: "onRequest", planFirst: false, openHumanChildren: 0) { _ in .ready }
        #expect(codex.map(\.mode) == ["acceptEdits", "onRequest", "acceptEdits"] && codex.allSatisfy { $0.blocked == nil })
        // Three open children the human started, of any agents: none more.
        let full = DelegationProviders.humanChildChoices(parent: "codex", parentMode: "fullAccess", planFirst: false, openHumanChildren: 3) { _ in .ready }
        #expect(full.map(\.blocked) == [.cap, .cap, .cap])
    }

    @Test func theMenusCapCountsTheOpenChildrenTheHumanStartedOfEveryAgent() throws {
        let rows = [
            DelegationChildRow(id: "a", parentSessionId: "p", state: .running, startedByHuman: true, agent: "codex"),
            DelegationChildRow(id: "b", parentSessionId: "p", state: .reported, startedByHuman: true),
            DelegationChildRow(id: "c", parentSessionId: "p", state: .running),
            DelegationChildRow(id: "d", parentSessionId: "p", state: .closed, startedByHuman: true, agent: "gemini"),
            DelegationChildRow(id: "e", parentSessionId: "q", state: .running, startedByHuman: true),
        ]
        #expect(DelegationSidebar.openHumanChildCount(of: "p", children: rows) == 2)
        let file = DelegationFile(children: [ChildRecord(id: "x", parentSessionId: "p", worktreePath: "/w/x", parentBranch: "main", baseCommit: String(repeating: "a", count: 40),
                                                         startingMode: "acceptEdits", requestKey: "k", startedByHuman: true, provider: "codex")])
        let row = try #require(DelegationSidebar.rows(file).first)
        #expect(row.agent == "codex" && row.startedByHuman)
    }

    @Test func aChildOfAnotherAgentShowsItsParentsViewAndTheHumansTakesThatAgentsLastUsedModelAndEffort() {
        var parent = RunSession(id: "parent", workspaceId: "ws", title: "Parent", kind: SessionKind.claude, provider: "claude", settings: RunSettings(effort: "high", permissionMode: "auto"))
        parent.model = "opus"
        parent.agentViewMode = "mighty"
        parent.graphViewMode = .timeline
        parent.mightyStyle = "superpowers"
        var codexPane = RunSession(id: "codex-pane", workspaceId: "ws", title: "Codex", kind: SessionKind.claude, provider: "codex", settings: RunSettings(effort: "low", permissionMode: "fullAccess"))
        codexPane.model = "gpt-5.6-sol"
        for human in [true, false] {
            let pane = DelegationChildPane(sessionId: "c1", parentSessionId: "parent", mode: "onRequest", folder: "/w/c1", startedByHuman: human, provider: "codex")
            let child = DelegationPanes.childSession(pane, workspaceId: "ws", title: "Fix it", parent: parent, agentDefaults: codexPane)
            #expect(child.provider == "codex" && child.settings.permissionMode == "onRequest", "\(human)")
            // The human's child takes what a new Codex pane would; one the parent's delegate made, plain defaults.
            #expect(human ? child.model == "gpt-5.6-sol" && child.settings.effort == "low" : child.model == RunSession(workspaceId: "ws", title: "x").model && child.settings.effort == "default", "\(human)")
            #expect(child.agentViewMode == "mighty" && child.mightyViewMode == .timeline, "\(human)")
            #expect(child.mightyStyle == nil && child.mightyStyleHash == nil, "\(human)")
            // With no pane of the agent yet, a new pane's defaults.
            let fresh = DelegationPanes.childSession(pane, workspaceId: "ws", title: "Fix it", parent: parent)
            #expect(fresh.model == RunSession(workspaceId: "ws", title: "x").model && fresh.settings.effort == "default", "\(human)")
        }
        // A Claude child of a Codex parent the human started: Claude's defaults, not the Codex model.
        let claude = DelegationPanes.childSession(DelegationChildPane(sessionId: "c2", parentSessionId: "codex-pane", mode: "fullAccess", folder: "/w/c2", startedByHuman: true),
                                                  workspaceId: "ws", title: "t", parent: codexPane, agentDefaults: parent)
        #expect(claude.provider == "claude" && claude.model == "opus" && claude.settings.effort == "high" && claude.mightyStyle == nil)
    }

    @Test func theWorktreeButtonShowsForItsMenuAloneOnlyWhereThePanesOwnChildCannotWriteButAnothersCan() {
        let other = [DelegationChildChoice(agent: "codex", mode: nil, blocked: .noMode), DelegationChildChoice(agent: "claude", mode: "plan", blocked: nil)]
        func menuOnly(_ provider: String, _ mode: String, choices: [DelegationChildChoice], isChild: Bool = false, on: Bool = true, host: Bool = true) -> Bool {
            ComposerSecondSend.menuOnly(kind: SessionKind.claude, provider: provider, mode: mode, isChild: isChild, delegationOn: on, canHostWorktree: host, choices: choices)
        }
        #expect(menuOnly("codex", "manual", choices: other))
        #expect(!menuOnly("codex", "manual", choices: other, isChild: true) && !menuOnly("codex", "manual", choices: other, on: false) && !menuOnly("codex", "manual", choices: other, host: false))
        // Where the pane's own child can write, ⌘Enter and the click make one: not the menu alone.
        #expect(!menuOnly("codex", "acceptEdits", choices: other) && !menuOnly("claude", "plan", choices: other))
        // A read-only Codex or an Antigravity pane in plan or manual mode: only a Claude plan child, from the menu.
        for (provider, mode) in [("codex", "manual"), ("gemini", "plan"), ("gemini", "manual")] {
            let choices = DelegationProviders.humanChildChoices(parent: provider, parentMode: mode, planFirst: false, openHumanChildren: 0) { _ in .ready }
            #expect(choices.filter { $0.blocked == nil }.map(\.agent) == ["claude"] && choices.first?.mode == "plan", "\(provider) \(mode)")
            #expect(menuOnly(provider, mode, choices: choices), "\(provider) \(mode)")
            #expect(DelegationProviders.mayHaveHumanChild(kind: SessionKind.claude, provider: provider, mode: mode), "\(provider) \(mode)")
            // Its Claude CLI not installed: the menu has nothing to run, so no button.
            let without = DelegationProviders.humanChildChoices(parent: provider, parentMode: mode, planFirst: false, openHumanChildren: 0) { $0 == "claude" ? .notInstalled : .ready }
            #expect(!menuOnly(provider, mode, choices: without), "\(provider) \(mode)")
        }
        #expect(DelegationProviders.mayHaveHumanChild(kind: SessionKind.claude, provider: "claude", mode: "plan"))
        #expect(!DelegationProviders.mayHaveHumanChild(kind: SessionKind.shell, provider: "claude", mode: "fullAccess"))
    }

    @Test func aTextForAnotherAgentGoesBackToTheComposerNeverToThePane() {
        #expect(ComposerSecondSend.returnsToComposer(childAgent: "codex", parentAgent: "claude"))
        #expect(ComposerSecondSend.returnsToComposer(childAgent: "claude", parentAgent: "gemini"))
        // ⌘Enter, or the menu's row for the pane's own agent: steered or queued as before.
        #expect(!ComposerSecondSend.returnsToComposer(childAgent: nil, parentAgent: "claude"))
        #expect(!ComposerSecondSend.returnsToComposer(childAgent: "codex", parentAgent: "codex"))
    }

    @Test func delegateTakesAnOptionalAgentAndNoOtherNewArgument() {
        let delegate = DelegationToolManifest.delegate
        #expect(delegate.arguments.map(\.name) == ["task", "mode", "agent"] && delegate.arguments.map(\.required) == [true, true, false])
        #expect(DelegationCoordinator.argumentProblem(["task": "t", "mode": "plan"], for: delegate) == nil)
        #expect(DelegationCoordinator.argumentProblem(["task": "t", "mode": "plan", "agent": "codex"], for: delegate) == nil)
        let problem = "delegate takes exactly these string arguments: task, mode, and optionally agent."
        #expect(DelegationCoordinator.argumentProblem(["task": "t", "agent": "codex"], for: delegate) == problem)
        #expect(DelegationCoordinator.argumentProblem(["task": "t", "mode": "plan", "model": "opus"], for: delegate) == problem)
        #expect(DelegationCoordinator.argumentProblem(["child": "c"], for: DelegationToolManifest.childStatus) == nil)
        #expect(DelegationCoordinator.argumentProblem([:], for: DelegationToolManifest.childStatus) == "child_status takes exactly these string arguments: child.")
        let schema = DelegationToolManifest.definition(delegate)["inputSchema"] as? [String: Any]
        #expect(schema?["required"] as? [String] == ["task", "mode"])
        for agent in ["Claude", "Codex", "Antigravity"] { #expect(delegate.arguments[1].description.contains("\(agent) child"), "\(agent)") }
    }

    @Test func aRefusalNamesWhatItIsAboutAfterItsOneCode() throws {
        let detail = try #require(DelegationCoordinator.unavailable("codex", .signedOut))
        #expect(detail == "Codex is not signed in on this Mac; the user can check it in Mighty Claude's Settings › CLI")
        #expect(DelegationCoordinator.unavailable("gemini", .notInstalled)?.hasSuffix("Settings › CLI") == true && DelegationCoordinator.unavailable("codex", .ready) == nil)
        let result = DelegationMCPServer.result(.refusal(.agentUnavailable, detail: detail))
        #expect(result["structuredContent"] as? [String: String] == ["refused": "agent_unavailable"])
        #expect((result["content"] as? [[String: Any]])?.first?["text"] as? String == "Refused: agent_unavailable (\(detail)). Nothing was changed.")
        let unknown = DelegationMCPServer.result(.refusal(.agentUnknown))
        #expect((unknown["content"] as? [[String: Any]])?.first?["text"] as? String == "Refused: agent_unknown (agent must be claude, codex or antigravity, or left out for your own). Nothing was changed.")
    }

    @Test func aChildsFirstWordsFollowItsAgentAndNameAClaudeParentAsOne() {
        let worktree = ChildWorktree(sessionId: "c1", worktreePath: "/tmp/worktrees/c1", workingFolder: "/tmp/worktrees/c1", branch: "mighty/c1", parentBranch: "main",
                                     baseCommit: String(repeating: "a", count: 40))
        let codexOfClaude = DelegationCoordinator.firstInput(task: "Do it.", worktree: worktree, provider: "codex", parentAgent: "claude")
        #expect(codexOfClaude.hasPrefix("Another Claude pane") && codexOfClaude.contains("Do not commit"))
        let claudeOfCodex = DelegationCoordinator.firstInput(task: "Do it.", worktree: worktree, provider: "claude", parentAgent: "codex")
        #expect(claudeOfCodex.hasPrefix("Another pane") && claudeOfCodex.contains("commit your work") && !claudeOfCodex.contains("Do not commit"))
        // The parent's own agent says what it always said.
        #expect(DelegationCoordinator.firstInput(task: "Do it.", worktree: worktree, provider: "codex", parentAgent: "codex") == DelegationCoordinator.firstInput(task: "Do it.", worktree: worktree, provider: "codex"))
        #expect(DelegationCoordinator.firstInput(task: "Do it.", worktree: worktree, provider: "claude", parentAgent: "claude") == DelegationCoordinator.firstInput(task: "Do it.", worktree: worktree))
        let note = DelegationCoordinator.humanNote(worktree: worktree, provider: "gemini", parentAgent: "claude")
        #expect(note.contains("beside another Claude pane") && note.contains("Do not commit"))
        #expect(DelegationCoordinator.humanNote(worktree: worktree, provider: "claude", parentAgent: "codex").contains("beside another pane so"))
    }
}

/// The app side as a child of another agent sees it: the panes a test sets
/// up, every child pane and run it is asked for, and what it knows of each
/// agent's CLI.
private final class CrossAgentHost: DelegationHost, @unchecked Sendable {
    private let lock = NSLock()
    private var panes: [String: DelegationPaneState] = [:]
    private var made: [DelegationChildPane] = []
    private var runs: [(sessionId: String, input: String)] = []
    private var agents: [String: DelegationAgentAvailability] = [:]
    private var asked: [String] = []

    func set(_ pane: DelegationPaneState) { lock.withLock { panes[pane.sessionId] = pane } }
    func set(_ provider: String, _ availability: DelegationAgentAvailability) { lock.withLock { agents[provider] = availability } }
    var createdPanes: [DelegationChildPane] { lock.withLock { made } }
    var startedRuns: [(sessionId: String, input: String)] { lock.withLock { runs } }
    var availabilityAsked: [String] { lock.withLock { asked } }

    func createPane(_ pane: DelegationChildPane) async -> Bool {
        lock.withLock {
            made.append(pane)
            panes[pane.sessionId] = DelegationPaneState(sessionId: pane.sessionId, provider: pane.provider, permissionMode: pane.mode, folder: pane.folder, parentSessionId: pane.parentSessionId)
            return true
        }
    }

    func startRun(sessionId: String, input: String) async -> String? { lock.withLock { runs.append((sessionId, input)); return "run-of-\(sessionId)" } }
    func deliver(_ input: String, to sessionId: String, route: DeliveryRoute) async -> String? { nil }
    func paneState(sessionId: String) async -> DelegationPaneState? { lock.withLock { panes[sessionId] } }
    func stopRun(sessionId: String) async {}
    func agentAvailability(_ provider: String) async -> DelegationAgentAvailability { lock.withLock { asked.append(provider); return agents[provider] ?? .ready } }
}

/// A temp repository with the parent pane "parent" of `provider` running in
/// it in `mode`, the worktree root and the profile with the delegation file.
private struct Fixture {
    let base: URL
    let host = CrossAgentHost()
    let store: DelegationFileStore
    let coordinator: DelegationCoordinator
    let provider: String
    var repo: URL { base.appendingPathComponent("repo", isDirectory: true) }
    var root: URL { base.appendingPathComponent("worktrees", isDirectory: true) }

    init(provider: String, mode: String) async throws {
        base = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-cross-agent-\(UUID().uuidString)", isDirectory: true)
        store = DelegationFileStore(directory: base.appendingPathComponent("profile", isDirectory: true))
        try store.save(DelegationFile())
        self.provider = provider
        let maker = ChildWorktreeMaker(root: base.appendingPathComponent("worktrees", isDirectory: true), freeBytes: { _ in 50_000_000_000 })
        coordinator = try DelegationCoordinator(store: store, host: host, worktrees: maker, isSwitchOn: { true })
        try FileManager.default.createDirectory(at: repo, withIntermediateDirectories: true)
        try await git(["init", "-q", "-b", "main"])
        try await git(["config", "user.name", "Fixture"]); try await git(["config", "user.email", "fixture@example.invalid"])
        try Data("0\n".utf8).write(to: repo.appendingPathComponent("file-0.txt"))
        try await git(["add", "-A"]); try await git(["commit", "-q", "-m", "commit 0"])
        setParent(mode: mode)
    }

    func setParent(mode: String) {
        host.set(DelegationPaneState(sessionId: "parent", provider: provider, permissionMode: mode, folder: repo.path, runId: "run-1", activity: .running))
    }

    func remove() { try? FileManager.default.removeItem(at: base) }

    /// A delegation tool call from the parent, as its pane token resolves.
    func call(_ tool: String, _ arguments: [String: String] = [:], mode: String = "") async -> DelegationResponse {
        let location = PaneMCPServerLocation(socketPath: base.appendingPathComponent("io.sock").path, executable: URL(fileURLWithPath: "/usr/bin/true"))
        let binding = PaneMCPBinding(agentPaneId: "parent", token: "token-parent", server: location, workspaceId: "ws-1", workspacePath: repo.path, provider: provider, delegation: true, permissionMode: mode)
        return await coordinator.handle(DelegationRequest(tool: tool, arguments: arguments), binding: binding)
    }

    /// The human's send of `text` from the parent's worktree menu, naming `agent`.
    func send(_ text: String, agent: String?, planFirst: Bool = false) async -> HumanChildStart {
        await coordinator.startHumanChild(text, parentId: "parent", planFirst: planFirst, agent: agent,
                                          starter: HumanChildStarter(start: { id, _ in "send-run-of-\(id)" }, failed: { _ in }))
    }

    func waitFor(_ id: String, _ state: ChildState) async -> Bool {
        let deadline = Date().addingTimeInterval(60)
        while Date() < deadline {
            let reached = await coordinator.file.children.first(where: { $0.id == id })?.state == state
            if reached, await !coordinator.starting.contains(id) { return true }
            try? await Task.sleep(nanoseconds: 10_000_000)
        }
        return false
    }

    /// A refused call made nothing: the same file, no worktree, no pane or run.
    func expectNothingMade(since before: Data) throws {
        #expect(try Data(contentsOf: store.fileURL) == before)
        #expect(!FileManager.default.fileExists(atPath: root.path))
        #expect(host.createdPanes.isEmpty && host.startedRuns.isEmpty)
    }

    /// The child's run finished with its report written.
    func report(_ child: ChildRecord, runId: String) async throws {
        try Data("# Report\n\nDone.\n".utf8).write(to: URL(fileURLWithPath: ChildWorktree.reportFile(worktreePath: child.worktreePath)))
        await coordinator.childRunEnded(child.id, runId: runId, end: .finished)
    }

    /// Fixture git, kept away from the user's and the system's settings.
    @discardableResult
    func git(_ arguments: [String], in directory: String? = nil) async throws -> String {
        let executable = try #require(DelegationGit.executable)
        var environment = ProcessInfo.processInfo.environment.filter { !$0.key.hasPrefix("GIT_") }
        environment["GIT_CONFIG_GLOBAL"] = "/dev/null"; environment["GIT_CONFIG_NOSYSTEM"] = "1"; environment["GIT_OPTIONAL_LOCKS"] = "0"
        for key in ["GIT_AUTHOR", "GIT_COMMITTER"] { environment[key + "_NAME"] = "Fixture"; environment[key + "_EMAIL"] = "fixture@example.invalid" }
        let folder = directory ?? repo.path
        let result = try await ProcessCapture.run(executable: executable, arguments: ["-C", folder] + arguments, environment: environment, cwd: URL(fileURLWithPath: folder), timeout: 120)
        #expect(result.exitCode == 0, "git \(arguments.joined(separator: " ")): \(String(decoding: result.stderr, as: UTF8.self))")
        return DelegationGit.line(result.stdout)
    }
}

/// Real temp repositories and a fake host: a parent's delegate call and the
/// human's menu making a child of another agent, started by the child's agent.
@Suite(.enabled(if: DelegationGit.executable != nil, "Requires a local Git executable"), .delegationLane)
struct DelegationCrossAgentTests {
    @Test func aClaudeParentsCodexChildGetsTheWorktreeAndTheAppsCommit() async throws {
        let fixture = try await Fixture(provider: "claude", mode: "auto")
        defer { fixture.remove() }
        let base = try await fixture.git(["rev-parse", "HEAD"])
        let answer = await fixture.call("delegate", ["task": "Add a note.", "mode": "onRequest", "agent": "codex"])
        let info = try #require(answer.child)
        #expect(info.agent == "codex" && info.startingMode == "onRequest")
        #expect(await fixture.waitFor(info.id, .running))
        let child = try #require(await fixture.coordinator.file.children.first)
        #expect(child.provider == "codex" && child.agent == "codex" && child.notifiesParent)
        #expect(fixture.host.createdPanes.map(\.provider) == ["codex"] && fixture.host.createdPanes.map(\.mode) == ["onRequest"])
        #expect(fixture.host.availabilityAsked == ["codex"])
        // Its first words are a Codex child's, from a Claude pane.
        let first = try #require(fixture.host.startedRuns.first?.input)
        #expect(first.hasPrefix("Another Claude pane") && first.contains("Do not commit"))

        // The app commits what it left when its report changed.
        try Data("Note\n".utf8).write(to: URL(fileURLWithPath: child.worktreePath).appendingPathComponent("note.txt"))
        try await fixture.report(child, runId: "run-of-\(child.id)")
        let head = try await fixture.git(["rev-parse", "HEAD"], in: child.worktreePath)
        #expect(head != base)
        #expect(try await fixture.git(["log", "-1", "--format=%s"], in: child.worktreePath) == ChildAutoCommit.message(childId: child.id, revision: 1))
        #expect(await fixture.coordinator.file.children.first?.reportHead == head)

        // The parent's tools name its agent.
        #expect(await fixture.call("list_children").children?.map(\.agent) == ["codex"])
        #expect(await fixture.call("child_status", ["child": child.id]).status?.agent == "codex")
        // Its notice goes to the parent like any other.
        #expect(await fixture.coordinator.file.notices.map(\.childId) == [child.id])
    }

    @Test func aCodexParentsClaudeChildCommitsItsOwnWork() async throws {
        let fixture = try await Fixture(provider: "codex", mode: "acceptEdits")
        defer { fixture.remove() }
        let base = try await fixture.git(["rev-parse", "HEAD"])
        let answer = await fixture.call("delegate", ["task": "Add a note.", "mode": "acceptEdits", "agent": "claude"], mode: "acceptEdits")
        let info = try #require(answer.child)
        #expect(info.agent == "claude")
        #expect(await fixture.waitFor(info.id, .running))
        let child = try #require(await fixture.coordinator.file.children.first)
        // A Claude child's record keeps no provider, as every Claude child's.
        #expect(child.provider == nil && child.agent == "claude")
        #expect(fixture.host.createdPanes.map(\.provider) == ["claude"])
        let first = try #require(fixture.host.startedRuns.first?.input)
        #expect(first.hasPrefix("Another pane") && first.contains("commit your work") && !first.contains("Do not commit"))

        // What it leaves uncommitted stays so: the app never commits for a Claude child.
        try Data("Note\n".utf8).write(to: URL(fileURLWithPath: child.worktreePath).appendingPathComponent("note.txt"))
        try await fixture.report(child, runId: "run-of-\(child.id)")
        #expect(try await fixture.git(["rev-parse", "HEAD"], in: child.worktreePath) == base)
        #expect(await fixture.coordinator.file.children.first?.reportHead == base)
        // The Codex parent still may not merge by tool outside fullAccess.
        #expect(await fixture.call("merge", ["child": child.id, "expected_head": base], mode: "acceptEdits") == .refusal(.mergeHumanOnly))
    }

    @Test func anUnknownOrUnavailableAgentOrAModeItCannotTakeIsRefusedAndNothingIsMade() async throws {
        let fixture = try await Fixture(provider: "claude", mode: "acceptEdits")
        defer { fixture.remove() }
        fixture.host.set("gemini", .notInstalled)
        fixture.host.set("codex", .signedOut)
        let before = try Data(contentsOf: fixture.store.fileURL)

        let unknown = await fixture.call("delegate", ["task": "Do it.", "mode": "acceptEdits", "agent": "gpt"])
        #expect(unknown.refused == .agentUnknown && unknown.error?.contains("\"gpt\"") == true)
        let notInstalled = await fixture.call("delegate", ["task": "Do it.", "mode": "acceptEdits", "agent": "antigravity"])
        #expect(notInstalled == .refusal(.agentUnavailable, detail: "Antigravity is not installed on this Mac; the user can check it in Mighty Claude's Settings › CLI"))
        let signedOut = await fixture.call("delegate", ["task": "Do it.", "mode": "onRequest", "agent": "codex"])
        #expect(signedOut == .refusal(.agentUnavailable, detail: "Codex is not signed in on this Mac; the user can check it in Mighty Claude's Settings › CLI"))
        // A mode that is not the agent's, or one in which its child cannot write: an error.
        #expect(await fixture.call("delegate", ["task": "Do it.", "mode": "auto", "agent": "codex"]).error == "mode must be one of a Codex child's: manual, acceptEdits, onRequest, fullAccess.")
        #expect(await fixture.call("delegate", ["task": "Do it.", "mode": "manual", "agent": "codex"]).error?.hasPrefix("A Codex child in manual mode cannot change files") == true)
        #expect(await fixture.call("delegate", ["task": "Do it.", "mode": "plan", "agent": "gemini"]).error?.hasPrefix("An Antigravity child in plan mode cannot change files") == true)
        // Claude acceptEdits asks before Bash: a Codex child that runs commands unasked, or has full access, is wider.
        fixture.host.set("codex", .ready)
        #expect(await fixture.call("delegate", ["task": "Do it.", "mode": "acceptEdits", "agent": "codex"]) == .refusal(.widerMode))
        #expect(await fixture.call("delegate", ["task": "Do it.", "mode": "fullAccess", "agent": "codex"]) == .refusal(.widerMode))
        try fixture.expectNothingMade(since: before)
        // An agent the parent runs itself is never asked about.
        #expect(!fixture.host.availabilityAsked.contains("claude"))
    }

    @Test func theCapCountsEveryAgentAndTheSameCallForAnotherAgentIsAnotherChild() async throws {
        let fixture = try await Fixture(provider: "claude", mode: "fullAccess")
        defer { fixture.remove() }
        let own = try #require(await fixture.call("delegate", ["task": "Same task.", "mode": "acceptEdits"]).child)
        let codex = try #require(await fixture.call("delegate", ["task": "Same task.", "mode": "acceptEdits", "agent": "codex"]).child)
        #expect(own.id != codex.id && own.agent == "claude" && codex.agent == "codex")
        // Naming the caller's own agent is leaving it out; repeats get the same children.
        #expect(await fixture.call("delegate", ["task": "Same task.", "mode": "acceptEdits", "agent": "claude"]).child?.id == own.id)
        #expect(await fixture.call("delegate", ["task": "Same task.", "mode": "acceptEdits", "agent": " "]).child?.id == own.id)
        #expect(await fixture.call("delegate", ["task": "Same task.", "mode": "acceptEdits", "agent": "CODEX"]).child?.id == codex.id)
        let gemini = try #require(await fixture.call("delegate", ["task": "Same task.", "mode": "fullAccess", "agent": "antigravity"]).child)
        #expect(gemini.agent == "antigravity" && gemini.startingMode == "fullAccess")
        #expect(await fixture.call("delegate", ["task": "A fourth.", "mode": "acceptEdits", "agent": "codex"]) == .refusal(.widthCap))
        for info in [own, codex, gemini] { #expect(await fixture.waitFor(info.id, .running)) }
        #expect(fixture.host.createdPanes.map(\.provider) == ["claude", "codex", "gemini"])
    }

    @Test func theHumansMenuStartsAChildOfTheAgentItNamesInThatAgentsWidestMode() async throws {
        let fixture = try await Fixture(provider: "claude", mode: "auto")
        defer { fixture.remove() }
        // The parent's style starts in plan, which stays with Claude's own children.
        guard case .started(let codex) = await fixture.send("Try it in Codex.", agent: "codex", planFirst: true) else { Issue.record("No Codex child."); return }
        #expect(codex.provider == "codex" && codex.startingMode == "acceptEdits" && codex.startedByHuman == true)
        guard case .started(let gemini) = await fixture.send("Try it in Antigravity.", agent: "gemini") else { Issue.record("No Antigravity child."); return }
        #expect(gemini.provider == "gemini" && gemini.startingMode == "acceptEdits")
        guard case .started(let claude) = await fixture.send("Plan it in Claude.", agent: nil, planFirst: true) else { Issue.record("No Claude child."); return }
        #expect(claude.provider == nil && claude.startingMode == "plan")
        for id in [codex.id, gemini.id, claude.id] { #expect(await fixture.waitFor(id, .running)) }
        #expect(fixture.host.createdPanes.map(\.provider) == ["codex", "gemini", "claude"])
        #expect(fixture.host.createdPanes.map(\.startedByHuman) == [true, true, true])
        // Three open children the human started, of any agents: the cap.
        #expect(await fixture.send("One more.", agent: "codex") == .refused(.widthCap))
        // The parent's tools still neither list nor act on them.
        #expect(await fixture.call("list_children").children == [])
    }

    @Test func theHumansMenuSaysWhyAnotherAgentCannotRunTheText() async throws {
        // A Claude pane in manual mode asks before each edit: no Codex child may edit unasked under it.
        let fixture = try await Fixture(provider: "claude", mode: "manual")
        defer { fixture.remove() }
        let before = try Data(contentsOf: fixture.store.fileURL)
        #expect(await fixture.send("Try it.", agent: "codex") == .failed(L("delegation.human.error.agentNoMode", ["agent": "Codex"])))
        fixture.host.set("gemini", .signedOut)
        #expect(await fixture.send("Try it.", agent: "gemini") == .failed(L("delegation.human.error.agentSignedOut", ["agent": "Antigravity"])))
        fixture.host.set("codex", .notInstalled)
        #expect(await fixture.send("Try it.", agent: "codex") == .failed(L("delegation.human.error.agentNotInstalled", ["agent": "Codex"])))
        #expect(L("delegation.human.error.agentNotInstalled", ["agent": "Codex"]).contains("› CLI"))
        #expect(await fixture.send("Try it.", agent: "other") == .refused(.agentUnknown))
        try fixture.expectNothingMade(since: before)

        // A read-only Codex pane gets a Claude child only in plan mode.
        let readOnly = try await Fixture(provider: "codex", mode: "manual")
        defer { readOnly.remove() }
        guard case .started(let claude) = await readOnly.send("Plan it.", agent: "claude") else { Issue.record("No Claude child."); return }
        #expect(claude.provider == nil && claude.startingMode == "plan")
        #expect(await readOnly.send("Try it.", agent: "gemini") == .failed(L("delegation.human.error.agentNoMode", ["agent": "Antigravity"])))
    }

    @Test func theHumansChildOfThePanesOwnAgentNeverAsksAboutItsCLI() async throws {
        let fixture = try await Fixture(provider: "claude", mode: "auto")
        defer { fixture.remove() }
        // Even what the app last learnt against Claude does not hold back a child of the running Claude pane.
        fixture.host.set("claude", .notInstalled)
        guard case .started(let first) = await fixture.send("First.", agent: nil) else { Issue.record("No child."); return }
        guard case .started(let second) = await fixture.send("Second.", agent: "claude") else { Issue.record("No child."); return }
        for id in [first.id, second.id] { #expect(await fixture.waitFor(id, .running)) }
        #expect(fixture.host.availabilityAsked.isEmpty)
        // Another agent is asked about once per send.
        _ = await fixture.send("Third.", agent: "codex")
        #expect(fixture.host.availabilityAsked == ["codex"])
    }

    @Test func aClaudeParentsAntigravityChildGetsTheAppsCommit() async throws {
        let fixture = try await Fixture(provider: "claude", mode: "auto")
        defer { fixture.remove() }
        let base = try await fixture.git(["rev-parse", "HEAD"])
        let info = try #require(await fixture.call("delegate", ["task": "Add a note.", "mode": "acceptEdits", "agent": "antigravity"]).child)
        #expect(info.agent == "antigravity")
        #expect(await fixture.waitFor(info.id, .running))
        let child = try #require(await fixture.coordinator.file.children.first)
        #expect(child.provider == "gemini" && fixture.host.createdPanes.map(\.provider) == ["gemini"])
        let first = try #require(fixture.host.startedRuns.first?.input)
        #expect(first.hasPrefix("Another Claude pane") && first.contains("Do not commit"))
        try Data("Note\n".utf8).write(to: URL(fileURLWithPath: child.worktreePath).appendingPathComponent("note.txt"))
        try await fixture.report(child, runId: "run-of-\(child.id)")
        let head = try await fixture.git(["rev-parse", "HEAD"], in: child.worktreePath)
        #expect(head != base)
        #expect(try await fixture.git(["log", "-1", "--format=%s"], in: child.worktreePath) == ChildAutoCommit.message(childId: child.id, revision: 1))
        #expect(try await fixture.git(["status", "--porcelain"], in: child.worktreePath).isEmpty)
        let record = await fixture.coordinator.file.children.first
        #expect(record?.state == .reported && record?.reportHead == head)
    }
}
