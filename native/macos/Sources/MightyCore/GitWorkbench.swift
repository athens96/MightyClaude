import Foundation

/// One changed path of `git status --porcelain=v2 -z`: its index (`X`) and
/// work tree (`Y`) status letters, and for a rename or copy the path it came from.
public struct GitChange: Equatable, Sendable, Identifiable {
    public var id: String { path }
    public let path: String
    public let originalPath: String?
    public let index: Character
    public let worktree: Character
    public var untracked: Bool { index == "?" }
    public var conflicted: Bool { index == "U" || worktree == "U" || (index == "A" && worktree == "A") || (index == "D" && worktree == "D") }

    /// One letter to show: what happened to the file overall.
    public var mark: String {
        if untracked { return "?" }
        if conflicted { return "!" }
        if index == "R" || worktree == "R" { return "R" }
        if index == "A" { return "A" }
        if index == "D" || worktree == "D" { return "D" }
        return "M"
    }
}

/// The pull request (GitHub) or merge request (GitLab) of the current branch.
public struct GitReviewRequest: Equatable, Sendable {
    public enum Checks: Equatable, Sendable { case none, pending, passed, failed }
    public let number: Int
    public let url: String
    /// `open`, `merged`, `closed`, lower-cased.
    public let state: String
    public let checks: Checks
}

/// The commit sheet's git (the workspace badge's 커밋·PR): read the changed
/// files, commit the chosen ones, push, and through `gh`/`glab` show, open
/// and make the branch's pull request and read the failing CI log. Git runs
/// as `GitTool` sets it up (the user's own configuration, credentials and
/// hooks, never a prompt).
public enum GitWorkbench {
    public enum Host: String, Sendable { case github, gitlab }

    /// Parses `git status --porcelain=v2 -z` (NUL separated; a rename's
    /// entry is followed by the path it came from). Ignored entries are skipped.
    public static func parseStatus(_ data: Data) -> [GitChange] {
        let fields = data.split(separator: 0, omittingEmptySubsequences: false).map { String(decoding: $0, as: UTF8.self) }
        var changes: [GitChange] = []
        var index = 0
        while index < fields.count {
            let field = fields[index]; index += 1
            guard let kind = field.first else { continue }
            switch kind {
            case "1", "2", "u":
                // "1 XY sub mH mI mW hH hI path", "2 XY sub mH mI mW hH hI Xscore path", "u XY sub m1 m2 m3 mW h1 h2 h3 path"
                let skip = kind == "1" ? 8 : kind == "2" ? 9 : 10
                let parts = field.split(separator: " ", maxSplits: skip, omittingEmptySubsequences: false)
                guard parts.count == skip + 1, parts[1].count == 2 else { continue }
                let xy = Array(parts[1])
                var original: String?
                if kind == "2", index < fields.count { original = fields[index]; index += 1 }
                changes.append(GitChange(path: String(parts[skip]), originalPath: original, index: xy[0] == "." ? " " : xy[0], worktree: xy[1] == "." ? " " : xy[1]))
            case "?":
                changes.append(GitChange(path: String(field.dropFirst(2)), originalPath: nil, index: "?", worktree: "?"))
            default:
                continue
            }
        }
        return changes
    }

    /// GitHub's `gh pr view --json number,url,state,statusCheckRollup`.
    public static func parseGitHub(_ data: Data) -> GitReviewRequest? {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let number = object["number"] as? Int, let url = object["url"] as? String else { return nil }
        let rollup = object["statusCheckRollup"] as? [[String: Any]] ?? []
        var failed = false, pending = false
        for check in rollup {
            // A check run has status/conclusion; a commit status has state.
            let conclusion = (check["conclusion"] as? String ?? "").uppercased()
            let status = (check["status"] as? String ?? "").uppercased()
            let state = (check["state"] as? String ?? "").uppercased()
            if ["FAILURE", "TIMED_OUT", "CANCELLED", "ACTION_REQUIRED", "STARTUP_FAILURE"].contains(conclusion) || ["FAILURE", "ERROR"].contains(state) { failed = true }
            else if (!status.isEmpty && status != "COMPLETED") || ["PENDING", "EXPECTED"].contains(state) { pending = true }
        }
        let checks: GitReviewRequest.Checks = rollup.isEmpty ? .none : failed ? .failed : pending ? .pending : .passed
        return GitReviewRequest(number: number, url: url, state: (object["state"] as? String ?? "").lowercased(), checks: checks)
    }

