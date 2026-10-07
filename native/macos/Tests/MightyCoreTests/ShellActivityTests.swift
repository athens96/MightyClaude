import Darwin
import Foundation
import Testing
@testable import MightyCore

/// A shell pane is running only while a command owns its terminal: the rule, the
/// pid the shell writes as it starts, and the kernel reads behind them.
struct ShellActivityTests {
    @Test func busyOnlyWhileAnotherGroupOwnsTheTerminal() {
        #expect(!ShellActivity.isBusy(shellGroup: 500, foregroundGroup: 500))
        #expect(ShellActivity.isBusy(shellGroup: 500, foregroundGroup: 612))
        // No terminal, or none read: not busy.
        #expect(!ShellActivity.isBusy(shellGroup: 500, foregroundGroup: 0))
        #expect(!ShellActivity.isBusy(shellGroup: 500, foregroundGroup: -1))
    }

    @Test func groupsReadThisProcess() throws {
        let groups = try #require(ShellProcessProbe.groups(pid: getpid()))
        #expect(groups.pgid == getpgrp())
        #expect(groups.ppid == getppid())
        #expect(groups.started > 0)
        #expect(ShellProcessProbe.groups(pid: getpid())?.started == groups.started)
        #expect(ShellProcessProbe.groups(pid: 0) == nil)
        #expect(ShellProcessProbe.groups(pid: 1) == nil)
    }

    @Test func onlyAReadingThatChangesTheStatusIsReported() {
        #expect(ShellActivity.nextStatus(current: "idle", busy: true) == "running")
        #expect(ShellActivity.nextStatus(current: "running", busy: false) == "idle")
        // No change, no report.
        #expect(ShellActivity.nextStatus(current: "idle", busy: false) == nil)
        #expect(ShellActivity.nextStatus(current: "running", busy: true) == nil)
        // A reading that could not be taken leaves the status alone.
        #expect(ShellActivity.nextStatus(current: "running", busy: nil) == nil)
        #expect(ShellActivity.nextStatus(current: "idle", busy: nil) == nil)
        // An exited or failed pane keeps that.
        for status in ["completed", "error"] {
            #expect(ShellActivity.nextStatus(current: status, busy: true) == nil)
            #expect(ShellActivity.nextStatus(current: status, busy: false) == nil)
        }
    }

