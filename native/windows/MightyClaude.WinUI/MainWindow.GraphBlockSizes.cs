using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private sealed partial class PaneView
    {
        private readonly HashSet<string> graphExpanded = [];
        private string? graphResizeNodeId;

        /// <summary>
        /// The expand / collapse and reset controls of a block's header, plain in the header's quiet
        /// <paramref name="ink"/> (<c>ink2</c>, the strip's ink on a result) with the subtle wash under the pointer.
        /// </summary>
        private void AddGraphBlockSizeControls(StackPanel controls, MightyGraphBlock block, Brush ink)
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
                PaintSizeControl(toggle, ink);
                AutomationProperties.SetAutomationId(toggle, "mighty-expand-" + block.Id); controls.Children.Add(toggle);
            }
            if (Session.GraphBlockSizes?.ContainsKey(block.Id) == true || graphExpanded.Contains(block.Id))
            {
                var reset = ReferenceButton("↺", "graph.block.reset", () => ResetGraphBlockSize(block.Id));
                PaintSizeControl(reset, ink);
                AutomationProperties.SetAutomationId(reset, "mighty-reset-" + block.Id); controls.Children.Add(reset);
            }
        }

        private void PaintSizeControl(Button button, Brush ink)
        {
            button.MinHeight = 0; button.Height = 22; button.Padding = new Thickness(6, 0, 6, 0); button.FontSize = DesignMetrics.Type.Pill;
            button.BorderThickness = new Thickness(0); button.CornerRadius = new CornerRadius(DesignMetrics.Radius.Segment); button.VerticalAlignment = VerticalAlignment.Center;
            owner.PaintPlainButton(button, owner.brushes.Transparent, owner.brushes.Subtle, ink: ink);
        }

        private Task ResetGraphBlockSize(string nodeId) => owner.Act(async () =>
        {
            CancelResultReveal(); graphExpanded.Remove(nodeId);
            await Change(p => GraphBlockPreferences.Set(nodeId == graphLatestResultId ? p with { GraphResultSize = null } : p, nodeId, null));
            RefreshMightyView(Session);
        });
    }
}
