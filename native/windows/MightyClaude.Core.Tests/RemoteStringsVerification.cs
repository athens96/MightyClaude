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
            ["sessionInfoHostOS"] = RemoteWindowsStrings.SessionInfoHostOS,
        };
        foreach (var (name, value) in values)
            Check(value.Length > 0 && !value.StartsWith("remote.", StringComparison.Ordinal), name + " has no text: " + value);
        // Korean is pinned by the runner, so these are the ko.json values.
        Check(RemoteWindowsStrings.HostLocalOnly.Contains("이 PC", StringComparison.Ordinal), "the Windows host says 이 PC, never 이 Mac");
        Check(RemoteWindowsStrings.CommandNote("studio-pc", "win32").Contains("studio-pc", StringComparison.Ordinal) && RemoteWindowsStrings.CommandNote("studio-pc", "win32").Contains("Windows", StringComparison.Ordinal), "the command note names the host and its OS");
        Check(RemoteWindowsStrings.FirewallFailed("denied").Contains("denied", StringComparison.Ordinal), "the firewall failure carries its error");
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
