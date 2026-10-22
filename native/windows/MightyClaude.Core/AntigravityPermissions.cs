using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MightyClaude.Core;

// Antigravity permission cards (docs/superpowers/plans/2026-10-22-antigravity-permission-cards.md, M/MightyCore/AntigravityPermissions.swift).
// A headless `agy -p` turn cannot ask: what its review mode would have asked about is declined and the turn goes on. The stream says
// exactly what was declined, so once the turn is over the pane asks the way a Claude card does, and an allow becomes a rule in agy's own
// settings (%USERPROFILE%\.gemini\antigravity-cli\settings.json, permissions.allow) for a follow-up turn in the same conversation.

/// <summary>
/// One action a turn was refused. <c>Kind</c> is command, url, file or other (a kind a newer host sends reads as other);
/// <c>Action</c> is agy's permission name (command, write_file, read_file, read_url, execute_url, mcp) or the tool's own name;
/// <c>DisplayName</c> agy's name for it (RunCommand, WriteFile), else the tool's; <c>Target</c> the command line, domain or path when
/// agy said; <c>Rule</c> the permissions.allow rule that allows exactly this, none when no rule can (the card then says to change the
/// permission mode instead).
/// </summary>
public sealed record AntigravityDeniedAction([property: JsonConverter(typeof(AntigravityKindConverter))] string Kind, string Action, string DisplayName, string? Target = null, string? Rule = null)
{
    public static readonly IReadOnlyList<string> Kinds = ["command", "url", "file", "other"];
    /// <summary>A kind this app does not know reads as other.</summary>
    public static string KindOf(string? value) => value is not null && Kinds.Contains(value) ? value : "other";
}

/// <summary>Reads an action's kind as the Mac's decoder does: a string, and one it does not know is <c>other</c>.</summary>
public sealed class AntigravityKindConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String ? AntigravityDeniedAction.KindOf(reader.GetString()) : throw new JsonException();
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}

/// <summary>
/// The card one Antigravity turn leaves: what it was refused, together. <c>Id</c> is <c>agy-&lt;uuid&gt;</c>, <c>RunId</c> the refused
/// run's activity id (the phone answers by both), <c>ReceivedAt</c> when the turn ended.
/// </summary>
public sealed record AntigravityPermissionRequest(string Id, string RunId, IReadOnlyList<AntigravityDeniedAction> Actions, string? ReceivedAt = null)
{
    public const int MaximumActions = 16;
    /// <summary>A new card of <paramref name="runId"/>, stamped now as the Mac stamps it (whole seconds).</summary>
    public static AntigravityPermissionRequest Make(string runId, IReadOnlyList<AntigravityDeniedAction> actions) =>
        new("agy-" + Guid.NewGuid().ToString("D"), runId, actions, DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
    /// <summary>The rules an allow adds, once each, in order.</summary>
    [JsonIgnore] public IReadOnlyList<string> Rules => Actions.Select(a => a.Rule).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
    /// <summary>At least one action can be allowed with a rule.</summary>
    [JsonIgnore] public bool CanAllow => Rules.Count > 0;
    /// <summary>What the transcript line and the continuation name: the targets, else agy's names.</summary>
    [JsonIgnore] public IReadOnlyList<string> Names => Actions.Select(a => a.Target ?? a.DisplayName).ToList();
}

/// <summary>The card's three buttons: 이번만 허용, 항상 허용, 거절.</summary>
public enum AntigravityPermissionDecision { Once, Always, Deny }

/// <summary>
/// What the follow-up run of an allowed card carries: the rules to put in place while it runs (<c>Temporary</c>) or for good.
/// <c>RequestId</c> is the card's: a host that runs remote panes adds only rules it reported itself.
/// </summary>
public sealed record AntigravityAllowGrant(string RequestId, IReadOnlyList<string> Rules, bool Temporary)
{
    public const int MaximumRuleBytes = 4_096;
    private static readonly Regex RuleShape = new(@"\A[a-z][a-z_]{0,63}\(.+\)\z", RegexOptions.CultureInvariant);
    /// <summary>A rule is <c>name(…)</c>: a lowercase permission name and a non-empty argument, at most 4 KiB, no NUL, no line break.</summary>
    public static bool ValidRule(string? rule) =>
        rule is not null && Encoding.UTF8.GetByteCount(rule) <= MaximumRuleBytes && !rule.Contains('\0') && !rule.Contains('\n') && !rule.Contains('\r') && RuleShape.IsMatch(rule);
    [JsonIgnore] public bool IsValid => Wire.Identifier(RequestId) && Rules is { Count: > 0 and <= AntigravityPermissionRequest.MaximumActions } && Rules.All(ValidRule);
    public bool Same(AntigravityAllowGrant? other) => other is not null && other.RequestId == RequestId && other.Temporary == Temporary && other.Rules.SequenceEqual(Rules);
}

/// <summary>
/// Collects one turn's refusals from agy's stream-json: each tool step that ended ERROR with a permission denial ("permission check
/// failed … user denied permission …"), then the result's denied_actions.
/// </summary>
public sealed class AntigravityDenialCollector
{
    private readonly Dictionary<long, AntigravityDeniedAction> steps = [];
    private readonly List<long> order = [];
    private sealed record Reported(string Action, string Name);

