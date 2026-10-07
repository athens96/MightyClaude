import Foundation

/// Whether a shell pane is running a command, read from the terminal's
/// foreground process group: at the prompt the shell owns the terminal, and
/// while a command runs (a build, `sleep`, `vim`, `ssh`) a job group does.
/// This needs no shell integration, so it also holds for `zsh -f`.
public enum ShellActivity {
    /// True only when the terminal names a foreground group and it is not the shell's own.
    public static func isBusy(shellGroup: pid_t, foregroundGroup: pid_t) -> Bool {
        foregroundGroup > 0 && foregroundGroup != shellGroup
    }

    /// The status a shell pane moves to after a reading, or nil to leave it: a pane
    /// that has completed or failed keeps that, a reading that could not be taken
    /// (`busy` nil) changes nothing, and only a different status is reported.
    public static func nextStatus(current: String, busy: Bool?) -> String? {
        guard let busy, !["completed", "error"].contains(current) else { return nil }
        let next = busy ? "running" : "idle"
        return next == current ? nil : next
    }
}
