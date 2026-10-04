using MightyClaude.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private async Task<bool> ShowStyleApproval(RegisteredStyle style, bool readOnly = false, Func<Task>? onApprove = null)
    {
        var trust = new StyleTrustStore(Path.Combine(StateDirectory, "style-trust"));
        Task<bool> IsTrustLocked() => Task.Run(() =>
        {
            try { trust.Load(); return false; }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { return true; }
        });
        var locked = await IsTrustLocked();
        var sections = StylePresentation.Approval(style); var decision = new StyleApprovalDecision(StyleManifest.Items(style.Manifest.Root,"autoAllow").Length);
        var body = new StackPanel { Spacing = 10, MinWidth = 400 }; var scrollBody = new StackPanel { Spacing = 12 };
        var disclosure = new CheckBox { Content = Locale.Get("styles.approval.expandAll"), IsChecked = true };
        var collapsible = new List<FrameworkElement>(); var visible = false;
        var dialog = new ContentDialog { Title = StylePresentation.Name(style), XamlRoot = SettingsXamlRoot, Content = body,
            PrimaryButtonText = readOnly ? "" : Locale.Get("settings.styles.allowButton"), CloseButtonText = Locale.Get("guidedPanel.cancelButton"), DefaultButton = ContentDialogButton.Close, IsPrimaryButtonEnabled = false };
        var lockBanner = new TextBlock { Text = Locale.Get("settings.styles.lockBanner") + "\n" + Path.Combine(trust.DirectoryPath, "approvals.json"), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Visibility = locked ? Visibility.Visible : Visibility.Collapsed };
        body.Children.Add(lockBanner);
        FrameworkElement Section(StyleApprovalSection section)
        {
            var panel = new StackPanel { Spacing = 4 };
            panel.Children.Add(new TextBlock { Text = section.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            var lines = new StackPanel { Spacing = 3 };
            foreach(var text in section.Lines) lines.Children.Add(new TextBlock { Text = text, FontSize = 11, FontFamily = new FontFamily(section.Monospaced ? "Consolas" : "Segoe UI"), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            panel.Children.Add(lines); if(section.Foldable)collapsible.Add(lines);
            return panel;
        }
        // Auto-allow stays in the fixed top portion, outside the expandable
        // details. Its exact wire names cannot disappear behind raw JSON.
        var auto = Section(sections.Single(s=>s.Id=="autoAllow"));
        var autoScroll = new ScrollViewer { Content = auto, MaxHeight = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        auto.Loaded += (_,_) => { visible = true; dialog.IsPrimaryButtonEnabled = !readOnly && !locked; };
        body.Children.Add(autoScroll); body.Children.Add(disclosure);
        foreach(var section in sections.Where(s=>s.Id!="autoAllow"))scrollBody.Children.Add(Section(section));
        var raw = new StackPanel { Spacing = 5 }; raw.Children.Add(new TextBlock { Text = Locale.Get("styles.approval.raw"), FontWeight = FontWeights.SemiBold });
        var contents = StyleContents(style); raw.Children.Add(contents); collapsible.Add(contents); scrollBody.Children.Add(raw);
        body.Children.Add(new ScrollViewer { Content = scrollBody, MaxHeight = 350, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        void Expanded() { foreach(var item in collapsible)item.Visibility = disclosure.IsChecked == true ? Visibility.Visible : Visibility.Collapsed; }
        disclosure.Checked += (_,_) => Expanded(); disclosure.Unchecked += (_,_) => Expanded();
        var warning = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Visibility = Visibility.Collapsed }; body.Children.Add(warning);
        dialog.PrimaryButtonClick += async (_,args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                locked = await IsTrustLocked();
                if(locked)
                {
                    args.Cancel = true; dialog.IsPrimaryButtonEnabled = false; lockBanner.Visibility = Visibility.Visible;
                }
                else if(!decision.Press(visible))
                {
                    args.Cancel = true;
                    if(decision.Confirming)
                    {
                        warning.Text = Locale.Get("styles.approval.secondConfirmation",new Dictionary<string,string>{{"count",StyleManifest.Items(style.Manifest.Root,"autoAllow").Length.ToString()}});
                        warning.Visibility = Visibility.Visible; dialog.PrimaryButtonText = Locale.Get("styles.approval.confirmAgain");
                    }
                }
            }
            finally { deferral.Complete(); }
        };
        if(await dialog.ShowAsync()!=ContentDialogResult.Primary||readOnly)return false;
        if(onApprove is not null)await onApprove(); else await ApproveStyle(style);
        return true;
    }
}
