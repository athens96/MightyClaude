using System.Globalization;
using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace MightyClaude.Core;

/// <summary>
/// The hidden switch for parent → child delegation between Claude panes (M/Delegation.swift:3-22). It has no settings UI and is
/// off by default. Where the Mac reads a defaults key, Windows reads the registry value <c>delegation.enabled</c> (REG_DWORD,
/// non-zero = on) under <c>HKEY_CURRENT_USER\Software\MightyClaudeNative</c>. While it is on, each new run in a Claude pane also
/// gets the delegation MCP server beside mighty-terminal; turning it off only detaches the server.
/// </summary>
public static class DelegationSwitch
{
    public const string RegistryKey = @"Software\MightyClaudeNative", ValueName = "delegation.enabled";

    /// <summary>Read at each run start and at each delegation call. A missing value is off.</summary>
    public static bool IsOn()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try { using var key = Registry.CurrentUser.OpenSubKey(RegistryKey); return IsOn(key?.GetValue(ValueName)); }
        catch (Exception ex) when (ex is SecurityException or IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Only a REG_DWORD that is not zero is on.</summary>
    public static bool IsOn(object? value) => value is int number && number != 0;

    /// <summary>A Claude pane: an agent pane (kind claude) running the Claude CLI. Only these get the delegation server or may call it.</summary>
    public static bool IsClaudePane(string kind, string provider) => kind == "claude" && provider == "claude";
}

/// <summary>
/// The names delegation's enums carry in the delegation file and in the text an agent reads: the Mac's raw values, which are
/// each case in snake_case (<c>WidthCap</c> is <c>width_cap</c>).
/// </summary>
public static class DelegationNames
{
    public static string Of<T>(T value) where T : struct, Enum => Names<T>.ByValue[value];
    public static T? Parse<T>(string? name) where T : struct, Enum => name is not null && Names<T>.ByName.TryGetValue(name, out var value) ? value : null;

    private static class Names<T> where T : struct, Enum
    {
        internal static readonly Dictionary<T, string> ByValue = Enum.GetValues<T>().ToDictionary(value => value, value => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString()));
        internal static readonly Dictionary<string, T> ByName = ByValue.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);
    }

    /// <summary>The first <paramref name="count"/> characters of <paramref name="text"/> (text elements, like a Swift prefix).</summary>
    internal static string Prefix(string text, int count)
    {
        var starts = StringInfo.ParseCombiningCharacters(text);
        return starts.Length <= count ? text : text[..starts[count]];
    }
}

public sealed class DelegationNameConverter<T> : JsonConverter<T> where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && DelegationNames.Parse<T>(reader.GetString()) is { } value ? value : throw new JsonException();
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => writer.WriteStringValue(DelegationNames.Of(value));
}

/// <summary>
/// Why a delegation call was refused (M/Delegation.swift:24-52). A refused call carries exactly one of these and changes
/// nothing. The agent sees the snake_case name.
/// </summary>
[JsonConverter(typeof(DelegationNameConverter<DelegationReasonCode>))]
public enum DelegationReasonCode
{
    ClaudeOnly, ChildCannotDelegate, WidthCap, LowDisk, StoreFull, NotGit, UnbornBranch, DetachedHead, WiderMode, DiscardHumanOnly, NotReported, TrackedChanges,
    HeadMoved, Diverged, BranchNotCheckedOut, ChildClosed, ParentClosed, FollowUpLimit, WorktreeMissing, MergeConflict, UndoParentMoved, ParentBusy,
    WorkspaceHasChildren, HeldNotRemovable,
}

/// <summary>
/// The pane a delegation call came from, as its run's pane token resolved it (M/AgentPaneMCPBinding.swift:20-35: agentPaneId,
/// kind, provider, delegation). <paramref name="Delegation"/> says the run was started with the delegation server attached:
/// only a Claude pane's run started while the switch was on.
/// </summary>
public sealed record DelegationCaller(string PaneId, string Kind = "claude", string Provider = "claude", bool Delegation = false);

