// The C# side of the Rust parity checks (native/tauri/tools/parity/run.ps1). Every command reads its input, runs the C# code the
// Windows app runs (MightyCode.Core, and MightyCode.WinUI/WebDelta.cs compiled in as it is) and writes what that code produces, so
// the Rust port (`cargo run -p mighty-core --example parity -- <same command>`) can be compared with it byte for byte.
//
//   save <state file | -> <out folder> [--lang ko|en|zh|ja] [--bak <file>] [--theme light|dark]
//        A profile holding the file (and the backup), loaded with StateStore.LoadAsync (normalize included) and saved with
//        SaveAsync, as the app does on its first save (with --theme: after UpdateAsync's normalize of that change). Writes
//        saved.json (the bytes C# wrote, when it wrote), page.json (the loaded state as the page is sent it) and report.json.
//   doubles <in> <out>   one IEEE-754 bit pattern (hex) a line -> Wire.Json's text of that double ("error" when it refuses one)
//   strings <in> <out>   one hex UTF-8 string a line -> Wire.Json's text of that string
//   times <in> <out>     "ticks:<n>" -> DateTimeOffset(n, +00:00).ToString("O"); "parse:<hex UTF-8>" -> AgentRunTiming.Parse's
//                        DateTimeOffset.TryParse(InvariantCulture, RoundtripKind) then ToString("O"), or "null"
//   delta <in> <out>     one AppSnapshot a line (Wire.Json) -> the bridge's snapshot push for the first, then the delta push the
//                        bridge's Flush (WebBridge.cs) sends for each next one ("" when it sends none)
//   schema <out>         each saved record's JSON property names in the order Wire.Json writes them
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MightyCode.Core;
using MightyCode.WinUI;

namespace MightyCode.Parity;

