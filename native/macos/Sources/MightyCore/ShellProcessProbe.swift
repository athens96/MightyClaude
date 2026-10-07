import Darwin
import Foundation

/// Reads, from the kernel, what `ShellActivity` decides on: which process is a
/// shell pane's shell, and which process group owns its terminal. Every read
/// fails safe: nil means "not known now", never "idle".
///
/// macOS no longer shows another process's environment through
/// `KERN_PROCARGS2`, even to its parent, so a pane's shell cannot be told apart
/// by `MIGHTYCLAUDE_TERMINAL_ID`. Instead the pane's own command names it
/// (`shellCommand`): a `zsh -f` reads the pane's pid file from the
/// environment, removes the variable, writes its own pid there and `exec`s the
/// real shell, which keeps that pid. Whatever Ghostty wraps the command in
/// (`login`, a `bash -c "exec -l …"`) stays outside and plays no part.
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

    /// The variable the pane's command reads its pid file from; the shell never sees it.
    public static let pidFileVariable = "MIGHTYCLAUDE_TERMINAL_PIDFILE"

    /// The pane's command: `/bin/zsh -fc` writes its pid (`set -C` keeps a file that
    /// is already there) and `exec -l`s `shell`, so the shell keeps that pid and
    /// starts as a login shell exactly as it would from Ghostty's own wrapper
    /// (`$0` is `-/bin/zsh` either way). The script runs under `zsh -f`, so no
    /// startup file of the user's runs twice, and it starts with `/bin/zsh` so
    /// Ghostty still loads its zsh integration into the real shell. `shell` is
    /// the app's own and has no single quote.
    public static func shellCommand(_ shell: String) -> String {
        precondition(!shell.contains("'"), "the pane's shell command is quoted in single quotes")
        let script = "f=\"${\(pidFileVariable)-}\"; unset \(pidFileVariable); "
            + "if [ -n \"$f\" ]; then { set -C; echo $$ > \"$f\"; } 2>/dev/null; fi; "
            + "exec -l \(shell)"
        return "/bin/zsh -fc '\(script)'"
    }

    /// The panes' pid files' folder in the user's own temporary folder, made 0700
    /// when missing. Nil (the probe is off) unless it is a real folder, not a
    /// link, owned by this user and closed to everyone else.
    public static func directory(in parent: URL = FileManager.default.temporaryDirectory) -> URL? {
        let folder = parent.appendingPathComponent("MightyClaude-terminals", isDirectory: true)
        if mkdir(folder.path, 0o700) != 0, errno != EEXIST { return nil }
        var info = stat()
        guard lstat(folder.path, &info) == 0, info.st_mode & S_IFMT == S_IFDIR,
              info.st_uid == getuid(), info.st_mode & 0o777 == 0o700 else { return nil }
        return folder
    }

    /// A pane's pid file, or nil when `directory` is unusable.
    public static func pidFile(terminalId: String, in parent: URL = FileManager.default.temporaryDirectory) -> URL? {
        directory(in: parent)?.appendingPathComponent(terminalId + ".pid")
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
