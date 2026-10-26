import Foundation

/// Git for making workspaces: the user's own git, configuration and credentials, never a prompt.
public enum GitTool {
    public static var executable: URL? {
        DelegationGit.executable ?? ToolkitProcess.resolveExecutable("git", environment: ProcessInfo.processInfo.environment)
    }

    /// No prompt can appear: a credential helper or ssh-agent answers, or git fails at once. An empty
    /// GIT_ASKPASS switches git's askpass off (it would otherwise fall back to core.askPass and SSH_ASKPASS
    /// and could open a window); git then fails with "terminal prompts disabled".
    public static func environment(base: [String: String] = ProcessInfo.processInfo.environment) async -> [String: String] {
        var environment = base.filter { $0.key != "GIT_DIR" && $0.key != "GIT_WORK_TREE" && $0.key != "GIT_INDEX_FILE" }
        environment["GIT_TERMINAL_PROMPT"] = "0"; environment["GCM_INTERACTIVE"] = "never"; environment["LC_ALL"] = "C"
        environment["GIT_ASKPASS"] = ""; environment["SSH_ASKPASS_REQUIRE"] = "never"
        if environment["GIT_SSH_COMMAND"] == nil, environment["GIT_SSH"] == nil, await configuredSSHCommand() == nil { environment["GIT_SSH_COMMAND"] = "ssh -o BatchMode=yes" }
        return environment
    }

    /// The user's own core.sshCommand (system or global configuration; run from home, so no repository is read).
    private static func configuredSSHCommand() async -> String? {
        guard let executable else { return nil }
        let result = try? await ProcessCapture.run(executable: executable, arguments: ["config", "--get", "core.sshCommand"], environment: nil, cwd: FileManager.default.homeDirectoryForCurrentUser, input: nil, timeout: 5, maximumBytes: 4096)
        let value = result.map { DelegationGit.line($0.stdout) } ?? ""
        return result?.exitCode == 0 && !value.isEmpty ? value : nil
    }

    public enum MakeFolderResult: Equatable, Sendable { case made, madeButGitInitFailed(String), failed(String) }

    public static func makeFolder(at path: String, gitInit: Bool) async -> MakeFolderResult {
        do { try FileManager.default.createDirectory(atPath: path, withIntermediateDirectories: false) }
        catch { return .failed(error.localizedDescription) }
        guard gitInit else { return .made }
        guard let executable else { return .madeButGitInitFailed(L("workspace.git.error.gitMissing")) }
        let gitEnvironment = await environment()
        do {
            let result = try await ProcessCapture.run(executable: executable, arguments: ["init", "-q"], environment: gitEnvironment, cwd: URL(fileURLWithPath: path), input: nil, timeout: 20, maximumBytes: 65_536)
            return result.exitCode == 0 ? .made : .madeButGitInitFailed(DelegationGit.message(result))
        } catch {
            return .madeButGitInitFailed(error.localizedDescription)
        }
    }

    public enum ProbeResult: Equatable, Sendable { case reachable, failed(WorkspaceCreate.GitErrorKind, String) }

    public static func probe(_ raw: String) async -> ProbeResult {
        let url = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard WorkspaceCreate.looksLikeGitURL(url) else { return .failed(.invalidURL, "") }
        guard let executable else { return .failed(.gitMissing, "") }
        let gitEnvironment = await environment()
        do {
            let result = try await ProcessCapture.run(executable: executable, arguments: ["ls-remote", "--heads", "--", url], environment: gitEnvironment, cwd: FileManager.default.temporaryDirectory, input: nil, timeout: 20, maximumBytes: 1_048_576)
            if result.exitCode == 0 { return .reachable }
            let text = String(decoding: result.stderr, as: UTF8.self)
            return .failed(WorkspaceCreate.errorKind(stderr: text), DelegationGit.message(result))
        } catch {
            return .failed(.network, error.localizedDescription)
        }
    }
}

/// `git clone --progress` into a folder that must not exist yet; progress from its stderr, cancel stops it,
/// and a cancelled or failed clone leaves no folder behind.
public final class GitCloneJob: @unchecked Sendable {
    public enum CloneResult: Equatable, Sendable { case done, cancelled, failed(WorkspaceCreate.GitErrorKind, String) }

