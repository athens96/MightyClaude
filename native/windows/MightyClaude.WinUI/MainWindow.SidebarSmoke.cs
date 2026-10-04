using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private async Task<Dictionary<string, object?>> RunSidebarSmoke()
    {
        var original = service.Snapshot; var originalSearch = search.Text;
        var first = original.Workspaces.First(); var second = original.Workspaces.Last();
        var firstSession = original.Sessions.First(p => p.WorkspaceId == first.Id);
        var retainedPane = views[firstSession.Id]; var draft = firstSession.Draft;
        var otherSession = new RunSession { WorkspaceId = second.Id, Title = "Other workspace fixture", Draft = "Preserved fixture draft" };
        async Task Invoke(string id)
        {
            Button? button = null;
            await WaitUI(() => (button = VisualChildren(root).OfType<Button>().FirstOrDefault(b => AutomationProperties.GetAutomationId(b) == id)) is { IsLoaded: true, ActualWidth: > 0, ActualHeight: > 0 });
            Require(button!.IsTabStop && button.Focus(FocusState.Keyboard), "Sidebar action must support native keyboard focus: " + id);
            ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
        }
        try
        {
            search.Text = "";
            await service.UpdateAsync(s => s with { ExpandedWorkspaceIds = [first.Id], ActiveWorkspaceId = first.Id, ActiveSessionId = firstSession.Id, Sessions = [.. s.Sessions, otherSession] }); Render();
            await Invoke("workspace-expand-" + second.Id);
            await WaitUI(() => WorkspaceDisclosure.Expanded(service.Snapshot).SetEquals([first.Id, second.Id]) && sidebarSessionButtons.ContainsKey(otherSession.Id));
            Require(service.Snapshot.ActiveSessionId == firstSession.Id && service.Snapshot.ActiveWorkspaceId == first.Id && ReferenceEquals(views[firstSession.Id], retainedPane), "Expanding another workspace must preserve the active pane and editor instance.");
            await Invoke("workspace-expand-" + first.Id);
            await WaitUI(() => !sidebarSessionButtons.ContainsKey(firstSession.Id));
            Require(service.Snapshot.ActiveSessionId == firstSession.Id && service.Snapshot.Sessions.First(s => s.Id == firstSession.Id).Draft == draft, "Collapsing an active list must preserve its selection and draft.");
            await Invoke("sidebar-session-" + otherSession.Id);
            await WaitUI(() => service.Snapshot.ActiveSessionId == otherSession.Id && service.Snapshot.ActiveWorkspaceId == second.Id);
            await Invoke("workspace-select-" + first.Id);
            await WaitUI(() => service.Snapshot.ActiveWorkspaceId == first.Id && sidebarSessionButtons.ContainsKey(firstSession.Id));
            Require(WorkspaceDisclosure.Expanded(service.Snapshot).SetEquals([first.Id, second.Id]) && ReferenceEquals(views[firstSession.Id], retainedPane), "Selecting a workspace reopens it without collapsing another or replacing the composer.");
            var add = VisualChildren(sidebar).OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "workspace-add-session-" + second.Id);
            await WaitUI(() => add.IsLoaded && add.ActualWidth > 0);
            Require(add.Flyout is MenuFlyout { Items.Count: > 0 } menu && menu.Items.Count == AddPaneMenu.Entries().Count, "Every workspace has the complete shared Add Pane menu.");
            add.Flyout!.ShowAt(add);
            var files = ((MenuFlyout)add.Flyout).Items.OfType<MenuFlyoutItem>().Single(item => AutomationProperties.GetAutomationId(item) == "dashboard-add-" + AddPaneMenu.Files + "-" + second.Id);
            await WaitUI(() => files.IsLoaded && files.ActualWidth > 0 && files.ActualHeight > 0);
            ((IInvokeProvider)new MenuFlyoutItemAutomationPeer(files).GetPattern(PatternInterface.Invoke)).Invoke();
            await WaitUI(() => service.Snapshot.ActiveWorkspaceId == second.Id && service.Snapshot.Sessions.Any(p => p.WorkspaceId == second.Id && FilePaneKind.IsFilePane(p.Kind)));
            add.Flyout.Hide();
            await ToggleWorkspaceDisclosure(first.Id, false); await ToggleWorkspaceDisclosure(second.Id, false);
            Require(service.Snapshot.ExpandedWorkspaceIds is { Count: 0 } && sidebarSessionButtons.Count == 0, "Both lists may stay collapsed independently of the active workspace.");
            var stored = await new StateStore(StateDirectory).LoadAsync();
            Require(stored.ExpandedWorkspaceIds is { Count: 0 }, "All-collapsed state must be persisted as [], not converted to null fallback.");
            await Invoke("sidebar-toggle-theme");
            await WaitUI(() => service.Snapshot.Theme != original.Theme);
            Require(ReferenceEquals(views[firstSession.Id], retainedPane) && service.Snapshot.Sessions.First(s => s.Id == firstSession.Id).Draft == draft, "Quick theme toggle must preserve the input control and draft.");
            return new() { ["independentDisclosure"] = true, ["selectionReopensOnlyTarget"] = true, ["inactiveWorkspacePaneSelection"] = true, ["workspaceBoundAddMenu"] = true, ["collapsedStatePersists"] = true, ["draftAndPaneIdentity"] = true, ["keyboardAccessible"] = true, ["quickTheme"] = true };
        }
        finally { await service.UpdateAsync(_ => original); search.Text = originalSearch; Render(); }
    }
}
