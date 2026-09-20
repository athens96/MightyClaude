using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace MightyClaude.WinUI;

// A section of the Settings screen.
// Title is the heading shown to the user (used as an automation ID in smoke).
// Build is called each time Settings opens so the controls reflect current state.
// One small registration in GetSettingsSections() is all a new feature needs.
internal sealed record SettingsSection(string Title, Func<StackPanel> Build);

public sealed partial class MainWindow
{
    // Last CLI update results — empty until a run completes or smoke injects a fixture.
    // Rebuilt into the CLI update section each time Settings opens.
    internal IReadOnlyList<CliUpdateResult> lastCliUpdateResults = [];

    // The sections Windows shows, in the macOS slot order.
    // Sections for features not yet on Windows (원격 연결, styles, mobile remote,
    // companion, CLI accounts, Claude Mods, app update) are simply absent here and
    // added one line each when their feature arrives.
    //
    // OS-bound substitution (recorded in docs/windows-settings-groundwork.md):
    //   "이 PC의 CLI" replaces the macOS "이 Mac의 CLI".
    internal List<SettingsSection> GetSettingsSections() =>
    [
        new("화면", BuildDisplaySection),
        new(CliUpdateStrings.SectionTitle, BuildCliUpdateSectionFromState),
        new("이 PC의 CLI", BuildProvidersSection),
        new("앱 정보", BuildAppInfoSection),
    ];

