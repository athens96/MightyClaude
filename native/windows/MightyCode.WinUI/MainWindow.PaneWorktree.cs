using System.Diagnostics;
using System.Text.Json;
using MightyCode.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace MightyCode.WinUI;

/// <summary>
/// A workspace's agent panes that work in their own worktree (PaneWorktree.cs; the Mac's AppStore+PaneWorktree.swift and
/// PaneWorktreeBar.swift): made when the pane is added, merged into the workspace branch, committed, undone or discarded from
/// the bar over the pane, given their own PORT, the files the workspace copies into them and its setup command, kept on the
/// workspace when their pane closes with work left, and listed with the ones git still has (docs/devtool-features.md §12).
/// </summary>
public sealed partial class MainWindow
{
    private readonly HashSet<string> paneWorktreeBusy = [];
    /// <summary>The panes whose last merge was refused for a conflict: their bar offers to ask the agent to resolve it.</summary>
    private readonly HashSet<string> paneWorktreeConflicts = [];
    private static string PaneWorktreeRoot => ChildWorktreeMaker.DefaultRoot;

    private static LogEntry SystemLog(string text, string? provider = null) => new(Wire.Id(), "system", text, Wire.Now(), provider);
    private static Dictionary<string, string> Words(params (string Key, string Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);

    /// <summary>A system line in the pane's transcript.</summary>
    private Task NotePane(string id, string text) => service.UpdateAsync(s => s with
    {
        Sessions = s.Sessions.Select(x => x.Id == id ? x with { Logs = [.. x.Logs, SystemLog(text, x.Provider)] } : x).ToList(),
    });

    private Task UpdatePaneWorktree(string id, Func<PaneWorktree, PaneWorktree> change) => service.UpdateAsync(s => s with
    {
        Sessions = s.Sessions.Select(x => x.Id == id && x.PaneWorktree is { } pane ? x with { PaneWorktree = change(pane) } : x).ToList(),
    });

    private Task UpdateWorkspace(string id, Func<Workspace, Workspace> change) => service.UpdateAsync(s => s with
    {
        Workspaces = s.Workspaces.Select(w => w.Id == id ? change(w) : w).ToList(),
    });

    /// <summary>
    /// A new (not resumed) agent pane for the active workspace, where <see cref="PaneWorktreePolicy.Decide"/> says: the workspace
    /// folder, a new worktree, or after the person chose. True when a pane was added.
    /// </summary>
    private async Task<bool> AddNewAgentPane(string provider, string? groupId = null)
    {
        var state = service.Snapshot;
        if (state.ActiveWorkspaceId is not { } workspaceId || state.Workspaces.FirstOrDefault(w => w.Id == workspaceId) is not { } workspace) return await AddPane("claude", provider, groupId);
        switch (PaneWorktreePolicy.Decide(workspace, state.Sessions))
        {
            case PaneWorktreeDecision.SameFolder: return await AddPane("claude", provider, groupId);
            case PaneWorktreeDecision.Worktree: return await OpenAgentPaneInWorktree(workspace, provider, groupId, fallBack: true);
        }
        var refusal = await new ChildWorktreeMaker(PaneWorktreeRoot).Check(LocalFolder(workspace)) is ChildWorktreeCheck.Refused refused ? DelegationText.Reason(refused.Reason) : null;
        switch (await AskPaneWorktreeChoice(refusal))
        {
            case "worktree" when refusal is null: return await OpenAgentPaneInWorktree(workspace, provider, groupId, fallBack: false);
            case "always" when refusal is null:
                await UpdateWorkspace(workspace.Id, w => w with { PaneWorktreeMode = PaneWorktreeMode.Further });
                return await OpenAgentPaneInWorktree(workspace, provider, groupId, fallBack: false);
            case "same": return await AddPane("claude", provider, groupId);
            default: return false;
        }
    }

    /// <summary>Where the new pane works: <c>worktree</c>, <c>always</c>, <c>same</c>, or null when the person cancelled.</summary>
    private async Task<string?> AskPaneWorktreeChoice(string? refusal)
    {
        if (dialogOpen) return null;
        var message = refusal is null ? Locale.Get("paneWorktree.choice.detail") : Locale.Get("paneWorktree.choice.notGit", Words(("reason", refusal)));
        dialogOpen = true;
        try
        {
            if (webShell is not null)
            {
                var (_, answer) = OpenWebDialog(new
                {
                    type = "paneWorktreeChoice", title = Locale.Get("paneWorktree.choice.title"), message, canWorktree = refusal is null,
                    worktree = Locale.Get("paneWorktree.choice.worktree"), always = Locale.Get("paneWorktree.choice.always"),
                    sameFolder = Locale.Get("paneWorktree.choice.sameFolder"), cancel = Locale.Get("resume.cancel"),
                });
                var result = await answer;
                return result.ValueKind == JsonValueKind.String ? result.GetString() : null;
            }
            var dialog = new ContentDialog
            {
                Title = Locale.Get("paneWorktree.choice.title"), Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = refusal is null ? Locale.Get("paneWorktree.choice.worktree") : "", SecondaryButtonText = Locale.Get("paneWorktree.choice.sameFolder"),
                CloseButtonText = Locale.Get("resume.cancel"), DefaultButton = refusal is null ? ContentDialogButton.Primary : ContentDialogButton.Secondary, XamlRoot = root.XamlRoot,
            };
            return await dialog.ShowAsync() switch { ContentDialogResult.Primary => "worktree", ContentDialogResult.Secondary => "same", _ => null };
        }
        finally { dialogOpen = false; }
    }

    /// <summary>
    /// Makes the worktree first (branch <c>mighty/&lt;id&gt;</c> at the workspace branch's head), then the pane that works in it. A worktree the
    /// person chose that cannot be made opens no pane; one the workspace's mode chose (<paramref name="fallBack"/>) opens the pane in the folder and says why.
    /// </summary>
    private async Task<bool> OpenAgentPaneInWorktree(Workspace workspace, string provider, string? groupId, bool fallBack, string? reuseId = null, PaneWorktree? reuse = null, string? title = null)
    {
        var id = reuseId ?? Wire.Id();
        PaneWorktree record;
        if (reuse is not null) record = reuse;
        else
        {
            var maker = new ChildWorktreeMaker(PaneWorktreeRoot);
            string? reason = null; ChildWorktree? made = null; var checkout = "";
            switch (await maker.Check(LocalFolder(workspace)))
            {
                case ChildWorktreeCheck.Ready ready:
                    checkout = ready.Base.Repository;
                    switch (await maker.MakePane(id, ready.Base))
                    {
                        case ChildWorktreeOutcome.Created created: made = created.Worktree; break;
                        case ChildWorktreeOutcome.Refused why: reason = DelegationText.Reason(why.Reason); break;
                        case ChildWorktreeOutcome.Failed failed: reason = failed.Message; break;
                    }
                    break;
                case ChildWorktreeCheck.Refused refused: reason = DelegationText.Reason(refused.Reason); break;
            }
            if (made is null)
            {
                if (fallBack) return await AddPane("claude", provider, groupId, pane => pane with { Logs = [.. pane.Logs, SystemLog(Locale.Get("paneWorktree.log.fellBack", Words(("reason", reason ?? ""))), provider)] });
                error.Text = Locale.Get("paneWorktree.error.cannotMake", Words(("reason", reason ?? "")));
                return false;
            }
            var taken = service.Snapshot.Sessions.Select(s => s.PaneWorktree?.Port).OfType<int>().ToHashSet();
            record = PaneWorktree.From(made, checkout, PaneWorktreePorts.Port(id, taken), workspace.WorktreeSetupCommand);
        }
        var words = Words(("branch", record.Branch), ("base", record.BaseBranch), ("port", record.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var added = await AddPane("claude", provider, groupId, pane => pane with
        {
            Id = id, Title = title ?? pane.Title, WorkingFolder = record.RunFolder, PaneWorktree = record,
            Logs = [.. pane.Logs, SystemLog(Locale.Get(reuse is null ? "paneWorktree.log.made" : "paneWorktree.log.reopened", words), provider)],
        });
        if (!service.Snapshot.Sessions.Any(s => s.Id == id))
        {
            // The pane could not be added (the pane limit): a worktree made for it goes again; a kept one stays kept.
            if (reuse is null) await PaneWorktreeGit.Discard(record, id, PaneWorktreeRoot);
            return false;
        }
        if (reuse is not null) await UpdateWorkspace(workspace.Id, w => w with { KeptPaneWorktrees = w.KeptPaneWorktrees?.Where(k => k.SessionId != id).ToList() });
        else _ = PreparePaneWorktree(id, workspace, record);
        return added;
    }

    /// <summary>
    /// The worktree a pane added where nobody can be asked (the phone) works in, when the workspace's mode says so: its record,
    /// or why the pane works in the folder instead; (null, null) for the folder by the rule.
    /// </summary>
    private async Task<(PaneWorktree? Record, string? FellBack)> WorktreeWithoutAsking(Workspace workspace, string id)
    {
        if (PaneWorktreePolicy.DecideWithoutAsking(workspace, service.Snapshot.Sessions) != PaneWorktreeDecision.Worktree) return (null, null);
        var maker = new ChildWorktreeMaker(PaneWorktreeRoot);
        if (await maker.Check(LocalFolder(workspace)) is not ChildWorktreeCheck.Ready ready) return (null, Locale.Get("paneWorktree.log.fellBack", Words(("reason", DelegationText.Reason(DelegationReasonCode.NotGit)))));
        switch (await maker.MakePane(id, ready.Base))
        {
            case ChildWorktreeOutcome.Created created:
                var taken = service.Snapshot.Sessions.Select(s => s.PaneWorktree?.Port).OfType<int>().ToHashSet();
                return (PaneWorktree.From(created.Worktree, ready.Base.Repository, PaneWorktreePorts.Port(id, taken), workspace.WorktreeSetupCommand), null);
            case ChildWorktreeOutcome.Refused refused: return (null, Locale.Get("paneWorktree.log.fellBack", Words(("reason", DelegationText.Reason(refused.Reason)))));
            case ChildWorktreeOutcome.Failed failed: return (null, Locale.Get("paneWorktree.log.fellBack", Words(("reason", failed.Message))));
        }
        return (null, null);
    }

    /// <summary>The workspace's files copied into the new worktree (PaneWorktreeCopy), then its setup command once, in the pane's run folder.</summary>
    private async Task PreparePaneWorktree(string id, Workspace workspace, PaneWorktree record)
    {
        try
        {
            if (workspace.WorktreeCopyPaths is { Count: > 0 } paths)
            {
                var copied = await Task.Run(() => PaneWorktreeCopy.Copy(paths, LocalFolder(workspace), record.RunFolder));
                if (copied.Copied.Count > 0) await NotePane(id, Locale.Get("paneWorktree.log.copied", Words(("paths", string.Join(", ", copied.Copied)))));
                if (copied.Skipped.Count > 0)
                    await NotePane(id, Locale.Get("paneWorktree.log.copySkipped", Words(("paths", string.Join(", ", copied.Skipped.Select(p => p.Key + " (" + Locale.Get("paneWorktree.copy.skip." + p.Value) + ")"))))));
            }
            if (record.SetupCommand is not { } command) return;
            await NotePane(id, Locale.Get("paneWorktree.log.setupStarted", Words(("command", command))));
            var (code, tail) = await Task.Run(() => RunSetupCommand(command, record.RunFolder, record.Environment(LocalFolder(workspace))));
            if (!service.Snapshot.Sessions.Any(s => s.Id == id)) return;
            await UpdatePaneWorktree(id, pane => pane with { SetupStatus = code == 0 ? PaneWorktree.Done : PaneWorktree.Failed, SetupExitCode = code });
            var text = code == 0 ? Locale.Get("paneWorktree.log.setupDone", Words(("command", command)))
                : Locale.Get("paneWorktree.log.setupFailed", Words(("command", command), ("code", code.ToString(System.Globalization.CultureInfo.InvariantCulture))));
            await NotePane(id, tail.Length == 0 ? text : text + "\n\n" + tail);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Trace.WriteLine("pane worktree setup: " + ex.Message); }
    }

    /// <summary>The setup command through <c>cmd /d /s /c</c> (the shell the app's commands run in), 30 minutes at most: its exit code and last 12 lines.</summary>
    private static async Task<(int Code, string Tail)> RunSetupCommand(string command, string folder, IReadOnlyDictionary<string, string> environment)
    {
        try
        {
            var shell = OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe" : "/bin/sh";
            var info = ChildProcess.StartInfo(shell, OperatingSystem.IsWindows() ? ["/d", "/s", "/c", command] : ["-c", command], folder);
            foreach (var (key, value) in environment) info.Environment[key] = value;
            await using var process = ChildProcess.Start(info); process.Input.Close();
            var output = process.Output.ReadToEndAsync(); var errors = process.Error.ReadToEndAsync();
            int code;
            try { code = await process.Completion.WaitAsync(TimeSpan.FromMinutes(30)); }
            catch (TimeoutException) { process.Kill(); code = -1; }
            var text = (await output) + "\n" + (await errors);
            var tail = string.Join('\n', text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(12));
            return (code, tail.Length > 8000 ? tail[^8000..] : tail);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return (-1, ex.Message); }
    }

    /// <summary>What the bar over a worktree pane shows beside its record: how far it is from its base, whether an action runs, whether the last merge conflicted.</summary>
    private async Task<object?> PaneWorktreeStatusModel(string id)
    {
        if (service.Snapshot.Sessions.FirstOrDefault(s => s.Id == id)?.PaneWorktree is not { } pane) return null;
        var status = await PaneWorktreeGit.Status(pane);
        return new
        {
            ahead = status?.Ahead ?? 0, behind = status?.Behind ?? 0, changed = status?.ChangedFiles ?? 0, known = status is not null,
            busy = paneWorktreeBusy.Contains(id), conflict = paneWorktreeConflicts.Contains(id), canUndo = pane.MergedCommit is not null && pane.PreMergeCommit is not null,
        };
    }

    /// <summary>One of the bar's actions on the worktree pane <paramref name="id"/>.</summary>
    private async Task PaneWorktreeAction(string id, string action)
    {
        if (service.Snapshot.Sessions.FirstOrDefault(s => s.Id == id) is not { PaneWorktree: { } pane } session) return;
        switch (action)
        {
            case "merge": await Busy(id, () => MergePane(id, pane, commitFirst: false)); break;
            case "commitMerge": await Busy(id, () => MergePane(id, pane, commitFirst: true)); break;
            case "resolve": await AskToResolve(session, pane); break;
            case "undo": await Busy(id, () => UndoPaneMerge(id, pane)); break;
            case "pr": await CreatePullRequest(id, pane); break;
            case "discard": await DiscardPaneWorktree(session, pane); break;
            case "changes": await ShowPaneWorktreeChanges(pane); break;
            case "terminal":
                await AddPane("shell", shape: shell => shell with { WorkingFolder = pane.RunFolder, Title = Locale.Get("paneWorktree.terminal.title", Words(("branch", pane.Branch))) });
                break;
            case "port": await Launcher.LaunchUriAsync(new Uri("http://localhost:" + pane.Port.ToString(System.Globalization.CultureInfo.InvariantCulture))); break;
            case "reveal": try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + pane.RunFolder + "\"") { UseShellExecute = true }); } catch (Exception ex) when (ex is not OutOfMemoryException) { error.Text = ex.Message; } break;
        }
        PushPaneWorktree(id);
    }

