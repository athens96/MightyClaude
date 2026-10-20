namespace MightyClaude.Core;

/// <summary>
/// Remote-workspace words only the Windows app says (remote.windows.*: "이 PC", DPAPI, PowerShell, the firewall rule), and the
/// host OS labels both apps show on remote panes (remote.hostOS.*). Everything else reuses the Mac's remote.* and
/// settings.remote.* keys as they are.
/// </summary>
public static class RemoteWindowsStrings
{
    public static string LocalTailscaleOff => Locale.Get("remote.windows.connect.error.localTailscaleOff");
    public static string Timeout => Locale.Get("remote.windows.connect.error.timeout");
    /// <summary>A connect while the saved-connections file is still held open by another program (it is read first, as the Mac's actor does).</summary>
    public static string SavedUnreadable => Locale.Get("remote.windows.connect.error.savedUnreadable");
    public static string ConnectedSaved => Locale.Get("remote.windows.connect.detail.connectedSaved");
    public static string ConnectedMemory => Locale.Get("remote.windows.connect.detail.connectedMemory");
    public static string HostLocalOnly => Locale.Get("remote.windows.host.error.localOnly");
    public static string HostNoTailscale => Locale.Get("remote.windows.host.error.noTailscale");
    public static string HostCommandTooLong => Locale.Get("remote.windows.host.error.commandTooLong");
    /// <summary>A Windows host refuses another computer's Codex or Antigravity run while the full-access switch is off, in any mode: Windows cannot confine it as macOS does.</summary>
    public static string HostAgentNeedsFullAccess(string provider) => Locale.Get("remote.windows.host.error.agentNeedsFullAccess", new Dictionary<string, string> { ["agent"] = ProviderMark.Label(provider) });
    public static string NotLocal => Locale.Get("remote.windows.workspace.error.notLocal");
    public static string PaneUnavailable => Locale.Get("remote.windows.workspace.paneUnavailable");
    public static string SettingsTailscaleOff => Locale.Get("remote.windows.settings.tailscaleOff");
    public static string SettingsShareNote => Locale.Get("remote.windows.settings.shareNote");
    public static string SettingsFirewallNote => Locale.Get("remote.windows.settings.firewallNote");
    public static string SettingsScopeKey => Locale.Get("remote.windows.settings.scopeKey");
    public static string SettingsAllowFullAccessDescription => Locale.Get("remote.windows.settings.allowFullAccessDescription");
    public static string KeyCopied => Locale.Get("remote.windows.settings.keyCopied");
    public static string KeyCopyFailed => Locale.Get("remote.windows.settings.keyCopyFailed");
    public static string FirewallAdd => Locale.Get("remote.windows.firewall.add");
    public static string FirewallRemove => Locale.Get("remote.windows.firewall.remove");
    /// <summary>The rule is there: one elevated <c>set rule</c> makes it hold exactly the ports in use now (the phone's direct port and the sharing port).</summary>
    public static string FirewallUpdate => Locale.Get("remote.windows.firewall.update");
    /// <summary>The rule is there but does not open every port in use now, or its ports could not be read (RemoteWorkspaceFeatures.FirewallNeedsUpdate).</summary>
    public static string FirewallNeedsUpdate => Locale.Get("remote.windows.firewall.needsUpdate");
    /// <summary>Both firewall rows say it: the phone's direct port and the sharing port live in the one rule, so removing it closes both.</summary>
    public static string FirewallRemoveClosesBoth => Locale.Get("remote.windows.firewall.removeClosesBoth");
    public static string SessionInfoHostOS => Locale.Get("remote.sessionInfo.hostOS");
    /// <summary>What a firewall change that did not happen says, in the same words as the phone-direct Settings; null after one that did.
    /// Takes the whole result so no raw code ("timeout", an exit code, a launch error) can reach the screen.</summary>
    public static string? FirewallFailed(FirewallChangeResult result) => MobileDirectStrings.FirewallOutcome(result);
    public static string CommandNote(string host, string? platform) => Locale.Get("remote.windows.pane.commandNote", new Dictionary<string, string> { ["host"] = host, ["os"] = HostOS(platform) });

    /// <summary>The host's OS from <c>/info</c> <c>runtime.platform</c>.</summary>
    public static string HostOS(string? platform) => platform switch
    {
        "darwin" => Locale.Get("remote.hostOS.darwin"),
        "win32" => Locale.Get("remote.hostOS.win32"),
        "linux" => Locale.Get("remote.hostOS.linux"),
        _ => Locale.Get("remote.hostOS.unknown"),
    };
    public static string HostWithOS(string host, string? platform) => Locale.Get("remote.hostWithOS", new Dictionary<string, string> { ["host"] = host, ["os"] = HostOS(platform) });
}
