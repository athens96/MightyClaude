import Foundation

/// How the toolkit finds and runs its commands. The executable is looked up on
/// the PATH the caller passes, which the app takes from the login shell like the
/// agent CLIs (`CLIEnvironmentResolver`), and the output is read while the
/// process runs, so a command that prints more than a pipe holds never blocks.
public enum ToolkitProcess {
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

    /// Runs `argv` with `environment`, stdout and stderr in one stream, and
    /// returns its exit code and output once the process has ended and the
    /// stream is drained. Nothing is read from stdin.
    public static func run(_ argv: [String], environment: [String: String]) -> ToolkitCommandOutput {
        guard let name = argv.first, let executable = resolveExecutable(name, environment: environment) else {
            return .failure(output: "executable not found")
        }
        let process = Process()
        process.executableURL = executable
        process.arguments = Array(argv.dropFirst())
        process.environment = environment
        process.standardInput = FileHandle.nullDevice
        let pipe = Pipe()
        process.standardOutput = pipe
        process.standardError = pipe
        do { try process.run() } catch { return .failure(output: error.localizedDescription) }
        // Drain on another thread before waiting: a child that fills the pipe
        // would otherwise wait for a reader that is itself waiting for the child.
        let output = DrainedOutput()
        let drained = DispatchSemaphore(value: 0)
        let reader = pipe.fileHandleForReading
        DispatchQueue.global(qos: .utility).async {
            output.data = reader.readDataToEndOfFile()
            drained.signal()
        }
        process.waitUntilExit()
        drained.wait()
        return ToolkitCommandOutput(exitCode: process.terminationStatus, output: String(decoding: output.data, as: UTF8.self))
    }

    private final class DrainedOutput: @unchecked Sendable { var data = Data() }
}
