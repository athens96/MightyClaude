using MightyClaude.Core;

/// The Mighty graph's picture preview block (MightyGraphImages; macOS MightyGraphImagesTests): which steps get one,
/// and where the layout attaches it and the other blocks that hang beside the flow.
internal static class MightyGraphImagesVerification
{
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
    private static LogEntry Markdown(string text) => new("md", "assistant", text, Wire.Now(), "claude");
    private static bool Intersects(GraphRect a, GraphRect b) => a.X < b.MaxX && b.X < a.MaxX && a.Y < b.MaxY && b.Y < a.MaxY;

    internal static Task TheGraphPreviewAttachesToTheStepThatProducedThePictures()
    {
        var workspace = Verification.Temp();
        try
        {
            File.WriteAllBytes(Path.Combine(workspace, "plot.png"), Png);
            var stored = new AgentImageRef(new string('c', 64), "image/png", 4, 4, 10, "Read");
            var picture = new LogEntry("pic", "image", "그림", Wire.Now(), "claude", null, [stored, stored]);
            var agent = new MightyGraphAgent { Id = "graph-child", Title = "Look", Input = "look", Status = "completed", Entries = [picture] };
            var quiet = new MightyGraphAgent { Id = "graph-quiet", Title = "Quiet", Input = "x", Status = "completed" };
            var run = new MightyGraphRun { Id = "run-1", Input = "show", Status = "completed", RootEntries = [Markdown("![plot](plot.png) ![web](https://example.com/x.png) ![out](../outside.png)")], Agents = [agent, quiet], FinalOutput = "done" };
            // Once each: the stored picture named twice is one; the remote and the refused Markdown targets are not previewed.
            var galleries = MightyGraphImages.Galleries([run], workspace);
            Check(galleries.SequenceEqual([("run-1", "request", 1), ("run-1", "agent:graph-child", 1)]), "one gallery per step with pictures: " + string.Join(", ", galleries));
            Check(MightyGraphImages.Items(run, "agent:graph-child", workspace).Select(i => i.Id).SequenceEqual(["hash:" + stored.Hash]), "the agent's stored picture");
            var located = MightyGraphImages.Items(run, MightyGraphImages.RequestStep, workspace).Single();
            Check(located.Id.StartsWith("file:", StringComparison.Ordinal) && located.Id.EndsWith("plot.png", StringComparison.Ordinal) && located is { Stored: null, Located: AgentImageLocation.File, Alt: "plot", Source: "plot.png" }, "the request's Markdown picture: " + located);
            Check(MightyGraphImages.Items(run, "agent:graph-quiet", workspace).Count == 0 && MightyGraphImages.Items(run, "agent:nobody", workspace).Count == 0 && MightyGraphImages.Items(run, "elsewhere", workspace).Count == 0, "a step without pictures has none");

            var layout = MightyGraphLayout.Make([run], "", false, new HashSet<string>(), galleries: galleries);
            var (stepID, galleryID) = (MightyGraphLayout.NodeID(run, "agent:graph-child"), MightyGraphLayout.NodeID(run, MightyGraphLayout.ImagesSuffix + "agent:graph-child"));
            var (step, gallery) = (layout.Nodes.Single(n => n.Id == stepID), layout.Nodes.Single(n => n.Id == galleryID));
            Check(gallery.Kind == MightyGraphLayout.ImagesKind && MightyGraphLayout.IsAuxiliary(gallery.Kind) && MightyGraphCamera.IsAuxiliary(galleryID) && !MightyGraphLayout.IsResizable(gallery.Kind), "an attachment, not a place for the camera");
            Check(gallery.Frame.Y == step.Frame.Y && gallery.Frame.X > step.Frame.MaxX && gallery.Frame.W == MightyGraphLayout.ImagesWidth, "beside the step that produced it");
            Check(!layout.Edges.Any(e => e.Source == galleryID || e.Target == galleryID) && layout.Size.W >= gallery.Frame.MaxX - layout.OriginX, "no edge, and the canvas reaches it");
            // No other card moves for it.
            var plain = MightyGraphLayout.Make([run], "", false, new HashSet<string>());
            Check(plain.Nodes.All(node => layout.Nodes.Single(n => n.Id == node.Id).Frame == node.Frame), "no other card moves");
            // Unfolding grows only the preview, and only when there is more to show.
            Check(MightyGraphLayout.ImagesHeight(1, true) == MightyGraphLayout.ImagesHeight(1, false) && MightyGraphLayout.ImagesHeight(9, true) > MightyGraphLayout.ImagesHeight(9, false), "unfolding grows it only with more to show");
            Check(MightyGraphLayout.VisibleImages(20, false) == 6 && MightyGraphLayout.VisibleImages(20, true) == 12 && MightyGraphLayout.ImagesHeight(1, false) == 38 + 24 + 88 && MightyGraphLayout.ImagesHeight(9, false) == 38 + 24 + 2 * 88 + 8, "two rows folded, four unfolded");
            // A step's preview never overlaps a request preview on the same rows.
            var request = layout.Nodes.Single(n => n.Id == MightyGraphLayout.NodeID(run, MightyGraphLayout.ImagesSuffix + "request"));
            Check(!Intersects(request.Frame, gallery.Frame), "two previews never overlap");
            // The block the diagram draws: its step in the record slot, and the block follows nothing.
            var block = MightyGraphBlockModel.Blocks(layout, [run], "", "Claude", true).Single(b => b.Id == galleryID);
            Check(block is { Kind: MightyGraphLayout.ImagesKind, RecordId: "agent:graph-child" } && block.Title == Locale.Get("graph.images.title") && MightyGraphFollow.TargetID(layout, [run]) == MightyGraphLayout.NodeID(run, "result"), "the preview's block");
        }
        finally { Directory.Delete(workspace, true); }
        return Task.CompletedTask;
    }

