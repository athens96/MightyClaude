namespace MightyClaude.Core;

// The status line's copy, read from the shared locale files on demand: the keys macOS
// StatusLineView.swift and StatusLine.swift read.
public static class StatusLineStrings
{
    // Trust prompt (StatusLineView.swift)
    public static string TrustPromptTemplate => Locale.Get("statusLine.untrustedPrompt");
    public static string TrustAllow => Locale.Get("statusLine.trust");
    public static string TrustDeny => Locale.Get("statusLine.notNow");
    public static string TrustNote => Locale.Get("statusLine.reaskNote");

    // Accessibility / pane label
    public static string AccessibilityLabel => Locale.Get("composer.statusLine.name");

    // Error messages (StatusLine.swift)
    public static string ErrorStartTemplate => Locale.Get("statusLine.error.start");
    public static string ErrorTimeout => Locale.Get("statusLine.error.timeout");
    public static string ErrorExitTemplate => Locale.Get("statusLine.error.exitCode");

    // StatusLineConfig.Source names. The trust fingerprint a workspace's allowed command is saved
    // under hashes this text, so it stays as written in every language (as on macOS); SourceLabel
    // is what the screen shows.
    public const string SourceWorkspaceLocal = "프로젝트 로컬 설정";
    public const string SourceWorkspace = "프로젝트 설정";
    public const string SourceUser = "사용자 설정";

    /// The localized name of a <see cref="StatusLineConfig.Source"/>; an unknown source shows as it is.
    public static string SourceLabel(string source) => source switch
    {
        SourceWorkspaceLocal => Locale.Get("statusLine.source.projectLocal"),
        SourceWorkspace => Locale.Get("statusLine.source.project"),
        SourceUser => Locale.Get("statusLine.source.user"),
        _ => source,
    };
}
