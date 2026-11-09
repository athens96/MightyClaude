import Foundation
import Testing
@testable import MightyCore

/// The commit sheet's git (`GitWorkbench.swift`): status and hosting parsers,
/// and a commit in a throwaway repository that takes only the chosen paths.
struct GitWorkbenchTests {
    @Test func porcelainV2EntriesBecomeChanges() {
        let raw = "1 .M N... 100644 100644 100644 aaa bbb src/a b.swift\u{0}"
            + "2 R. N... 100644 100644 100644 aaa bbb R100 new.txt\u{0}old.txt\u{0}"
            + "u UU N... 100644 100644 100644 100644 a b c conflict.txt\u{0}"
            + "? notes/new file.md\u{0}"
            + "! ignored.log\u{0}"
            + "1 D. N... 100644 000000 000000 aaa 000 gone.txt\u{0}"
        let changes = GitWorkbench.parseStatus(Data(raw.utf8))
        #expect(changes.map(\.path) == ["src/a b.swift", "new.txt", "conflict.txt", "notes/new file.md", "gone.txt"])
        #expect(changes.map(\.mark) == ["M", "R", "!", "?", "D"])
        #expect(changes[1].originalPath == "old.txt")
        #expect(changes[0].index == " " && changes[0].worktree == "M")
    }

    @Test func gitHubChecksRollUpToOneVerdict() throws {
        func parse(_ rollup: String) -> GitReviewRequest.Checks? {
            GitWorkbench.parseGitHub(Data(#"{"number":7,"url":"https://github.com/o/r/pull/7","state":"OPEN","statusCheckRollup":\#(rollup)}"#.utf8))?.checks
        }
        #expect(parse("[]") == GitReviewRequest.Checks.none)
        #expect(parse(#"[{"status":"COMPLETED","conclusion":"SUCCESS"},{"state":"SUCCESS"}]"#) == .passed)
        #expect(parse(#"[{"status":"IN_PROGRESS","conclusion":""},{"status":"COMPLETED","conclusion":"SUCCESS"}]"#) == .pending)
        #expect(parse(#"[{"status":"IN_PROGRESS"},{"status":"COMPLETED","conclusion":"FAILURE"}]"#) == .failed)
        #expect(parse(#"[{"state":"ERROR"}]"#) == .failed)
        let request = try #require(GitWorkbench.parseGitHub(Data(#"{"number":7,"url":"u","state":"MERGED"}"#.utf8)))
        #expect(request.number == 7 && request.state == "merged" && request.checks == .none)
        #expect(GitWorkbench.parseGitHub(Data("no".utf8)) == nil)
    }

    @Test func gitLabMergeRequestsReadTheirPipeline() throws {
        let request = try #require(GitWorkbench.parseGitLab(Data(#"{"iid":12,"web_url":"https://gitlab.com/o/r/-/merge_requests/12","state":"opened","head_pipeline":{"status":"failed"}}"#.utf8)))
        #expect(request == GitReviewRequest(number: 12, url: "https://gitlab.com/o/r/-/merge_requests/12", state: "open", checks: .failed))
        #expect(GitWorkbench.parseGitLab(Data(#"{"iid":1,"web_url":"u","state":"merged","pipeline":{"status":"running"}}"#.utf8))?.checks == .pending)
        #expect(GitWorkbench.host(remoteURL: "git@gitlab.example.com:o/r.git") == .gitlab)
        #expect(GitWorkbench.host(remoteURL: "https://github.com/o/r") == .github)
    }

    @Test func aLogTailDropsColoursAndKeepsTheLastLines() {
        let log = (1...10).map { "\u{1B}[31mline \($0)\u{1B}[0m" }.joined(separator: "\n")
        #expect(GitWorkbench.tail(log, lines: 2) == "line 9\nline 10")
    }

    @Test func aCommitTakesOnlyTheChosenPaths() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-git-" + UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let git = try #require(GitTool.executable)
        func run(_ args: [String]) async throws -> ProcessResult {
            try await ProcessCapture.run(executable: git, arguments: ["-C", root.path, "-c", "user.name=t", "-c", "user.email=t@t", "-c", "commit.gpgsign=false"] + args, cwd: root, timeout: 20)
        }
        _ = try await run(["init", "-q"])
        try Data("a\n".utf8).write(to: root.appendingPathComponent("a.txt"))
        try Data("b\n".utf8).write(to: root.appendingPathComponent("b.txt"))
        _ = try await run(["add", "."]); _ = try await run(["commit", "-qm", "base"])
        try Data("a2\n".utf8).write(to: root.appendingPathComponent("a.txt"))
        try Data("b2\n".utf8).write(to: root.appendingPathComponent("b.txt"))
        try Data("c\n".utf8).write(to: root.appendingPathComponent("c.txt"))
        _ = try await run(["config", "user.name", "t"]); _ = try await run(["config", "user.email", "t@t"]); _ = try await run(["config", "commit.gpgsign", "false"])

        let before = try await GitWorkbench.status(root: root)
        #expect(Set(before.map(\.path)) == ["a.txt", "b.txt", "c.txt"])
        let sha = try await GitWorkbench.commit(root: root, paths: ["a.txt", "c.txt"], message: "  change a, add c \n")
        #expect(!sha.isEmpty)
        let after = try await GitWorkbench.status(root: root)
        #expect(after.map(\.path) == ["b.txt"])
        let subject = try await run(["log", "-1", "--format=%s"])
        #expect(String(decoding: subject.stdout, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines) == "change a, add c")
        await #expect(throws: GitWorkbench.Failure.self) { try await GitWorkbench.commit(root: root, paths: ["b.txt"], message: "  ") }
    }
}