    internal static Task AStepsPicturesAreWorkedOutOnceUntilItChanges()
    {
        var workspace = Verification.Temp();
        try
        {
            var plot = Path.Combine(workspace, "plot.png");
            File.WriteAllBytes(plot, Png);
            var id = "run-memo-" + Wire.Id();
            MightyGraphRun Run(string status, params LogEntry[] more) => new() { Id = id, Input = "show", Status = status, RootEntries = [Markdown("![plot](plot.png) ![later](later.png)"), .. more] };
            Check(MightyGraphImages.Items(Run("running"), "request", workspace).Count == 1, "the picture that is there");
            // Nothing about the step changed: the remembered answer, no file look-up.
            File.Delete(plot);
            File.WriteAllBytes(Path.Combine(workspace, "later.png"), Png);
            Check(MightyGraphImages.Items(Run("running"), "request", workspace).Count == 1 && MightyGraphImages.Galleries([Run("running")], workspace).FirstOrDefault().Count == 1, "an unchanged step is not looked up again");
            // The step settles: worked out again from the files as they are now.
            var settled = MightyGraphImages.Items(Run("completed"), "request", workspace);
            Check(settled.Count == 1 && settled[0].Id.EndsWith("later.png", StringComparison.Ordinal), "a settled step is worked out again");
            // A new entry also counts as a change.
            var stored = new AgentImageRef(new string('a', 64), "image/png", 4, 4, 10, "Read");
            Check(MightyGraphImages.Galleries([Run("completed", new LogEntry("pic", "image", "그림", Wire.Now(), "claude", null, [stored]))], workspace).FirstOrDefault().Count == 2, "a new entry is a change");
            // A streaming step's text grows on every redraw: only a source it had not named yet is looked up.
            var stream = "run-stream-" + Wire.Id();
            File.WriteAllBytes(Path.Combine(workspace, "first.png"), Png);
            MightyGraphRun Streaming(string status, string text) => new() { Id = stream, Input = "show", Status = status, RootEntries = [Markdown(text)] };
            Check(MightyGraphImages.Items(Streaming("running", "![first](first.png)"), "request", workspace).Count == 1, "the first picture as it streams in");
            File.Delete(Path.Combine(workspace, "first.png"));
            File.WriteAllBytes(Path.Combine(workspace, "second.png"), Png);
            var grown = MightyGraphImages.Items(Streaming("running", "![first](first.png) and ![second](second.png)"), "request", workspace);
            Check(grown.Count == 2 && grown[0].Id.EndsWith("first.png", StringComparison.Ordinal) && grown[1].Id.EndsWith("second.png", StringComparison.Ordinal), "a source already found is not looked up again; a new one is");
            var done = MightyGraphImages.Items(Streaming("completed", "![first](first.png) and ![second](second.png)"), "request", workspace);
            Check(done.Count == 1 && done[0].Id.EndsWith("second.png", StringComparison.Ordinal), "settled, every source is looked up again");
        }
        finally { Directory.Delete(workspace, true); }
        return Task.CompletedTask;
    }

