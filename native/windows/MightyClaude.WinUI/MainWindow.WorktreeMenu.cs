using System.Collections.Concurrent;
using MightyClaude.Core;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace MightyClaude.WinUI;

/// <summary>
/// The composer's worktree button's agent menu (M/SessionPaneView.swift worktreeAgentMenu, M/AppStore+Delegation.swift
/// worktreeChildChoices/agentAvailabilityNow/returnToComposer): a click or Ctrl+Enter runs the draft in a child of the pane's own
/// agent; a right-click (or Shift+F10, or the menu key) picks the child's agent among Claude, Codex and Antigravity, each greyed out
/// with why it cannot run now. A text whose other-agent child cannot start goes back to the composer, never to the pane.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>The agents a smoke run treats as ready, in what the window learnt of their CLIs' place.</summary>
    private readonly ConcurrentDictionary<string, bool> smokeAgentsReady = new();

    /// <summary>
    /// Whether <paramref name="provider"/>'s CLI can run a child, as the window last learnt it: found by its CLI search
    /// (<see cref="runtime"/>), and not signed out by the account status Settings last read. Called from the coordinator's thread:
    /// the runtime reference and the accounts' concurrent dictionary are each read once, and no CLI is asked.
    /// </summary>
    private DelegationAgentAvailability AgentAvailabilityFromCli(string provider)
    {
        if (smokeAgentsReady.ContainsKey(provider)) return DelegationAgentAvailability.Ready;
        var known = runtime;
        return DelegationProviders.Availability(known?.Providers.FirstOrDefault(p => p.Id == provider)?.Available, accountsCoordinator.Statuses.GetValueOrDefault(provider));
    }

    private sealed partial class PaneView
    {
        /// <summary>The worktree button's agent menu: its right-click flyout, and its click's where only another agent's child fits.</summary>
        private readonly MenuFlyout worktreeMenu = new();
        /// <summary>The button shows for its menu alone: this pane's own agent cannot have a child in its mode, another can.</summary>
        private bool worktreeMenuOnly;
        /// <summary>Whether the button's tooltip names the menu alone, so it is set again only when that changes.</summary>
        private bool worktreeTipIsMenu;

        /// <summary>One row per agent under the menu's header, the pane's own marked as Ctrl+Enter's; an agent that cannot run the child now is greyed out with why.</summary>
        private void FillWorktreeMenu()
        {
            worktreeMenu.Items.Clear();
            worktreeMenu.Items.Add(MenuHeader(Locale.Get("composer.worktreeMenu.title")));
            var pane = Session;
            foreach (var choice in owner.service.WorktreeChildChoices(pane, LaunchesInPlanMode))
            {
                var agent = choice.Agent;
                var item = new MenuFlyoutItem { Text = ProviderCatalog.BetaLabel(agent, WorktreeChoiceTitle(choice, pane.Provider)), IsEnabled = choice.Blocked is null };
                item.Click += async (_, _) => await owner.Act(() => SendToWorktreeAgent(agent));
                AutomationProperties.SetAutomationId(item, $"composer-worktree-{agent}-{id}");
                worktreeMenu.Items.Add(item);
            }
        }

        /// <summary><c>Codex · Working folder</c>, <c>Claude · Accept file edits (Ctrl+Enter)</c>, or <c>Codex — not signed in</c>.</summary>
        private static string WorktreeChoiceTitle(DelegationChildChoice choice, string parent)
        {
            var words = new Dictionary<string, string> { ["agent"] = ProviderMark.Label(choice.Agent) };
            if (choice.Blocked is not { } blocked)
            {
                words["mode"] = PermissionLabel(choice.Agent, choice.Mode ?? "");
                return Locale.Get(choice.Agent == parent ? "composer.worktreeMenu.sameAgentWindows" : "composer.worktreeMenu.agent", words);
            }
            words["reason"] = Locale.Get(blocked switch
            {
                DelegationChildBlock.NotInstalled => "composer.worktreeMenu.notInstalled",
                DelegationChildBlock.SignedOut => "composer.worktreeMenu.signedOut",
                DelegationChildBlock.NoMode => "composer.worktreeMenu.noMode",
                _ => "composer.worktreeMenu.cap",
            });
            return Locale.Get("composer.worktreeMenu.blocked", words);
        }

        /// <summary>
        /// The menu's pick (M/AppStore.swift submit(agent:)): the composer's words and files run now in a new child of
        /// <paramref name="agent"/>, of another agent too, and never in this pane. Offered no more, the draft stays where it is.
        /// </summary>
        private async Task SendToWorktreeAgent(string agent)
        {
            var text = input.Text; var files = pendingAttachments.ToArray();
            if (string.IsNullOrWhiteSpace(text) || composingInput || attachmentsLoading || !owner.service.OffersWorktreeMenu(Session, LaunchesInPlanMode)) return;
            // Taken from the composer now, so a second pick cannot send it again.
            var sent = files.Select(file => file.Id).ToHashSet(); pendingAttachments.RemoveAll(file => sent.Contains(file.Id)); RefreshAttachments();
            if (input.Text == text) { updating = true; input.Text = ""; updating = false; await Change(s => s with { Draft = "" }); }
            RunInNewWorktree(text, files, agent);
            RefreshComposerState();
        }

        /// <summary>
        /// The text of a child of another agent that could not be made or started goes back to the composer with its files, ahead of
        /// anything typed there meanwhile, and the transcript says why (M/AppStore+Delegation.swift returnToComposer). It never runs in this pane.
        /// </summary>
        private async Task ReturnToComposer(string text, IReadOnlyList<RunAttachment> files, string reason)
        {
            if (!QueuePaneAlive) return;
            await SlashNote(Locale.Get("delegation.human.returnedToComposer", new Dictionary<string, string> { ["reason"] = reason }));
            var draft = input.Text.Length == 0 ? text : text + "\n\n" + input.Text;
            updating = true; input.Text = draft; updating = false; await Change(p => p with { Draft = draft });
            var kept = pendingAttachments.Select(file => file.Id).ToHashSet(); pendingAttachments.InsertRange(0, files.Where(file => !kept.Contains(file.Id))); RefreshAttachments();
            RefreshComposerState();
        }
    }
}
