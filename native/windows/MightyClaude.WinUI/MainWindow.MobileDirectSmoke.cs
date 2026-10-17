using System.ComponentModel;
using System.Diagnostics;
using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    /// <summary>
    /// The direct path's rows in 모바일 리모트 over fixed fake endpoints and a firewall runner that records what it would run and starts
    /// nothing: the switch, the port and its apply button, the status line, the rule read (unelevated) before any click, then the add and
    /// remove commands exactly as Core builds them, and a declined and a timed-out UAC prompt ending in Core's words with the button free
    /// again. Nothing on this PC is probed and no netsh runs.
    /// </summary>
    private async Task<Dictionary<string, object?>> RunMobileDirectSmoke()
    {
        var checks = new Dictionary<string, object?>();
        const string program = @"C:\Program Files\Mighty Claude\MightyClaude.exe";
        // The runner starts netsh only from the folder Windows reports, so the recorder is given that folder's name (read, never run).
        var system = WindowsFirewallRule.SystemDirectory ?? @"C:\Windows\System32";
        var netsh = system.TrimEnd('\\') + @"\netsh.exe";
        var launched = new List<ProcessStartInfo>(); var present = false; Exception? elevationFails = null;
        var heldRunner = firewallRunner; var heldRemote = service.Snapshot.MobileRemote;
        firewallRunner = new((info, _) =>
        {
            launched.Add(info);
            var elevated = info.Verb == WindowsFirewallRule.ElevationVerb;
            if (elevated && elevationFails is { } failure) return Task.FromException<int>(failure);
            if (elevated) present = !info.Arguments.StartsWith("advfirewall firewall delete rule ", StringComparison.Ordinal);
            return Task.FromResult(elevated || present ? 0 : 1);
        }, system, program);
        var fixedStatus = new MobileDirectStatus("listening", ["100.101.102.103:43139", "[fd7a:115c:a1e0::1234:5678]:43139"]);
        mobileDirectStatusOverride = fixedStatus;
        try
        {
            await service.UpdateAsync(s => s with { MobileRemote = s.MobileRemote with { Enabled = true, DirectEnabled = true, DirectPort = MobileDirect.DefaultPort } });
            await RefreshFirewallState();
            var rows = BuildMobileRemoteSection();
            T Element<T>(string id) where T : FrameworkElement => SettingsElements(rows).OfType<T>().FirstOrDefault(element => AutomationProperties.GetAutomationId(element) == id)
                ?? throw new InvalidOperationException("the mobile remote section has no " + id);
            Require(Element<ToggleButton>("settings-mobile-direct-toggle").IsChecked == true, "the direct switch must start on");
            Require(Element<TextBox>("settings-mobile-direct-port").Text == "43139", "the port field must show 43139");
            Element<Button>("settings-mobile-direct-apply");
            checks["rows"] = true;
            Require(Element<TextBlock>("settings-mobile-direct-status").Text == MobileDirectStatusText.Detail(fixedStatus), "the status line must list the endpoints");
            checks["status"] = true;
            Require(Element<TextBlock>("settings-mobile-firewall-state").Text == MobileDirectStrings.FirewallState(FirewallRuleState.Absent), "the rule must read as absent");
            var button = Element<Button>("settings-mobile-firewall-button");
            Require(Equals(button.Content, MobileDirectStrings.FirewallButton(FirewallRuleState.Absent)) && !button.IsEnabled, "the rule's button must offer to add it and be off in a smoke run");
            Require(launched.Count == 1 && !launched[0].UseShellExecute && launched[0].Verb != WindowsFirewallRule.ElevationVerb, "before a click only the unelevated query may run");
            checks["firewallRow"] = true;
            await ChangeFirewallRule(add: true);
            var adds = launched.Where(info => info.Verb == WindowsFirewallRule.ElevationVerb).ToArray();
            var expectedAdd = WindowsFirewallRule.AddArguments(program, [MobileDirect.DefaultPort]) ?? throw new InvalidOperationException("the fixed program path was refused");
            Require(adds.Length == 1 && adds[0].UseShellExecute && adds[0].FileName == netsh && adds[0].Arguments == WindowsFirewallRule.CommandLine(expectedAdd)
                && firewallState == FirewallRuleState.Present && firewallMessage is null && !firewallBusy, "add: " + string.Join(" | ", adds.Select(info => info.FileName + " " + info.Arguments)));
            checks["addCommand"] = true;
            await ChangeFirewallRule(add: false);
            var removes = launched.Where(info => info.Verb == WindowsFirewallRule.ElevationVerb).Skip(1).ToArray();
            Require(removes.Length == 1 && removes[0].Arguments == WindowsFirewallRule.CommandLine(WindowsFirewallRule.DeleteArguments()) && firewallState == FirewallRuleState.Absent && !firewallBusy,
                "remove: " + string.Join(" | ", removes.Select(info => info.FileName + " " + info.Arguments)));
            checks["removeCommand"] = true;
            // A declined UAC prompt and one Windows never answered: Core's words only, the rule as read again, the button free again.
            elevationFails = new Win32Exception(WindowsFirewallRule.ElevationCancelled);
            await ChangeFirewallRule(add: true);
            Require(firewallMessage == MobileDirectStrings.FirewallOutcome(new(FirewallChange.Cancelled)) && firewallState == FirewallRuleState.Absent && !firewallBusy, "a declined prompt: " + firewallMessage);
            elevationFails = new TimeoutException();
            await ChangeFirewallRule(add: true);
            Require(firewallMessage == MobileDirectStrings.FirewallOutcome(new(FirewallChange.Failed, "timeout")) && firewallState == FirewallRuleState.Absent && !firewallBusy, "a timed-out prompt: " + firewallMessage);
            checks["outcomes"] = true;
            checks["passed"] = true;
        }
        finally
        {
            firewallRunner = heldRunner; mobileDirectStatusOverride = null; firewallState = FirewallRuleState.Unknown; firewallMessage = null;
            await service.UpdateAsync(s => s with { MobileRemote = heldRemote });
        }
        return checks;
    }
}