    private let url: String, target: String
    private let onProgress: @Sendable (WorkspaceCreate.CloneProgress) -> Void
    private let lock = NSLock()
    private var child: NativeChildProcess?
    private var cancelled = false
    private var spawnError: String?

    /// `progress` is called on the child process's reader queue, not the main actor: a UI must hop to the main actor itself.
    public init(url: String, target: String, progress: @escaping @Sendable (WorkspaceCreate.CloneProgress) -> Void) {
        self.url = url.trimmingCharacters(in: .whitespacesAndNewlines); self.target = target; self.onProgress = progress
    }

    public func cancel() {
        lock.lock(); cancelled = true; let child = self.child; lock.unlock()
        child?.stop()
    }

    private var isCancelled: Bool { lock.lock(); defer { lock.unlock() }; return cancelled }

    /// Remembers the child; true when a cancel arrived before it was known, so the caller stops it.
    private func attach(_ started: NativeChildProcess) -> Bool {
        lock.lock(); defer { lock.unlock() }
        child = started; return cancelled
    }

    private func detach(spawnError message: String? = nil) {
        lock.lock(); child = nil; if let message { spawnError = message }; lock.unlock()
    }

    private var spawnFailure: String? { lock.lock(); defer { lock.unlock() }; return spawnError }

    public func run() async -> CloneResult {
        if isCancelled { return .cancelled }
        guard WorkspaceCreate.looksLikeGitURL(url) else { return .failed(.invalidURL, "") }
        guard let executable = GitTool.executable else { return .failed(.gitMissing, "") }
        if FileManager.default.fileExists(atPath: target) { return .failed(.other, L("workspace.name.error.exists")) }
        let parent = (target as NSString).deletingLastPathComponent
        var isFolder: ObjCBool = false
        guard FileManager.default.fileExists(atPath: parent, isDirectory: &isFolder), isFolder.boolValue else { return .failed(.other, L("workspace.location.error.missing")) }
        let environment = await GitTool.environment()
        let errors = StderrLines()
        let code: Int32 = await withCheckedContinuation { continuation in
            do {
                let started = try NativeChildProcess(
                    executable: executable, arguments: ["clone", "--progress", "--", url, target], environment: environment,
                    cwd: URL(fileURLWithPath: parent),
                    stdout: { _ in },
                    stderr: { [onProgress] data in for line in errors.append(data) { if let p = WorkspaceCreate.progress(line: line) { onProgress(p) } } },
                    exited: { continuation.resume(returning: $0) })
                let stop = attach(started)
                started.write(Data(), closeAfter: true)
                if stop { started.stop() }
            } catch {
                detach(spawnError: error.localizedDescription)
                continuation.resume(returning: -1)
            }
        }
        detach()
        if code == 0 && !isCancelled { return .done }
        try? FileManager.default.removeItem(atPath: target)
        if isCancelled { return .cancelled }
        if let failure = spawnFailure { return .failed(.other, failure) }
        let text = errors.text
        return .failed(WorkspaceCreate.errorKind(stderr: text), text.split(separator: "\n").last.map(String.init) ?? "")
    }
}

/// stderr split on `\r` and `\n` (git redraws its progress line with `\r`); keeps the last 64 KiB of text.
private final class StderrLines: @unchecked Sendable {
    private let lock = NSLock(); private var pending = ""; private var all = ""
    func append(_ data: Data) -> [String] {
        lock.lock(); defer { lock.unlock() }
        pending += String(decoding: data, as: UTF8.self)
        var lines: [String] = []
        while let cut = pending.firstIndex(where: { $0 == "\r" || $0 == "\n" }) {
            let line = String(pending[..<cut]); pending = String(pending[pending.index(after: cut)...])
            if !line.isEmpty { lines.append(line); all += line + "\n" }
        }
        if all.utf8.count > 65_536 { all = String(all.suffix(32_768)) }
        return lines
    }
    var text: String { lock.lock(); defer { lock.unlock() }; return (all + pending).trimmingCharacters(in: .whitespacesAndNewlines) }
}
