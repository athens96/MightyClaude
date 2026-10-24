using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace MightyClaude.WinUI;

/// <summary>
/// The whole window as one WebView2 page: the React front end built from native/windows/web into <c>Assets\Web</c>, served
/// under a virtual host so it loads as a real origin without a server. Only that origin loads: any other navigation, a new
/// window and a download are refused, and the page's messages are taken only from it (<see cref="WebBridge"/>). Developer
/// tools and the stock context menu are there only in a smoke run or while the state folder holds <c>web-devtools</c>.
/// The window's front end unless the state folder holds <c>ui-winui</c> at start or no WebView2 runtime is installed
/// (<see cref="Configure"/>); then the WinUI screens stand as before.
/// </summary>
internal sealed class WebShell : Grid
{
    /// <summary>A name under <c>.invalid</c>, which no DNS ever answers, so the page can never be mistaken for a site.</summary>
    internal const string Host = "app.mighty.invalid";
    internal const string Origin = "https://" + Host + "/";

    /// <summary>Whether the page is the window's front end: on unless the state folder held <c>ui-winui</c> (the escape hatch) or WebView2 was missing at start.</summary>
    internal static bool Enabled { get; private set; }
    /// <summary>The page was wanted but no WebView2 runtime was installed, so the WinUI screens stand and say so.</summary>
    internal static bool RuntimeMissing { get; private set; }
    /// <summary>
    /// Looks once, at start, as <see cref="PerfLog.Configure"/> does. <c>ui-web</c>, the switch of the days the page was opt-in,
    /// is still read and changes nothing: the page is the default.
    /// </summary>
    internal static void Configure(string stateDirectory, bool runtimeInstalled)
    {
        var wanted = !File.Exists(Path.Combine(stateDirectory, "ui-winui"));
        RuntimeMissing = wanted && !runtimeInstalled;
        Enabled = wanted && runtimeInstalled;
    }

    internal static bool Trusted(string? uri) => uri is not null && uri.StartsWith(Origin, StringComparison.OrdinalIgnoreCase);

    internal WebView2 View { get; } = new();
    internal WebBridge Bridge { get; }
    /// <summary>The page's WebView2 once it is navigating, or null when WebView2 could not start (the reason is in perf.log).</summary>
    internal Task<CoreWebView2?> Started => started.Task;
    private readonly TaskCompletionSource<CoreWebView2?> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string stateDirectory;
    private readonly bool smoke;
    private bool starting;

    /// <param name="background">The page colour, painted until the page draws its own so the window never flashes white.</param>
    internal WebShell(WebBridge bridge, string stateDirectory, bool smoke, Windows.UI.Color background)
    {
        Bridge = bridge; this.stateDirectory = stateDirectory; this.smoke = smoke;
        View.DefaultBackgroundColor = background;
        AutomationProperties.SetAutomationId(View, "web-shell");
        Children.Add(View);
        View.Loaded += async (_, _) => { if (!starting) { starting = true; await Initialize(); } };
    }

    private async Task Initialize()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync("", Path.Combine(stateDirectory, "web-profile"), new());
            await View.EnsureCoreWebView2Async(environment);
            var core = View.CoreWebView2;
            var debugging = smoke || File.Exists(Path.Combine(stateDirectory, "web-devtools"));
            core.Settings.AreDevToolsEnabled = debugging; core.Settings.AreDefaultContextMenusEnabled = debugging; core.Settings.AreBrowserAcceleratorKeysEnabled = debugging;
            core.Settings.AreDefaultScriptDialogsEnabled = false; core.Settings.IsStatusBarEnabled = false; core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsPasswordAutosaveEnabled = false; core.Settings.IsGeneralAutofillEnabled = false;
            // The page's sizes follow the app's text setting; Ctrl+wheel must not zoom it behind the setting's back.
            core.Settings.IsZoomControlEnabled = false;
            core.SetVirtualHostNameToFolderMapping(Host, Path.Combine(AppContext.BaseDirectory, "Assets", "Web"), CoreWebView2HostResourceAccessKind.DenyCors);
            core.NavigationStarting += (_, args) => args.Cancel = !Trusted(args.Uri);
            core.FrameNavigationStarting += (_, args) => args.Cancel = !Trusted(args.Uri);
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.DownloadStarting += (_, args) => args.Cancel = true;
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            Bridge.Attach(core);
            core.Navigate(Origin + "index.html");
            started.TrySetResult(core);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { PerfLog.Mark("web shell failed: " + ex.Message); started.TrySetResult(null); }
    }
}