    /// <summary>A denial's message, or null when the step's error is something else.</summary>
    public static string? DenialMessage(JsonElement info)
    {
        var error = MetadataJson.Property(info, "error");
        var message = error.ValueKind == JsonValueKind.Object ? error.Text("message") : error.ValueKind == JsonValueKind.String ? error.GetString() : null;
        if (message is null || Encoding.UTF8.GetByteCount(message) > 65_536) return null;
        var lowered = message.ToLowerInvariant();
        return lowered.Contains("permission check failed", StringComparison.Ordinal) && lowered.Contains("denied permission", StringComparison.Ordinal) ? message : null;
    }

    /// <summary>One <c>step_update</c> payload of a tool step.</summary>
    public void Observe(JsonElement payload)
    {
        if ((payload.Text("state") ?? "").ToUpperInvariant() != "ERROR" || MetadataJson.Integer(payload, "step_index") is not { } index) return;
        var info = MetadataJson.Property(payload, "tool_info");
        if (DenialMessage(info) is not { } message) return;
        var tool = info.Text("name") ?? payload.Text("tool_name") ?? "tool";
        if (!steps.ContainsKey(index)) order.Add(index);
        steps[index] = Action(Prefix(tool, 128), MetadataJson.Property(info, "parameters"), message);
    }

    /// <summary>The turn's <c>result</c> payload: every refusal, once each. Starts over for the next turn.</summary>
    public IReadOnlyList<AntigravityDeniedAction> Finish(JsonElement payload)
    {
        try
        {
            var rows = MetadataJson.Property(payload, "denied_actions");
            var reported = rows.ValueKind != JsonValueKind.Array ? [] : rows.EnumerateArray().Take(AntigravityPermissionRequest.MaximumActions)
                .Select(row => row.Text("action") is { Length: > 0 } action ? new Reported(Prefix(action, 128), row.Text("display_name") is { } name ? Prefix(name, 128) : Prefix(action, 128)) : null)
                .OfType<Reported>().ToList();
            var result = new List<AntigravityDeniedAction>(); var seen = new HashSet<string>(StringComparer.Ordinal);
            void Add(AntigravityDeniedAction value)
            {
                var key = value.Rule ?? value.Kind + "|" + value.Action + "|" + (value.Target ?? "");
                if (result.Count < AntigravityPermissionRequest.MaximumActions && seen.Add(key)) result.Add(value);
            }
            foreach (var index in order)
            {
                if (!steps.TryGetValue(index, out var value)) continue;
                if (reported.FirstOrDefault(row => row.Action == value.Action) is { } named) value = value with { DisplayName = named.Name };
                Add(value);
            }
            // A refusal the steps did not show (agy reports each permission once).
            var covered = result.Select(a => a.Action).ToHashSet(StringComparer.Ordinal);
            foreach (var row in reported.Where(row => !covered.Contains(row.Action))) Add(new(Kind(row.Action, row.Action), row.Action, row.Name));
            return result;
        }
        finally { steps.Clear(); order.Clear(); }
    }

    internal static string Kind(string action, string tool)
    {
        var name = action.ToLowerInvariant(); var toolName = tool.ToLowerInvariant();
        if (name == "command" || toolName == "run_command") return "command";
        if (name.EndsWith("_url", StringComparison.Ordinal) || name == "url" || toolName.Contains("url", StringComparison.Ordinal) || toolName.Contains("browser", StringComparison.Ordinal)) return "url";
        if (name.EndsWith("_file", StringComparison.Ordinal) || toolName is "write_to_file" or "replace_file_content" or "multi_replace_file_content" or "sed_file" or "notebook_edit" or "view_file") return "file";
        return "other";
    }

    /// <summary>
    /// What one denied step was. A command is allowed by its whole command line (<c>command(&lt;line&gt;)</c>, a prefix rule in agy); any
    /// other denial names its own rule ("user denied permission for write_file(/path)"), which is used as agy wrote it.
    /// </summary>
    internal static AntigravityDeniedAction Action(string tool, JsonElement parameters, string message)
    {
        string? Parameter(params string[] names)
        {
            foreach (var name in names) if (parameters.Text(name) is { Length: > 0 } value) return Prefix(value, AntigravityAllowGrant.MaximumRuleBytes);
            return null;
        }
        if (tool == "run_command" || message.ToLowerInvariant().Contains("denied permission to run command", StringComparison.Ordinal))
        {
            var line = Parameter("CommandLine", "Command", "command");
            var rule = line is null ? null : "command(" + line + ")";
            return new("command", "command", tool, line, AntigravityAllowGrant.ValidRule(rule) ? rule : null);
        }
        if (NamedRule(message) is { } named)
        {
            var open = named.IndexOf('(');
            var name = named[..open]; var target = named[(open + 1)..^1];
            var kind = Kind(name, tool);
            // A URL rule names a domain; show the domain, as the rule does.
            if (kind == "url" && Host(target) is { Length: > 0 } host) target = host;
            return new(kind, name, tool, target, named);
        }
        var plain = Kind(tool, tool);
        var shown = plain switch
        {
            "url" => Parameter("Url", "URL", "url") is { } url ? Host(url) ?? url : null,
            "file" => Parameter("Path", "TargetFile", "AbsolutePath", "path"),
            "command" => Parameter("CommandLine"),
            _ => null,
        };
        return new(plain, tool, tool, shown);
    }

