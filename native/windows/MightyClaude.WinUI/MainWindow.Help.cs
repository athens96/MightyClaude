using MightyClaude.Core;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace MightyClaude.WinUI;

/// <summary>
/// The user guide (<see cref="HelpSite"/>) in the app's chosen language, opened in the default browser
/// from the sidebar footer's help symbol and from F1 anywhere in the window (the Mac's Help menu and ⌘?).
/// </summary>
public sealed partial class MainWindow
{
    private Button? sidebarHelpButton;

    /// <summary>Segoe Fluent Icons' Help (a question mark).</summary>
    private const string SidebarHelpGlyph = "";

    /// <summary>The address the help symbol and F1 open: the guide's folder in <see cref="Locale.ChosenLanguage"/>.</summary>
    internal static string HelpAddress() => HelpSite.Current();

    private Button BuildSidebarHelpButton()
    {
        sidebarHelpButton = SafeButton("", OpenHelp);
        sidebarHelpButton.Content = new FontIcon { Glyph = SidebarHelpGlyph, FontSize = SidebarFooterGlyph };
        AutomationProperties.SetAutomationId(sidebarHelpButton, "sidebar-help");
        RefreshSidebarHelpButton();
        return sidebarHelpButton;
    }

    /// <summary>The symbol's name and tooltip, set again once the saved language is applied.</summary>
    private void RefreshSidebarHelpButton()
    {
        if (sidebarHelpButton is null) return;
        var title = Locale.Get("menu.help");
        AutomationProperties.SetName(sidebarHelpButton, title); ToolTipService.SetToolTip(sidebarHelpButton, title);
    }

    /// <summary>F1 opens the guide from anywhere in the window.</summary>
    private void InitHelpShortcut()
    {
        var accelerator = new KeyboardAccelerator { Key = VirtualKey.F1 };
        accelerator.Invoked += async (_, args) => { args.Handled = true; await Act(OpenHelp); };
        root.KeyboardAccelerators.Add(accelerator);
    }

    internal bool HasHelpShortcut => root.KeyboardAccelerators.Any(a => a.Key == VirtualKey.F1 && a.Modifiers == VirtualKeyModifiers.None);

    private static async Task OpenHelp() => await Launcher.LaunchUriAsync(new Uri(HelpAddress()));
}
