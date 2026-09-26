using System.Net.Http;
using System.Runtime.InteropServices;
using MightyClaude.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;

namespace MightyClaude.WinUI;

/// <summary>
/// The manual browser tab of docs/browser-pane.md on WebView2: a 새 브라우저 탭 entry
/// opens a pane with an address field, back, forward and reload. Every rule comes from
/// MightyClaude.Core — BrowserAddress, BrowserHistory, BrowserProfile and
/// BrowserEngineService — and this file only draws them and drives the control.
///
/// No remote debugging port and no CDP flag is ever set; new windows, downloads and
/// script dialogs are refused. The runtime is never bundled: when the Evergreen runtime
/// is missing the pane offers the Microsoft per-user bootstrapper and downloads nothing
/// until the user clicks 설치.
/// </summary>
public sealed partial class MainWindow
{
    // The opt-in setting, latched once per launch so a change only applies after a
    // restart. Null means "not read yet".
    private bool? browserEngineLatch;

    /// <summary>Reads the opt-in setting once at launch and never again.</summary>
    private void LatchBrowserEngineSetting() => browserEngineLatch ??= service.Snapshot.BrowserEngineEnabled;

    private bool BrowserEngineEnabled
    {
        get { LatchBrowserEngineSetting(); return browserEngineLatch!.Value; }
    }

    /// <summary>Forces the setting on for the GUI smoke only, before any pane is built.</summary>
    internal void ForceBrowserEngineForSmoke() => browserEngineLatch = true;

    // One CoreWebView2Environment and one user data folder per workspace: panes of the
    // same workspace share them and other workspaces never do.
    private readonly Dictionary<string, CoreWebView2Environment> browserEnvironments = [];

    /// <summary>
    /// Adds a browser tab to the active workspace. The ordinary new-pane path owns the
    /// layout, the selection and the "add a workspace first" copy; only the browser
    /// pane's own fields are stamped on afterwards.
    /// </summary>
    internal async Task AddBrowserPane(string? groupId = null)
    {
        await AddPane("browser", groupId: groupId);
        if (service.Snapshot.ActiveSessionId is not { } added) return;
        if (service.Snapshot.Sessions.FirstOrDefault(p => p.Id == added)?.Kind != "browser") return;
        await Act(async () =>
        {
            await service.UpdateAsync(s => s with
            {
                Sessions = s.Sessions.Select(p => p.Id == added
                    ? p with { Title = Locale.Get("browser.tab.title"), WorkspaceProfileKey = p.WorkspaceId }
                    : p).ToList(),
            });
            Render();
        });
    }

    /// <summary>
    /// Returns the workspace's shared environment, creating it on first use under
    /// &lt;state folder&gt;\browser-profiles\&lt;workspaceProfileKey&gt;. Nothing in the folder is
    /// ever deleted and no other browser's cookies are imported.
    /// </summary>
    private async Task<CoreWebView2Environment?> GetBrowserEnvironmentAsync(string profileKey)
    {
        if (browserEnvironments.TryGetValue(profileKey, out var existing)) return existing;
        var folder = BrowserProfile.ProfileFolder(StateDirectory, profileKey);
        try
        {
            Directory.CreateDirectory(folder);
            // No additional browser arguments: no remote debugging port, no CDP flag.
            var env = await CoreWebView2Environment.CreateWithOptionsAsync(
                "", folder, new CoreWebView2EnvironmentOptions());
            browserEnvironments[profileKey] = env;
            return env;
        }
        catch { return null; }
    }

    /// <summary>The runtime-missing notice, built standalone so the smoke can scan its copy.</summary>
    internal static StackPanel BuildBrowserMissingNotice()
    {
        var panel = new StackPanel { Spacing = 12, Padding = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = Locale.Get("browser.runtime.missing"), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new Button { Content = Locale.Get("browser.runtime.install") });
        return panel;
    }

    /// <summary>The "browser is off" notice, built standalone so the smoke can scan its copy.</summary>
    internal static TextBlock BuildBrowserDisabledNotice() =>
        new() { Text = Locale.Get("browser.engine.disabled"), TextWrapping = TextWrapping.Wrap };

    /// <summary>Asks WebView2 for the installed Evergreen runtime version.</summary>
    private sealed class WebView2RuntimeLocator : IBrowserRuntimeLocator
    {
        public string? GetRuntimeVersion()
        {
            try { return CoreWebView2Environment.GetAvailableBrowserVersionString(); }
            catch { return null; }
        }
    }

    /// <summary>
    /// The Microsoft per-user bootstrapper: downloaded over HTTPS into a temporary folder,
    /// checked for a valid Authenticode signature signed by Microsoft Corporation, then run
    /// as the current user with /silent /install and no elevation request.
    /// </summary>
    private sealed class WebView2BootstrapperInstaller : IBrowserInstaller
    {
        private const string BootstrapperUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

