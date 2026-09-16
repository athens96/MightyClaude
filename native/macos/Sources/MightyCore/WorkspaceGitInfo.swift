import Foundation

/// Local, read-only repository metadata. No remote URL or file names leave
/// this probe, and no fetch, index refresh, or configured fsmonitor runs.
public struct WorkspaceGitInfo: Equatable, Sendable {
    public var branch: String
    public var revision: String?
    public var isDirty: Bool
    public var ahead: Int?
    public var behind: Int?

    public var label: String { branch == "(detached)" ? "HEAD · \(revision.map { String($0.prefix(7)) } ?? "분리됨")" : branch }

    public static func parse(_ output: String) -> WorkspaceGitInfo? {
        var branch: String?, revision: String?, ahead: Int?, behind: Int?
        var dirty = false
        for line in output.split(separator: "\n") {
            if line.hasPrefix("# branch.head ") { branch = String(line.dropFirst(14)) }
            else if line.hasPrefix("# branch.oid ") {
                let value = String(line.dropFirst(13))
                if value.range(of: "^[a-fA-F0-9]{7,64}$", options: .regularExpression) != nil { revision = value }
            } else if line.hasPrefix("# branch.ab ") {
                let counts = line.dropFirst(12).split(separator: " ")
                if counts.count == 2, counts[0].hasPrefix("+"), counts[1].hasPrefix("-"),
                   let a = Int(counts[0].dropFirst()), let b = Int(counts[1].dropFirst()), a >= 0, b >= 0 {
                    ahead = a; behind = b
                }
            } else if ["1 ", "2 ", "u ", "? "].contains(where: { line.hasPrefix($0) }) { dirty = true }
        }
        guard let branch, !branch.isEmpty, branch.utf8.count <= 4096,
              !branch.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }) else { return nil }
        return WorkspaceGitInfo(branch: branch, revision: revision, isDirty: dirty, ahead: ahead, behind: behind)
    }

    public static func read(path: String) async -> WorkspaceGitInfo? {
        guard !Task.isCancelled, path.hasPrefix("/"), !path.contains("\0") else { return nil }
        let candidates = ["/Library/Developer/CommandLineTools/usr/bin/git", "/opt/homebrew/bin/git", "/usr/local/bin/git", "/usr/bin/git"]
        guard let executable = candidates.first(where: { FileManager.default.isExecutableFile(atPath: $0) }) else { return nil }
        var environment = ProcessInfo.processInfo.environment.filter { !$0.key.hasPrefix("GIT_") }
        environment["GIT_OPTIONAL_LOCKS"] = "0"; environment["GIT_TERMINAL_PROMPT"] = "0"
        environment["LC_ALL"] = "C"
        let args = ["--no-optional-locks", "-c", "core.fsmonitor=false", "-c", "core.hooksPath=/dev/null",
                    "-c", "core.untrackedCache=false", "-c", "gc.auto=0", "-C", path,
                    "status", "--porcelain=v2", "--branch", "--untracked-files=normal"]
        guard let result = try? await ProcessCapture.run(executable: URL(fileURLWithPath: executable), arguments: args,
            environment: environment, cwd: URL(fileURLWithPath: path), timeout: 2, maximumBytes: 262_144),
              !Task.isCancelled, result.exitCode == 0 else { return nil }
        return parse(String(decoding: result.stdout, as: UTF8.self))
    }
}