    private async Task Busy(string id, Func<Task> work)
    {
        if (!paneWorktreeBusy.Add(id)) return;
        PushPaneWorktree(id);
        try { await work(); } finally { paneWorktreeBusy.Remove(id); }
    }

    /// <summary>The page's bar reads its status again when its call returns and every 20 seconds; the pane's record reaches it with the state.</summary>
    private static void PushPaneWorktree(string id) { _ = id; }

    private async Task MergePane(string id, PaneWorktree pane, bool commitFirst)
    {
        if (commitFirst)
        {
            switch (await PaneWorktreeGit.CommitAll(pane))
            {
                case PaneWorktreeCommitOutcome.Unmerged: await NotePane(id, Locale.Get("paneWorktree.refused.unmerged")); return;
                case PaneWorktreeCommitOutcome.Failed failed: await NotePane(id, Locale.Get("paneWorktree.error.commitFailed", Words(("reason", failed.Message)))); return;
                case PaneWorktreeCommitOutcome.Committed { LeftOut.Count: 0 }: await NotePane(id, Locale.Get("paneWorktree.log.committed")); break;
                case PaneWorktreeCommitOutcome.Committed { LeftOut: var left }: await NotePane(id, Locale.Get("paneWorktree.log.committed")); await NotePane(id, Locale.Get("paneWorktree.log.leftOut", Words(("paths", string.Join(", ", left))))); return;
                case PaneWorktreeCommitOutcome.Nothing { LeftOut.Count: > 0 } nothing: await NotePane(id, Locale.Get("paneWorktree.log.leftOut", Words(("paths", string.Join(", ", nothing.LeftOut))))); return;
            }
        }
        switch (await PaneWorktreeGit.Merge(pane))
        {
            case PaneWorktreeMergeOutcome.Merged merged:
                paneWorktreeConflicts.Remove(id);
                await UpdatePaneWorktree(id, p => p with { MergedCommit = merged.Commit, PreMergeCommit = merged.PreMerge });
                await NotePane(id, Locale.Get("paneWorktree.log.merged", Words(("branch", pane.Branch), ("base", pane.BaseBranch), ("commit", merged.Commit[..8]))));
                break;
            case PaneWorktreeMergeOutcome.Refused refused:
                if (refused.Reason == PaneWorktreeRefusal.MergeConflict) paneWorktreeConflicts.Add(id); else paneWorktreeConflicts.Remove(id);
                await NotePane(id, PaneWorktreeRefusal.Text(refused.Reason, pane.BaseBranch));
                break;
            case PaneWorktreeMergeOutcome.Failed failed:
                await NotePane(id, Locale.Get("paneWorktree.error.mergeFailed", Words(("reason", failed.Message))));
                break;
        }
    }

