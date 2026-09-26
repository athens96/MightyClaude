namespace MightyClaude.Core;

public static class BrowserProfile
{
    // Returns the WebView2 user data folder for a workspace.
    // Path: <stateDirectory>\browser-profiles\<workspaceProfileKey>
    // --profile isolates it because stateDirectory itself follows the profile flag.
    public static string ProfileFolder(string stateDirectory, string workspaceProfileKey) =>
        Path.Combine(stateDirectory, "browser-profiles", workspaceProfileKey);
}
