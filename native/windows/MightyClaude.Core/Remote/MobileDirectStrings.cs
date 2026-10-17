using System.Globalization;

namespace MightyClaude.Core;

/// <summary>
/// The direct path's words only the Windows app says (mobileRemote.direct.windows.*): this PC instead of this Mac, and the Windows
/// Firewall rule's row. The switch, the port label and the status line reuse the Mac's mobileRemote.direct.* keys.
/// </summary>
public static class MobileDirectStrings
{
    public static readonly IReadOnlyList<string> Keys =
    [
        "mobileRemote.direct.windows.description", "mobileRemote.direct.windows.portHint", "mobileRemote.direct.windows.firewall.description",
        "mobileRemote.direct.windows.firewall.present", "mobileRemote.direct.windows.firewall.absent", "mobileRemote.direct.windows.firewall.unknown",
        "mobileRemote.direct.windows.firewall.addButton", "mobileRemote.direct.windows.firewall.removeButton", "mobileRemote.direct.windows.firewall.cancelled",
        "mobileRemote.direct.windows.firewall.failedTemplate", "mobileRemote.direct.windows.firewall.badProgram", "mobileRemote.direct.windows.firewall.noPorts",
        "mobileRemote.direct.windows.firewall.badPorts", "mobileRemote.direct.windows.firewall.timeout", "mobileRemote.direct.windows.firewall.queryFailed",
        "mobileRemote.direct.windows.firewall.noSystemDirectory", "mobileRemote.direct.windows.firewall.failedUnknown",
    ];

    public static string Description => Locale.Get("mobileRemote.direct.windows.description");
    public static string PortHint => Locale.Get("mobileRemote.direct.windows.portHint");
    public static string FirewallDescription => Locale.Get("mobileRemote.direct.windows.firewall.description");

    public static string FirewallState(FirewallRuleState state) => Locale.Get(state switch
    {
        FirewallRuleState.Present => "mobileRemote.direct.windows.firewall.present",
        FirewallRuleState.Absent => "mobileRemote.direct.windows.firewall.absent",
        _ => "mobileRemote.direct.windows.firewall.unknown",
    });

    public static string FirewallButton(FirewallRuleState state) =>
        Locale.Get(state == FirewallRuleState.Present ? "mobileRemote.direct.windows.firewall.removeButton" : "mobileRemote.direct.windows.firewall.addButton");

    /// <summary>
    /// What a change that did not happen says; null after one that did. Every detail code has its own words. Only a netsh exit code
    /// (digits) is quoted, as the template's number; a launch error's text (Windows' own language) and any code not listed here are never
    /// shown, they become the unknown-failure words.
    /// </summary>
    public static string? FirewallOutcome(FirewallChangeResult result) => result.Kind switch
    {
        FirewallChange.Done => null,
        FirewallChange.Cancelled => Locale.Get("mobileRemote.direct.windows.firewall.cancelled"),
        FirewallChange.Refused => Locale.Get(result.Detail switch
        {
            "noPorts" => "mobileRemote.direct.windows.firewall.noPorts",
            "badPorts" => "mobileRemote.direct.windows.firewall.badPorts",
            "badProgram" => "mobileRemote.direct.windows.firewall.badProgram",
            "noSystemDirectory" => "mobileRemote.direct.windows.firewall.noSystemDirectory",
            _ => "mobileRemote.direct.windows.firewall.failedUnknown",
        }),
        _ => result.Detail switch
        {
            "timeout" => Locale.Get("mobileRemote.direct.windows.firewall.timeout"),
            "queryFailed" => Locale.Get("mobileRemote.direct.windows.firewall.queryFailed"),
            { Length: > 0 and <= 11 } code when int.TryParse(code, NumberStyles.Integer, CultureInfo.InvariantCulture, out var exit) =>
                Locale.Get("mobileRemote.direct.windows.firewall.failedTemplate", new Dictionary<string, string> { ["error"] = exit.ToString(CultureInfo.InvariantCulture) }),
            _ => Locale.Get("mobileRemote.direct.windows.firewall.failedUnknown"),
        },
    };
}
