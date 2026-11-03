using System.Text.Json;
using System.Text.RegularExpressions;
using MightyClaude.Core;

// native/contracts/fixtures/work-map.json: the rules of docs/work-map.md that MightyCore's WorkMap.swift holds too — lanes, release
// tags, the version being worked on, release state, dot sizes and the reading of a topic summary — and, beside them, a real git
// repository read through DelegationGit, the summary run with a scripted Claude, the pane that is never saved and the phone's routes.
internal static class WorkMapVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private static readonly Lazy<JsonElement> Fixture = new(() =>
    {
        const string name = "MightyClaude.Core.Tests.WorkMap.json";
        using var stream = typeof(WorkMapVerification).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException(name + " is not embedded in MightyClaude.Core.Tests");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    });
    private static IEnumerable<JsonElement> Cases(string key) => Fixture.Value.GetProperty(key).EnumerateArray();
    private static string[] Strings(JsonElement list) => list.EnumerateArray().Select(v => v.GetString()!).ToArray();
    private static string? Text(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static Task TheFixtureLimitsAreTheCodesLimits()
    {
        var f = Fixture.Value;
        Check(f.GetProperty("maximumVersions").GetInt32() == WorkMap.MaximumVersions && f.GetProperty("maximumCommitsPerVersion").GetInt32() == WorkMap.MaximumCommitsPerVersion
            && f.GetProperty("maximumFilesPerCommit").GetInt32() == WorkMap.MaximumFilesPerCommit && f.GetProperty("maximumRequests").GetInt32() == WorkMap.MaximumRequests
            && f.GetProperty("maximumCommitsPerRequest").GetInt32() == WorkMap.MaximumCommitsPerRequest && f.GetProperty("requestTextLength").GetInt32() == WorkMap.RequestTextLength, "the version, commit, file and request limits");
        Check(f.GetProperty("maximumTopics").GetInt32() == WorkMapSummary.MaximumTopics && f.GetProperty("titleLength").GetInt32() == WorkMapSummary.TitleLength && f.GetProperty("summaryLength").GetInt32() == WorkMapSummary.SummaryLength
            && f.GetProperty("notesLength").GetInt32() == WorkMapSummary.NotesLength && f.GetProperty("summaryTimeoutSeconds").GetInt32() == (int)WorkMapSummary.Timeout.TotalSeconds, "the summary limits");
        Check(Strings(f.GetProperty("lanes")).SequenceEqual(WorkMap.Lanes) && Strings(f.GetProperty("releaseSteps")).SequenceEqual(WorkMap.ReleaseSteps), "the lanes and the release steps");
        Check(f.GetProperty("prompt").GetString() == WorkMapSummary.PromptTemplate, "the prompt template is the fixture's");
        return Task.CompletedTask;
    }

    internal static Task LanesAreReadFromTheScope()
    {
        foreach (var c in Cases("laneCases"))
        {
            var name = c.GetProperty("subject").GetString()!;
            var (lanes, scope, text) = WorkMap.ParseSubject(name);
            Check(lanes.SequenceEqual(Strings(c.GetProperty("lanes"))), $"lanes of \"{name}\": {string.Join(",", lanes)}");
            Check(scope == Text(c, "scope"), $"scope of \"{name}\": {scope ?? "null"}");
            Check(text == c.GetProperty("text").GetString(), $"text of \"{name}\": {text}");
        }
        return Task.CompletedTask;
    }

    internal static Task OnlyReleaseTagsCountNewestFirst()
    {
        foreach (var c in Cases("tagCases"))
            Check(WorkMap.Tags(Strings(c.GetProperty("tags"))).SequenceEqual(Strings(c.GetProperty("kept"))), "tags kept and ordered by version number");
        return Task.CompletedTask;
    }

    internal static Task TheVersionBeingWorkedOnComesFromTheVersionFile()
    {
        foreach (var c in Cases("versionNameCases"))
            Check(WorkMap.VersionName(Text(c, "file"), Text(c, "newestTag")) == Text(c, "version"), $"VERSION {Text(c, "file")?.Trim() ?? "absent"} after {Text(c, "newestTag") ?? "no tag"}");
        return Task.CompletedTask;
    }

    internal static Task ReleaseStateNamesTheNextStep()
    {
        foreach (var c in Cases("releaseStatusCases"))
        {
            var state = c.GetProperty("state");
            var (status, step) = WorkMap.ReleaseStatus(state.ValueKind == JsonValueKind.Null ? null : state.ValueKind == JsonValueKind.String ? state.GetString() : state.GetRawText());
            Check(status == c.GetProperty("status").GetString() && step == Text(c, "step"), $"release state {state}: {status} {step}");
        }
        return Task.CompletedTask;
    }

    internal static Task DotsGrowWithTheSquareRootOfTheCount()
    {
        var dots = Fixture.Value.GetProperty("dotSize");
        Check(dots.GetProperty("minimum").GetInt32() == 6 && dots.GetProperty("range").GetInt32() == 14, "the dot's minimum and range");
        foreach (var c in dots.GetProperty("cases").EnumerateArray())
            Check(WorkMap.DotSize(c.GetProperty("count").GetInt32(), c.GetProperty("largest").GetInt32()) == c.GetProperty("size").GetInt32(), $"dot of {c.GetProperty("count")} of {c.GetProperty("largest")}");
        return Task.CompletedTask;
    }

    private static string Other => Locale.Get("workMap.topic.other");

    internal static Task AnswersAreReadByTheFixtureRules()
    {
        foreach (var c in Cases("summaryCases"))
        {
            var name = c.GetProperty("name").GetString();
            var commits = c.GetProperty("commits").EnumerateArray().Select(x => (Sha: x.GetProperty("sha").GetString()!, Lanes: (IReadOnlyList<string>)Strings(x.GetProperty("lanes")))).ToList();
            var ids = Strings(c.GetProperty("requests")).ToHashSet();
            IReadOnlyList<WorkMapTopic>? topics = null; var failed = false;
            try { topics = WorkMapSummary.ParseAnswer(c.GetProperty("answer").GetString()!, commits, ids, Other); }
            catch (WorkMapSummaryException ex) { failed = ex.Key == "workMap.error.output"; }
            if (c.TryGetProperty("error", out var error) && error.GetBoolean()) { Check(failed && topics is null, $"{name}: unreadable answers fail"); continue; }
            Check(topics is not null, $"{name}: read");
            var expected = c.GetProperty("topics").EnumerateArray().ToList();
            Check(topics!.Count == expected.Count, $"{name}: {topics.Count} topics");
            for (var i = 0; i < expected.Count; i++)
            {
                var title = expected[i].GetProperty("title").GetString() == "@other" ? Other : expected[i].GetProperty("title").GetString();
                Check(topics[i].Title == title && topics[i].Summary == expected[i].GetProperty("summary").GetString(), $"{name}: topic {i} title and summary");
                Check(topics[i].Lanes.SequenceEqual(Strings(expected[i].GetProperty("lanes"))) && topics[i].Commits.SequenceEqual(Strings(expected[i].GetProperty("commits"))) && topics[i].Requests.SequenceEqual(Strings(expected[i].GetProperty("requests"))), $"{name}: topic {i} lanes, commits and requests");
            }
        }
        return Task.CompletedTask;
    }

    internal static Task ThePromptFillsEveryPlaceholderOnce()
    {
        var commits = new[] { new WorkMapCommit("aaaaaaa1", "2026-01-01T00:00:00+00:00", ["mac"], "mac", "uses {version} literally", [], 0), new WorkMapCommit("bbbbbbb2", "2026-01-01T00:00:00+00:00", ["etc"], null, "plain", [], 0) };
        var prompt = WorkMapSummary.Prompt("Korean", "0.4.49", commits, [new("p1/r1", "p1", "make it", null, [], [])], "NOTES");
        Check(prompt.Contains("Answer in Korean.") && prompt.Contains("Version: 0.4.49\n") && prompt.Contains("aaaaaaa1 [mac] mac: uses {version} literally\nbbbbbbb2 [etc] plain") && prompt.Contains("p1/r1: make it") && prompt.EndsWith("Release notes:\nNOTES"), "the prompt's parts");
        var empty = WorkMapSummary.Prompt("English", "next", [], [], null);
        Check(empty.Contains("Requests the user sent (id: text):\n(none)\nRelease notes:\n(none)"), "no requests and no notes read (none)");
        Check(WorkMapSummary.Prompt("English", "1", [], [], new string('x', 9000)).Length - WorkMapSummary.Prompt("English", "1", [], [], "").Length == WorkMapSummary.NotesLength - "(none)".Length, "notes are cut to 8,000");
        return Task.CompletedTask;
    }

    // ---- a real repository ----

    private static async Task Git(string folder, params string[] arguments)
    {
        var run = await DelegationGit.RunDetailed(["-c", "user.name=Check", "-c", "user.email=check@example.com", "-c", "commit.gpgsign=false", "-c", "tag.gpgsign=false", .. arguments], folder, 30);
        Check(run.Result is { ExitCode: 0 }, "git " + string.Join(" ", arguments) + ": " + (run.Result?.Stderr ?? run.Failure.ToString()));
    }
    private static async Task Commit(string folder, string file, string subject, string date)
    {
        File.WriteAllText(Path.Combine(folder, file), subject + Guid.NewGuid());
        await Git(folder, "add", "--", file);
        await Git(folder, "commit", "-m", subject, "--date=" + date);
    }

    /// <summary>v0.1.0 (one commit), v0.1.1 (two commits and a side branch merged in), a tag that is no release, then one commit past it with a VERSION file and a release in flight.</summary>
    private static async Task<string> MakeRepository(string parent)
    {
        var folder = Directory.CreateDirectory(Path.Combine(parent, "repo")).FullName;
        await Git(folder, "init", "-q");
        await Commit(folder, "a.txt", "(mac) one", "2026-01-01T00:00:00+00:00");
        await Git(folder, "tag", "v0.1.0");
        await Commit(folder, "b.txt", "(windows) two", "2026-01-02T00:00:00+00:00");
        await Commit(folder, "c.txt", "fix(phone): three", "2026-01-03T00:00:00+00:00");
        await Git(folder, "switch", "-q", "-c", "side");
        await Commit(folder, "d.txt", "(ci) side", "2026-01-04T00:00:00+00:00");
        await Git(folder, "switch", "-q", "-");
        await Git(folder, "merge", "--no-ff", "-q", "-m", "Merge branch 'side'", "side");
        await Git(folder, "tag", "v0.1.1");
        await Git(folder, "tag", "mighty-style-engine-v3");
        await Commit(folder, "a.txt", "(mac, windows) four", "2026-02-01T00:00:00+00:00");
        File.WriteAllText(Path.Combine(folder, "VERSION"), "0.1.2\n");
        Directory.CreateDirectory(Path.Combine(folder, "release", "0.1.2"));
        File.WriteAllText(Path.Combine(folder, "release", "0.1.2", "state.json"), """{"done":{"check":true,"version":true,"push":true}}""");
        return folder;
    }

    private static void Remove(string folder) { try { Directory.Delete(folder, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }

    internal static async Task AGitRepositoryIsReadByTagsMergesAndReleaseState()
    {
        if (DelegationGit.Executable is null) return;
        var parent = Verification.Temp();
        try
        {
            var folder = await MakeRepository(parent);
            var empty = new Dictionary<string, WorkMapStoredSummary>(); var none = new Dictionary<string, string>();
            var model = await WorkMap.BuildAsync(folder, [], empty, null, none);
            Check(model.Available && model.Reason is null && model.Head is { Length: 8 }, "a repository is available, with the HEAD's first 8");
            Check(model.Versions.Select(v => v.Key).SequenceEqual(["next", "v0.1.1", "v0.1.0"]), "newest first: the unreleased version, then the tags (the other tag is no version): " + string.Join(",", model.Versions.Select(v => v.Key)));
            var next = model.Versions[0]; var middle = model.Versions[1]; var first = model.Versions[2];
            Check(next.Version == "0.1.2" && next.Tag is null && next.Status == "releasing" && next.ReleaseStep == "ci" && next.Total == 1 && next.Date == "2026-02-01", $"the unreleased version: {next.Version} {next.Status} {next.ReleaseStep} {next.Date}");
            Check(next.Commits.Single().Lanes.SequenceEqual(["mac", "windows"]) && next.Commits.Single().Files.SequenceEqual(["a.txt"]) && next.Counts["mac"] == 1 && next.Counts["windows"] == 1, "its commit's lanes and files");
            Check(next.RangeEnd.Length == 40 && next.RangeEnd.StartsWith(model.Head!, StringComparison.Ordinal), "the unreleased range ends at HEAD");
            Check(middle.Status == "released" && middle.Version == "0.1.1" && middle.Tag == "v0.1.1" && middle.Total == 3 && middle.Commits.Count == 3 && !middle.Truncated, "v0.1.1 holds the two commits and the side commit, not the merge");
            Check(!model.Versions.SelectMany(v => v.Commits).Any(c => c.Subject.StartsWith("Merge", StringComparison.Ordinal)), "merges are left out");
            Check(middle.Commits.First(c => c.Subject == "three").Scope == "phone" && middle.Counts["ci"] == 1 && middle.Counts["phone"] == 1 && middle.Counts["windows"] == 1 && !middle.Counts.ContainsKey("mac"), "its commits are read by scope, a conventional title too");
            Check(first.Total == 1 && first.Commits.Single().Lanes.SequenceEqual(["mac"]) && first.Date!.Length == 10, "the oldest tag starts from the beginning");
            Check(middle.Commits.All(c => c.Sha.Length == 8) && middle.RangeEnd.Length == 40, "commits carry 8 characters, a range its end's 40");
            Check(model.Versions.All(v => v.Topics is null && !v.TopicsStale && v.SummaryError is null), "no summary yet");

            // A stored summary is stale once the range ends elsewhere.
            var stored = new Dictionary<string, WorkMapStoredSummary> { ["v0.1.1"] = new(middle.RangeEnd, [new("T", "S", ["phone"], [middle.Commits[0].Sha], [])], "now"), ["v0.1.0"] = new("0000", [new("Old", "", ["mac"], [first.Commits[0].Sha], [])], "then") };
            var summed = await WorkMap.BuildAsync(folder, [], stored, "next", new Dictionary<string, string> { ["v0.1.0"] = "oops" });
            Check(summed.Summarizing == "next" && summed.Versions[1].Topics is { Count: 1 } && !summed.Versions[1].TopicsStale && summed.Versions[2].TopicsStale && summed.Versions[2].Topics is { Count: 1 } && summed.Versions[2].SummaryError == "oops", "a summary shows, and is stale when its range ended elsewhere");

            // Requests go to the version of the oldest commit that followed them.
            WorkMapRequestInput Ask(string run, string? date, params string[] files) => new("p1", run, "text " + run, date, files);
            var asked = await WorkMap.BuildAsync(folder, [Ask("r1", "2000-01-01T00:00:00+00:00", "a.txt"), Ask("r2", "2026-01-01T00:00:01+00:00", "a.txt"), Ask("r3", "2999-01-01T00:00:00+00:00"), Ask("r4", "2000-01-01T00:00:00+00:00"), Ask("r5", null, "b.txt"), Ask("r6", "2026-01-01T12:00:00+00:00", "nothing.txt")], empty, null, none);
            Check(asked.Versions[2].Requests.Select(r => r.Id).SequenceEqual(["p1/r1"]) && asked.Versions[2].Requests[0].Commits.Count == 2, "a request whose file the oldest commit touched sits in that commit's version");
            Check(asked.Versions[1].Requests.Select(r => r.Id).SequenceEqual(["p1/r5"]) && asked.Versions[1].Requests[0].Date is null && asked.Versions[1].Requests[0].Commits.Count == 1, "no date: every commit that touched its files follows");
            Check(asked.Versions[0].Requests.Select(r => r.Id).OrderBy(x => x).SequenceEqual(["p1/r2", "p1/r3"]), "only commits after the request count; one with no commit sits in the unreleased version when it is newer than the newest tag: " + string.Join(",", asked.Versions[0].Requests.Select(r => r.Id)));
            Check(asked.Versions.SelectMany(v => v.Requests).All(r => r.Id != "p1/r4" && r.Id != "p1/r6"), "an old request with no commit is dropped");

            // Not a repository.
            var plain = Directory.CreateDirectory(Path.Combine(parent, "plain")).FullName;
            var gone = await WorkMap.BuildAsync(plain, [], empty, null, none);
            Check(!gone.Available && gone.Reason == "notGit" && gone.Versions.Count == 0, "a folder that is no repository says notGit");
        }
        finally { Remove(parent); }
    }

    internal static async Task ARepositoryWithoutTagsIsOneUnreleasedVersion()
    {
        if (DelegationGit.Executable is null) return;
        var parent = Verification.Temp();
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(parent, "repo")).FullName;
            await Git(folder, "init", "-q");
            var fresh = await WorkMap.BuildAsync(folder, [], new Dictionary<string, WorkMapStoredSummary>(), null, new Dictionary<string, string>());
            Check(fresh.Available && fresh.Head is null && fresh.Versions.Count == 0, "no commit yet: nothing to show, nothing failed");
            await Commit(folder, "a.txt", "(docs) first", "2026-01-01T00:00:00+00:00");
            var model = await WorkMap.BuildAsync(folder, [], new Dictionary<string, WorkMapStoredSummary>(), null, new Dictionary<string, string>());
            Check(model.Versions.Count == 1 && model.Versions[0].Key == "next" && model.Versions[0].Version is null && model.Versions[0].Status == "unreleased" && model.Versions[0].Commits[0].Lanes.SequenceEqual(["etc"]), "with no tag the whole history is the unreleased version");
        }
        finally { Remove(parent); }
    }

    // ---- the summary run and its saving ----

    internal static async Task ClaudeIsAskedOnceInAFolderOfItsOwn()
    {
        var ok = await WorkMapSummary.RunAsync(Verification.Self("--workmap-fake", "ok"), "hello prompt", TimeSpan.FromSeconds(60));
        var parts = ok.Split('|');
        Check(parts[0] == "got:hello prompt", "the prompt arrives as one stream-json user message and the result event's text comes back: " + ok);
        Check(parts[1].Contains("mighty-work-map-", StringComparison.Ordinal) && !Directory.Exists(parts[1]), "it runs in a temporary folder that is gone after");
        Check(parts[2] == "tools-off", "it is launched with the model probe's headless arguments");
        try { await WorkMapSummary.RunAsync(Verification.Self("--workmap-fake", "hold"), "x", TimeSpan.FromSeconds(1)); throw new InvalidOperationException("no timeout"); }
        catch (WorkMapSummaryException ex) { Check(ex.Key == "workMap.error.timeout", "a run past its time says timeout"); }
        try { await WorkMapSummary.RunAsync(Verification.Self("--workmap-fake", "silent"), "x", TimeSpan.FromSeconds(30)); throw new InvalidOperationException("no failure"); }
        catch (WorkMapSummaryException ex) { Check(ex.Key == "workMap.error.output", "no result event reads as unreadable"); }
        try { await WorkMapSummary.RunAsync(new CliCommand(Path.Combine(Verification.Temp(), "no-such-claude.exe"), [], "1"), "x", TimeSpan.FromSeconds(30)); throw new InvalidOperationException("no failure"); }
        catch (WorkMapSummaryException ex) { Check(ex.Key == "workMap.error.cli", "a program that cannot start says cli"); }
    }

    internal static async Task TheServiceSummarizesOneVersionAtATimeAndSavesIt()
    {
        if (DelegationGit.Executable is null) return;
        var parent = Verification.Temp();
        try
        {
            var folder = await MakeRepository(parent);
            var workspace = new Workspace { Id = "ws1", Name = "w", Path = folder };
            var snapshot = new AppSnapshot { Workspaces = [workspace] };
            var data = Path.Combine(parent, "data");
            var gate = new TaskCompletionSource(); var runs = 0; var lastPrompt = "";
            CliCommand? found = new CliCommand("claude", [], "1"); Task<CliCommand?> Find() => Task.FromResult<CliCommand?>(found);
            var service = new WorkMapService(data, () => snapshot, new FileOriginalStore(Path.Combine(parent, "originals")), _ => Find(), async (_, prompt, _, _) =>
            {
                runs++; lastPrompt = prompt; await gate.Task;
                var shas = Regex.Matches(prompt, @"^([0-9a-f]{8}) \[", RegexOptions.Multiline).Select(m => m.Groups[1].Value);
                return "```json\n" + JsonSerializer.Serialize(new { topics = new[] { new { title = "Everything", summary = "All of it.", commits = shas.ToArray() } } }) + "\n```";
            });
            var changed = new SemaphoreSlim(0); service.Changed += _ => changed.Release();

            Check(await service.StartAsync(workspace, "v9.9.9") == WorkMapStart.UnknownKey && (await service.ModelAsync(workspace)).Summarizing is null, "an unknown key is refused and leaves nothing busy");
            Check(await service.StartAsync(workspace, "v0.1.1") == WorkMapStart.Started, "a known version starts");
            Check(await service.StartAsync(workspace, "next") == WorkMapStart.Busy && (await service.ModelAsync(workspace)).Summarizing == "v0.1.1", "a second one waits: one at a time per workspace");
            gate.SetResult();
            Check(await changed.WaitAsync(TimeSpan.FromSeconds(30)), "the end is announced");
            var model = await service.ModelAsync(workspace);
            var done = model.Versions.First(v => v.Key == "v0.1.1");
            Check(model.Summarizing is null && done.Topics is { Count: 1 } && done.Topics[0].Commits.Count == 3 && !done.TopicsStale && done.SummaryError is null && runs == 1, "the topics are read, kept and fresh");
            Check(lastPrompt.Contains("Version: 0.1.1") && lastPrompt.Contains("[phone] phone: three"), "the prompt carries the version and its commits: " + lastPrompt);
            var saved = Directory.GetFiles(Path.Combine(data, "work-map"));
            Check(saved.Length == 1 && Regex.IsMatch(Path.GetFileName(saved[0]), "^[0-9a-f]{64}\\.json$"), "saved as <sha256 of the root>.json, with no temporary file left");
            var later = new WorkMapService(data, () => snapshot, new FileOriginalStore(Path.Combine(parent, "originals")), _ => Find());
            Check((await later.ModelAsync(workspace)).Versions.First(v => v.Key == "v0.1.1").Topics is { Count: 1 }, "a new run of the app reads the saved summary");

            // The CLI is not there.
            found = null; gate = new TaskCompletionSource(); gate.SetResult();
            Check(await service.StartAsync(workspace, "v0.1.0") == WorkMapStart.Started && await changed.WaitAsync(TimeSpan.FromSeconds(30)), "a summary without the CLI ends");
            Check((await service.ModelAsync(workspace)).Versions.First(v => v.Key == "v0.1.0").SummaryError == Locale.Get("workMap.error.cli"), "and says the CLI was not found");
            var remote = new Workspace { Id = "ws2", Name = "r", Path = folder, Remote = new RemoteWorkspaceReference("c", "w", "host") };
            var denied = await service.ModelAsync(remote);
            Check(!denied.Available && denied.Reason == "remote" && await service.StartAsync(remote, "next") == WorkMapStart.Unavailable, "a remote workspace has no work map");
        }
        finally { Remove(parent); }
    }

    /// <summary>The scripted Claude of the summary checks (<c>--workmap-fake ok|hold|silent</c>): reads the one user message and answers with a result event.</summary>
    internal static async Task FakeClaudeAsync(string mode, string[] args)
    {
        var line = await Console.In.ReadLineAsync() ?? "";
        if (mode == "hold") { await Task.Delay(60000); return; }
        if (mode == "silent") { Console.WriteLine("{\"type\":\"system\",\"subtype\":\"init\"}"); return; }
        using var message = JsonDocument.Parse(line);
        var content = message.RootElement.GetProperty("message").GetProperty("content").GetString();
        var tools = Array.IndexOf(args, "--tools") is var at and >= 0 && at + 1 < args.Length && args[at + 1] == "" ? "tools-off" : "tools-on";
        Console.WriteLine("{\"type\":\"system\",\"subtype\":\"init\"}");
        Console.WriteLine(JsonSerializer.Serialize(new { type = "result", subtype = "success", is_error = false, result = "got:" + content + "|" + Environment.CurrentDirectory + "|" + tools }));
    }

    internal static Task RequestsComeFromTheOpenPanesRunsAndWhatTheyEdited()
    {
        var parent = Verification.Temp();
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(parent, "work")).FullName;
            File.WriteAllText(Path.Combine(folder, "a.txt"), "now");
            var store = new FileOriginalStore(Path.Combine(parent, "originals"));
            Check(store.Capture("pane", "run-1", folder, Path.Combine(folder, "a.txt"), "before"), "the run's original is kept");
            var entry = new LogEntry("e1", "user", "x", "2026-11-02T20:58:00+09:00");
            RunSession Pane(string id, string workspace, params MightyGraphRun[] runs) => new() { Id = id, WorkspaceId = workspace, GraphRuns = [.. runs] };
            var sessions = new[]
            {
                Pane("pane", "ws1",
                    new MightyGraphRun { Id = "entry-1", SourceRunID = "run-1", Input = new string('q', 300), RootEntries = [entry] },
                    new MightyGraphRun { Id = "notice", Input = "a notice", NoticeRun = true },
                    new MightyGraphRun { Id = "blank", Input = "   " },
                    new MightyGraphRun { Id = "plain", Input = "no edits" }),
                Pane("other", "ws2", new MightyGraphRun { Id = "x", Input = "another workspace" }),
            };
            var inputs = WorkMap.Requests(sessions, "ws1", folder, store);
            Check(inputs.Select(i => i.RunId).SequenceEqual(["entry-1", "plain"]), "a notice run, an empty input and another workspace's pane are left out: " + string.Join(",", inputs.Select(i => i.RunId)));
            Check(inputs[0].PaneId == "pane" && inputs[0].Text.Length == 200 && inputs[0].Date == "2026-11-02T20:58:00+09:00" && inputs[0].Files.SequenceEqual(["a.txt"]), "the text is cut to 200, the date is the first root record's and the files are the run's (read under its source run)");
            Check(inputs[1].Date is null && inputs[1].Files.Count == 0, "a run with no record has no date and no files");
            Check(WorkMap.Requests(sessions, "ws1", Path.Combine(parent, "elsewhere"), store)[0].Files.Count == 0, "originals kept under another folder name no files");
        }
        finally { Remove(parent); }
        return Task.CompletedTask;
    }

    // ---- the pane ----

    internal static Task TheWorkMapPaneIsNeverSavedAndNeverCounted()
    {
        Check(WorkMapPaneKind.Kind == "workmap" && WorkMapPaneKind.PaneId("ws1") == "workmap:ws1" && Wire.Identifier(WorkMapPaneKind.PaneId(Wire.Id())), "one pane per workspace, a valid identifier");
        var paneId = WorkMapPaneKind.PaneId("ws1");
        var workspace = new Workspace { Id = "ws1", Name = "w", Path = Verification.Temp() };
        var agent = new RunSession { Id = "agent1", WorkspaceId = "ws1", Title = "Claude" };
        var map = new RunSession { Id = paneId, WorkspaceId = "ws1", Title = Locale.Get("workMap.title"), Kind = WorkMapPaneKind.Kind, Draft = "never kept", Status = "running" };
        var stray = new RunSession { Id = "workmap-other", WorkspaceId = "ws1", Kind = WorkMapPaneKind.Kind };
        var snapshot = new AppSnapshot { Workspaces = [workspace], Sessions = [agent, map, stray], ActiveWorkspaceId = "ws1", ActiveSessionId = paneId };
        var live = StateStore.Normalize(snapshot, false);
        Check(live.Sessions.Select(s => s.Id).SequenceEqual(["agent1", paneId]) && live.ActiveSessionId == paneId, "the running app keeps exactly one work map per workspace");
        var kept = live.Sessions.First(s => s.Id == paneId);
        Check(kept.Kind == "workmap" && kept.Draft == "" && kept.Status == "idle", "it keeps nothing but its title");
        Check(StateStore.Normalize(snapshot, true).Sessions.Select(s => s.Id).SequenceEqual(["agent1"]), "a restart never brings it back");
        var stored = FilePaneKind.Stored(live with { PaneLayoutActiveSessionIds = new() { ["ws1"] = paneId } });
        Check(!JsonSerializer.Serialize(stored, Wire.Json).Contains(paneId, StringComparison.Ordinal) && stored.ActiveSessionId is null && stored.PaneLayoutActiveSessionIds is { Count: 0 }, "the written state names it nowhere");
        Check(!WorkDashboard.IsCounted("workmap"), "the status counts leave it out");
        Check(AccountUsageStatus.LocalProviders(new AppSnapshot { Workspaces = [workspace], Sessions = [map] }).Count == 0, "the usage chips do not take it for an agent pane");
        return Task.CompletedTask;
    }

    // ---- the phone ----

    internal static async Task ThePhoneReadsAndStartsTheWorkMapThroughTheRouter()
    {
        if (DelegationGit.Executable is null) return;
        var parent = Verification.Temp();
        try
        {
            var folder = await MakeRepository(parent);
            await using var service = new DesktopService(Path.Combine(parent, "state"), null, parent); await service.InitializeAsync();
            var workspace = await service.AddWorkspaceAsync(folder);
            var started = new List<string>(); var next = WorkMapStart.Started;
            var maps = new WorkMapService(Path.Combine(parent, "data"), () => service.Snapshot, service.FileOriginals, _ => Task.FromResult<CliCommand?>(null));
            using var router = new MobileDesktopRouter(service, Path.Combine(parent, "mobile"), "Fixture", (_, _) => Task.FromResult<object?>(null), _ => new([]))
            {
                WorkMapRead = maps.ModelAsync, WorkMapSummarize = (_, key) => { started.Add(key); return Task.FromResult(next); },
            };
            async Task<(int Status, JsonElement Body)> Route(string method, string path, string? body = null)
            {
                var reply = await router.RouteAsync(method, path, body is null ? null : JsonDocument.Parse(body).RootElement.Clone(), "phone", CancellationToken.None);
                return (reply.Status, JsonDocument.Parse(JsonSerializer.Serialize(reply.Body, Wire.Json)).RootElement.Clone());
            }
            Check(MobileDesktopRouter.Capabilities.Contains("workMap") && (await Route("GET", "/m1/info")).Body.GetProperty("capabilities").EnumerateArray().Any(c => c.GetString() == "workMap"), "the host lists workMap");
            var read = await Route("GET", $"/m1/workspaces/{workspace.Id}/workmap");
            Check(read.Status == 200 && read.Body.GetProperty("available").GetBoolean() && read.Body.GetProperty("versions").GetArrayLength() == 3, "the model is read");
            var commit = read.Body.GetProperty("versions")[1].GetProperty("commits")[0];
            Check(!commit.TryGetProperty("files", out _) && commit.GetProperty("filesCount").GetInt32() >= 1 && commit.GetProperty("sha").GetString()!.Length == 8, "a commit carries filesCount, not its files");
            Check(read.Body.GetProperty("versions")[0].GetProperty("version").GetString() == "0.1.2" && read.Body.GetProperty("versions")[0].GetProperty("topics").ValueKind == JsonValueKind.Null, "a missing value is null, as the page reads it");
            Check((await Route("GET", "/m1/workspaces/nope/workmap")).Status == 404, "an unknown workspace is 404");
            var accepted = await Route("POST", $"/m1/workspaces/{workspace.Id}/workmap/summarize", """{"key":"v0.1.1"}""");
            Check(accepted.Status == 202 && started.SequenceEqual(["v0.1.1"]), "a summary is accepted with 202");
            next = WorkMapStart.Busy; Check((await Route("POST", $"/m1/workspaces/{workspace.Id}/workmap/summarize", """{"key":"next"}""")).Status == 409, "a busy workspace answers 409");
            next = WorkMapStart.UnknownKey; Check((await Route("POST", $"/m1/workspaces/{workspace.Id}/workmap/summarize", """{"key":"v9.9.9"}""")).Status == 400, "an unknown key answers 400");
            Check((await Route("POST", $"/m1/workspaces/{workspace.Id}/workmap/summarize", "{}")).Status == 400, "a missing key answers 400");
        }
        finally { Remove(parent); }
    }
}
