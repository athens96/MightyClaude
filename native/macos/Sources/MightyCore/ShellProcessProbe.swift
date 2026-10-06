import Darwin
import Foundation

/// Reads, from the kernel, what `ShellActivity` decides on: which process is a
/// shell pane's shell, and which process group owns its terminal. Every read
/// fails safe: nil means "not known now", never "idle".
///
/// macOS no longer shows another process's environment through
/// `KERN_PROCARGS2`, even to its parent, so a pane's shell cannot be told apart
/// by `MIGHTYCLAUDE_TERMINAL_ID`. Instead the shell names itself: Ghostty starts
/// it as `login -flp <user> /bin/bash --noprofile --norc -c "exec -l <shell>"`,
/// and that non-interactive bash reads `BASH_ENV` before it becomes the shell.
/// The file there writes bash's pid, which the shell keeps, to the pane's pid
/// file, and removes both variables so nothing later sees them.
public enum ShellProcessProbe {
    /// The groups `kinfo_proc` reports for one process, its parent, and its start
    /// time, so a cached pid that the system has handed to another process is noticed.
    public struct Groups: Equatable, Sendable {
        public var pgid: pid_t
        /// The foreground process group of the process's controlling terminal; 0 or less without one.
        public var tpgid: pid_t
        public var ppid: pid_t
        public var started: UInt64
    }

    /// The variable the startup file reads the pane's pid file from.
    public static let pidFileVariable = "MIGHTYCLAUDE_TERMINAL_PIDFILE"

    /// Run by the wrapper bash only. `set -C` keeps the first pid written, so a
    /// bash started later cannot replace it.
    static let startupScript = """
    unset BASH_ENV
    if [ -n "${\(pidFileVariable)-}" ]; then { set -C; printf '%s\\n' "$$" > "$\(pidFileVariable)"; set +C; } 2>/dev/null; fi
    unset \(pidFileVariable)

    """

    /// Where the startup file and the panes' pid files live: the user's own temporary folder.
    public static let directory = FileManager.default.temporaryDirectory.appendingPathComponent("MightyClaude-terminals", isDirectory: true)

    /// The startup file, written once per launch; nil when it cannot be written,
    /// and then shells start as before and their activity is not read.
    public static let startupFile: URL? = {
        let file = directory.appendingPathComponent("bash-env.sh")
        do {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
            try Data(startupScript.utf8).write(to: file, options: .atomic)
            return file
        } catch { return nil }
    }()

    public static func pidFile(terminalId: String) -> URL { directory.appendingPathComponent(terminalId + ".pid") }

    /// The variables a pane's shell needs to name itself, or none when the app's
    /// own environment already sets `BASH_ENV` (that one is the user's to keep).
    public static func environment(terminalId: String, inherited: [String: String] = ProcessInfo.processInfo.environment) -> [String: String] {
        guard inherited["BASH_ENV"] == nil, let startupFile else { return [:] }
        return ["BASH_ENV": startupFile.path, pidFileVariable: pidFile(terminalId: terminalId).path]
    }

    /// The pid in `pidFile`, when that process is still running below `root`
    /// (the app). Nil when the file is missing, malformed, or names anything else.
    public static func findShellPid(pidFile: URL, under root: pid_t = getpid()) -> pid_t? {
        guard let data = try? Data(contentsOf: pidFile), data.count < 32,
              let pid = pid_t(String(decoding: data, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)),
              pid > 1, pid != root else { return nil }
        var current = pid
        for _ in 0..<16 {
            guard let parent = groups(pid: current)?.ppid else { return nil }
            if parent == root { return pid }
            guard parent > 1 else { return nil }
            current = parent
        }
        return nil
    }

    /// `e_pgid`, `e_tpgid` and the parent of one process, from `KERN_PROC_PID`. Nil when it is gone or unreadable.
    public static func groups(pid: pid_t) -> Groups? {
        guard pid > 1 else { return nil }
        var info = kinfo_proc()
        var size = MemoryLayout<kinfo_proc>.stride
        var mib: [Int32] = [CTL_KERN, KERN_PROC, KERN_PROC_PID, pid]
        guard sysctl(&mib, 4, &info, &size, nil, 0) == 0, size == MemoryLayout<kinfo_proc>.stride, info.kp_proc.p_pid == pid else { return nil }
        let start = info.kp_proc.p_un.__p_starttime
        let started = UInt64(max(0, start.tv_sec)) &* 1_000_000 &+ UInt64(max(0, start.tv_usec))
        return Groups(pgid: info.kp_eproc.e_pgid, tpgid: info.kp_eproc.e_tpgid, ppid: info.kp_eproc.e_ppid, started: started)
    }
}