    /// <summary>The rule a denial names: the text after "denied permission for " up to the end of its line, when it is a whole <c>name(…)</c> rule.</summary>
    public static string? NamedRule(string message)
    {
        var at = message.IndexOf("denied permission for ", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;
        var rest = message[(at + "denied permission for ".Length)..];
        var end = rest.IndexOfAny(['\n', '\r']);
        var line = (end < 0 ? rest : rest[..end]).Trim(' ', '\t');
        // "User denied permission for write_file(/x)." ends its sentence.
        var rule = line.EndsWith(").", StringComparison.Ordinal) ? line[..^1] : line;
        return AntigravityAllowGrant.ValidRule(rule) ? rule : null;
    }

    /// <summary>The host of an absolute URL as written (scheme://[user@]host[:port]/…); null for anything else, a bare domain included.</summary>
    internal static string? Host(string value)
    {
        var match = Regex.Match(value, @"\A[A-Za-z][A-Za-z0-9+.\-]*://([^/?#]*)", RegexOptions.CultureInvariant);
        if (!match.Success) return null;
        var authority = match.Groups[1].Value;
        if (authority.LastIndexOf('@') is var user and >= 0) authority = authority[(user + 1)..];
        if (authority.StartsWith('[')) { var close = authority.IndexOf(']'); return close > 1 ? authority[1..close] : null; }
        var colon = authority.IndexOf(':');
        var host = colon >= 0 ? authority[..colon] : authority;
        return host.Length > 0 ? host : null;
    }

    /// <summary>The first <paramref name="count"/> characters, never cutting a surrogate pair.</summary>
    private static string Prefix(string value, int count)
    {
        if (value.Length <= count) return value;
        var cut = char.IsHighSurrogate(value[count - 1]) ? count - 1 : count;
        return value[..cut];
    }
}

/// <summary>
/// The cards of every pane: one per pane, the last turn's. A card stays until it is answered, or until a later run of that pane ends —
/// that run's own card (if any) replaces it, and one without any leaves none. Not thread-safe: its owner locks around it.
/// </summary>
public sealed class AntigravityPermissionBoard
{
    private readonly Dictionary<string, AntigravityPermissionRequest> cards = new(StringComparer.Ordinal);
    /// <summary>Panes whose card came from the run that is still ending.</summary>
    private readonly HashSet<string> fromRunningTurn = new(StringComparer.Ordinal);

    public AntigravityPermissionRequest? this[string sessionId] => cards.GetValueOrDefault(sessionId);
    public IReadOnlyDictionary<string, AntigravityPermissionRequest> Cards => cards;

    /// <summary>A turn ended with refusals (before its run's final status).</summary>
    public void Received(AntigravityPermissionRequest card, string sessionId) { cards[sessionId] = card; fromRunningTurn.Add(sessionId); }
    /// <summary>A run of the pane ended (completed, error or stopped). True when the pane's card went.</summary>
    public bool RunEnded(string sessionId) => !fromRunningTurn.Remove(sessionId) && cards.Remove(sessionId);
    /// <summary>Puts back a card whose answer could not run; the next run's end clears it as usual.</summary>
    public bool Restore(AntigravityPermissionRequest card, string sessionId) => cards.TryAdd(sessionId, card);
    public AntigravityPermissionRequest? Remove(string sessionId)
    {
        fromRunningTurn.Remove(sessionId);
        return cards.Remove(sessionId, out var card) ? card : null;
    }
    public AntigravityPermissionRequest? Pending(string sessionId, string requestId, string runId) =>
        cards.TryGetValue(sessionId, out var card) && card.Id == requestId && card.RunId == runId ? card : null;
}

/// <summary>What an answer does, decided without the app: the grant and the user turn sent on the same conversation.</summary>
public sealed record AntigravityContinuation(AntigravityAllowGrant Grant, string Prompt);

public static class AntigravityPermissionFlow
{
    /// <summary>Null for deny, and for an allow of a card no rule can allow.</summary>
    public static AntigravityContinuation? Continuation(AntigravityPermissionRequest card, AntigravityPermissionDecision decision)
    {
        if (decision == AntigravityPermissionDecision.Deny || !card.CanAllow) return null;
        var allowed = card.Actions.Where(a => a.Rule is not null).Select(a => a.Target ?? a.DisplayName);
        var list = ActivitySupport.Clean(string.Join(", ", allowed), 2_048, true);
        return new(new(card.Id, card.Rules, decision == AntigravityPermissionDecision.Once), Locale.Get("antigravity.permission.continue", new Dictionary<string, string> { ["actions"] = list }));
    }

