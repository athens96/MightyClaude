using MightyClaude.Core;

namespace MightyClaude.WinUI;

/// <summary>
/// The composer's "send to" switch on a Claude agent pane (M/MightyClaude/AppStore+AskTargets.swift): another installed CLI asked
/// through oh-my-claudecode's ask skill (<see cref="AskTargets"/>). Which target executables this PC has is read off the UI thread
/// with the slash catalogue, at most every 30 s; whether a pane's catalogue has the skill is noted by each scan (SlashCatalogCache).
/// A saved choice that is no longer offered reads as Claude, and the pane says so once.
/// </summary>
public sealed partial class MainWindow
{
    private IReadOnlySet<string> installedAskExecutables = new HashSet<string>(StringComparer.Ordinal);
    /// <summary>Until the PATH was read conclusively once, no saved target is judged missing.</summary>
    private bool askProbeFinished, askProbeInFlight;
    /// <summary>When the PATH was last read successfully (a pane's choice can be judged from then on), and when a read was last tried (the 30 s pace).</summary>
    private DateTime? askProbedAt, askProbeTriedAt;
    /// <summary>A pane's notice that its saved target is gone and its sends go to Claude, until it is dismissed or another target is chosen.</summary>
    private readonly Dictionary<string, string> askFallbackNotices = new(StringComparer.Ordinal);
    /// <summary>How long a send with a saved choice not yet known to stand waits for the pane's catalogue and the PATH read (M/AppStore.swift submit).</summary>
    private static readonly TimeSpan AskWait = TimeSpan.FromSeconds(2);
    private static readonly IReadOnlySet<string> AskSkillOnly = new HashSet<string>(StringComparer.Ordinal) { AskTargets.Skill }, NoSlash = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Reads which target executables this PC's PATH has (PATH with PATHEXT and the installers' folders, as the setup screen finds a CLI). At most every 30 s.</summary>
    private void RefreshAskExecutables()
    {
        if (closing || askProbeInFlight) return;
        if (askProbeTriedAt is { } at && DateTime.UtcNow - at < TimeSpan.FromSeconds(30)) return;
        askProbeInFlight = true;
        _ = ProbeAskExecutables();
    }

    private async Task ProbeAskExecutables()
    {
        IReadOnlySet<string> found;
        try
        {
            found = await Task.Run(() =>
            {
                var environment = CliEnvironment.Current();
                return AskTargets.All.Select(t => t.Executable).Where(name => SetupProber.Executables(name, environment).Count > 0).ToHashSet(StringComparer.Ordinal);
            });
        }
        // A read that failed leaves the PATH unknown: a saved choice is never taken as gone, or sent unwrapped, on its account.
        catch (Exception ex) when (ex is not OutOfMemoryException) { askProbeInFlight = false; askProbeTriedAt = DateTime.UtcNow; return; }
        askProbeInFlight = false; askProbeTriedAt = askProbedAt = DateTime.UtcNow;
        if (closing) return;
        // This PC's own environment, read from the registry each time: as the Mac's login shell, it may judge a target gone.
        var read = AskTargets.Probed(installedAskExecutables, found, loginShell: true);
        if (!installedAskExecutables.SetEquals(read.Installed)) { installedAskExecutables = read.Installed; AskTargetsChanged(); }
        // An inconclusive read never judges a choice gone.
        if (!read.Conclusive) return;
        askProbeFinished = true;
        ReconcileAskTargets();
    }

    /// <summary>The folder a pane's catalogue is kept under: its workspace's, none for a remote one (as <c>SlashWorkspacePath</c>).</summary>
    private string? AskCatalogPath(RunSession pane) => LocalPath(service.Snapshot.Workspaces.FirstOrDefault(w => w.Id == pane.WorkspaceId));

    /// <summary>A Claude agent pane of this PC: a remote workspace's pane runs on another computer, whose CLIs this PC cannot see.</summary>
    private bool AsksOtherCli(RunSession pane) => pane.Kind == "claude" && pane.Provider == "claude" && !service.IsRemoteWorkspace(pane.WorkspaceId);

    /// <summary>The targets a pane's switch offers, in the menu's order; empty hides it.</summary>
    internal IReadOnlyList<AskTarget> AskTargetsFor(RunSession pane)
    {
        // Cheapest tests first: the composer asks on every refresh and the phone four times a second.
        if (installedAskExecutables.Count == 0 || !AsksOtherCli(pane)) return [];
        var offers = slashCatalogs.OffersAsk(pane.Provider, AskCatalogPath(pane)) == true;
        return AskTargets.Available(true, offers ? AskSkillOnly : NoSlash, installedAskExecutables);
    }

    /// <summary>The target a pane's sends go to now; null for Claude.</summary>
    internal AskTarget? AskSendTarget(RunSession pane) => pane.AskTarget is null ? null : AskTargets.Effective(pane.AskTarget, AskTargetsFor(pane));

    /// <summary>Whether a pane's choice can be judged now: the PATH read once and its catalogue scanned.</summary>
    private bool AskTargetsKnown(RunSession pane) => askProbedAt is not null && slashCatalogs.OffersAsk(pane.Provider, AskCatalogPath(pane)) is not null;

    /// <summary>
    /// Waits briefly until a pane with a saved choice knows whether it still stands, so a send never goes unwrapped just because its
    /// workspace was not scanned yet (M/AppStore+AskTargets.swift awaitAskTargets). Panes on Claude return at once.
    /// </summary>
    private async Task AwaitAskTargets(string id)
    {
        // A remote workspace's pane never asks another CLI, so there is nothing to wait for.
        if (service.Snapshot.Sessions.FirstOrDefault(s => s.Id == id) is not { AskTarget: not null } pane || !AsksOtherCli(pane) || AskTargetsKnown(pane)) return;
        RefreshAskCatalog(pane); RefreshAskExecutables();
        var deadline = DateTime.UtcNow + AskWait;
        while (!closing && DateTime.UtcNow < deadline && service.Snapshot.Sessions.FirstOrDefault(s => s.Id == id) is { } current && !AskTargetsKnown(current)) await Task.Delay(25);
    }

    /// <summary>
    /// Scans a Claude agent pane's catalogue so its switch can show: when it was never scanned, or with <paramref name="whenStale"/>
    /// also once it is older than 30 s (a phone reading the pane, as the Mac's mobileSession does). The PATH is read again with it.
    /// </summary>
    private void RefreshAskCatalog(RunSession pane, bool whenStale = true)
    {
        if (closing || !AsksOtherCli(pane)) return;
        var path = AskCatalogPath(pane);
        if (whenStale ? !slashCatalogs.IsStale(pane.Provider, path) : slashCatalogs.OffersAsk(pane.Provider, path) is not null) return;
        var key = pane.Provider + "|" + (path ?? "");
        if (!slashScansInFlight.Add(key)) return;
        // Only a scan that starts reads the PATH again with it (M/AppStore+SlashCommands.swift refreshSlashCommands).
        RefreshAskExecutables();
        _ = ScanAskCatalog(pane.Provider, path, key);
    }

    private async Task ScanAskCatalog(string provider, string? path, string key)
    {
        try
        {
            var commands = await Task.Run(() => SlashCommandCatalog.Commands(provider, path));
            if (!closing) SetSlashCatalog(provider, path, commands);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* No catalogue: the switch stays hidden. */ }
        finally { slashScansInFlight.Remove(key); }
    }

    /// <summary>
    /// Keeps a scanned catalogue. Whether oh-my-claudecode's ask is in it decides the switch, here and on the phone, so a change
    /// redraws both, and a saved choice is judged again (M/AppStore+SlashCommands.swift refreshSlashCommands).
    /// </summary>
    private void SetSlashCatalog(string provider, string? path, SlashCommand[] commands)
    {
        var had = slashCatalogs.OffersAsk(provider, path);
        slashCatalogs.Set(provider, path, commands);
        RefreshAskExecutables();
        if (had != slashCatalogs.OffersAsk(provider, path)) AskTargetsChanged();
        ReconcileAskTargets();
    }

    /// <summary>What a switch offers moved: every pane's composer is drawn again and the phones are told.</summary>
    private void AskTargetsChanged()
    {
        foreach (var view in views.Values.ToArray()) view.AskTargetsChanged();
        mobileRouter?.Changed();
    }

    /// <summary>
    /// A saved target that is no longer offered (oh-my-claudecode or its CLI gone) reads as Claude: the pane says so once and the
    /// choice is cleared. Judged only after the PATH was read and the pane's catalogue scanned, so a launch never throws a good choice
    /// away before either is known.
    /// </summary>
    private void ReconcileAskTargets()
    {
        if (!askProbeFinished || closing) return;
        foreach (var pane in service.Snapshot.Sessions.Where(s => s.AskTarget is not null).ToArray())
        {
            var scanned = AsksOtherCli(pane) && slashCatalogs.OffersAsk(pane.Provider, AskCatalogPath(pane)) is not null;
            if (!AskTargets.FellBack(pane.AskTarget, AskTargetsFor(pane), askProbeFinished, scanned) || pane.AskTarget is not { } saved) continue;
            var name = AskTargets.All.FirstOrDefault(t => t.Id == saved)?.Label ?? saved;
            askFallbackNotices[pane.Id] = Locale.Get("composer.ask.fellBack", new Dictionary<string, string> { ["name"] = name });
            _ = Act(() => SetAskTarget(pane.Id, null));
        }
    }

    /// <summary>Saves a pane's choice (null: Claude) and redraws its composer and the phones. A target the pane does not offer is refused.</summary>
    private async Task SetAskTarget(string id, string? target)
    {
        if (service.Snapshot.Sessions.FirstOrDefault(s => s.Id == id) is not { } pane) throw new InvalidOperationException("Session not found.");
        if (target is not null)
        {
            if (!AskTargetsFor(pane).Any(t => t.Id == target)) throw new ArgumentException(Locale.Get("remote.error.badBody"));
            askFallbackNotices.Remove(id);
        }
        if (pane.AskTarget != target) await service.UpdateAsync(s => s with { Sessions = s.Sessions.Select(p => p.Id == id ? p with { AskTarget = target } : p).ToList() });
        if (views.TryGetValue(id, out var view)) view.AskTargetsChanged();
        mobileRouter?.Changed();
    }

    /// <summary>Takes a pane's fallback notice down.</summary>
    private void DismissAskNotice(string id)
    {
        if (!askFallbackNotices.Remove(id)) return;
        if (views.TryGetValue(id, out var view)) view.AskTargetsChanged();
    }

    private sealed partial class PaneView
    {
        /// <summary>The switch's targets, the choice or a notice moved: the composer (and the page's copy of it) is drawn again.</summary>
        internal void AskTargetsChanged() { if (QueuePaneAlive && Session.Kind == "claude") RefreshComposerState(); }

        /// <summary>The target a send from this pane asks now, null for Claude; only a Claude agent pane has one.</summary>
        private AskTarget? AskingTarget => owner.AskSendTarget(Session);
    }
}