    /// GitLab's `glab mr view --output json`.
    public static func parseGitLab(_ data: Data) -> GitReviewRequest? {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let number = object["iid"] as? Int, let url = object["web_url"] as? String else { return nil }
        let pipeline = (object["head_pipeline"] as? [String: Any]) ?? (object["pipeline"] as? [String: Any])
        let status = (pipeline?["status"] as? String ?? "").lowercased()
        let checks: GitReviewRequest.Checks
        switch status {
        case "": checks = .none
        case "success", "skipped", "manual": checks = .passed
        case "failed", "canceled": checks = .failed
        default: checks = .pending
        }
        var state = (object["state"] as? String ?? "").lowercased()
        if state == "opened" { state = "open" }
        return GitReviewRequest(number: number, url: url, state: state, checks: checks)
    }

    /// GitLab when the remote's host names it, else GitHub.
    public static func host(remoteURL: String) -> Host {
        remoteURL.lowercased().contains("gitlab") ? .gitlab : .github
    }

    /// The last `lines` lines of a log, its ANSI colour codes taken out.
    public static func tail(_ log: String, lines: Int) -> String {
        let clean = log.replacingOccurrences(of: "\u{1B}\\[[0-9;]*[A-Za-z]", with: "", options: .regularExpression)
        return clean.split(separator: "\n", omittingEmptySubsequences: false).suffix(lines).joined(separator: "\n")
            .trimmingCharacters(in: .whitespacesAndNewlines)
    }

    // MARK: - Running git, gh and glab

    public struct Failure: Error, Equatable, Sendable { public let message: String }

    private static func git(_ root: URL, _ arguments: [String], timeout: TimeInterval = 20) async throws -> ProcessResult {
        let environment = await GitTool.environment()
        guard let executable = GitTool.resolveExecutable(environment: environment) else { throw Failure(message: L("workspace.git.error.gitMissing")) }
        return try await ProcessCapture.run(executable: executable, arguments: ["-C", root.path] + arguments, environment: environment, cwd: root, timeout: timeout, maximumBytes: 4 * 1_048_576)
    }

    private static func checked(_ result: ProcessResult) throws -> ProcessResult {
        guard result.exitCode == 0 else { throw Failure(message: DelegationGit.message(result)) }
        return result
    }

    public static func status(root: URL) async throws -> [GitChange] {
        let result = try checked(await git(root, ["-c", "core.quotepath=false", "status", "--porcelain=v2", "-z", "--untracked-files=all"]))
        return parseStatus(result.stdout)
    }

    /// Commits exactly `paths` (new, changed and deleted alike) with `message`;
    /// whatever else is staged stays staged and out of this commit. The
    /// repository's hooks run. Returns the new commit's short hash.
    public static func commit(root: URL, paths: [String], message: String) async throws -> String {
        let message = message.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !paths.isEmpty, !message.isEmpty else { throw Failure(message: L("git.commit.error.empty")) }
        _ = try checked(await git(root, ["add", "-A", "--"] + paths))
        _ = try checked(await git(root, ["commit", "--only", "-m", message, "--"] + paths, timeout: 120))
        let head = try checked(await git(root, ["rev-parse", "--short", "HEAD"]))
        return DelegationGit.line(head.stdout)
    }