        public async Task<string> DownloadBootstrapperAsync(string tempPath, CancellationToken ct)
        {
            using var client = new HttpClient();
            var bytes = await client.GetByteArrayAsync(new Uri(BootstrapperUrl), ct);
            await File.WriteAllBytesAsync(tempPath, bytes, ct);
            return tempPath;
        }

        public bool VerifyMicrosoftSignature(string filePath) => WinTrustVerify.IsMicrosoftSigned(filePath);

        public async Task<int> RunInstallerAsync(string filePath, CancellationToken ct)
        {
            var info = new System.Diagnostics.ProcessStartInfo(filePath, "/silent /install") { UseShellExecute = false };
            using var process = System.Diagnostics.Process.Start(info)
                ?? throw new InvalidOperationException("WebView2 bootstrapper did not start.");
            await process.WaitForExitAsync(ct);
            return process.ExitCode;
        }
    }

    /// <summary>
    /// WinVerifyTrust plus the Authenticode signer subject. Returns false anywhere but
    /// Windows, so a missing verdict is never mistaken for a good one.
    /// </summary>
    private static class WinTrustVerify
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustFileInfo
        {
            public uint cbStruct;
            [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
            public nint hFile;
            public nint pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustData
        {
            public uint cbStruct;
            public nint pPolicyCallbackData;
            public nint pSIPClientData;
            public uint dwUIChoice;           // 2 = WTD_UI_NONE
            public uint fdwRevocationChecks;  // 0 = WTD_REVOKE_NONE
            public uint dwUnionChoice;        // 1 = WTD_CHOICE_FILE
            public nint pFile;
            public uint dwStateAction;        // 0 = WTD_STATEACTION_IGNORE
            public nint hWVTStateData;
            public nint pwszURLReference;
            public uint dwProvFlags;          // 0x10 = WTD_CACHE_ONLY_URL_RETRIEVAL
            public uint dwUIContext;
            public nint pSignatureSettings;
        }

        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
        private static extern uint WinVerifyTrust(nint hwnd, ref Guid pgActionID, ref WinTrustData pData);

        private static readonly Guid ActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        public static bool IsMicrosoftSigned(string filePath)
        {
            if (!OperatingSystem.IsWindows()) return false;
            var fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            try
            {
                Marshal.StructureToPtr(new WinTrustFileInfo
                {
                    cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                    pcwszFilePath = filePath,
                    hFile = nint.Zero,
                    pgKnownSubject = nint.Zero,
                }, fileInfoPtr, false);
                var trustData = new WinTrustData
                {
                    cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                    dwUIChoice = 2,
                    fdwRevocationChecks = 0,
                    dwUnionChoice = 1,
                    pFile = fileInfoPtr,
                    dwStateAction = 0,
                    dwProvFlags = 0x10,
                };
                var actionId = ActionGenericVerifyV2;
                if (WinVerifyTrust(nint.Zero, ref actionId, ref trustData) != 0) return false;
#pragma warning disable SYSLIB0057 // Nothing else reads an Authenticode signer subject.
                var cert = System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(filePath);
#pragma warning restore SYSLIB0057
                return cert.Subject.Contains("Microsoft Corporation", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
            finally { Marshal.FreeHGlobal(fileInfoPtr); }
        }
    }

    private sealed partial class PaneView
    {
        private bool browserAttached;
        private Grid? browserHost;
        private Border? browserContent;
        private WebView2? webView;
        private TextBox? addressBox;
        private Button? browserBack, browserForward, browserReload;
        private BrowserHistory? browserHistory;
        private Task? browserInitTask;
        private string? browserRuntimeVersion;

        /// <summary>
        /// Schedules the browser view. The pane's own grid exists only once the pane is in
        /// the visual tree, so the composer slot that loads with the pane is the anchor —
        /// the same anchor the Mighty view waits on. A pane of any other kind builds nothing.
        /// </summary>
        internal void AttachBrowserView(FrameworkElement anchor)
        {
            if (Session.Kind != "browser") return;
            anchor.Loaded += (_, _) => BuildBrowserView();
        }

        /// <summary>Builds the view now instead of waiting for the anchor to load.</summary>
        internal void EnsureBrowserView() => BuildBrowserView();

        /// <summary>Completes once the engine has been started (or refused).</summary>
        internal Task BrowserReady => browserInitTask ?? Task.CompletedTask;

        /// <summary>The Evergreen runtime version the pane found, or null when none is installed.</summary>
        internal string? BrowserRuntimeVersion => browserRuntimeVersion;

        /// <summary>True once the WebView2 control is live.</summary>
        internal bool BrowserControlLive => webView is not null;

        private void BuildBrowserView()
        {
            if (browserAttached || Container.Child is not Grid grid) return;
            browserAttached = true;
            // A browser pane runs no agent: hide the transcript and the composer the pane
            // built, so nothing can start a CLI run from here.
            foreach (var child in grid.Children.OfType<FrameworkElement>().ToArray())
                child.Visibility = Visibility.Collapsed;

            browserHistory = new BrowserHistory();
            browserHost = new Grid { RowSpacing = 8 };
            browserHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            browserHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            browserHost.Children.Add(BuildBrowserNavBar());
            browserContent = new Border { VerticalAlignment = VerticalAlignment.Stretch };
            Grid.SetRow(browserContent, 1); browserHost.Children.Add(browserContent);
            AutomationProperties.SetAutomationId(browserHost, "browser-pane-" + id);
            Grid.SetRow(browserHost, 0); Grid.SetRowSpan(browserHost, grid.RowDefinitions.Count);
            grid.Children.Add(browserHost);

            browserInitTask = StartBrowserAsync();
        }

        private Grid BuildBrowserNavBar()
        {
            var nav = new Grid { ColumnSpacing = 4, Height = 36, VerticalAlignment = VerticalAlignment.Center };
            for (var index = 0; index < 3; index++) nav.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            nav.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            browserBack = NavButton("←", Locale.Get("browser.back"));
            browserForward = NavButton("→", Locale.Get("browser.forward"));
            browserReload = NavButton("↻", Locale.Get("browser.reload"));
            addressBox = new TextBox
            {
                PlaceholderText = Locale.Get("browser.address.placeholder"),
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(8, 4, 8, 4),
            };
            AutomationProperties.SetName(addressBox, Locale.Get("browser.address.placeholder"));

            var controls = new Control[] { browserBack, browserForward, browserReload, addressBox };
            for (var index = 0; index < controls.Length; index++)
            {
                Grid.SetColumn(controls[index], index); nav.Children.Add(controls[index]);
            }
            SetBrowserNavEnabled(false);
            return nav;
        }

        // Grid carries no IsEnabled, so the nav controls are switched one by one.
        private void SetBrowserNavEnabled(bool enabled)
        {
            foreach (var control in new Control?[] { browserBack, browserForward, browserReload, addressBox })
                if (control is not null) control.IsEnabled = enabled;
        }

        private static Button NavButton(string icon, string label)
        {
            var button = new Button
            {
                Content = icon, Width = 36, Height = 36, MinWidth = 0,
                Padding = new Thickness(4), FontSize = 14,
            };
            AutomationProperties.SetName(button, label);
            ToolTipService.SetToolTip(button, label);
            return button;
        }

        private async Task StartBrowserAsync()
        {
            if (browserContent is null) return;

            // While the setting is off the pane shows only browser.engine.disabled and no
            // WebView2 is ever created.
            if (!owner.BrowserEngineEnabled)
            {
                browserContent.Child = BrowserNotice(Locale.Get("browser.engine.disabled"));
                return;
            }

            if (new WebView2RuntimeLocator().GetRuntimeVersion() is not { } version)
            {
                browserContent.Child = BuildRuntimeInstallPanel();
                return;
            }
            browserRuntimeVersion = version;

            var profileKey = Session.WorkspaceProfileKey ?? Session.WorkspaceId;
            if (await owner.GetBrowserEnvironmentAsync(profileKey) is not { } environment)
            {
                browserContent.Child = BrowserNotice(Locale.Get("browser.engine.failed"));
                return;
            }

            var view = new WebView2();
            try { await view.EnsureCoreWebView2Async(environment); }
            catch
            {
                browserContent.Child = BrowserNotice(Locale.Get("browser.engine.failed"));
                return;
            }

            // Popups and new windows are refused, downloads are cancelled and script
            // dialogs are dismissed: with the default dialogs off, ScriptDialogOpening
            // fires and a handler that never calls Accept() dismisses the dialog.
            view.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = false;
            view.CoreWebView2.NewWindowRequested += (_, args) => args.Handled = true;
            view.CoreWebView2.DownloadStarting += (_, args) => { args.Cancel = true; args.Handled = true; };
            view.CoreWebView2.ScriptDialogOpening += (_, _) => { };

            // History follows the engine's own navigation events, not the WinUI control's:
            // for a data: page the engine reports an empty Source and the control never
            // raises its NavigationCompleted. A successful navigation is recorded under the
            // URL the engine reports, or, when that is empty, the URL the same navigation
            // started with (redirects restart it under the same id with the new URL).
            var startedUris = new Dictionary<ulong, string>();
            view.CoreWebView2.NavigationStarting += (_, args) => startedUris[args.NavigationId] = args.Uri;
            view.CoreWebView2.NavigationCompleted += (core, args) =>
            {
                startedUris.Remove(args.NavigationId, out var started);
                var reported = string.IsNullOrEmpty(core.Source) ? started : core.Source;
                if (args.IsSuccess && Uri.TryCreate(reported, UriKind.Absolute, out var uri))
                    browserHistory?.Visit(uri);
                RefreshBrowserNavBar();
            };

            browserBack!.Click += (_, _) => BrowserGoBack();
            browserForward!.Click += (_, _) => BrowserGoForward();
            browserReload!.Click += (_, _) => view.Reload();
            addressBox!.KeyDown += (_, args) =>
            {
                if (args.Key != Windows.System.VirtualKey.Enter) return;
                if (BrowserAddress.Resolve(addressBox.Text) is { } resolved) NavigateBrowser(resolved);
            };

            webView = view;
            SetBrowserNavEnabled(true);
            browserContent.Child = view;
        }

        /// <summary>
        /// The address field: the engine navigates directly, so a repeated or data: address
        /// never depends on the WinUI control's cached Source property.
        /// </summary>
        private void NavigateBrowser(Uri address) => webView?.CoreWebView2?.Navigate(address.AbsoluteUri);

        /// <summary>
        /// The back button: the engine goes back and BrowserHistory follows. Like the
        /// address field it drives CoreWebView2 directly, so it never depends on the WinUI
        /// control's own back/forward bookkeeping.
        /// </summary>
        private void BrowserGoBack()
        {
            if (webView?.CoreWebView2 is not { } core || browserHistory is not { CanGoBack: true }) return;
            core.GoBack(); browserHistory.GoBack(); RefreshBrowserNavBar();
        }

        /// <summary>The forward button: the engine goes forward and BrowserHistory follows.</summary>
        private void BrowserGoForward()
        {
            if (webView?.CoreWebView2 is not { } core || browserHistory is not { CanGoForward: true }) return;
            core.GoForward(); browserHistory.GoForward(); RefreshBrowserNavBar();
        }

        /// <summary>Follows BrowserHistory: the address field, back and forward.</summary>
        private void RefreshBrowserNavBar()
        {
            if (browserHistory is null) return;
            var state = browserHistory.State(false);
            if (browserBack is not null) browserBack.IsEnabled = state.CanGoBack;
            if (browserForward is not null) browserForward.IsEnabled = state.CanGoForward;
            if (state.Url is { } url && addressBox is { FocusState: FocusState.Unfocused })
                addressBox.Text = url.AbsoluteUri;
        }

        private StackPanel BuildRuntimeInstallPanel()
        {
            var panel = new StackPanel
            {
                Spacing = 12, Padding = new Thickness(24),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            panel.Children.Add(new TextBlock
            {
                Text = Locale.Get("browser.runtime.missing"),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
            });
            var install = new Button { Content = Locale.Get("browser.runtime.install") };
            AutomationProperties.SetName(install, Locale.Get("browser.runtime.install"));
            // Nothing is downloaded or run before this click.
            install.Click += async (_, _) => await RunBrowserInstallAsync(panel, install);
            panel.Children.Add(install);
            return panel;
        }

        private async Task RunBrowserInstallAsync(StackPanel panel, Button install)
        {
            install.IsEnabled = false;
            var progress = new TextBlock { Text = Locale.Get("browser.runtime.installing"), TextWrapping = TextWrapping.Wrap };
            panel.Children.Add(progress);
            var temporary = Path.Combine(Path.GetTempPath(), "MightyClaudeWebView2Setup_" + Wire.Id());
            try
            {
                Directory.CreateDirectory(temporary);
                var service = new BrowserEngineService(new WebView2RuntimeLocator(), new WebView2BootstrapperInstaller());
                var outcome = await service.InstallAsync(temporary, CancellationToken.None);
                panel.Children.Remove(progress);
                panel.Children.Add(new TextBlock
                {
                    Text = outcome.Success
                        ? Locale.Get("browser.runtime.installSuccess")
                        : outcome.Error ?? Locale.Get("browser.runtime.installFailed"),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = outcome.Success ? null : new SolidColorBrush(Colors.OrangeRed),
                });
                if (!outcome.Success) install.IsEnabled = true;
            }
            finally
            {
                // The download itself is deleted by BrowserEngineService; drop the folder too.
                try { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); } catch { }
            }
        }

        private static TextBlock BrowserNotice(string text) =>
            new()
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Padding = new Thickness(24),
            };

        /// <summary>The nav bar strings the smoke feeds to the locale-key leak scan.</summary>
        internal IEnumerable<string> BrowserVisibleStrings()
        {
            yield return Locale.Get("browser.back");
            yield return Locale.Get("browser.forward");
            yield return Locale.Get("browser.reload");
            yield return Locale.Get("browser.address.placeholder");
            yield return Locale.Get("browser.tab.title");
        }
    }
}