    /// <summary>The short transcript line a card leaves (what an older phone shows).</summary>
    public static string TranscriptLine(AntigravityPermissionRequest card) =>
        Locale.Get("run.notice.antigravityAsked", new Dictionary<string, string> { ["actions"] = ActivitySupport.Clean(string.Join(", ", card.Names), 512, true) });

    /// <summary>The card's label for an action's kind.</summary>
    public static string KindLabel(string kind) => Locale.Get("antigravity.permission.kind." + AntigravityDeniedAction.KindOf(kind));

    /// <summary>The phone's and the desktop's answer to a permission card: allow alone is once, allow with always is always.</summary>
    public static AntigravityPermissionDecision Decision(bool allow, bool always) =>
        allow ? always ? AntigravityPermissionDecision.Always : AntigravityPermissionDecision.Once : AntigravityPermissionDecision.Deny;
}

/// <summary>Why agy's settings were left alone. <c>Kind</c> is the Mac's case name (missingFolder, unreadable, notJSON, notObject,
/// permissionsNotObject, allowNotArray, writeFailed, badRule); the message is the user's words.</summary>
public sealed class AntigravitySettingsException(string kind, string message) : InvalidOperationException(message)
{
    public string Kind { get; } = kind;
    internal static AntigravitySettingsException Of(string kind, string? path = null, string? error = null)
    {
        var values = new Dictionary<string, string>();
        if (path is not null) values["path"] = path;
        if (error is not null) values["error"] = error;
        return new(kind, Locale.Get("antigravity.settings.error." + kind, values));
    }
}

/// <summary>
/// Edits permissions.allow in the bytes of agy's settings, leaving every other byte as it was: other keys, their order and values,
/// unknown fields, whitespace. Rules are added after the last one in the style of the list (or of the file, for a list it has to
/// create), and removing one takes out exactly what adding it put in (M/MightyCore/AntigravityPermissions.swift AntigravitySettingsText).
/// </summary>
public static class AntigravitySettingsText
{
    /// <summary>The file's rules; throws when it is not an object, permissions is not an object or allow is not an array (non-string entries are skipped).</summary>
    public static IReadOnlyList<string> Rules(byte[] data, string path)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(data); }
        catch (JsonException) { throw AntigravitySettingsException.Of("notJSON", path); }
        using (document)
        {
            var top = document.RootElement;
            if (top.ValueKind != JsonValueKind.Object) throw AntigravitySettingsException.Of("notObject", path);
            if (Last(top, "permissions") is not { } permissions) return [];
            if (permissions.ValueKind != JsonValueKind.Object) throw AntigravitySettingsException.Of("permissionsNotObject", path);
            if (Last(permissions, "allow") is not { } allow) return [];
            if (allow.ValueKind != JsonValueKind.Array) throw AntigravitySettingsException.Of("allowNotArray", path);
            return allow.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();
        }
    }

    private static JsonElement? Last(JsonElement value, string key)
    {
        JsonElement? found = null;
        foreach (var member in value.EnumerateObject()) if (member.Name == key) found = member.Value;
        return found;
    }

    /// <summary>A new settings file that holds only these rules.</summary>
    public static byte[] NewFile(IReadOnlyList<string> rules) => Encoding.UTF8.GetBytes("{\n  \"permissions\": {\n    \"allow\": " + Array(rules, "    ", "  ") + "\n  }\n}\n");

    /// <summary><paramref name="rules"/> not in the file yet, added. Returns the bytes and what was added.</summary>
    public static (byte[] Data, IReadOnlyList<string> Added) Adding(IReadOnlyList<string> rules, byte[] data, string path)
    {
        var existing = Rules(data, path).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var missing = rules.Where(rule => !existing.Contains(rule) && seen.Add(rule)).ToList();
        if (missing.Count == 0) return (data, []);
        var b = new List<byte>(data);
        var scan = new Scanner(data, path);
        var top = scan.SkipWhitespace(0);
        if (top >= data.Length || data[top] != (byte)'{') throw AntigravitySettingsException.Of("notObject", path);
        var topObject = scan.Object(top);
        var unit = scan.LineIndent(topObject.Members.Count > 0 ? topObject.Members[0].KeyStart : -1) is { Length: > 0 } found ? found : "  ";
        if (topObject.Members.LastOrDefault(m => m.Key == "permissions") is not { } permissions)
        {
            InsertMember(b, scan, topObject, "", unit, indent => "\"permissions\": " + AllowObject(missing, indent, unit));
            return (b.ToArray(), missing);
        }
        var permissionsIndent = scan.LineIndent(permissions.KeyStart);
        var section = scan.Object(permissions.ValueStart);
        if (section.Members.LastOrDefault(m => m.Key == "allow") is not { } allow)
        {
            InsertMember(b, scan, section, permissionsIndent, unit, indent => "\"allow\": " + Array(missing, indent, unit));
            return (b.ToArray(), missing);
        }
        var list = scan.Array(allow.ValueStart);
        if (list.Elements.Count > 0)
        {
            var last = list.Elements[^1];
            var from = scan.SeparatorEnd(last.Start, list.Elements.Count > 1 ? list.Elements[^2].End : list.Open);
            var separator = data[from..last.Start];
            var inserted = new List<byte>();
            foreach (var rule in missing) { inserted.Add((byte)','); inserted.AddRange(separator); inserted.AddRange(Encoded(rule)); }
            b.InsertRange(last.End, inserted);
        }
        else
        {
            var rendered = Encoding.UTF8.GetBytes(Array(missing, scan.LineIndent(allow.KeyStart), unit));
            b.RemoveRange(list.Open, list.Close - list.Open + 1);
            b.InsertRange(list.Open, rendered);
        }
        return (b.ToArray(), missing);
    }