    /// Pushes the branch, setting its upstream to `origin` the first time.
    public static func push(root: URL) async throws {
        let upstream = try await git(root, ["rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}"])
        let arguments = upstream.exitCode == 0 ? ["push"] : ["push", "-u", "origin", "HEAD"]
        _ = try checked(await git(root, arguments, timeout: 120))
    }

    public static func remoteHost(root: URL) async -> Host? {
        guard let result = try? await git(root, ["remote", "get-url", "origin"]), result.exitCode == 0 else { return nil }
        return host(remoteURL: DelegationGit.line(result.stdout))
    }

    public static func currentBranch(root: URL) async -> String? {
        guard let result = try? await git(root, ["symbolic-ref", "--short", "-q", "HEAD"]), result.exitCode == 0 else { return nil }
        let branch = DelegationGit.line(result.stdout)
        return branch.isEmpty ? nil : branch
    }

    /// `gh` or `glab` on the login shell's PATH.
    public static func hostingCLI(_ host: Host) async -> URL? {
        ToolkitProcess.resolveExecutable(host == .github ? "gh" : "glab", environment: await GitTool.loginEnvironment())
    }

    private static func hosting(_ host: Host, root: URL, _ arguments: [String], timeout: TimeInterval = 20) async throws -> ProcessResult {
        guard let executable = await hostingCLI(host) else { throw Failure(message: L(host == .github ? "git.pr.error.ghMissing" : "git.pr.error.glabMissing")) }
        var environment = await GitTool.environment()
        environment["GH_PROMPT_DISABLED"] = "1"; environment["NO_COLOR"] = "1"; environment["GLAB_NO_PROMPT"] = "1"
        return try await ProcessCapture.run(executable: executable, arguments: arguments, environment: environment, cwd: root, timeout: timeout, maximumBytes: 4 * 1_048_576)
    }

    /// The branch's open (or last) request; nil when there is none or the CLI cannot tell.
    public static func reviewRequest(root: URL, host: Host) async -> GitReviewRequest? {
        switch host {
        case .github:
            guard let result = try? await hosting(.github, root: root, ["pr", "view", "--json", "number,url,state,statusCheckRollup"]), result.exitCode == 0 else { return nil }
            return parseGitHub(result.stdout)
        case .gitlab:
            guard let result = try? await hosting(.gitlab, root: root, ["mr", "view", "--output", "json"]), result.exitCode == 0 else { return nil }
            return parseGitLab(result.stdout)
        }
    }

    /// Pushes, then opens the request from the branch's commits (title and
    /// body filled from them). Returns what the CLI printed (the request's address).
    public static func createReviewRequest(root: URL, host: Host) async throws -> String {
        try await push(root: root)
        let arguments = host == .github ? ["pr", "create", "--fill"] : ["mr", "create", "--fill", "--yes"]
        let result = try await hosting(host, root: root, arguments, timeout: 60)
        guard result.exitCode == 0 else { throw Failure(message: DelegationGit.message(result)) }
        return DelegationGit.line(result.stdout)
    }

    /// The failing part of the branch's latest CI run (GitHub Actions), its last `lines` lines; nil when there is none.
    public static func failedCheckLog(root: URL, branch: String, lines: Int = 80) async -> String? {
        guard let list = try? await hosting(.github, root: root, ["run", "list", "--branch", branch, "--limit", "1", "--json", "databaseId,conclusion"]),
              list.exitCode == 0,
              let runs = try? JSONSerialization.jsonObject(with: list.stdout) as? [[String: Any]],
              let run = runs.first, let id = run["databaseId"] as? Int, (run["conclusion"] as? String) == "failure",
              let log = try? await hosting(.github, root: root, ["run", "view", String(id), "--log-failed"], timeout: 60), log.exitCode == 0 else { return nil }
        let text = tail(String(decoding: log.stdout, as: UTF8.self), lines: lines)
        return text.isEmpty ? nil : text
    }
}
