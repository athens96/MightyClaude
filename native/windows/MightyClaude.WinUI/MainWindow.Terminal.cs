using System.Text;
using System.Text.Json;
using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.ApplicationModel.DataTransfer;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private readonly HashSet<Task> terminalClosures = [];
    private bool smokeTerminalEnabled;
    /// <summary>
    /// Opens a terminal pane and puts <paramref name="command"/> at its prompt for the user. A command with a line break is
    /// not put in at all and Enter follows only where <paramref name="autoRun"/> says so (TerminalInputPolicy). A pane
    /// that could not be added (the 129th) has already said why, and a command with a line break opens no pane at all.
    /// </summary>
    private async Task RunTerminalCommandAsync(string command, string title, bool autoRun = true)
    {
        if (TerminalInputPolicy.HasLineBreak(command)) throw new InvalidOperationException(TerminalInputPolicy.NewlineRefusal);
        var paneId = Wire.Id();
        if (!await AddPane("shell", shape: pane => pane with { Id = paneId, Title = title })) return;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (!closing && DateTimeOffset.UtcNow < deadline)
        {
            if (!views.TryGetValue(paneId, out var pane)) throw new InvalidOperationException(Locale.Get("terminal.failed"));
            if (pane.CurrentTerminal is { } process)
            {
                switch (TerminalInputPolicy.Apply(command, autoRun, process.TryWrite))
                {
                    case TerminalInputPolicy.Outcome.Refused: throw new InvalidOperationException(TerminalInputPolicy.NewlineRefusal);
                    case TerminalInputPolicy.Outcome.Failed: throw new InvalidOperationException(Locale.Get("terminal.inputBusy"));
                }
                return;
            }
            if (pane.TerminalClosed) break;
            await Task.Delay(50);
        }
        throw new InvalidOperationException(Locale.Get("terminal.failed"));
    }
    private void CloseTerminal(PaneView pane)
    {
        var task = pane.DisposeTerminalAsync(); terminalClosures.Add(task);
        _ = task.ContinueWith(_ => DispatcherQueue.TryEnqueue(() => terminalClosures.Remove(task)), TaskScheduler.Default);
    }
    private async Task CloseTerminalsAsync()
    {
        await Task.WhenAll(views.Values.Select(p => p.DisposeTerminalAsync()).Concat(terminalClosures));
        terminalClosures.Clear();
    }
    /// <summary>
    /// A shell pane's status as its terminal reports it (M/AppStore+Terminals.swift:25-27), drawn where an agent pane's
    /// is: the sidebar row, the tab, the header capsule, the dashboard and the status bar. The phone reads it next.
    /// </summary>
    private void ShellStatusChanged(string id, string status)
    {
        if (closing || !service.SetShellStatus(id, status)) return;
        RefreshRunningIndicators(); mobileRouter?.Changed();
        // A shell's status is not a run event: the push hub is told here, a started command too, so one that ends before the next tick is still pushed.
        if (status == "running") mobilePush?.Began(id); else mobilePush?.Changed();
    }
    /// <summary>
    /// Reads whether each attached shell runs a command, at the Mac's interval (M/LocalTerminalSession.swift:211-216): one
    /// timer for the window and one reading off the UI thread for every shell pane. It turns only while a shell is attached.
    /// </summary>
    private DispatcherTimer? shellActivity;
    private bool shellActivityReading;
    private void WatchShellActivity()
    {
        if (shellActivity is null) { shellActivity = new() { Interval = TimeSpan.FromMilliseconds(500) }; shellActivity.Tick += async (_, _) => await ReadShellActivity(); }
        shellActivity.Start();
    }
    /// <summary>A reading that could not be taken, or that a restart or an exit overtook, changes nothing (PaneView.ApplyShellActivity).</summary>
    private async Task ReadShellActivity()
    {
        if (shellActivityReading) return;
        var shells = views.Values.Select(pane => (Pane: pane, Terminal: pane.AttachedShell)).Where(shell => shell.Terminal is not null).ToArray();
        if (closing || shells.Length == 0) { shellActivity?.Stop(); return; }
        shellActivityReading = true;
        try
        {
            var readings = await Task.Run(() => shells.Select(shell => ShellProcessProbe.IsBusy(shell.Terminal!)).ToArray());
            for (var i = 0; i < shells.Length && !closing; i++) shells[i].Pane.ApplyShellActivity(shells[i].Terminal!, readings[i]);
        }
        finally { shellActivityReading = false; }
    }

    private sealed partial class PaneView
    {
        private const string TerminalHost = "terminal.mightyclaude.invalid";
        private WebView2? terminalView;
        private PseudoTerminal? terminal;
        internal PseudoTerminal? CurrentTerminal => terminal;
        internal bool TerminalClosed => terminalClosed;
        private Task? terminalStartup, terminalDisposal;
        private Task<PseudoTerminal>? terminalProcessStart;
        private bool terminalClosed, terminalReady;
        private readonly StringBuilder terminalPending = new();
        private readonly object terminalOutputLock = new();
        private readonly DispatcherTimer terminalFlush = new() { Interval = TimeSpan.FromMilliseconds(32) };
        private TextBlock? terminalNotice;
        private Grid? terminalHost;
        private Button? terminalRestart;
        private TextBlock? terminalTitle, terminalDirectory, terminalGrid;
        /// <summary>The footer room the working directory may take, and the whole path it stands for.</summary>
        private Border? terminalDirectoryRoom;
        private string terminalDirectoryPath = "";
        private bool terminalRestarting;
        private int terminalColumns = 100, terminalRows = 30;
        /// <summary>The terminal's colours, and its size and face from the text setting: 12pt at scale 1, the code font when one is chosen.</summary>
        private void RefreshTerminalTheme()
        {
            PostTerminal(new { type = "theme", light = owner.service.Snapshot.Theme == "light" });
            PostTerminal(new { type = "font", size = AppText.Current.TerminalSize, family = AppText.Current.CodeFamily });
        }

        /// <summary>
        /// A terminal pane (M/LocalTerminalView.swift:67-89, M/AgentTerminalPaneView.swift:57-73): the terminal
        /// edge to edge in the card, 2 under a shell's slim bar or from the card's top for an agent's (whose
        /// bar its tab group draws); under it, when it has something to say, the notice with the shell's
        /// restart button (11pt <c>ink2</c>, padding 10) and always the footer line (9pt <c>ink2</c>, padding
        /// h10 v6, 7 apart, the grid size in the tertiary ink), both on the subtle wash.
        /// </summary>
        private void InitializeTerminal(Grid grid, FrameworkElement composer)
        {
            if (Session.Kind is not ("shell" or AgentIOPaneKind.Terminal) || owner.options.SmokeTest && !owner.smokeTerminalEnabled) return;
            // A remote command pane sends each command to the host through the composer: no local ConPTY, never in the host's path.
            if (LocalPath(Workspace) is null) return;
            output.View.Visibility = composer.Visibility = Visibility.Collapsed;
            const double inset = PaneInset;
            var b = owner.brushes; var ink2 = b.Brush(DesignToken.Ink2); var shell = Session.Kind == "shell";
            var inner = DesignMetrics.Radius.Pane - DesignMetrics.Stroke.Line;
            // Over the pane grid's padding and row spacing, as the header and the conversation are; the corners follow the card's inner curve.
            var host = terminalHost = new Grid
            {
                Margin = new Thickness(-inset, shell ? DesignMetrics.Spacing.Xxs - grid.RowSpacing : -inset - grid.RowSpacing, -inset, -inset),
                CornerRadius = shell ? new CornerRadius(0, 0, inner, inner) : new CornerRadius(inner),
            };
            Grid.SetRow(host, 1); Grid.SetRowSpan(host, 2); grid.Children.Add(host);
            host.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
            host.RowDefinitions.Add(new() { Height = GridLength.Auto });
            host.RowDefinitions.Add(new() { Height = GridLength.Auto });
            InstallTerminalRenderer();
            terminalNotice = new TextBlock { Text = Locale.Get("terminal.starting"), TextFont = TextArea.Interface, TextWrapping = TextWrapping.Wrap, TextSize = 11, Foreground = ink2, VerticalAlignment = VerticalAlignment.Center };
            terminalRestart = Button(Locale.Get("terminal.restart"), RestartTerminalAsync); terminalRestart.TextSize = 11; terminalRestart.MinHeight = 22; terminalRestart.Padding = new Thickness(DesignMetrics.Spacing.Sm, DesignMetrics.Spacing.Xxs, DesignMetrics.Spacing.Sm, DesignMetrics.Spacing.Xxs); terminalRestart.VerticalAlignment = VerticalAlignment.Center;
            terminalRestart.IsEnabled = false; terminalRestart.Visibility = shell ? Visibility.Visible : Visibility.Collapsed;
            var notices = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm, Padding = new Thickness(DesignMetrics.Spacing.Md), Background = b.Subtle }; notices.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); notices.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            notices.Children.Add(terminalNotice); Grid.SetColumn(terminalRestart, 1); notices.Children.Add(terminalRestart); Grid.SetRow(notices, 1); host.Children.Add(notices);
            // The bar shows only while the notice does: a running terminal has nothing between it and its footer.
            terminalNotice.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => notices.Visibility = terminalNotice.Visibility);
            var footer = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm, Padding = new Thickness(DesignMetrics.Spacing.Md, DesignMetrics.Spacing.Sm, DesignMetrics.Spacing.Md, DesignMetrics.Spacing.Sm), Background = b.Subtle };
            footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); footer.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            TextBlock Words(string text, bool medium = false) => new() { Text = text, TextFont = TextArea.Interface, TextSize = 9, Foreground = ink2, TextWrapping = TextWrapping.NoWrap, FontWeight = medium ? Microsoft.UI.Text.FontWeights.Medium : Microsoft.UI.Text.FontWeights.Normal };
            // A shell names its engine and its title, "ConPTY · powershell" (the Mac's "Ghostty · zsh"); an agent's terminal says what it is.
            var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm };
            name.Children.Add(Words(shell ? "ConPTY" : Locale.Get("agentTerminal.terminalPane.title"), medium: true));
            terminalTitle = Words(Path.GetFileNameWithoutExtension(PseudoTerminal.DefaultShell)); terminalTitle.MaxWidth = 180; terminalTitle.TextTrimming = TextTrimming.CharacterEllipsis;
            if (shell) { name.Children.Add(Words("·")); name.Children.Add(terminalTitle); }
            footer.Children.Add(name);
            terminalDirectory = Words(""); terminalDirectory.HorizontalAlignment = HorizontalAlignment.Right; terminalDirectory.TextTrimming = TextTrimming.CharacterEllipsis;
            terminalDirectoryRoom = new Border { Child = terminalDirectory }; terminalDirectoryRoom.SizeChanged += (_, _) => FitTerminalDirectory();
            Grid.SetColumn(terminalDirectoryRoom, 1); footer.Children.Add(terminalDirectoryRoom);
            ShowTerminalDirectory(LocalFolder(Workspace));
            // The grid size is the tertiary ink (M/LocalTerminalView.swift:85, M/AgentTerminalPaneView.swift:69).
            terminalGrid = Words(""); terminalGrid.Foreground = b.Tertiary;
            Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(terminalGrid, FontNumeralAlignment.Tabular);
            Grid.SetColumn(terminalGrid, 2); footer.Children.Add(terminalGrid); Grid.SetRow(footer, 2); host.Children.Add(footer);
            terminalFlush.Tick += (_, _) => FlushTerminal();
        }
        private void InstallTerminalRenderer()
        {
            // The web shell's page draws the terminal itself and its "ready" starts the shell (MainWindow.WebShell.Panes.cs).
            if (owner.webShell is not null) return;
            terminalView = new WebView2(); terminalHost!.Children.Insert(0, terminalView);
            terminalView.Loaded += (_, _) => terminalStartup ??= StartTerminalViewAsync();
            terminalView.GotFocus += (_, _) => PostTerminal(new { type = "focus" });
        }
        private async Task StartTerminalViewAsync()
        {
            try
            {
                var folder = Path.Combine(owner.StateDirectory, "terminal-profiles", Session.WorkspaceId);
                var environment = await CoreWebView2Environment.CreateWithOptionsAsync("", folder, new CoreWebView2EnvironmentOptions());
                if (terminalClosed) return;
                await terminalView!.EnsureCoreWebView2Async(environment);
                if (terminalClosed) return;
                var core = terminalView.CoreWebView2;
                core.Settings.AreDevToolsEnabled = false; core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.AreDefaultScriptDialogsEnabled = false; core.Settings.IsStatusBarEnabled = false;
                // The terminal's size is the app's text setting; Ctrl+= and Ctrl+0 must not zoom the page behind its back.
                core.Settings.IsZoomControlEnabled = false;
                core.SetVirtualHostNameToFolderMapping(TerminalHost, Path.Combine(AppContext.BaseDirectory, "Assets", "Terminal"), CoreWebView2HostResourceAccessKind.Deny);
                core.NavigationStarting += (_, args) => args.Cancel = !IsTerminalAddress(args.Uri);
                core.NewWindowRequested += (_, args) => args.Handled = true;
                core.DownloadStarting += (_, args) => args.Cancel = true;
                core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
                core.WebMessageReceived += ReceiveTerminalMessage;
                core.Navigate("https://" + TerminalHost + "/index.html");
            }
            catch (Exception ex) { if (!terminalClosed && terminalNotice is not null) { terminalNotice.Text = Locale.Get("terminal.failed") + "\n" + ex.Message; if (terminalRestart is not null) terminalRestart.IsEnabled = true; owner.ShellStatusChanged(id, "error"); } }
        }
        private static bool IsTerminalAddress(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host == TerminalHost && uri.IsDefaultPort && uri.AbsolutePath == "/index.html";

        private async void ReceiveTerminalMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            if (terminalClosed || !IsTerminalAddress(args.Source)) return;
            await HandleTerminalMessage(args.WebMessageAsJson);
        }

        /// <summary>One message of the terminal view: the trusted terminal page's, or the web shell page's xterm (MainWindow.WebShell.Panes.cs).</summary>
        internal async Task HandleTerminalMessage(string message)
        {
            if (terminalClosed) return;
            try
            {
                if (message.Length > 1_048_576) return;
                using var json = JsonDocument.Parse(message);
                var value = json.RootElement; var type = value.Text("type");
                switch (type)
                {
                    case "ready" when !terminalReady:
                        terminalReady = true;
                        var columns = value.GetProperty("columns").GetInt32(); var rows = value.GetProperty("rows").GetInt32();
                        UpdateTerminalGrid(columns, rows);
                        if (Session.Kind == AgentIOPaneKind.Terminal)
                        {
                            if (!ConnectAgentTerminal(columns, rows)) throw new InvalidOperationException("Agent terminal is no longer available.");
                            terminalNotice!.Visibility = Visibility.Collapsed; terminalFlush.Start(); RefreshTerminalTheme(); break;
                        }
                        await StartTerminalShellAsync();
                        break;
                    // A web shell page loaded again: its new terminal takes the colours and the font again.
                    case "ready": RefreshTerminalTheme(); break;
                    case "input":
                        terminalInputs++;
                        if (value.Text("text") is { } text && (ownedAgentTerminal is { } owned ? owned.Write(text) : terminal?.TryWrite(text)) == false) owner.error.Text = Locale.Get("terminal.inputBusy");
                        break;
                    case "resize":
                        UpdateTerminalGrid(value.GetProperty("columns").GetInt32(), value.GetProperty("rows").GetInt32());
                        if (ownedAgentTerminal is { } agent) agent.Resize(value.GetProperty("columns").GetInt32(), value.GetProperty("rows").GetInt32());
                        else terminal?.Resize(value.GetProperty("columns").GetInt32(), value.GetProperty("rows").GetInt32());
                        break;
                    case "title":
                        if (terminalTitle is not null && value.Text("title") is { Length: > 0 and <= 200 } title && !title.Any(char.IsControl)) terminalTitle.Text = title;
                        break;
                    case "directory":
                        if (terminalDirectory is not null && value.Text("url") is { Length: <= 8192 } location && Uri.TryCreate(location, UriKind.Absolute, out var uri) && uri.IsFile && !uri.IsUnc && (uri.Host.Length == 0 || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)))
                            ShowTerminalDirectory(uri.LocalPath);
                        break;
                    // A press inside the terminal page never reaches the pane as a pointer event.
                    case "pressed": owner.ActivatePane(id); break;
                    case "copy": if (value.Text("text") is { Length: <= 1_000_000 } selection) Copy(selection); break;
                    case "paste":
                        var data = Clipboard.GetContent();
                        if (data.Contains(StandardDataFormats.Text))
                        {
                            var pasted = await data.GetTextAsync();
                            if (pasted.Length <= 65_536 && !terminalClosed) PostTerminal(new { type = "paste", text = pasted });
                            else if (!terminalClosed) owner.error.Text = Locale.Get("terminal.pasteTooLarge");
                        }
                        break;
                }
            }
            catch (Exception ex) { if (!terminalClosed) { owner.error.Text = ex.Message; if (terminal is null && terminalNotice is not null) { terminalNotice.Text = Locale.Get("terminal.failed") + "\n" + ex.Message; if (terminalRestart is not null) terminalRestart.IsEnabled = true; owner.ShellStatusChanged(id, "error"); } } }
        }
        private void ShowTerminalDirectory(string path)
        {
            terminalDirectoryPath = path; ToolTipService.SetToolTip(terminalDirectory!, path); FitTerminalDirectory();
        }

        /// <summary>
        /// The working directory keeps both ends and drops its middle when the footer has no room for all
        /// of it, as the Mac's <c>.truncationMode(.middle)</c> does (M/LocalTerminalView.swift:84); WinUI only
        /// trims the end, which stays as the backstop. Cut between text elements, each candidate measured.
        /// </summary>
        private void FitTerminalDirectory()
        {
            if (terminalDirectory is not { } label) return;
            var full = terminalDirectoryPath; label.Text = full;
            if (terminalDirectoryRoom is not { ActualWidth: > 0 } room) return;
            bool Fits(string text)
            {
                label.Text = text; label.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                return label.DesiredSize.Width <= room.ActualWidth;
            }
            var starts = System.Globalization.StringInfo.ParseCombiningCharacters(full);
            if (Fits(full) || starts.Length <= 2) return;
            string Kept(int keep) => full[..starts[(keep + 1) / 2]] + "…" + (keep / 2 == 0 ? "" : full[starts[starts.Length - keep / 2]..]);
            // The longest kept count (head and tail elements) that fits; 1 always shows the first element.
            int low = 1, high = starts.Length - 1;
            while (low < high) { var mid = (low + high + 1) / 2; if (Fits(Kept(mid))) low = mid; else high = mid - 1; }
            label.Text = Kept(low);
        }
        private void UpdateTerminalGrid(int columns, int rows)
        { terminalColumns = Math.Clamp(columns, 2, 1000); terminalRows = Math.Clamp(rows, 1, 1000); if (terminalGrid is not null) terminalGrid.Text = terminalColumns + "×" + terminalRows; }
        private async Task StartTerminalShellAsync()
        {
            var workspacePath = LocalFolder(Workspace);
            terminalProcessStart = Task.Run(() => PseudoTerminal.Start(workspacePath, ReceiveTerminalOutput, terminalColumns, terminalRows,
                arguments: PseudoTerminal.ShellArguments(PseudoTerminal.DefaultShell, noProfile: owner.options.SmokeTest)));
            terminal = await terminalProcessStart;
            if (terminalClosed) { await terminal.DisposeAsync(); return; }
            terminalNotice!.Visibility = Visibility.Collapsed; terminalRestart!.Visibility = Visibility.Collapsed;
            terminalFlush.Start(); RefreshTerminalTheme(); _ = ObserveTerminalExit(terminal);
            // Attached: idle until a reading says a command runs (M/LocalTerminalSession.swift:144).
            owner.ShellStatusChanged(id, "idle"); owner.WatchShellActivity();
        }
        /// <summary>The shell this shell pane has attached and running, the one the window's reading asks about; null for any other pane.</summary>
        internal PseudoTerminal? AttachedShell => Session.Kind == "shell" && !terminalClosed && terminal is { Completion.IsCompleted: false } process ? process : null;
        /// <summary>
        /// Sets the pane running while a command occupies its shell's terminal and idle at its prompt (M/LocalTerminalSession.swift:211-216):
        /// a pane that has exited or failed keeps that, and a reading of a shell the pane no longer has changes nothing.
        /// </summary>
        internal void ApplyShellActivity(PseudoTerminal process, bool? busy)
        {
            if (terminalClosed || !ReferenceEquals(terminal, process) || process.Completion.IsCompleted) return;
            if (owner.service.SessionStatus(id) is { } current && ShellActivity.NextStatus(current, busy) is { } next) owner.ShellStatusChanged(id, next);
        }
        private async Task RestartTerminalAsync()
        {
            if (terminalClosed || terminalRestarting || owner.service.Snapshot.Sessions.FirstOrDefault(p => p.Id == id)?.Kind != "shell") return;
            terminalRestarting = true; terminalRestart!.IsEnabled = false;
            owner.ShellStatusChanged(id, "idle");
            try
            {
                var previous = terminal; terminal = null;
                if (previous is not null) await previous.DisposeAsync();
                if (terminalClosed) return;
                lock (terminalOutputLock) terminalPending.Clear();
                terminalNotice!.Text = Locale.Get("terminal.starting"); terminalNotice.Visibility = Visibility.Visible;
                if (!terminalReady)
                {
                    terminalView?.Close(); if (terminalView is not null) terminalHost!.Children.Remove(terminalView);
                    terminalStartup = null; InstallTerminalRenderer();
                }
                else { PostTerminal(new { type = "reset" }); await StartTerminalShellAsync(); }
            }
            catch (Exception ex) { if (!terminalClosed) { terminalNotice!.Text = Locale.Get("terminal.failed") + "\n" + ex.Message; terminalRestart.Visibility = Visibility.Visible; terminalRestart.IsEnabled = true; owner.ShellStatusChanged(id, "error"); } }
            finally { terminalRestarting = false; }
        }
        private void ReceiveTerminalOutput(string text)
        {
            lock (terminalOutputLock)
            {
                terminalPending.Append(text);
                // Bound retained output while the window/renderer is unavailable.
                if (terminalPending.Length > 1_048_576) terminalPending.Remove(0, terminalPending.Length - 1_048_576);
            }
        }
        private void FlushTerminal()
        {
            if (terminalClosed || !terminalReady) return;
            string text;
            lock (terminalOutputLock) { text = terminalPending.ToString(); terminalPending.Clear(); }
            // The web shell's page takes the bytes, so a chunk that ends inside a character is still written whole.
            if (text.Length > 0) { terminalOutputPosts++; terminalOutputTail = (terminalOutputTail + text)[^Math.Min(400, terminalOutputTail.Length + text.Length)..]; }
            if (text.Length > 0) PostTerminal(owner.webShell is null ? new { type = "output", text } : new { type = "output", data = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) });
        }
        /// <summary>For the smokes: whether the view said it is ready, output waiting, output chunks sent on, the shell running.</summary>
        internal object TerminalDiagnostics() { int pending; lock (terminalOutputLock) pending = terminalPending.Length; return new { ready = terminalReady, closed = terminalClosed, pending, posts = terminalOutputPosts, flushing = terminalFlush.IsEnabled, inputs = terminalInputs, tail = terminalOutputTail }; }
        private int terminalOutputPosts, terminalInputs;
        private string terminalOutputTail = "";
        private void PostTerminal(object message)
        {
            if (terminalClosed || !terminalReady) return;
            if (owner.webShell is { } web) { web.Bridge.Push(new { kind = "terminal", paneId = id, message }); return; }
            try { terminalView?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message, Wire.Json)); }
            catch (InvalidOperationException) { }
            catch (System.Runtime.InteropServices.COMException) { }
        }
        private async Task ObserveTerminalExit(PseudoTerminal process)
        {
            int? code = null;
            Exception? failure = null;
            try
            {
                code = await process.Completion;
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                try { await process.DisposeAsync(); }
                catch (Exception ex) { failure ??= ex; }
            }
            if (terminalClosed || !ReferenceEquals(terminal, process)) return;
            FlushTerminal(); terminalFlush.Stop();
            // The shell is gone: completed, or error when its exit could not be read (M/LocalTerminalSession.swift:204).
            owner.ShellStatusChanged(id, failure is null ? "completed" : "error");
            terminalNotice!.Text = failure is null
                ? Locale.Get("terminal.exited", new Dictionary<string, string> { ["code"] = code!.Value.ToString() })
                : Locale.Get("terminal.failed") + "\n" + failure.Message;
            terminalNotice.Visibility = Visibility.Visible;
            terminalRestart!.Visibility = Visibility.Visible; terminalRestart.IsEnabled = true;
        }
        internal Task DisposeTerminalAsync() => terminalDisposal ??= DisposeTerminalCoreAsync();
        private async Task DisposeTerminalCoreAsync()
        {
            terminalClosed = true; terminalFlush.Stop();
            if (ownedAgentTerminal is { } source) { source.Output -= ReceiveTerminalOutput; ownedAgentTerminal = null; }
            if (terminalStartup is not null) await terminalStartup;
            if (terminalProcessStart is not null)
            {
                try { await (await terminalProcessStart).DisposeAsync(); }
                catch (Exception) when (terminal is null) { /* Failed starts own their native-handle cleanup. */ }
            }
            terminalView?.Close(); terminalView = null;
        }
    }
}
