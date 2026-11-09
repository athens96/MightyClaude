import Foundation

/// 비교 실행: one request handed to several agents at once, each in its own
/// child worktree (the worktree menu's children), and their results set side
/// by side so one is merged and the rest discarded. Built on the human
/// children delegation already makes; this only picks who runs and reads
/// what each one changed.
public enum CompareRun {
    /// How many open children a parent may have that the human started (`DelegationChildChoice.Blocked.cap`).
    public static let childCap = 3

    /// The agents to run the request: those that can start a child now, the
    /// parent's own agent first, no more than the room left under the cap.
    public static func agents(choices: [DelegationChildChoice], parent: String, openChildren: Int, cap: Int = childCap) -> [String] {
        let ready = choices.filter { $0.blocked == nil }.map(\.agent)
        let ordered = ready.filter { $0 == parent } + ready.filter { $0 != parent }
        return Array(ordered.prefix(max(0, cap - openChildren)))
    }

    /// Whether a compare run is offered: at least two agents could run it.
    public static func offered(choices: [DelegationChildChoice], parent: String, openChildren: Int) -> Bool {
        agents(choices: choices, parent: parent, openChildren: openChildren).count >= 2
    }

    /// The parent's children a compare compares: the human-started, not yet
    /// discarded or merged ones whose task is the same as another's. The
    /// newest such group, in the order they were made; empty when there is none.
    public static func group(children: [DelegationChildRow], parentId: String) -> [DelegationChildRow] {
        let candidates = children.filter { $0.parentSessionId == parentId && $0.startedByHuman && $0.task != nil && ![.discarded, .merged].contains($0.state) }
        guard let task = candidates.reversed().first(where: { row in candidates.filter { $0.task == row.task }.count >= 2 })?.task else { return [] }
        return candidates.filter { $0.task == task }
    }

    /// What one child changed against the branch it came from.
    public struct Stat: Equatable, Sendable {
        public var files: Int
        public var insertions: Int
        public var deletions: Int
        public init(files: Int = 0, insertions: Int = 0, deletions: Int = 0) { self.files = files; self.insertions = insertions; self.deletions = deletions }
    }

    /// Reads `git diff --shortstat` ("3 files changed, 10 insertions(+), 2 deletions(-)"); an empty output is no change.
    public static func parseShortstat(_ text: String) -> Stat {
        func number(before word: String) -> Int {
            guard let range = text.range(of: "(\\d+) \(word)", options: .regularExpression) else { return 0 }
            return Int(text[range].split(separator: " ").first ?? "") ?? 0
        }
        return Stat(files: number(before: "files? changed"), insertions: number(before: "insertions?"), deletions: number(before: "deletions?"))
    }

    /// What the child's branch `mighty/<id>` changed since it left `parentBranch`, read in the parent's checkout; nil when git cannot tell.
    public static func stat(checkout: String, parentBranch: String, childId: String) async -> Stat? {
        guard checkout.hasPrefix("/"), !parentBranch.isEmpty, CoreValidation.identifier(childId) else { return nil }
        let environment = await GitTool.environment()
        guard let git = GitTool.resolveExecutable(environment: environment),
              let result = try? await ProcessCapture.run(executable: git, arguments: ["-C", checkout, "diff", "--shortstat", "\(parentBranch)...mighty/\(childId)"],
                                                        environment: environment, cwd: URL(fileURLWithPath: checkout), timeout: 10, maximumBytes: 65_536),
              result.exitCode == 0 else { return nil }
        return parseShortstat(String(decoding: result.stdout, as: UTF8.self))
    }
}
