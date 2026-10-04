using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private async Task<Dictionary<string, object?>> RunSmallParitySmoke(Workspace workspace)
    {
        var saved = service.Snapshot;
        var id = saved.Sessions.First(p => p.Kind == "claude" && p.WorkspaceId == workspace.Id).Id;
        try
        {
            await SelectLayoutSession(id); Render();
            var pane = views[id]; await WaitUI(() => pane.Container.IsLoaded);
            return await pane.SmokeSmallParityAsync();
        }
        finally { await service.UpdateAsync(_ => saved); Render(); }
    }
    private sealed partial class PaneView
    {
        internal async Task<Dictionary<string, object?>> SmokeSmallParityAsync()
        {
            var checks = new Dictionary<string, object?>();
            var usage = new SessionUsage { Provider = Session.Provider, Source = "synthetic-smoke", UpdatedAt = Wire.Now(), TokenScope = "session", TotalTokens = 12345, ProviderSessionId = "smoke-cli-session", ContextUsedTokens = 123, ContextWindowTokens = 1000 };
            await Change(p => p with { SessionUsage = usage }); Refresh();
            await ShowContext();
            try
            {
                Require(sessionInfoOpen && sessionInfoRows["total"].Value.Text == 12345.ToString("N0") && sessionInfoRows["cli-identity"].Value.Text == "smoke-cli-session", "Session info omitted total tokens or CLI identity.");
                Require(sessionInfoRows["path"].Value.Text == Workspace.Path && context.Content is Grid, "Session info path or circular context indicator is missing.");
                var row = sessionInfoRows["total"].Value;
                await Change(p => p with { SessionUsage = usage with { TotalTokens = 23456, ContextWindowTokens = null } }); RefreshSessionInfo();
                Require(ReferenceEquals(row, sessionInfoRows["total"].Value) && row.Text == 23456.ToString("N0") && sessionInfoRows["context"].Value.Text.Contains("123", StringComparison.Ordinal), "Live session info must update in place and keep partial context counts.");
                checks["liveSessionPopover"] = true; checks["contextRingAndIdentifiers"] = true;
            }
            finally { sessionInfoFlyout?.Hide(); }
            Require(attach.ContextFlyout is MenuFlyout { Items.Count: 4 }, "Attachment menu must include file picker, paste and remove-all.");
            checks["attachmentMenu"] = true;
            var pixels = await SmokePng(10, 8); var originalPath = Path.Combine(Workspace.Path, "smoke-image-external-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                await File.WriteAllBytesAsync(originalPath, pixels);
                var action = new TranscriptAction("image", originalPath);
                var revealed = ImageExternalPath(action, Workspace.Path, pixels, "image/png", revealOriginal: true);
                var opened = ImageExternalPath(action, Workspace.Path, pixels, "image/png", revealOriginal: false);
                Require(WorkspaceFiles.RealPath(originalPath) == revealed && WorkspaceFiles.Contains(opened, WorkspaceFiles.RealPath(owner.service.Images.Directory)!) && AgentImageSupport.Sha256(await File.ReadAllBytesAsync(opened)) == AgentImageSupport.Sha256(pixels),
                    "Image reveal must use the bounded original while external viewing uses the verified cache snapshot.");
                checks["imageExternalVerifiedSnapshot"] = true;
            }
            finally { File.Delete(originalPath); }
            var registry = new AgentIOBindings();
            var binding = registry.Bind(id, Workspace, "codex", "smoke", Path.Combine(owner.StateDirectory, "fixture.exe"));
            var one = owner.agentUrlPrompts.RequestAsync(binding, new Uri("https://example.com/first"), CancellationToken.None);
            var two = owner.agentUrlPrompts.RequestAsync(binding, new Uri("https://example.com/second"), CancellationToken.None);
            try
            {
                RenderAgentWebPrompts();
                Require(agentWebPromptCards.Count == 2 && agentWebPromptScroll.MaxHeight == 320 && agentWebPromptScroll.Visibility == Visibility.Visible && !owner.dialogOpen,
                    "Agent URL choices must be independent bounded cards without a modal dialog.");
                var requests = owner.agentUrlPrompts.ForPane(id);
                owner.agentUrlPrompts.Choose(requests[1].Id, "external", true);
                Require(await two == new AgentWebOpenAnswer("external", true) && !one.IsCompleted, "Second card must answer independently.");
                registry.Revoke(id);
                try { await one; throw new InvalidOperationException("Revoked URL card completed instead of cancelling."); } catch (OperationCanceledException) { }
                RenderAgentWebPrompts(); Require(agentWebPromptCards.Count == 0 && agentWebPromptScroll.Visibility == Visibility.Collapsed, "Revocation must remove pending URL cards.");
                checks["agentUrlCardsNonmodalAndBounded"] = true;
            }
            finally { registry.Clear(); RenderAgentWebPrompts(); }
            await SmokeConversationReset(); checks["conversationResetPreservesDraftAndHistory"] = true;
            return checks;
        }
        private async Task SmokeConversationReset()
        {
            var original = Session; var originalDraft = input.Text;
            try
            {
                updating = true; input.Text = "unsent draft \uD55C\uAE00"; updating = false;
                await Change(p => p with { Draft = input.Text, Status = "completed", ResumeId = "smoke-resume", SessionUsage = new SessionUsage { Provider = p.Provider, Source = "smoke-reset", TotalTokens = 234, UpdatedAt = Wire.Now() } });
                var logs = Session.Logs.ToArray();
                Require(await ResetConversationAsync(CancellationToken.None), "Idle new-conversation action should succeed.");
                Require(Session.ResumeId is null && Session.SessionUsage is null && Session.Logs.Take(logs.Length).SequenceEqual(logs) && Session.Draft == "unsent draft \uD55C\uAE00" && input.Text == Session.Draft,
                    "New conversation must clear CLI identity/usage while keeping the pending draft and previous logs.");
                await Change(p => p with { Status = "running", ResumeId = "still-running" });
                Require(!await ResetConversationAsync(CancellationToken.None) && Session.ResumeId == "still-running", "New conversation must refuse a running pane.");
                // Mirror the view-ownership removal without deleting the main
                // fixture used by later smokes. This await completes synchronously.
                owner.views.Remove(id);
                try { Require(!await ResetConversationAsync(CancellationToken.None), "A removed pane must be ignored without resolving stale Session state."); }
                finally { owner.views[id] = this; }
            }
            finally
            {
                await owner.service.UpdateAsync(s => s with { Sessions = s.Sessions.Any(p => p.Id == id) ? s.Sessions.Select(p => p.Id == id ? original : p).ToList() : [original, .. s.Sessions] });
                updating = true; input.Text = originalDraft; updating = false; Refresh();
            }
        }
    }
}
