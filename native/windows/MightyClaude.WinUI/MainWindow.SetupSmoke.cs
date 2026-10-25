using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    /// <summary>
    /// 필요한 도구 over fixed probe results (M/AppStore+SetupSmoke.swift): nothing on this PC is probed. The screen opens in the page
    /// focused on Antigravity, its rows read as the probes say, Git and the focused CLI start ticked and gh is picked, the plan
    /// is in install order, the confirmation lists the first lines and every command and is cancelled, and no terminal opens. The
    /// welcome offers the screen, a pane blocked for a missing CLI offers its install, and the screen holds back nothing else.
    /// </summary>
    private async Task<Dictionary<string, object?>> RunSetupSmoke()
    {
        var checks = new Dictionary<string, object?>();
        var catalog = LoadSetupCatalog() ?? throw new InvalidOperationException(setupMessage ?? "the app carries no setup catalog");
        var heldRuntime = runtime; var openedBefore = setupTerminalsOpened;
        var fixedResults = catalog.Items.Where(item => item.Entry(SetupPlatform.Windows) is not null).ToDictionary(item => item.Id, _ => SetupProbeResult.Missing, StringComparer.Ordinal);
        fixedResults["claude"] = new(SetupProbeState.Outdated, "2.0.0 (Claude Code)", "2.1.271");
        fixedResults["codex"] = SetupProbeResult.Installed("codex-cli 0.160.0");
        setupFixedResults = fixedResults; setupResults.Clear(); setupSelection.Clear(); setupDidPreselect = false;
        try
        {
            await RefreshSetup();
            Require(SetupFirstRun.ShouldPresent(catalog, setupResults, false), "a missing Git must open the screen at launch");
            Require(!SetupFirstRun.ShouldPresent(catalog, setupResults, true), "never-show-again must keep the screen closed");
            checks["firstRunDecision"] = true;
            Require(SettingsElements(BuildWelcome()).Any(element => AutomationProperties.GetAutomationId(element) == "welcome-setup"), "the welcome must offer the setup screen while Git is missing");
            checks["welcomeButton"] = true;

            // The screen in the page: its list is the form the page reads, hosted under the page (MainWindow.WebShell.Settings.cs).
            OpenSetup("agy");
            await WaitUI(() => webSurfaces.ContainsKey("setup") && setupDidPreselect && !setupProbing);
            ToggleSetupSelection("gh");
            var frame = webNativeHost ?? throw new InvalidOperationException("the page hosts no form");
            frame.UpdateLayout();
            Require(!dialogOpen, "the setup screen must hold back neither the phone nor the sign-in recovery");
            T Element<T>(string id) where T : FrameworkElement => SettingsElements(frame).OfType<T>().FirstOrDefault(element => AutomationProperties.GetAutomationId(element) == id)
                ?? throw new InvalidOperationException("the setup screen has no " + id);
            var expected = new Dictionary<string, SetupRowState>
            {
                ["claude"] = SetupRowState.Outdated, ["codex"] = SetupRowState.Installed, ["agy"] = SetupRowState.Missing, ["git"] = SetupRowState.Missing,
                ["node"] = SetupRowState.Missing, ["gh"] = SetupRowState.Missing, ["tailscale"] = SetupRowState.Missing, ["glab"] = SetupRowState.Missing,
            };
            foreach (var (id, state) in expected)
                Require(AutomationProperties.GetItemStatus(Element<Grid>("setup-row-" + id)) == state.ToString(), $"the {id} row must read {state}; got {AutomationProperties.GetItemStatus(Element<Grid>("setup-row-" + id))}");
            Require(!SettingsElements(frame).Any(element => AutomationProperties.GetAutomationId(element) is "setup-row-homebrew" or "setup-row-gstack" or "setup-row-bun"), "the Mac's tools must not be listed");
            checks["rows"] = true;
            // Git starts ticked, the focused CLI and the picked tool are ticked, glab is not; a Claude below its minimum offers its update, Tailscale its page.
            var ticks = new[] { "git", "agy", "gh", "glab" }.ToDictionary(id => id, id => Element<CheckBox>("setup-check-" + id).IsChecked == true);
            Require(ticks["git"] && ticks["agy"] && ticks["gh"] && !ticks["glab"], "checkboxes: " + string.Join(", ", ticks.Select(pair => pair.Key + " " + pair.Value)));
            Element<Button>("setup-update-claude"); Element<Button>("setup-page-tailscale");
            Require(Element<Button>("setup-install").IsEnabled, "the install button must be enabled with a plan");
            checks["actions"] = true;
            Require(SetupPlanIds.SequenceEqual(["agy", "git", "gh"]), "plan order " + string.Join(",", SetupPlanIds));
            checks["planOrder"] = SetupPlanIds.ToList();

            // The confirmation's content is read and it is not answered: nothing is written and no window opens.
            var dialog = BuildSetupConfirmation(root.XamlRoot, out var steps) ?? throw new InvalidOperationException("the plan has no steps to confirm");
            Require(steps.Select(step => step.ItemId).SequenceEqual(["agy", "git", "gh"]), "confirmation steps " + string.Join(",", steps.Select(step => step.ItemId)));
            Require(steps.All(step => !step.Body.Contains("RunAs", StringComparison.OrdinalIgnoreCase) && !step.Body.Contains("sudo", StringComparison.Ordinal)), "a step elevates");
            var preamble = SettingsElements((FrameworkElement)dialog.Content).OfType<StackPanel>().First(element => AutomationProperties.GetAutomationId(element) == "setup-confirm-preamble");
            Require(preamble.Children.OfType<TextBlock>().Last().Text.StartsWith("Set-Location -LiteralPath $HOME", StringComparison.Ordinal), "the confirmation must show the working-folder line first");
            Require(steps.All(step => SettingsElements((FrameworkElement)dialog.Content).Any(element => AutomationProperties.GetAutomationId(element) == "setup-confirm-step-" + step.ItemId)), "the confirmation must list every step");
            checks["confirmation"] = steps.Select(step => new Dictionary<string, object?> { ["item"] = step.ItemId, ["command"] = step.Body }).ToList();
            Require(setupRun is null && setupTerminalsOpened == openedBefore, "a cancelled confirmation must start no install and open no window");
            checks["cancelledWithoutTerminal"] = true;

            // A pane whose CLI is missing offers its install; an old Claude does not (it gets the update).
            if (heldRuntime is not null)
            {
                runtime = ProviderCatalog.With(heldRuntime, heldRuntime.Providers.First(provider => provider.Id == "gemini") with { Available = false, Version = null });
                Require(SetupItemForMissingCli(new RunSession { Kind = "claude", Provider = "gemini" }) == "agy" && SetupItemForMissingCli(new RunSession { Kind = "shell", Provider = "gemini" }) is null,
                    "an agent pane blocked for a missing Antigravity must offer its install, a shell never");
                runtime = ProviderCatalog.With(heldRuntime, heldRuntime.Providers.First(provider => provider.Id == "claude") with { Available = false, Version = "2.0.0" });
                Require(SetupItemForMissingCli(new RunSession { Kind = "claude", Provider = "claude" }) is null, "an old Claude gets its update, not an install");
                checks["blockedPaneInstall"] = true;
            }

            webSurfaces["setup"].Button?.Invoke(null);
            checks["passed"] = true;
            return checks;
        }
        finally
        {
            runtime = heldRuntime;
            if (webSurfaces.TryGetValue("setup", out var open)) open.Button?.Invoke(null);
            setupFixedResults = null; setupResults.Clear(); setupSelection.Clear(); setupDidPreselect = false; setupFocus = null;
        }
    }
}
