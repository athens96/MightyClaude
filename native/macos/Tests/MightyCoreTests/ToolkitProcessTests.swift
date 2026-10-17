import Foundation
import Testing
@testable import MightyCore

/// The toolkit's commands are found on the login shell's PATH it is given, not
/// the app's own, and a command that prints more than a pipe holds still ends.
struct ToolkitProcessTests {
    private func temporary() throws -> URL {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("toolkit-process-" + UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }

    private func script(_ name: String, in directory: URL, body: String) throws {
        let url = directory.appendingPathComponent(name)
        try Data(("#!/bin/sh\n" + body + "\n").utf8).write(to: url)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: url.path)
    }

    @Test func executablesAreFoundOnTheGivenPathOnly() throws {
        let root = try temporary(); defer { try? FileManager.default.removeItem(at: root) }
        let name = "mighty-toolkit-fixture-" + UUID().uuidString.prefix(8)
        try script(String(name), in: root, body: "echo found")
        #expect(ToolkitProcess.resolveExecutable(String(name), environment: ["PATH": "/usr/bin:" + root.path])?.path == root.appendingPathComponent(String(name)).path)
        // The app's own PATH does not hold the fixture folder, and an empty PATH finds nothing.
        #expect(ToolkitProcess.resolveExecutable(String(name), environment: ProcessInfo.processInfo.environment) == nil)
        #expect(ToolkitProcess.resolveExecutable(String(name), environment: [:]) == nil)
        // A relative path is never resolved against the working folder.
        #expect(ToolkitProcess.resolveExecutable("./" + name, environment: ["PATH": root.path]) == nil)
        let output = ToolkitProcess.run([String(name)], environment: ["PATH": root.path])
        #expect(output.exitCode == 0 && output.output == "found\n")
    }

    @Test func theCommandRunsWithTheGivenEnvironment() throws {
        let root = try temporary(); defer { try? FileManager.default.removeItem(at: root) }
        try script("show-env", in: root, body: "printf '%s' \"$MIGHTY_FIXTURE\"")
        let output = ToolkitProcess.run(["show-env"], environment: ["PATH": root.path + ":/usr/bin:/bin", "MIGHTY_FIXTURE": "login-shell"])
        #expect(output.output == "login-shell")
    }

    @Test func largeOutputIsDrainedWhileTheCommandRuns() throws {
        let root = try temporary(); defer { try? FileManager.default.removeItem(at: root) }
        // 2 MB on stdout and stderr together, far past a pipe's 64 KB buffer.
        try script("chatty", in: root, body: "/usr/bin/head -c 1048576 /dev/zero; /usr/bin/head -c 1048576 /dev/zero >&2; exit 3")
        let output = ToolkitProcess.run(["chatty"], environment: ["PATH": root.path + ":/usr/bin:/bin"])
        #expect(output.exitCode == 3)
        #expect(output.output.utf8.count == 2 * 1_048_576)
    }

    @Test func aMissingExecutableIsAFailureNotACrash() {
        let output = ToolkitProcess.run(["mighty-no-such-tool"], environment: ["PATH": "/nonexistent"])
        #expect(output.exitCode != 0)
    }

    @Test func aCommandPastItsDeadlineIsStoppedWithItsGroup() throws {
        let root = try temporary(); defer { try? FileManager.default.removeItem(at: root) }
        let pidFile = root.appendingPathComponent("child.pid")
        // A child of the command, in the same group, must not outlive the stop.
        try script("slow", in: root, body: "/bin/sleep 60 & echo $! > '\(pidFile.path)'; wait")
        let started = Date()
        let output = ToolkitProcess.run(["slow"], environment: ["PATH": root.path + ":/usr/bin:/bin"], timeout: 3)
        #expect(Date().timeIntervalSince(started) < 15)
        #expect(output.exitCode != 0)
        let pid = try #require(Int32(String(decoding: try Data(contentsOf: pidFile), as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)))
        Thread.sleep(forTimeInterval: 0.5)
        #expect(Darwin.kill(pid, 0) != 0)
    }

    @Test func aBackgroundProcessHoldingThePipeIsNotWaitedFor() throws {
        let root = try temporary(); defer { try? FileManager.default.removeItem(at: root) }
        // The command ends at once; what it left behind keeps stdout open.
        try script("leaves-child", in: root, body: "/bin/sleep 30 & echo done")
        let started = Date()
        let output = ToolkitProcess.run(["leaves-child"], environment: ["PATH": root.path + ":/usr/bin:/bin"], timeout: 60)
        #expect(Date().timeIntervalSince(started) < 10)
        #expect(output.exitCode == 0 && output.output.hasPrefix("done"))
    }
}
