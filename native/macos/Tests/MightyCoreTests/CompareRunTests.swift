import Foundation
import Testing
@testable import MightyCore

/// 비교 실행 (`CompareRun.swift`): who runs the request, which children are compared, and what each changed.
struct CompareRunTests {
    private let choices = [
        DelegationChildChoice(agent: "claude", mode: "acceptEdits", blocked: nil),
        DelegationChildChoice(agent: "codex", mode: "workspace-write", blocked: nil),
        DelegationChildChoice(agent: "antigravity", mode: nil, blocked: .signedOut),
    ]

    @Test func readyAgentsRunTheParentsOwnFirstWithinTheCap() {
        #expect(CompareRun.agents(choices: choices, parent: "codex", openChildren: 0) == ["codex", "claude"])
        #expect(CompareRun.agents(choices: choices, parent: "claude", openChildren: 2) == ["claude"])
        #expect(CompareRun.offered(choices: choices, parent: "claude", openChildren: 1))
        #expect(!CompareRun.offered(choices: choices, parent: "claude", openChildren: 2))
        let all = choices.map { DelegationChildChoice(agent: $0.agent, mode: "m", blocked: nil) }
        #expect(CompareRun.agents(choices: all, parent: "antigravity", openChildren: 0) == ["antigravity", "claude", "codex"])
    }

    @Test func theNewestGroupOfSameTaskChildrenIsCompared() {
        func row(_ id: String, _ task: String?, _ state: ChildState = .reported, parent: String = "p", human: Bool = true) -> DelegationChildRow {
            DelegationChildRow(id: id, parentSessionId: parent, state: state, task: task, parentBranch: "main", startedByHuman: human)
        }
        let rows = [
            row("a1", "old task"), row("a2", "old task"),
            row("b1", "new task"), row("b2", "new task", .running), row("b3", "new task", .discarded),
            row("c1", "solo"), row("d1", "new task", parent: "other"), row("e1", "new task", human: false),
        ]
        #expect(CompareRun.group(children: rows, parentId: "p").map(\.id) == ["b1", "b2"])
        #expect(CompareRun.group(children: [row("c1", "solo")], parentId: "p").isEmpty)
    }

    @Test func shortstatIsRead() {
        #expect(CompareRun.parseShortstat(" 3 files changed, 10 insertions(+), 2 deletions(-)\n") == CompareRun.Stat(files: 3, insertions: 10, deletions: 2))
        #expect(CompareRun.parseShortstat(" 1 file changed, 1 insertion(+)") == CompareRun.Stat(files: 1, insertions: 1, deletions: 0))
        #expect(CompareRun.parseShortstat(" 2 files changed, 4 deletions(-)") == CompareRun.Stat(files: 2, insertions: 0, deletions: 4))
        #expect(CompareRun.parseShortstat("") == CompareRun.Stat())
    }
}
