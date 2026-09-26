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
    private Task StartFromComposer(StartRunRequest request) => options.SmokeTest
        ? smokeStart?.Invoke(request) ?? throw new InvalidOperationException("스모크 모드에서는 실제 CLI를 실행하지 않습니다.")
        : service.StartAsync(request);

    private async Task RunUISmoke()
    {
        var result = new Dictionary<string, object?> { ["passed"] = false, ["aiRequestSent"] = false, ["clipboardUsed"] = false, ["physicalIMEAndPointerTested"] = false };
        var directory = options.ProfileDirectory ?? throw new InvalidOperationException("스모크 프로필이 없습니다.");
        var passed = false;
        try
        {
            var project = Path.Combine(directory, "Fixture project"); Directory.CreateDirectory(project);
            var workspace = await service.AddWorkspaceAsync(project);
            var otherPath = Path.Combine(directory, "Second project"); Directory.CreateDirectory(otherPath); var other = await service.AddWorkspaceAsync(otherPath);
            var now = DateTimeOffset.UtcNow;
            var activities = new AgentActivity("fixture-command", "claude", "command", "completed", "dotnet test", DurationMs: 12300, Output: "12 tests passed");
            const string markdown = "## Windows 네이티브 검증\n\n첫 번째 **문단**입니다.\n\n두 번째 문단도 함께 선택할 수 있습니다.\n\n- 목록 항목\n- 다음 항목\n\n> 인용문\n\n```csharp\nConsole.WriteLine(\"Hello\");\n```\n\n| 항목 | 결과 |\n| --- | --- |\n| UI | 완료 |";
            var sessions = Wire.Providers.Select(provider => new RunSession
            {
                WorkspaceId = workspace.Id, Provider = provider, Title = ProviderCatalog.Name(provider), Status = "completed",
                RunTiming = new(now.AddSeconds(-42), now, now),
                Logs = [new(Wire.Id(), "user", "Windows 클라이언트를 확인해 줘.", Wire.Now(), provider), new(Wire.Id(), "assistant", markdown, Wire.Now(), provider), new(Wire.Id(), "system", activities.Summary, Wire.Now(), provider, activities with { Provider = provider })],
                SessionUsage = provider == "claude" ? new() { Provider = provider, Source = "smoke.fixture", Model = "fixture-model", InputTokens = 42000, OutputTokens = 1250, ContextUsedTokens = 43250, ContextWindowTokens = 200000 } : null
            }).ToList();
            runtime = new("win32", "smoke", true, "fixture", ProviderCatalog.Fallback("claude"), Wire.Providers.Select(p => new ProviderRuntime(p, ProviderCatalog.Name(p), true, "fixture", "isolated fixture", ProviderCatalog.Fallback(p), ProviderCatalog.Capabilities(p))).ToList(), null);
            await service.UpdateAsync(s => s with { Sessions = sessions, ActiveWorkspaceId = workspace.Id, ActiveSessionId = sessions[0].Id });
            await ApplyLayoutPreset("tabs");
            await WaitUI(() => root.XamlRoot is not null && root.ActualWidth > 0 && views.TryGetValue(sessions[0].Id, out var p) && p.Container.ActualWidth > 0);
            var pane = views[sessions[0].Id];
            result["composerAndTranscript"] = await pane.RunComposerSmoke();
            result["slashCommandPalette"] = await pane.RunSlashCommandPaletteSmoke();
            result["statusLine"] = await pane.RunStatusLineSmoke();
            result["toolPermission"] = await pane.RunToolPermissionSmoke();
            result[CompletionNotificationSmokeOutcome.ResultKey] = await RunCompletionNotificationSmoke();
            result[SettingsSectionsSmokeOutcome.ResultKey] = await RunSettingsSectionsSmoke();
            result["phaseModelsSection"] = RunPhaseModelsSectionSmoke();
            var componentsLeakStrings = new List<string>();
            result["componentsSection"] = RunComponentsSectionSmoke(result, componentsLeakStrings);
            result[AccountUsageSmokeOutcome.ResultKey] = await RunAccountUsageSmoke();
            result["usageReset"] = await RunUsageResetSmoke();
            result[AppUpdateSmokeOutcome.ResultKey] = await RunAppUpdateSectionSmoke();
            result["liveWiring"] = await RunLiveWiringSmoke();
            result["rename"] = await RunRenameSmoke();
            result[ClaudePluginSmokeOutcome.ResultKey] = await RunClaudePluginSmoke();
            result[CodexPluginSmokeOutcome.ResultKey] = await RunCodexPluginSmoke();
            result[PluginMarketplaceSmokeOutcome.ResultKey] = await RunPluginMarketplaceSmoke();
            var mightyLeakStrings = new List<string>();
            result["mightyGraph"] = await RunMightyGraphSmoke(pane, workspace, mightyLeakStrings);
            await ApplyLayoutPreset("focus"); await SelectWorkspace(other.Id);
            Require(LayoutMode(service.Snapshot, workspace.Id) == "focus" && LayoutMode(service.Snapshot, other.Id) != "focus", "집중 모드가 다른 워크스페이스에 영향을 주었습니다.");
            await SelectWorkspace(workspace.Id); Require(service.Snapshot.ActiveSessionId == sessions[0].Id, "워크스페이스의 마지막 탭 선택이 복원되지 않았습니다.");
            await ApplyLayoutPreset("tabs");
            var group = PaneLayout.Groups(EffectiveLayout(service.Snapshot, workspace.Id)).First();
            await DockSession(sessions[1].Id, workspace.Id, group.Id, "right");
            Require(EffectiveLayout(service.Snapshot, workspace.Id)?.Kind == "split", "좌우 분할을 저장하지 못했습니다.");
            await SelectLayoutSession(sessions[0].Id);
            Require(ReferenceEquals(pane, views[sessions[0].Id]), "탭 이동이 기존 입력창을 다시 만들었습니다.");
            Require(service.Snapshot.Sessions.First(p => p.Id == sessions[0].Id).Draft == "다음 요청 초안", "분할 후 초안이 보존되지 않았습니다.");
            Require(paneHosts.Count == 2 && paneHosts.All(pair => ReferenceEquals(pair.Value.Child, views[pair.Key].Container)), "분할 후 실행 창의 소유 컨테이너가 잘못 연결되었습니다.");
            // Exercise immediate tab changes and split -> merged tabs -> split
            // without a dispatcher delay; old detached trees must release panes.
            await SelectLayoutSession(sessions[2].Id); await SelectLayoutSession(sessions[0].Id);
            var mergeTarget = PaneLayout.Groups(EffectiveLayout(service.Snapshot, workspace.Id)).First(g => g.SessionIds.Contains(sessions[0].Id));
            await DockSession(sessions[1].Id, workspace.Id, mergeTarget.Id, "center");
            Require(paneHosts.Count == 1 && EffectiveLayout(service.Snapshot, workspace.Id)?.Kind == "tabs", "분할 창을 하나의 탭 그룹으로 합치지 못했습니다.");
            await SelectLayoutSession(sessions[0].Id);
            Require(ReferenceEquals(pane, views[sessions[0].Id]) && service.Snapshot.Sessions.First(p => p.Id == sessions[0].Id).Draft == "다음 요청 초안", "탭 합치기가 입력창 또는 초안을 바꿨습니다.");
            result["paneReparentingAcrossImmediateTabAndSplitChanges"] = true;
            result["workspaceModesSelectionSplitDraftPreserved"] = true;
            var originalTheme = service.Snapshot.Theme;
            try
            {
                await service.UpdateAsync(s => s with { Theme = "light" }); Render();
                Require(root.RequestedTheme == ElementTheme.Light && root.Background is SolidColorBrush { Color.A: 255 }, "밝은 테마의 창 배경이 불투명하지 않습니다.");
                var lightColor = ((SolidColorBrush)root.Background).Color;
                root.UpdateLayout(); await Task.Delay(120);
                result["lightThemeScreenshot"] = await CaptureSmoke(Path.Combine(directory, "smoke-window-light.png"));
                await service.UpdateAsync(s => s with { Theme = "dark" }); Render();
                Require(root.RequestedTheme == ElementTheme.Dark && root.Background is SolidColorBrush { Color.A: 255 } dark && dark.Color.R < lightColor.R, "어두운 테마의 창 배경이 투명하거나 밝은 테마와 구분되지 않습니다.");
                result["opaqueBackgroundInBothThemes"] = true;
            }
            finally { await service.UpdateAsync(s => s with { Theme = originalTheme }); Render(); }
            await ApplyLayoutPreset("columns"); root.UpdateLayout(); await Task.Delay(120);
            var leakStrings = new List<string>();
            CollectVisibleStrings(root, leakStrings);
            var settingsSectionsForLeak = GetSettingsSections();
            var settingsPanelForLeak = new StackPanel { Spacing = 0, MinWidth = 420, MaxWidth = 540 };
            foreach (var sec in settingsSectionsForLeak)
                settingsPanelForLeak.Children.Add(BuildSectionContainer(sec.Title, sec.Build()));
            CollectVisibleStrings(settingsPanelForLeak, leakStrings);
            // 페이즈별 모델 칸의 ComboBox 머리글과 항목은 화면 나무에 바로 보이지 않으므로 따로 넣는다.
            leakStrings.AddRange(PhaseModelSectionTexts(BuildPhaseModelsSection(new(), PhaseModelSection.SmokeFixtureTools)));
            // 구성 요소 칸은 임시 toolkit.json과 실행 결과 표까지 채운 모습으로 넣는다.
            leakStrings.AddRange(componentsLeakStrings);
            // mighty 그래프 캔버스의 글자도 로케일 키 누수 검사에 넣는다.
            leakStrings.AddRange(mightyLeakStrings);
            var koKeys = Locale.Catalogue("ko").Keys.ToList();
            var keyLeaks = LocaleKeyLeak.Detect(leakStrings, koKeys);
            result["localeKeyLeakScanned"] = leakStrings.Count;
            result["localeKeyLeaks"] = keyLeaks;
            Require(keyLeaks.Count == 0, "로케일 키가 화면에 그대로 노출됩니다: " + string.Join(", ", keyLeaks));
            result["screenshot"] = await CaptureSmoke(Path.Combine(directory, "smoke-window.png"));
            result["passed"] = true; passed = true;
        }
        catch (Exception ex)
        {
            result["error"] = ex.Message; result["exceptionType"] = ex.GetType().FullName; result["exception"] = ex.ToString();
            try { result["screenshot"] = await CaptureSmoke(Path.Combine(directory, "smoke-window.png")); } catch (Exception capture) { result["captureError"] = capture.Message; }
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "smoke-result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        await FinishSmoke(passed);
    }
    // 페이즈별 모델 칸을 붙박이 도구 값으로 짓는다. 실제 사용자의 ~/.config나
    // ~/.ouroboros는 읽지도 쓰지도 않는다. 네 페이즈 줄과 네 도구 묶음이 있어야 하고,
    // 매인 손잡이들이 다른 실행 줄은 혼합으로 보여야 한다.
    private bool RunPhaseModelsSectionSmoke()
    {
        var panel = BuildPhaseModelsSection(new PhaseModelsSnapshot { ClaudeMain = "fixture-main" }, PhaseModelSection.SmokeFixtureTools);
        var phaseRows = panel.Children.OfType<ComboBox>()
            .Where(box => AutomationProperties.GetAutomationId(box).StartsWith(PhaseRowIdPrefix, StringComparison.Ordinal))
            .ToList();
        Require(phaseRows.Count >= 4, "페이즈별 모델 칸에 페이즈 줄 넷이 없습니다: " + phaseRows.Count);
        foreach (var phase in PhaseModelSection.Phases)
            Require(phaseRows.Any(box => AutomationProperties.GetAutomationId(box) == PhaseRowIdPrefix + phase), "페이즈 줄이 없습니다: " + phase);
        var headings = panel.Children.OfType<TextBlock>().Select(text => text.Text).ToList();
        foreach (var tool in new[] { PhaseModelSection.ClaudeTool, PhaseModelSection.CodexTool, PhaseModelSection.OmcTool, PhaseModelSection.OuroborosTool })
            Require(headings.Contains(PhaseModelSection.ToolLabel(tool)), "도구 묶음이 없습니다: " + tool);
        var execution = phaseRows.Single(box => AutomationProperties.GetAutomationId(box) == PhaseRowIdPrefix + Phase.Execution);
        Require(execution.SelectedItem is ComboBoxItem { Tag: string tag } && tag == PhaseModelSection.MixedSentinel, "값이 다른 실행 줄이 혼합으로 보이지 않습니다.");
        return true;
    }

    // 구성 요소 칸을 임시 폴더의 toolkit.json으로 짓는다. 픽스처에는 plugin·npm·winget
    // (이 PC 항목)과 brew·repoScript(macOS 전용 항목)가 하나씩 있다. 목록에는 이 PC
    // 항목만 보이고, 설치 계획은 가짜 실행기로만 돌리며(실제 프로세스 없음), 저장이
    // 일어난 뒤에도 macOS 전용 두 객체가 파일에 그대로 남아야 한다. 실제 사용자
    // 파일은 읽지도 쓰지도 않는다.
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
            // 승인은 파일을 다시 쓴다 — 이 저장 뒤에도 macOS 전용 객체가 남아야 한다.
            foreach (var id in ComponentsSmokeVisible) store.Approve(id);

            var panel = BuildComponentsSection();
            const string rowPrefix = "toolkit-entry-";
            var visibleIds = toolkitListPanel!.Children.OfType<FrameworkElement>()
                .Select(AutomationProperties.GetAutomationId)
                .Where(id => id.StartsWith(rowPrefix, StringComparison.Ordinal))
                .Select(id => id[rowPrefix.Length..])
                .ToList();
            result["toolkitVisibleIds"] = visibleIds;
            foreach (var id in ComponentsSmokeVisible)
                Require(visibleIds.Contains(id), "구성 요소 칸에 이 PC 항목이 보이지 않습니다: " + id);
            foreach (var id in ComponentsSmokeOtherOs)
                Require(!visibleIds.Contains(id), "구성 요소 칸에 macOS 전용 항목이 보입니다: " + id);
            Require(panel.Children.OfType<FrameworkElement>().Any(), "구성 요소 칸이 비어 있습니다.");

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

            // 설치 결과 표까지 채운 칸의 글자가 로케일 키 누수 검사에 들어간다.
            CollectVisibleStrings(panel, leakStrings);

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

    // 설치 명령을 기록만 하는 가짜 실행기. 프로세스를 띄우지 않는다. winget 명령은
    // 임시 LocalAppData의 WinGet\Links에 실행 파일을 만들어, 다시 조사하는 단계가
    // 파일만 보고 설치됨을 판정하는지 확인하게 한다.
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

    private async Task<Dictionary<string, object?>> RunRenameSmoke()
    {
        var checks = new Dictionary<string, object?>();
        var fixtureSession = service.Snapshot.Sessions[0];
        var originalTitle = fixtureSession.Title;
        try
        {
            var currentPrefilled = false;
            var emptyDisablesSave = false;
            var tooLongDisablesSave = false;
            var tooLongShowsCaption = false;
            var lineBreakNeverReachesName = false;
            var lineBreakTracksCore = false;

            smokeAskName = async (dialog, field, errors) =>
            {
                currentPrefilled = field.Text == originalTitle;
                // Empty name: save must be disabled, no caption (macOS behaviour)
                field.Text = "";
                await Task.Delay(20);
                emptyDisablesSave = !dialog.IsPrimaryButtonEnabled;
                // 121-char name: save disabled, length caption shown
                field.Text = new string('가', 121);
                await Task.Delay(20);
                tooLongDisablesSave = !dialog.IsPrimaryButtonEnabled;
                tooLongShowsCaption = errors.Children.OfType<TextBlock>().Any(t => t.Text == RenameStrings.ErrorTooLong);
                // Line break: a single-line WinUI TextBox drops \r and \n before the Text property
                // changes, so the break never reaches the name and the 줄바꿈 caption cannot appear
                // from the field. Prove both halves: the break never survives, and 저장 and the caption
                // still agree with RenameSupport for whatever the control kept. The caption and the
                // control-character rule themselves stay proven by the `rename …` checks in Core.Tests.
                field.Text = "hello\rworld";
                await Task.Delay(20);
                lineBreakNeverReachesName = !field.Text.Contains('\r') && !field.Text.Contains('\n');
                lineBreakTracksCore = dialog.IsPrimaryButtonEnabled == RenameSupport.IsValid(field.Text)
                    && errors.Children.OfType<TextBlock>().Any(t => t.Text == RenameStrings.ErrorControlCharacter)
                        == RenameSupport.Messages(field.Text).Contains(RenameStrings.ErrorControlCharacter);
                // Save a valid Korean name
                field.Text = "변경된 이름";
                await Task.Delay(20);
                return ContentDialogResult.Primary;
            };
            await RenameSession(fixtureSession.Id);

            Require(currentPrefilled, "이름 변경 대화창에 현재 이름이 미리 채워지지 않았습니다.");
            Require(emptyDisablesSave, "빈 이름에서 저장 버튼이 비활성화되지 않았습니다.");
            Require(tooLongDisablesSave, "121자 이름에서 저장 버튼이 비활성화되지 않았습니다.");
            Require(tooLongShowsCaption, "121자 이름에서 길이 초과 메시지가 표시되지 않았습니다.");
            Require(lineBreakNeverReachesName, "줄바꿈이 이름 입력란에 그대로 남았습니다.");
            Require(lineBreakTracksCore, "줄바꿈 입력 뒤 저장 버튼·문구가 Core 규칙과 어긋납니다.");
            checks["validationRulesMatchMacOS"] = true;
            checks["lineBreakStrippedByTextBox"] = true;

            // Verify the new name appears in the snapshot, tab indicator and sidebar
            await WaitUI(() => service.Snapshot.Sessions.First(s => s.Id == fixtureSession.Id).Title == "변경된 이름");
            Require(tabIndicators.ContainsKey(fixtureSession.Id), "이름 저장 후 탭 지시자가 없습니다.");
            var sidebarTitleFound = sessionLinks.Children.OfType<Button>()
                .Select(b => b.Content).OfType<Grid>()
                .Where(g => g.Children.Count > 1)
                .Select(g => g.Children[1]).OfType<TextBlock>()
                .Any(t => t.Text == "변경된 이름");
            Require(sidebarTitleFound, "사이드바에 변경된 이름이 표시되지 않았습니다.");
            checks["nameInTabAndSidebar"] = true;

            // Cancel a second rename — name must not change
            smokeAskName = (_, _, _) => Task.FromResult(ContentDialogResult.None);
            await RenameSession(fixtureSession.Id);
            Require(service.Snapshot.Sessions.First(s => s.Id == fixtureSession.Id).Title == "변경된 이름", "취소 후 이름이 바뀌었습니다.");
            checks["cancelPreservesName"] = true;

            checks["passed"] = true;
        }
        finally
        {
            smokeAskName = null;
            await service.RenameSessionAsync(fixtureSession.Id, originalTitle);
            Render();
        }
        return checks;
    }

    /// Drives the 리셋권 smoke through the shared Core entry point: an injected
    /// fixture clock and a fake transport (GET only), so the rows render from
    /// fixture data irrespective of the direct-lookup switch. Asserts
    /// fakeTransportPostCount is 0, the available and unknown states rendered,
    /// and the real ClaudeDirectUsageLookupEnabled setting is unchanged.
    private async Task<Dictionary<string, object?>> RunUsageResetSmoke()
    {
        var checks = new Dictionary<string, object?>();
        var beforeSwitch = service.Snapshot.ClaudeDirectUsageLookupEnabled;
        var result = await ClaudeResetSmoke.RunAsync();
        var fakeTransportPostCount = result.FakeTransportPostCount;
        Require(fakeTransportPostCount == 0, "usageReset smoke: no POST exists in this app");
        Require(result.CedarEmberState == ResetState.Available, "usageReset smoke: cedar_ember must be available from fixture");
        Require(result.JuniperTideState == ResetState.Unknown, "usageReset smoke: juniper_tide must be unknown when the handler returns 503");
        Require(result.Requests.SequenceEqual(ClaudeResetSmoke.ExpectedRequests), "usageReset smoke: GET allow-list with skip_spend=1");
        // Real controls: the rows and the always-enabled link are drawn with
        // the window's own builder into a detached panel, so nothing on screen
        // and no stored setting is changed and there is nothing to put back.
        var panel = new StackPanel { Spacing = 4 };
        RenderAccountUsageReset(panel, result.Rows);
        Require(panel.Children.OfType<StackPanel>().Count() == 2, "usageReset smoke: a row per programme renders in the real panel");
        var link = panel.Children.OfType<HyperlinkButton>().FirstOrDefault();
        Require(link is { IsEnabled: true }, "usageReset smoke: the claude.ai 리셋 link is enabled in every state");
        var preferenceUnchanged = service.Snapshot.ClaudeDirectUsageLookupEnabled == beforeSwitch;
        Require(preferenceUnchanged, "usageReset smoke: real direct-lookup preference is unchanged");
        checks["usageReset.cedar_ember"] = result.CedarEmberState;
        checks["usageReset.juniper_tide"] = result.JuniperTideState;
        checks["usageReset.requests"] = result.Requests;
        checks["fakeTransportPostCount"] = fakeTransportPostCount;
        checks["preferenceUnchanged"] = preferenceUnchanged;
        checks["passed"] = result.Passed;
        return checks;
    }

    // Drives both the real CLI update section and the real status line refresher with fake
    // runners: records both under key liveWiring and restores everything it changed.
    private async Task<Dictionary<string, object?>> RunLiveWiringSmoke()
    {
        var checks = new Dictionary<string, object?>();
        var previousResults = lastCliUpdateResults;
        smokeCliUpdater = (provider, _) => Task.FromResult(
            new CliUpdateResult(provider, "updated", "1.0.0", "2.0.0", "native", CliUpdateStrings.DetailUpdated));
        try
        {
            // The section must expose the button before we press it.
            var section = BuildCliUpdateSection([]);
            var button = section.Children.OfType<Button>()
                .FirstOrDefault(b => AutomationProperties.GetAutomationId(b) == "cli-update-start");
            Require(button is not null, "업데이트 하기 button must be present in the CLI update section");
            Require((string?)button!.Content == CliUpdateStrings.UpdateButton, "button must read 업데이트 하기 when idle");
            Require(button.IsEnabled, "button must be enabled when idle");
            checks["buttonPresentAndEnabled"] = true;

            // Press the button (starts the coordinator with the fake updater).
            coordinator.Start();
            Require(coordinator.IsUpdating, "coordinator must report isUpdating after Start");
            checks["isUpdatingAfterStart"] = true;

            // Wait for the run to complete and check results.
            await WaitUI(() => !coordinator.IsUpdating);
            Require(coordinator.Results.Count == Wire.Providers.Length,
                "coordinator must have one result per provider after the run");
            Require(coordinator.Results.All(r => r.Status == "updated"),
                "all fake results must be updated");
            Require(coordinator.FinishedAt is not null, "finishedAt must be set after the run");
            checks["resultsAppearAfterRun"] = true;
        }
        finally
        {
            smokeCliUpdater = null;
            lastCliUpdateResults = previousResults;
        }

        // Status line refresher live wiring: drive the real pane's refresher with a fake
        // discovery and runner, wait until the result appears in the rendered status line,
        // then restore the status line to its pre-smoke state.
        var sessions = service.Snapshot.Sessions;
        var statusPane = views[sessions[0].Id];
        var fakeConfig = new MightyClaude.Core.StatusLineConfig("echo smoke", 0, "사용자 설정", false);
        smokeStatusLineDiscovery = () => new MightyClaude.Core.StatusLineDiscovery(null, fakeConfig);
        smokeStatusLineRunner = (_, _, _) => Task.FromResult(
            new MightyClaude.Core.StatusLineResult(
                [MightyClaude.Core.AnsiText.Parse("\u001b[36msmoke\u001b[0m ok")], null, 0, false));
        try
        {
            // Trigger through the real refresher that the real pane owns.
            statusPane.RequestStatusLineRefresh(force: true);
            await WaitUI(() => statusPane.Refresher?.Result is not null);
            Require(statusPane.Refresher!.Result!.Lines.Count > 0,
                "status line refresher ran and produced output in the real pane");
            checks["statusLineRefresherFiredAndRendered"] = true;
        }
        finally
        {
            smokeStatusLineDiscovery = null;
            smokeStatusLineRunner = null;
            // Restore the status line to the empty/hidden state it had before.
            statusPane.Refresher?.Close();
            statusPane.RenderStatusLine(null, null, null);
        }
        checks["passed"] = true;
        return checks;
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

    // ── mighty graph smoke ────────────────────────────────────────────────────

    /// Drives the real Mighty view: the pane's GraphRuns are produced by feeding
    /// claudeStream cases from native/contracts/graph-vectors.json through the
    /// stage 4a ExecutionGraphTracker, the pane is switched to 마이티, the canvas
    /// is rendered, and the drawn blocks, edges, kinds, result files and zoom
    /// range are reported. Switching back to 기본 must leave the draft alone.
    private async Task<Dictionary<string, object?>> RunMightyGraphSmoke(PaneView pane, Workspace workspace, List<string> leakStrings)
    {
        var runs = MightyGraphVectorRuns();
        Require(runs.Count >= 3, "mighty 스모크: 벡터에서 만든 실행이 너무 적습니다: " + runs.Count);

        // A real file inside the smoke workspace so the 결과에 나온 파일 panel has
        // something to list; nothing outside the workspace is ever resolved.
        var notesDirectory = Path.Combine(workspace.Path, "docs");
        Directory.CreateDirectory(notesDirectory);
        var notePath = Path.Combine(notesDirectory, "mighty-note.md");
        await File.WriteAllTextAsync(notePath, "# mighty\n");
        var last = runs[^1];
        last.FinalOutput = "정리한 내용은 docs/mighty-note.md 에 적었습니다.";
        MightyGraphSupport.RefreshResult(last);

        var draftBefore = pane.SessionForSmoke.Draft;
        await pane.SetGraphRunsForSmoke(runs);
        await pane.SetAgentViewMode("mighty");
        root.UpdateLayout(); await Task.Delay(60);
        Require(pane.SessionForSmoke.AgentViewMode == "mighty", "마이티 모드가 저장되지 않았습니다.");
        Require(pane.SessionForSmoke.Draft == draftBefore, "모드 전환이 입력창 초안을 지웠습니다.");

        var reading = pane.ReadGraphForSmoke();
        Require(reading.Blocks >= 3, "mighty 스모크: 블록이 3개 미만입니다: " + reading.Blocks);
        Require(reading.Edges >= 2, "mighty 스모크: 엣지가 2개 미만입니다: " + reading.Edges);
        Require(reading.Kinds.Contains("request") && reading.Kinds.Contains("result"), "mighty 스모크: 요청·결과 블록이 없습니다.");

        // Zoom: 50% at the bottom, 100% on reset, 150% at the top, disabled at the ends.
        pane.SetGraphZoom(MightyGraphViewModel.ZoomMin);
        Require(MightyGraphViewModel.ZoomOutDisabled(pane.GraphZoom), "최소 배율에서 축소 단추가 잠기지 않았습니다.");
        var minimum = (int)Math.Round(pane.GraphZoom * 100);
        pane.SetGraphZoom(MightyGraphViewModel.ZoomDefault);
        var reset = (int)Math.Round(pane.GraphZoom * 100);
        pane.SetGraphZoom(MightyGraphViewModel.ZoomMax);
        Require(MightyGraphViewModel.ZoomInDisabled(pane.GraphZoom), "최대 배율에서 확대 단추가 잠기지 않았습니다.");
        var maximum = (int)Math.Round(pane.GraphZoom * 100);
        pane.SetGraphZoom(MightyGraphViewModel.ZoomDefault);

        // Selection routes the wheel into the block; the empty background clears it.
        var first = pane.GraphBlockIds.First();
        pane.SelectGraphBlock(first);
        Require(pane.GraphSelection == first && MightyGraphViewModel.WheelScrollsBlock(pane.GraphSelection), "블록 선택이 휠을 블록으로 보내지 않았습니다.");
        pane.ClearGraphSelection();
        Require(pane.GraphSelection is null && !MightyGraphViewModel.WheelScrollsBlock(pane.GraphSelection), "빈 배경 클릭이 선택을 지우지 않았습니다.");

        // Windows animations off: a running block must take the static indicator
        // and a waiting block the pause mark. Core owns the choice.
        PaneView.AnimationsEnabledOverride = false;
        pane.RefreshMightyView(pane.SessionForSmoke); root.UpdateLayout(); await Task.Delay(30);
        Require(MightyGraphViewModel.BlockIndicator("running", false) == "static"
            && MightyGraphViewModel.BlockIndicator("running", true) == "animating"
            && MightyGraphViewModel.BlockIndicator("waiting", false) == "waiting",
            "애니메이션이 꺼졌을 때의 표시가 macOS와 다릅니다.");
        PaneView.AnimationsEnabledOverride = null;
        pane.RefreshMightyView(pane.SessionForSmoke); root.UpdateLayout(); await Task.Delay(30);

        // The canvas text joins the locale-key leak scan.
        CollectVisibleStrings(pane.GraphCanvas, leakStrings);
        leakStrings.Add(pane.GraphTotalText);
        leakStrings.AddRange(pane.GraphHeaderTextsForSmoke());

        await pane.SetAgentViewMode("default");
        root.UpdateLayout(); await Task.Delay(30);
        var modeRestored = pane.SessionForSmoke.AgentViewMode == "default" && pane.SessionForSmoke.Draft == draftBefore;
        Require(modeRestored, "기본으로 되돌린 뒤 모드나 초안이 어긋났습니다.");

        return new Dictionary<string, object?>
        {
            ["blocks"] = reading.Blocks,
            ["edges"] = reading.Edges,
            ["kinds"] = reading.Kinds,
            ["resultFiles"] = reading.ResultFiles,
            ["zoom"] = new[] { minimum, reset, maximum },
            ["modeRestored"] = modeRestored,
        };
    }

    /// Every claudeStream case of the committed vector file, replayed through the
    /// stage 4a tracker. No graph is hand-built here — the tracker produces them.
    private static List<MightyGraphRun> MightyGraphVectorRuns()
    {
        using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("MightyClaude.WinUI.GraphVectors.json")
            ?? throw new InvalidOperationException("graph-vectors.json이 WinUI 어셈블리에 포함되지 않았습니다.");
        using var document = JsonDocument.Parse(stream);
        var runs = new List<MightyGraphRun>();
        foreach (var value in document.RootElement.GetProperty("claudeStream").EnumerateArray())
        {
            var tracker = new ExecutionGraphTracker(
                value.GetProperty("runId").GetString()!,
                value.TryGetProperty("input", out var input) ? input.GetString() : null,
                value.TryGetProperty("provider", out var provider) ? provider.GetString()! : "claude",
                null, _ => { });
            foreach (var step in value.GetProperty("steps").EnumerateArray())
                switch (step.GetProperty("kind").GetString())
                {
                    case "frame": tracker.Consume(step.GetProperty("value")); break;
                    case "steer": tracker.Steer(step.TryGetProperty("id", out var steerId) ? steerId.GetString() ?? "" : "", step.TryGetProperty("text", out var steerText) ? steerText.GetString() ?? "" : ""); break;
                    case "finish": tracker.Finish(step.TryGetProperty("state", out var state) ? state.GetString() ?? "completed" : "completed"); break;
                }
            if (tracker.BuildRun() is { } run) runs.Add(run);
        }
        return runs;
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
    private static async Task WaitUI(Func<bool> predicate, [CallerArgumentExpression(nameof(predicate))] string condition = "")
    {
        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (!predicate()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException("WinUI 검증 상태 대기 시간이 초과되었습니다: " + condition); await Task.Delay(20); }
    }

    private sealed partial class PaneView
    {
        internal async Task<Dictionary<string, object?>> RunComposerSmoke()
        {
            var checks = new Dictionary<string, object?>();
            Require(SubmitKeyAllowed(false, false, false, false) && !SubmitKeyAllowed(true, false, false, false) && !SubmitKeyAllowed(false, true, false, false) && !SubmitKeyAllowed(false, false, true, false), "Enter/Shift+Enter/IME 키 정책이 잘못됐습니다.");
            input.Text = "한글 첫 입력"; await WaitUI(() => Session.Draft == input.Text);
            composingInput = true; var nativeInput = input; Refresh(); Require(ReferenceEquals(input, nativeInput) && input.Text == "한글 첫 입력", "상태 갱신이 조합 중인 입력 컨트롤을 변경했습니다."); composingInput = false;
            input.Text = ""; Container.UpdateLayout(); await Task.Delay(40); var singleHeight = input.ActualHeight;
            input.Text = "첫째 줄\n둘째 줄\n셋째 줄\n" + string.Concat(Enumerable.Repeat("자동 줄바꿈 ", 30)); Container.UpdateLayout();
            await WaitUI(() => input.ActualHeight > singleHeight && input.ActualHeight <= 141);
            input.Text = ""; Container.UpdateLayout(); await WaitUI(() => input.ActualHeight <= singleHeight + 1);
            checks["nativeEditorRetainedAndAutoHeight"] = true;
            var doc = output.View.Document; doc.GetText(TextGetOptions.None, out var text);
            Require(output.View.IsReadOnly, "출력 갱신 후 읽기 전용 상태가 복원되지 않았습니다.");
            var start = text.IndexOf("첫 번째", StringComparison.Ordinal); var end = text.IndexOf("두 번째", StringComparison.Ordinal) + "두 번째 문단".Length;
            Require(start >= 0 && end > start && !text.Contains("**문단**", StringComparison.Ordinal) && text.Contains("12.3초", StringComparison.Ordinal), "Markdown 또는 도구 경과시간이 표시되지 않았습니다.");
            doc.Selection.SetRange(start, end); doc.Selection.GetText(TextGetOptions.None, out var selected);
            await Change(p => p with { Logs = p.Logs.Append(new LogEntry(Wire.Id(), "assistant", "추가 응답", Wire.Now(), p.Provider)).ToList() }); Refresh();
            doc.Selection.GetText(TextGetOptions.None, out var retained); Require(selected == retained, "새 출력이 여러 문단의 선택 범위를 바꿨습니다."); Require(output.View.IsReadOnly, "추가 출력 후 읽기 전용 상태가 복원되지 않았습니다."); checks["crossParagraphSelectionSurvivesAppend"] = true;
            Container.Width = 315; Container.UpdateLayout(); await WaitUI(() => Math.Abs(Container.ActualWidth - 315) < 1); ArrangeComposer(); Container.UpdateLayout(); await Task.Delay(40);
            var controls = selectors.Children.OfType<FrameworkElement>().Where(c => c.Visibility == Visibility.Visible).Concat([context, send]).ToArray();
            var centers = controls.Select(c => c.TransformToVisual(Container).TransformPoint(new(0, 0)).Y + c.ActualHeight / 2).ToArray();
            Require(controls.All(c => Math.Abs(c.ActualHeight - 32) < 1 && c.TransformToVisual(Container).TransformPoint(new(c.ActualWidth, 0)).X <= Container.ActualWidth + 1) && centers.Max() - centers.Min() < 1, "좁은 입력창의 버튼이 한 줄 안에 맞지 않습니다.");
            checks["compactControls32pxSingleRow"] = true; Container.Width = double.NaN; Container.UpdateLayout();
            var oldFile = AttachmentSupport.Make("old.txt", "old"u8.ToArray()); var newFile = AttachmentSupport.Make("next.txt", "next"u8.ToArray()); pendingAttachments.Add(oldFile); RefreshAttachments();
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0; StartRunRequest? submitted = null;
            owner.smokeStart = async request => { calls++; submitted = request; await Change(p => p with { Status = "running" }); await gate.Task; await Change(p => p with { Status = "completed" }); };
            input.Text = "전송할 요청"; await WaitUI(() => Session.Draft == input.Text); RefreshComposerState();
            var sending = Send(); await WaitUI(() => calls == 1 && Session.Status == "running");
            Require((string?)send.Content == "■" && send.IsEnabled && !canSend, "실행 중 단일 버튼이 중지로 바뀌지 않았습니다.");
            await Send(); Require(calls == 1, "중복 입력이 같은 요청을 다시 시작했습니다.");
            input.Text = "다음 요청 초안"; pendingAttachments.Add(newFile); RefreshAttachments();
            gate.SetResult(); await sending;
            Require(submitted?.Input == "전송할 요청" && submitted.Attachments?.Single().Id == oldFile.Id && input.Text == "다음 요청 초안" && pendingAttachments.Count == 1 && pendingAttachments[0].Id == newFile.Id, "전송 완료가 새 초안이나 첨부를 지웠습니다.");
            checks["singleActionBusyDedupAndNewDraftPreserved"] = true; checks["passed"] = true; owner.smokeStart = null; return checks;
        }

        /// <summary>
        /// Drives the real palette: types a slash, waits for the list, counts its
        /// rows against a fixture injected for smoke mode, moves the highlight,
        /// chooses an entry and reads the draft back. No CLI is ever started, and
        /// the draft and focus this check found are put back before it returns.
        /// </summary>
        internal async Task<Dictionary<string, object?>> RunSlashCommandPaletteSmoke()
        {
            var checks = new Dictionary<string, object?>();
            // Fixture commands standing in for a real disk scan.
            var fixture = new SlashCommand[]
            {
                new("review", "코드 검토", SlashCommandStrings.ProjectSkillSource, SlashCommandOrigin.Project),
                new("deploy", "", SlashCommandStrings.UserCommandSource, SlashCommandOrigin.User),
            };
            var previousDraft = input.Text;
            var previousFocus = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(owner.root.XamlRoot) as Control;
            owner.smokeSlashCommands = fixture;
            try
            {
                var expected = SlashPalette.Builtins(Session.Provider).Length + fixture.Length;
                input.Text = "/";
                await WaitUI(() => paletteState.IsOpen && slashPaletteHost.Visibility == Visibility.Visible);
                Require(paletteState.Commands.Length == expected && slashRows.Children.Count == expected,
                    "슬래시 팔레트의 줄 수가 주입한 목록과 다릅니다.");
                Require(slashCount.Text == SlashPalette.CountLabel(expected), "슬래시 팔레트의 개수 표시가 잘못됐습니다.");
                Require(Session.Provider != "claude" || paletteState.Commands.Any(c => c.Action == SlashCommandAction.OpenPlugins && c.Invocation == "plugin"),
                    "Claude 팔레트에 /plugin이 없습니다.");
                Require(Session.Provider == "claude" || !paletteState.Commands.Any(c => c.Action == SlashCommandAction.OpenPlugins),
                    "Windows에 화면이 없는 앱 명령이 팔레트에 나왔습니다.");
                checks["opensAboveComposerWithFixtureRows"] = true;

                input.Text = "/rev";
                await WaitUI(() => paletteState.Commands.Length == 1);
                Require(paletteState.Commands[0].Invocation == "review", "입력에 따른 슬래시 팔레트 필터가 잘못됐습니다.");
                checks["typingFilters"] = true;

                input.Text = "/";
                await WaitUI(() => paletteState.Commands.Length == expected);
                Require(HandlePaletteKey(Windows.System.VirtualKey.Down) && paletteState.SafeIndex == 1, "↓ 키가 다음 줄로 이동하지 않았습니다.");
                Require(HandlePaletteKey(Windows.System.VirtualKey.Up) && paletteState.SafeIndex == 0, "↑ 키가 이전 줄로 이동하지 않았습니다.");
                Require(HandlePaletteKey(Windows.System.VirtualKey.Up) && paletteState.SafeIndex == expected - 1, "↑ 키가 마지막 줄로 넘어가지 않았습니다.");
                checks["arrowsMoveHighlight"] = true;

                input.Text = "/rev";
                await WaitUI(() => paletteState.Commands.Length == 1);
                Require(HandlePaletteKey(Windows.System.VirtualKey.Enter), "Enter가 팔레트 대신 입력창으로 갔습니다.");
                await WaitUI(() => input.Text == "/review " && Session.Draft == "/review ");
                Require(!paletteState.IsOpen && slashPaletteHost.Visibility == Visibility.Collapsed, "명령 선택 후 슬래시 팔레트가 닫히지 않았습니다.");
                checks["choosingInsertsInvocationAndSpace"] = true;

                input.Text = "/model ";
                await WaitUI(() => paletteState.IsOpen);
                Require(paletteState.Commands.All(c => c.Action == SlashCommandAction.SetModel),
                    "/model 뒤에서 모델 선택이 이어지지 않았습니다.");
                checks["argumentCompletionContinues"] = true;

                Require(HandlePaletteKey(Windows.System.VirtualKey.Escape) && !paletteState.IsOpen && input.Text == "/model ",
                    "Esc가 팔레트를 닫지 못했거나 초안을 바꿨습니다.");
                Require(!HandlePaletteKey(Windows.System.VirtualKey.Down), "닫힌 팔레트가 방향키를 가로챘습니다.");
                checks["escapeClosesWithoutChangingDraft"] = true;
                checks["passed"] = true;
            }
            finally
            {
                // Smoke checks share one pane: put back the draft and the focus
                // this check found, and forget that Esc ever closed the list.
                owner.smokeSlashCommands = null; slashDismissedFor = null;
                updating = true; input.Text = previousDraft; updating = false;
                await Change(p => p with { Draft = previousDraft });
                RefreshPalette(previousDraft); RefreshComposerState();
                previousFocus?.Focus(FocusState.Programmatic);
            }
            return checks;
        }
    }
}
