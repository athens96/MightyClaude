using MightyClaude.Core;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    /// <summary>The firewall rule as last read; Unknown until read, and for a whole smoke run that set no runner.</summary>
    private FirewallRuleState firewallState = FirewallRuleState.Unknown;
    /// <summary>What the last add or remove that did not happen said, in Core's words only; null after one that did.</summary>
    private string? firewallMessage;
    /// <summary>Runs netsh; the GUI smoke puts a recorder here that starts nothing.</summary>
    private WindowsFirewallRuleRunner? firewallRunner;
    /// <summary>An add or remove is under way. Windows' UAC prompt waits as long as the user does, so the button stays busy until it ends, however it ends.</summary>
    private bool firewallBusy;
    /// <summary>The shown 모바일 리모트 section redraws its firewall row when the rule's state or the busy mark changes.</summary>
    private event Action? FirewallChanged;
    /// <summary>The GUI smoke's direct-path status: fixed fake endpoints, never this PC's.</summary>
    private MobileDirectStatus? mobileDirectStatusOverride;
    private MobileDirectStatus MobileDirectStatusNow => mobileDirectStatusOverride ?? mobileHost?.DirectStatus ?? MobileDirectStatus.Off;

    /// <summary>The ports the rule must open: the phone's direct port while the remote and its direct switch are on (part 3 adds the remote workspaces' port).</summary>
    internal IReadOnlyList<int> FirewallPorts() => service.Snapshot.MobileRemote is { Enabled: true, DirectEnabled: true } remote ? [remote.DirectPort] : [];

    /// <summary>Reads whether the rule exists, without elevation. Anything unexpected reads as Unknown, never as an error's own text.</summary>
    private async Task RefreshFirewallState()
    {
        if (options.SmokeTest && firewallRunner is null) return;
        var runner = firewallRunner ??= WindowsFirewallRuleRunner.ForThisApp();
        try { firewallState = await runner.QueryAsync(CancellationToken.None); }
        catch (Exception ex) when (!options.SmokeTest && ex is not OutOfMemoryException) { firewallState = FirewallRuleState.Unknown; }
    }

    /// <summary>
    /// The firewall row's button (and a switch turned off by the user): one netsh command, elevated by Windows' own UAC prompt, then
    /// the rule read again. Only Core's outcome words are kept; a press while a change waits does nothing.
    /// </summary>
    internal async Task ChangeFirewallRule(bool add)
    {
        if (options.SmokeTest && firewallRunner is null) return;
        if (firewallBusy) return;
        firewallBusy = true; FirewallChanged?.Invoke();
        try
        {
            var runner = firewallRunner ??= WindowsFirewallRuleRunner.ForThisApp();
            FirewallChangeResult result;
            try { result = add ? await runner.AddAsync(FirewallPorts(), CancellationToken.None) : await runner.RemoveAsync(CancellationToken.None); }
            catch (Exception ex) when (!options.SmokeTest && ex is not OutOfMemoryException) { result = new(FirewallChange.Failed); }
            firewallMessage = MobileDirectStrings.FirewallOutcome(result);
            await RefreshFirewallState();
        }
        finally { firewallBusy = false; FirewallChanged?.Invoke(); }
    }

    /// <summary>Turning the remote or its direct path off takes the rule away when nothing needs it any more: one UAC prompt from that same click.</summary>
    private async Task RemoveFirewallRuleIfUnneeded()
    {
        if (firewallState == FirewallRuleState.Present && FirewallPorts().Count == 0) await ChangeFirewallRule(add: false);
    }
}
