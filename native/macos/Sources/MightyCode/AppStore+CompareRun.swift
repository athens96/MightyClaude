import Foundation
import MightyCore

extension AppStore {
    /// The agents a compare run from `session` would start now (`CompareRun.agents`).
    func compareRunAgents(_ session: RunSession) -> [String] {
        guard offersWorktreeMenu(session) else { return [] }
        return CompareRun.agents(choices: worktreeChildChoices(session), parent: session.provider,
                                 openChildren: DelegationSidebar.openHumanChildCount(of: session.id, children: delegationChildren))
    }

    /// 비교 실행: the composer's text runs now in one new child worktree per
    /// agent that can take it, each the way the worktree menu starts one.
    func startCompareRun(_ id: String) {
        guard let session = snapshot.sessions.first(where: { $0.id == id }) else { return }
        let agents = compareRunAgents(session)
        let original = drafts[id] ?? ""
        let input = original.trimmingCharacters(in: .whitespacesAndNewlines)
        guard agents.count >= 2, !input.isEmpty else { return }
        let attachments = attachmentDrafts[id] ?? []
        for agent in agents {
            runInNewWorktree(id, input: input, attachments: attachments, from: .composer(draft: original), agent: agent)
        }
    }

    /// The children of `parentId` a comparison shows (`CompareRun.group`).
    func compareGroup(parentId: String) -> [DelegationChildRow] {
        CompareRun.group(children: delegationChildren, parentId: parentId)
    }
}
