using MightyClaude.Core;

internal static class RemoteStringsVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    internal static Task EveryWindowsOnlyStringResolves()
    {
        var values = new Dictionary<string, string>
        {
            ["localTailscaleOff"] = RemoteWindowsStrings.LocalTailscaleOff, ["connectedSaved"] = RemoteWindowsStrings.ConnectedSaved,
            ["connectedMemory"] = RemoteWindowsStrings.ConnectedMemory, ["hostLocalOnly"] = RemoteWindowsStrings.HostLocalOnly,
            ["hostNoTailscale"] = RemoteWindowsStrings.HostNoTailscale, ["notLocal"] = RemoteWindowsStrings.NotLocal,
            ["paneUnavailable"] = RemoteWindowsStrings.PaneUnavailable, ["settingsTailscaleOff"] = RemoteWindowsStrings.SettingsTailscaleOff,
            ["settingsShareNote"] = RemoteWindowsStrings.SettingsShareNote, ["settingsFirewallNote"] = RemoteWindowsStrings.SettingsFirewallNote,
            ["settingsScopeKey"] = RemoteWindowsStrings.SettingsScopeKey, ["allowFullAccess"] = RemoteWindowsStrings.SettingsAllowFullAccessDescription,
            ["keyCopied"] = RemoteWindowsStrings.KeyCopied, ["firewallAdd"] = RemoteWindowsStrings.FirewallAdd, ["firewallRemove"] = RemoteWindowsStrings.FirewallRemove,
            ["firewallUpdate"] = RemoteWindowsStrings.FirewallUpdate,
            ["sessionInfoHostOS"] = RemoteWindowsStrings.SessionInfoHostOS,
            ["timeout"] = RemoteWindowsStrings.Timeout,
            ["hostCommandTooLong"] = RemoteWindowsStrings.HostCommandTooLong,
        };
        foreach (var (name, value) in values)
            Check(value.Length > 0 && !value.StartsWith("remote.", StringComparison.Ordinal), name + " has no text: " + value);
        // Korean is pinned by the runner, so these are the ko.json values.
        Check(RemoteWindowsStrings.HostLocalOnly.Contains("이 PC", StringComparison.Ordinal), "the Windows host says 이 PC, never 이 Mac");
        {
            // "Windows" and "Mac" already appear in the note's own shell explanation, so the OS is pinned with Linux, which does not.
            var note = RemoteWindowsStrings.CommandNote("studio-pc", "linux");
            Check(note.Contains("studio-pc", StringComparison.Ordinal) && note.Contains("Linux", StringComparison.Ordinal) && !note.Contains('{'), "the command note names the host and its OS and leaves no placeholder");
        }
        Check(RemoteWindowsStrings.FirewallFailed(new(FirewallChange.Done)) is null, "a change that happened has no failure words");
        Check(RemoteWindowsStrings.FirewallFailed(new(FirewallChange.Cancelled)) == Locale.Get("mobileRemote.direct.windows.firewall.cancelled"), "a declined prompt says it was declined");
        var timeout = RemoteWindowsStrings.FirewallFailed(new(FirewallChange.Failed, "timeout"));
        Check(!string.IsNullOrEmpty(timeout) && !timeout.Contains("timeout", StringComparison.Ordinal), "a raw code never reaches the words: " + timeout);
        return Task.CompletedTask;
    }

    internal static Task HostOSLabelsCoverEveryPlatform()
    {
        Check(RemoteWindowsStrings.HostOS("darwin") == "macOS" && RemoteWindowsStrings.HostOS("win32") == "Windows" && RemoteWindowsStrings.HostOS("linux") == "Linux", "the three platforms have their names");
        Check(RemoteWindowsStrings.HostOS(null) == RemoteWindowsStrings.HostOS("plan9") && RemoteWindowsStrings.HostOS(null).Length > 0, "an unknown platform reads as unknown");
        Check(RemoteWindowsStrings.HostWithOS("my-mac", "darwin") == "my-mac · macOS", "host and OS read as one label");
        return Task.CompletedTask;
    }
}