    /// The pid files' folder is made 0700, and one that is not this user's private
    /// folder turns the probe off.
    @Test func thePidFolderMustBePrivate() throws {
        let parent = FileManager.default.temporaryDirectory.appendingPathComponent("shell-activity-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: parent, withIntermediateDirectories: false)
        defer { try? FileManager.default.removeItem(at: parent) }
        let folder = try #require(ShellProcessProbe.directory(in: parent))
        let attributes = try FileManager.default.attributesOfItem(atPath: folder.path)
        #expect((attributes[.posixPermissions] as? NSNumber)?.intValue == 0o700)
        #expect(ShellProcessProbe.directory(in: parent) == folder)
        #expect(ShellProcessProbe.pidFile(terminalId: "pane-1", in: parent) == folder.appendingPathComponent("pane-1.pid"))
        // Opened to others: off.
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: folder.path)
        #expect(ShellProcessProbe.directory(in: parent) == nil)
        #expect(ShellProcessProbe.pidFile(terminalId: "pane-1", in: parent) == nil)
        // A link, even to a private folder: off.
        let elsewhere = FileManager.default.temporaryDirectory.appendingPathComponent("shell-activity-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: elsewhere, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o700])
        defer { try? FileManager.default.removeItem(at: elsewhere) }
        try FileManager.default.removeItem(at: folder)
        try FileManager.default.createSymbolicLink(at: folder, withDestinationURL: elsewhere)
        #expect(ShellProcessProbe.directory(in: parent) == nil)
        // A file in its place: off.
        try FileManager.default.removeItem(at: folder)
        try Data().write(to: folder)
        #expect(ShellProcessProbe.directory(in: parent) == nil)
    }

    /// The command the app gives Ghostty, run the way Ghostty runs it on macOS
    /// (`bash --noprofile --norc -c "exec -l <command>"`, without `login`) and
    /// plainly: the pid file holds the pid that stays alive as the login zsh, and
    /// the variable never reaches that zsh.
    @Test(arguments: [true, false]) func thePaneCommandNamesTheShellItBecomes(ghosttyWrapper: Bool) throws {
        let parent = FileManager.default.temporaryDirectory.appendingPathComponent("shell-activity-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: parent, withIntermediateDirectories: false)
        defer { try? FileManager.default.removeItem(at: parent) }
        let pidFile = try #require(ShellProcessProbe.pidFile(terminalId: "pane", in: parent))
        let seen = parent.appendingPathComponent("seen.txt")
        let command = ShellProcessProbe.shellCommand("/bin/zsh -f")
        let input = Pipe()
        let shell = Process()
        shell.executableURL = URL(fileURLWithPath: ghosttyWrapper ? "/bin/bash" : "/bin/sh")
        shell.arguments = ghosttyWrapper ? ["--noprofile", "--norc", "-c", "exec -l " + command] : ["-c", "exec " + command]
        shell.environment = ["PATH": "/usr/bin:/bin", ShellProcessProbe.pidFileVariable: pidFile.path]
        shell.standardInput = input
        try shell.run()
        defer {
            try? input.fileHandleForWriting.close()
            if shell.isRunning { shell.terminate() }
            shell.waitUntilExit()
        }
        // The zsh reads commands from the pipe and stays alive until it closes.
        input.fileHandleForWriting.write(Data("{ print -r -- $$; print -r -- $0; print -r -- ${options[login]}; env; } > '\(seen.path)'\n".utf8))
        let report = try #require(Self.poll { (try? String(contentsOf: seen, encoding: .utf8)).flatMap { $0.contains("PATH=") ? $0 : nil } })
        let lines = report.split(separator: "\n").map(String.init)
        #expect(lines.first == "\(shell.processIdentifier)")
        #expect(lines.dropFirst().first == "-/bin/zsh")
        #expect(lines.dropFirst(2).first == "on")
        #expect(!report.contains(ShellProcessProbe.pidFileVariable))
        #expect(ShellProcessProbe.findShellPid(pidFile: pidFile) == shell.processIdentifier)
        #expect(shell.isRunning)
        #expect(Self.executable(shell.processIdentifier) == "/bin/zsh")
    }

    /// A pid file already there is kept (`set -C`), and the command still execs what follows.
    @Test func thePaneCommandNeverReplacesAPidFile() throws {
        let parent = FileManager.default.temporaryDirectory.appendingPathComponent("shell-activity-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: parent, withIntermediateDirectories: false)
        defer { try? FileManager.default.removeItem(at: parent) }
        let pidFile = try #require(ShellProcessProbe.pidFile(terminalId: "pane", in: parent))
        try "4242\n".write(to: pidFile, atomically: true, encoding: .utf8)
        let shell = Process()
        shell.executableURL = URL(fileURLWithPath: "/bin/sh")
        shell.arguments = ["-c", ShellProcessProbe.shellCommand("/usr/bin/false")]
        shell.environment = ["PATH": "/usr/bin:/bin", ShellProcessProbe.pidFileVariable: pidFile.path]
        try shell.run()
        shell.waitUntilExit()
        #expect(shell.terminationStatus == 1)
        #expect((try? String(contentsOf: pidFile, encoding: .utf8)) == "4242\n")
    }

    @Test func aPidFileCountsOnlyForAProcessBelowTheApp() throws {
        let file = FileManager.default.temporaryDirectory.appendingPathComponent("shell-activity-\(UUID().uuidString).pid")
        defer { try? FileManager.default.removeItem(at: file) }
        #expect(ShellProcessProbe.findShellPid(pidFile: file) == nil)
        let child = try Self.spawn("/bin/sleep", ["20"], environment: [:])
        defer { child.terminate(); child.waitUntilExit() }
        try "\(child.processIdentifier)\n".write(to: file, atomically: true, encoding: .utf8)
        #expect(ShellProcessProbe.findShellPid(pidFile: file) == child.processIdentifier)
        // Not below this process: its own parent, launchd, itself, or a pid not running.
        for text in ["\(getppid())", "1", "\(getpid())", "0", "-4", "abc", ""] {
            try text.write(to: file, atomically: true, encoding: .utf8)
            #expect(ShellProcessProbe.findShellPid(pidFile: file) == nil)
        }
    }

    private static func spawn(_ executable: String, _ arguments: [String], environment: [String: String]) throws -> Process {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: executable)
        process.arguments = arguments
        process.environment = environment.merging(["PATH": "/usr/bin:/bin"]) { current, _ in current }
        try process.run()
        return process
    }

    private static func executable(_ pid: pid_t) -> String? {
        var buffer = [CChar](repeating: 0, count: Int(MAXPATHLEN))
        guard proc_pidpath(pid, &buffer, UInt32(buffer.count)) > 0 else { return nil }
        return String(cString: buffer)
    }

    private static func poll<T>(_ read: () -> T?) -> T? {
        for _ in 0..<60 {
            if let value = read() { return value }
            Thread.sleep(forTimeInterval: 0.05)
        }
        return nil
    }
}
