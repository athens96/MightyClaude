using System.Text.Json;
using System.Text.Json.Nodes;
using MightyClaude.Core;
using Microsoft.UI.Windowing;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;
using Windows.Storage.Streams;

namespace MightyClaude.WinUI;

/// <summary>
/// <c>--help-capture --profile &lt;empty dir&gt; [-language ko|en|zh|ja] [--smoke-exit]</c>: the help site's pictures of the web
/// shell, taken the way the Mac's capture takes its own (M/HelpCaptureDiagnostics.swift). It reads docs/help/shots.json and the
/// demo profile (native/contracts/fixtures/demo-profile.json, both embedded), seeds the profile's workspaces, panes and runs into
/// the empty profile, and for each shot sets the window's content size, drives the page to the shot's screen and saves the
/// WebView2's picture (CapturePreviewAsync, PNG; a popover shot is cut out of the page by its element's bounds) as
/// <c>&lt;profile&gt;\help-shots\&lt;lang&gt;\&lt;id&gt;-&lt;theme&gt;.png</c> for each theme, beside a manifest.json with every shot's
/// status. A screen this capture does not know yet is recorded as skipped and the run goes on.
///
/// The runs are the profile's canned CLI lines played by the app's own binary in the CLI's place (<see cref="HelpDemoCli"/>),
/// through the real run path, so the panes, cards and diagrams show what a real run would. No real CLI starts and nothing goes
/// on the network; the workspaces are folders under the profile.
/// </summary>
public sealed partial class MainWindow
{
    private sealed class HelpDemo(JsonObject profile)
    {
        internal JsonObject Profile { get; } = profile;
        internal Dictionary<string, string> Workspaces { get; } = [];
        internal Dictionary<string, string> Panes { get; } = [];
        internal string? TerminalProblem { get; set; }
        internal string Pane(string role) => Panes.TryGetValue(role, out var id) ? id : throw new InvalidOperationException("the demo has no " + role + " pane");
        internal string? DelegationWorkspace => (string?)Profile["delegation"]?["workspace"];
        internal IEnumerable<JsonObject> Layouts => (Profile["layouts"]?.AsArray() ?? []).OfType<JsonObject>().Where(l => (string?)l["workspace"] != DelegationWorkspace);
    }

    /// <summary>What one screen shows: the element to cut out (null: the whole page) and how to put things back.</summary>
    private sealed record HelpTarget(string? Clip = null, Func<Task>? Cleanup = null, Func<string, Task>? Save = null);

    private static readonly string[] HelpKnownScreens = ["welcome", "overview", "sidebar-collapsed", "dashboard", "agent-basic", "agent-mighty", "agent-mighty-overview", "agent-timeline", "agent-structure", "result-card", "composer", "composer-settings", "session-info", "plan-card", "plan-document", "question-card", "permission-card", "background-work", "terminal-pane", "files-pane", "companion-pet",
        "resume-choice", "resume-list", "settings-general", "settings-models", "settings-styles", "settings-tools", "settings-cli", "settings-mobile", "settings-companion", "settings-about"];