    /// <summary>One occurrence of each of <paramref name="rules"/> taken out of permissions.allow (the last one). Returns the bytes and what was removed.</summary>
    public static (byte[] Data, IReadOnlyList<string> Removed) Removing(IReadOnlyList<string> rules, byte[] data, string path)
    {
        _ = Rules(data, path);
        var b = data;
        var removed = new List<string>();
        foreach (var rule in rules.Distinct(StringComparer.Ordinal).OrderBy(r => r, StringComparer.Ordinal))
        {
            var scan = new Scanner(b, path);
            var topObject = scan.Object(scan.SkipWhitespace(0));
            if (topObject.Members.LastOrDefault(m => m.Key == "permissions") is not { } permissions) break;
            var section = scan.Object(permissions.ValueStart);
            if (section.Members.LastOrDefault(m => m.Key == "allow") is not { } allow) break;
            var list = scan.Array(allow.ValueStart);
            var elements = list.Elements;
            var index = -1;
            for (var at = elements.Count - 1; at >= 0; at--) if (scan.String(elements[at]) == rule) { index = at; break; }
            if (index < 0) continue;
            var next = new List<byte>(b);
            if (elements.Count == 1) next.RemoveRange(list.Open + 1, list.Close - list.Open - 1);
            else if (index == elements.Count - 1) next.RemoveRange(elements[index - 1].End, elements[index].End - elements[index - 1].End);
            else next.RemoveRange(elements[index].Start, elements[index + 1].Start - elements[index].Start);
            b = next.ToArray();
            removed.Add(rule);
        }
        // Whatever is left must still read as the same kind of file.
        _ = Rules(b, path);
        return (b, removed);
    }

    // Rendering

