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

    @Test func aPaneGetsTheStartupFileUnlessTheUserSetsBashEnv() throws {
        let startup = try #require(ShellProcessProbe.startupFile)
        let environment = ShellProcessProbe.environment(terminalId: "pane-1", inherited: [:])
        #expect(environment["BASH_ENV"] == startup.path)
        #expect(environment[ShellProcessProbe.pidFileVariable] == ShellProcessProbe.pidFile(terminalId: "pane-1").path)
        #expect(ShellProcessProbe.environment(terminalId: "pane-1", inherited: ["BASH_ENV": "/Users/me/.bashenv"]).isEmpty)
    }

    /// The wrapper Ghostty runs (`bash --noprofile --norc -c "exec …"`) writes the
    /// pid the shell keeps, and the shell no longer sees either variable.
    @Test func theWrapperBashNamesTheShellItBecomes() throws {
        let id = "shell-activity-\(UUID().uuidString)"
        let pidFile = ShellProcessProbe.pidFile(terminalId: id)
        let seen = FileManager.default.temporaryDirectory.appendingPathComponent("\(id).env")
        defer { try? FileManager.default.removeItem(at: pidFile); try? FileManager.default.removeItem(at: seen) }
        let shell = try Self.spawn("/bin/bash", ["--noprofile", "--norc", "-c", "exec /bin/sh -c 'env > \"$0\"; exec /bin/sleep 20' '\(seen.path)'"],
                                   environment: ShellProcessProbe.environment(terminalId: id, inherited: [:]))
        defer { shell.terminate(); shell.waitUntilExit() }
        let found = Self.poll { ShellProcessProbe.findShellPid(pidFile: pidFile) }
        #expect(found == shell.processIdentifier)
        let environment = try #require(Self.poll { (try? String(contentsOf: seen, encoding: .utf8)).flatMap { $0.contains("PATH=") ? $0 : nil } })
        #expect(!environment.contains("BASH_ENV="))
        #expect(!environment.contains(ShellProcessProbe.pidFileVariable))
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

    private static func poll<T>(_ read: () -> T?) -> T? {
        for _ in 0..<60 {
            if let value = read() { return value }
            Thread.sleep(forTimeInterval: 0.05)
        }
        return nil
    }
}
