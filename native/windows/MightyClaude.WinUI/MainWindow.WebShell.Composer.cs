using System.Text.Json;
using MightyClaude.Core;
using Microsoft.UI.Xaml;

namespace MightyClaude.WinUI;

/// <summary>
/// The web page's composer (native/windows/web/src/composer) on the window's side. Each pane the page writes in has a WinUI
/// <see cref="PaneView"/> that is never shown, made the way the phone's are (<see cref="MobilePane"/>): its editor holds the
/// page's draft and its attachments, so a send, the queue, a stop and every block reason go through the same code the WinUI
/// composer runs (<see cref="PaneView.Submit"/>). When the view's composer state changes it is pushed to the page as a
/// <c>composer</c> message; the page asks for it once on mount with <c>composerState</c>.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Panes whose composer state changed since the last push, and the JSON last pushed per pane.</summary>
    private readonly HashSet<string> webComposerDirty = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> webComposerSent = new(StringComparer.Ordinal);
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? webComposerFlush;

    /// <summary>The never-shown view of a pane the page writes in; a shell or agent pane, any provider.</summary>
    private PaneView WebPane(string? id)
    {
        if (id is null || !service.Snapshot.Sessions.Any(p => p.Id == id && p.Kind is "claude" or "shell")) throw new InvalidOperationException("Session not found.");
        if (!views.TryGetValue(id, out var pane)) { pane = new(this, id); views[id] = pane; pane.InitRefresher(); pane.Refresh(); }
        return pane;
    }

    /// <summary>Called at the end of every composer refresh (WinUI or never-shown view); a push follows within a frame or two.</summary>
    private void WebComposerChanged(PaneView view)
    {
        if (webShell is null) return;
        webComposerDirty.Add(view.PaneId);
        if (webComposerFlush is null)
        {
            webComposerFlush = DispatcherQueue.CreateTimer(); webComposerFlush.Interval = TimeSpan.FromMilliseconds(30); webComposerFlush.IsRepeating = false;
            webComposerFlush.Tick += (_, _) => FlushWebComposers();
        }
        if (!webComposerFlush.IsRunning) webComposerFlush.Start();
    }

    private void FlushWebComposers()
    {
        if (webShell is null) return;
        foreach (var id in webComposerDirty.ToArray())
        {
            webComposerDirty.Remove(id);
            if (!views.TryGetValue(id, out var view) || !service.Snapshot.Sessions.Any(s => s.Id == id)) { webComposerSent.Remove(id); continue; }
            var state = view.WebComposerState(); var json = JsonSerializer.Serialize(state, Wire.Json);
            if (webComposerSent.TryGetValue(id, out var last) && last == json) continue;
            webComposerSent[id] = json; webShell.Bridge.Push(new { kind = "composer", state });
        }
    }

    private static bool Flag(JsonElement args, string name) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>
    /// The composer's methods: the state, the draft, send / stop / the queue, and attachments (picked here, or dropped and pasted
    /// in the page as bytes). A refusal throws, and the page shows its message.
    /// </summary>
    private void RegisterComposerMethods(WebBridge bridge)
    {
        bridge.Register("composerState", args => Task.FromResult<object?>(WebPane(args.Text("paneId")).WebComposerState()));
        bridge.Register("updateDraft", args =>
        {
            WebPane(args.Text("paneId")).WebSetDraft(args.Text("text") ?? "", save: true);
            return Task.FromResult<object?>(null);
        });
        bridge.Register("send", async args =>
        {
            var view = WebPane(args.Text("paneId"));
            view.WebSetDraft(args.Text("text") ?? "", save: false);
            if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("attachments", out var ids) && ids.ValueKind == JsonValueKind.Array)
                view.WebKeepAttachments(ids.EnumerateArray().Select(i => i.GetString() ?? "").ToHashSet(StringComparer.Ordinal));
            var outcome = await view.Submit(Flag(args, "second"));
            return new { outcome, draft = view.WebDraft, state = view.WebComposerState() };
        });
        bridge.Register("stop", async args => { await WebPane(args.Text("paneId")).WebStop(); return null; });
        bridge.Register("queueRemove", args => { WebPane(args.Text("paneId")).WebQueueRemove(args.Text("itemId") ?? ""); return Task.FromResult<object?>(null); });
        bridge.Register("queueRunNext", async args => { await WebPane(args.Text("paneId")).WebRunNext(); return null; });
        bridge.Register("installCli", args =>
        {
            if (service.Snapshot.Sessions.FirstOrDefault(s => s.Id == args.Text("paneId")) is { } pane && SetupItemForMissingCli(pane) is { } item) OpenSetup(item);
            return Task.FromResult<object?>(null);
        });
        bridge.Register("pickAttachments", async args => { await WebPane(args.Text("paneId")).WebPickAttachments(); return null; });
        bridge.Register("addAttachments", async args =>
        {
            var view = WebPane(args.Text("paneId"));
            var files = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("files", out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().Select(f => (Name: f.Text("name") ?? "attachment", Data: f.Text("data") ?? "")).ToList() : [];
            await view.WebAddAttachments(files);
            return null;
        });
        bridge.Register("removeAttachment", args => { WebPane(args.Text("paneId")).WebRemoveAttachment(args.Text("id") ?? ""); return Task.FromResult<object?>(null); });
        bridge.Register("dismissAttachmentError", args => { WebPane(args.Text("paneId")).WebDismissAttachmentError(); return Task.FromResult<object?>(null); });
        // The slash palette: the rows the WinUI palette lists (built-ins, the scanned catalogue, each argument's choices); the page filters them.
        bridge.Register("slashCommands", async args => await WebPane(args.Text("paneId")).WebSlashCatalog());
        bridge.Register("slashAction", async args =>
        {
            var view = WebPane(args.Text("paneId"));
            if (!Enum.TryParse<SlashCommandAction>(args.Text("action"), out var action)) throw new ArgumentException("Unknown action.");
            return await view.WebSlashAction(action, args.Text("arg"));
        });
        bridge.Register("attachmentPreview", args => Task.FromResult(WebPane(args.Text("paneId")).WebAttachmentPreview(args.Text("id") ?? "")));
    }

    private sealed partial class PaneView
    {
        internal string PaneId => id;
        internal string WebDraft => input.Text;
        /// <summary>The editor's placeholder and the blocked row's reason as the last composer refresh chose them.</summary>
        private string placeholderKey = "composer.placeholder.idle", blockedReason = "";

        /// <summary>The page's draft into the editor the send reads; <paramref name="save"/> as a keystroke saves it (DesktopService.UpdateDraft).</summary>
        internal void WebSetDraft(string text, bool save)
        {
            if (text.Length > input.MaxLength) text = text[..input.MaxLength];
            draftLoaded = true;
            if (input.Text != text) { updating = true; input.Text = text; updating = false; }
            if (save) owner.service.UpdateDraft(id, text);
            RefreshPalette(text); RefreshComposerState();
        }

        /// <summary>Only the attachments the page still shows go with a send.</summary>
        internal void WebKeepAttachments(HashSet<string> ids) { if (pendingAttachments.RemoveAll(a => !ids.Contains(a.Id)) > 0) { RefreshAttachments(); RefreshComposerState(); } }

        internal Task WebStop() => StopActiveRun();

        internal void WebQueueRemove(string itemId) { if (queuedInputs.Remove(itemId)) { RenderQueuedInputs(); RefreshComposerState(); } }

        internal Task WebRunNext() => RunNext();

        internal Task WebPickAttachments() => PickAttachments();

        /// <summary>Files dropped or pasted in the page, as base64: each read as the picker's (5 MB each, 8 MB in all, 8 files), then the composer's own checks.</summary>
        internal Task WebAddAttachments(IReadOnlyList<(string Name, string Data)> files) => LoadAttachments(() =>
        {
            if (Session.Kind == "shell") throw new ArgumentException(Locale.Get("wire.startRun.attachmentAiOnly"));
            if (files.Count > AttachmentSupport.MaximumCount) throw new ArgumentException(Locale.Get("composer.attachment.tooMany"));
            var read = new List<RunAttachment>(); long total = 0;
            foreach (var (name, data) in files)
            {
                if (data.Length > (AttachmentSupport.MaximumFileBytes + 2) / 3 * 4) throw new ArgumentException(Locale.Get("composer.attachment.fileTooLarge"));
                byte[] bytes;
                try { bytes = Convert.FromBase64String(data); } catch (FormatException) { throw new IOException(Locale.Get("composer.attachment.readIncomplete")); }
                if (bytes.Length > AttachmentSupport.MaximumFileBytes) throw new ArgumentException(Locale.Get("composer.attachment.fileTooLarge"));
                total += bytes.Length; if (total > AttachmentSupport.MaximumTotalBytes) throw new ArgumentException(Locale.Get("attachments.error.totalTooLarge"));
                read.Add(AttachmentSupport.Make(Path.GetFileName(name), bytes));
            }
            return Task.FromResult(read);
        });

        internal void WebRemoveAttachment(string attachmentId) { pendingAttachments.RemoveAll(a => a.Id == attachmentId); ShowAttachmentError(null); RefreshAttachments(); RefreshComposerState(); }

        internal void WebDismissAttachmentError() { ShowAttachmentError(null); RefreshComposerState(); }

        /// <summary>An attachment's preview as the chip's dialog shows it: a picture as a data URL, text up to 20,000 characters, else nothing.</summary>
        internal object? WebAttachmentPreview(string attachmentId)
        {
            if (pendingAttachments.FirstOrDefault(a => a.Id == attachmentId) is not { } file) return null;
            var size = AttachmentSupport.DecodedLength(file);
            if (file.MediaType is "image/png" or "image/jpeg" or "image/gif" or "image/webp") return new { name = file.Name, mediaType = file.MediaType, size, image = "data:" + file.MediaType + ";base64," + file.DataBase64 };
            if (file.MediaType == "text/plain") { var text = System.Text.Encoding.UTF8.GetString(AttachmentSupport.Decode(file)); return new { name = file.Name, mediaType = file.MediaType, size, text = text[..Math.Min(text.Length, 20000)] }; }
            return new { name = file.Name, mediaType = file.MediaType, size };
        }

        private static object WebSlashRow(SlashCommand c) => new { invocation = c.Invocation, description = SlashPalette.Description(c), source = c.Source, origin = c.Origin.ToString(), action = c.Action?.ToString(), argument = c.Argument?.ToString(), actionArg = c.ActionArg };

        /// <summary>The palette's rows for this pane, the catalogue scanned first when it is missing or older than 30 s (as <see cref="ScanSlashCommands"/>).</summary>
        internal async Task<object> WebSlashCatalog()
        {
            var pane = Session;
            if (pane.Kind == "shell") return new { builtins = Array.Empty<object>(), scanned = Array.Empty<object>(), choices = new Dictionary<string, object[]>() };
            var path = SlashWorkspacePath;
            if (owner.smokeSlashCommands is null && owner.slashCatalogs.IsStale(pane.Provider, path))
            {
                try { owner.slashCatalogs.Set(pane.Provider, path, await Task.Run(() => SlashCommandCatalog.Commands(pane.Provider, path))); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { /* A pane with no catalogue still shows its built-ins. */ }
            }
            var builtins = SlashPalette.Builtins(pane.Provider);
            var scanned = owner.smokeSlashCommands ?? owner.slashCatalogs.Get(pane.Provider, path) ?? [];
            var choices = builtins.Where(b => b.Argument is not null).ToDictionary(b => b.Invocation, b => ArgumentChoices(b.Argument!.Value, b.Invocation).Select(WebSlashRow).ToArray());
            return new { builtins = builtins.Select(WebSlashRow), scanned = scanned.Select(WebSlashRow), choices };
        }

        /// <summary>A palette row's app action, run as the WinUI palette runs it (notes in the transcript included); the draft is cleared. The editor's text after it.</summary>
        internal async Task<string> WebSlashAction(SlashCommandAction action, string? argument)
        {
            if (await PerformSlashAction(action, argument)) { slashDismissedFor = null; RefreshPalette(""); await SetDraft(""); }
            return input.Text;
        }

        /// <summary>What the page draws of the composer, read from the state the last <see cref="RefreshComposerState"/> left.</summary>
        internal object WebComposerState()
        {
            var pane = Session;
            var busy = pane.Status == "running" || starting || queueStarting || owner.BackgroundUpdateHolds(pane);
            var queueable = busy && !starting && !queueStarting && pane.Kind == "claude" && HasComposerContent;
            var primary = queueable ? "queue" : busy ? "stop" : "send";
            var held = owner.service.HeldItems(id);
            var mutationBlock = owner.ManualMutationBlockReason(pane);
            var queueEnabled = !starting && !queueStarting && !attachmentsLoading && !stopping && queuedInputs.Items.Count < QueuedInputBuffer.MaximumItems && mutationBlock is null;
            return new
            {
                paneId = id, kind = pane.Kind, busy, starting = starting || queueStarting, stopping, canSend, checkingModels, attachmentsLoading,
                placeholderKey, blocked = blockedReason.Length == 0 ? null : blockedReason,
                install = blockedReason.Length > 0 && mutationBlock is null && owner.SetupItemForMissingCli(pane) is not null,
                hold = owner.BackgroundUpdateHolds(pane) ? ProviderMark.Label(pane.Provider) : null,
                primary, primaryEnabled = primary == "queue" ? queueEnabled : busy ? !stopping : canSend,
                worktree = offersWorktree, worktreeMenuOnly, worktreeEnabled = offersWorktree && queueEnabled,
                queue = new
                {
                    held = held.Select(h => new { id = h.Id, text = h.Text }),
                    items = queuedInputs.Items.Select(q => new { id = q.Id, text = q.Text, attachments = q.Attachments.Select(a => a.Name) }),
                    runNext = DelegationQueueRows.OffersRunNext(busy, held.Count),
                    waitsOnBackground = BackgroundQueuePolicy.WaitsOnBackground(pane.BackgroundWork, queuedInputs.Items.Count)
                        ? owner.service.SecondSend(pane) == ComposerSecondSend.Worktree ? "queue.waitingOnBackground.windowsWorktree" : "queue.waitingOnBackground.windows" : null,
                },
                attachments = pendingAttachments.Select(a => new { id = a.Id, name = a.Name, mediaType = a.MediaType, size = AttachmentSupport.DecodedLength(a) }),
                attachmentError = attachmentErrorText.Text.Length == 0 ? null : attachmentErrorText.Text,
                attachmentsAllowed = pane.Kind != "shell",
            };
        }
    }
}
