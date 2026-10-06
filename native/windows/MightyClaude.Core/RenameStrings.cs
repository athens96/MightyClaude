namespace MightyClaude.Core;

// The rename dialog's copy, read from the shared locale files on demand (the keys macOS
// RenameViews.swift reads), so the saved language preference applies however early a caller asks.
public static class RenameStrings
{
    public static string MenuEntry => Locale.Get("menu.rename");
    public static string FieldLabel => Locale.Get("pane.rename.namePlaceholder");
    public static string HeadingWorkspace => Locale.Get("pane.rename.workspaceTitle");
    public static string HeadingSession => Locale.Get("pane.rename.paneTitle");
    public static string HintWorkspace => Locale.Get("pane.rename.workspaceNote");
    public static string HintSession => Locale.Get("pane.rename.paneNote");
    public static string ErrorTooLong => Locale.Get("pane.rename.tooLong");
    public static string ErrorControlCharacter => Locale.Get("pane.rename.noNewlines");
    public static string ButtonCancel => Locale.Get("resume.cancel");
    public static string ButtonSave => Locale.Get("pane.rename.save");
    public static string ErrorNotFound => Locale.Get("pane.rename.targetMissing");
}