    /// <summary>A rule as a JSON string, as the Mac writes it: non-ASCII and slashes as they are.</summary>
    internal static byte[] Encoded(string rule)
    {
        var text = new StringBuilder("\"");
        foreach (var c in rule)
        {
            switch (c)
            {
                case '"': text.Append("\\\""); break;
                case '\\': text.Append("\\\\"); break;
                case '\b': text.Append("\\b"); break;
                case '\f': text.Append("\\f"); break;
                case '\n': text.Append("\\n"); break;
                case '\r': text.Append("\\r"); break;
                case '\t': text.Append("\\t"); break;
                default:
                    if (c < 0x20) text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture)); else text.Append(c);
                    break;
            }
        }
        return Encoding.UTF8.GetBytes(text.Append('"').ToString());
    }

    /// <summary>A list on its own lines under <paramref name="indent"/>, or on one line when the place it goes is one line (null).</summary>
    internal static string Array(IReadOnlyList<string> rules, string? indent, string unit)
    {
        var items = rules.Select(rule => Encoding.UTF8.GetString(Encoded(rule)));
        if (indent is null) return "[" + string.Join(", ", items) + "]";
        return "[\n" + string.Join(",\n", items.Select(item => indent + unit + item)) + "\n" + indent + "]";
    }

    internal static string AllowObject(IReadOnlyList<string> rules, string? indent, string unit) => indent is null
        ? "{\"allow\": " + Array(rules, null, unit) + "}"
        : "{\n" + indent + unit + "\"allow\": " + Array(rules, indent + unit, unit) + "\n" + indent + "}";

    /// <summary>
    /// Adds a member to <paramref name="value"/>: after its last member, in that member's style, or as the only member of an empty
    /// object. <paramref name="render"/> gets the member's indent (null when it goes on one line).
    /// </summary>
    private static void InsertMember(List<byte> b, Scanner scan, Scanner.ObjectSpan value, string? indentOfObject, string unit, Func<string?, string> render)
    {
        if (value.Members.Count > 0)
        {
            var last = value.Members[^1];
            var previousEnd = value.Members.Count > 1 ? value.Members[^2].ValueEnd : value.Open;
            var separator = scan.Bytes[scan.SeparatorEnd(last.KeyStart, previousEnd)..last.KeyStart];
            var inserted = new List<byte> { (byte)',' };
            inserted.AddRange(separator); inserted.AddRange(Encoding.UTF8.GetBytes(render(scan.LineIndent(last.KeyStart))));
            b.InsertRange(last.ValueEnd, inserted);
            return;
        }
        var text = indentOfObject is { } outer ? "{\n" + outer + unit + render(outer + unit) + "\n" + outer + "}" : "{" + render(null) + "}";
        b.RemoveRange(value.Open, value.Close - value.Open + 1);
        b.InsertRange(value.Open, Encoding.UTF8.GetBytes(text));
    }

    /// <summary>Just enough of a JSON reader to find where things are. The bytes were already read by JsonDocument; anything unexpected is notJSON.</summary>
    internal sealed class Scanner(byte[] bytes, string path)
    {
        internal sealed record Member(string Key, int KeyStart, int ValueStart, int ValueEnd);
        internal sealed record ObjectSpan(int Open, int Close, IReadOnlyList<Member> Members);
        internal readonly record struct Element(int Start, int End);
        internal sealed record ListSpan(int Open, int Close, IReadOnlyList<Element> Elements);
        internal byte[] Bytes => bytes;

        private AntigravitySettingsException Fail() => AntigravitySettingsException.Of("notJSON", path);
        private static bool Space(byte value) => value is 0x20 or 0x09 or 0x0A or 0x0D;
        internal int SkipWhitespace(int start)
        {
            var i = start;
            while (i < bytes.Length && Space(bytes[i])) i++;
            return i;
        }
        /// <summary>Index after the closing quote of the string at <paramref name="start"/>.</summary>
        private int StringEnd(int start)
        {
            if (start >= bytes.Length || bytes[start] != (byte)'"') throw Fail();
            var i = start + 1;
            while (i < bytes.Length)
            {
                if (bytes[i] == (byte)'\\') { i += 2; continue; }
                if (bytes[i] == (byte)'"') return i + 1;
                i++;
            }
            throw Fail();
        }
        internal string? String(Element range) => Decode(range.Start, range.End);
        private string? Decode(int start, int end)
        {
            if (start >= bytes.Length || bytes[start] != (byte)'"') return null;
            try { using var document = JsonDocument.Parse(bytes.AsMemory(start, end - start)); return document.RootElement.ValueKind == JsonValueKind.String ? document.RootElement.GetString() : null; }
            catch (JsonException) { return null; }
        }
        private int ValueEnd(int start)
        {
            if (start >= bytes.Length) throw Fail();
            switch (bytes[start])
            {
                case (byte)'"': return StringEnd(start);
                case (byte)'{': return Object(start).Close + 1;
                case (byte)'[': return Array(start).Close + 1;
                default:
                    var i = start;
                    while (i < bytes.Length && bytes[i] is not ((byte)',' or (byte)']' or (byte)'}') && !Space(bytes[i])) i++;
                    if (i <= start) throw Fail();
                    return i;
            }
        }
        internal ObjectSpan Object(int open)
        {
            if (open >= bytes.Length || bytes[open] != (byte)'{') throw Fail();
            var members = new List<Member>();
            var i = SkipWhitespace(open + 1);
            if (i < bytes.Length && bytes[i] == (byte)'}') return new(open, i, members);
            while (true)
            {
                var keyStart = i;
                var keyEnd = StringEnd(keyStart);
                var key = Decode(keyStart, keyEnd) ?? throw Fail();
                i = SkipWhitespace(keyEnd);
                if (i >= bytes.Length || bytes[i] != (byte)':') throw Fail();
                var valueStart = SkipWhitespace(i + 1);
                var end = ValueEnd(valueStart);
                members.Add(new(key, keyStart, valueStart, end));
                i = SkipWhitespace(end);
                if (i >= bytes.Length) throw Fail();
                if (bytes[i] == (byte)'}') return new(open, i, members);
                if (bytes[i] != (byte)',') throw Fail();
                i = SkipWhitespace(i + 1);
            }
        }
        internal ListSpan Array(int open)
        {
            if (open >= bytes.Length || bytes[open] != (byte)'[') throw AntigravitySettingsException.Of("allowNotArray", path);
            var elements = new List<Element>();
            var i = SkipWhitespace(open + 1);
            if (i < bytes.Length && bytes[i] == (byte)']') return new(open, i, elements);
            while (true)
            {
                var end = ValueEnd(i);
                elements.Add(new(i, end));
                i = SkipWhitespace(end);
                if (i >= bytes.Length) throw Fail();
                if (bytes[i] == (byte)']') return new(open, i, elements);
                if (bytes[i] != (byte)',') throw Fail();
                i = SkipWhitespace(i + 1);
            }
        }
        /// <summary>Where the whitespace before <paramref name="position"/> starts, after the comma (or bracket) that follows <paramref name="after"/>.</summary>
        internal int SeparatorEnd(int position, int after)
        {
            var i = position;
            while (i > after + 1 && Space(bytes[i - 1])) i--;
            return i;
        }
        /// <summary>The spaces and tabs before <paramref name="position"/> when nothing else is on its line before it.</summary>
        internal string? LineIndent(int position)
        {
            if (position < 0 || position > bytes.Length) return null;
            var i = position;
            while (i > 0 && bytes[i - 1] is 0x20 or 0x09) i--;
            if (i != 0 && bytes[i - 1] != 0x0A) return null;
            return Encoding.UTF8.GetString(bytes, i, position - i);
        }
    }
}

