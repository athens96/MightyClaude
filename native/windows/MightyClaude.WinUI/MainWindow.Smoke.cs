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
    private Task StartFromComposer(StartRunRequest request) =>
        AutomaticLoginOf(request.Provider) is { } login ? StartAfterAutomaticLogin(request, login.Task) : StartAdmitted(request);
    /// <summary>
    /// A send while the provider's automatic sign-in runs cancels it (holding the next automatic start for the
    /// cooldown) and goes ahead once its process is gone. The checks and the start below stay one synchronous step.
    /// </summary>
    private async Task StartAfterAutomaticLogin(StartRunRequest request, Task login)
    {
        CancelBackgroundLogin(request.Provider); RefreshLoginCards();
        try { await login.WaitAsync(TimeSpan.FromSeconds(10)); } catch (TimeoutException) { }
        await StartAdmitted(request);
    }
    private Task StartAdmitted(StartRunRequest request)
    {
        if (loginJobs.ContainsKey(request.Provider)) throw new InvalidOperationException(Locale.Get("loginRecovery.busySignIn", new Dictionary<string, string> { ["provider"] = ProviderCatalog.Name(request.Provider) }));
        if (loginBusy.ContainsKey(request.Provider) || accountChanges.ContainsKey(request.Provider)) throw new InvalidOperationException(Locale.Get("loginRecovery.busy"));
        if (automaticUpdateRunning && automaticallyUpdatingProvider == request.Provider) throw new InvalidOperationException(Locale.Get("loginRecovery.updating"));
        if (ManualMutationBlockReason(new RunSession { Kind = request.Kind, Provider = request.Provider }) is { } block) throw new InvalidOperationException(block);
        return options.SmokeTest ? smokeStart?.Invoke(request) ?? throw new InvalidOperationException("스모크 모드에서는 실제 CLI를 실행하지 않습니다.") : service.StartAsync(request);
    }

    private async Task RunUISmoke()
    {
        var result = new Dictionary<string, object?> { ["passed"] = false, ["aiRequestSent"] = false, ["clipboardUsed"] = false, ["physicalIMEAndPointerTested"] = false };
        var directory = options.ProfileDirectory ?? throw new InvalidOperationException("스모크 프로필이 없습니다.");
        var passed = false;
        void Checkpoint(string name, string state)
        {
            result["phase"] = name; result["stepState"] = state;
            options.TraceStartup("smoke:" + name + ":" + state);
            File.WriteAllText(Path.Combine(directory, "smoke-result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        }
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
            Checkpoint("composerAndTranscript", "running");
            result["composerAndTranscript"] = await pane.RunComposerSmoke();
            Checkpoint("composerAndTranscript", "passed");
            Checkpoint("styles", "running");
            result["styles"] = await pane.RunStylesSmoke();
            Checkpoint("styles", "passed");
            Checkpoint("nextActions", "running");
            result["nextActions"] = await pane.RunNextActionsSmoke();
            Checkpoint("nextActions", "passed");
            Checkpoint("smallParity", "running");
            result["smallParity"] = await RunSmallParitySmoke(workspace);
            Checkpoint("smallParity", "passed");
            Checkpoint("automaticUpdates", "running");
            result["automaticUpdates"] = await RunAutomaticUpdatesSmoke(workspace);
            Checkpoint("automaticUpdates", "passed");
            Checkpoint("companion", "running");
            result["companion"] = await RunCompanionSmoke();
            Checkpoint("companion", "passed");
            Checkpoint("screenCapture", "running");
            result["screenCapture"] = await WindowsScreenCapture.SmokeAsync();
            Checkpoint("screenCapture", "passed");
            Checkpoint("screenTransport", "running");
            result["screenTransport"] = await WindowsScreenTransportSmoke.RunAsync(root, directory);
            Checkpoint("screenTransport", "passed");
            Require(ScreenShareTapMarkerOverlay.SmokeNoFocus(), "Screen tap marker must preserve focus and pass through input.");
            result["screenTapMarkerNoFocus"] = true;
            Checkpoint("nativeTerminal", "running");
            result["nativeTerminal"] = await RunTerminalSmoke();
            Checkpoint("nativeTerminal", "passed");
            Checkpoint("workspaceSidebar", "running");
            result["workspaceSidebar"] = await RunSidebarSmoke();
            Checkpoint("workspaceSidebar", "passed");
            Checkpoint("sidebarToggle", "running");
            result["sidebarToggle"] = await RunSidebarToggleSmoke();
            Checkpoint("sidebarToggle", "passed");
            Checkpoint("desktopSurfaces", "running");
            result["desktopSurfaces"] = await RunDesktopSurfaceSmoke();
            Checkpoint("desktopSurfaces", "passed");
            Checkpoint("slashCommandPalette", "running");
            result["slashCommandPalette"] = await pane.RunSlashCommandPaletteSmoke();
            Checkpoint("slashCommandPalette", "passed");
            Checkpoint("statusLine", "running");
            result["statusLine"] = await pane.RunStatusLineSmoke();
            Checkpoint("statusLine", "passed");
            result["modelLabel"] = pane.RunModelLabelSmoke();
            Checkpoint("toolPermission", "running");
            result["toolPermission"] = await pane.RunToolPermissionSmoke();
            Checkpoint("toolPermission", "passed");
            Checkpoint("transcriptActions", "running");
            result["transcriptActions"] = await pane.Transcript.RunActionsSmoke();
            Checkpoint("transcriptActions", "passed");
            // The Default conversation at the window's width, as the Mac's 02-pane-basic shows it.
            root.UpdateLayout(); await Task.Delay(200);
            result["transcriptScreenshot"] = await CaptureSmoke(Path.Combine(directory, "smoke-transcript-wide.png"));
            Checkpoint(CompletionNotificationSmokeOutcome.ResultKey, "running");
            result[CompletionNotificationSmokeOutcome.ResultKey] = await RunCompletionNotificationSmoke();
            Checkpoint(CompletionNotificationSmokeOutcome.ResultKey, "passed");
            Checkpoint(SettingsSectionsSmokeOutcome.ResultKey, "running");
            result[SettingsSectionsSmokeOutcome.ResultKey] = await RunSettingsSectionsSmoke();
            Checkpoint(SettingsSectionsSmokeOutcome.ResultKey, "passed");
            result["phaseModelsSection"] = RunPhaseModelsSectionSmoke();
            var componentsLeakStrings = new List<string>();
            result["componentsSection"] = RunComponentsSectionSmoke(result, componentsLeakStrings);
            Checkpoint(AccountUsageSmokeOutcome.ResultKey, "running");
            result[AccountUsageSmokeOutcome.ResultKey] = await RunAccountUsageSmoke();
            Checkpoint(AccountUsageSmokeOutcome.ResultKey, "passed");
            Checkpoint("usageReset", "running");
            result["usageReset"] = await RunUsageResetSmoke();
            Checkpoint("usageReset", "passed");
            Checkpoint(AppUpdateSmokeOutcome.ResultKey, "running");
            result[AppUpdateSmokeOutcome.ResultKey] = await RunAppUpdateSectionSmoke();
            Checkpoint(AppUpdateSmokeOutcome.ResultKey, "passed");
            Checkpoint("liveWiring", "running");
            result["liveWiring"] = await RunLiveWiringSmoke();
            Checkpoint("liveWiring", "passed");
            Checkpoint("rename", "running");
            result["rename"] = await RunRenameSmoke();
            Checkpoint("rename", "passed");
            Checkpoint("statusGlyph", "running");
            result["statusGlyph"] = await RunStatusGlyphSmoke();
            Checkpoint("statusGlyph", "passed");
            result["agentMark"] = RunAgentMarkSmoke();
            Checkpoint("agentImages", "running");
            result["agentImages"] = await RunAgentImagesSmoke(workspace);
            Checkpoint("agentImages", "passed");
            result["betaBadge"] = RunBetaBadgeSmoke();
            Checkpoint(ClaudePluginSmokeOutcome.ResultKey, "running");
            result[ClaudePluginSmokeOutcome.ResultKey] = await RunClaudePluginSmoke();
            Checkpoint(ClaudePluginSmokeOutcome.ResultKey, "passed");
            Checkpoint(CodexPluginSmokeOutcome.ResultKey, "running");
            result[CodexPluginSmokeOutcome.ResultKey] = await RunCodexPluginSmoke();
            Checkpoint(CodexPluginSmokeOutcome.ResultKey, "passed");
            Checkpoint(PluginMarketplaceSmokeOutcome.ResultKey, "running");
            result[PluginMarketplaceSmokeOutcome.ResultKey] = await RunPluginMarketplaceSmoke();
            Checkpoint(PluginMarketplaceSmokeOutcome.ResultKey, "passed");
            var mightyLeakStrings = new List<string>();
            Checkpoint("mightyGraph", "running");
            result["mightyGraph"] = await RunMightyGraphSmoke(pane, workspace, mightyLeakStrings);
            Checkpoint("mightyGraph", "passed");
            Checkpoint("mightyTimeline", "running");
            result["mightyTimeline"] = await pane.RunTimelineSmoke();
            Checkpoint("mightyTimeline", "passed");
            var browserLeakStrings = new List<string>();
            Checkpoint("browserPane", "running");
            result["browserPane"] = await RunBrowserPaneSmoke(workspace, browserLeakStrings);
            Checkpoint("browserPane", "passed");
            var filesLeakStrings = new List<string>();
            Checkpoint("filesPane", "running");
            result["filesPane"] = await RunFilesPaneSmoke(workspace, filesLeakStrings);
            Checkpoint("filesPane", "passed");
            Checkpoint("sessionHistory", "running");
            result["sessionHistory"] = await RunSessionHistorySmoke(workspace, other);
            Checkpoint("sessionHistory", "passed");
            Checkpoint("addPaneMenu", "running");
            result["addPaneMenu"] = await RunAddPaneMenuSmoke(workspace, other);
            Checkpoint("addPaneMenu", "passed");
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
            var accentProbe = AddAccentProbe();
            Func<Task>? restoreMightyDesign = null;
            try
            {
                // 디자인 토큰 1단계: 두 테마 모두 창 배경이 공유 page 브러시이고 픽스처의 hex이며,
                // 토글 전에 받은 브러시가 같은 인스턴스로 새 테마 색이 된다(같은 실행 창 재사용).
                await service.UpdateAsync(s => s with { Theme = "light" }); Render();
                Checkpoint("designTokens", "running");
                RequireDesignTokensInTheme(accentProbe);
                var pageBrush = brushes.Brush(DesignToken.Page); var cardBrush = brushes.Brush(DesignToken.Card);
                // Design stage 2, the app shell: the same check in both themes, on the same reused
                // panes, whose border brushes must be recoloured in place by the toggle.
                Checkpoint(AppShellKey, "running");
                var inactivePane = views.Where(p => p.Key != service.Snapshot.ActiveSessionId).Select(p => p.Value).FirstOrDefault()
                    ?? throw new InvalidOperationException($"{AppShellKey}: needs one inactive pane besides the active smoke pane; views {views.Count}");
                Require(service.Snapshot.ActiveSessionId is { } shellActiveId && views.TryGetValue(shellActiveId, out var shellActiveView) && ReferenceEquals(pane, shellActiveView), $"{AppShellKey}: the smoke pane must be the active pane; active {service.Snapshot.ActiveSessionId ?? "none"}");
                RequireAppShellInTheme(pane, inactivePane);
                var activeBorder = pane.Container.BorderBrush; var inactiveBorder = inactivePane.Container.BorderBrush;
                // Design stage 3, the sidebar: the same check in both themes; its long-lived parts keep
                // their brush instances across the toggle (recoloured in place).
                Checkpoint(SidebarDesignKey, "running");
                await RequireSidebarDesignInTheme();
                var sidebarBrushes = new[] { sidebarSearchBox.Background, dashboardEntryIcon!.Background, sidebarFooter!.BorderBrush, layout.Background };
                // Design stage 4, the tab strip, the pane header and the composer: the same check in both
                // themes on the same reused pane, whose chrome brushes the toggle recolours in place.
                Checkpoint(PaneChromeKey, "running");
                await pane.RequirePaneChromeInTheme();
                var chromeBrushes = pane.PaneChromeBrushes();
                // Design stage 5, the Mighty diagram, the timeline and the result card: the light pass draws
                // the fixture and leaves the timeline on screen; the dark pass right after the toggle reads the
                // same views again, recoloured in place, before the fixture is put away.
                Checkpoint(MightyDesignKey, "running");
                restoreMightyDesign = pane.MightyDesignRestore();
                await pane.BeginMightyDesignSmoke();
                var mightyViews = await pane.RequireMightyDesignInTheme(null);
                root.UpdateLayout(); await Task.Delay(120);
                Checkpoint("lightThemeScreenshot", "running");
                result["lightThemeScreenshot"] = await CaptureSmoke(Path.Combine(directory, "smoke-window-light.png"));
                Checkpoint("lightThemeScreenshot", "passed");
                await service.UpdateAsync(s => s with { Theme = "dark" }); Render();
                Checkpoint("designTokens", "running");
                RequireDesignTokensInTheme(accentProbe);
                RequireDesignBrushesRecolouredInPlace(pageBrush, cardBrush, "light");
                Require(ReferenceEquals(pane, views[sessions[0].Id]), "designTokens: the theme toggle rebuilt the pane instead of reusing it");
                result["designTokens"] = true;
                Checkpoint("designTokens", "passed");
                Checkpoint(MightyDesignKey, "running");
                await pane.RequireMightyDesignInTheme(mightyViews);
                var restoreMighty = restoreMightyDesign; restoreMightyDesign = null; await restoreMighty();
                result[MightyDesignKey] = true;
                Checkpoint(MightyDesignKey, "passed");
                Checkpoint(AppShellKey, "running");
                Require(views.ContainsValue(inactivePane) && ReferenceEquals(pane.Container.BorderBrush, activeBorder) && ReferenceEquals(inactivePane.Container.BorderBrush, inactiveBorder),
                    $"{AppShellKey} (dark): the toggle replaced a pane or its border brush instead of recolouring it in place; active {Describe(pane.Container.BorderBrush)}, inactive {Describe(inactivePane.Container.BorderBrush)}");
                RequireAppShellInTheme(pane, inactivePane);
                result[AppShellKey] = true;
                Checkpoint(AppShellKey, "passed");
                Checkpoint(SidebarDesignKey, "running");
                Require(new[] { sidebarSearchBox.Background, dashboardEntryIcon.Background, sidebarFooter.BorderBrush, layout.Background }.Zip(sidebarBrushes).All(pair => ReferenceEquals(pair.First, pair.Second)),
                    $"{SidebarDesignKey} (dark): the toggle replaced a sidebar brush instead of recolouring it in place");
                await RequireSidebarDesignInTheme();
                result[SidebarDesignKey] = true;
                Checkpoint(SidebarDesignKey, "passed");
                Checkpoint(PaneChromeKey, "running");
                var chromeAfter = pane.PaneChromeBrushes();
                Require(chromeAfter.Length == chromeBrushes.Length && chromeAfter.Zip(chromeBrushes).All(pair => pair.First is not null && ReferenceEquals(pair.First, pair.Second)),
                    $"{PaneChromeKey} (dark): the toggle replaced a pane chrome brush instead of recolouring it in place: {string.Join(", ", chromeAfter.Select(Describe))}");
                await pane.RequirePaneChromeInTheme();
                result[PaneChromeKey] = true;
                Checkpoint(PaneChromeKey, "passed");
                result["opaqueBackgroundInBothThemes"] = true;
            }
            finally
            {
                if (restoreMightyDesign is not null) await restoreMightyDesign();
                root.Children.Remove(accentProbe); await service.UpdateAsync(s => s with { Theme = originalTheme }); Render();
            }
            // Design stage 6: the dashboard, files pane, settings, sheets and popovers, light then dark on the same views.
            Checkpoint(PanelsDesignKey, "running");
            result["panelsDesignChecks"] = await RunPanelsDesignSmoke(workspace);
            result[PanelsDesignKey] = true;
            Checkpoint(PanelsDesignKey, "passed");
            // Design stage 7: the terminal header, the conversation, the empty states, and a walk of every
            // brush in the main and settings windows against the palette, light then dark.
            Checkpoint(PaletteDesignKey, "running");
            result["paletteDesignChecks"] = await RunPaletteDesignSmoke(workspace, sessions);
            result[PaletteDesignKey] = true;
            Checkpoint(PaletteDesignKey, "passed");
            await ApplyLayoutPreset("columns"); root.UpdateLayout(); await Task.Delay(120);
            var leakStrings = new List<string>();
            CollectVisibleStrings(root, leakStrings);
            var settingsSectionsForLeak = GetSettingsSections();
            var settingsPanelForLeak = new StackPanel { Spacing = 0, MinWidth = 420, MaxWidth = 540 };
            foreach (var sec in settingsSectionsForLeak)
                settingsPanelForLeak.Children.Add(BuildSectionContainer(sec.Title, sec.Build()));
            // The boxes a tab draws beside its registered slots (agent links, the toolkit, screen view and control) are scanned as well.
            foreach (var extra in SettingsNavigation.Available.SelectMany(tab => SettingsGroups(tab.Id)).Where(box => settingsSectionsForLeak.All(slot => slot.Title != box.Title)))
                settingsPanelForLeak.Children.Add(BuildSectionContainer(extra.Title, extra.Build(), extra.Beta));
            CollectVisibleStrings(settingsPanelForLeak, leakStrings);
            // 페이즈별 모델 칸의 ComboBox 머리글과 항목은 화면 나무에 바로 보이지 않으므로 따로 넣는다.
            leakStrings.AddRange(PhaseModelSectionTexts(BuildPhaseModelsSection(new(), PhaseModelSection.SmokeFixtureTools)));
            // 구성 요소 칸은 임시 toolkit.json과 실행 결과 표까지 채운 모습으로 넣는다.
            leakStrings.AddRange(componentsLeakStrings);
            // mighty 그래프 캔버스의 글자도 로케일 키 누수 검사에 넣는다.
            leakStrings.AddRange(mightyLeakStrings);
            leakStrings.AddRange(browserLeakStrings);
            leakStrings.AddRange(filesLeakStrings);
            var koKeys = Locale.Catalogue("ko").Keys.ToList();
            var keyLeaks = LocaleKeyLeak.Detect(leakStrings, koKeys);
            result["localeKeyLeakScanned"] = leakStrings.Count;
            result["localeKeyLeaks"] = keyLeaks;
            Require(keyLeaks.Count == 0, "로케일 키가 화면에 그대로 노출됩니다: " + string.Join(", ", keyLeaks));
            Checkpoint("screenshot", "running");
            result["screenshot"] = await CaptureSmoke(Path.Combine(directory, "smoke-window.png"));
            Checkpoint("screenshot", "passed");
            result["passed"] = true; passed = true;
        }
        catch (Exception ex)
        {
            result["error"] = ex.Message; result["exceptionType"] = ex.GetType().FullName; result["exception"] = ex.ToString();
            try { result["screenshot"] = await CaptureSmoke(Path.Combine(directory, "smoke-window.png")); } catch (Exception capture) { result["captureError"] = capture.Message; }
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "smoke-result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        // 실패한 실행의 기록은 그 실패의 것이다. 정리하는 동안 뒤따라 나는 예외가 이 기록을 덮어쓰지 못하게 한다.
        if (!passed) options.KeepFailureRecord();
        await FinishSmoke(passed);
    }
    // 브라우저 창 스모크: 임시 --profile 안에서 실제 WebView2로 임시 폴더의 로컬 페이지
    // 둘을 가상 호스트(https://mighty-smoke.invalid/)로 탐색하고, 뒤로 가기와 window.open() 차단, 워크스페이스별 프로필 폴더를
    // 확인한다. 망은 건드리지 않는다. 런타임이 없는 실행기는 실패로 본다.
    private async Task<Dictionary<string, object?>> RunBrowserPaneSmoke(Workspace workspace, List<string> leakStrings)
    {
        // 이 스모크 한 번만 설정을 켠 것으로 본다.
        ForceBrowserEngineForSmoke();

        await AddBrowserPane();
        var session = service.Snapshot.Sessions.Last(s => s.Kind == "browser" && s.WorkspaceId == workspace.Id);
        await WaitUI(() => views.ContainsKey(session.Id));
        var view = views[session.Id];
        view.EnsureBrowserView();
        await view.BrowserReady;

        if (string.IsNullOrEmpty(view.BrowserRuntimeVersion))
            throw new InvalidOperationException("이 실행기에 WebView2 Evergreen 런타임이 없습니다. 런타임을 설치한 뒤 다시 실행하세요.");
        Require(view.BrowserControlLive, "WebView2 컨트롤이 만들어지지 않았습니다.");

        var (navigated, backWorked, popupBlocked) = await view.DriveBrowserSmokeAsync();
        // The slim bar and the toolbar on screen (the page itself is not part of a XAML capture), for the parity review.
        root.UpdateLayout(); await CaptureSmoke(Path.Combine(options.ProfileDirectory!, "smoke-chrome-browser.png"));

        // 프로필 폴더는 임시 --profile 아래의 상태 폴더 안에 있어야 한다.
        var profile = BrowserProfile.ProfileFolder(StateDirectory, session.WorkspaceProfileKey ?? session.WorkspaceId);
        var profileUnderTemp = profile.StartsWith(StateDirectory, StringComparison.OrdinalIgnoreCase)
            && profile.Contains(session.WorkspaceId, StringComparison.Ordinal);

        // 주소줄·단추 문구와 런타임 없음 알림·꺼짐 알림도 로케일 키 누수 검사에 넣는다.
        leakStrings.AddRange(view.BrowserVisibleStrings());
        CollectVisibleStrings(BuildBrowserMissingNotice(), leakStrings);
        CollectVisibleStrings(BuildBrowserDisabledNotice(), leakStrings);

        await CloseSession(session.Id);
        return new Dictionary<string, object?>
        {
            ["runtimeVersion"] = view.BrowserRuntimeVersion,
            ["navigated"] = navigated,
            ["backWorked"] = backWorked,
            ["profileUnderTemp"] = profileUnderTemp,
            ["popupBlocked"] = popupBlocked,
        };
    }

    // 페이즈별 모델 칸을 붙박이 도구 값으로 짓는다. 실제 사용자의 ~/.config나
    // ~/.ouroboros는 읽지도 쓰지도 않는다. 네 페이즈 줄과 네 도구 묶음이 있어야 하고,
    // 매인 손잡이들이 다른 실행 줄은 혼합으로 보여야 한다.
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

            // 설치 결과 표까지 채운 칸의 글자가 로케일 키 누수 검사에 들어간다.
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
        var originalMode = fixtureSession.TitleMode;
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
            var sidebarTitleFound = sidebarSessionButtons.Values
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

            // Auto-titles: the rename fixed the title, so a new request leaves it alone; 자동 hands it back
            // to the latest request (40 characters), and hovering the sidebar title shows the request whole.
            Require(service.Snapshot.Sessions.First(s => s.Id == fixtureSession.Id).TitleMode == PaneTitle.Fixed, "이름을 바꾼 창의 제목이 고정되지 않았습니다.");
            var offersAutomatic = false;
            smokeAskName = (dialog, _, _) => { offersAutomatic = RenameButtons(dialog).Any(b => AutomationProperties.GetAutomationId(b) == RenameAutomaticId && b.Content as string == Locale.Get("pane.rename.automatic")); return Task.FromResult(ContentDialogResult.Secondary); };
            await RenameSession(fixtureSession.Id);
            Require(offersAutomatic, "이름 변경 대화창에 자동 (요청 따름) 단추가 없습니다.");
            var latest = PaneTitle.Tooltip(service.Snapshot.Sessions.First(s => s.Id == fixtureSession.Id)) ?? "";
            Require(latest.Length > 0, "스모크 창에 제목이 될 요청이 없습니다.");
            await WaitUI(() => service.Snapshot.Sessions.First(s => s.Id == fixtureSession.Id) is { TitleMode: PaneTitle.Automatic } p && p.Title == PaneTitle.Shortened(latest));
            var sidebarTitle = sidebarSessionButtons.Values.Select(b => b.Content).OfType<Grid>()
                .Where(g => g.Children.Count > 1).Select(g => g.Children[1]).OfType<TextBlock>()
                .FirstOrDefault(t => t.Text == PaneTitle.Shortened(latest));
            Require(sidebarTitle is not null && ToolTipService.GetToolTip(sidebarTitle) as string == latest, "자동 제목의 사이드바 도움말이 최근 요청과 다릅니다.");
            checks["automaticFollowsLatestRequest"] = true;

            checks["passed"] = true;
        }
        finally
        {
            smokeAskName = null;
            await service.UpdateAsync(s => s with { Sessions = s.Sessions.Select(p => p.Id == fixtureSession.Id ? p with { Title = originalTitle, TitleMode = originalMode } : p).ToList() });
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
            // The button stands at the trailing edge of the progress row; it is still found by its id.
            var button = SettingsElements(section).OfType<Button>()
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
            await SetStatusLineEnabled(false);
            Require(statusPane.Refresher is null && statusPane.StatusLineHost.Visibility == Visibility.Collapsed,
                "disabling status line must close the refresher and hide its output");
            await SetStatusLineEnabled(true);
            await WaitUI(() => statusPane.Refresher?.Result is not null);
            checks["statusLineToggleStopsAndRestarts"] = true;
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
    // 파일 창 스모크 (docs/file-pane.md): 임시 워크스페이스에 Markdown·Swift·PNG 픽스처를 두고
    // Ctrl+Shift+E와 같은 길(OpenFilePane)로 실제 창을 연다. 루트 목록과 잡음 폴더 접힘, 폴더 펼치기,
    // 세 미리보기(렌더링한 Markdown, 줄 여섯 개의 UTF-8 소스, 40×30 PNG)를 확인하고, 다시 열면 같은
    // 창으로 가는지, 상태 파일에 쓰이지 않는지 본다. 픽스처 밖의 파일은 읽지 않고 AI 요청도 없다.
    private async Task<Dictionary<string, object?>> RunFilesPaneSmoke(Workspace workspace, List<string> leakStrings)
    {
        var previousMode = LayoutMode(service.Snapshot, workspace.Id);
        var previousTree = EffectiveLayout(service.Snapshot, workspace.Id);
        var previousActive = service.Snapshot.ActiveSessionId;
        var project = workspace.Path;
        Directory.CreateDirectory(Path.Combine(project, "Sources"));
        Directory.CreateDirectory(Path.Combine(project, "node_modules", "pkg"));
        await File.WriteAllTextAsync(Path.Combine(project, "README.md"), "# Files pane\n\n- rendered **markdown**\n\n```swift\nlet x = 1\n```\n");
        await File.WriteAllTextAsync(Path.Combine(project, "Sources", "App.swift"), "import Foundation\n\n// A comment\nlet answer = 42\nprint(\"hello\")\n");
        using (var png = new InMemoryRandomAccessStream())
        {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, png);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, 40, 30, 96, 96, new byte[40 * 30 * 4]); await encoder.FlushAsync();
            using var reader = new DataReader(png.GetInputStreamAt(0)); await reader.LoadAsync((uint)png.Size); var bytes = new byte[(int)png.Size]; reader.ReadBytes(bytes);
            await File.WriteAllBytesAsync(Path.Combine(project, "image.png"), bytes);
        }

        Require(HasOpenFilesAccelerator, "Ctrl+Shift+E 단축키가 창에 등록되지 않았습니다.");
        // placedLeft must not depend on the layout an earlier smoke left behind: in tabs mode
        // every pane shares one group, so the files pane has room to split off to its left.
        Require(service.Snapshot.ActiveWorkspaceId == workspace.Id, "파일 창 스모크가 다른 워크스페이스에서 시작했습니다.");
        await ApplyLayoutPreset("tabs");
        await OpenFilePane(workspace.Id);
        var paneId = FilePaneKind.PaneId(workspace.Id);
        var paneOpened = service.Snapshot.ActiveSessionId == paneId && service.Snapshot.Sessions.Any(s => s.Id == paneId && s.Kind == FilePaneKind.Kind);
        var placedLeft = EffectiveLayout(service.Snapshot, workspace.Id) is { Kind: "split" } placed && PaneLayout.Groups(placed).First().SessionIds.SequenceEqual([paneId]);
        await WaitUI(() => views.TryGetValue(paneId, out var opened) && opened.FilesHost is not null);
        var view = views[paneId];
        await WaitUI(() => view.FilesTree.Children.ContainsKey("") && view.FilesTreeItemCount > 0);
        var rootEntries = view.FilesTree.Children[""].Select(e => e.Name).ToList();
        var noiseCollapsed = view.FilesTree.Children[""].FirstOrDefault(e => e.Name == "node_modules")?.IsNoise == true && !view.FilesTree.Expanded.Contains("node_modules");
        await view.FilesSmokeOpenFolder("Sources");
        await WaitUI(() => view.FilesTree.Children.ContainsKey("Sources"));
        var folderOpened = view.FilesTree.Children["Sources"].Any(e => e.RelativePath == "Sources/App.swift");

        await view.FilesSmokePreview("README.md");
        var markdown = view.FilesShown is { Kind.Tag: FilePreviewKindTag.Markdown, MarkdownRenderable: true };
        await view.FilesSmokePreview("Sources/App.swift");
        var swift = view.FilesShown is { Kind.Tag: FilePreviewKindTag.Source, Encoding: TextEncodingKind.Utf8, Text: { } source } && SourceLines.Scan(source).Starts.Count == 6;
        await view.FilesSmokePreview("image.png");
        var image = view.FilesShown is { Kind.Tag: FilePreviewKindTag.Image } && view.FilesPreviewPixels == (40, 30);
        root.UpdateLayout(); await Task.Delay(150);
        var screenshot = await CaptureSmoke(Path.Combine(options.ProfileDirectory!, "smoke-files.png"));
        CollectVisibleStrings(view.FilesHost!, leakStrings);

        await OpenFilePane(workspace.Id);
        var reopenFocusesSamePane = service.Snapshot.Sessions.Count(s => s.Id == paneId) == 1 && service.Snapshot.ActiveSessionId == paneId && ReferenceEquals(view, views[paneId]);
        // What the app actually wrote: wait for the queued save, then read the state file.
        await service.UpdateAsync(s => s);
        var written = await File.ReadAllTextAsync(Path.Combine(StateDirectory, "workspace-state.json"));
        var neverSaved = !written.Contains(paneId, StringComparison.Ordinal) && service.Snapshot.Sessions.Any(s => s.Id == paneId);
        await CloseSession(paneId);
        // Put back the tree and mode the earlier smokes left, not a rebuilt preset.
        await service.UpdateAsync(s => SaveLayoutMode(SaveLayout(s, workspace.Id, previousTree), workspace.Id, previousMode)); Render();
        if (previousActive is not null && service.Snapshot.Sessions.Any(s => s.Id == previousActive)) await SelectLayoutSession(previousActive);
        Require(paneOpened && placedLeft && noiseCollapsed && folderOpened && markdown && swift && image && reopenFocusesSamePane && neverSaved,
            $"파일 창 스모크 실패: opened={paneOpened} left={placedLeft} noise={noiseCollapsed} folder={folderOpened} md={markdown} swift={swift} png={image} reopen={reopenFocusesSamePane} unsaved={neverSaved}");
        return new Dictionary<string, object?>
        {
            ["shortcut"] = FilePaneKind.Shortcut,
            ["paneOpened"] = paneOpened,
            ["placedLeft"] = placedLeft,
            ["rootEntries"] = rootEntries,
            ["noiseCollapsed"] = noiseCollapsed,
            ["folderOpened"] = folderOpened,
            ["markdown"] = markdown,
            ["swift"] = swift,
            ["png"] = image,
            ["reopenFocusesSamePane"] = reopenFocusesSamePane,
            ["neverSaved"] = neverSaved,
            ["screenshot"] = screenshot,
        };
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
        pane.EnsureMightyView();
        pane.GraphDiagnosticsForSmoke = true;
        options.TraceStartup("smoke:mightyGraph:fixture");
        await pane.SetGraphRunsForSmoke(runs);
        options.TraceStartup("smoke:mightyGraph:mode-enter");
        await pane.SetAgentViewMode("mighty");
        options.TraceStartup("smoke:mightyGraph:layout-enter");
        root.UpdateLayout(); options.TraceStartup("smoke:mightyGraph:update-layout-returned"); await Task.Delay(60);
        options.TraceStartup("smoke:mightyGraph:layout-ready");
        Require(pane.SessionForSmoke.AgentViewMode == "mighty", "마이티 모드가 저장되지 않았습니다.");
        Require(pane.SessionForSmoke.Draft == draftBefore, "모드 전환이 입력창 초안을 지웠습니다.");

        var reading = pane.ReadGraphForSmoke();
        Require(reading.Blocks >= 3, "mighty 스모크: 블록이 3개 미만입니다: " + reading.Blocks);
        Require(reading.Edges >= 2, "mighty 스모크: 엣지가 2개 미만입니다: " + reading.Edges);
        Require(reading.Kinds.Contains("request") && reading.Kinds.Contains("result"), "mighty 스모크: 요청·결과 블록이 없습니다.");
        await WaitUI(() => pane.GraphNativeViewsForSmoke().Values.All(view => view.Card is FrameworkElement { IsLoaded: true }
            && (view.Document is null || view.Document is FrameworkElement { IsLoaded: true })));
        var nativeViews = pane.GraphNativeViewsForSmoke();
        Require(nativeViews.Values.Any(view => view.Document is not null), "Graph lifetime smoke needs a real native document.");
        Require(nativeViews.Values.Where(view => view.Document is not null).All(view => view.Parent is not null), "Loaded native graph documents must have an actual parent.");
        void RequireRetainedNativeViews()
        {
            var current = pane.GraphNativeViewsForSmoke();
            Require(nativeViews.All(pair => current.TryGetValue(pair.Key, out var next) && ReferenceEquals(pair.Value.Card, next.Card)
                && ReferenceEquals(pair.Value.Document, next.Document) && ReferenceEquals(pair.Value.Parent, next.Parent)),
                "Graph redraw must retain each card, native document and document parent.");
        }
        pane.RefreshMightyView(pane.SessionForSmoke); root.UpdateLayout(); await Task.Delay(30);
        RequireRetainedNativeViews();
        var savedAnswer = last.FinalOutput;
        last.FinalOutput += "\nNative document retention fixture."; MightyGraphSupport.RefreshResult(last);
        await pane.SetGraphRunsForSmoke(runs); root.UpdateLayout(); await Task.Delay(30);
        RequireRetainedNativeViews();
        last.FinalOutput = savedAnswer; MightyGraphSupport.RefreshResult(last);
        await pane.SetGraphRunsForSmoke(runs);

        // Right-side cards (macOS a65a65b): every request block's header carries the pane's
        // agent mark before `· Claude`; sub-agent, result and draft headers carry none.
        var paneProvider = pane.SessionForSmoke.Provider;
        var titleMarks = pane.GraphTitleMarksForSmoke();
        var requestTitles = titleMarks.Where(t => t.Kind == "request").ToList();
        Require(requestTitles.Count > 0, "mighty 스모크: 요청 블록 머리가 그려지지 않았습니다.");
        Require(requestTitles.All(t => t.Mark == ProviderMark.MarkedProvider(paneProvider)), "mighty 스모크: 요청 블록 머리에 " + paneProvider + " 마크가 없습니다.");
        Require(titleMarks.Where(t => t.Kind != "request").All(t => t.Mark is null), "mighty 스모크: 요청이 아닌 블록 머리에 에이전트 마크가 있습니다.");
        var requestMarks = new Dictionary<string, object?> { ["provider"] = paneProvider, ["requestHeaders"] = requestTitles.Count, ["otherHeadersWithoutMark"] = titleMarks.Count - requestTitles.Count };

        // Zoom: 50% at the bottom, 100% on reset, 150% at the top, disabled at the ends.
        options.TraceStartup("smoke:mightyGraph:zoom");
        pane.SetGraphZoom(MightyGraphViewModel.ZoomMin);
        Require(MightyGraphViewModel.ZoomOutDisabled(pane.GraphZoom), "최소 배율에서 축소 단추가 잠기지 않았습니다.");
        var minimum = (int)Math.Round(pane.GraphZoom * 100);
        pane.SetGraphZoom(MightyGraphViewModel.ZoomDefault);
        var reset = (int)Math.Round(pane.GraphZoom * 100);
        pane.SetGraphZoom(MightyGraphViewModel.ZoomMax);
        Require(MightyGraphViewModel.ZoomInDisabled(pane.GraphZoom), "최대 배율에서 확대 단추가 잠기지 않았습니다.");
        var maximum = (int)Math.Round(pane.GraphZoom * 100);
        pane.SetGraphZoom(MightyGraphViewModel.ZoomDefault);

        options.TraceStartup("smoke:mightyGraph:result-fit");
        var resultFit = await RunResultFitSmoke(pane);
        RequireRetainedNativeViews();
        options.TraceStartup("smoke:mightyGraph:result-reveal");
        var resultReveal = await RunResultRevealSmoke(pane);

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

        options.TraceStartup("smoke:mightyGraph:outlines");
        var outlines = await RunActivityOutlineSmoke(pane);

        // The canvas text joins the locale-key leak scan.
        CollectVisibleStrings(pane.GraphCanvas, leakStrings);
        leakStrings.Add(pane.GraphTotalText);
        leakStrings.AddRange(pane.GraphHeaderTextsForSmoke());
        await SettleDesktopCapture(root);
        await CaptureSmoke(Path.Combine(options.ProfileDirectory!, "smoke-mighty-graph.png"));

        await pane.SetAgentViewMode("default");
        root.UpdateLayout(); await Task.Delay(30);
        var modeRestored = pane.SessionForSmoke.AgentViewMode == "default" && pane.SessionForSmoke.Draft == draftBefore;
        Require(modeRestored, "기본으로 되돌린 뒤 모드나 초안이 어긋났습니다.");
        pane.GraphDiagnosticsForSmoke = false;

        return new Dictionary<string, object?>
        {
            ["blocks"] = reading.Blocks,
            ["edges"] = reading.Edges,
            ["kinds"] = reading.Kinds,
            ["resultFiles"] = reading.ResultFiles,
            ["zoom"] = new[] { minimum, reset, maximum },
            ["modeRestored"] = modeRestored,
            ["resultFit"] = resultFit,
            ["resultReveal"] = resultReveal,
            ["retainedNativeDocuments"] = true,
            ["requestMarks"] = requestMarks,
            ["outlines"] = outlines,
        };
    }

    /// Running blocks have the slowly moving dashed outline (macOS MightyGraphActivityOutline):
    /// 9/7 dashes fitted to the card, solid with Windows animations off; a waiting block
    /// keeps a still amber line. The first request is set running, then waiting, then put back.
    private async Task<Dictionary<string, object?>> RunActivityOutlineSmoke(PaneView pane)
    {
        // SessionForSmoke is a copy of the saved snapshot: the runs change through the
        // service (as RunResultRevealSmoke does) so the canvas really draws the new status.
        var original = (pane.SessionForSmoke.GraphRuns ?? []).Select(r => r.Copy()).ToList();
        Require(original.Count > 0, "실행 표시 테두리 스모크: 요청 블록이 없습니다.");
        var requestId = MightyGraphLayout.NodeID(original[0], "request");
        async Task<(string Kind, double[] Dash)> Draw(string next, bool animations)
        {
            PaneView.AnimationsEnabledOverride = animations;
            var runs = original.Select(r => r.Copy()).ToList(); runs[0].Status = next;
            await pane.SetGraphRunsForSmoke(runs);
            Require((pane.SessionForSmoke.GraphRuns ?? []).FirstOrDefault()?.Status == next, "실행 표시 테두리 스모크: 요청 상태가 " + next + "(으)로 저장되지 않았습니다.");
            pane.RefreshMightyView(pane.SessionForSmoke); root.UpdateLayout(); await Task.Delay(30);
            Require(pane.GraphOutlinesForSmoke.TryGetValue(requestId, out var drawn),
                "실행 표시 테두리 스모크: " + next + " 요청 블록에 테두리가 없습니다.");
            return drawn;
        }
        try
        {
            var marching = await Draw("running", true);
            Require(marching.Kind == MightyGraphActivity.Marching && marching.Dash.Length == 2,
                "실행 중 블록의 테두리가 움직이는 점선이 아닙니다: " + marching.Kind);
            // Fitted 9/7 dashes in 2pt stroke widths: the dash-to-gap ratio stays 9:7.
            Require(Math.Abs(marching.Dash[0] / marching.Dash[1] - 9.0 / 7) < 1e-6, "점선의 9/7 비율이 macOS와 다릅니다.");
            var still = await Draw("running", false);
            Require(still.Kind == MightyGraphActivity.Solid && still.Dash.Length == 0, "애니메이션이 꺼졌는데 실행 테두리가 점선입니다.");
            var waiting = await Draw("waiting", true);
            Require(waiting.Kind == MightyGraphActivity.Waiting && waiting.Dash.Length == 0, "대기 블록의 테두리가 멈춘 주황 선이 아닙니다.");
            return new Dictionary<string, object?> { ["running"] = marching.Kind, ["reducedMotion"] = still.Kind, ["waiting"] = waiting.Kind };
        }
        finally
        {
            PaneView.AnimationsEnabledOverride = null;
            await pane.SetGraphRunsForSmoke(original);
            pane.RefreshMightyView(pane.SessionForSmoke); root.UpdateLayout(); await Task.Delay(30);
        }
    }

    /// The Mighty result box fits the agent pane (macOS 1d5a0db): with a saved
    /// size larger than a narrow pane the newest result card is drawn inside
    /// the pane, and once the pane is wide again it is back at the saved size;
    /// zoomed in it still stays inside; 창에 맞추기 clears the saved size.
    /// Every expected size is Core's own rule for the viewport actually drawn.
    private async Task<Dictionary<string, object?>> RunResultFitSmoke(PaneView pane)
    {
        var runs = pane.SessionForSmoke.GraphRuns ?? [];
        var resultId = MightyGraphLayout.LatestResultID(runs);
        Require(resultId is not null, "결과 박스 스모크: 최신 결과 카드가 없습니다.");
        var saved = new GraphBlockSize(500, 300);
        await pane.SetGraphResultSizeForSmoke(saved);
        var fitShown = pane.GraphFitResultButtonShownForSmoke;
        Require(fitShown, "저장한 결과 크기가 있는데 창에 맞추기 단추가 없습니다.");

        // The card is as tall as its measured content under the cap (the saved
        // size kept within the pane), so the expected size is Core's rule for
        // the cap and the height the pane measured; a new width re-measures,
        // so the drawing is given a few passes to settle.
        async Task<((double W, double H) Card, (double W, double H) Cap, (double W, double H) Expected, (double W, double H) Viewport)> Draw(double width, double height, double zoom)
        {
            pane.SetGraphViewportSizeForSmoke(width, height);
            pane.SetGraphZoom(zoom);
            root.UpdateLayout(); await Task.Delay(60);
            pane.RefreshMightyView(pane.SessionForSmoke); root.UpdateLayout(); await Task.Delay(30);
            var viewport = pane.GraphViewportSizeForSmoke;
            var limit = MightyGraphLayout.ResultViewportLimit(viewport, zoom, MightyGraphLayout.FilesPanelOpen(runs, pane.GraphResultFilesRunId));
            var cap = MightyGraphLayout.ResultCap((saved.Width, saved.Height), limit);
            (double W, double H) card = default, expected = default;
            for (var pass = 0; pass < 10; pass++)
            {
                card = pane.GraphCardSizeForSmoke(resultId!) ?? throw new InvalidOperationException("결과 박스 스모크: 최신 결과 카드가 그려지지 않았습니다.");
                expected = MightyGraphLayout.ResultSize(cap, pane.GraphResultContentHeightForSmoke(resultId!));
                if (Math.Abs(card.W - expected.W) < 0.5 && Math.Abs(card.H - expected.H) < 0.5) break;
                root.UpdateLayout(); await Task.Delay(30);
            }
            Require(Math.Abs(card.W - expected.W) < 0.5 && Math.Abs(card.H - expected.H) < 0.5,
                $"결과 박스가 창 크기 규칙과 다릅니다: 창 {viewport.W}×{viewport.H} @{zoom}, 카드 {card.W}×{card.H}, 기대 {expected.W}×{expected.H}");
            Require(card.H <= cap.H + 0.5, "결과 박스가 저장한 크기(창 안)보다 높습니다.");
            Require(card.W * zoom <= Math.Max(viewport.W - 48, MightyGraphBlockSize.MinimumWidth * zoom) + 0.5, "결과 박스가 창보다 넓습니다.");
            return (card, cap, expected, viewport);
        }

        var wide = await Draw(1000, 700, 1);
        // The result strip with a saved size in force: "fit to window" and the expand control (M/MightyGraphView.swift:528-547).
        await SettleDesktopCapture(root); await CaptureSmoke(Path.Combine(options.ProfileDirectory!, "smoke-mighty-result-saved.png"));
        var grewToSaved = wide.Cap == (saved.Width, saved.Height) && wide.Card.W == saved.Width;
        Require(grewToSaved, $"넓은 창에서 결과 박스가 저장한 크기가 아닙니다: {wide.Card.W}×{wide.Card.H} (최대 {wide.Cap.W}×{wide.Cap.H})");
        var narrow = await Draw(600, 300, 1);
        var shrank = narrow.Card.W < saved.Width && narrow.Cap.H < saved.Height && narrow.Card.H <= narrow.Cap.H + 0.5;
        Require(shrank, $"좁은 창에서 결과 박스가 줄어들지 않았습니다: {narrow.Card.W}×{narrow.Card.H}");
        Require(pane.SessionForSmoke.GraphResultSize == saved, "창이 좁아졌다고 저장한 결과 크기가 바뀌었습니다.");
        var back = await Draw(1000, 700, 1);
        var grewBack = back.Cap == (saved.Width, saved.Height) && back.Card.W == saved.Width;
        Require(grewBack, $"창을 다시 키웠는데 결과 박스가 저장한 크기로 돌아오지 않았습니다: {back.Card.W}×{back.Card.H}");
        var zoomed = await Draw(700, 400, MightyGraphViewModel.ZoomMax);
        var zoomedInside = zoomed.Card.H * MightyGraphViewModel.ZoomMax <= zoomed.Viewport.H - 48 + 0.5;
        Require(zoomedInside, "확대했을 때 결과 박스가 창 높이를 넘습니다.");

        await pane.FitResultToWindowForSmoke();
        var fitCleared = pane.SessionForSmoke.GraphResultSize is null && !pane.GraphFitResultButtonShownForSmoke;
        Require(fitCleared, "창에 맞추기가 저장한 결과 크기를 지우지 않았습니다.");

        pane.SetGraphViewportSizeForSmoke(double.NaN, double.NaN);
        pane.SetGraphZoom(MightyGraphViewModel.ZoomDefault);
        root.UpdateLayout(); await Task.Delay(30);
        return new Dictionary<string, object?>
        {
            ["fitButton"] = fitShown,
            ["grewToSaved"] = grewToSaved,
            ["shrank"] = shrank,
            ["grewBack"] = grewBack,
            ["zoomedInside"] = zoomedInside,
            ["fitCleared"] = fitCleared,
        };
    }

    /// A new result right above the composer (macOS 1e7b48d): the pane runs a
    /// request and its finished diagram arrives while it still runs (RunManager
    /// emits graph_run before the state event). The camera scrolls once so the
    /// new card's bottom sits 16pt above the diagram's bottom edge — the
    /// composer — with the card as tall as its short answer under the saved
    /// size, which is never rewritten. A resize keeps the card there; the
    /// user's own pan ends the hold.
    private async Task<Dictionary<string, object?>> RunResultRevealSmoke(PaneView pane)
    {
        var before = pane.SessionForSmoke.GraphRuns ?? [];
        var statusBefore = pane.SessionForSmoke.Status;
        var saved = new GraphBlockSize(900, 600);
        pane.SetGraphViewportSizeForSmoke(1000, 700);
        pane.SetGraphZoom(MightyGraphViewModel.ZoomDefault);
        await pane.SetGraphResultSizeForSmoke(saved);
        root.UpdateLayout(); await Task.Delay(60);

        await pane.SetPaneStatusForSmoke("running");
        root.UpdateLayout(); await Task.Delay(30);
        var run = new MightyGraphRun { Id = "smoke-reveal", Input = "짧은 요청", Status = "completed", FinalOutput = "짧은 답입니다." };
        MightyGraphSupport.RefreshResult(run);
        await pane.SetGraphRunsForSmoke([.. before, run]);
        pane.RefreshMightyView(pane.SessionForSmoke);
        await pane.SetPaneStatusForSmoke(statusBefore);
        var resultId = MightyGraphBlockSize.NodeId(run.Id, "result");

        bool AtComposer() => pane.GraphCardFrameForSmoke(resultId) is { } frame
            && Math.Abs(pane.GraphPan.Y + frame.MaxY * pane.GraphZoom - (pane.GraphViewportSizeForSmoke.H - 16)) < 1;
        async Task<bool> Settle()
        {
            for (var pass = 0; pass < 20; pass++)
            {
                root.UpdateLayout(); await Task.Delay(50);
                if (pane.GraphResultContentHeightForSmoke(resultId) is not null && AtComposer()) return true;
            }
            return false;
        }

        var revealed = await Settle();
        Require(revealed, $"새 결과가 입력창 바로 위에 오지 않았습니다: 카드 {pane.GraphCardFrameForSmoke(resultId)}, 이동 {pane.GraphPan}, 창 {pane.GraphViewportSizeForSmoke}");
        var card = pane.GraphCardSizeForSmoke(resultId) ?? throw new InvalidOperationException("새 결과 카드가 그려지지 않았습니다.");
        var contentFit = card.H < saved.Height - 0.5 && card.H >= MightyGraphLayout.MinimumResultHeight - 0.5 && card.W <= saved.Width + 0.5;
        Require(contentFit, $"짧은 결과가 내용 높이로 줄지 않았습니다: {card.W}×{card.H}, 저장한 크기 {saved.Width}×{saved.Height}");
        var savedKept = pane.SessionForSmoke.GraphResultSize == saved;
        Require(savedKept, "결과 카드가 내용에 맞춰 줄면서 저장한 크기를 바꿨습니다.");

        pane.SetGraphViewportSizeForSmoke(1000, 560);
        root.UpdateLayout(); await Task.Delay(60);
        var followsResize = await Settle();
        Require(followsResize, "창 크기가 바뀐 뒤 새 결과가 입력창 바로 위에 남지 않았습니다.");

        pane.PanGraphForSmoke(-40);
        var panned = pane.GraphPan;
        pane.SetGraphViewportSizeForSmoke(1000, 700);
        root.UpdateLayout(); await Task.Delay(60);
        pane.RefreshMightyView(pane.SessionForSmoke); root.UpdateLayout(); await Task.Delay(30);
        var userPanStops = pane.GraphRevealHoldingForSmoke is null && pane.GraphPan == panned && !AtComposer();
        Require(userPanStops, "사용자가 스크롤한 뒤에도 새 결과를 계속 따라갑니다.");

        await pane.SetGraphRunsForSmoke([.. before]);
        await pane.SetGraphResultSizeForSmoke(null);
        pane.SetGraphViewportSizeForSmoke(double.NaN, double.NaN);
        pane.RefreshMightyView(pane.SessionForSmoke);
        root.UpdateLayout(); await Task.Delay(30);
        return new Dictionary<string, object?>
        {
            ["revealed"] = revealed,
            ["contentFit"] = contentFit,
            ["savedKept"] = savedKept,
            ["followsResize"] = followsResize,
            ["userPanStops"] = userPanStops,
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

    /// <summary>
    /// Session history and resume (macOS b393741, c5e04e5) in the real window, on a
    /// fixture Claude record in a temporary home: 창 추가 → Claude asks 새로 시작 /
    /// 이어가기…, the list shows the folder's one typed session with the nested
    /// Ouroboros run hidden, picking it adds a pane that resumes it (titled after
    /// its first request, remembered in known-sessions.json), the Mighty view loads
    /// the latest ten requests from the record at once and the next ten from the
    /// history block without moving the cards already drawn, and a second 창 추가
    /// no longer offers the session the pane holds. Every pane it adds is closed again.
    /// </summary>
    private async Task<Dictionary<string, object?>> RunSessionHistorySmoke(Workspace workspace, Workspace other)
    {
        var checks = new Dictionary<string, object?>();
        var home = Path.Combine(options.ProfileDirectory!, "history-home");
        var before = service.Snapshot.Sessions.Select(s => s.Id).ToHashSet();
        PaneView.HistoryHomeOverride = home;
        try
        {
            await SelectWorkspace(other.Id);
            var folder = Path.Combine(home, ".claude", "projects", SessionHistory.ClaudeProjectFolder(other.Path));
            Directory.CreateDirectory(folder);
            const string typed = "6a1e2c1a-1111-4222-8333-444455556666", nested = "7a1e2c1a-1111-4222-8333-444455556666";
            var start = DateTimeOffset.UtcNow.AddDays(-1);
            var lines = new List<string>();
            for (var i = 1; i <= 12; i++)
            {
                var at = start.AddMinutes(i * 5);
                lines.Add(JsonSerializer.Serialize(new { type = "user", message = new { role = "user", content = "이전 요청 " + i }, uuid = "u" + i, timestamp = at.ToString("O"), cwd = other.Path }));
                lines.Add(JsonSerializer.Serialize(new { type = "assistant", message = new { id = "m" + i, role = "assistant", model = "claude-opus-4-5", content = new[] { new { type = "text", text = "이전 답 " + i } } }, uuid = "a" + i, timestamp = at.AddSeconds(3).ToString("O") }));
            }
            await File.WriteAllTextAsync(Path.Combine(folder, typed + ".jsonl"), string.Join("\n", lines) + "\n");
            await File.WriteAllTextAsync(Path.Combine(folder, nested + ".jsonl"), JsonSerializer.Serialize(new { type = "user", message = new { role = "user", content = "User: 단계 실행\n\nAssistant: 네" }, uuid = "n1", timestamp = start.ToString("O"), cwd = other.Path }) + "\n");

            var titles = new List<string?>(); var listed = -1; var hiddenText = "";
            smokeResumeDialog = async dialog =>
            {
                // The sheets draw their own titles (the Mac's layout); each is named after its title.
                titles.Add(AutomationProperties.GetName(dialog));
                if (AutomationProperties.GetAutomationId(dialog) == "add-pane-choice") return ContentDialogResult.Secondary;
                var sheet = (ResumeSheetParts)dialog.Tag;
                await WaitUI(() => sheet.List.Items.Count > 0);
                listed = sheet.List.Items.Count;
                hiddenText = sheet.Hidden.Text;
                // The show-all check box's words (M/ResumeSessionSheet.swift:93): the regular caption in ink, set at its own size
                // beside the scaled box (not inside it), which keeps their name.
                var showAllWords = sheet.ShowAllWords;
                Require(sheet.ShowAll.Content is null && AutomationProperties.GetName(sheet.ShowAll) == showAllWords.Text && showAllWords.Text == Locale.Get("resume.showAll")
                    && showAllWords.FontSize == DesignMetrics.Type.Small && showAllWords.FontWeight.Weight == Microsoft.UI.Text.FontWeights.Normal.Weight && ReferenceEquals(showAllWords.Foreground, brushes.Brush(DesignToken.Ink)),
                    $"the resume sheet's show-all words must be the regular {DesignMetrics.Type.Small}pt caption in ink beside their box, which keeps their name; got {showAllWords.FontSize}pt at weight {showAllWords.FontWeight.Weight} in {(showAllWords.Foreground as SolidColorBrush)?.Color}, box named '{AutomationProperties.GetName(sheet.ShowAll)}' holding {sheet.ShowAll.Content ?? "nothing"}");
                await CaptureSheet(dialog, "sheet-resume-list");
                sheet.Choose((ResumableSession)((FrameworkElement)sheet.List.Items[0]).Tag);
                return ContentDialogResult.None;
            };
            await AddAgentPane("claude", null);
            var name = ProviderMark.Label("claude");
            Require(titles.Count == 2 && titles[0] == Locale.Get("resume.choice.title", new Dictionary<string, string> { ["provider"] = name }) && titles[1] == Locale.Get("resume.title"), "창 추가가 새로 시작 / 이어가기… 선택과 세션 목록을 차례로 열지 않았습니다: " + string.Join(" | ", titles));
            checks["choiceOffered"] = true;
            Require(listed == 1 && hiddenText == Locale.Get("resume.hiddenCount", new Dictionary<string, string> { ["count"] = "1" }), $"세션 목록이 자동 실행 기록을 숨기지 않았습니다: 줄 {listed}, 숨김 '{hiddenText}'");
            checks["nestedRunHidden"] = true;
            var added = service.Snapshot.Sessions.FirstOrDefault(s => !before.Contains(s.Id));
            Require(added is { Kind: "claude", Provider: "claude", ResumeId: typed, Title: "이전 요청 1", TitleMode: PaneTitle.Automatic }, "고른 세션을 이어가는 새 창이 생기지 않았습니다.");
            Require(service.KnownSessions().Contains(typed) && File.Exists(service.KnownSessionsPath), "이어간 세션이 known-sessions.json에 남지 않았습니다.");
            checks["resumedInNewPane"] = true;

            await WaitUI(() => views.TryGetValue(added!.Id, out var view) && view.Container.ActualWidth > 0);
            var pane = views[added!.Id];
            pane.EnsureMightyView();
            await pane.SetAgentViewMode("mighty");
            root.UpdateLayout();
            await WaitUI(() => pane.GraphHistoryForSmoke.Runs.Count == 10 && pane.GraphHistoryForSmoke.Phase == SessionHistoryState.Phases.Idle);
            Require(pane.GraphHistoryForSmoke.Runs.Select(r => r.Input).SequenceEqual(Enumerable.Range(3, 10).Select(i => "이전 요청 " + i)), "마이티 화면이 기록의 최근 요청 10개를 불러오지 않았습니다.");
            Require(pane.GraphHistoryTextForSmoke() == pane.GraphHistoryForSmoke.BlockText(10), "다이어그램 맨 위 기록 블록의 문구가 다릅니다: " + pane.GraphHistoryTextForSmoke());
            await CaptureHistoryBlock(pane, "load");
            checks["latestLoadedOnOpen"] = true;
            var firstLoaded = MightyGraphLayout.NodeID(pane.GraphHistoryForSmoke.Runs[0], "request");
            // The newest result is measured after it is drawn, and that moves the blocks above it: sample once its height holds.
            double? settled = null;
            for (int tries = 0, held = 0; tries < 120 && held < 6; tries++)
            {
                await Task.Delay(25);
                var height = pane.GraphSettledResultHeightForSmoke; held = height is not null && height == settled ? held + 1 : 0; settled = height;
            }
            root.UpdateLayout();
            var position = pane.GraphCardPositionForSmoke(firstLoaded);
            pane.LoadOlderGraphHistory();
            await WaitUI(() => pane.GraphHistoryForSmoke.Phase == SessionHistoryState.Phases.Start);
            root.UpdateLayout();
            Require(pane.GraphHistoryForSmoke.Runs.Count == 12 && pane.GraphOriginYForSmoke < 0, "맨 위에서 이전 요청을 더 불러오지 않았습니다.");
            var after = pane.GraphCardPositionForSmoke(firstLoaded);
            Require(position is { } p0 && after is { } p1 && Math.Abs(p0.Y - p1.Y) < 0.5, $"이전 요청을 불러올 때 화면의 카드가 움직였습니다: {position} → {after}");
            Require(pane.GraphHistoryTextForSmoke() == pane.GraphHistoryForSmoke.BlockText(12), "기록의 처음에 닿았다는 문구가 없습니다.");
            await CaptureHistoryBlock(pane, "start");
            checks["olderLoadedAboveWithoutMoving"] = true;

            // The pane holds the typed session and the nested one is hidden: nothing left to offer.
            var asked = false;
            smokeResumeDialog = _ => { asked = true; return Task.FromResult(ContentDialogResult.None); };
            var count = service.Snapshot.Sessions.Count;
            await AddAgentPane("claude", null);
            Require(!asked && service.Snapshot.Sessions.Count == count + 1 && service.Snapshot.Sessions[^1].ResumeId is null, "열린 창이 이어가는 세션이 다시 이어가기로 제안되었습니다.");
            checks["openSessionNotOffered"] = true;
        }
        finally
        {
            smokeResumeDialog = null;
            PaneView.HistoryHomeOverride = null;
            foreach (var id in service.Snapshot.Sessions.Where(s => !before.Contains(s.Id)).Select(s => s.Id).ToList()) await CloseSession(id);
            await SelectWorkspace(workspace.Id);
        }
        return checks;
    }
    /// <summary>
    /// Smoke key <c>addPaneMenu</c> (macOS WorkspaceView.swift <c>WorkspaceAddMenuItems</c>):
    /// the real 창 추가 menu ends with 프로젝트 폴더 열기… after a separator, showing Ctrl+O;
    /// the sidebar open-folder button hides while a workspace is listed and shows for a
    /// search that lists none; Ctrl+O and Ctrl+N are registered; and Gemini from the menu
    /// and Ctrl+N add their panes without asking 새로 시작 / 이어가기. Every pane it adds
    /// is closed again.
    /// </summary>
    private async Task<Dictionary<string, object?>> RunAddPaneMenuSmoke(Workspace workspace, Workspace other)
    {
        var checks = new Dictionary<string, object?>();
        var items = NewSessionMenu().Items.ToList();
        Require(items.Count == AddPaneMenu.Entries().Count, $"창 추가 메뉴 항목 수가 다릅니다: {items.Count}");
        var last = items[^1] as MenuFlyoutItem;
        Require(last is not null && last.Text == Locale.Get(AddPaneMenu.OpenProjectKey) && last.KeyboardAcceleratorTextOverride == AddPaneMenu.OpenFolderShortcut && items[^2] is MenuFlyoutSeparator,
            "창 추가 메뉴 맨 아래에 구분선과 프로젝트 폴더 열기…가 없습니다: " + last?.Text);
        checks["openProjectLast"] = true;
        checks["openProjectText"] = last!.Text;

        RenderSidebar();
        Require(workspaces.Children.Count > 0 && addFolderButton.Visibility == Visibility.Collapsed, "워크스페이스가 보이는데 사이드바 폴더 열기 단추가 보입니다.");
        var previous = search.Text;
        try
        {
            search.Text = "no-such-folder-" + Wire.Id(); RenderSidebar();
            Require(workspaces.Children.Count == 0 && addFolderButton.Visibility == Visibility.Visible, "검색 결과가 없는데 사이드바 폴더 열기 단추가 숨어 있습니다.");
        }
        finally { search.Text = previous; RenderSidebar(); }
        Require(addFolderButton.Visibility == Visibility.Collapsed, "검색을 지운 뒤에도 폴더 열기 단추가 남았습니다.");
        checks["openFolderButtonOnlyWhenNoneListed"] = true;
        Require(HasAddPaneShortcuts, "Ctrl+O·Ctrl+N 단축키가 창에 등록되지 않았습니다.");
        checks["shortcuts"] = AddPaneMenu.OpenFolderShortcut + ", Ctrl+N";

        var before = service.Snapshot.Sessions.Select(s => s.Id).ToHashSet();
        var asked = false;
        smokeResumeDialog = _ => { asked = true; return Task.FromResult(ContentDialogResult.None); };
        try
        {
            await SelectWorkspace(other.Id);
            var count = service.Snapshot.Sessions.Count;
            await AddAgentPane("gemini", null);
            Require(!asked && service.Snapshot.Sessions.Count == count + 1 && service.Snapshot.Sessions[^1] is { Kind: "claude", Provider: "gemini" }, "창 추가 → Gemini가 묻지 않고 곧바로 창을 만들지 않았습니다.");
            checks["geminiStartsAtOnce"] = true;
            await AddPaneFromShortcut();
            Require(!asked && service.Snapshot.Sessions.Count == count + 2 && service.Snapshot.Sessions[^1] is { Kind: "claude", Provider: AddPaneMenu.NewPaneShortcutProvider, ResumeId: null }, "Ctrl+N이 묻지 않고 곧바로 Claude 창을 만들지 않았습니다.");
            checks["ctrlNStartsAtOnce"] = true;
            // 새 창은 활성 창이 있는 탭 그룹에 붙고, 다른 그룹이 보여 주던 탭은 그대로다 (M/AppStore.swift:520).
            string a = Wire.Id(), b = Wire.Id(), c = Wire.Id(), z = Wire.Id(), space = Wire.Id();
            RunSession Fixture(string id) => new() { Id = id, WorkspaceId = space };
            var tree = new PaneLayoutNode { Kind = "split", Axis = "horizontal", Children = [new() { SessionIds = [a, b, c], SelectedSessionId = b }, new() { SessionIds = [z], SelectedSessionId = z }] };
            var laid = service.Snapshot with { Sessions = [Fixture(a), Fixture(b), Fixture(c), Fixture(z)], ActiveWorkspaceId = space, ActiveSessionId = z, PaneLayouts = new() { [space] = tree } };
            var joining = Fixture(Wire.Id());
            try
            {
                var placed = AddToLayout(laid, space, joining, null);
                var groups = PaneLayout.Groups(placed.PaneLayouts![space]).ToList();
                Require(placed.ActiveSessionId == joining.Id && groups.Count == 2 && groups[0].SessionIds.SequenceEqual(new[] { a, b, c }) && groups[0].SelectedSessionId == b
                    && groups[1].SessionIds.SequenceEqual(new[] { z, joining.Id }) && groups[1].SelectedSessionId == joining.Id,
                    "A new pane must join the active pane's tab group and leave the tab every other group shows; got " + string.Join(" | ", groups.Select(g => g.SessionIds.Count + " tabs, showing #" + g.SessionIds.IndexOf(g.SelectedSessionId ?? ""))));
            }
            finally { layoutDefaults.Remove(space); }
            checks["newPaneJoinsActiveGroup"] = true;
        }
        finally
        {
            smokeResumeDialog = null;
            foreach (var id in service.Snapshot.Sessions.Where(s => !before.Contains(s.Id)).Select(s => s.Id).ToList()) await CloseSession(id);
            await SelectWorkspace(workspace.Id);
        }
        return checks;
    }
    /// <summary>Longer than a UI wait: for a step that first reads the CLIs again, which takes as long as starting each of them does.</summary>
    private const double RuntimeReadWait = 30;
    private static async Task WaitUI(Func<bool> predicate, [CallerArgumentExpression(nameof(predicate))] string condition = "", double seconds = 4)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!predicate()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException("WinUI 검증 상태 대기 시간이 초과되었습니다: " + condition); await Task.Delay(20); }
    }

    private sealed partial class PaneView
    {
        // 브라우저 창을 실제 WebView2로 몰아 본다. 두 페이지는 임시 폴더의 HTML 파일이고,
        // SetVirtualHostNameToFolderMapping으로 https://mighty-smoke.invalid/ 아래에 비춘다.
        // .invalid는 실제 사이트가 쓸 수 없는 이름이고 매핑은 DNS를 거치지 않으므로 망은
        // 건드리지 않으면서, data: 주소와 달리 엔진이 진짜 세션 기록을 남기는 https 페이지다.
        // 탐색은 주소줄과 같은 길(NavigateBrowser)로, 뒤로 가기는 앱의 뒤로 단추와 같은 길로
        // 부른다. 각 단계는 그 탐색의 NavigationCompleted를 기다린 뒤 앱이 BrowserHistory에
        // 적은 주소를 BrowserHistory와 같은 Uri 비교로 확인한다. 마지막으로 첫 페이지의 스크립트가
        // window.open()을 부르면 NewWindowRequested가 와서 Handled로 끝나야 하고 새 창은
        // 없어야 한다. 실패하면 기록된 주소와 본 탐색 사건을 메시지에 싣는다.
        internal async Task<(bool Navigated, bool BackWorked, bool PopupBlocked)> DriveBrowserSmokeAsync()
        {
            const string host = "mighty-smoke.invalid";
            var first = new Uri($"https://{host}/one.html");
            var second = new Uri($"https://{host}/two.html");
            var view = webView ?? throw new InvalidOperationException("WebView2 컨트롤이 없습니다.");
            var core = view.CoreWebView2;
            var pages = Path.Combine(Path.GetTempPath(), "MightyBrowserSmoke_" + Wire.Id());
            Directory.CreateDirectory(pages);
            await File.WriteAllTextAsync(Path.Combine(pages, "one.html"),
                "<!doctype html><html><head><meta charset=\"utf-8\"><title>MightyBrowserSmokeOne</title></head>"
                + "<body><p>MightyBrowserSmokeOne</p><script>function mightyOpenPopup() { window.open('two.html'); return 'opened'; }</script></body></html>");
            await File.WriteAllTextAsync(Path.Combine(pages, "two.html"),
                "<!doctype html><html><head><meta charset=\"utf-8\"><title>MightyBrowserSmokeTwo</title></head>"
                + "<body><p>MightyBrowserSmokeTwo</p></body></html>");
            core.SetVirtualHostNameToFolderMapping(host, pages, Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Deny);

            var events = new List<string>();
            core.NavigationStarting += (_, a) => events.Add($"starting#{a.NavigationId} {a.Uri}");
            core.SourceChanged += (_, _) => events.Add($"source {core.Source}");
            core.NavigationCompleted += (_, a) => events.Add($"completed#{a.NavigationId} ok={a.IsSuccess} status={a.WebErrorStatus}");
            core.ProcessFailed += (_, a) => events.Add($"processFailed {a.ProcessFailedKind}");
            var popupRequests = new List<(bool UserInitiated, bool Handled, string Uri)>();
            // 앱의 처리기가 먼저 등록돼 있으므로 여기서는 앱이 Handled로 끝냈는지를 본다.
            core.NewWindowRequested += (_, a) => popupRequests.Add((a.IsUserInitiated, a.Handled, a.Uri));

            string Diagnose(string step) =>
                $"{step}: 기록된 주소={browserHistory?.Current?.OriginalString ?? "(없음)"}, 엔진 주소={core.Source}, "
                + $"엔진 뒤로={core.CanGoBack}, 엔진 앞으로={core.CanGoForward}, 사건=[{string.Join(" | ", events)}]";

            // 앱이 기록에 쓰는 것과 같은 엔진 NavigationCompleted를 기다린다. 이어서 할 일은
            // 사건 처리가 모두 끝난 뒤에 돌므로(RunContinuationsAsynchronously), 그때는 앱이
            // 이미 BrowserHistory에 주소를 적은 뒤다.
            async Task NavigateAndWait(string step, Action start, Uri expected)
            {
                var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnCompleted(Microsoft.Web.WebView2.Core.CoreWebView2 _, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs a) => done.TrySetResult(a.IsSuccess);
                core.NavigationCompleted += OnCompleted;
                try
                {
                    start();
                    if (await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(30))) != done.Task)
                        throw new TimeoutException("브라우저 탐색이 30초 안에 끝나지 않았습니다. " + Diagnose(step));
                    Require(done.Task.Result, "브라우저 탐색이 실패했습니다. " + Diagnose(step));
                    Require(browserHistory?.Current == expected, "브라우저 기록 주소가 탐색한 페이지와 다릅니다. " + Diagnose(step));
                    Require(core.Source == expected.AbsoluteUri, "엔진 주소가 탐색한 페이지와 다릅니다. " + Diagnose(step));
                }
                finally { core.NavigationCompleted -= OnCompleted; }
            }

            try
            {
                await NavigateAndWait("첫 페이지", () => NavigateBrowser(first), first);
                await NavigateAndWait("둘째 페이지", () => NavigateBrowser(second), second);
                var navigated = browserHistory?.State(false) is { CanGoBack: true, CanGoForward: false } && core.CanGoBack;
                Require(navigated, "두 페이지를 탐색한 뒤 뒤로 갈 수 없습니다. " + Diagnose("둘째 페이지"));

                await NavigateAndWait("뒤로 가기", BrowserGoBack, first);
                var backWorked = browserHistory?.State(false) is { CanGoForward: true } && core.CanGoForward;

                // 첫 페이지에 실린 스크립트가 window.open()을 부른다. 함수가 있다는 것은 첫 페이지가
                // 로컬 폴더에서 실제로 읽혀 살아 있다는 뜻이기도 하다.
                var opened = await core.ExecuteScriptAsync("typeof mightyOpenPopup === 'function' ? mightyOpenPopup() : 'missing'");
                Require(opened == "\"opened\"", "첫 페이지의 window.open() 스크립트가 돌지 않았습니다: " + opened + " " + Diagnose("팝업"));
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (popupRequests.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(20);
                Require(popupRequests.Count > 0, "window.open()이 NewWindowRequested를 일으키지 않았습니다. " + Diagnose("팝업"));
                var popupBlocked = popupRequests.All(r => r.Handled) && browserHistory?.Current == first && core.CanGoForward;
                Require(popupBlocked, "팝업 요청이 막히지 않았습니다: " + string.Join(", ", popupRequests.Select(r => $"{r.Uri} handled={r.Handled}")) + " " + Diagnose("팝업"));
                return (navigated, backWorked, popupBlocked);
            }
            finally
            {
                core.ClearVirtualHostNameToFolderMapping(host);
                try { Directory.Delete(pages, true); } catch { }
            }
        }

        internal async Task<Dictionary<string, object?>> RunComposerSmoke()
        {
            var checks = new Dictionary<string, object?>();
            Require(SubmitKeyAllowed(false, false, false) && !SubmitKeyAllowed(true, false, false) && !SubmitKeyAllowed(false, true, false) && !SubmitKeyAllowed(false, false, true), "Enter/Shift+Enter/IME key policy is invalid.");
            input.Text = "한글 첫 입력"; await WaitUI(() => Session.Draft == input.Text);
            composingInput = true; var nativeInput = input; Refresh(); Require(ReferenceEquals(input, nativeInput) && input.Text == "한글 첫 입력", "상태 갱신이 조합 중인 입력 컨트롤을 변경했습니다."); composingInput = false;
            input.Text = ""; Container.UpdateLayout(); await Task.Delay(40); var singleHeight = input.ActualHeight;
            input.Text = "첫째 줄\n둘째 줄\n셋째 줄\n" + string.Concat(Enumerable.Repeat("자동 줄바꿈 ", 30)); Container.UpdateLayout();
            // 한 줄은 20, 여섯 줄에서 멈춘다 (M/NativeComposerEditor.swift:32-33, M/SessionPaneView.swift:579).
            Require(Math.Abs(singleHeight - ComposerLine) < .6 && input.MaxHeight == ComposerLine + 5 * ComposerLineStep, $"입력창 한 줄 높이는 {ComposerLine}, 최대 여섯 줄이어야 합니다: {singleHeight:F1}, 최대 {input.MaxHeight:F1}");
            await WaitUI(() => input.ActualHeight > singleHeight && input.ActualHeight <= input.MaxHeight + .6);
            input.Text = ""; Container.UpdateLayout(); await WaitUI(() => input.ActualHeight <= singleHeight + 1);
            checks["nativeEditorRetainedAndAutoHeight"] = true;
            var doc = output.View.Document; doc.GetText(TextGetOptions.None, out var text);
            Require(output.View.IsReadOnly, "출력 갱신 후 읽기 전용 상태가 복원되지 않았습니다.");
            var start = text.IndexOf("첫 번째", StringComparison.Ordinal); var end = text.IndexOf("두 번째", StringComparison.Ordinal) + "두 번째 문단".Length;
            Require(start >= 0 && end > start && !text.Contains("**문단**", StringComparison.Ordinal) && text.Contains(Locale.Get("run.activity.durationSeconds", new Dictionary<string, string> { ["seconds"] = "12.3" }), StringComparison.Ordinal), "Markdown 또는 도구 경과시간이 표시되지 않았습니다.");
            doc.Selection.SetRange(start, end); doc.Selection.GetText(TextGetOptions.None, out var selected);
            await Change(p => p with { Logs = p.Logs.Append(new LogEntry(Wire.Id(), "assistant", "추가 응답", Wire.Now(), p.Provider)).ToList() }); Refresh();
            doc.Selection.GetText(TextGetOptions.None, out var retained); Require(selected == retained, "새 출력이 여러 문단의 선택 범위를 바꿨습니다."); Require(output.View.IsReadOnly, "추가 출력 후 읽기 전용 상태가 복원되지 않았습니다."); checks["crossParagraphSelectionSurvivesAppend"] = true;
            Container.Width = 315; Container.UpdateLayout(); await WaitUI(() => Math.Abs(Container.ActualWidth - 315) < 1); ArrangeComposer(); Container.UpdateLayout(); await Task.Delay(40);
            var controls = selectors.Children.OfType<FrameworkElement>().Concat(toolbarActions.Children.OfType<FrameworkElement>()).Where(c => c.Visibility == Visibility.Visible).ToArray();
            var centers = controls.Select(c => c.TransformToVisual(Container).TransformPoint(new(0, 0)).Y + c.ActualHeight / 2).ToArray();
            Require(controls.Contains(context) && controls.Contains(sendHost) && controls.Contains(statusLineToggle), "입력창 도구 줄에 컨텍스트 링, 상태줄 토글, 전송 버튼이 모두 있어야 합니다.");
            Require(controls.All(c => Math.Abs(c.ActualHeight - 32) < 1 && c.TransformToVisual(Container).TransformPoint(new(c.ActualWidth, 0)).X <= Container.ActualWidth + 1) && centers.Max() - centers.Min() < 1, "좁은 입력창의 버튼이 한 줄 안에 맞지 않습니다.");
            // 좁은 패널은 모델 필과 옵션 메뉴 하나로 접힌다 (M/ComposerControls.swift:48-55, M/SessionPaneView.swift:713-720); 필은 오른쪽 묶음 밑으로 들어가지 않는다.
            var pillsEnd = selectors.TransformToVisual(toolbar).TransformPoint(new(selectors.ActualWidth, 0)).X; var clusterStart = toolbarActions.TransformToVisual(toolbar).TransformPoint(new(0, 0)).X;
            Require(toolbarStyle == ToolbarStyle.Overflow && options.Visibility == Visibility.Visible && more.Visibility == Visibility.Collapsed && permission.Visibility == Visibility.Collapsed && effort.Visibility == Visibility.Collapsed && pillsEnd <= clusterStart + .5,
                $"좁은 입력창은 첨부, 모델, 옵션 메뉴만 보여야 합니다: {toolbarStyle}, 필 끝 {pillsEnd:F1}, 오른쪽 묶음 시작 {clusterStart:F1}");
            // 세 단계의 경계는 Mac의 식 그대로다: 필 = 글자(상한) + 16 + 14 + 5 (+ 12 chevron), 전체 = 64 + 모델(155) + 권한(90) + 강도(48) + 간격, 축약 = 32 x (개수 - 1) + 모델(90) + 간격.
            double Words(string text, double cap) => Math.Min(PillTextWidth(text), cap);
            var fullWidth = 64 + (Words("Opus 4.7", 155) + 47) + (Words("Auto mode", 90) + 47) + (Words("High", 48) + 47) + 4 * 6; var compactWidth = 32 * 4 + (Words("Opus 4.7", 90) + 47) + 4 * 6;
            Require(StyleFor(fullWidth + 4, "Opus 4.7", "High", "Auto mode", false) == ToolbarStyle.Full && StyleFor(fullWidth + 3, "Opus 4.7", "High", "Auto mode", false) == ToolbarStyle.Compact
                && StyleFor(compactWidth + 4, "Opus 4.7", "High", "Auto mode", false) == ToolbarStyle.Compact && StyleFor(compactWidth + 3, "Opus 4.7", "High", "Auto mode", false) == ToolbarStyle.Overflow
                && ModelTextCap(ToolbarStyle.Overflow, 171) == 48 && ModelTextCap(ToolbarStyle.Overflow, 400) == 110,
                $"입력창 도구 줄의 단계 경계가 Mac과 다릅니다: 전체 {fullWidth:F1}, 축약 {compactWidth:F1}");
            checks["compactControls32pxSingleRow"] = true; Container.Width = double.NaN; Container.UpdateLayout();
            var oldFile = AttachmentSupport.Make("old.txt", "old"u8.ToArray()); var newFile = AttachmentSupport.Make("next.txt", "next"u8.ToArray()); pendingAttachments.Add(oldFile); RefreshAttachments();
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0; StartRunRequest? submitted = null;
            owner.smokeStart = async request => { calls++; submitted = request; await Change(p => p with { Status = "running" }); await gate.Task; await Change(p => p with { Status = "completed" }); };
            input.Text = "전송할 요청"; await WaitUI(() => Session.Draft == input.Text); RefreshComposerState();
            var sending = Send(); await WaitUI(() => calls == 1 && Session.Status == "running");
            Require(sendSymbol == "stop" && sendStop.View.Visibility == Visibility.Visible && send.IsEnabled && !canSend, "실행 중 단일 버튼이 중지로 바뀌지 않았습니다.");
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
                await SettleDesktopCapture(owner.root); await CaptureElement(Container, Path.Combine(owner.options.ProfileDirectory!, "smoke-composer-slash.png"));

                input.Text = "/rev";
                // Description matches are part of the palette contract. In
                // English, "previous" also matches /rev in the clear command.
                var partialMatches = SlashCommandCatalog.Filter([.. SlashPalette.Builtins(Session.Provider), .. fixture], "rev").Select(c => c.Invocation).ToArray();
                await WaitUI(() => paletteState.Commands.Select(c => c.Invocation).SequenceEqual(partialMatches));
                Require(paletteState.Commands[0].Invocation == "review" && slashRows.Children.Count == partialMatches.Length,
                    "Partial search must keep the invocation prefix first and include localized description matches.");
                input.Text = "/review";
                await WaitUI(() => paletteState.Commands.Select(c => c.Invocation).SequenceEqual(["review"]));
                Require(paletteState.Commands[0].Invocation == "review", "입력에 따른 슬래시 팔레트 필터가 잘못됐습니다.");
                checks["typingFilters"] = true;

                input.Text = "/";
                await WaitUI(() => paletteState.Commands.Length == expected);
                Require(HandlePaletteKey(Windows.System.VirtualKey.Down) && paletteState.SafeIndex == 1, "↓ 키가 다음 줄로 이동하지 않았습니다.");
                Require(HandlePaletteKey(Windows.System.VirtualKey.Up) && paletteState.SafeIndex == 0, "↑ 키가 이전 줄로 이동하지 않았습니다.");
                Require(HandlePaletteKey(Windows.System.VirtualKey.Up) && paletteState.SafeIndex == expected - 1, "↑ 키가 마지막 줄로 넘어가지 않았습니다.");
                checks["arrowsMoveHighlight"] = true;

                input.Text = "/review";
                await WaitUI(() => paletteState.Commands.Select(c => c.Invocation).SequenceEqual(["review"]));
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