    internal static Task TheAttachmentsHangOffTheExecutionBlockAndStack()
    {
        var run = new MightyGraphRun { Id = "one", Input = "request", Status = "completed", FinalOutput = "done" };
        (string, string, int)[] galleries = [("one", MightyGraphImages.RequestStep, 2), ("gone", MightyGraphImages.RequestStep, 3), ("one", "agent:nobody", 1), ("one", MightyGraphImages.RequestStep, 0)];
        (string, string)[] executions = [("one", "job-1"), ("one", "job-2"), ("gone", "job-3"), ("one", "job-1")];
        var layout = MightyGraphLayout.Make([run], "", false, new HashSet<string> { MightyGraphBlockSize.NodeId("one", MightyGraphLayout.ExecutionSuffix + "job-2") },
            planRecords: [("one", "answered")], splitsExecution: true, galleries: galleries, executions: executions);
        GraphRect Frame(string suffix) => layout.Nodes.Single(n => n.Id == MightyGraphBlockSize.NodeId("one", suffix)).Frame;
        var (work, pictures, first, second, record) = (Frame(MightyGraphLayout.WorkSuffix), Frame(MightyGraphLayout.ImagesSuffix + "request"), Frame(MightyGraphLayout.ExecutionSuffix + "job-1"),
            Frame(MightyGraphLayout.ExecutionSuffix + "job-2"), Frame(MightyGraphLayout.PlanRecordSuffix + "answered"));
        // With a 실행 block the request step's pictures and the executions the main agent started hang off it, not off the request.
        Check(pictures.Y == work.Y && pictures.X == work.MaxX + 8 && first.Y == work.Y && first.X == pictures.MaxX + 8 && first is { W: MightyGraphLayout.ExecutionWidth, H: 290 }, "the pictures, then the first execution, beside the 실행 block");
        // Executions stack, each right of what shares its rows; an opened one is taller; the answered plan goes below them.
        Check(second.Y == first.MaxY + 8 && second.H == MightyGraphLayout.ExecutionHeight(true) && MightyGraphLayout.ExecutionHeight(true) == 470 && record.Y == second.MaxY + 8, "executions stack and the answered plan follows");
        Check(layout.Nodes.Count(n => n.Kind == MightyGraphLayout.ExecutionKind) == 2 && layout.Nodes.Count(n => n.Kind == MightyGraphLayout.ImagesKind) == 1, "a request that is gone, a step that is not there, an empty gallery and a repeated execution add nothing");
        Check(layout.Size.W >= first.MaxX + 24 - layout.OriginX && layout.Size.H >= record.MaxY + 24 - layout.OriginY, "the canvas reaches every attachment");
        // Nothing in the flow moves for them, and none is where a camera stands or a drag resizes.
        var plain = MightyGraphLayout.Make([run], "", false, new HashSet<string>(), splitsExecution: true);
        Check(plain.Nodes.All(node => layout.Nodes.Single(n => n.Id == node.Id).Frame == node.Frame) && layout.Edges.Count == plain.Edges.Count, "nothing in the flow moves and no edge is added");
        var execution = MightyGraphBlockModel.Blocks(layout, [run], "", "Claude", true).Single(b => b.Id == MightyGraphBlockSize.NodeId("one", MightyGraphLayout.ExecutionSuffix + "job-2"));
        Check(execution is { Kind: MightyGraphLayout.ExecutionKind, RecordId: "job-2" } && MightyGraphCamera.IsAuxiliary(execution.Id) && !MightyGraphCamera.IsAuxiliary(MightyGraphBlockSize.NodeId("one", MightyGraphLayout.WorkSuffix)), "the execution's block, its key in the record slot");
        // Without the split they hang off the request, as the shared layout has it.
        var shared = MightyGraphLayout.Make([run], "", false, new HashSet<string>(), galleries: galleries, executions: executions);
        var request = shared.Nodes.Single(n => n.Kind == "request").Frame;
        Check(shared.Nodes.Single(n => n.Kind == MightyGraphLayout.ImagesKind).Frame.Y == request.Y && shared.Nodes.First(n => n.Kind == MightyGraphLayout.ExecutionKind).Frame.Y == request.Y, "without a 실행 block they hang off the request");
        return Task.CompletedTask;
    }
}
