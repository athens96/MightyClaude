using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MightyClaude.Core;

/// <summary>
/// Antigravity permission cards (M/Tests/MightyCoreTests/AntigravityPermissionTests.swift): what a headless agy turn was refused, the
/// card, and the rules an allow puts in agy's settings for the follow-up turn. native/contracts/fixtures/antigravity-permissions.json is
/// the Mac's own file: its streams (recorded from agy 1.4.4 where it says so), its byte-exact settings edits and its /v2 and phone samples
/// are read here unchanged. Every settings file is a temporary one; the user's own is never read.
/// </summary>
internal static class AntigravityPermissionVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private static readonly Lazy<JsonNode> Fixture = new(() =>
    {
        using var stream = typeof(AntigravityPermissionVerification).Assembly.GetManifestResourceStream("MightyClaude.Core.Tests.AntigravityPermissions.json") ?? throw new InvalidOperationException("antigravity-permissions.json is not embedded");
        return JsonNode.Parse(stream)!;
    });
    private static JsonNode Settings => Fixture.Value["settings"]!;
    /// <summary>The real settings' shape, the first edit's starting point.</summary>
    private static string Real => Settings["edits"]![0]!["before"]!.GetValue<string>();

    private static async Task English(Func<Task> action)
    {
        var saved = Locale.LanguagePreference; Locale.LanguagePreference = "en"; Locale.ResetCache();
        try { await action(); }
        finally { Locale.LanguagePreference = saved; Locale.ResetCache(); }
    }

    private static (List<(string Kind, string Text)> Logs, List<AntigravityPermissionRequest> Cards) Parse(IEnumerable<string> lines, string provider = "gemini", bool cards = true)
    {
        var logs = new List<(string, string)>(); var found = new List<AntigravityPermissionRequest>();
        var parser = new OutputParser(provider, (kind, text) => logs.Add((kind, text)), _ => { }, activityNamespace: "run-1", antigravityPermission: cards ? found.Add : null);
        foreach (var line in lines) parser.Parse(line);
        parser.Flush();
        return (logs, found);
    }
    private static IReadOnlyList<string> Lines(JsonNode stream) => stream["lines"]!.AsArray().Select(l => l!.GetValue<string>()).ToList();
    private static JsonNode Stream(string name) => Fixture.Value["streams"]!.AsArray().First(s => s!["name"]!.GetValue<string>() == name)!;

    // Parsing

    internal static Task EveryFixtureTurnGivesItsActions() => English(() =>
    {
        foreach (var stream in Fixture.Value["streams"]!.AsArray())
        {
            var name = stream!["name"]!.GetValue<string>();
            var expected = stream["actions"].Deserialize<List<AntigravityDeniedAction>>(Wire.Json)!;
            var (logs, cards) = Parse(Lines(stream));
            if (expected.Count == 0)
            {
                Check(cards.Count == 0 && !logs.Any(l => l.Text.Contains("Antigravity declined", StringComparison.Ordinal)), name + ": a turn refused nothing leaves no card and no notice");
                continue;
            }
            Check(cards.Count == 1, name + ": one card");
            var card = cards[0];
            Check(card.Actions.SequenceEqual(expected), name + ": the fixture's actions, got " + JsonSerializer.Serialize(card.Actions, Wire.Json));
            Check(card.RunId == "run-1" && card.Id.StartsWith("agy-", StringComparison.Ordinal) && Wire.Identifier(card.Id), name + ": the card's ids");
            // One short transcript line, not the old how-to notice.
            var notices = logs.Where(l => l.Kind == "system" && l.Text.Contains("Antigravity declined", StringComparison.Ordinal)).ToList();
            Check(notices.Count == 1 && notices[0].Text.Contains("card", StringComparison.Ordinal) && !notices[0].Text.Contains("permissions.allow", StringComparison.Ordinal), name + ": one line pointing at the card");
        }
        return Task.CompletedTask;
    });

    internal static Task WithoutACardTheOlderNoticeStays() => English(() =>
    {
        var (logs, _) = Parse(Lines(Stream("command")), cards: false);
        Check(logs.Any(l => l.Kind == "system" && l.Text.Contains("permissions.allow", StringComparison.Ordinal) && l.Text.Contains("RunCommand", StringComparison.Ordinal)), "a parser nobody listens to for cards keeps the older notice");
        return Task.CompletedTask;
    });

    internal static Task TwoTurnsInOneRunGiveTwoCardsAndNothingCarriesOver()
    {
        var command = Lines(Stream("command")); var clean = Lines(Stream("noDenial"));
        var (_, cards) = Parse(command.Concat(clean).Concat(command));
        Check(cards.Count == 2 && cards[0].Id != cards[1].Id && cards.All(c => c.Actions.Count == 1), "two refused turns give two cards, the clean one between none");
        Check(Parse(command, provider: "claude").Cards.Count == 0, "another provider never builds a card");
        return Task.CompletedTask;
    }

    internal static Task RulesAreWellFormedOrAbsent()
    {
        Check(AntigravityAllowGrant.ValidRule("command(git status)") && AntigravityAllowGrant.ValidRule("write_file(/tmp/a b.txt)") && AntigravityAllowGrant.ValidRule("read_url(example.com)"), "rules agy writes are rules");
        foreach (var bad in new[] { "command()", "Command(ls)", "command(ls", "(ls)", "command(a\nb)", "command(\0)", "command(" + new string('x', 4_096) + ")" })
            Check(!AntigravityAllowGrant.ValidRule(bad), "not a rule: " + bad[..Math.Min(20, bad.Length)]);
        Check(AntigravityDenialCollector.NamedRule("x: user denied permission for write_file(/a).\nmore") == "write_file(/a)", "a named rule ends its sentence");
        Check(AntigravityDenialCollector.NamedRule("user denied permission for something") is null, "words that are no rule name none");
        using var exit = JsonDocument.Parse("{\"error\":{\"message\":\"exit status 1\"}}");
        Check(AntigravityDenialCollector.DenialMessage(exit.RootElement) is null && AntigravityDenialCollector.DenialMessage(default) is null, "a failure that is no denial is not one");
        return Task.CompletedTask;
    }

    // Settings bytes

    internal static Task SettingsEditsChangeOnlyTheAllowList()
    {
        static List<string> Strings(JsonNode? list) => list!.AsArray().Select(r => r!.GetValue<string>()).ToList();
        static JsonNode? WithoutAllow(string text, bool dropEmpty)
        {
            var node = JsonNode.Parse(text)!.AsObject();
            if (node["permissions"] is JsonObject permissions) { permissions.Remove("allow"); if (dropEmpty && permissions.Count == 0) node.Remove("permissions"); }
            return node;
        }
        foreach (var edit in Settings["edits"]!.AsArray())
        {
            var name = edit!["name"]!.GetValue<string>(); var before = edit["before"]!.GetValue<string>();
            var added = AntigravitySettingsText.Adding(Strings(edit["add"]), Encoding.UTF8.GetBytes(before), "s.json");
            Check(Encoding.UTF8.GetString(added.Data) == edit["afterAdd"]!.GetValue<string>(), name + " add: " + Encoding.UTF8.GetString(added.Data));
            Check(added.Added.SequenceEqual(Strings(edit["added"])), name + " added");
            var removed = AntigravitySettingsText.Removing(Strings(edit["remove"]), added.Data, "s.json");
            Check(Encoding.UTF8.GetString(removed.Data) == edit["afterRemove"]!.GetValue<string>(), name + " remove: " + Encoding.UTF8.GetString(removed.Data));
            // Whatever else the file held reads the same.
            var hadPermissions = JsonNode.Parse(before)!["permissions"] is not null;
            Check(JsonNode.DeepEquals(WithoutAllow(before, false), WithoutAllow(Encoding.UTF8.GetString(added.Data), !hadPermissions)), name + " other keys");
        }
        var created = Settings["newFile"]!;
        Check(Encoding.UTF8.GetString(AntigravitySettingsText.NewFile(Strings(created["add"]))) == created["after"]!.GetValue<string>(), "a new file holds the rule alone");
        return Task.CompletedTask;
    }

    internal static Task SettingsThatAreNotTheExpectedShapeAreRefused()
    {
        foreach (var refused in Settings["refused"]!.AsArray())
        {
            var name = refused!["name"]!.GetValue<string>(); var data = Encoding.UTF8.GetBytes(refused["before"]!.GetValue<string>()); var error = refused["error"]!.GetValue<string>();
            try { AntigravitySettingsText.Adding(["command(ls)"], data, "s.json"); throw new InvalidOperationException(name + ": added"); }
            catch (AntigravitySettingsException ex) { Check(ex.Kind == error, name + ": " + ex.Kind); }
            try { AntigravitySettingsText.Removing(["command(ls)"], data, "s.json"); throw new InvalidOperationException(name + ": removed"); }
            catch (AntigravitySettingsException ex) { Check(ex.Kind == error, name + " remove: " + ex.Kind); }
        }
        return Task.CompletedTask;
    }

    // The rules the app holds

    private sealed class Folder : IDisposable
    {
        internal readonly string Root = Verification.Temp();
        internal string SettingsPath => Path.Combine(Root, ".gemini", "antigravity-cli", "settings.json");
        internal string Ledger => Path.Combine(Root, "app", "antigravity-temporary-rules.json");
        internal void Make(string? text) { Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!); if (text is not null) File.WriteAllText(SettingsPath, text); }
        internal string? Text() => File.Exists(SettingsPath) ? File.ReadAllText(SettingsPath) : null;
        internal AntigravityRuleLeases Leases() => new(SettingsPath, Ledger);
        public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }
    }
    private static AntigravityAllowGrant Grant(string id, bool temporary, params string[] rules) => new(id, rules, temporary);
    private static string? Kind(Action action) { try { action(); return null; } catch (AntigravitySettingsException ex) { return ex.Kind; } }

    internal static Task ATemporaryRuleIsAddedRecordedAndTakenOutLeavingEveryOtherByte()
    {
        using var folder = new Folder(); folder.Make(Real);
        var leases = folder.Leases();
        var lease = leases.Apply(Grant("agy-1", true, "command(echo hi)")) ?? throw new InvalidOperationException("no lease");
        Check(folder.Text()!.Contains("\"command(echo hi)\"", StringComparison.Ordinal), "the rule is in the settings");
        Check(File.ReadAllText(folder.Ledger).Contains("command(echo hi)", StringComparison.Ordinal), "and in the ledger");
        Check(leases.Held().SequenceEqual([KeyValuePair.Create("command(echo hi)", 1)]), "held once");
        leases.Release(lease);
        Check(folder.Text() == Real && !File.Exists(folder.Ledger) && leases.Held().Count == 0, "released: every byte as it was, no ledger");
        leases.Release(lease);
        Check(folder.Text() == Real, "releasing twice changes nothing");
        return Task.CompletedTask;
    }

    internal static Task ARuleTwoPanesHoldStaysUntilTheLastIsDone()
    {
        using var folder = new Folder(); folder.Make(Real);
        var leases = folder.Leases();
        var first = leases.Apply(Grant("agy-1", true, "command(echo hi)", "command(echo hi)"))!;
        var second = leases.Apply(Grant("agy-2", true, "command(echo hi)"))!;
        Check(leases.Held()["command(echo hi)"] == 2 && folder.Text()!.Split("command(echo hi)").Length == 2, "two holds, one rule");
        leases.Release(first);
        Check(folder.Text()!.Contains("command(echo hi)", StringComparison.Ordinal), "still held by the second");
        leases.Release(second);
        Check(folder.Text() == Real, "gone with the last");
        return Task.CompletedTask;
    }

    internal static Task ARuleTheSettingsAlreadyHadIsNeverRemoved()
    {
        using var folder = new Folder(); folder.Make(Real);
        var leases = folder.Leases();
        var lease = leases.Apply(Grant("agy-1", true, "command(ls)"))!;
        Check(lease.Rules.Count == 0 && folder.Text() == Real, "a rule already there is not added");
        leases.Release(lease);
        Check(folder.Text() == Real, "nor removed");
        // Always: kept, and a temporary hold on the same rule no longer removes it.
        var held = leases.Apply(Grant("agy-2", true, "command(echo hi)"))!;
        Check(leases.Apply(Grant("agy-3", false, "command(echo hi)", "command(pwd)")) is null, "always has no lease");
        leases.Release(held);
        Check(folder.Text()!.Contains("\"command(echo hi)\"", StringComparison.Ordinal) && folder.Text()!.Contains("\"command(pwd)\"", StringComparison.Ordinal), "always keeps both");
        Check(leases.Held().Count == 0 && !File.Exists(folder.Ledger), "nothing held, nothing recorded");
        return Task.CompletedTask;
    }

    internal static Task SettingsThatCannotBeReadAreLeftAloneAndRefused()
    {
        using var folder = new Folder();
        var grant = Grant("agy-1", true, "command(echo hi)");
        // No folder: agy was never set up here.
        Check(Kind(() => folder.Leases().Apply(grant)) == "missingFolder", "no folder");
        foreach (var (text, expected) in new[] { ("{ broken", "notJSON"), ("{\"permissions\": {\"allow\": 3}}", "allowNotArray") })
        {
            folder.Make(text);
            var leases = folder.Leases();
            Check(Kind(() => leases.Apply(grant)) == expected && Kind(leases.Check) == expected, text + ": " + expected);
            Check(folder.Text() == text && !File.Exists(folder.Ledger), text + ": left as it was");
        }
        // A rule that is not one is refused before the file is read.
        Check(Kind(() => folder.Leases().Apply(Grant("agy-1", true, "rm -rf /"))) == "badRule", "a bad rule");
        return Task.CompletedTask;
    }

    internal static Task AMissingFileIsCreatedWithTheRuleAlone()
    {
        using var folder = new Folder(); folder.Make(null);
        var leases = folder.Leases();
        var lease = leases.Apply(Grant("agy-1", true, "command(echo hi)"))!;
        Check(folder.Text() == Settings["newFile"]!["after"]!.GetValue<string>(), "the new file");
        leases.Release(lease);
        Check(folder.Text() == "{\n  \"permissions\": {\n    \"allow\": []\n  }\n}\n", "an empty list after");
        Check(Directory.GetFileSystemEntries(Path.GetDirectoryName(folder.SettingsPath)!).Select(Path.GetFileName).SequenceEqual(["settings.json"]), "nothing else was left in the folder");
        return Task.CompletedTask;
    }

    internal static Task WhatACrashLeftIsRemovedAtTheNextLaunch()
    {
        using var folder = new Folder(); folder.Make(Real);
        // The app ends here without releasing.
        folder.Leases().Apply(Grant("agy-1", true, "command(echo hi)", "command(ls)"));
        Check(folder.Text()!.Contains("command(echo hi)", StringComparison.Ordinal), "left behind");
        folder.Leases().RemoveLeftovers();
        Check(folder.Text() == Real && !File.Exists(folder.Ledger), "the next launch takes it out");
        // A ledger whose settings cannot be read is kept for a later launch.
        folder.Leases().Apply(Grant("agy-2", true, "command(echo hi)"));
        var saved = folder.Text()!;
        File.WriteAllText(folder.SettingsPath, "{ broken");
        folder.Leases().RemoveLeftovers();
        Check(File.Exists(folder.Ledger), "kept while the settings cannot be read");
        File.WriteAllText(folder.SettingsPath, saved);
        folder.Leases().RemoveLeftovers();
        Check(folder.Text() == Real && !File.Exists(folder.Ledger), "taken out once they can");
        return Task.CompletedTask;
    }

    internal static Task QuitReleasesEverythingAndALinkedFileStaysLinked()
    {
        using var folder = new Folder(); folder.Make(null);
        var target = Path.Combine(folder.Root, "dotfiles", "agy-settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllText(target, Real);
        // A symbolic link needs a privilege on Windows: there the file itself stands in, and File.Replace keeps its ACL and attributes.
        var linked = !OperatingSystem.IsWindows();
        if (linked) { File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite); File.CreateSymbolicLink(folder.SettingsPath, target); }
        else target = folder.SettingsPath;
        if (!linked) File.WriteAllText(target, Real);
        var leases = folder.Leases();
        leases.Apply(Grant("agy-1", true, "command(echo hi)"));
        leases.Apply(Grant("agy-2", true, "command(pwd)"));
        Check(File.ReadAllText(target).Contains("command(pwd)", StringComparison.Ordinal), "written where the link points");
        if (linked)
        {
            Check(new FileInfo(folder.SettingsPath).LinkTarget is not null, "the link is still a link");
            Check(File.GetUnixFileMode(target) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "its mode is kept");
        }
        leases.ReleaseAll();
        Check(File.ReadAllText(target) == Real && leases.Held().Count == 0, "quit takes every temporary rule out");
        return Task.CompletedTask;
    }

    // The card

    private static readonly AntigravityPermissionRequest Card = new("agy-card", "run-1", [
        new("command", "command", "RunCommand", "curl -s https://example.com", "command(curl -s https://example.com)"),
        new("other", "notebook_execution", "NotebookExecution"),
    ], "2026-10-22T00:00:00Z");

    internal static Task ACardStaysUntilALaterRunEndsOrItIsAnswered()
    {
        var board = new AntigravityPermissionBoard();
        board.Received(Card, "pane");
        // The run that refused it ends: the card stays.
        board.RunEnded("pane");
        Check(board["pane"] == Card && board.Pending("pane", "agy-card", "run-1") == Card && board.Pending("pane", "agy-card", "run-2") is null, "the card waits for its answer");
        // A later run of the pane ends without one: it goes.
        board.RunEnded("pane");
        Check(board["pane"] is null, "a later run's end takes it");
        // A later run's own card replaces it.
        board.Received(Card, "pane"); board.RunEnded("pane");
        var next = Card with { Id = "agy-next" };
        board.Received(next, "pane"); board.RunEnded("pane");
        Check(board["pane"]?.Id == "agy-next", "the newer card replaces it");
        // Answered, then put back when its run could not start.
        Check(board.Remove("pane")?.Id == "agy-next", "answered");
        board.Restore(next, "pane");
        Check(board["pane"]?.Id == "agy-next", "put back");
        board.RunEnded("pane");
        Check(board["pane"] is null, "and the next run's end clears it as usual");
        return Task.CompletedTask;
    }

    internal static Task AnAllowContinuesWithTheRulesAndADenyDoesNothing() => English(() =>
    {
        Check(AntigravityPermissionFlow.Continuation(Card, AntigravityPermissionDecision.Deny) is null, "deny does nothing");
        var once = AntigravityPermissionFlow.Continuation(Card, AntigravityPermissionDecision.Once)!;
        Check(once.Grant.Same(new("agy-card", ["command(curl -s https://example.com)"], true)) && once.Grant.IsValid, "once holds the rule for the run");
        Check(once.Prompt.Contains("curl -s https://example.com", StringComparison.Ordinal) && !once.Prompt.Contains("NotebookExecution", StringComparison.Ordinal) && once.Prompt.Contains("continue", StringComparison.Ordinal), "the continuation names what was allowed: " + once.Prompt);
        var always = AntigravityPermissionFlow.Continuation(Card, AntigravityPermissionDecision.Always)!;
        Check(!always.Grant.Temporary && always.Grant.Rules.SequenceEqual(once.Grant.Rules), "always keeps the same rules");
        var other = Card with { Actions = [Card.Actions[1]] };
        Check(!other.CanAllow && AntigravityPermissionFlow.Continuation(other, AntigravityPermissionDecision.Once) is null, "nothing a rule can allow: no continuation");
        Check(AntigravityPermissionFlow.TranscriptLine(Card).Contains("NotebookExecution", StringComparison.Ordinal), "the transcript line names every action");
        return Task.CompletedTask;
    });

    // Wire

    internal static Task TheCardRidesARunEventAndAnUnknownKindReadsAsOther()
    {
        var value = new RunEvent("pane", "antigravityPermission", AntigravityPermission: Card);
        var node = JsonSerializer.SerializeToNode(value, RemoteWire.Json)!.AsObject();
        var back = node.Deserialize<RunEvent>(RemoteWire.Json)!;
        Check(back.AntigravityPermission is { } read && read.Id == Card.Id && read.RunId == Card.RunId && read.ReceivedAt == Card.ReceivedAt && read.Actions.SequenceEqual(Card.Actions) && back.Valid(), "the card travels and reads back");
        node["antigravityPermission"]!["actions"]![0]!["kind"] = "network";
        Check(node.Deserialize<RunEvent>(RemoteWire.Json)!.AntigravityPermission!.Actions[0].Kind == "other", "an unknown kind reads as other");
        // A remote client takes the card only in its bounds.
        Check(RemoteWorkspaceValidation.AntigravityCard(Card), "the card is in bounds");
        Check(!new RunEvent("pane", "antigravityPermission", AntigravityPermission: Card with { Actions = [Card.Actions[0] with { Rule = "rm -rf /" }] }).Valid(), "a malformed rule is refused");
        Check(!new RunEvent("pane", "antigravityPermission", AntigravityPermission: Card with { Actions = [] }).Valid(), "an empty card is refused");
        // A run start carries the grant beside the request; the request itself does not.
        var request = new StartRunRequest("pane", "ws", "claude", "go on", [], Provider: "gemini") { AntigravityGrant = new("agy-card", ["command(ls)"], true) };
        Check(!JsonSerializer.Serialize(request, Wire.Json).Contains("antigravity", StringComparison.OrdinalIgnoreCase) && request.Validate().AntigravityGrant is not null, "never saved with the request, kept through validation");
        Check(RemoteWireStart.From(request with { Provider = "claude" }, "ws").AntigravityGrant is null && RemoteWireStart.From(request, "ws").AntigravityGrant is not null, "only an Antigravity start carries it");
        return Task.CompletedTask;
    }

    internal static Task ThePhoneSeesTheCardAsAPermissionAnOlderPhoneCanAnswer()
    {
        var permission = MobilePermission.FromAntigravity(Card);
        var node = JsonSerializer.SerializeToNode(permission, Wire.Json)!.AsObject();
        Check(node["id"]!.GetValue<string>() == "agy-card" && node["runId"]!.GetValue<string>() == "run-1" && node["toolName"]!.GetValue<string>() == "AntigravityPermission", "ids and tool name");
        Check(node["canAllow"]!.GetValue<bool>() && node["title"]!.GetValue<string>() == Locale.Get("antigravity.permission.title"), "allowable, with the card's title");
        var fields = node["fields"]!.AsArray().Select(f => (f!["label"]!.GetValue<string>(), f["value"]!.GetValue<string>())).ToList();
        Check(fields.SequenceEqual([(Locale.Get("antigravity.permission.kind.command"), "curl -s https://example.com"), (Locale.Get("antigravity.permission.kind.other"), "NotebookExecution")]), "every action is a field an older phone shows");
        var actions = node["antigravity"]!["actions"]!.AsArray();
        Check(actions.Count == 2 && actions[0]!["kind"]!.GetValue<string>() == "command" && actions[0]!["rule"]!.GetValue<string>() == "command(curl -s https://example.com)", "the structured actions");
        Check(actions[1]!["rule"] is null && actions[1]!["target"] is null && actions[1]!["displayName"]!.GetValue<string>() == "NotebookExecution", "absent fields stay absent");
        Check(JsonSerializer.SerializeToNode(MobilePermission.From(new ToolPermissionRequest("p", "r", "t", "Bash", "{}", "ls")), Wire.Json)!["antigravity"] is null, "a Claude permission carries no card");
        Check(AntigravityPermissionFlow.Decision(true, false) == AntigravityPermissionDecision.Once && AntigravityPermissionFlow.Decision(true, true) == AntigravityPermissionDecision.Always && AntigravityPermissionFlow.Decision(false, false) == AntigravityPermissionDecision.Deny, "the answers");
        return Task.CompletedTask;
    }

    /// <summary>The Mac's /v2 samples in the shared fixture: read, checked and written back by the Windows wire as the Mac writes them.</summary>
    internal static Task TheRemoteSamplesAreTheMacsOwnAndPassTheClientsChecks()
    {
        var remote = Fixture.Value["remote"]!;
        var pollNode = remote["pollEvent"]!;
        var poll = pollNode.Deserialize<RemoteWirePoll>(RemoteWire.Json)!;
        Check(RemoteWireFixtureVerification.Same(pollNode, JsonSerializer.SerializeToNode(poll, RemoteWire.Json)), "the poll writes back as it came");
        var read = poll.Events.Select(e => RemoteWireEvents.Read(e.Event, "job-1", "pane", graphRuns: false)).ToList();
        Check(read.All(r => r.Usable && r.Event is not null), "every event is one a Windows client uses");
        var card = read[0].Event!.AntigravityPermission ?? throw new InvalidOperationException("no card");
        Check(read[0].Event!.SessionId == "pane" && card.Rules.SequenceEqual(["command(curl -s https://example.com | grep -i '<title>')"]) && card.Actions[1].Rule is null, "the card becomes the pane's");
        // A Windows host writes the same event for the same card.
        var written = RemoteWireEvents.FromRunEvent(new RunEvent("job-1", "antigravityPermission", AntigravityPermission: card), "job-1", graphRuns: false);
        Check(RemoteWireFixtureVerification.Same(poll.Events[0].Event, written), "a Windows host writes the Mac's event: " + written?.ToJsonString());
        var startNode = remote["start"]!;
        var start = startNode.Deserialize<RemoteWireStart>(RemoteWire.Json)!;
        Check(RemoteWireFixtureVerification.Same(startNode, JsonSerializer.SerializeToNode(start, RemoteWire.Json)), "the start writes back as it came");
        Check(start.AntigravityGrant is { IsValid: true, Temporary: true } grant && grant.RequestId == card.Id && grant.Rules.SequenceEqual(card.Rules), "the grant names the card's rules");
        Check(start.Request.Provider == "gemini" && start.Request.ResumeId is not null, "a continuation of the same conversation");
        var pane = start.ToRequest("pane-3", "ws-project") with { AntigravityGrant = start.AntigravityGrant };
        Check(RemoteWireFixtureVerification.Same(startNode, JsonSerializer.SerializeToNode(RemoteWireStart.From(pane, "ws-project"), RemoteWire.Json)), "a Windows client writes the Mac's start");
        return Task.CompletedTask;
    }

    internal static Task ThePhoneSampleIsWhatTheMacSends() => English(() =>
    {
        var mobile = Fixture.Value["mobile"]!;
        var poll = Fixture.Value["remote"]!["pollEvent"]!.Deserialize<RemoteWirePoll>(RemoteWire.Json)!;
        var card = RemoteWireEvents.Read(poll.Events[0].Event, "job-1", "pane", false).Event!.AntigravityPermission!;
        var sent = JsonSerializer.SerializeToNode(MobilePermission.FromAntigravity(card), Wire.Json);
        Check(RemoteWireFixtureVerification.Same(mobile["permission"], sent), "the phone sample is what this host sends: " + sent?.ToJsonString());
        foreach (var answer in mobile["answers"]!.AsArray())
        {
            var body = answer!["body"]!; var allow = body["allow"]!.GetValue<bool>(); var always = body["always"]?.GetValue<bool>() == true;
            if (answer["decision"] is { } decision) Check(AntigravityPermissionFlow.Decision(allow, always).ToString().Equals(decision.GetValue<string>(), StringComparison.OrdinalIgnoreCase), "answer " + decision);
            else Check(always && !allow && answer["status"]!.GetValue<int>() == 400, "always without allow is refused");
        }
        return Task.CompletedTask;
    });

    // Runs

    /// <summary>The fake agy (Verification.FakeCliAsync --agy-denied): refused a command on its first turn and, continued, says whether its settings allow it.</summary>
    internal static void FakeAgy(string settings, bool continued)
    {
        const string conversation = "11111111-2222-4333-8444-555555555555";
        Console.WriteLine("{\"event\":\"init\",\"conversation_id\":\"" + conversation + "\"}");
        if (continued)
        {
            var answer = File.Exists(settings) && File.ReadAllText(settings).Contains("command(echo hi)", StringComparison.Ordinal) ? "allowed" : "missing";
            Console.WriteLine("{\"event\":\"result\",\"result\":{\"conversation_id\":\"" + conversation + "\",\"status\":\"SUCCESS\",\"response\":\"" + answer + "\"}}");
            return;
        }
        Console.WriteLine("{\"event\":\"step_update\",\"step_update\":{\"step_index\":2,\"state\":\"ERROR\",\"step_type\":\"tool\",\"tool_name\":\"run_command\",\"tool_info\":{\"name\":\"run_command\",\"parameters\":{\"CommandLine\":\"echo hi\"},\"error\":{\"type\":\"TOOL_ERROR\",\"message\":\"permission check failed for unsandboxed \\\"echo hi\\\": user denied permission to run command:\\necho hi\\nDo not attempt to circumvent this denial.\"}}}}");
        Console.WriteLine("{\"event\":\"result\",\"result\":{\"conversation_id\":\"" + conversation + "\",\"status\":\"SUCCESS\",\"response\":\"\",\"denied_actions\":[{\"action\":\"command\",\"display_name\":\"RunCommand\"}]}}");
    }
    private const string Conversation = "11111111-2222-4333-8444-555555555555";
    private static ProviderCatalog FakeAgyCatalog(Folder folder) =>
        new((_, _) => Task.FromResult<CliCommand?>(Verification.Self("--fake-cli", "gemini", Path.Combine(folder.Root, "agy-record"), "--agy-denied", folder.SettingsPath)));
    private static bool Ended(RunEvent value) => value.Type == "status" && value.Status is "completed" or "error" or "stopped";

    internal static async Task TheRunnerSendsTheCardBeforeTheRunsEnd()
    {
        using var folder = new Folder(); folder.Make(Real);
        var events = new ConcurrentQueue<RunEvent>(); var workspace = new Workspace { Path = folder.Root };
        await using var catalog = FakeAgyCatalog(folder);
        await using (var manager = new RunManager(_ => Task.FromResult(workspace), catalog, "", events.Enqueue))
        {
            await manager.StartAsync(new("pane", workspace.Id, "claude", "say hi", [], Provider: "gemini"));
            await Verification.Until(() => events.Any(Ended), 20000);
        }
        var values = events.ToList();
        var card = values.FindIndex(e => e.Type == "antigravityPermission"); var end = values.FindIndex(e => e.Type == "status" && e.Status == "completed");
        Check(card >= 0 && end > card, "the card comes before the run's end: " + string.Join(",", values.Select(e => e.Type + ":" + e.Status)));
        Check(values[card].AntigravityPermission is { } sent && sent.Rules.SequenceEqual(["command(echo hi)"]) && sent.Actions[0].DisplayName == "RunCommand" && values[card].Valid(), "the card names the command");
        Check(folder.Text() == Real, "a run alone never touches the settings");
    }

    /// <summary>
    /// A pane's card through the desktop service: it waits past its run's end, its continuation (once) puts the rule in place while the
    /// follow-up runs on the same conversation and takes it out after, the title stays, and always keeps it.
    /// </summary>
    internal static async Task APanesContinuationHoldsTheRuleForItsRunAndKeepsTheTitle()
    {
        using var folder = new Folder(); folder.Make(Real);
        await using var catalog = FakeAgyCatalog(folder);
        var (service, pane, workspace) = await Desktop(folder, catalog);
        await using (service)
        {
            var card = await FirstTurn(service, pane, workspace);
            var title = service.Snapshot.Sessions.Single(s => s.Id == pane).Title;
            Check(service.Snapshot.Sessions.Single(s => s.Id == pane).ResumeId == Conversation, "the conversation to continue");
            var once = AntigravityPermissionFlow.Continuation(card, AntigravityPermissionDecision.Once)!;
            Check(service.TakeAntigravityCard(pane) == card && service.AntigravityCard(pane) is null, "answered: the card goes");
            await service.StartReachedAsync(new StartRunRequest(pane, workspace, "claude", once.Prompt, [], Provider: "gemini", ResumeId: Conversation) { AntigravityGrant = once.Grant });
            await Verification.Until(() => Runs(service, pane) == 2 && service.Snapshot.Sessions.Single(s => s.Id == pane).Status == "completed", 20000);
            var logs = service.Snapshot.Sessions.Single(s => s.Id == pane).Logs;
            Check(logs.Any(l => l.Kind == "assistant" && l.Text == "allowed"), "the continuation ran with the rule in place: " + string.Join(" | ", logs.Select(l => l.Kind + ":" + l.Text)));
            await Verification.Until(() => folder.Text() == Real, 10000);
            Check(!File.Exists(Path.Combine(folder.Root, "state", "antigravity-temporary-rules.json")) && service.AntigravityRules.Held().Count == 0, "the rule left with its run, and the ledger with it");
            Check(service.Snapshot.Sessions.Single(s => s.Id == pane).Title == title, "the pane's title is not the continuation's words");
            // Always keeps the rule.
            card = await FirstTurn(service, pane, workspace, runs: 3);
            var always = AntigravityPermissionFlow.Continuation(card, AntigravityPermissionDecision.Always)!;
            service.TakeAntigravityCard(pane);
            await service.StartReachedAsync(new StartRunRequest(pane, workspace, "claude", always.Prompt, [], Provider: "gemini", ResumeId: Conversation) { AntigravityGrant = always.Grant });
            await Verification.Until(() => Runs(service, pane) == 4 && service.Snapshot.Sessions.Single(s => s.Id == pane).Status == "completed", 20000);
            Check(folder.Text()!.Contains("\"command(echo hi)\"", StringComparison.Ordinal) && service.AntigravityRules.Held().Count == 0, "always keeps the rule for good");
        }
    }

    /// <summary>A continuation agy's settings refuse never starts: the pane says why, and nothing is changed.</summary>
    internal static async Task AContinuationTheSettingsRefuseNeverStarts()
    {
        using var folder = new Folder(); folder.Make(Real);
        await using var catalog = FakeAgyCatalog(folder);
        var (service, pane, workspace) = await Desktop(folder, catalog);
        await using (service)
        {
            var card = await FirstTurn(service, pane, workspace);
            File.WriteAllText(folder.SettingsPath, "{ broken");
            var once = AntigravityPermissionFlow.Continuation(card, AntigravityPermissionDecision.Once)!;
            await RemoteClientVerification.RejectAsync(() => service.CheckAntigravitySettingsAsync(), "the check reads the settings first", ex => ex is AntigravitySettingsException { Kind: "notJSON" });
            await RemoteClientVerification.RejectAsync(() => service.StartReachedAsync(new StartRunRequest(pane, workspace, "claude", once.Prompt, [], Provider: "gemini", ResumeId: Conversation) { AntigravityGrant = once.Grant }),
                "a start whose rules cannot go in place", ex => ex is AntigravitySettingsException { Kind: "notJSON" });
            var session = service.Snapshot.Sessions.Single(s => s.Id == pane);
            Check(session.Status == "error" && session.Logs.Last(l => l.Kind == "error").Text == Locale.Get("antigravity.settings.error.notJSON", new Dictionary<string, string> { ["path"] = folder.SettingsPath }), "the pane says why");
            Check(folder.Text() == "{ broken" && !File.Exists(Path.Combine(folder.Root, "state", "antigravity-temporary-rules.json")), "nothing was written");
            service.RestoreAntigravityCard(pane, card);
            Check(service.AntigravityCard(pane) == card, "the card can come back with the reason");
        }
    }

    /// <summary>The phone answers a card on the permission route (M/Tests/MightyCoreTests/MobileRemoteTests.swift antigravityCardsAreAnsweredOnThePermissionRoute).</summary>
    internal static async Task AntigravityCardsAreAnsweredOnThePermissionRoute()
    {
        using var folder = new Folder(); folder.Make(Real);
        await using var catalog = FakeAgyCatalog(folder);
        var (service, pane, workspace) = await Desktop(folder, catalog);
        await using (service)
        {
            var card = await FirstTurn(service, pane, workspace);
            var actions = new List<MobileDesktopAction>(); string? failure = null;
            using var router = new MobileDesktopRouter(service, Path.Combine(folder.Root, "mobile"), "Fixture", (action, _) => { actions.Add(action); return Task.FromResult<object?>(failure); }, _ => new([]));
            async Task<(int Status, JsonElement Body)> Post(object body)
            {
                var reply = await router.RouteAsync("POST", "/m1/sessions/" + pane + "/permission", JsonSerializer.SerializeToElement(body), "phone", CancellationToken.None);
                return (reply.Status, JsonSerializer.SerializeToElement(reply.Body, Wire.Json));
            }
            var detail = JsonSerializer.SerializeToElement((await router.RouteAsync("GET", "/m1/sessions/" + pane, null, "phone", CancellationToken.None)).Body, Wire.Json);
            var shown = detail.GetProperty("permissions").EnumerateArray().Single();
            Check(shown.Text("toolName") == "AntigravityPermission" && shown.Text("id") == card.Id && shown.Text("runId") == card.RunId && shown.GetProperty("antigravity").GetProperty("actions").GetArrayLength() == 1, "the session detail carries the card");
            Check(detail.GetProperty("session").GetProperty("pendingPermissions").GetInt32() == 1, "the card counts as one waiting permission");
            Check((await Post(new { requestId = card.Id, runId = card.RunId, allow = true })).Status == 200, "allow");
            Check((await Post(new { requestId = card.Id, runId = card.RunId, allow = true, always = true })).Status == 200, "always");
            Check((await Post(new { requestId = card.Id, runId = card.RunId, allow = false, always = false })).Status == 200, "deny");
            Check(actions.Select(a => a.Action + ":" + a.Body.Text("requestId") + ":" + a.Body.Text("runId") + ":" + a.Body.Text("decision")).SequenceEqual(new[] { "once", "always", "deny" }.Select(d => "antigravityPermission:" + card.Id + ":" + card.RunId + ":" + d)), "each answer reaches the pane as its decision");
            var contradictory = await Post(new { requestId = card.Id, runId = card.RunId, allow = false, always = true });
            Check(contradictory.Status == 400 && contradictory.Body.Text("error") == Locale.Get("remote.error.alwaysNeedsAllow"), "always without allow is refused");
            var claude = await Post(new { requestId = "perm-1", runId = pane, allow = true, always = true });
            Check(claude.Status == 409 && claude.Body.Text("error") == Locale.Get("remote.error.notAntigravityPermission"), "always on any other request is refused");
            var wrongRun = await Post(new { requestId = card.Id, runId = "run-2", allow = true, always = true });
            Check(wrongRun.Status == 409 && wrongRun.Body.Text("error") == Locale.Get("remote.error.notAntigravityPermission"), "a card of another run is not this card");
            var malformed = await Post(new { requestId = "../x", runId = card.RunId, allow = true });
            Check(malformed.Status == 400 && malformed.Body.Text("error") == Locale.Get("remote.error.badPermissionId"), "a malformed id is refused");
            Check((await Post(new { requestId = "stale", runId = pane, allow = true })).Status == 409, "a stale Claude answer fails closed as before");
            failure = Locale.Get("antigravity.permission.noRule");
            var refused = await Post(new { requestId = card.Id, runId = card.RunId, allow = true });
            Check(refused.Status == 409 && refused.Body.Text("error") == failure, "what the pane says back is the phone's reason");
            Check(actions.Count == 4, "no refused answer reached the pane");
        }
    }

    /// <summary>
    /// A remote pane's card over loopback (M/Tests/MightyCoreTests/AntigravityPermissionTests.swift aRemotePanesHostPutsTheRulesInItsOwnSettingsForTheContinuation):
    /// the host reports it, refuses a rule it never reported (403) and changes nothing, then holds the card's own rule while the
    /// continuation runs and takes it out after.
    /// </summary>
    internal static async Task ARemotePanesHostPutsTheRulesInItsOwnSettingsAndRefusesForgedOnes()
    {
        using var folder = new Folder(); folder.Make(Real);
        await using var catalog = FakeAgyCatalog(folder);
        var store = new StateStore(Path.Combine(folder.Root, "host")); await store.LoadAsync();
        Directory.CreateDirectory(Path.Combine(folder.Root, "project"));
        var shared = store.ApproveLocal(Path.Combine(folder.Root, "project"));
        await store.SaveAsync(new AppSnapshot { Workspaces = [shared] });
        var runtime = RemoteWireFixtureVerification.Node("info.json")["runtime"].Deserialize<RuntimeInfo>(Wire.Json)!;
        runtime = runtime with { Providers = runtime.Providers.Select(p => p.Id == "gemini" ? p with { Available = true, Detail = "" } : p).ToList() };
        var rules = new AntigravityRuleLeases(folder.SettingsPath, Path.Combine(folder.Root, "host", "antigravity-temporary-rules.json"));
        await using var host = new RemoteHostService(new RemoteHostOptions
        {
            Workspaces = () => store.Snapshot.Workspaces, ResolveLocal = store.ResolveLocalAsync, Runtime = () => Task.FromResult(runtime),
            CreateRunner = sinks => new RunManager(sinks.Workspace, catalog, "", sinks.Event, sinks.Permission), HostName = "studio-pc", AllowLoopbackForTests = true, AntigravityRules = rules,
        });
        var state = await host.StartAsync([shared.Id], 0);
        // A Windows host runs another computer's Antigravity only with the switch on.
        host.SetAllowsFullAccess(true);
        var (clientStore, clientFolder) = await RemoteClientVerification.ClientStore(); var seen = new RemoteClientVerification.Collected();
        var client = new RemoteClientService(clientStore, clientFolder, new MemoryRemoteKeyVault(), seen.Event, seen.Permission, new() { AllowLoopbackForTests = true });
        try
        {
            var info = await client.ConnectAsync("", "127.0.0.1:" + state.Port, state.Key!);
            var workspace = client.OpenWorkspace(info.Id, shared.Id);
            await client.StartAsync(new StartRunRequest("pane", workspace.Id, "claude", "say hi", [], Provider: "gemini"), workspace);
            await Verification.Until(() => seen.Events.Count(Ended) == 1, 20000);
            var card = seen.Events.FirstOrDefault(e => e.Type == "antigravityPermission")?.AntigravityPermission ?? throw new InvalidOperationException("no card: " + string.Join(",", seen.Events.Select(e => e.Type)));
            Check(card.Rules.SequenceEqual(["command(echo hi)"]), "the host reported the card");
            // A rule the host never reported is refused, and nothing runs.
            var forged = new StartRunRequest("pane", workspace.Id, "claude", "go on", [], Provider: "gemini", ResumeId: Conversation) { AntigravityGrant = new(card.Id, ["command(rm -rf /)"], true) };
            await RemoteClientVerification.RejectAsync(() => client.StartAsync(forged, workspace), "a forged rule", ex => ex is RemoteHttpFailure { Status: 403 } && ex.Message == Locale.Get("remote.host.error.antigravityRules"));
            var unknown = forged with { AntigravityGrant = new("agy-unknown", card.Rules, true) };
            await RemoteClientVerification.RejectAsync(() => client.StartAsync(unknown, workspace), "a card the host never reported", ex => ex is RemoteHttpFailure { Status: 403 });
            Check(folder.Text() == Real && seen.Events.Count(Ended) == 1, "nothing changed and nothing ran");
            // The card's own rule: in place while the continuation runs, gone after.
            var once = AntigravityPermissionFlow.Continuation(card, AntigravityPermissionDecision.Once)!;
            await client.StartAsync(new StartRunRequest("pane", workspace.Id, "claude", once.Prompt, [], Provider: "gemini", ResumeId: Conversation) { AntigravityGrant = once.Grant }, workspace);
            await Verification.Until(() => seen.Events.Count(Ended) == 2, 20000);
            Check(seen.Events.Any(e => e.Entry is { Kind: "assistant", Text: "allowed" }), "the host's agy ran with the rule in place");
            await Verification.Until(() => folder.Text() == Real && rules.Held().Count == 0, 10000);
        }
        finally { await client.DisposeAsync(); try { Directory.Delete(clientFolder, true); } catch (IOException) { } }
    }

    private static async Task<(DesktopService Service, string Pane, string Workspace)> Desktop(Folder folder, ProviderCatalog catalog)
    {
        var project = Path.Combine(folder.Root, "project"); Directory.CreateDirectory(project);
        var service = new DesktopService(Path.Combine(folder.Root, "state"), null, folder.Root, catalog, delegationEnabled: () => false, antigravitySettings: folder.SettingsPath);
        await service.InitializeAsync();
        var workspace = await service.AddWorkspaceAsync(project);
        var pane = new RunSession { Id = "agy-pane", WorkspaceId = workspace.Id, Kind = "claude", Provider = "gemini", Title = "Antigravity" };
        await service.UpdateAsync(s => s with { Sessions = [pane] });
        return (service, pane.Id, workspace.Id);
    }
    private static int Runs(DesktopService service, string pane) => service.Snapshot.Sessions.Single(s => s.Id == pane).Logs.Count(l => l.Kind == "user");
    /// <summary>A first turn that is refused "echo hi": its card waits past its run's end.</summary>
    private static async Task<AntigravityPermissionRequest> FirstTurn(DesktopService service, string pane, string workspace, int runs = 1)
    {
        // A new conversation: the fake agy refuses only a turn it was not asked to continue.
        await service.StartAsync(new StartRunRequest(pane, workspace, "claude", "say hi", [], Provider: "gemini"));
        await Verification.Until(() => Runs(service, pane) == runs && service.Snapshot.Sessions.Single(s => s.Id == pane).Status == "completed" && service.AntigravityCard(pane) is not null, 20000);
        var card = service.AntigravityCard(pane)!;
        Check(card.Rules.SequenceEqual(["command(echo hi)"]), "the card names the refused command");
        Check(service.Snapshot.Sessions.Single(s => s.Id == pane).Logs.Any(l => l.Kind == "system" && l.Text == AntigravityPermissionFlow.TranscriptLine(card)), "the transcript says a card waits");
        return card;
    }
}
