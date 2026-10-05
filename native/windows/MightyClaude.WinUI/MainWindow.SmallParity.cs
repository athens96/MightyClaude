using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private Task ConfirmRemoveWorkspace(string id) => Act(async () =>
    {
        if (dialogOpen || service.Snapshot.Workspaces.FirstOrDefault(w => w.Id == id) is not { } workspace) return;
        dialogOpen = true;
        try
        {
            var dialog = StyledDialog(new ContentDialog
            {
                Title = Locale.Get("workspace.remove.title"),
                Content = new TextBlock { Text = Locale.Get("workspace.remove.body", new Dictionary<string, string> { ["name"] = workspace.Name }), TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = Locale.Get("workspace.menu.remove"), CloseButtonText = Locale.Get("settings.run.cancelButton"),
                DefaultButton = ContentDialogButton.Close, XamlRoot = root.XamlRoot,
            });
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || closing) return;
            // Keep the ID the user approved even if a remote selection changed.
            if (service.Snapshot.Workspaces.Any(w => w.Id == id)) { await service.RemoveWorkspaceAsync(id); Render(); }
        }
        finally { dialogOpen = false; }
    });

    private sealed partial class PaneView
    {
        private void InitializeAttachmentMenu()
        {
            // Primary click remains the file picker; right-click/keyboard menu
            // exposes the same paste and remove-all actions as the Mac menu.
            var menu = new MenuFlyout();
            var choose = Item(Locale.Get("composer.attachment.choose"), PickAttachments);
            var paste = Item(Locale.Get("composer.attachment.paste"), () => LoadAttachments(() => AttachmentInput.ReadDataAsync(Clipboard.GetContent())));
            var remove = Item(Locale.Get("composer.attachment.removeAll"), () =>
            {
                if (attachmentsLoading) return Task.CompletedTask;
                pendingAttachments.Clear(); ShowAttachmentError(null); RefreshAttachments(); RefreshComposerState(); input.Focus(FocusState.Programmatic); return Task.CompletedTask;
            });
            var rule = new MenuFlyoutSeparator();
            menu.Items.Add(choose); menu.Items.Add(paste); menu.Items.Add(rule); menu.Items.Add(remove);
            menu.Opening += (_, _) =>
            {
                choose.IsEnabled = paste.IsEnabled = !attachmentsLoading && Session.Kind == "claude" && Capabilities.Attachments;
                remove.IsEnabled = !attachmentsLoading && pendingAttachments.Count > 0;
                // The rule and "remove all" show only while something is attached (M/SessionPaneView.swift:881-884).
                rule.Visibility = remove.Visibility = pendingAttachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            };
            attach.ContextFlyout = menu;
            AutomationProperties.SetHelpText(attach, Locale.Get("composer.attachment.choose") + " · " + Locale.Get("composer.attachment.paste") + " · " + Locale.Get("composer.attachment.removeAll"));
        }
    }
}
