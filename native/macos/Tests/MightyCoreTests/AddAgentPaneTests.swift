import Foundation
import Testing
@testable import MightyCore

/// Picking an agent in "창 추가": which agents ask "새로 시작" or "이어가기", and when
/// the question is skipped.
struct AddAgentPaneTests {
    private func session(_ provider: String, _ id: String) -> ResumableSession {
        ResumableSession(provider: provider, sessionID: id, title: "t", modified: Date(timeIntervalSince1970: 0), requests: nil, model: nil,
                         url: URL(fileURLWithPath: "/tmp/\(id).jsonl"))
    }

    @Test func onlyAgentsTheAppCanResumeOfferTheChoice() {
        #expect(AddAgentPane.offersResume("claude"))
        #expect(AddAgentPane.offersResume("codex"))
        #expect(!AddAgentPane.offersResume("gemini"))
        #expect(!AddAgentPane.offersResume(""))
        #expect(ProviderOptions.ids.filter(AddAgentPane.offersResume) == ResumableSessions.providers)
    }

    @Test func anAgentWithoutResumeStartsAtOnceEvenWithSessionsListed() {
        #expect(AddAgentPane.step(provider: "gemini", sessions: [session("gemini", "g1"), session("claude", "c1")], inUse: []) == .startNew)
    }

    @Test func theChoiceIsSkippedWhenThisAgentHasNothingToContinue() {
        #expect(AddAgentPane.step(provider: "claude", sessions: [], inUse: []) == .startNew)
        // Another agent's session does not count.
        #expect(AddAgentPane.step(provider: "claude", sessions: [session("codex", "x1")], inUse: []) == .startNew)
        // A session a pane took while the look-up ran does not count either, in any case.
        #expect(AddAgentPane.step(provider: "codex", sessions: [session("codex", "AbC-1")], inUse: ["abc-1"]) == .startNew)
        #expect(AddAgentPane.step(provider: "codex", sessions: [session("codex", "abc-1")], inUse: ["ABC-1"]) == .startNew)
    }

    @Test func theChoiceIsAskedWhenThisAgentHasASessionToContinue() {
        #expect(AddAgentPane.step(provider: "claude", sessions: [session("claude", "c1")], inUse: []) == .askResumeOrNew)
        #expect(AddAgentPane.step(provider: "codex", sessions: [session("codex", "x1"), session("codex", "x2")], inUse: ["x1"]) == .askResumeOrNew)
    }

    @Test func theProbeKeepsThePickerRulesAndStopsAtOne() {
        let query = ResumableSessionQuery(workspacePath: "/w", excluding: ["A"], known: ["B"], includeAutomated: true, maximumSessions: 200)
        let probe = AddAgentPane.probe(query)
        #expect(probe.maximumSessions == 1)
        #expect(!probe.includeAutomated)
        #expect(probe.headOnly && !query.headOnly)
        #expect(probe.excluding == ["a"] && probe.known == ["b"] && probe.workspacePath == "/w")
        #expect(probe.maximumCandidates == query.maximumCandidates && probe.maximumScanned == query.maximumScanned)
    }

    @Test func aSlowLookUpShowsTheSheetAfterAShortQuietWait() {
        #expect(AddAgentPane.quietLookUp == .milliseconds(300))
    }
}
