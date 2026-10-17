import Foundation

/// How the toolkit finds and runs its commands. The executable is looked up on
/// the PATH the caller passes, which the app takes from the login shell like the
/// agent CLIs (`CLIEnvironmentResolver`). The command runs in its own process
/// group like `ProcessCapture`'s: its output is read while it runs, so one that
/// prints more than a pipe holds never blocks; past the deadline its group gets
/// SIGTERM, then SIGKILL; and a background process left holding the pipe is not
/// waited for.
public enum ToolkitProcess {
    /// A clone, a Homebrew build or an npm install can take a while; nothing more.
    public static let defaultTimeout: TimeInterval = 15 * 60
    /// Output kept per command; the rest is read and dropped.
    static let maximumOutputBytes = 8 * 1_048_576

    /// An absolute path is taken as is when it is executable; a bare name is
    /// looked up on `environment["PATH"]`. A relative path with a slash is refused.
    public static func resolveExecutable(_ name: String, environment: [String: String]) -> URL? {
        if name.hasPrefix("/") { return FileManager.default.isExecutableFile(atPath: name) ? URL(fileURLWithPath: name) : nil }
        guard !name.isEmpty, !name.contains("/") else { return nil }
        for directory in (environment["PATH"] ?? "").split(separator: ":") where !directory.isEmpty {
            let url = URL(fileURLWithPath: String(directory)).appendingPathComponent(name)
            if FileManager.default.isExecutableFile(atPath: url.path) { return url }
        }
        return nil
    }

    /// Runs `argv` with `environment`, stdout and stderr in one stream, nothing on
    /// stdin, and returns its exit code and output once it has ended. Past
    /// `timeout` the process group is stopped and the result is a failure.
    public static func run(_ argv: [String], environment: [String: String], timeout: TimeInterval = defaultTimeout) -> ToolkitCommandOutput {
        guard let name = argv.first, let executable = resolveExecutable(name, environment: environment) else {
            return .failure(output: "executable not found")
        }
        let output = Collected(limit: maximumOutputBytes)
        let ended = DispatchSemaphore(value: 0)
        let exitCode = Collected.Code()
        let child: NativeChildProcess
        do {
            child = try NativeChildProcess(executable: executable, arguments: Array(argv.dropFirst()), environment: environment, cwd: FileManager.default.temporaryDirectory,
                                           stdout: { output.append($0) }, stderr: { output.append($0) },
                                           exited: { code in exitCode.set(code); ended.signal() })
        } catch { return .failure(output: error.localizedDescription) }
        child.write(Data(), closeAfter: true)
        if ended.wait(timeout: .now() + max(0.01, timeout)) == .timedOut {
            output.timedOut = true
            child.stop()
            // The reaper reports the end once the group is gone and the pipes are drained or given up.
            if ended.wait(timeout: .now() + 5) == .timedOut { child.kill() }
        }
        let text = output.text + (output.timedOut ? "\n" + L("process.error.timeout") : "")
        return ToolkitCommandOutput(exitCode: output.timedOut ? -1 : exitCode.value, output: text)
    }

    private final class Collected: @unchecked Sendable {
        final class Code: @unchecked Sendable {
            private let lock = NSLock(); private var code: Int32 = -1
            func set(_ value: Int32) { lock.lock(); code = value; lock.unlock() }
            var value: Int32 { lock.lock(); defer { lock.unlock() }; return code }
        }
        private let lock = NSLock()
        private var data = Data()
        private let limit: Int
        var timedOut = false
        init(limit: Int) { self.limit = limit }
        func append(_ chunk: Data) {
            lock.lock(); defer { lock.unlock() }
            if data.count < limit { data.append(chunk.prefix(limit - data.count)) }
        }
        var text: String { lock.lock(); defer { lock.unlock() }; return String(decoding: data, as: UTF8.self) }
    }
}