/// <summary>
/// Puts rules in agy's settings and takes them out again. One per app: the panes on this PC and the runs it hosts for other computers
/// share it, so a rule two of them hold at once stays until the last is done. Every rule it adds for a while is written to its ledger
/// first, and taken out of it only after the settings no longer have it, so a crash leaves nothing a next launch
/// (<see cref="RemoveLeftovers"/>) does not clean up. A rule the settings already had is never removed. Calls are serialized; each
/// reads and writes files, so callers keep them off the UI thread.
/// </summary>
public sealed class AntigravityRuleLeases
{
    /// <summary>The rules one run holds a count on (the ones the app added); released once.</summary>
    public sealed class Lease
    {
        internal Lease(IReadOnlyList<string> rules) { Rules = rules; }
        public IReadOnlyList<string> Rules { get; }
    }
    private sealed record LedgerEntry(string Settings, string Rule);
    private sealed record Ledger(int Version, List<LedgerEntry> Entries);

    public string SettingsPath { get; }
    private readonly string? ledgerPath;
    private readonly object gate = new();
    private readonly Dictionary<string, int> counts = new(StringComparer.Ordinal);
    private readonly HashSet<Lease> leases = [];

    /// <summary>%USERPROFILE%\.gemini\antigravity-cli\settings.json (on a Mac, ~/.gemini/antigravity-cli/settings.json).</summary>
    public static string DefaultSettingsPath(string? home = null) =>
        Path.Combine(home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity-cli", "settings.json");

    /// <param name="ledgerPath">Where the rules added for a while are recorded (the app's data folder); null keeps no record.</param>
    public AntigravityRuleLeases(string settingsPath, string? ledgerPath) { SettingsPath = settingsPath; this.ledgerPath = ledgerPath; }

    /// <summary>The rules the app holds for a while now, with their counts.</summary>
    public IReadOnlyDictionary<string, int> Held() { lock (gate) return new Dictionary<string, int>(counts, StringComparer.Ordinal); }

    /// <summary>Reads the settings the way adding would, and changes nothing.</summary>
    public void Check() { lock (gate) _ = Read(); }

    /// <summary>Puts <paramref name="grant"/>'s rules in place. A temporary grant returns the lease to <see cref="Release"/> when its run ends; one for good returns null.</summary>
    public Lease? Apply(AntigravityAllowGrant grant)
    {
        if (!grant.IsValid) throw AntigravitySettingsException.Of("badRule");
        lock (gate)
        {
            var current = Read();
            var existing = current.Rules.ToHashSet(StringComparer.Ordinal);
            var rules = grant.Rules.Distinct(StringComparer.Ordinal).ToList();
            if (!grant.Temporary)
            {
                // Kept for good: a rule the app holds for a while is no longer its to remove.
                var owned = rules.Where(counts.ContainsKey).ToList();
                Write(rules.Where(rule => !existing.Contains(rule)).ToList(), current);
                foreach (var rule in owned) counts.Remove(rule);
                try { Forget(owned); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AntigravitySettingsException) { }
                return null;
            }
            var held = new List<string>(); var added = new List<string>();
            foreach (var rule in rules)
            {
                if (counts.TryGetValue(rule, out var count)) { counts[rule] = count + 1; held.Add(rule); }
                else if (!existing.Contains(rule)) added.Add(rule);
            }
            if (added.Count > 0)
            {
                try { Record(added); Write(added, current); }
                catch
                {
                    try { Forget(added); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AntigravitySettingsException) { }
                    foreach (var rule in held) counts[rule] = counts.GetValueOrDefault(rule, 1) - 1;
                    throw;
                }
                foreach (var rule in added) counts[rule] = 1;
                held.AddRange(added);
            }
            var lease = new Lease(held);
            leases.Add(lease);
            return lease;
        }
    }

    /// <summary>The lease's run ended: a rule nobody holds any more leaves the settings.</summary>
    public void Release(Lease lease)
    {
        lock (gate)
        {
            if (!leases.Remove(lease)) return;
            var gone = new List<string>();
            foreach (var rule in lease.Rules)
            {
                if (!counts.TryGetValue(rule, out var count)) continue;
                if (count > 1) counts[rule] = count - 1; else { counts.Remove(rule); gone.Add(rule); }
            }
            Remove(gone);
        }
    }

    /// <summary>Quit: every rule held for a while leaves now.</summary>
    public void ReleaseAll()
    {
        lock (gate)
        {
            var gone = counts.Keys.ToList();
            counts.Clear(); leases.Clear();
            Remove(gone);
        }
    }

    /// <summary>Launch: what a previous run of the app added for a while and could not take out.</summary>
    public void RemoveLeftovers()
    {
        lock (gate)
        {
            var left = (ReadLedger()?.Entries ?? []).Where(e => e.Settings == SettingsPath && !counts.ContainsKey(e.Rule)).Select(e => e.Rule).Distinct(StringComparer.Ordinal).ToList();
            Remove(left);
        }
    }

    // Files

    private sealed record Current(byte[]? Data, IReadOnlyList<string> Rules);

    /// <summary>The settings file itself: a symbolic link is followed, so a linked file stays linked.</summary>
    private string Target()
    {
        try { if (new FileInfo(SettingsPath) is { LinkTarget: not null } link && link.ResolveLinkTarget(true) is { } final) return final.FullName; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return SettingsPath;
    }

    private Current Read()
    {
        var target = Target();
        var folder = Path.GetDirectoryName(target);
        if (folder is null || !Directory.Exists(folder)) throw AntigravitySettingsException.Of("missingFolder", folder ?? target);
        if (Directory.Exists(target)) throw AntigravitySettingsException.Of("unreadable", SettingsPath);
        if (!File.Exists(target)) return new(null, []);
        byte[] data;
        try { data = File.ReadAllBytes(target); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw AntigravitySettingsException.Of("unreadable", SettingsPath); }
        return new(data, AntigravitySettingsText.Rules(data, SettingsPath));
    }

    private void Write(IReadOnlyList<string> rules, Current current)
    {
        if (rules.Count == 0) return;
        Save(current.Data is { } data ? AntigravitySettingsText.Adding(rules, data, SettingsPath).Data : AntigravitySettingsText.NewFile(rules));
    }

    /// <summary>Takes <paramref name="rules"/> out of the settings, then out of the ledger. A file that cannot be read or written keeps them in the ledger for the next launch.</summary>
    private void Remove(IReadOnlyList<string> rules)
    {
        if (rules.Count == 0) return;
        try
        {
            var current = Read();
            if (current.Data is { } data)
            {
                var result = AntigravitySettingsText.Removing(rules, data, SettingsPath);
                if (result.Removed.Count > 0) Save(result.Data);
            }
            Forget(rules);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AntigravitySettingsException) { }
    }

    /// <summary>
    /// Writes beside the file and puts it in the file's place. On Windows File.Replace keeps the file's ACL, attributes and creation
    /// time; elsewhere its mode is copied. A symbolic link keeps pointing where it did.
    /// </summary>
    private void Save(byte[] data)
    {
        var target = Target();
        var folder = Path.GetDirectoryName(target)!;
        var temporary = Path.Combine(folder, "." + Path.GetFileName(target) + ".mighty-" + Guid.NewGuid().ToString("D") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(data); stream.Flush(true); }
            var exists = File.Exists(target);
            if (OperatingSystem.IsWindows())
            {
                if (exists) File.Replace(temporary, target, null, ignoreMetadataErrors: true); else File.Move(temporary, target);
            }
            else
            {
                File.SetUnixFileMode(temporary, exists ? File.GetUnixFileMode(target) : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                File.Move(temporary, target, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            try { File.Delete(temporary); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            throw AntigravitySettingsException.Of("writeFailed", SettingsPath, ex.Message);
        }
    }

    private Ledger? ReadLedger()
    {
        if (ledgerPath is null || !File.Exists(ledgerPath)) return null;
        try { return JsonSerializer.Deserialize<Ledger>(File.ReadAllBytes(ledgerPath), Wire.Json) is { Entries: not null } ledger ? ledger with { Entries = ledger.Entries.Where(e => e is { Settings: not null, Rule: not null }).ToList() } : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }

    private void WriteLedger(Ledger ledger)
    {
        if (ledgerPath is null) return;
        if (ledger.Entries.Count == 0) { if (File.Exists(ledgerPath)) File.Delete(ledgerPath); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(ledgerPath)!);
        var temporary = ledgerPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(ledger, new JsonSerializerOptions(Wire.Json) { WriteIndented = true }));
            File.Move(temporary, ledgerPath, overwrite: true);
        }
        catch { try { File.Delete(temporary); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { } throw; }
    }

    private void Record(IReadOnlyList<string> rules)
    {
        if (ledgerPath is null) return;
        var ledger = ReadLedger() ?? new(1, []);
        foreach (var rule in rules) if (!ledger.Entries.Contains(new(SettingsPath, rule))) ledger.Entries.Add(new(SettingsPath, rule));
        try { WriteLedger(ledger); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw AntigravitySettingsException.Of("writeFailed", ledgerPath, ex.Message); }
    }

    private void Forget(IReadOnlyList<string> rules)
    {
        if (ledgerPath is null || rules.Count == 0 || ReadLedger() is not { } ledger) return;
        var gone = rules.ToHashSet(StringComparer.Ordinal);
        ledger.Entries.RemoveAll(e => e.Settings == SettingsPath && gone.Contains(e.Rule));
        WriteLedger(ledger);
    }
}
