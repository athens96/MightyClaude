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
    private async Task<Dictionary<string, object?>> RunTerminalSmoke()
    {
        var saved = service.Snapshot; var id = Wire.Id(); PaneView? pane = null;
        try
        {
            smokeTerminalEnabled = true;
            await AddPane("shell", shape: session => session with { Id = id, Title = "Windows terminal smoke" });
            await WaitUI(() => views.TryGetValue(id, out var current) && current.Container.ActualWidth > 0);
            pane = views[id];
            return await pane.RunTerminalBridgeSmoke();
        }
        finally
        {
            smokeTerminalEnabled = false;
            if (pane is not null) await pane.DisposeTerminalAsync().WaitAsync(TimeSpan.FromSeconds(20));
            await service.UpdateAsync(_ => saved); Render();
        }
    }
    private async Task RunTerminalCommandAsync(string command, string title, bool autoRun = true)
    {
        var paneId = Wire.Id();
        await AddPane("shell", shape: pane => pane with { Id = paneId, Title = title });
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (!closing && DateTimeOffset.UtcNow < deadline)
        {
            if (!views.TryGetValue(paneId, out var pane)) throw new InvalidOperationException(Locale.Get("terminal.failed"));
            if (pane.CurrentTerminal is { } process)
            {
                if (!process.TryWrite(command + (autoRun ? "\r" : ""))) throw new InvalidOperationException(Locale.Get("terminal.inputBusy"));
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
        private void RefreshTerminalTheme() => PostTerminal(new { type = "theme", light = owner.service.Snapshot.Theme == "light" });

        private void InitializeTerminal(Grid grid, FrameworkElement composer, Button copyButton)
        {
            if (Session.Kind is not ("shell" or AgentIOPaneKind.Terminal) || owner.options.SmokeTest && !owner.smokeTerminalEnabled) return;
            output.View.Visibility = composer.Visibility = copyButton.Visibility = Visibility.Collapsed;
            var host = new Grid(); Grid.SetRow(host, 1); grid.Children.Add(host);
            terminalView = new WebView2(); host.Children.Add(terminalView);
            terminalNotice = new TextBlock { Text = Locale.Get("terminal.starting"), TextWrapping = TextWrapping.Wrap, Margin = new(12) }; host.Children.Add(terminalNotice);
            terminalFlush.Tick += (_, _) => FlushTerminal();
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
                core.SetVirtualHostNameToFolderMapping(TerminalHost, Path.Combine(AppContext.BaseDirectory, "Assets", "Terminal"), CoreWebView2HostResourceAccessKind.Deny);
                core.NavigationStarting += (_, args) => args.Cancel = !IsTerminalAddress(args.Uri);
                core.NewWindowRequested += (_, args) => args.Handled = true;
                core.DownloadStarting += (_, args) => args.Cancel = true;
                core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
                core.WebMessageReceived += ReceiveTerminalMessage;
                core.Navigate("https://" + TerminalHost + "/index.html");
            }
            catch (Exception ex) { if (!terminalClosed && terminalNotice is not null) terminalNotice.Text = Locale.Get("terminal.failed") + "\n" + ex.Message; }
        }
        private static bool IsTerminalAddress(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host == TerminalHost && uri.IsDefaultPort && uri.AbsolutePath == "/index.html";

        private async void ReceiveTerminalMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            if (terminalClosed || !IsTerminalAddress(args.Source)) return;
            try
            {
                if (args.WebMessageAsJson.Length > 1_048_576) return;
                using var json = JsonDocument.Parse(args.WebMessageAsJson);
                var value = json.RootElement; var type = value.Text("type");
                switch (type)
                {
                    case "ready" when !terminalReady:
                        terminalReady = true;
                        var columns = value.GetProperty("columns").GetInt32(); var rows = value.GetProperty("rows").GetInt32();
                        if (Session.Kind == AgentIOPaneKind.Terminal)
                        {
                            if (!ConnectAgentTerminal(columns, rows)) throw new InvalidOperationException("Agent terminal is no longer available.");
                            terminalNotice!.Visibility = Visibility.Collapsed; terminalFlush.Start(); RefreshTerminalTheme(); break;
                        }
                        var workspacePath = Workspace.Path;
                        terminalProcessStart = Task.Run(() => PseudoTerminal.Start(workspacePath, ReceiveTerminalOutput, columns, rows,
                            arguments: PseudoTerminal.ShellArguments(PseudoTerminal.DefaultShell, noProfile: owner.options.SmokeTest)));
                        terminal = await terminalProcessStart;
                        if (terminalClosed) { await terminal.DisposeAsync(); return; }
                        terminalNotice!.Visibility = Visibility.Collapsed;
                        terminalFlush.Start(); PostTerminal(new { type = "theme", light = owner.service.Snapshot.Theme == "light" });
                        _ = ObserveTerminalExit(terminal);
                        break;
                    case "input":
                        if (value.Text("text") is { } text && (ownedAgentTerminal is { } owned ? owned.Write(text) : terminal?.TryWrite(text)) == false) owner.error.Text = Locale.Get("terminal.inputBusy");
                        break;
                    case "resize":
                        if (ownedAgentTerminal is { } agent) agent.Resize(value.GetProperty("columns").GetInt32(), value.GetProperty("rows").GetInt32());
                        else terminal?.Resize(value.GetProperty("columns").GetInt32(), value.GetProperty("rows").GetInt32());
                        break;
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
            catch (Exception ex) { if (!terminalClosed) { owner.error.Text = ex.Message; if (terminal is null && terminalNotice is not null) terminalNotice.Text = Locale.Get("terminal.failed") + "\n" + ex.Message; } }
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
            if (text.Length > 0) PostTerminal(new { type = "output", text });
        }
        private void PostTerminal(object message)
        {
            if (terminalClosed || !terminalReady) return;
            try { terminalView?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message, Wire.Json)); }
            catch (InvalidOperationException) { }
            catch (System.Runtime.InteropServices.COMException) { }
        }
        private async Task ObserveTerminalExit(PseudoTerminal process)
        {
            try
            {
                var code = await process.Completion;
                await process.DisposeAsync();
                if (terminalClosed) return;
                FlushTerminal(); terminalFlush.Stop();
                terminalNotice!.Text = Locale.Get("terminal.exited", new Dictionary<string, string> { ["code"] = code.ToString() }); terminalNotice.Visibility = Visibility.Visible;
            }
            catch (Exception ex) { if (!terminalClosed) owner.error.Text = ex.Message; }
        }
        internal async Task<Dictionary<string, object?>> RunTerminalBridgeSmoke()
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            while (terminal is null && DateTimeOffset.UtcNow < deadline) await Task.Delay(50);
            Require(terminal is not null && terminalReady && terminalView?.CoreWebView2 is not null, "Trusted xterm/ConPTY did not become ready: " + terminalNotice?.Text);
            var process = terminal!; var core = terminalView!.CoreWebView2;
            Require(IsTerminalAddress(core.Source), "Terminal renderer did not load from the packaged trusted origin.");
            var marker = "MIGHTY_" + Guid.NewGuid().ToString("N") + "_\uD55C\uAE00";
            var powershell = Path.GetFileNameWithoutExtension(PseudoTerminal.DefaultShell).Equals("powershell", StringComparison.OrdinalIgnoreCase);
            var command = powershell ? "[Console]::WriteLine(('" + marker[..16] + "'+'" + marker[16..] + "'))\r"
                : "chcp 65001\rset MIGHTY_SMOKE=" + marker[..16] + "\rset MIGHTY_SMOKE=%MIGHTY_SMOKE%" + marker[16..] + "\recho %MIGHTY_SMOKE%\r";
            await core.ExecuteScriptAsync("send(" + JsonSerializer.Serialize(new { type = "input", text = command }, Wire.Json) + ")");
            var query = "Array.from({length:terminal.buffer.active.length},(_,i)=>terminal.buffer.active.getLine(i)?.translateToString()||'').join('\\n').includes(" + JsonSerializer.Serialize(marker, Wire.Json) + ")";
            var rendered = false; deadline = DateTimeOffset.UtcNow.AddSeconds(20);
            while (DateTimeOffset.UtcNow < deadline)
            {
                rendered = await core.ExecuteScriptAsync(query) == "true"; if (rendered) break; await Task.Delay(50);
            }
            Require(rendered, "ConPTY UTF-8 output did not reach the xterm buffer.");
            await core.ExecuteScriptAsync("terminal.resize(92,24)");
            Require(await core.ExecuteScriptAsync("terminal.cols===92 && terminal.rows===24") == "true", "Terminal resize bridge failed.");
            core.Navigate("https://not-authorized.invalid/"); await Task.Delay(100);
            Require(IsTerminalAddress(core.Source), "Terminal allowed navigation away from the trusted packaged origin.");
            await DisposeTerminalAsync().WaitAsync(TimeSpan.FromSeconds(20));
            await process.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Require(!process.TryWrite("after-close"), "Closed ConPTY accepted input.");
            return new() { ["trustedRendererLoaded"] = true, ["conptyUtf8RoundTrip"] = true, ["resizeBridge"] = true,
                ["externalNavigationBlocked"] = true, ["processClosed"] = true, ["physicalIMEAndClipboardTested"] = false };
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
