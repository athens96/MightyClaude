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
        /// The dragged sizes of blocks read back from the session record, kept while the pane shows (M/MightyGraphView.swift:400-401):
        /// a pane saves the sizes of its own blocks only (<see cref="GraphBlockPreferences"/>).
        /// </summary>
        private readonly Dictionary<string, GraphBlockSize> graphRecordSizes = [];

        /// <summary>The sizes the diagram is laid out with: the pane's saved ones and, over them, those of blocks read back from the record.</summary>
        private IReadOnlyDictionary<string, GraphBlockSize>? GraphBlockSizes(RunSession pane)
        {
            if (graphRecordSizes.Count == 0) return pane.GraphBlockSizes;
            var sizes = new Dictionary<string, GraphBlockSize>(pane.GraphBlockSizes ?? [], StringComparer.Ordinal);
            foreach (var (nodeId, size) in graphRecordSizes) sizes[nodeId] = size;
            return sizes;
        }

        /// <summary>Keeps a dragged size: on the pane, or here for a block read back from the record.</summary>
        private Task SaveGraphBlockSize(string nodeId, GraphBlockSize size) => owner.Act(async () =>
        {
            if (!LoadedFromRecord(nodeId, graphHistory.Runs)) await Change(p => GraphBlockPreferences.Set(p, nodeId, size));
            else if (size.NormalizedFor(nodeId) is { } kept) graphRecordSizes[nodeId] = kept;
            SettleGraphResize();
        });

        /// <summary>
        /// The expand / collapse control that ends a block's header (M/MightyGraphView.swift:538-547): the two
        /// brackets with their arrows leaving (expand) or entering (collapse), plain in the header's quiet
        /// <paramref name="ink"/> (<c>ink2</c>, the strip's ink on a result) with the subtle wash under the
        /// pointer. The newest result has none while it fits the window. Putting a dragged size back is the
        /// corner handle's menu (<see cref="BuildResultResizeGrip"/>), as on the Mac.
        /// </summary>
        private void AddGraphBlockSizeControls(StackPanel controls, MightyGraphBlock block, Brush ink, GraphCardView view)
        {
            if (graphLayout?.FittedResultID == block.Id) return;
            var expanded = graphExpanded.Contains(block.Id);
            var name = expanded ? Locale.Get("graph.block.collapse") : Locale.Get("graph.block.expand");
            var toggle = HeaderButton(MightySymbols.Create(expanded ? "rectangle.compress.vertical" : "rectangle.expand.vertical", 13, ink), ink);
            toggle.Click += async (_, _) => await owner.Act(async () =>
            {
                // A block the user opened or folded never moves the camera; it follows on only if the newest block is still in view
                // (M/MightyGraphView.swift:280-282). Said again before the redraw: saving the size may have drawn the diagram in between.
                graphRecordSizes.Remove(block.Id); graphFollow.UserChangedLayout();
                await Change(p => GraphBlockPreferences.Set(p, block.Id, null));
                if (!graphExpanded.Remove(block.Id)) graphExpanded.Add(block.Id);
                graphFollow.UserChangedLayout();
                RefreshMightyView(Session);
            });
            AutomationProperties.SetName(toggle, name); ToolTipService.SetToolTip(toggle, name);
            AutomationProperties.SetAutomationId(toggle, "mighty-expand-" + block.Id);
            controls.Children.Add(view.Expand = toggle);
        }

        private Task ResetGraphBlockSize(string nodeId) => owner.Act(async () =>
        {
            var folded = graphExpanded.Remove(nodeId);
            if (folded) graphFollow.UserChangedLayout();
            graphRecordSizes.Remove(nodeId);
            // The newest result and the pending plan go back to the window fit; any other block to its own size.
            await Change(p => nodeId == graphPlanNodeId ? p with { GraphPlanSize = null }
                : GraphBlockPreferences.Set(nodeId == graphLatestResultId ? p with { GraphResultSize = null } : p, nodeId, null));
            if (folded) graphFollow.UserChangedLayout();
            RefreshMightyView(Session);
        });
    }
}
