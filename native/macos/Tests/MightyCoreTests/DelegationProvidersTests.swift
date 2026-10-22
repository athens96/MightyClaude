import Foundation
import Testing
@testable import MightyCore

/// Which agents take part in delegation and how, without the app.
@Suite(.delegationLane) struct DelegationProvidersTests {
    private let worktree = ChildWorktree(sessionId: "c1", worktreePath: "/tmp/worktrees/c1", workingFolder: "/tmp/worktrees/c1", branch: "mighty/c1", parentBranch: "main",
                                         baseCommit: String(repeating: "a", count: 40))

    @Test func eachAgentAsksForItsOwnModesNoWiderThanItsPanes() {
        #expect(DelegationProviders.startingModes(provider: "claude") == ["plan", "manual", "acceptEdits", "auto", "fullAccess"])
        #expect(DelegationProviders.startingModes(provider: "codex") == ["manual", "acceptEdits", "onRequest", "fullAccess"])
        #expect(DelegationProviders.startingModes(provider: "gemini") == ["plan", "manual", "acceptEdits", "fullAccess"])
        #expect(DelegationCoordinator.startingModes == DelegationProviders.startingModes(provider: "claude"))
        for provider in ["claude", "codex", "gemini"] {
            let modes = DelegationProviders.startingModes(provider: provider)
            for (own, stored) in modes.enumerated() {
                for (asked, mode) in modes.enumerated() { #expect(DelegationProviders.isNoWider(mode, than: stored, provider: provider) == (asked <= own), "\(provider) \(mode) under \(stored)") }
            }
        }
        // A mode the agent lacks, on either side, is never no wider.
        #expect(!DelegationProviders.isNoWider("plan", than: "fullAccess", provider: "codex"))
        #expect(!DelegationProviders.isNoWider("manual", than: "onRequest", provider: "claude"))
        #expect(!DelegationProviders.isNoWider("auto", than: "fullAccess", provider: "gemini"))
        #expect(DelegationCoordinator.isNoWider("acceptEdits", than: "onRequest", provider: "codex") && !DelegationCoordinator.isNoWider("fullAccess", than: "onRequest", provider: "codex"))
    }

    @Test func onlyACodexRunWhoseToolsCanWorkGetsTheDelegationTools() {
        for mode in DelegationProviders.startingModes(provider: "claude") { #expect(DelegationProviders.attachesDelegation(kind: SessionKind.claude, provider: "claude", mode: mode)) }
        // onRequest declines the MCP prompts the app cannot show, so it gets none either.
        for (mode, attaches, approves) in [("manual", false, false), ("acceptEdits", true, true), ("onRequest", false, false), ("fullAccess", true, true)] {
            #expect(DelegationProviders.attachesDelegation(kind: SessionKind.claude, provider: "codex", mode: mode) == attaches, "\(mode)")
            #expect(DelegationProviders.codexApprovesTools(mode: mode) == approves, "\(mode)")
        }
        for mode in DelegationProviders.startingModes(provider: "gemini") { #expect(!DelegationProviders.attachesDelegation(kind: SessionKind.claude, provider: "gemini", mode: mode)) }
        #expect(!DelegationProviders.attachesDelegation(kind: SessionKind.shell, provider: "claude", mode: "manual"))
        // ⌘Enter does not depend on the tools: an onRequest Codex pane still makes children.
        #expect(ComposerSecondSend.choose(kind: SessionKind.claude, provider: "codex", mode: "onRequest", isChild: false, delegationOn: true, canHostWorktree: true) == .worktree)
    }

    @Test func aChildCanWriteInEveryClaudeModeAndOnlyInTheEditingModesOfTheOthers() {
        for mode in DelegationProviders.startingModes(provider: "claude") { #expect(DelegationProviders.childCanWrite(provider: "claude", mode: mode)) }
        #expect(DelegationProviders.startingModes(provider: "codex").filter { DelegationProviders.childCanWrite(provider: "codex", mode: $0) } == ["acceptEdits", "onRequest", "fullAccess"])
        #expect(DelegationProviders.startingModes(provider: "gemini").filter { DelegationProviders.childCanWrite(provider: "gemini", mode: $0) } == ["acceptEdits", "fullAccess"])
        #expect(!DelegationProviders.childCanWrite(provider: "other", mode: "fullAccess"))
        #expect(!DelegationProviders.canParentByHuman(kind: SessionKind.claude, provider: "codex", mode: "manual"))
        #expect(DelegationProviders.canParentByHuman(kind: SessionKind.claude, provider: "gemini", mode: "acceptEdits"))
    }

    @Test func onlyClaudeCommitsAndMergesInAnyModeOthersOnlyInFullAccess() {
        for mode in ["plan", "manual", "acceptEdits", "auto", "fullAccess"] {
            #expect(DelegationProviders.canCommit(provider: "claude", mode: mode) && DelegationProviders.agentMayMerge(provider: "claude", mode: mode))
        }
        for provider in ["codex", "gemini"] {
            for mode in DelegationProviders.startingModes(provider: provider) {
                #expect(DelegationProviders.canCommit(provider: provider, mode: mode) == (mode == "fullAccess"), "\(provider) \(mode)")
                #expect(DelegationProviders.agentMayMerge(provider: provider, mode: mode) == (mode == "fullAccess"), "\(provider) \(mode)")
            }
        }
    }

    @Test func theAppCommitsOnlyForAnotherAgentsChangedReportWithChangesAndNoConflict() {
        #expect(!ChildAutoCommit.shouldCommit(provider: "claude", reportChanged: true, hasChanges: true, unmerged: false))
        for provider in ["codex", "gemini"] {
            #expect(ChildAutoCommit.shouldCommit(provider: provider, reportChanged: true, hasChanges: true, unmerged: false), "\(provider)")
            #expect(!ChildAutoCommit.shouldCommit(provider: provider, reportChanged: false, hasChanges: true, unmerged: false))
            #expect(!ChildAutoCommit.shouldCommit(provider: provider, reportChanged: true, hasChanges: false, unmerged: false))
            #expect(!ChildAutoCommit.shouldCommit(provider: provider, reportChanged: true, hasChanges: true, unmerged: true))
        }
        #expect(ChildAutoCommit.message(childId: "c1", revision: 2) == "mighty: c1 report r2")
        #expect(ChildAutoCommit.sameFolder("/tmp/a/../b/", "/tmp/b") && !ChildAutoCommit.sameFolder("/tmp/b/sub", "/tmp/b"))
    }

    @Test func aClaudeChildsWordsAreAsBeforeAndAnotherAgentsNameNoClaudePane() {
        let claude = DelegationCoordinator.firstInput(task: "Fix the parser.\n", worktree: worktree)
        #expect(claude == "Another Claude pane in Mighty Claude delegated this task to you. You work in your own git worktree on the branch mighty/c1; the task is also saved in \(worktree.taskFile).\n\nWhen you are done, commit your work on this branch, leaving no uncommitted changes to tracked files, and then write your report to \(worktree.reportFile): what you did, what is left and what your parent should check. Your parent gets one notice for each of your runs that ends with that file changed, and may merge your branch. Do not merge, push or switch branches.\n\nTask:\nFix the parser.\n")
        #expect(DelegationCoordinator.humanNote(worktree: worktree) == "[Mighty Claude] This request runs in its own git worktree on the branch mighty/c1, opened beside another Claude pane so that pane keeps working undisturbed. When you are done, commit your work on this branch, leaving no uncommitted changes to tracked files, and then write a short report to \(worktree.reportFile): what you did and what is left. The person who sent this merges your branch from the app. Do not merge, push or switch branches.")
        let followUp = FollowUp(id: "f-1", childId: "c1", text: "Also add tests.")
        #expect(DelegationCoordinator.text(of: followUp, reportFile: worktree.reportFile).hasSuffix("When you are done, commit your work on your branch and write your updated report to \(worktree.reportFile)."))
        for provider in ["codex", "gemini"] {
            let texts = [DelegationCoordinator.firstInput(task: "Fix the parser.", worktree: worktree, provider: provider),
                         DelegationCoordinator.humanNote(worktree: worktree, provider: provider),
                         DelegationCoordinator.humanFirstRun(text: "Fix the parser.", worktree: worktree, provider: provider).input,
                         DelegationCoordinator.text(of: followUp, reportFile: worktree.reportFile, provider: provider)]
            for text in texts {
                #expect(!text.contains("Claude pane") && !text.contains("commit your work"), "\(provider): \(text)")
                #expect(text.contains("Do not commit") && text.contains(worktree.reportFile), "\(provider): \(text)")
            }
            #expect(texts[0].hasPrefix("Another pane in Mighty Claude delegated this task to you.") && texts[0].hasSuffix("Task:\nFix the parser."))
            #expect(texts[0].contains(DelegationCoordinator.appCommitNote(worktree: worktree)) && texts[1].contains(DelegationCoordinator.appCommitNote(worktree: worktree)))
        }
        // The provider rides with the follow-up's child.
        let base = "/tmp/worktrees/c1"
        let file = DelegationFile(children: [ChildRecord(id: "c1", parentSessionId: "p", worktreePath: base, parentBranch: "main", baseCommit: String(repeating: "a", count: 40),
                                                         startingMode: "acceptEdits", requestKey: "k", state: .reported, provider: "codex")], followUps: [followUp])
        #expect(file.heldItemsByPane().isEmpty)
        #expect(file.deliveryItem(of: followUp)?.text == DelegationCoordinator.text(of: followUp, reportFile: ChildWorktree.reportFile(worktreePath: base), provider: "codex"))
    }

    @Test func aChildRecordStoresItsAgentOnlyWhenItIsNotClaude() throws {
        let claude = ChildRecord(id: "c1", parentSessionId: "p", worktreePath: "/w/c1", parentBranch: "main", baseCommit: "abc", startingMode: "plan", requestKey: "k", provider: "claude")
        #expect(claude.provider == nil && claude.agent == "claude")
        #expect(!String(decoding: try JSONEncoder().encode(claude), as: UTF8.self).contains("provider"))
        let codex = ChildRecord(id: "c2", parentSessionId: "p", worktreePath: "/w/c2", parentBranch: "main", baseCommit: "abc", startingMode: "manual", requestKey: "k", provider: "codex")
        let decoded = try JSONDecoder().decode(ChildRecord.self, from: JSONEncoder().encode(codex))
        #expect(decoded.provider == "codex" && decoded.agent == "codex")
    }

    @Test func aChildPaneRunsItsParentsAgentAndOnlyAClaudeChildTakesTheStyle() {
        for provider in ["codex", "gemini"] {
            var parent = RunSession(id: "parent", workspaceId: "ws", title: "Parent", kind: SessionKind.claude, provider: provider, settings: RunSettings(effort: "high", permissionMode: "acceptEdits"))
            parent.model = "model-x"; parent.agentViewMode = "mighty"; parent.graphViewMode = .structure
            parent.mightyStyle = "claude-plan"; parent.mightyStyleHash = "h1"
            let pane = DelegationChildPane(sessionId: "c1", parentSessionId: "parent", mode: "acceptEdits", folder: "/w/c1", startedByHuman: true, provider: provider)
            let human = DelegationPanes.childSession(pane, workspaceId: "ws", title: "Fix it", parent: parent)
            #expect(human.kind == SessionKind.claude && human.provider == provider && human.settings.permissionMode == "acceptEdits", "\(provider)")
            #expect(human.model == "model-x" && human.settings.effort == "high", "\(provider)")
            #expect(human.agentViewMode == "mighty" && human.mightyViewMode == .structure, "\(provider)")
            #expect(human.mightyStyle == nil && human.mightyStyleHash == nil, "\(provider)")
            #expect(human.parentSessionId == "parent" && human.workingFolder == "/w/c1")
            // A delegate call's child keeps its agent's default model.
            var agent = pane; agent.startedByHuman = false
            let made = DelegationPanes.childSession(agent, workspaceId: "ws", title: "Fix it", parent: parent)
            #expect(made.provider == provider && made.model == "default" && made.settings.effort == "default" && made.agentViewMode == "mighty")
        }
    }
}