    private async Task RunHelpCapture()
    {
        var directory = options.ProfileDirectory ?? throw new InvalidOperationException("--help-capture needs a temporary --profile");
        var language = options.HelpLanguage ?? "ko";
        var output = Path.Combine(directory, "help-shots", language);
        var manifest = new Dictionary<string, object?> { ["language"] = language, ["appVersion"] = typeof(MainWindow).Assembly.GetName().Version?.ToString(3), ["frontEnd"] = "web" };
        var shots = new List<Dictionary<string, object?>>();
        try
        {
            Directory.CreateDirectory(output);
            var list = JsonNode.Parse(HelpResource("MightyClaude.WinUI.HelpShots.json"))?["shots"]?.AsArray() ?? throw new InvalidOperationException("shots.json lists no shots");
            var profile = HelpLocalized(JsonNode.Parse(HelpResource("MightyClaude.WinUI.DemoProfile.json")), language) as JsonObject ?? throw new InvalidOperationException("the demo profile is not an object");
            manifest["shotCount"] = list.Count;
            var shell = webShell ?? throw new InvalidOperationException("the help capture draws the web shell; the profile holds ui-winui");
            var core = await shell.Started ?? throw new InvalidOperationException("WebView2 did not start");
            if (!await HelpUntil(core, "!!document.querySelector('[data-testid=shell]')", 30)) throw new InvalidOperationException("the page did not mount");
            // Still pictures, as the Mac's CI capture draws them: the page's reduced-motion rules (a still bolt for the moving
            // bars, solid outlines for the marching ones, no turning spark).
            await core.CallDevToolsProtocolMethodAsync("Emulation.setEmulatedMedia", "{\"features\":[{\"name\":\"prefers-reduced-motion\",\"value\":\"reduce\"}]}");
            var ordered = list.OfType<JsonObject>().Where(s => (string?)s["screen"] == "welcome").Concat(list.OfType<JsonObject>().Where(s => (string?)s["screen"] != "welcome"));
            HelpDemo? demo = null; string? seedError = null;
            foreach (var shot in ordered)
            {
                var screen = (string?)shot["screen"] ?? "";
                var themes = shot["themes"]?.AsArray().Select(t => (string?)t ?? "").ToList() is { Count: > 0 } listed ? listed : ["light", "dark"];
                Dictionary<string, object?> Entry(string theme, string status) => new() { ["id"] = (string?)shot["id"], ["section"] = (string?)shot["section"], ["screen"] = screen, ["theme"] = theme, ["file"] = $"{(string?)shot["id"]}-{theme}.png", ["status"] = status };
                // MIGHTY_HELP_SHOTS=<id>,<id>… takes only those shots, for a quicker look while a screen is worked on.
                if (Environment.GetEnvironmentVariable("MIGHTY_HELP_SHOTS") is { Length: > 0 } only && !only.Split(',').Contains((string?)shot["id"]))
                {
                    shots.AddRange(themes.Select(theme => { var e = Entry(theme, "skipped"); e["note"] = "not in MIGHTY_HELP_SHOTS"; return e; }));
                    continue;
                }
                if (!HelpKnownScreens.Contains(screen))
                {
                    shots.AddRange(themes.Select(theme => { var e = Entry(theme, "skipped"); e["note"] = "the Windows capture does not draw this screen yet"; return e; }));
                    continue;
                }
                if (screen != "welcome" && demo is null && seedError is null)
                {
                    try { demo = await SeedHelpDemo(new HelpDemo(profile), directory, core); }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { seedError = ex.Message; manifest["error"] = "seeding the demo profile failed: " + ex.Message; }
                }
                if (screen != "welcome" && seedError is not null)
                {
                    shots.AddRange(themes.Select(theme => { var e = Entry(theme, "failed"); e["error"] = "seeding the demo profile failed: " + seedError; return e; }));
                    continue;
                }
                shots.AddRange(await CaptureHelpShot(shot, screen, themes, demo, core, output, Entry));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { manifest["error"] = ex.Message; }
        var failed = shots.Count(s => (string?)s["status"] == "failed");
        manifest["shots"] = shots; manifest["failed"] = failed; manifest["skipped"] = shots.Count(s => (string?)s["status"] == "skipped");
        var passed = !manifest.ContainsKey("error") && failed == 0 && shots.Any(s => (string?)s["status"] == "ok");
        manifest["passed"] = passed;
        try
        {
            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            await File.WriteAllTextAsync(Path.Combine(directory, "smoke-result.json"), JsonSerializer.Serialize(new { passed, helpCapture = true, aiRequestSent = false, clipboardUsed = false, failed, error = manifest.GetValueOrDefault("error") }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { PerfLog.Mark("help capture manifest: " + ex.Message); }
        await CloseTerminalsAsync();
        await FinishSmoke(passed);
    }

    private static string HelpText(JsonNode? node) => (string?)node ?? "";

    private static string HelpResource(string name)
    {
        using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException("missing resource " + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>The profile with every user-visible string (an object whose keys are exactly the four languages) read in <paramref name="language"/>.</summary>
    private static JsonNode? HelpLocalized(JsonNode? node, string language)
    {
        switch (node)
        {
            case JsonObject value when value.Count == Locale.Languages.Count && Locale.Languages.All(value.ContainsKey):
                return JsonValue.Create((string?)value[language] ?? "");
            case JsonObject value:
                var copy = new JsonObject();
                foreach (var (key, child) in value) copy[key] = HelpLocalized(child, language);
                return copy;
            case JsonArray items:
                return new JsonArray([.. items.Select(item => HelpLocalized(item, language))]);
            default:
                return node?.DeepClone();
        }
    }

    // MARK: Seeding

    /// <summary>
    /// The demo as the Mac's capture seeds it, the delegation workspace left out: the workspaces' folders and files, the panes with
    /// their titles, models and settings, their runs played through the real run path by the canned CLI, their drafts, the layouts,
    /// and the shell pane's terminal showing the canned command's output.
    /// </summary>
    private async Task<HelpDemo> SeedHelpDemo(HelpDemo demo, string directory, CoreWebView2 core)
    {
        var profile = demo.Profile;
        var later = demo.DelegationWorkspace;
        var root = Path.Combine(directory, "Projects");
        foreach (var spec in (profile["workspaces"]?.AsArray() ?? []).OfType<JsonObject>().Where(w => (string?)w["id"] != later))
        {
            var folder = Path.Combine(root, (string?)spec["directory"] ?? throw new InvalidOperationException("a demo workspace has no folder"));
            foreach (var file in (spec["files"]?.AsArray() ?? []).OfType<JsonObject>())
            {
                var path = Path.Combine(folder, ((string?)file["path"] ?? "").Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, (string?)file["content"] ?? "");
            }
            var workspace = await service.AddWorkspaceAsync(folder);
            demo.Workspaces[HelpText(spec["id"])] = workspace.Id;
        }
        var panes = (profile["panes"]?.AsArray() ?? []).OfType<JsonObject>().Where(p => (string?)p["workspace"] != later).ToList();
        var sessions = new List<RunSession>();
        foreach (var pane in panes)
        {
            var role = HelpText(pane["role"]);
            var session = new RunSession
            {
                WorkspaceId = demo.Workspaces[HelpText(pane["workspace"])], Kind = (string?)pane["kind"] ?? "claude", Provider = (string?)pane["provider"] ?? "claude",
                Model = (string?)pane["model"] ?? "default", Title = (string?)pane["title"] ?? role, TitleMode = "fixed",
                Settings = new((string?)pane["effort"] ?? "default", (string?)pane["permissionMode"] ?? "manual"),
                AgentViewMode = (string?)pane["view"] == "mighty" ? "mighty" : null, Draft = (string?)pane["draft"] ?? "",
            };
            demo.Panes[role] = session.Id; sessions.Add(session);
        }
        var firstWorkspace = demo.Workspaces[(string?)profile["activeWorkspace"] ?? ""];
        await WriteHelpResumeRecords(demo, directory);
        // The shell pane is a real terminal, as the Mac's is; no status line runs the user's own command into a picture.
        smokeTerminalEnabled = true;
        await service.UpdateAsync(s => s with { StatusLineEnabled = false, Sessions = [.. s.Sessions, .. sessions], ActiveWorkspaceId = firstWorkspace, ActiveSessionId = sessions[0].Id, ExpandedWorkspaceIds = [.. demo.Workspaces.Values] });
        Render();

        // The runs: the canned CLI in the real CLIs' place, each run waited for until its last line is in.
        var scripts = Path.Combine(directory, "help-demo"); Directory.CreateDirectory(scripts);
        var runs = new JsonArray(); var plays = new List<(JsonObject Pane, string Id, JsonObject Run, string Marker)>();
        foreach (var pane in panes)
        {
            var index = 0;
            foreach (var run in (pane["runs"]?.AsArray() ?? []).OfType<JsonObject>())
            {
                var marker = Path.Combine(scripts, $"{(string?)pane["role"]}-{index++}.done");
                runs.Add(new JsonObject { ["provider"] = (string?)pane["provider"], ["input"] = (string?)run["input"], ["frames"] = run["frames"]?.DeepClone(), ["marker"] = marker });
                plays.Add((pane, demo.Panes[HelpText(pane["role"])], run, marker));
            }
        }
        var script = Path.Combine(scripts, "script.json");
        await File.WriteAllTextAsync(script, new JsonObject { ["runs"] = runs }.ToJsonString());
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("the app's own binary is not known");
        foreach (var provider in (profile["providers"]?.AsArray() ?? []).OfType<JsonObject>())
            if ((string?)provider["id"] is "claude" or "codex" && (string?)provider["id"] is { } id)
                service.Providers.Override(id, new CliCommand(executable, [HelpDemoCli.Argument, script], id == "claude" ? "2.1.271" : (string?)provider["version"]));
        var timings = new Dictionary<string, AgentRunTiming>();
        foreach (var (pane, id, run, marker) in plays)
        {
            var current = service.Snapshot.Sessions.First(s => s.Id == id);
            var finish = (string?)run["finish"];
            await service.SendAsync(new StartRunRequest(id, current.WorkspaceId, current.Kind, (string?)run["input"] ?? "", [], current.Model, current.Provider, current.Settings, current.ResumeId));
            if (!await HelpWait(() => File.Exists(marker), 30)) throw new InvalidOperationException("the " + (string?)pane["role"] + " pane's run did not play");
            if (finish is not null && !await HelpWait(() => service.Snapshot.Sessions.First(s => s.Id == id).Status is not "running" and not "idle", 30))
                throw new InvalidOperationException("the " + (string?)pane["role"] + " pane's run did not end");
            await Task.Delay(400);
            var now = DateTimeOffset.UtcNow;
            if (run["elapsedSeconds"]?.GetValue<double>() is { } seconds) timings[id] = new(now.AddSeconds(-seconds), now, finish is null ? null : now);
        }
        await service.UpdateAsync(s => s with
        {
            Sessions = s.Sessions.Select(session =>
            {
                if (!demo.Panes.ContainsValue(session.Id)) return session;
                var spec = panes.First(p => demo.Panes[HelpText(p["role"])] == session.Id);
                return session with { RunTiming = timings.GetValueOrDefault(session.Id, session.RunTiming!) };
            }).ToList(),
        });
        // The drafts as typed, so each composer's own rules (send or queue) see them too.
        foreach (var pane in panes) if ((string?)pane["draft"] is { Length: > 0 } draft) await HelpDraft(demo.Panes[HelpText(pane["role"])], draft);
        foreach (var layout in demo.Layouts)
        {
            await SelectWorkspace(demo.Workspaces[HelpText(layout["workspace"])]);
            await SelectLayoutSession(demo.Pane(HelpText(layout["active"])));
            await ApplyLayoutPreset((string?)layout["preset"] ?? "grid");
        }
        await HelpSelect(demo);
        // The pet comes up as in a normal start (a capture, as a smoke, starts without it): Settings' pet list and the pet's own picture need it.
        await InitializeCompanionAsync();
        try { await PrepareHelpTerminal(demo, core); } catch (Exception ex) when (ex is not OutOfMemoryException) { demo.TerminalProblem = ex.Message; }
        return demo;
    }

    /// <summary>
    /// The Claude session records "continue a session" lists, as the Mac's capture writes them (HelpDemoRecords.claudeLines): each
    /// record's requests, each answered, the last <c>minutesAgo</c> back, under a history home in the profile that this process
    /// alone reads (<see cref="PaneView.HistoryHomeOverride"/>), so the user's own records never show.
    /// </summary>
    private async Task WriteHelpResumeRecords(HelpDemo demo, string directory)
    {
        var home = Path.Combine(directory, "help-demo", "home");
        var now = DateTimeOffset.UtcNow;
        foreach (var record in (demo.Profile["resumeSessions"]?.AsArray() ?? []).OfType<JsonObject>().Where(r => (string?)r["provider"] == "claude"))
        {
            var workspace = service.Snapshot.Workspaces.First(w => w.Id == demo.Workspaces[HelpText(record["workspace"])]);
            var folder = Path.Combine(home, ".claude", "projects", SessionHistory.ClaudeProjectFolder(workspace.Path));
            Directory.CreateDirectory(folder);
            var id = HelpText(record["sessionId"]); var title = HelpText(record["title"]);
            var modified = now.AddMinutes(-(record["minutesAgo"]?.GetValue<double>() ?? 0));
            var count = Math.Max(1, record["requests"]?.GetValue<int>() ?? 1);
            var lines = new List<string>();
            for (var index = 0; index < count; index++)
            {
                var stamp = modified.AddSeconds((index - count) * 90).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
                lines.Add(new JsonObject { ["cwd"] = workspace.Path, ["message"] = new JsonObject { ["content"] = title, ["role"] = "user" }, ["sessionId"] = id, ["timestamp"] = stamp, ["type"] = "user", ["uuid"] = $"u{index}-{id}" }.ToJsonString());
                lines.Add(new JsonObject { ["cwd"] = workspace.Path, ["message"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["text"] = title, ["type"] = "text" }), ["model"] = "claude-sonnet-5-5", ["role"] = "assistant" }, ["sessionId"] = id, ["timestamp"] = stamp, ["type"] = "assistant", ["uuid"] = $"a{index}-{id}" }.ToJsonString());
            }
            var file = Path.Combine(folder, id + ".jsonl");
            await File.WriteAllTextAsync(file, string.Join("\n", lines) + "\n");
            File.SetLastWriteTimeUtc(file, modified.UtcDateTime);
        }
        PaneView.HistoryHomeOverride = home;
    }

    /// <summary>
    /// Types the demo command into the shell pane's terminal, as the Mac's capture does: a prompt named after the workspace and a
    /// command of that name that prints the profile's canned output from a file, so nothing runs.
    /// </summary>
    private async Task PrepareHelpTerminal(HelpDemo demo, CoreWebView2 core)
    {
        var spec = demo.Profile["terminal"] as JsonObject ?? throw new InvalidOperationException("the demo has no terminal");
        var id = demo.Pane((string?)spec["role"] ?? "shell");
        var session = service.Snapshot.Sessions.First(s => s.Id == id);
        var name = service.Snapshot.Workspaces.First(w => w.Id == session.WorkspaceId).Name;
        await SelectLayoutSession(id);
        if (!await HelpUntil(core, "!!document.querySelector('[data-session-id=\"" + id + "\"] .xterm, .pane-slot.active .xterm')", 20)) throw new InvalidOperationException("the terminal did not open");
        var file = Path.Combine(options.ProfileDirectory!, "help-demo", "terminal-output.txt");
        await File.WriteAllTextAsync(file, string.Join("\r\n", (spec["output"]?.AsArray() ?? []).Select(l => (string?)l ?? "")) + "\r\n");
        var command = (string?)spec["command"] ?? "";
        if (!System.Text.RegularExpressions.Regex.IsMatch(command.Split(' ')[0], "^[A-Za-z][A-Za-z0-9_-]*$")) throw new InvalidOperationException("the terminal command needs a plain program name");
        var pane = WebTerminalPane(id);
        // A prompt like the workspace's, a function (or macro) of the command's name that types the canned output, and a cleared screen.
        Task Type(string text) => pane.HandleTerminalMessage(JsonSerializer.Serialize(new { type = "input", text }));
        var program = command.Split(' ')[0];
        await Type(Path.GetFileNameWithoutExtension(PseudoTerminal.DefaultShell).Equals("powershell", StringComparison.OrdinalIgnoreCase)
            ? $"Remove-Module PSReadLine -ErrorAction SilentlyContinue; function global:prompt {{ '{name} > ' }}; function global:{program} {{ [Console]::Write([IO.File]::ReadAllText('{file.Replace("'", "''")}')) }}; Clear-Host\r"
            : $"prompt {name} $G $S& doskey {program}=type \"{file}\"& cls\r");
        await Task.Delay(1200);
        await Type(command + "\r");
        var marker = (spec["output"]?.AsArray() ?? []).Select(l => ((string?)l ?? "").Trim()).LastOrDefault(l => l.Length > 0) ?? "";
        if (!await HelpUntil(core, "(document.querySelector('.pane-slot.active .xterm-rows')?.textContent ?? '').includes(" + JsonSerializer.Serialize(marker) + ")", 10)) throw new InvalidOperationException("the terminal output did not show");
    }

    /// <summary>Back to the overview: the active workspace with its layout's pane selected, every workspace in its own layout.</summary>
    private async Task HelpSelect(HelpDemo demo)
    {
        HideDashboard();
        foreach (var pane in (demo.Profile["panes"]?.AsArray() ?? []).OfType<JsonObject>())
        {
            if (!demo.Panes.TryGetValue(HelpText(pane["role"]), out var id) || service.Snapshot.Sessions.FirstOrDefault(s => s.Id == id) is not { } session) continue;
            var draft = (string?)pane["draft"] ?? "";
            var mode = (string?)pane["view"] == "mighty" ? "mighty" : null;
            if (session.AgentViewMode != mode || session.GraphViewMode is not null)
                await service.UpdateAsync(s => s with { Sessions = s.Sessions.Select(p => p.Id == id ? p with { AgentViewMode = mode, GraphViewMode = null } : p).ToList() });
            if (session.Draft != draft) await HelpDraft(id, draft);
        }
        var active = (string?)demo.Profile["activeWorkspace"] ?? "";
        if (demo.Layouts.FirstOrDefault(l => (string?)l["workspace"] == active) is { } layout)
        {
            await SelectWorkspace(demo.Workspaces[active]);
            await SelectLayoutSession(demo.Pane(HelpText(layout["active"])));
            if (LayoutMode(service.Snapshot, demo.Workspaces[active]) != (string?)layout["preset"]) await ApplyLayoutPreset((string?)layout["preset"] ?? "grid");
        }
        RenderSidebar(); Render();
        await Task.Delay(300);
    }

    // MARK: Shots

    private async Task<List<Dictionary<string, object?>>> CaptureHelpShot(JsonObject shot, string screen, List<string> themes, HelpDemo? demo, CoreWebView2 core, string output, Func<string, string, Dictionary<string, object?>> entry)
    {
        if (shot["window"] is JsonObject window) { HelpResize(window["width"]!.GetValue<int>(), window["height"]!.GetValue<int>()); await Task.Delay(400); }
        HelpTarget target;
        try { target = await OpenHelpScreen(screen, demo, core); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (demo is not null) await HelpSelect(demo);
            return themes.Select(theme => { var e = entry(theme, "failed"); e["error"] = ex.Message; return e; }).ToList();
        }
        var results = new List<Dictionary<string, object?>>();
        foreach (var theme in themes)
        {
            var value = entry(theme, "ok");
            try
            {
                if (service.Snapshot.Theme != theme) { await service.UpdateAsync(s => s with { Theme = theme }); Render(); }
                if (!await HelpUntil(core, "document.documentElement.dataset.theme === " + JsonSerializer.Serialize(theme), 10)) throw new InvalidOperationException("the page did not take the " + theme + " theme");
                await Task.Delay(700);
                if (error.Text is { Length: > 0 } message) { value["clearedError"] = message; error.Text = ""; Render(); await Task.Delay(300); }
                var file = Path.Combine(output, $"{(string?)shot["id"]}-{theme}.png");
                if (target.Save is { } save) await save(file);
                var (width, height) = target.Save is null ? await HelpSave(core, target.Clip, file) : HelpPngSize(await File.ReadAllBytesAsync(file));
                // MIGHTY_HELP_DOM=1 keeps the shot's markup beside its picture, for comparing a screen's layout with the Mac's.
                if (Environment.GetEnvironmentVariable("MIGHTY_HELP_DOM") == "1")
                {
                    var markup = await core.ExecuteScriptAsync("(document.querySelector(" + JsonSerializer.Serialize(target.Clip ?? "body") + ") ?? document.body).outerHTML");
                    await File.WriteAllTextAsync(Path.Combine(output, $"{(string?)shot["id"]}-{theme}.html"), JsonSerializer.Deserialize<string>(markup) ?? "");
                }
                value["pixelWidth"] = width; value["pixelHeight"] = height;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { value["status"] = "failed"; value["error"] = ex.Message; }
            results.Add(value);
        }
        if (target.Cleanup is { } cleanup) { try { await cleanup(); } catch (Exception ex) when (ex is not OutOfMemoryException) { PerfLog.Mark("help capture cleanup: " + ex.Message); } }
        if (demo is not null) await HelpSelect(demo);
        return results;
    }

    private async Task<HelpTarget> OpenHelpScreen(string screen, HelpDemo? demo, CoreWebView2 core)
    {
        if (screen == "welcome")
        {
            if (demo is not null || service.Snapshot.Workspaces.Count > 0) throw new InvalidOperationException("the welcome screen needs the empty profile");
            await Task.Delay(600);
            return new();
        }
        if (demo is null) throw new InvalidOperationException("the demo profile is not seeded");
        switch (screen)
        {
            case "overview":
                await HelpSelect(demo);
                return new();
            case "sidebar-collapsed":
                await SetSidebarCollapsed(true); await Task.Delay(500);
                return new(Cleanup: () => SetSidebarCollapsed(false));
            case "dashboard":
                showsDashboard = true; RenderSidebar(); RenderDashboard();
                if (!await HelpUntil(core, "!!document.querySelector('[data-testid=dashboard]')", 10)) throw new InvalidOperationException("the dashboard did not open");
                await Task.Delay(500);
                return new(Cleanup: () => { HideDashboard(); RenderSidebar(); return Task.CompletedTask; });
            case "agent-basic" or "agent-mighty" or "agent-mighty-overview" or "agent-timeline" or "agent-structure" or "result-card" or "composer" or "plan-card" or "question-card" or "permission-card" or "background-work" or "terminal-pane":
            {
                var role = screen switch { "agent-basic" => "codex", "plan-card" => "plan", "question-card" => "question", "permission-card" => "permission", "terminal-pane" => "shell", "result-card" => "result", _ => "mighty" };
                var id = demo.Pane(role);
                CheckHelpScreen(screen, id, demo);
                // As the Mac's capture: the timeline or the structure view, or the Default view while the background work runs.
                var view = screen switch { "agent-timeline" => "timeline", "agent-structure" => "structure", _ => null };
                if (view is not null || screen == "background-work")
                    await service.UpdateAsync(s => s with { Sessions = s.Sessions.Select(p => p.Id == id ? p with { GraphViewMode = view ?? p.GraphViewMode, AgentViewMode = screen == "background-work" ? "default" : p.AgentViewMode } : p).ToList() });
                await SelectLayoutSession(id); await ApplyLayoutPreset("focus");
                // The diagram aims at the next-request block while a draft is typed; the Mac's capture empties it for the screens that show the requests.
                var showsRequests = screen is "agent-mighty" or "agent-mighty-overview" or "agent-timeline" or "agent-structure" or "background-work";
                if (showsRequests) { await Task.Delay(400); await HelpDraft(id, ""); }
                Render();
                await Task.Delay(showsRequests || screen == "result-card" ? 1500 : 900);
                return new();
            }
            case "composer-settings":
            {
                var id = demo.Pane("mighty");
                await SelectLayoutSession(id); await ApplyLayoutPreset("focus"); await Task.Delay(600);
                if (!await HelpTrue(core, "(() => { const b = document.querySelector('.pane-slot.active:not(.hidden) [data-pill=more]'); if (!b) return false; b.click(); return true; })()")) throw new InvalidOperationException("the pane has no run settings button");
                if (!await HelpUntil(core, "!!document.querySelector('[data-testid=run-settings]')", 6)) throw new InvalidOperationException("the run settings did not open");
                await Task.Delay(500);
                return new("[data-testid=run-settings]", async () => { await core.ExecuteScriptAsync("document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))"); await Task.Delay(200); });
            }
            case "plan-document":
            {
                var id = demo.Pane("plan");
                await SelectLayoutSession(id); await ApplyLayoutPreset("focus"); await Task.Delay(800);
                if (!await HelpUntil(core, "(() => { const b = document.querySelector('.pane-slot.active:not(.hidden) [data-answer=plan-expand]'); if (!b) return false; b.click(); return true; })()", 6)) throw new InvalidOperationException("the plan card has no expand button");
                if (!await HelpUntil(core, "!!document.querySelector('[data-testid=plan-document]')", 6)) throw new InvalidOperationException("the plan document did not open");
                await Task.Delay(500);
                return new("[data-testid=plan-document]", async () => { await core.ExecuteScriptAsync("document.querySelector('[data-testid=plan-document-close]')?.click()"); await Task.Delay(200); });
            }
            case "session-info":
            {
                var id = demo.Pane("mighty");
                await SelectLayoutSession(id); await ApplyLayoutPreset("focus"); await Task.Delay(600);
                if (!await HelpTrue(core, "(() => { const r = document.querySelector('.pane-slot.active:not(.hidden) [data-testid=context-ring]'); if (!r) return false; r.click(); return true; })()")) throw new InvalidOperationException("the pane has no context ring");
                if (!await HelpUntil(core, "!!document.querySelector('[data-testid=session-info]')", 6)) throw new InvalidOperationException("the session details did not open");
                await Task.Delay(500);
                return new("[data-testid=session-info]", async () => { await core.ExecuteScriptAsync("document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))"); await Task.Delay(200); });
            }
            case "settings-general" or "settings-models" or "settings-styles" or "settings-tools" or "settings-cli" or "settings-mobile" or "settings-companion" or "settings-about":
            {
                // As the Mac's: the saved category, then Settings opened; the sheet alone is the picture.
                var category = screen["settings-".Length..];
                await service.UpdateAsync(s => s with { SettingsPane = category });
                if (!SelectWebSettingsCategory(category)) _ = ShowWebSettingsAsync();
                if (!await HelpUntil(core, "!!document.querySelector('[data-testid=settings-form][data-category=" + category + "]')", 10)) throw new InvalidOperationException("Settings did not open on " + category);
                await Task.Delay(900);
                return new("[data-testid=native-settings]", async () => { CloseWebSettings(); await Task.Delay(300); });
            }
            case "resume-choice" or "resume-list":
            {
                // Add Pane → Claude in the active workspace, which has earlier sessions: the choice, and for the list its Continue.
                await HelpSelect(demo);
                _ = WebAddAgentPane("claude", null);
                if (!await HelpUntil(core, "!!document.querySelector('[data-testid=resume-choice-resume]:not([disabled])')", 10)) throw new InvalidOperationException("the resume choice did not open");
                if (screen == "resume-list")
                {
                    await core.ExecuteScriptAsync("document.querySelector('[data-testid=resume-choice-resume]')?.click()");
                    if (!await HelpUntil(core, "document.querySelectorAll('[data-testid=resume-rows] .resume-row').length > 0", 10)) throw new InvalidOperationException("the session list shows no record");
                }
                await Task.Delay(600);
                return new("[data-testid=dialog] .sheet", async () =>
                {
                    await core.ExecuteScriptAsync("document.querySelector('[data-testid=dialog] .sheet')?.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))");
                    await Task.Delay(400);
                });
            }
            case "companion-pet":
            {
                // The pet is its own window (CompanionOverlay), not the page: its picture is the one it last put on screen, with the
                // permission pane's approval in its bubble as the Mac's capture focuses it (store.companion.focus).
                var id = demo.Pane("permission");
                // A capture, as a smoke, starts without the pet; it comes up here, in the profile's own preferences.
                await InitializeCompanionAsync();
                if (!await HelpWait(() => companionOverlay is not null, 10) || !companionPreferences.Enabled) throw new InvalidOperationException("the pet is not shown");
                companionPinned = id; RefreshCompanion();
                await Task.Delay(1200);
                return new(Cleanup: () => { companionPinned = null; RefreshCompanion(); return Task.CompletedTask; },
                    Save: async path => { RefreshCompanion(); DrawCompanion(); await Task.Delay(300); if (companionOverlay is not { } pet || !await pet.SavePicture(path)) throw new InvalidOperationException("the pet has no picture"); });
            }
            case "files-pane":
            {
                var workspace = demo.Workspaces[HelpText(demo.Profile["activeWorkspace"])];
                await OpenFilePane(workspace);
                // The files pane on its own first: a change of layout draws the pane again, which would drop a selection made before it.
                var paneId = service.Snapshot.ActiveSessionId;
                await ApplyLayoutPreset("focus"); await Task.Delay(500);
                if (!await HelpUntil(core, "!!document.querySelector('.pane-slot.active:not(.hidden) [data-testid=files-tree]')", 10)) throw new InvalidOperationException("the files pane did not open");
                static string Click(string name) => "(() => { const row = [...document.querySelectorAll('.pane-slot.active:not(.hidden) [data-testid=files-tree] [data-path]')].find(e => e.dataset.path === " + JsonSerializer.Serialize(name) + "); if (!row) return false; row.click(); return true; })()";
                if (!await HelpUntilClick(core, Click("src"))) throw new InvalidOperationException("the files tree has no src folder");
                await Task.Delay(600);
                if (!await HelpUntilClick(core, Click("README.md"))) throw new InvalidOperationException("the files tree has no README.md");
                if (!await HelpUntil(core, "!!document.querySelector('.pane-slot.active:not(.hidden) [data-testid=files-preview] .markdown')", 10)) throw new InvalidOperationException("the README preview did not show");
                await Task.Delay(500);
                return new(Cleanup: async () =>
                {
                    if (paneId is not null && service.Snapshot.Sessions.Any(s => s.Id == paneId && s.Kind != "claude" && s.Kind != "shell")) await CloseSession(paneId);
                    foreach (var layout in demo.Layouts.Where(l => (string?)l["workspace"] == (string?)demo.Profile["activeWorkspace"]))
                    {
                        await SelectLayoutSession(demo.Pane(HelpText(layout["active"]))); await ApplyLayoutPreset((string?)layout["preset"] ?? "grid");
                    }
                });
            }
            default:
                throw new InvalidOperationException("unknown screen " + screen);
        }
    }

    /// <summary>A pane's draft as typed in its composer: the page's editor and the composer's own rules (send, queue) both see it.</summary>
    private async Task HelpDraft(string id, string text)
    {
        if (service.Snapshot.Sessions.FirstOrDefault(s => s.Id == id) is not { Kind: "claude" or "shell" }) return;
        WebPane(id).WebSetDraft(text, save: true);
        // A composer the page already shows keeps its own editor's text: it is typed there as well, through the editor's input event.
        if (webShell is not null && await webShell.Started is { } core)
            await core.ExecuteScriptAsync("(() => { const set = Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value').set; for (const e of document.querySelectorAll('[data-session-id=\"" + id + "\"] [data-testid=composer-input]')) { set.call(e, " + JsonSerializer.Serialize(text) + "); e.dispatchEvent(new Event('input', { bubbles: true })); } })()");
    }

    /// <summary>Checks that the seeded pane is in the state the screen is meant to show, as the Mac's capture does.</summary>
    private void CheckHelpScreen(string screen, string id, HelpDemo demo)
    {
        var session = service.Snapshot.Sessions.FirstOrDefault(s => s.Id == id) ?? throw new InvalidOperationException("the pane is missing");
        switch (screen)
        {
            case "agent-mighty" or "agent-mighty-overview" or "agent-timeline" or "agent-structure" or "result-card" when session.AgentViewMode != "mighty" || session.GraphRuns?.Any(r => r.Status == "completed") != true:
                throw new InvalidOperationException("the Mighty pane has no finished request");
            case "background-work" when session.BackgroundWork?.WaitingOnBackground != true:
                throw new InvalidOperationException("the pane has no background work");
            case "terminal-pane" when demo.TerminalProblem is { } problem:
                throw new InvalidOperationException("the terminal is not ready: " + problem);
        }
    }

    /// <summary>The window's content at <paramref name="width"/> × <paramref name="height"/> points.</summary>
    private void HelpResize(int width, int height)
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: not OverlappedPresenterState.Restored } presenter) presenter.Restore();
        var scale = root.XamlRoot?.RasterizationScale ?? 1;
        AppWindow.ResizeClient(new SizeInt32((int)Math.Round(width * scale), (int)Math.Round(height * scale)));
        AppWindow.Move(new PointInt32(0, 0));
    }

    /// <summary>The page's picture as a PNG; with <paramref name="clip"/> only that element's box, cut out by the DevTools protocol at the page's scale.</summary>
    private static async Task<(int Width, int Height)> HelpSave(CoreWebView2 core, string? clip, string path)
    {
        byte[] bytes;
        if (clip is null)
        {
            using var stream = new InMemoryRandomAccessStream();
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)stream.Size); bytes = new byte[(int)stream.Size]; reader.ReadBytes(bytes);
        }
        else
        {
            var box = await core.ExecuteScriptAsync("(() => { const e = document.querySelector(" + JsonSerializer.Serialize(clip) + "); if (!e) return null; const r = e.getBoundingClientRect(); return { x: r.left, y: r.top, width: r.width, height: r.height, scale: window.devicePixelRatio }; })()");
            using var parsed = JsonDocument.Parse(box);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("the element to capture is gone");
            var r = parsed.RootElement;
            var arguments = JsonSerializer.Serialize(new { format = "png", clip = new { x = r.GetProperty("x").GetDouble(), y = r.GetProperty("y").GetDouble(), width = r.GetProperty("width").GetDouble(), height = r.GetProperty("height").GetDouble(), scale = r.GetProperty("scale").GetDouble() } });
            using var result = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", arguments));
            bytes = Convert.FromBase64String(result.RootElement.GetProperty("data").GetString() ?? "");
        }
        await File.WriteAllBytesAsync(path, bytes);
        return HelpPngSize(bytes);
    }

    /// <summary>A PNG's width and height: the IHDR's first two big-endian words.</summary>
    private static (int Width, int Height) HelpPngSize(byte[] bytes) =>
        bytes.Length > 24 ? ((bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19], (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23]) : (0, 0);

    private static async Task<bool> HelpTrue(CoreWebView2 core, string script) => await core.ExecuteScriptAsync(script) == "true";

    private static async Task<bool> HelpUntil(CoreWebView2 core, string script, double seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline) { if (await HelpTrue(core, script)) return true; await Task.Delay(100); }
        return false;
    }

    private static Task<bool> HelpUntilClick(CoreWebView2 core, string script) => HelpUntil(core, script, 10);

    private static async Task<bool> HelpWait(Func<bool> done, double seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline) { if (done()) return true; await Task.Delay(100); }
        return false;
    }
}