    private async Task UndoPaneMerge(string id, PaneWorktree pane)
    {
        switch (await PaneWorktreeGit.UndoMerge(pane))
        {
            case PaneWorktreeUndoOutcome.Undone:
                await UpdatePaneWorktree(id, p => p with { MergedCommit = null, PreMergeCommit = null });
                await NotePane(id, Locale.Get("paneWorktree.log.undone", Words(("base", pane.BaseBranch))));
                break;
            case PaneWorktreeUndoOutcome.Refused refused: await NotePane(id, Locale.Get("paneWorktree.undo.refused." + refused.Reason, Words(("base", pane.BaseBranch)))); break;
            case PaneWorktreeUndoOutcome.Failed failed: await NotePane(id, Locale.Get("paneWorktree.error.undoFailed", Words(("reason", failed.Message)))); break;
        }
    }

    /// <summary>The conflict request as the pane's next run (PaneWorktreeNote.ConflictPrompt); a pane that cannot take a run now says why.</summary>
    private async Task AskToResolve(RunSession session, PaneWorktree pane)
    {
        try
        {
            await service.StartAsync(new StartRunRequest(session.Id, session.WorkspaceId, "claude", PaneWorktreeNote.ConflictPrompt(pane), [], session.Model, session.Provider, session.Settings, session.ResumeId));
            paneWorktreeConflicts.Remove(session.Id);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { await NotePane(session.Id, Locale.Get("paneWorktree.conflict.notSent", Words(("reason", ex.Message)))); }
    }

    private async Task CreatePullRequest(string id, PaneWorktree pane)
    {
        if (await PaneWorktreePR.Origin(pane) is not { } origin) { await NotePane(id, Locale.Get("paneWorktree.pr.noOrigin")); return; }
        var confirm = new ContentDialog
        {
            Title = Locale.Get("paneWorktree.pr.title"), Content = new TextBlock { Text = Locale.Get("paneWorktree.pr.detail", Words(("branch", pane.Branch), ("base", pane.BaseBranch))), TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = Locale.Get("paneWorktree.pr.confirm"), CloseButtonText = Locale.Get("resume.cancel"), DefaultButton = ContentDialogButton.Close, XamlRoot = root.XamlRoot,
        };
        if (await ShowDelegationDialog(confirm) != ContentDialogResult.Primary) return;
        await Busy(id, async () =>
        {
            if (await PaneWorktreePR.Push(pane) is { } failed) { await NotePane(id, Locale.Get("paneWorktree.pr.failed", Words(("reason", failed)))); return; }
            if (PaneWorktreePR.GitHubRepository(origin) is { } repository)
            {
                await NotePane(id, Locale.Get("paneWorktree.pr.pushedOpening", Words(("branch", pane.Branch))));
                await Launcher.LaunchUriAsync(new Uri(PaneWorktreePR.CompareUrl(repository, pane.BaseBranch, pane.Branch)));
            }
            else await NotePane(id, Locale.Get("paneWorktree.pr.pushed", Words(("branch", pane.Branch))));
        });
    }

    /// <summary>Asks, then removes the worktree and branch and closes the pane; refused while children branch from it.</summary>
    private async Task DiscardPaneWorktree(RunSession session, PaneWorktree pane)
    {
        if (PaneWorktreePolicy.DiscardBlocker(session.Id, service.Snapshot.Sessions, service.DelegationChildren) is { } blocked) { await NotePane(session.Id, blocked); return; }
        var confirm = new ContentDialog
        {
            Title = Locale.Get("paneWorktree.discard.title"), Content = new TextBlock { Text = Locale.Get("paneWorktree.discard.detail", Words(("branch", pane.Branch))), TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = Locale.Get("paneWorktree.discard.confirm"), CloseButtonText = Locale.Get("resume.cancel"), DefaultButton = ContentDialogButton.Close, XamlRoot = root.XamlRoot,
        };
        if (await ShowDelegationDialog(confirm) != ContentDialogResult.Primary) return;
        await Busy(session.Id, async () =>
        {
            if (session.Status == "running") await service.StopAsync(session.Id);
            switch (await PaneWorktreeGit.Discard(pane, session.Id, PaneWorktreeRoot))
            {
                case ChildDiscardOutcome.Discarded:
                    // Gone already: the close must not keep it.
                    await service.UpdateAsync(s => s with { Sessions = s.Sessions.Select(x => x.Id == session.Id ? x with { PaneWorktree = null } : x).ToList() });
                    await ClosePane(session.Id);
                    break;
                case ChildDiscardOutcome.Failed failed: await NotePane(session.Id, Locale.Get("paneWorktree.error.discardFailed", Words(("reason", failed.Message)))); break;
            }
        });
    }

    /// <summary>
    /// After a worktree pane closed (rule 9): a worktree with nothing unmerged and no child on its branch goes with it; one with work or
    /// children is kept on the workspace's list (KeptPaneWorktree), and the app says where.
    /// </summary>
    private async Task PaneWorktreeClosed(RunSession closed)
    {
        if (closed.PaneWorktree is not { } pane) return;
        var children = service.Snapshot.Sessions.Any(s => s.ParentSessionId == closed.Id) || service.DelegationChildren.Any(c => c.ParentSessionId == closed.Id && c.State.IsOpen());
        if (!children && await PaneWorktreeGit.HasUnmergedWork(pane) == false)
        {
            if (await PaneWorktreeGit.Discard(pane, closed.Id, PaneWorktreeRoot) is ChildDiscardOutcome.Failed failed) error.Text = Locale.Get("paneWorktree.error.discardFailed", Words(("reason", failed.Message)));
            return;
        }
        var kept = new KeptPaneWorktree { SessionId = closed.Id, Title = closed.Title, Provider = closed.Provider, Worktree = pane };
        await UpdateWorkspace(closed.WorkspaceId, w => w with { KeptPaneWorktrees = [.. (w.KeptPaneWorktrees ?? []).Where(k => k.SessionId != closed.Id), kept] });
        error.Text = Locale.Get("paneWorktree.closedWithWork", Words(("branch", pane.Branch), ("path", pane.WorktreePath)));
    }

    /// <summary>The changes sheet: the files the pane changed against its base, each file's diff asked of <c>paneWorktreeDiff</c>.</summary>
    private async Task ShowPaneWorktreeChanges(PaneWorktree pane)
    {
        var files = await PaneWorktreeGit.ChangedFiles(pane) ?? [];
        var title = Locale.Get("paneWorktree.changes.title", Words(("branch", pane.Branch), ("base", pane.BaseBranch)));
        if (webShell is not null)
        {
            if (dialogOpen) return;
            dialogOpen = true;
            try
            {
                var paneId = service.Snapshot.Sessions.FirstOrDefault(s => s.PaneWorktree?.Branch == pane.Branch)?.Id ?? "";
                var (_, answer) = OpenWebDialog(new { type = "paneWorktreeChanges", title, paneId, files = files.Select(f => new { path = f.Path, status = f.Status }), none = Locale.Get("paneWorktree.changes.none"), pick = Locale.Get("paneWorktree.changes.pick") });
                await answer;
            }
            finally { dialogOpen = false; }
            return;
        }
        var text = files.Count == 0 ? Locale.Get("paneWorktree.changes.none") : string.Join('\n', files.Select(f => f.Status + "  " + f.Path));
        await ShowDelegationDialog(new ContentDialog { Title = title, Content = new ScrollViewer { Content = new TextBlock { Text = text, IsTextSelectionEnabled = true }, MaxHeight = 420 }, CloseButtonText = Locale.Get("resume.cancel"), XamlRoot = root.XamlRoot });
    }

    /// <summary>A one-line setting asked in a sheet: the setup command or the files copied into new worktrees. Null when cancelled.</summary>
    private async Task<string?> AskWorktreeText(string title, string detail, string current, string placeholder)
    {
        if (dialogOpen) return null;
        dialogOpen = true;
        try
        {
            if (webShell is not null)
            {
                var (_, answer) = OpenWebDialog(new { type = "paneWorktreeText", title, message = detail, current, placeholder, save = Locale.Get("paneWorktree.setup.save"), cancel = Locale.Get("resume.cancel") });
                var result = await answer;
                return result.ValueKind == JsonValueKind.String ? result.GetString() : null;
            }
            var box = new TextBox { Text = current, PlaceholderText = placeholder };
            var panel = new StackPanel { Spacing = DesignMetrics.Spacing.Sm }; panel.Children.Add(new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap }); panel.Children.Add(box);
            var dialog = new ContentDialog { Title = title, Content = panel, PrimaryButtonText = Locale.Get("paneWorktree.setup.save"), CloseButtonText = Locale.Get("resume.cancel"), DefaultButton = ContentDialogButton.Primary, XamlRoot = root.XamlRoot };
            return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text : null;
        }
        finally { dialogOpen = false; }
    }

    /// <summary>The workspace row's worktree items: its mode, its setup command, the files it copies, its worktree list.</summary>
    private async Task WorkspaceWorktreeAction(string workspaceId, string action, string? value)
    {
        if (service.Snapshot.Workspaces.FirstOrDefault(w => w.Id == workspaceId) is not { Remote: null } workspace) return;
        switch (action)
        {
            case "mode": await UpdateWorkspace(workspaceId, w => w with { PaneWorktreeMode = PaneWorktreeMode.Saved(value) }); break;
            case "setup":
                if (await AskWorktreeText(Locale.Get("paneWorktree.setup.title"), Locale.Get("paneWorktree.setup.detail"), workspace.WorktreeSetupCommand ?? "", "npm ci") is { } command)
                    await UpdateWorkspace(workspaceId, w => w with { WorktreeSetupCommand = PaneWorktreeSetup.NormalizedCommand(command) });
                break;
            case "copy":
                if (await AskWorktreeText(Locale.Get("paneWorktree.copy.title"), Locale.Get("paneWorktree.copy.detail"), string.Join(", ", workspace.WorktreeCopyPaths ?? []), ".env, .env.local") is { } list)
                    await UpdateWorkspace(workspaceId, w => w with { WorktreeCopyPaths = PaneWorktreeCopy.NormalizedPaths(PaneWorktreeCopy.Entries(list)) });
                break;
            case "list": await ShowWorktreeList(workspace); break;
        }
    }

    /// <summary>The workspace's worktree list (rule 10): each row reopens, merges or deletes; the sheet opens again after an action.</summary>
    private async Task ShowWorktreeList(Workspace workspace)
    {
        while (!closing)
        {
            var rows = await WorktreeRows(workspace);
            if (webShell is null)
            {
                var text = rows.Count == 0 ? Locale.Get("paneWorktree.list.empty") : string.Join('\n', rows.Select(r => r.Row.Worktree.Branch + " · " + Locale.Get("paneWorktree.list.origin." + r.Row.Origin) + " · " + r.Row.Worktree.WorktreePath));
                await ShowDelegationDialog(new ContentDialog { Title = Locale.Get("paneWorktree.list.title"), Content = new ScrollViewer { Content = new TextBlock { Text = text, IsTextSelectionEnabled = true }, MaxHeight = 420 }, CloseButtonText = Locale.Get("resume.cancel"), XamlRoot = root.XamlRoot });
                return;
            }
            if (dialogOpen) return;
            JsonElement answer;
            dialogOpen = true;
            try
            {
                var (_, waiting) = OpenWebDialog(new
                {
                    type = "paneWorktreeList", title = Locale.Get("paneWorktree.list.title"), empty = Locale.Get("paneWorktree.list.empty"), close = Locale.Get("resume.cancel"),
                    rows = rows.Select(r => new
                    {
                        id = r.Row.SessionId, title = r.Row.Title, branch = r.Row.Worktree.Branch, path = r.Row.Worktree.WorktreePath, origin = r.Row.Origin,
                        originText = Locale.Get("paneWorktree.list.origin." + r.Row.Origin),
                        counts = Locale.Get("paneWorktree.list.counts", Words(("ahead", N(r.Status?.Ahead)), ("behind", N(r.Status?.Behind)), ("changed", N(r.Status?.ChangedFiles)))),
                        actions = RowActions(r.Row, r.Status),
                    }),
                });
                answer = await waiting;
            }
            finally { dialogOpen = false; }
            if (answer.ValueKind != JsonValueKind.Object || answer.Text("id") is not { } id || answer.Text("action") is not { } action || rows.FirstOrDefault(r => r.Row.SessionId == id).Row is not { } row) return;
            if (!await WorktreeRowAction(workspace, row, action)) return;
            workspace = service.Snapshot.Workspaces.FirstOrDefault(w => w.Id == workspace.Id) ?? workspace;
        }
        static string N(int? value) => (value ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static object[] RowActions(PaneWorktreeInventory.Row row, PaneWorktreeStatus? status)
    {
        if (row.Origin == PaneWorktreeInventory.Open) return [new { action = "show", label = Locale.Get("paneWorktree.list.show") }];
        var merge = status is { ChangedFiles: > 0 } ? new { action = "commitMerge", label = Locale.Get("paneWorktree.bar.commitMerge") } : new { action = "merge", label = Locale.Get("paneWorktree.bar.merge") };
        return [new { action = "reopen", label = Locale.Get("paneWorktree.list.reopen") }, merge, new { action = "delete", label = Locale.Get("paneWorktree.list.delete") }];
    }

    private async Task<List<(PaneWorktreeInventory.Row Row, PaneWorktreeStatus? Status)>> WorktreeRows(Workspace workspace)
    {
        var state = service.Snapshot;
        var open = state.Sessions.Where(s => s.WorkspaceId == workspace.Id && s.PaneWorktree is not null).ToList();
        var check = await new ChildWorktreeMaker(PaneWorktreeRoot).Check(LocalFolder(workspace));
        var checkout = check is ChildWorktreeCheck.Ready ready ? ready.Base.Repository : open.FirstOrDefault()?.PaneWorktree?.Checkout ?? LocalFolder(workspace);
        var baseBranch = check is ChildWorktreeCheck.Ready readyBase ? readyBase.Base.ParentBranch : null;
        var onDisk = await PaneWorktreeGit.Worktrees(checkout, PaneWorktreeRoot) ?? [];
        var children = state.Sessions.Where(s => s.ParentSessionId is not null).Select(s => s.Id).Concat(service.DelegationChildren.Select(c => c.Id)).ToHashSet();
        var taken = state.Sessions.Select(s => s.PaneWorktree?.Port).OfType<int>().ToHashSet();
        var rows = PaneWorktreeInventory.Rows(open, workspace.KeptPaneWorktrees ?? [], onDisk, children, checkout, baseBranch, sessionId => PaneWorktreePorts.Port(sessionId, taken));
        var result = new List<(PaneWorktreeInventory.Row, PaneWorktreeStatus?)>();
        foreach (var row in rows) result.Add((row, await PaneWorktreeGit.Status(row.Worktree)));
        return result;
    }

    /// <summary>A list row's action; true when the list should open again.</summary>
    private async Task<bool> WorktreeRowAction(Workspace workspace, PaneWorktreeInventory.Row row, string action)
    {
        switch (action)
        {
            case "show": ActivatePane(row.SessionId); return false;
            case "reopen":
                if (service.Snapshot.Sessions.Any(s => s.Id == row.SessionId)) { ActivatePane(row.SessionId); return false; }
                if (service.Snapshot.ActiveWorkspaceId != workspace.Id) await SelectWorkspace(workspace.Id);
                await OpenAgentPaneInWorktree(workspace, row.Provider, null, fallBack: false, reuseId: row.SessionId, reuse: row.Worktree, title: row.Origin == PaneWorktreeInventory.Kept ? row.Title : null);
                return false;
            case "merge":
            case "commitMerge":
                if (action == "commitMerge" && await PaneWorktreeGit.CommitAll(row.Worktree) is PaneWorktreeCommitOutcome.Committed { LeftOut.Count: > 0 } or PaneWorktreeCommitOutcome.Nothing { LeftOut.Count: > 0 } or PaneWorktreeCommitOutcome.Unmerged or PaneWorktreeCommitOutcome.Failed)
                { error.Text = Locale.Get("paneWorktree.refused.uncommittedChanges"); return true; }
                switch (await PaneWorktreeGit.Merge(row.Worktree))
                {
                    case PaneWorktreeMergeOutcome.Merged merged:
                        error.Text = Locale.Get("paneWorktree.log.merged", Words(("branch", row.Worktree.Branch), ("base", row.Worktree.BaseBranch), ("commit", merged.Commit[..8])));
                        await UpdateWorkspace(workspace.Id, w => w with { KeptPaneWorktrees = w.KeptPaneWorktrees?.Select(k => k.SessionId == row.SessionId ? k with { Worktree = k.Worktree with { MergedCommit = merged.Commit, PreMergeCommit = merged.PreMerge } } : k).ToList() });
                        break;
                    case PaneWorktreeMergeOutcome.Refused refused: error.Text = PaneWorktreeRefusal.Text(refused.Reason, row.Worktree.BaseBranch); break;
                    case PaneWorktreeMergeOutcome.Failed failed: error.Text = Locale.Get("paneWorktree.error.mergeFailed", Words(("reason", failed.Message))); break;
                }
                return true;
            case "delete":
                var confirm = new ContentDialog
                {
                    Title = Locale.Get("paneWorktree.list.delete"), Content = new TextBlock { Text = Locale.Get("paneWorktree.list.deleteDetail", Words(("branch", row.Worktree.Branch))), TextWrapping = TextWrapping.Wrap },
                    PrimaryButtonText = Locale.Get("paneWorktree.list.delete"), CloseButtonText = Locale.Get("resume.cancel"), DefaultButton = ContentDialogButton.Close, XamlRoot = root.XamlRoot,
                };
                if (await ShowDelegationDialog(confirm) != ContentDialogResult.Primary) return true;
                if (await PaneWorktreeGit.Discard(row.Worktree, row.SessionId, PaneWorktreeRoot) is ChildDiscardOutcome.Failed failedDiscard)
                    error.Text = Locale.Get("paneWorktree.error.discardFailed", Words(("reason", failedDiscard.Message)));
                else await UpdateWorkspace(workspace.Id, w => w with { KeptPaneWorktrees = w.KeptPaneWorktrees?.Where(k => k.SessionId != row.SessionId).ToList() });
                return true;
        }
        return false;
    }

    /// <summary>The page's calls for pane worktrees.</summary>
    private void RegisterPaneWorktreeMethods(WebBridge bridge)
    {
        bridge.Register("paneWorktreeStatus", async args => args.Text("id") is { } id ? await PaneWorktreeStatusModel(id) : null);
        bridge.Register("paneWorktreeAction", async args => { if (args.Text("id") is { } id && args.Text("action") is { } action) await Act(() => PaneWorktreeAction(id, action)); return null; });
        bridge.Register("paneWorktreeDiff", async args =>
            args.Text("id") is { } id && args.Text("path") is { } path && service.Snapshot.Sessions.FirstOrDefault(s => s.Id == id)?.PaneWorktree is { } pane ? await PaneWorktreeGit.FileDiff(pane, path) : null);
        bridge.Register("workspaceWorktree", async args =>
        {
            if (args.Text("workspaceId") is { } workspaceId && args.Text("action") is { } action) await Act(() => WorkspaceWorktreeAction(workspaceId, action, args.Text("mode")));
            return null;
        });
    }
}
