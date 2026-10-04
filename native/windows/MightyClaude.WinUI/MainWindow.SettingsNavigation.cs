using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private string settingsCategory = "general";
    private Window? settingsWindow;
    private XamlRoot SettingsXamlRoot => (settingsWindow?.Content as FrameworkElement)?.XamlRoot ?? root.XamlRoot;
    private async Task ShowCategorizedSettingsAsync()
    {
        if (dialogOpen) return;
        dialogOpen = true;
        try
        {
            settingsCategory = service.Snapshot.SettingsPane;
            var body = new Grid { Width = Math.Clamp(root.ActualWidth - 100, 320, 740), Height = Math.Clamp(root.ActualHeight - 180, 300, 540), ColumnSpacing = 16 };
            body.ColumnDefinitions.Add(new() { Width = new(170) });
            body.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            var navigation = new ListView { SelectionMode = ListViewSelectionMode.Single };
            AutomationProperties.SetAutomationId(navigation, "settings-navigation");
            body.Children.Add(navigation);
            var content = new StackPanel { Spacing = 4 };
            var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            Grid.SetColumn(scroll, 1); body.Children.Add(scroll);
            void Display(SettingsCategory category)
            {
                settingsCategory = category.Id;
                content.Children.Clear();
                foreach (var id in category.Sections)
                {
                    var section = SettingsSections.Windows.FirstOrDefault(s => s.Id == id);
                    if (section is null) continue;
                    content.Children.Add(BuildSectionContainer(section.WindowsTitle!, BuilderFor(id)()));
                }
                scroll.ChangeView(null, 0, null, true);
                // Refresh only the still-visible account section; an async CLI
                // result must not replace the category selected in the meantime.
                if (category.Id == "cli" && !options.SmokeTest) _ = RefreshVisibleSettingsAccountsAsync(content);
            }
            foreach (var category in SettingsNavigation.Available)
            {
                var item = new ListViewItem { Content = category.Title, Tag = category, Padding = new(10, 10, 4, 10) };
                AutomationProperties.SetAutomationId(item, "settings-nav-" + category.Id); navigation.Items.Add(item);
            }
            navigation.SelectionChanged += async (_, _) =>
            {
                if (navigation.SelectedItem is not ListViewItem { Tag: SettingsCategory category }) return;
                Display(category);
                await Act(() => service.UpdateAsync(s => s with { SettingsPane = category.Id }));
            };
            navigation.SelectedItem = navigation.Items.OfType<ListViewItem>().FirstOrDefault(item => ((SettingsCategory)item.Tag).Id == settingsCategory) ?? navigation.Items[0];
            // A separate settings window leaves the main XamlRoot available
            // for account/reset confirmations (WinUI forbids nested dialogs).
            var frame = new Grid { Padding = new(20), RowSpacing = 16, RequestedTheme = root.RequestedTheme, Background = WindowBackground(service.Snapshot.Theme == "light") };
            frame.RowDefinitions.Add(new() { Height = GridLength.Auto });
            frame.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
            frame.RowDefinitions.Add(new() { Height = GridLength.Auto });
            frame.Children.Add(new TextBlock { Text = Locale.Get("settings.title"), FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            body.Width = double.NaN; body.Height = double.NaN; Grid.SetRow(body, 1); frame.Children.Add(body);
            var window = new Window { Title = Locale.Get("settings.title"), Content = frame };
            settingsWindow = window;
            var close = Button(Locale.Get("settings.closeButton"), () => { window.Close(); return Task.CompletedTask; });
            close.HorizontalAlignment = HorizontalAlignment.Right; Grid.SetRow(close, 2); frame.Children.Add(close);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) => closed.TrySetResult();
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(800, 700));
            window.Activate();
            await closed.Task;
        }
        finally { settingsWindow = null; dialogOpen = false; }
    }
    private async Task RefreshVisibleSettingsAccountsAsync(StackPanel content)
    {
        await RefreshCliAccounts();
        if (!dialogOpen || settingsCategory != "cli") return;
        var wrapper = content.Children.OfType<StackPanel>().FirstOrDefault(section => section.Children.OfType<TextBlock>().FirstOrDefault()?.Text == CliAccountStrings.SectionTitle);
        if (wrapper is not null && wrapper.Children.Count > 1) wrapper.Children[1] = BuildCliAccountsSectionFromState();
    }
}
