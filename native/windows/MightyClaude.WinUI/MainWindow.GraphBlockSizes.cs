using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private sealed partial class PaneView
    {
        private readonly HashSet<string> graphExpanded = [];
        private string? graphResizeNodeId;

        private void AddGraphBlockSizeControls(StackPanel controls, MightyGraphBlock block)
        {
            if (block.Kind is not ("request" or "result" or "agent" or "draft")) return;
            if (graphLayout?.FittedResultID != block.Id)
            {
                var expanded = graphExpanded.Contains(block.Id);
                var toggle = ReferenceButton(expanded ? "↥" : "↧", expanded ? "graph.block.collapse" : "graph.block.expand", () => owner.Act(async () =>
                {
                    CancelResultReveal();
                    await Change(p => GraphBlockPreferences.Set(p, block.Id, null));
                    if (!graphExpanded.Remove(block.Id)) graphExpanded.Add(block.Id);
                    RefreshMightyView(Session);
                }));
                AutomationProperties.SetAutomationId(toggle, "mighty-expand-" + block.Id); controls.Children.Add(toggle);
            }
            if (Session.GraphBlockSizes?.ContainsKey(block.Id) == true || graphExpanded.Contains(block.Id))
            {
                var reset = ReferenceButton("↺", "graph.block.reset", () => ResetGraphBlockSize(block.Id));
                AutomationProperties.SetAutomationId(reset, "mighty-reset-" + block.Id); controls.Children.Add(reset);
            }
        }

        private Task ResetGraphBlockSize(string nodeId) => owner.Act(async () =>
        {
            CancelResultReveal(); graphExpanded.Remove(nodeId);
            await Change(p => GraphBlockPreferences.Set(nodeId == graphLatestResultId ? p with { GraphResultSize = null } : p, nodeId, null));
            RefreshMightyView(Session);
        });
    }
}
