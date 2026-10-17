using System.Text.Json;
using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    /// <summary>The delegation smoke's stand-in for the composer's start: the service's own send, whose run is the scripted Claude's.</summary>
    private Func<StartRunRequest, Task<DelegationRelease>>? smokeSend;

    /// <summary>
    /// The close paths of parent → child delegation, ported from the Mac's smoke (M/AppStore+DelegationSmoke.swift:14-175) and driven
    /// through the window's own functions in the smoke's disposable profile: three reported children in worktrees of a temporary
    /// git repository, listed one step in under their parent with a held notice pinned in its queue; a merged child closed in the
    /// window and one closed from the phone are cleaned up; a phone send releases the held notice ahead of its text in one run; a
    /// workspace with an open child or a kept child worktree is refused; a parent with an open child asks first in the window and
    /// closes from the phone at once, its child kept under a 'parent closed' node; and once the last child is discarded from its
    /// card the workspace goes. No model runs: the parent's run is the scripted Claude's (<see cref="DelegationFakeClaude"/>), the
    /// app's own binary in its <c>--fake-claude</c> mode, put in the CLI's place for this step only. The smoke's own switch is on
    /// only while the composer's second send runs a text in a new child worktree, whose child the human merges or discards from its card.
    /// Then the worktree menu's pick of Claude on a read-only Codex pane (which never runs) starts a Claude child in plan mode, run
    /// by the scripted Claude (M/AppStore+DelegationSmoke.swift step 9).
    /// </summary>
    private async Task<Dictionary<string, object?>> RunDelegationSmoke()
    {
        var directory = options.ProfileDirectory ?? throw new InvalidOperationException("The delegation smoke needs a disposable profile.");
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("The app's own binary is not known.");
        var checks = new Dictionary<string, object?>();
        var before = service.Snapshot;
        var root = Path.Combine(directory, "Delegation Smoke"); var repo = Path.Combine(root, "repo"); var fake = Path.Combine(root, "fake");
        Directory.CreateDirectory(repo); Directory.CreateDirectory(fake);
        service.Providers.Override("claude", new CliCommand(executable, [DelegationFakeClaude.Argument, fake], "2.1.271"));
        smokeSend = service.SendAsync;
        try
        {
            await DelegationSmokeGit(repo, "init", "-q", "-b", "main");
            File.WriteAllText(Path.Combine(repo, "README.md"), "# Delegation smoke\n");
            await DelegationSmokeGit(repo, "add", "-A"); await DelegationSmokeCommit(repo, "Start");
            var workspace = await service.AddWorkspaceAsync(repo);
            var parent = Wire.Id(); const string mode = "acceptEdits";
            await service.UpdateAsync(s => s with { Sessions = [.. s.Sessions, new RunSession { Id = parent, WorkspaceId = workspace.Id, Kind = "claude", Provider = "claude", Title = "Delegation parent", Settings = new(PermissionMode: mode) }], ActiveWorkspaceId = workspace.Id, ActiveSessionId = parent });
            Render();

            // Three reported children in worktrees made the way delegate makes them. B starts at A's head, so both merge by fast-forward.
            var maker = new ChildWorktreeMaker(Path.Combine(directory, "worktrees"), _ => long.MaxValue);
            if (await maker.Check(repo) is not ChildWorktreeCheck.Ready { Base: var start }) throw new InvalidOperationException("The smoke repository is not ready for worktrees.");
            var ids = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid().ToString().ToLowerInvariant()).ToArray();
            var records = new List<ChildRecord>(); var folders = new Dictionary<string, string>(); var heads = new Dictionary<string, string>();
            for (var index = 0; index < ids.Length; index++)
            {
                var id = ids[index]; var task = $"Smoke child {index + 1}: write note {index + 1}.";
                var from = index == 1 ? start with { BaseCommit = heads[ids[0]] } : start;
                if (await maker.Make(id, from, task) is not ChildWorktreeOutcome.Created { Worktree: var made }) throw new InvalidOperationException($"Child {index + 1}'s worktree was not made.");
                File.WriteAllText(Path.Combine(made.WorktreePath, $"note-{index + 1}.txt"), $"Note {index + 1}\n");
                await DelegationSmokeGit(made.WorktreePath, "add", "-A"); await DelegationSmokeCommit(made.WorktreePath, $"Note {index + 1}");
                File.WriteAllText(ChildWorktree.ReportFileIn(made.WorktreePath), $"# Report\n\nChild {index + 1} wrote its note.\n");
                heads[id] = await DelegationSmokeGit(made.WorktreePath, "rev-parse", "HEAD"); folders[id] = made.WorkingFolder;
                records.Add(ChildRecord.Make(id, parent, made.WorktreePath, made.ParentBranch, made.BaseCommit, mode, DelegationRequestKey.Make(parent, "smoke", task, mode), reportRevision: 1, reportHead: heads[id], state: ChildState.Reported, parentCheckout: repo));
            }
            var (a, b, c) = (ids[0], ids[1], ids[2]);
            // C's notice was pending when the app last quit, so this launch holds it.
            var notice = new Notice(Guid.NewGuid().ToString().ToLowerInvariant(), c, 1, NoticeKind.Reported, DeliveryLane.Pending);
            new DelegationFileStore(directory).Save(DelegationFile.Of(records, [notice]));
            await service.RestartDelegationAsync();
            var delegation = service.Delegation ?? throw new InvalidOperationException("Delegation did not load the smoke's file.");
            foreach (var id in ids)
                if (!await ((IDelegationHost)service).CreatePane(new(id, parent, mode, folders[id]))) throw new InvalidOperationException("A child pane was not made.");
            await DelegationSmokeUntil("the children and the held notice", 10, () => service.DelegationChildren.Count == 3 && service.HeldItems(parent).Select(item => item.Id).SequenceEqual([notice.Id]));
            checks["childrenListed"] = true;
            // The sidebar lists each child one step in under its parent with its state, and the parent's queue pins the held notice first.
            Render();
            Require(ids.All(id => sidebarChildStates.ContainsKey(id) && DelegationSmokeFind(workspaces, "sidebar-child-card-" + id) is StackPanel { Margin.Left: DesignMetrics.Spacing.Xl }), "The children are not listed one step in under their parent.");
            Require(ids.All(id => sessionIndicators.TryGetValue(id, out var row) && row.Status.Text == DelegationText.State(ChildState.Reported)), "A child's row does not carry its state.");
            Require(ids.All(id => DelegationSmokeFind(workspaces, "child-card-merge-" + id) is Button && DelegationSmokeFind(workspaces, "child-card-discard-" + id) is Button), "A reported child's card does not offer merge and discard.");
            checks["sidebarTree"] = true;
            Require(MobilePane(parent).DelegationSmokeHeldRow(notice.Id), "The held notice is not the first row of the parent's queue, pinned and with no remove button.");
            checks["heldRowPinned"] = true;

            // 1. A merged child closed in the window is cleaned up: its worktree and branch go, its merge record stays.
            await DelegationSmokeMerge(a);
            Require(DelegationSmokeFind(workspaces, "child-card-undo-" + a) is Button && DelegationSmokeFind(workspaces, "child-card-note-" + a) is TextBlock, "The merged child's card does not offer undo or say it merged.");
            await CloseSession(a);
            checks["windowCloseMergedChild"] = await DelegationSmokeCleaned(a, delegation, repo);

            // 2. So is one closed from the phone.
            await DelegationSmokeMerge(b);
            await MobileAction(new(b, "close", JsonSerializer.SerializeToElement(new { })), CancellationToken.None);
            checks["phoneCloseMergedChild"] = await DelegationSmokeCleaned(b, delegation, repo);
            Require(await DelegationSmokeGit(repo, "rev-parse", "main") == heads[b], "main is not at child B's head after both merges.");

            // 3. A phone send to the idle parent releases C's held notice ahead of its text, in one run.
            const string text = "Carry on with what the third child reported.";
            Require(await MobileAction(new(parent, "submit", JsonSerializer.SerializeToElement(new { text })), CancellationToken.None) as string == "started", "The phone send did not start a run.");
            var delivered = await DelegationSmokeWait("C's notice delivered", 60, () => delegation.File.Notices.FirstOrDefault(n => n.Id == notice.Id && n.Lane == DeliveryLane.Delivered && !string.IsNullOrEmpty(n.Receipt?.RunId)));
            await DelegationSmokeUntil("the parent's run to end", 60, () => !service.IsSessionRunning(parent) && service.Snapshot.Sessions.First(s => s.Id == parent).Status != "running");
            var sent = service.Snapshot.Sessions.First(s => s.Id == parent).Logs.LastOrDefault(entry => entry.Kind == "user")?.Text ?? "";
            int pointer = sent.IndexOf($"[Mighty Claude notice {notice.Id}]", StringComparison.Ordinal), human = sent.IndexOf(text, StringComparison.Ordinal);
            Require(delivered.Receipt?.Route == DeliveryRoute.Queue && pointer >= 0 && sent.Contains(c, StringComparison.Ordinal) && human > pointer && service.HeldItems(parent).Count == 0, "The held notice did not go ahead of the phone's text in one run.");
            checks["phoneSendReleasedHeldNotice"] = true;
            checks["heldNoticeReceipt"] = new Dictionary<string, object?> { ["route"] = DelegationNames.Of(delivered.Receipt!.Route), ["runId"] = delivered.Receipt.RunId };

            // 3b. With Mighty Orchestration on, the second send runs a text in a new child worktree beside the parent: the child is
            // the human's, shows at once, runs the text, and reports to its card with no notice for its parent.
            // The human is on the parent with nothing typed there, so the new child shows at once.
            await service.UpdateAsync(s => s with { ActiveWorkspaceId = workspace.Id, ActiveSessionId = parent, Sessions = s.Sessions.Select(p => p.Id == parent ? p with { Draft = "" } : p).ToList() });
            var parentPane = service.Snapshot.Sessions.First(s => s.Id == parent);
            Require(service.SecondSend(parentPane with { Status = "running" }) != ComposerSecondSend.Worktree, "The second send offered a new worktree with Mighty Orchestration off.");
            SetDelegationOn(true);
            try
            {
                service.RefreshWorktreeReadiness(parentPane);
                await DelegationSmokeUntil("the parent's folder to be ready for worktrees", 30, () => service.SecondSend(parentPane with { Status = "running" }) == ComposerSecondSend.Worktree);
                const string phrase = "Smoke human child";
                File.WriteAllText(Path.Combine(fake, "child-scripts.json"), new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
                {
                    ["phrase"] = phrase,
                    ["steps"] = new System.Text.Json.Nodes.JsonArray(
                        new System.Text.Json.Nodes.JsonObject { ["write"] = "note-h.txt", ["text"] = "Note h\n" },
                        new System.Text.Json.Nodes.JsonObject { ["run"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonArray("git", "add", "note-h.txt"), new System.Text.Json.Nodes.JsonArray("git", "commit", "-q", "-m", "Note h")), ["ask"] = false },
                        new System.Text.Json.Nodes.JsonObject { ["write"] = ".mighty-delegation/REPORT.md", ["text"] = "# Report\n\nWrote note h.\n" }),
                }).ToJsonString());
                MobilePane(parent).DelegationSmokeRunInNewWorktree(phrase + ": write note h.");
                var humanChild = await DelegationSmokeWait("the human's child to report", 90, () => delegation.File.Children.FirstOrDefault(child => child.StartedByHuman == true && child.State == ChildState.Reported));
                Require(service.Snapshot.Sessions.FirstOrDefault(s => s.Id == humanChild.Id) is { ParentSessionId: var linked, Provider: "claude" } && linked == parent && service.Snapshot.ActiveSessionId == humanChild.Id
                    && humanChild.StartingMode == mode && delegation.File.Notices.All(n => n.ChildId != humanChild.Id) && service.HeldItems(parent).Count == 0, "The human's child did not open beside its parent, run its text and report with no notice.");
                Render();
                Require(DelegationSmokeFind(workspaces, "child-card-merge-" + humanChild.Id) is Button, "The human's child's card offers no merge.");
                checks["secondSendNewWorktree"] = true;
                // Its card's discard, through its confirmation, removes it again.
                smokeDelegationDialog = _ => Task.FromResult(ContentDialogResult.Primary);
                await DiscardChild(humanChild.Id);
                smokeDelegationDialog = null;
                Require(delegation.File.Children.First(child => child.Id == humanChild.Id).State == ChildState.Discarded && !Directory.Exists(humanChild.WorktreePath), "The human's child was not discarded.");
            }
            finally { smokeDelegationDialog = null; SetDelegationOn(false); }
            await service.UpdateAsync(s => s with { ActiveSessionId = parent });

            // 4. A workspace with an open child is not removed.
            Require(await DelegationSmokeRemovalRefused(workspace.Id) && service.Snapshot.Sessions.Any(s => s.Id == c), "A workspace with an open child was removed.");
            checks["removeRefusedOpenChild"] = DelegationNames.Of(DelegationReasonCode.WorkspaceHasChildren);

            // 5. Closing the parent in the window asks first and closes nothing yet.
            ContentDialog? asked = null;
            smokeDelegationDialog = dialog => { asked = dialog; return Task.FromResult(ContentDialogResult.None); };
            await CloseSession(parent);
            var parentTitle = service.Snapshot.Sessions.FirstOrDefault(s => s.Id == parent)?.Title ?? "";
            Require(asked is { Content: TextBlock words } && AutomationProperties.GetAutomationId(asked) == "delegation-parent-close-" + parent && words.Text == DelegationText.ParentCloseMessage(new(parent, parentTitle, 1)) && parentTitle.Length > 0,
                "The window's close of a parent with an open child did not ask first, or closed it.");
            checks["windowParentCloseAsks"] = true;

            // 6. From the phone it closes only the parent at once: C stays open, listed under a 'parent closed' node.
            smokeDelegationDialog = _ => throw new InvalidOperationException("The phone's close of a parent asked.");
            await MobileAction(new(parent, "close", JsonSerializer.SerializeToElement(new { })), CancellationToken.None);
            await DelegationSmokeUntil("the parent to close", 15, () => !service.Snapshot.Sessions.Any(s => s.Id == parent));
            Render();
            var tree = service.SidebarTree(service.Snapshot, workspace.Id);
            Require(service.Snapshot.Sessions.Any(s => s.Id == c) && delegation.File.Children.First(child => child.Id == c).State == ChildState.Reported
                && tree.ParentClosed.FirstOrDefault(node => node.Id == parent)?.Children.Any(child => child.Id == c) == true && DelegationSmokeFind(workspaces, "sidebar-parent-closed-" + parent) is not null,
                "Closing the parent did not keep its open child under 'parent closed'.");
            checks["phoneParentCloseKeepsChild"] = true;

            // 7. Closing the unmerged child keeps its worktree, and the workspace stays refused while it is on disk.
            await CloseSession(c);
            var kept = await DelegationSmokeWait("C's close", 30, () => service.ChildClose(c) is { IsCompleted: true } close ? close.Result : null);
            Require(kept == new ChildCleanupOutcome.Kept(ChildCleanupHold.NotMerged) && Directory.Exists(records[2].WorktreePath) && await DelegationSmokeRemovalRefused(workspace.Id),
                "A workspace with a child worktree not cleaned up was removed, or C's close removed its work.");
            checks["removeRefusedKeptWorktree"] = true;

            // 8. Once the human discards C from its card, through its confirmation, the workspace goes.
            ContentDialog? confirmation = null;
            smokeDelegationDialog = dialog => { confirmation = dialog; return Task.FromResult(ContentDialogResult.Primary); };
            await DiscardChild(c);
            Require(confirmation is { Content: TextBlock message } && message.Text.Contains(Locale.Get("delegation.discard.noNested"), StringComparison.Ordinal) && confirmation.PrimaryButtonText == Locale.Get("delegation.discard.confirm"),
                "The discard confirmation did not name what goes.");
            Require(delegation.File.Children.First(child => child.Id == c).State == ChildState.Discarded && !Directory.Exists(records[2].WorktreePath), "C was not discarded.");
            await RemoveWorkspaceConfirmed(workspace.Id);
            await DelegationSmokeUntil("the workspace to go", 15, () => !service.Snapshot.Workspaces.Any(w => w.Id == workspace.Id));
            checks["removedAfterDiscard"] = true;

            // 9. A child of another agent: on a Codex pane in manual mode, whose own child could not change files, the worktree
            //    menu offers only a Claude child in plan mode, and its pick starts one, run by the scripted Claude. The menu is
            //    filled as the pane fills it on opening; the pick goes the way the menu item's send does. Claude's CLI is the
            //    smoke's scripted one, so the smoke treats it as ready.
            SetDelegationOn(true); smokeAgentsReady["claude"] = true;
            try
            {
                var crossRepo = Path.Combine(root, "cross"); Directory.CreateDirectory(crossRepo);
                await DelegationSmokeGit(crossRepo, "init", "-q", "-b", "main");
                File.WriteAllText(Path.Combine(crossRepo, "README.md"), "# Cross-agent smoke\n");
                await DelegationSmokeGit(crossRepo, "add", "-A"); await DelegationSmokeCommit(crossRepo, "Start");
                var crossWorkspace = await service.AddWorkspaceAsync(crossRepo);
                var codexPane = Wire.Id();
                await service.UpdateAsync(s => s with { Sessions = [.. s.Sessions, new RunSession { Id = codexPane, WorkspaceId = crossWorkspace.Id, Kind = "claude", Provider = "codex", Title = "Codex parent", Settings = new(PermissionMode: "manual") }], ActiveWorkspaceId = crossWorkspace.Id, ActiveSessionId = codexPane });
                Render();
                var codex = service.Snapshot.Sessions.First(s => s.Id == codexPane);
                service.RefreshWorktreeReadiness(codex);
                await DelegationSmokeUntil("the Codex pane's worktree menu", 30, () => service.WorktreeMenuOnly(codex, false));
                Require(service.WorktreeChildChoices(codex, false).Select(choice => (choice.Agent, choice.Mode, choice.Blocked))
                    .SequenceEqual([("claude", (string?)"plan", (DelegationChildBlock?)null), ("codex", null, DelegationChildBlock.NoMode), ("gemini", null, DelegationChildBlock.NoMode)]),
                    "The read-only Codex pane's menu does not offer only a Claude child in plan mode.");
                Require(MobilePane(codexPane).DelegationSmokeWorktreeMenu().Select(item => (AutomationProperties.GetAutomationId(item), item.IsEnabled))
                    .SequenceEqual([("composer-worktree-claude-" + codexPane, true), ("composer-worktree-codex-" + codexPane, false), ("composer-worktree-gemini-" + codexPane, false)]),
                    "The worktree menu's rows are not Claude (enabled), then Codex and Antigravity (greyed out with why).");
                MobilePane(codexPane).DelegationSmokeRunInNewWorktree("Smoke cross-agent child: write a note.", "claude");
                var crossChild = await DelegationSmokeWait("the Codex pane's Claude child", 60, () => delegation.File.Children.FirstOrDefault(child => child.ParentSessionId == codexPane && child.StartedByHuman == true && child.State != ChildState.Creating));
                Require(crossChild.State != ChildState.Failed && crossChild.Provider is null && crossChild.Agent == "claude" && crossChild.StartingMode == "plan"
                    && service.Snapshot.Sessions.FirstOrDefault(s => s.Id == crossChild.Id) is { Provider: "claude", ParentSessionId: var crossParent } && crossParent == codexPane
                    && service.Snapshot.Sessions.First(s => s.Id == codexPane).Provider == "codex" && service.DelegationChildren.FirstOrDefault(row => row.Id == crossChild.Id)?.Agent == "claude",
                    $"The Codex pane's pick of Claude did not start a Claude child ({DelegationNames.Of(crossChild.State)}).");
                checks["otherAgentChildStarted"] = new Dictionary<string, object?> { ["parent"] = "codex", ["child"] = "claude", ["mode"] = crossChild.StartingMode, ["state"] = DelegationNames.Of(crossChild.State) };
            }
            finally { smokeAgentsReady.TryRemove("claude", out _); SetDelegationOn(false); }
            checks["passed"] = true;
            return checks;
        }
        finally
        {
            smokeDelegationDialog = null; smokeSend = null; service.Providers.Override("claude", null);
            if (!closing)
            {
                await service.UpdateAsync(s => s with { ActiveWorkspaceId = s.Workspaces.Any(w => w.Id == before.ActiveWorkspaceId) ? before.ActiveWorkspaceId : s.ActiveWorkspaceId, ActiveSessionId = s.Sessions.Any(p => p.Id == before.ActiveSessionId) ? before.ActiveSessionId : s.ActiveSessionId });
                Render();
            }
        }
    }

    /// <summary>The human's merge of <paramref name="id"/> from its card, which must succeed; the offer to discard the child at once that follows is put off.</summary>
    private async Task DelegationSmokeMerge(string id)
    {
        Render();
        Require(DelegationSmokeFind(workspaces, "child-card-merge-" + id) is Button, "The child's card offers no merge: " + id);
        ContentDialog? offered = null; var driver = smokeDelegationDialog;
        smokeDelegationDialog = dialog => { offered = dialog; return Task.FromResult(ContentDialogResult.None); };
        try { await PerformCardAction(DelegationCardAction.Merge, id); }
        finally { smokeDelegationDialog = driver; }
        Require(offered is { Content: TextBlock words } && AutomationProperties.GetAutomationId(offered) == "delegation-merged-discard-" + id && offered.PrimaryButtonText == Locale.Get("delegation.mergedDiscard.confirm")
            && offered.CloseButtonText == Locale.Get("delegation.mergedDiscard.later") && words.Text.Contains(ChildRecord.BranchName(id), StringComparison.Ordinal), "The merge did not offer to discard the merged child: " + id);
        var note = await DelegationSmokeWait("the card merge of " + id, 30, () => service.CardNote(id));
        Require(note.Result is DelegationCardResult.Done, $"The card merge of {id} was {note.Result}.");
        Render();
    }

    /// <summary>Waits for the close of the merged child <paramref name="id"/> to clean it up, and checks what it left: no pane, worktree or branch, a closed record and its merge.</summary>
    private async Task<bool> DelegationSmokeCleaned(string id, DelegationCoordinator delegation, string repo)
    {
        var outcome = await DelegationSmokeWait(id + "'s cleanup", 60, () => service.ChildClose(id) is { IsCompleted: true } close ? close.Result : null);
        Require(outcome is ChildCleanupOutcome.Cleaned, $"Closing merged child {id} gave {outcome}.");
        var file = delegation.File; var child = file.Children.FirstOrDefault(record => record.Id == id);
        var branch = child is null ? null : await DelegationGit.Run(["rev-parse", "-q", "--verify", "refs/heads/" + child.Branch], repo);
        Require(child is { State: ChildState.Closed } && file.Merges.Any(merge => merge.ChildId == id) && !Directory.Exists(child.WorktreePath) && branch is { ExitCode: 1 } && !service.Snapshot.Sessions.Any(s => s.Id == id),
            $"Merged child {id}'s close left its pane, worktree or branch, or lost its record.");
        return true;
    }

    /// <summary>The window's removal of the workspace, which must be refused with delegation's message and change nothing.</summary>
    private async Task<bool> DelegationSmokeRemovalRefused(string workspaceId)
    {
        var expected = Locale.Get("delegation.workspace.hasChildren", new Dictionary<string, string> { ["code"] = DelegationNames.Of(DelegationReasonCode.WorkspaceHasChildren) });
        try { await RemoveWorkspaceConfirmed(workspaceId); }
        catch (InvalidOperationException ex) when (ex.Message == expected) { return service.Snapshot.Workspaces.Any(w => w.Id == workspaceId); }
        return false;
    }

    private static async Task<T> DelegationSmokeWait<T>(string what, double seconds, Func<T?> probe) where T : class
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            if (probe() is { } found) return found;
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Timed out waiting for " + what + ".");
            await Task.Delay(50);
        }
    }

    private static Task DelegationSmokeUntil(string what, double seconds, Func<bool> condition) => DelegationSmokeWait(what, seconds, () => condition() ? what : null);

    private static Task DelegationSmokeCommit(string folder, string message) => DelegationSmokeGit(folder, "-c", "user.name=Mighty Smoke", "-c", "user.email=smoke@example.invalid", "commit", "-q", "-m", message);

    /// <summary>Git in <paramref name="folder"/> the way delegation runs it; its trimmed output, or a throw when it fails.</summary>
    private static async Task<string> DelegationSmokeGit(string folder, params string[] arguments) =>
        await DelegationGit.Run(arguments, folder, 60) is { ExitCode: 0 } done ? done.Stdout.Trim() : throw new InvalidOperationException("git " + string.Join(' ', arguments) + " failed.");

    /// <summary>The element under <paramref name="node"/> with this automation id, through panels, borders and button contents.</summary>
    private static DependencyObject? DelegationSmokeFind(DependencyObject node, string automationId)
    {
        if (AutomationProperties.GetAutomationId(node) == automationId) return node;
        IEnumerable<object?> parts = node switch { Panel panel => panel.Children, Border border => [border.Child], ContentControl control => [control.Content], _ => [] };
        return parts.OfType<DependencyObject>().Select(part => DelegationSmokeFind(part, automationId)).FirstOrDefault(found => found is not null);
    }

    private sealed partial class PaneView
    {
        internal void DelegationSmokeRunInNewWorktree(string text, string? agent = null) => RunInNewWorktree(text, [], agent);

        /// <summary>The worktree menu's agent rows as this pane fills them on opening: Claude, Codex, Antigravity, by their automation ids.</summary>
        internal IReadOnlyList<MenuFlyoutItem> DelegationSmokeWorktreeMenu()
        {
            FillWorktreeMenu();
            return worktreeMenu.Items.OfType<MenuFlyoutItem>().Where(item => AutomationProperties.GetAutomationId(item).StartsWith("composer-worktree-", StringComparison.Ordinal)).ToList();
        }

        /// <summary>Whether the held item <paramref name="itemId"/> is the first row of this pane's queue, under its header, pinned and with no remove button.</summary>
        internal bool DelegationSmokeHeldRow(string itemId)
        {
            RenderQueuedInputs();
            return queuedInputHost.Visibility == Visibility.Visible && queuedInputHost.Children.Count >= 2 && queuedInputHost.Children[1] is Border { Child: Grid row }
                && AutomationProperties.GetAutomationId(row) == "queue-held-" + itemId && row.Children.OfType<FontIcon>().Any() && !row.Children.OfType<Button>().Any();
        }
    }
}
