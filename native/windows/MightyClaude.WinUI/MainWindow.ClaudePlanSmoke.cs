using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private sealed partial class PaneView
    {
        /// <summary>
        /// The bundled plan style on a fake pane (docs/mighty-styles.md §1.17): the phase bar goes plan → approve → execute
        /// with a fake plan, plan answer, checklist and background work, the progress widget and the task list draw, a new
        /// request starts in plan mode, and the background strip shows outside the style. A waiting plan's answers sit in the
        /// style's panel while the diagram draws the plan, and on the docked card in the timeline. No CLI runs; the pane is restored.
        /// </summary>
        internal async Task<Dictionary<string, object?>> RunClaudePlanStyleSmoke()
        {
            var checks = new Dictionary<string, object?>();
            var original = Session; var draft = input.Text; var savedPrerequisites = stylePrerequisites;
            var todos = new TodoProgress(Enumerable.Range(1, 7).Select(i => new TodoItem("step " + i, i <= 3 ? "completed" : i == 4 ? "in_progress" : "pending", "doing step " + i)).ToList());
            var work = new BackgroundWork([
                new BackgroundTask("smoke-bg-1", "agent", "review the diff", Wire.Now()),
                new BackgroundTask("smoke-bg-2", "shell", "npm test", Wire.Now(), "completed", EndedAt: Wire.Now()),
            ], TurnEnded: true);
            MightyGraphRun Run(string status) => new() { Id = "smoke-plan-run", Input = "plan smoke", Status = status, SourceRunID = "smoke-plan-process" };
            var approved = new PlanRecord("smoke-plan-style", "smoke-plan-process", "# Smoke plan", Wire.Now(), Wire.Now(), PlanOutcome.ApprovedAuto, GraphRunId: "smoke-plan-run");
            // The phase bar's current phase is the one drawn semibold (MainWindow.Styles.cs RenderGuidedStyle).
            string? CurrentPhase() => FindById<StackPanel>(guidedBody, "mighty-phases-" + id)?.Children.OfType<TextBlock>()
                .FirstOrDefault(t => t.FontWeight.Weight == Microsoft.UI.Text.FontWeights.SemiBold.Weight)?.Text;
            string Title(string phase) => activeStyle!.Manifest.Phases.Single(p => p.Id == phase).Title;
            bool HasText(string automation, string text) => FindById<StackPanel>(guidedBody, automation + id) is { } host
                && VisualDescendants(host).OfType<TextBlock>().Any(t => t.IsLoaded && t.Text == text);
            try
            {
                await Change(p => p with { Provider = "claude", AgentViewMode = "mighty", MightyStyle = "claude-plan", MightyStyleHash = null, MightyStyleSince = null, Status = "completed", GraphRuns = [], PlanHistory = null, TodoProgress = null, BackgroundWork = null });
                ClearToolPermissions(); Refresh();
                await WaitUI(() => !styleLoading);
                await LoadStyles();
                Require(activeStyle is { Id: "claude-plan" }, "the bundled plan style did not resolve");
                stylePrerequisites = new(true, [], null, null); RenderGuidedStyle();
                await WaitUI(() => CurrentPhase() == Title("plan"));
                Require(styleActionButtons.ContainsKey("new-plan") && !styleActionButtons.ContainsKey("verify"), "planning offers a new plan only");
                checks["planning"] = CurrentPhase();

                // A plan waits for its answer: the approve phase. The diagram draws the plan, so under the style its answers wait in
                // the style's panel, as a question does (M/GuidedPanel.swift:115-119, PlanCardSupport.ComposerPlace), with no chips
                // and nothing docked over the composer.
                await Change(p => p with { GraphRuns = [Run("running")], Status = "running", GraphViewMode = "diagram" });
                ReceiveToolPermission(SmokePlan("smoke-plan-style")); Refresh();
                await WaitUI(() => CurrentPhase() == Title("approve"));
                await WaitUI(() => FindById<Border>(guidedBody, "plan-actions-" + id) is not null && FindById<Button>(guidedBody, "plan-approve-auto-" + id) is { IsLoaded: true });
                Require(styleActionButtons.Count == 0 && FindById<ProgressRing>(guidedBody, "mighty-progress-" + id) is null, "awaiting approval in the diagram shows the plan's answers in the style panel, not chips or progress");
                Require(planDockCard is null && toolPermissionHost.Visibility == Visibility.Collapsed && FindById<Button>(toolPermissionHost, "plan-approve-auto-" + id) is null, "the plan's answers must not also dock over the composer");
                foreach (var automation in new[] { "plan-cancel", "plan-revise", "plan-approve-confirm", "plan-approve-auto" })
                    Require(FindById<Button>(guidedBody, automation + "-" + id) is not null, "the answers in the style panel must have " + automation);
                // In the timeline the diagram does not draw the plan: the whole card docks over the composer, and the style panel
                // shows the sequence in flight with no chips.
                await Change(p => p with { GraphViewMode = "timeline" }); Refresh(); RefreshMightyView(Session);
                await WaitUI(() => planDockCard is not null && toolPermissionHost.Visibility == Visibility.Visible && toolPermissionHost.Children.Contains(planDockCard) && FindById<ProgressRing>(guidedBody, "mighty-progress-" + id) is not null);
                Require(styleActionButtons.Count == 0 && FindById<Border>(guidedBody, "plan-actions-" + id) is null, "awaiting approval shows progress, not chips");
                await Change(p => p with { GraphViewMode = "diagram" }); Refresh(); RefreshMightyView(Session);
                await WaitUI(() => FindById<Border>(guidedBody, "plan-actions-" + id) is not null && planDockCard is null);
                checks["awaitingApproval"] = CurrentPhase(); checks["planAnswersInStylePanel"] = true;

                // Approved and carried out, the turn and its background work both over: the next chips.
                ClearToolPermissions();
                var finished = work with { Tasks = work.Tasks.Select(t => t with { Status = "completed", EndedAt = Wire.Now() }).ToList() };
                await Change(p => p with { Status = "completed", GraphRuns = [Run("completed")], PlanHistory = [approved], TodoProgress = todos, BackgroundWork = finished });
                Refresh();
                await WaitUI(() => CurrentPhase() == Title("execute"));
                await WaitUI(() => HasText("mighty-state-", "3/7"));
                Require(HasText("mighty-state-", Locale.Get("styles.state.todoCurrent", new Dictionary<string, string> { ["item"] = "doing step 4" })), "the current step is drawn under the bar");
                await WaitUI(() => styleActionButtons.ContainsKey("new-plan") && styleActionButtons.ContainsKey("verify"), () => "after execution: new plan and verify");

                // The turn is over but a background agent still runs: the pane stays running (the process is open
                // for that work, and the store keeps running tasks only while the pane runs), still in execute.
                await Change(p => p with { Status = "running", BackgroundWork = work });
                Refresh();
                await WaitUI(() => CurrentPhase() == Title("execute") && HasText("mighty-state-", PlanCardSupport.BackgroundSummary(work)),
                    () => $"the background line is drawn: phase {CurrentPhase()}, expected '{PlanCardSupport.BackgroundSummary(work)}', stored {Session.BackgroundWork?.Running.Count ?? -1} running, turn ended {Session.BackgroundWork?.TurnEnded}");
                await WaitUI(() => FindById<StackPanel>(guidedBody, "background-tasks-" + id) is { IsLoaded: true } rows && rows.Children.Count == 2);
                Require(HasText("background-tasks-", "review the diff"), "the running background agent is listed");
                checks["executing"] = CurrentPhase(); checks["progressWidget"] = true; checks["backgroundList"] = true;

                // Every new request starts in plan mode; the pane's stored mode stays.
                var request = await PrepareStyleRunRequest(new StartRunRequest(id, Session.WorkspaceId, "claude", "fixture", []));
                Require(request.PermissionModeOverride == "plan", "a new request in the plan style starts in plan mode");
                Require(Session.Settings.PermissionMode == original.Settings.PermissionMode, "the style leaves the pane's stored mode alone");
                checks["launchesInPlanMode"] = request.PermissionModeOverride;

                // The style draws its own task list, so the strip outside it stays hidden; outside the style it shows.
                Require(backgroundHost?.Visibility == Visibility.Collapsed, "the plan style keeps its own background list");
                await Change(p => p with { MightyStyle = null }); activeStyle = null; loadedStyleKey = null; Refresh();
                await WaitUI(() => backgroundHost?.Visibility == Visibility.Visible && FindById<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>(backgroundHost, "background-work-" + id) is { IsLoaded: true });
                await Change(p => p with { AgentViewMode = "default" }); Refresh(); RefreshMightyView(Session);
                await WaitUI(() => backgroundHost?.Visibility == Visibility.Visible);
                checks["backgroundStripOutsideStyle"] = true;
                return checks;
            }
            finally
            {
                ClearToolPermissions();
                await Change(p => p with { Provider = original.Provider, AgentViewMode = original.AgentViewMode, MightyStyle = original.MightyStyle, MightyStyleHash = original.MightyStyleHash, MightyStyleSince = original.MightyStyleSince, Status = original.Status, GraphRuns = original.GraphRuns, GraphViewMode = original.GraphViewMode, PlanHistory = original.PlanHistory, TodoProgress = original.TodoProgress, BackgroundWork = original.BackgroundWork });
                input.Text = draft; activeStyle = null; loadedStyleKey = null; stylePrerequisites = savedPrerequisites; Refresh(); RefreshMightyView(Session);
                await WaitUI(() => !styleLoading);
            }
        }
    }
}
