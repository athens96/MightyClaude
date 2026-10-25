using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.CompilerServices;
using MightyClaude.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private Func<StartRunRequest, Task>? smokeStart;
    /// <summary>
    /// The WinUI fallback's smoke (the <c>ui-winui</c> escape hatch and the window without WebView2): the window starts on the
    /// WinUI screens, a Claude pane and a shell pane show side by side, Settings opens in its own window and closes again, and
    /// nothing throws. Everything the app does beside its screens is the web smoke's (MainWindow.WebShell.LiveSmoke.cs).
    /// </summary>
    private async Task RunUISmoke()
    {
        var result = new Dictionary<string, object?> { ["passed"] = false, ["winuiFallback"] = true, ["aiRequestSent"] = false, ["clipboardUsed"] = false };
        var directory = options.ProfileDirectory ?? throw new InvalidOperationException("smoke profile missing");
        var passed = false;
        void Checkpoint(string name, string state)
        {
            result["phase"] = name; result["stepState"] = state;
            options.TraceStartup("smoke:" + name + ":" + state);
            File.WriteAllText(Path.Combine(directory, "smoke-result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        }
        try
        {
            Checkpoint("started", "running");
            Require(webShell is null, "the WinUI smoke must run without the web shell");
            var project = Path.Combine(directory, "Fixture project"); Directory.CreateDirectory(project);
            var workspace = await service.AddWorkspaceAsync(project);
            runtime = new("win32", "smoke", true, "fixture", ProviderCatalog.Fallback("claude"), Wire.Providers.Select(p => new ProviderRuntime(p, ProviderCatalog.Name(p), true, "fixture", "isolated fixture", ProviderCatalog.Fallback(p), ProviderCatalog.Capabilities(p))).ToList(), null);
            RunSession[] sessions =
            [
                new() { WorkspaceId = workspace.Id, Kind = "claude", Provider = "claude", Title = "Claude", Status = "completed", Logs = [new(Wire.Id(), "user", "fixture request", Wire.Now()), new(Wire.Id(), "assistant", "fixture answer", Wire.Now(), "claude")] },
                new() { WorkspaceId = workspace.Id, Kind = "shell", Provider = "claude", Title = "Shell", Status = "completed" },
            ];
            await service.UpdateAsync(s => s with { Sessions = [.. sessions], ActiveWorkspaceId = workspace.Id, ActiveSessionId = sessions[0].Id });
            await ApplyLayoutPreset("columns");
            Checkpoint("started", "passed");
            Checkpoint("panes", "running");
            await WaitUI(() => root.XamlRoot is not null && root.ActualWidth > 0 && sessions.All(s => views.TryGetValue(s.Id, out var view) && view.Container.IsLoaded && view.Container.ActualWidth > 0), seconds: 15);
            result["panes"] = new Dictionary<string, object?> { ["claude"] = true, ["shell"] = true, ["shown"] = paneHosts.Count };
            Checkpoint("panes", "passed");
            Checkpoint("settings", "running");
            var opening = ShowCategorizedSettingsAsync();
            await WaitUI(() => settingsWindow?.Content is FrameworkElement { IsLoaded: true, ActualWidth: > 0 }, seconds: 15);
            settingsWindow!.Close();
            await opening.WaitAsync(TimeSpan.FromSeconds(15));
            await WaitUI(() => settingsWindow is null);
            result["settings"] = new Dictionary<string, object?> { ["opened"] = true, ["closed"] = true };
            Checkpoint("settings", "passed");
            root.UpdateLayout(); await Task.Delay(200);
            result["screenshot"] = await CaptureSmoke(Path.Combine(directory, "smoke-window.png"));
            result["passed"] = true; passed = true;
        }
        catch (Exception ex)
        {
            result["error"] = ex.Message; result["exceptionType"] = ex.GetType().FullName; result["exception"] = ex.ToString();
            try { result["screenshot"] = await CaptureSmoke(Path.Combine(directory, "smoke-window.png")); } catch (Exception capture) { result["captureError"] = capture.Message; }
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "smoke-result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        // A failed run's record belongs to that failure. An exception thrown while cleaning up must not overwrite it.
        if (!passed) options.KeepFailureRecord();
        await FinishSmoke(passed);
    }


    // Builds the phase-model section from fixed tool values. It neither reads nor writes the real user's ~/.config or
    // ~/.ouroboros. There must be four phase rows and four tool groups, and
    // a run row whose pinned handles differ must show as mixed.
    private bool RunPhaseModelsSectionSmoke()
    {
        var panel = BuildPhaseModelsSection(new PhaseModelsSnapshot { ClaudeMain = "fixture-main", CodexSubagentEffort = "xhigh" }, PhaseModelSection.SmokeFixtureTools);
        var elements = PhaseModelElements(panel).ToList();
        ComboBox Picker(string id) => elements.OfType<ComboBox>().Single(box => AutomationProperties.GetAutomationId(box) == id);
        Require(elements.Count(e => AutomationProperties.GetAutomationId(e).StartsWith("phaseModels-provider-", StringComparison.Ordinal)) == 2, "Separate Claude and Codex settings are present.");
        Require(Picker("phaseModels-model-claude-execution").SelectedItem is ComboBoxItem { Tag: PhaseModelSection.MixedSentinel }, "Distinct Claude main/alias values show mixed.");
        Require(Picker("phaseModels-effort-codex-subagents").SelectedItem is ComboBoxItem { Tag: "xhigh" }, "Saved Codex subagent effort remains visible.");
        Require(!elements.Any(e => AutomationProperties.GetAutomationId(e) == "phaseModels-model-codex-execution" || AutomationProperties.GetAutomationId(e) == "phaseModels-model-codex-planning"), "Codex main model belongs to the pane.");
        foreach (var provider in new[] { "claude", "codex" })
        {
            Require(elements.OfType<TextBox>().Any(e => AutomationProperties.GetAutomationId(e) == "phaseModels-add-" + provider), "Provider model registration is available.");
            Require(elements.OfType<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>().Count(e => AutomationProperties.GetAutomationId(e).StartsWith("phaseModels-addLevel-" + provider + "-", StringComparison.Ordinal)) == Wire.Efforts.Length, "Every supported reasoning level can be registered.");
        }
        return true;
    }

    // Builds the components section from a toolkit.json in a temporary folder. The fixture has one each of plugin, npm and winget
    // (items for this PC) and brew and repoScript (macOS-only items). The list shows only this PC's
    // items, the install plan runs only through a fake executor (no real process), and after a save
    // both macOS-only objects must still be in the file unchanged. The real user's
    // files are neither read nor written.
    private const string ComponentsSmokeFixture = """
        {
          "version": 1,
          "entries": [
            { "id": "smoke-plugin", "displayName": "Smoke plugin", "install": { "kind": "plugin", "source": "athens96/smoke-plugin", "pluginID": "smoke-plugin@smoke-market" } },
            { "id": "smoke-npm", "displayName": "Smoke npm", "install": { "kind": "package", "manager": "npm", "name": "smoke-npm" } },
            { "id": "smoke-winget", "displayName": "Smoke winget", "install": { "kind": "package", "manager": "winget", "name": "Smoke.Tool", "executable": "smoke-tool.exe" } },
            { "id": "smoke-brew", "displayName": "Smoke brew", "install": { "kind": "package", "manager": "brew", "name": "smoke-brew" }, "approval": { "contentHash": "0000000000000000000000000000000000000000000000000000000000000000" } },
            { "id": "smoke-repo-script", "displayName": "Smoke script", "install": { "kind": "repoScript", "url": "https://github.com/athens96/smoke-script.git", "ref": "v1", "scriptPath": "install.sh" } }
          ]
        }
        """;
    private static readonly string[] ComponentsSmokeVisible = ["smoke-plugin", "smoke-npm", "smoke-winget"];
    private static readonly string[] ComponentsSmokeOtherOs = ["smoke-brew", "smoke-repo-script"];

    private bool RunComponentsSectionSmoke(Dictionary<string, object?> result, List<string> leakStrings)
    {
        var folder = Path.Combine(Path.GetTempPath(), "mighty-components-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var previousStore = toolkitStore;
        try
        {
            var toolkitPath = Path.Combine(folder, "toolkit.json");
            File.WriteAllText(toolkitPath, ComponentsSmokeFixture);
            var store = new ToolkitStore(folder);
            toolkitStore = store;
            // Approving rewrites the file: the macOS-only objects must survive this save too.
            foreach (var id in ComponentsSmokeVisible) store.Approve(id);

            // The CLI rows and the toolkit are two boxes of the tab now, as on the Mac; both are built here.
            var panel = BuildComponentsSection();
            var toolkit = BuildToolkitSection();
            const string rowPrefix = "toolkit-entry-";
            var visibleIds = SettingsElements(toolkitListPanel!)
                .Select(AutomationProperties.GetAutomationId)
                .Where(id => id.StartsWith(rowPrefix, StringComparison.Ordinal))
                .Select(id => id[rowPrefix.Length..])
                .ToList();
            result["toolkitVisibleIds"] = visibleIds;
            foreach (var id in ComponentsSmokeVisible)
                Require(visibleIds.Contains(id), "구성 요소 칸에 이 PC 항목이 보이지 않습니다: " + id);
            foreach (var id in ComponentsSmokeOtherOs)
                Require(!visibleIds.Contains(id), "구성 요소 칸에 macOS 전용 항목이 보입니다: " + id);
            Require(panel.Children.OfType<FrameworkElement>().Any() && toolkit.Children.OfType<FrameworkElement>().Any(), "구성 요소 칸이 비어 있습니다.");

            var probe = new ToolkitProbeContext
            {
                HomeDirectory = Path.Combine(folder, "home"),
                PathDirectories = [],
                LocalAppData = Path.Combine(folder, "local"),
            };
            var executor = new SmokeToolkitExecutor(probe.LocalAppData);
            var runner = new ToolkitRunner(store, probe);
            var runs = runner.Run(runner.Plan(), executor);
            FillToolkitResults(toolkitResultsPanel!, runs);
            result["toolkitRunResults"] = runs.Select(run => new Dictionary<string, object?>
            {
                ["id"] = run.EntryId,
                ["verdict"] = run.RunVerdict.ToString(),
                ["steps"] = run.Steps.Select(step => string.Join(" ", step.Argv) + " => " + step.Outcome).ToList(),
            }).ToList();
            var runIds = runs.Select(run => run.EntryId).ToList();
            foreach (var id in ComponentsSmokeVisible)
                Require(runIds.Contains(id), "승인된 이 PC 항목이 설치 계획에 없습니다: " + id);
            foreach (var id in ComponentsSmokeOtherOs)
                Require(!runIds.Contains(id), "macOS 전용 항목이 설치 계획에 들어갔습니다: " + id);
            Require(executor.Commands.Count > 0 && executor.Commands.All(argv => argv[0] is "claude" or "npm" or "winget"),
                "가짜 실행기가 이 PC 명령 밖의 명령을 받았습니다: " + string.Join(" | ", executor.Commands.Select(argv => string.Join(" ", argv))));
            Require(runs.Single(run => run.EntryId == "smoke-winget").RunVerdict == ToolkitRunItem.Verdict.Installed,
                "winget 항목이 다시 조사한 결과 설치됨으로 보이지 않습니다.");

            // The section's text, with the install result table filled in, goes into the locale-key leak check.
            CollectVisibleStrings(panel, leakStrings);
            CollectVisibleStrings(toolkit, leakStrings);

            using var before = JsonDocument.Parse(ComponentsSmokeFixture);
            using var after = JsonDocument.Parse(File.ReadAllText(toolkitPath));
            foreach (var id in ComponentsSmokeOtherOs)
            {
                var original = before.RootElement.GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("id").GetString() == id);
                var kept = after.RootElement.GetProperty("entries").EnumerateArray().Where(e => e.GetProperty("id").GetString() == id).ToList();
                Require(kept.Count == 1 && JsonNode.DeepEquals(JsonNode.Parse(original.GetRawText()), JsonNode.Parse(kept[0].GetRawText())),
                    "macOS 전용 항목이 toolkit.json에서 사라졌거나 바뀌었습니다: " + id);
            }
            return true;
        }
        finally
        {
            toolkitStore = previousStore;
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // A fake executor that only records install commands; it starts no process. A winget command
    // creates an executable in the temporary LocalAppData's WinGet\Links, so the re-inspection step
    // is checked to judge "installed" from the file alone.
    private sealed class SmokeToolkitExecutor(string localAppData) : IToolkitRunnerExecutor
    {
        public List<IReadOnlyList<string>> Commands { get; } = [];

        public ToolkitCommandOutput Run(IReadOnlyList<string> argv)
        {
            Commands.Add(argv);
            if (argv.Count > 4 && argv[0] == "winget" && argv[1] == "install")
            {
                var links = Path.Combine(localAppData, "Microsoft", "WinGet", "Links");
                Directory.CreateDirectory(links);
                File.WriteAllText(Path.Combine(links, "smoke-tool.exe"), "");
            }
            return ToolkitCommandOutput.Success;
        }
    }

    private async Task FinishSmoke(bool passed)
    {
        smokeStart = null; clock.Stop();
        if (!options.SmokeExit) return;
        closing = true;
        try { await service.DisposeAsync(); } catch (Exception ex) { options.WriteStartupFailure(ex); passed = false; }
        Environment.ExitCode = passed ? 0 : 1; canClose = true; Close(); Application.Current.Exit();
    }
    private async Task<string> CaptureSmoke(string path)
    {
        var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(root);
        if (bitmap.PixelWidth == 0 || bitmap.PixelHeight == 0) throw new InvalidOperationException("WinUI 화면을 캡처하지 못했습니다.");
        var buffer = await bitmap.GetPixelsAsync(); using var reader = DataReader.FromBuffer(buffer); var pixels = new byte[buffer.Length]; reader.ReadBytes(pixels);
        using var stream = new InMemoryRandomAccessStream(); var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels); await encoder.FlushAsync();
        using var source = stream.GetInputStreamAt(0); using var output = new DataReader(source); await output.LoadAsync((uint)stream.Size); var png = new byte[(int)stream.Size]; output.ReadBytes(png); await File.WriteAllBytesAsync(path, png); return path;
    }

    private static void CollectVisibleStrings(DependencyObject element, List<string> strings)
    {
        switch (element)
        {
            case TextBlock tb:
                if (!string.IsNullOrEmpty(tb.Text)) strings.Add(tb.Text);
                foreach (var inline in tb.Inlines)
                    if (inline is Microsoft.UI.Xaml.Documents.Run run && !string.IsNullOrEmpty(run.Text))
                        strings.Add(run.Text);
                break;
            case TextBox txb:
                if (!string.IsNullOrEmpty(txb.Text)) strings.Add(txb.Text);
                break;
            case ContentControl cc when cc.Content is string cs && !string.IsNullOrEmpty(cs):
                strings.Add(cs);
                break;
        }
        if (element is UIElement uie)
        {
            var tip = ToolTipService.GetToolTip(uie);
            var tipText = tip is ToolTip tt ? tt.Content as string : tip as string;
            if (!string.IsNullOrEmpty(tipText)) strings.Add(tipText);
        }
        var childCount = VisualTreeHelper.GetChildrenCount(element);
        for (var i = 0; i < childCount; i++)
            CollectVisibleStrings(VisualTreeHelper.GetChild(element, i), strings);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private static async Task WaitUI(Func<bool> predicate, [CallerArgumentExpression(nameof(predicate))] string condition = "", double seconds = 4)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!predicate()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException("WinUI 검증 상태 대기 시간이 초과되었습니다: " + condition); await Task.Delay(20); }
    }

}