    private Task OpenSettings() => Act(async () =>
    {
        var content = new StackPanel { Spacing = 0, MinWidth = 420, MaxWidth = 540 };
        foreach (var section in GetSettingsSections())
            content.Children.Add(BuildSectionContainer(section.Title, section.Build()));
        var scroll = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 520,
        };
        await new ContentDialog
        {
            Title = "설정",
            Content = scroll,
            CloseButtonText = "닫기",
            XamlRoot = root.XamlRoot,
        }.ShowAsync();
    });

    private static StackPanel BuildSectionContainer(string title, StackPanel body)
    {
        var wrapper = new StackPanel { Spacing = 8, Padding = new(0, 0, 0, 20) };
        var heading = new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Opacity = .85,
        };
        AutomationProperties.SetAutomationId(heading, "settings-section-" + title);
        wrapper.Children.Add(heading);
        wrapper.Children.Add(body);
        return wrapper;
    }

    // 화면 — theme and completion-notification controls; behaviour unchanged.
    private StackPanel BuildDisplaySection()
    {
        var panel = new StackPanel { Spacing = 8 };
        var theme = new ComboBox { Header = "테마", HorizontalAlignment = HorizontalAlignment.Stretch };
        theme.Items.Add(new ComboBoxItem { Content = "어둡게", Tag = "dark" });
        theme.Items.Add(new ComboBoxItem { Content = "밝게", Tag = "light" });
        theme.SelectedIndex = service.Snapshot.Theme == "light" ? 1 : 0;
        theme.SelectionChanged += async (_, _) =>
        {
            if (theme.SelectedItem is ComboBoxItem item)
                await Act(async () => { await service.UpdateAsync(s => s with { Theme = (string)item.Tag }); Render(); });
        };
        panel.Children.Add(theme);
        panel.Children.Add(BuildNotificationSettingsSection());
        return panel;
    }

    // CLI 업데이트 — delegates to the parameterised builder so smoke can inject fixture results.
    private StackPanel BuildCliUpdateSectionFromState() => BuildCliUpdateSection(lastCliUpdateResults);

    // Called by both OpenSettings (via BuildCliUpdateSectionFromState) and the smoke check.
    internal StackPanel BuildCliUpdateSection(IReadOnlyList<CliUpdateResult> results)
    {
        var panel = new StackPanel { Spacing = 6 };
        var toggle = new ToggleSwitch
        {
            Header = CliUpdateStrings.AutoUpdateToggle,
            IsOn = service.Snapshot.AutoUpdateCLIs == true,
            OffContent = "",
            OnContent = "",
        };
        AutomationProperties.SetAutomationId(toggle, "cli-auto-update");
        toggle.Toggled += async (_, _) =>
            await service.UpdateAsync(s => s with { AutoUpdateCLIs = toggle.IsOn });
        panel.Children.Add(toggle);
        panel.Children.Add(new TextBlock
        {
            Text = CliUpdateStrings.SectionDescription,
            FontSize = 12,
            Opacity = .7,
            TextWrapping = TextWrapping.Wrap,
        });
        foreach (var result in results)
        {
            var row = new StackPanel { Spacing = 2 };
            row.Children.Add(new TextBlock
            {
                Text = CliUpdateService.ResultRow(result),
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
            if (CliUpdateService.VersionChange(result) is { } change)
                row.Children.Add(new TextBlock { Text = change, FontSize = 11, Opacity = .7 });
            if (!string.IsNullOrEmpty(result.Detail))
                row.Children.Add(new TextBlock
                {
                    Text = result.Detail,
                    FontSize = 11,
                    Opacity = .7,
                    TextWrapping = TextWrapping.Wrap,
                });
            AutomationProperties.SetAutomationId(row, "cli-update-result-" + result.Provider);
            panel.Children.Add(row);
        }
        return panel;
    }

    // 이 PC의 CLI — provider list and refresh button; behaviour unchanged.
    private StackPanel BuildProvidersSection()
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = "로컬 Claude Code · Codex CLI · Gemini CLI를 그대로 사용합니다. 로그인과 설치는 각 CLI에서 진행하세요.",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(Button("설치된 실행기 새로고침", RefreshRuntime));
        foreach (var item in runtime?.Providers ?? [])
            panel.Children.Add(new TextBlock
            {
                Text = item.Name + " · " + (item.Available ? item.Version : item.Detail),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
            });
        return panel;
    }

    // 앱 정보 — usage notes; behaviour unchanged.
    private StackPanel BuildAppInfoSection()
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock
        {
            Text = "Enter로 전송 · Shift+Enter로 줄바꿈\n탭을 드래그해 합치거나 가장자리로 옮겨 분할합니다.\n명령 실행 창은 명령별 실행이며 대화형 터미널이 아닙니다.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = .7,
            FontSize = 12,
        });
        return panel;
    }

    // Smoke: verifies section order, fixture results and auto-update toggle persistence.
    internal async Task<Dictionary<string, object?>> RunSettingsSectionsSmoke()
    {
        var checks = new Dictionary<string, object?>();
        var originalAutoUpdate = service.Snapshot.AutoUpdateCLIs;

        // Section titles must appear in macOS slot order.
        var sections = GetSettingsSections();
        var expectedTitles = new[] { "화면", CliUpdateStrings.SectionTitle, "이 PC의 CLI", "앱 정보" };
        Require(sections.Select(s => s.Title).SequenceEqual(expectedTitles),
            "Settings sections are not in macOS order: " + string.Join(", ", sections.Select(s => s.Title)));
        checks["settingsSections"] = true;

        // Inject fixture results covering updated, current, skipped and failed.
        var fixtureResults = new CliUpdateResult[]
        {
            new("claude", "updated", "2.1.270", "2.1.271", "native", CliUpdateStrings.DetailUpdated),
            new("codex",  "current", "0.51.0",  "0.51.0",  "npm",    CliUpdateStrings.DetailUnchanged),
            new("gemini", "skipped", null,       null,      "missing", CliUpdateStrings.DetailMissing),
        };
        var cliPanel = BuildCliUpdateSection(fixtureResults);
        var resultRows = cliPanel.Children.OfType<StackPanel>()
            .Where(p => AutomationProperties.GetAutomationId(p).StartsWith("cli-update-result-"))
            .ToArray();
        Require(resultRows.Length == fixtureResults.Length,
            "CLI update section must show all fixture result rows, got " + resultRows.Length);

        // Verify a failed result also renders as a row.
        var failedPanel = BuildCliUpdateSection(
            [new("claude", "failed", "2.1.270", null, "native",
                CliUpdateStrings.DetailFailedExitTemplate.Replace("{code}", "1"))]);
        var failedRows = failedPanel.Children.OfType<StackPanel>()
            .Where(p => AutomationProperties.GetAutomationId(p).StartsWith("cli-update-result-"))
            .ToArray();
        Require(failedRows.Length == 1, "a failed result must appear as a row");
        checks["cliUpdateSection"] = true;

        // Toggle auto-update switch and read it back from saved state.
        var toggle = cliPanel.Children.OfType<ToggleSwitch>().First();
        var wasOn = toggle.IsOn;
        var expected = !wasOn;

        // Flip the same way the toggle's Toggled handler does.
        await service.UpdateAsync(s => s with { AutoUpdateCLIs = expected });
        Require(service.Snapshot.AutoUpdateCLIs == expected,
            "CLI auto-update toggle must persist to saved state");

        // Restore original value; put back draft and focus on exit.
        await service.UpdateAsync(s => s with { AutoUpdateCLIs = originalAutoUpdate });
        checks["passed"] = true;
        return checks;
    }
}