internal static class Program
{
    private static readonly UTF8Encoding Utf8 = new(false);

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Utf8;
        if (args.Length == 0) { Console.Error.WriteLine("parity save|doubles|strings|times|delta|schema ..."); return 2; }
        switch (args[0])
        {
            case "save": return await Save(args[1..]);
            case "save-corpus":
                // Every case folder of a corpus (its workspace-state.json, an optional .bak and an options.txt of save's options).
                foreach (var folder in Directory.GetDirectories(args[1]).Order(StringComparer.Ordinal))
                {
                    var state = Path.Combine(folder, "workspace-state.json");
                    var options = File.Exists(Path.Combine(folder, "options.txt")) ? File.ReadAllText(Path.Combine(folder, "options.txt")).Split(' ', StringSplitOptions.RemoveEmptyEntries) : [];
                    var bak = File.Exists(state + ".bak") ? new[] { "--bak", state + ".bak" } : [];
                    await Save([File.Exists(state) ? state : "-", Path.Combine(args[2], Path.GetFileName(folder)), .. options, .. bak]);
                }
                return 0;
            case "doubles": return Lines(args[1], args[2], Double);
            case "strings": return Lines(args[1], args[2], line => JsonSerializer.Serialize(Utf8.GetString(Convert.FromHexString(line)), Wire.Json));
            case "times": return Lines(args[1], args[2], Time);
            case "delta": return Delta(args[1], args[2]);
            case "schema": return Schema(args[1]);
            case "composer": return ComposerParity.Run(args[1], args[2]);
            default: Console.Error.WriteLine("unknown command " + args[0]); return 2;
        }
    }

    private static string? Option(string[] args, string name)
    {
        var at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    /// <summary>What kind of failure a load or a save was, as the Rust port names it.</summary>
    private static string Kind(Exception ex)
    {
        if (ex is InvalidDataException)
        {
            foreach (var key in new[] { "store.error.fileTooLarge", "store.error.invalidFormat", "store.error.noSnapshot", "store.error.stateTooLarge", "store.error.legacyFileTooLarge" })
                if (ex.Message == Locale.Get(key)) return key;
        }
        if (ex is InvalidOperationException && ex.Message == Locale.Get("store.error.pathNotApproved")) return "store.error.pathNotApproved";
        if (ex is InvalidOperationException && ex.Message == Locale.Get("store.error.notLoaded")) return "store.error.notLoaded";
        // Any other exception (a null dereferenced while it normalizes) fails the load the same way: the Rust port says "crash".
        return ex is JsonException ? "json" : ex is IOException ? "IOException" : "crash";
    }

    private static async Task<int> Save(string[] args)
    {
        var input = args[0]; var output = args[1];
        Locale.LanguagePreference = Option(args, "--lang") ?? "en"; Locale.ResetCache();
        Directory.CreateDirectory(output);
        foreach (var name in new[] { "saved.json", "page.json", "report.json" }) File.Delete(Path.Combine(output, name));
        var profile = Path.Combine(Path.GetTempPath(), "mighty-parity-cs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        var report = new JsonObject();
        try
        {
            var state = Path.Combine(profile, "workspace-state.json");
            if (input != "-") File.Copy(input, state);
            if (Option(args, "--bak") is { } bak) File.Copy(bak, state + ".bak");
            var store = new StateStore(profile);
            AppSnapshot loaded;
            try { loaded = await store.LoadAsync(); }
            catch (Exception ex)
            {
                report["load"] = Kind(ex);
                report["detail"] = ex.GetType().Name + ": " + ex.Message;
                report["notice"] = store.LoadNotice;
                File.WriteAllText(Path.Combine(output, "report.json"), report.ToJsonString(), Utf8);
                return 0;
            }
            report["load"] = "ok";
            report["notice"] = store.LoadNotice;
            File.WriteAllBytes(Path.Combine(output, "page.json"), JsonSerializer.SerializeToUtf8Bytes(WebDelta.PageSnapshot(loaded), Wire.Json));
            var next = loaded;
            if (Option(args, "--theme") is { } theme) next = StateStore.Normalize(loaded with { Theme = theme }, false);
            try
            {
                await store.SaveAsync(next);
                report["save"] = "ok";
                File.Copy(state, Path.Combine(output, "saved.json"));
            }
            catch (Exception ex) { report["save"] = Kind(ex); report["detail"] = ex.GetType().Name + ": " + ex.Message; }
            report["files"] = new JsonArray([.. Directory.GetFiles(profile).Select(f => (JsonNode)Path.GetFileName(f)!).Order(Comparer<JsonNode>.Create((a, b) => string.CompareOrdinal((string)a!, (string)b!)))]);
            File.WriteAllText(Path.Combine(output, "report.json"), report.ToJsonString(), Utf8);
            return 0;
        }
        finally { try { Directory.Delete(profile, true); } catch (IOException) { } }
    }

    private static int Lines(string input, string output, Func<string, string> each)
    {
        var lines = File.ReadAllLines(input, Utf8).Where(line => line.Length > 0).Select(each);
        File.WriteAllText(output, string.Join("\n", lines) + "\n", Utf8);
        return 0;
    }

    private static string Double(string line)
    {
        var value = BitConverter.Int64BitsToDouble(unchecked((long)ulong.Parse(line, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
        try { return JsonSerializer.Serialize(value, Wire.Json); }
        catch (ArgumentException) { return "error"; }
    }

    private static string Time(string line)
    {
        if (line.StartsWith("ticks:", StringComparison.Ordinal))
            return new DateTimeOffset(long.Parse(line[6..], CultureInfo.InvariantCulture), TimeSpan.Zero).ToString("O");
        var text = Utf8.GetString(Convert.FromHexString(line[6..]));
        // AgentRunTiming.Parse (internal): at most 80 characters, DateTimeOffset.TryParse with the invariant culture, round-trip kind.
        return text is { Length: > 0 and <= 80 } && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date) ? date.ToString("O") : "null";
    }

    /// <summary>
    /// WebBridge's SendSnapshot and Flush (MightyCode.WinUI/WebBridge.cs:110-186), without its timers and WebView2: the bridge's
    /// state and the delta it writes, line for line. Each state is read anew, so every pane is a new object and is compared by value
    /// (WebDelta.Write), as a pane a normalize made anew is.
    /// </summary>
    private static int Delta(string input, string output)
    {
        var states = File.ReadAllLines(input, Utf8).Where(line => line.Length > 0).Select(line => JsonSerializer.Deserialize<AppSnapshot>(line, Wire.Json)!).ToList();
        var lines = new List<string>();
        AppSnapshot? sent = null;
        var sentSessions = new Dictionary<string, RunSession>(StringComparer.Ordinal);
        var sentFields = new Dictionary<string, (RunSession Session, JsonObject Fields)>(StringComparer.Ordinal);
        List<string> sentOrder = [];
        string? sentState = null;
        void Remember(AppSnapshot current, List<string> order, string state)
        {
            sent = current; sentOrder = order; sentState = state; sentSessions.Clear();
            foreach (var session in current.Sessions) sentSessions[session.Id] = session;
            foreach (var id in sentFields.Keys.Where(id => !sentSessions.ContainsKey(id)).ToList()) sentFields.Remove(id);
        }
        static string StateJson(AppSnapshot current)
        {
            var node = JsonSerializer.SerializeToNode(current with { Sessions = [] }, Wire.Json)!.AsObject();
            node.Remove("sessions");
            return node.ToJsonString(Wire.Json);
        }
        var first = states[0];
        lines.Add(JsonSerializer.Serialize(new { kind = "snapshot", snapshot = WebDelta.PageSnapshot(first) }, Wire.Json));
        Remember(first, first.Sessions.Select(s => s.Id).ToList(), StateJson(first));
        foreach (var current in states.Skip(1))
        {
            if (ReferenceEquals(current, sent)) { lines.Add(""); continue; }
            var changed = new List<RunSession>(); var order = new List<string>(current.Sessions.Count);
            foreach (var session in current.Sessions)
            {
                order.Add(session.Id);
                if (!sentSessions.TryGetValue(session.Id, out var previous) || !ReferenceEquals(previous, session)) changed.Add(session);
            }
            var present = order.ToHashSet(StringComparer.Ordinal);
            var removed = sentSessions.Keys.Where(id => !present.Contains(id)).ToList();
            var state = StateJson(current);
            var stateChanged = state != sentState; var orderChanged = !order.SequenceEqual(sentOrder);
            if (changed.Count == 0 && removed.Count == 0 && !stateChanged && !orderChanged) { Remember(current, order, state); lines.Add(""); continue; }
            using var buffer = new MemoryStream(); var panes = 0;
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject(); writer.WriteString("kind", "delta");
                writer.WriteStartArray("sessions");
                foreach (var session in changed)
                {
                    if (!sentSessions.TryGetValue(session.Id, out var previous))
                    {
                        writer.WriteStartObject(); writer.WriteString("id", session.Id); writer.WritePropertyName("session");
                        JsonSerializer.Serialize(writer, session with { GraphRuns = WebDelta.PageRuns(session.GraphRuns) }, Wire.Json); writer.WriteEndObject();
                        panes++; continue;
                    }
                    var before = sentFields.TryGetValue(session.Id, out var known) && ReferenceEquals(known.Session, previous) ? known.Fields : WebDelta.Fields(previous);
                    var after = WebDelta.Fields(session);
                    if (WebDelta.Write(writer, previous, before, session, after)) panes++;
                    sentFields[session.Id] = (session, after);
                }
                writer.WriteEndArray();
                writer.WritePropertyName("removed"); JsonSerializer.Serialize(writer, removed, Wire.Json);
                if (orderChanged) { writer.WritePropertyName("order"); JsonSerializer.Serialize(writer, order, Wire.Json); }
                if (stateChanged) { writer.WritePropertyName("state"); writer.WriteRawValue(state, skipInputValidation: true); }
                writer.WriteEndObject();
            }
            Remember(current, order, state);
            if (panes == 0 && removed.Count == 0 && !stateChanged && !orderChanged) { lines.Add(""); continue; }
            lines.Add(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
        }
        File.WriteAllText(output, string.Join("\n", lines) + "\n", Utf8);
        return 0;
    }

    private static int Schema(string output)
    {
        Type[] types = [typeof(AppSnapshot), typeof(Workspace), typeof(RemoteWorkspaceReference), typeof(ModelDefaultsConfig), typeof(ProviderModeDefaults),
            typeof(RegisteredModelEntry), typeof(PhaseModelsSnapshot), typeof(MobileRemoteSettings), typeof(RunSession), typeof(LogEntry), typeof(AgentImageRef),
            typeof(MightyGraphRun), typeof(MightyGraphAgent), typeof(GraphTokenUsage), typeof(GraphResponseRecord), typeof(GraphAgentIdentity),
            typeof(GraphAgentFigures), typeof(GraphAdvisor), typeof(GraphAdvisorCall), typeof(FlowStep), typeof(GraphBlockSize), typeof(PlanRecord),
            typeof(TodoProgress), typeof(TodoItem), typeof(BackgroundWork), typeof(BackgroundTask), typeof(ShellBlockRun), typeof(PaneLayoutNode),
            typeof(SessionRateLimit)];
        var options = new JsonSerializerOptions(Wire.Json) { TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() };
        // A [JsonIgnore] property is listed by the resolver too; it is never written, so it is left out here.
        static bool Ignored(System.Text.Json.Serialization.Metadata.JsonPropertyInfo p) =>
            p.AttributeProvider is System.Reflection.MemberInfo member && member.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), true).Length > 0 || p.Get is null;
        var lines = types.Select(type => type.Name + ": " + string.Join(" ", options.GetTypeInfo(type).Properties.Where(p => !Ignored(p)).Select(p => p.Name)));
        File.WriteAllText(output, string.Join("\n", lines) + "\n", Utf8);
        return 0;
    }
}
