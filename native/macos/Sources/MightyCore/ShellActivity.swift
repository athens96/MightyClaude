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
}