/// <summary>
/// One delegation tool call forwarded by the delegation MCP server (M/Delegation.swift:54-70). It carries no pane id: the
/// caller is whatever pane the token resolves to. Every delegation tool argument is a string.
/// </summary>
[method: JsonConstructor]
public sealed record DelegationRequest(string Tool, IReadOnlyDictionary<string, string> Arguments)
{
    public DelegationRequest(string tool) : this(tool, new Dictionary<string, string>()) { }

    /// <summary>Lowercase letters, digits and underscores, starting with a letter, at most 64.</summary>
    public static bool IsToolName(string name) =>
        name.Length is > 0 and <= 64 && name[0] is >= 'a' and <= 'z' && name.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
}

/// <summary>The app's answer to one delegation call (M/Delegation.swift:72-101): exactly one of these is set.</summary>
/// <param name="Refused">The call was refused with exactly this reason, and nothing changed.</param>
/// <param name="Error">The call reached no delegation tool, or the tool could not take it as given.</param>
/// <param name="Child">The child delegate just made, or the same one again when that call is repeated in the same parent run.</param>
/// <param name="Children">list_children's answer: the caller's children, possibly none.</param>
/// <param name="Status">child_status's answer: one of the caller's children.</param>
/// <param name="Merged">merge's answer: the fast-forward it made, as the MergeRecord it wrote.</param>
/// <param name="FollowUp">follow_up's answer: the follow-up it recorded.</param>
public sealed record DelegationResponse(DelegationReasonCode? Refused = null, string? Error = null, DelegationChildInfo? Child = null, IReadOnlyList<DelegationChildSummary>? Children = null,
    DelegationChildStatus? Status = null, MergeRecord? Merged = null, DelegationFollowUpInfo? FollowUp = null)
{
    public static DelegationResponse Refusal(DelegationReasonCode reason) => new(Refused: reason);
    public static DelegationResponse Failure(string message) => new(Error: message);
    public static DelegationResponse Delegated(ChildRecord child) => new(Child: DelegationChildInfo.Of(child));
}

/// <summary>One child as its parent's tools show it (M/Delegation.swift:103-113). The branch is always <c>mighty/&lt;id&gt;</c>.</summary>
public sealed record DelegationChildInfo(string Id, ChildState State, string Branch, string StartingMode)
{
    public static DelegationChildInfo Of(ChildRecord child) => new(child.Id, child.State, child.Branch, child.StartingMode);
}

/// <summary>Serves one delegation call, already resolved to the pane its token belongs to (M/Delegation.swift:115-118).</summary>
public interface IDelegationRequestHandler
{
    Task<DelegationResponse> Handle(DelegationRequest request, DelegationCaller caller);
}

/// <summary>
/// The app's side of the delegation server with no coordinator behind it (M/Delegation.swift:120-150). Each call is answered
/// by the first rule that applies, and none of these answers changes anything: the switch is off (the call reaches nothing);
/// the caller is not a Claude pane (<c>claude_only</c>); the caller's run started without the delegation server (the call
/// reaches nothing); otherwise the call goes to the delegation tool it names, and there is no tool by any other name.
/// </summary>
public sealed class DelegationIOHandler(Func<bool>? isSwitchOn = null) : IDelegationRequestHandler
{
    public const string DetachedMessage = "The delegation tools are not attached to this pane's run.";
    private readonly Func<bool> isSwitchOn = isSwitchOn ?? DelegationSwitch.IsOn;

    public Task<DelegationResponse> Handle(DelegationRequest request, DelegationCaller caller) =>
        Task.FromResult(Gate(caller, isSwitchOn()) ?? DelegationResponse.Failure(UnknownToolMessage(request.Tool)));

    /// <summary>The answer of the first three rules, or null when the call may go on to a tool. <see cref="DelegationCoordinator"/> applies the same gates.</summary>
    public static DelegationResponse? Gate(DelegationCaller caller, bool isSwitchOn)
    {
        if (!isSwitchOn) return DelegationResponse.Failure(DetachedMessage);
        if (!DelegationSwitch.IsClaudePane(caller.Kind, caller.Provider)) return DelegationResponse.Refusal(DelegationReasonCode.ClaudeOnly);
        return caller.Delegation ? null : DelegationResponse.Failure(DetachedMessage);
    }

    public static string UnknownToolMessage(string tool) => $"Unknown delegation tool {DelegationNames.Prefix(tool, 80)}.";
}
