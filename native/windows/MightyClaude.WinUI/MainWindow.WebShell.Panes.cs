using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace MightyClaude.WinUI;

/// <summary>
/// The web page's panes that are not a conversation (native/windows/web/src/panes) on the window's side.
/// <list type="bullet">
/// <item>Terminal (a shell, an agent's terminal): the never-shown <see cref="PaneView"/> keeps its whole terminal lifecycle
/// (MainWindow.Terminal.cs: PseudoTerminal or the agent's terminal, the output buffer and its 32 ms flush, exit, restart, the
/// shell's activity); only the xterm view moved into the page. The page's xterm talks with <c>terminalMessage</c> (the old
/// terminal page's messages), and the view's posts go to the page as <c>{kind:'terminal'}</c> pushes, output as base64 UTF-8.
/// The footer and notice are the view's own TextBlocks, pushed whenever one changes.</item>
/// <item>Files: the workspace's FilePaneTree (MainWindow.Files.cs keeps one per workspace), WorkspaceFiles.List under the
/// workspace, FilePreviewLoader and SourceHighlighter, so every rule and note is the WinUI pane's. Pictures, SVG and a PDF's first
/// page come as data URLs.</item>
/// <item>Browser (and an agent's browser): a WebView2 of the workspace's browser profile (BrowserProfile, set up as
/// MainWindow.Browser.cs does) laid over the page's slot by the bounds the page reports, above the page in the same window, and
/// collapsed while the slot is hidden. BrowserAddress and BrowserHistory decide the address and back / forward.</item>
/// </list>
/// </summary>
public sealed partial class MainWindow
{
    private void RegisterPaneMethods(WebBridge bridge)
    {
        bridge.Register("terminalOpen", args => Task.FromResult<object?>(WebTerminalPane(args.Text("paneId")).WebTerminalOpen()));
        bridge.Register("terminalMessage", async args =>
        {
            var pane = WebTerminalPane(args.Text("paneId"));
            if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
                await pane.HandleTerminalMessage(message.GetRawText());
            return null;
        });
        bridge.Register("terminalRestart", async args => { await WebTerminalPane(args.Text("paneId")).WebRestartTerminal(); return null; });
        bridge.Register("terminalHistory", async args =>
        {
            if (args.Text("paneId") is { } id && service.Snapshot.Sessions.Any(s => s.Id == id && s.Kind == "shell")) await Act(() => ShowTerminalHistory(id));
            return null;
        });

        bridge.Register("filesState", async args =>
        {
            if (WebFiles(args) is not { } files) return new { available = false };
            if (!files.Tree.Children.ContainsKey("")) await WebLoadFolder(files, "");
            foreach (var path in files.Tree.Expanded.Where(p => !files.Tree.Children.ContainsKey(p)).ToList()) await WebLoadFolder(files, path);
            return WebFilesModel(files.Tree);
        });
        bridge.Register("filesToggle", async args =>
        {
            var files = RequireWebFiles(args); var path = args.Text("path") ?? "";
            files.Tree.SelectedPath = path;
            // Opening a folder always reads it again; the earlier listing shows while it reads.
            if (!files.Tree.Expanded.Remove(path)) { files.Tree.Expanded.Add(path); await WebLoadFolder(files, path); }
            return WebFilesModel(files.Tree);
        });
        bridge.Register("filesReveal", async args =>
        {
            var files = RequireWebFiles(args); var path = args.Text("path") ?? "";
            files.Tree.Reveal(path);
            foreach (var folder in files.Tree.Expanded.Where(p => path == p || path.StartsWith(p + "/", StringComparison.Ordinal)).OrderBy(p => p.Length).ToList()) await WebLoadFolder(files, folder);
            return WebFilesModel(files.Tree);
        });
        bridge.Register("filesFilter", args =>
        {
            var files = RequireWebFiles(args); files.Tree.Filter = args.Text("text") ?? "";
            return Task.FromResult<object?>(WebFilesModel(files.Tree));
        });
        bridge.Register("filesSelect", args =>
        {
            var files = RequireWebFiles(args); files.Tree.SelectedPath = args.Text("path");
            return Task.FromResult<object?>(WebFilesModel(files.Tree));
        });
        bridge.Register("filesRefresh", async args =>
        {
            var files = RequireWebFiles(args);
            await Task.WhenAll(files.Tree.RefreshTargets().Select(path => WebLoadFolder(files, path)));
            return WebFilesModel(files.Tree);
        });
        bridge.Register("filesPreview", async args =>
        {
            var files = RequireWebFiles(args); var path = args.Text("path") ?? "";
            files.Tree.PreviewPath = path;
            return await WebFilePreview(files.Root, path);
        });
        bridge.Register("filesShowInExplorer", args =>
        {
            if (WorkspaceFiles.Resolve(args.Text("path") ?? "", RequireWebFiles(args).Root) is { } path)
            {
                try { using var _ = Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = "/select,\"" + path + "\"", UseShellExecute = false }); }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { error.Text = ex.Message; }
            }
            return Task.FromResult<object?>(null);
        });
        // Its default app, by Windows' launcher (which refuses programs), only for a file that still resolves inside the workspace.
        bridge.Register("filesOpen", async args =>
        {
            if (WorkspaceFiles.Resolve(args.Text("path") ?? "", RequireWebFiles(args).Root) is not { } path || Directory.Exists(path)) return false;
            return await Windows.System.Launcher.LaunchFileAsync(await Windows.Storage.StorageFile.GetFileFromPathAsync(path));
        });

        bridge.Register("browserOpen", args => Task.FromResult<object?>(WebBrowserState(WebBrowser(args.Text("paneId")))));
        bridge.Register("browserNavigate", args =>
        {
            if (WebBrowser(args.Text("paneId")).View?.CoreWebView2 is { } core && BrowserAddress.Resolve(args.Text("address")) is { } address) core.Navigate(address.AbsoluteUri);
            return Task.FromResult<object?>(null);
        });
        bridge.Register("browserBack", args =>
        {
            var browser = WebBrowser(args.Text("paneId"));
            if (browser.View?.CoreWebView2 is { } core && browser.History.CanGoBack) { core.GoBack(); browser.History.GoBack(); PushWebBrowser(browser); }
            return Task.FromResult<object?>(null);
        });
        bridge.Register("browserForward", args =>
        {
            var browser = WebBrowser(args.Text("paneId"));
            if (browser.View?.CoreWebView2 is { } core && browser.History.CanGoForward) { core.GoForward(); browser.History.GoForward(); PushWebBrowser(browser); }
            return Task.FromResult<object?>(null);
        });
        bridge.Register("browserReload", args => { WebBrowser(args.Text("paneId")).View?.Reload(); return Task.FromResult<object?>(null); });
        bridge.Register("browserBounds", args =>
        {
            var browser = WebBrowser(args.Text("paneId"));
            static double Number(JsonElement value, string name) => value.TryGetProperty(name, out var n) && n.ValueKind == JsonValueKind.Number ? n.GetDouble() : 0;
            browser.Bounds = (Number(args, "x"), Number(args, "y"), Number(args, "width"), Number(args, "height"));
            browser.Shown = Flag(args, "visible");
            PlaceWebBrowser(browser);
            return Task.FromResult<object?>(null);
        });
        bridge.Register("browserInstall", async args => { await InstallWebBrowserRuntime(WebBrowser(args.Text("paneId"))); return null; });
    }

    // ---- Terminal ----

    /// <summary>The never-shown view of a shell or agent terminal pane the page shows.</summary>
    private PaneView WebTerminalPane(string? id)
    {
        if (id is null || !service.Snapshot.Sessions.Any(p => p.Id == id && p.Kind is "shell" or AgentIOPaneKind.Terminal)) throw new InvalidOperationException("Session not found.");
        if (!views.TryGetValue(id, out var pane)) { pane = new(this, id); views[id] = pane; pane.InitRefresher(); pane.Refresh(); }
        return pane;
    }

    // ---- Files ----

    private sealed record WebFilesTarget(FilePaneTree Tree, string Root);

    /// <summary>A files pane's tree and folder; null for a remote workspace, whose folder is on its host.</summary>
    private WebFilesTarget? WebFiles(JsonElement args)
    {
        var id = args.Text("paneId");
        var pane = service.Snapshot.Sessions.FirstOrDefault(s => s.Id == id && FilePaneKind.IsFilePane(s.Kind)) ?? throw new InvalidOperationException("Session not found.");
        return LocalPath(service.Snapshot.Workspaces.FirstOrDefault(w => w.Id == pane.WorkspaceId)) is { } root ? new(FilePaneTreeFor(pane.WorkspaceId), root) : null;
    }

    private WebFilesTarget RequireWebFiles(JsonElement args) => WebFiles(args) ?? throw new InvalidOperationException(RemoteWindowsStrings.PaneUnavailable);

    /// <summary>One folder's listing, read off the UI thread (PaneView.LoadFolder); anything Windows cannot read is an unreadable folder.</summary>
    private static async Task WebLoadFolder(WebFilesTarget files, string path)
    {
        WorkspaceDirectoryListing? listing = null; WorkspaceFileError? failure = null;
        await Task.Run(() =>
        {
            try { listing = WorkspaceFiles.List(path, files.Root); }
            catch (WorkspaceFileException ex) { failure = ex.Error; }
            catch (Exception) { failure = WorkspaceFileError.Unreadable; }
        });
        files.Tree.Apply(path, listing, failure);
    }

    private static string FileSymbolKind(string name) => FilePreviewClassifier.KindForName(name)?.Tag switch
    {
        FilePreviewKindTag.Source => "source",
        FilePreviewKindTag.Markdown => "markdown",
        FilePreviewKindTag.Image => "image",
        _ => "unsupported",
    };

    /// <summary>The tree's rows and the notes after them, as PaneView.RenderTree draws them.</summary>
    private static object WebFilesModel(FilePaneTree tree)
    {
        var (allRows, hitCap) = tree.Rows();
        var (rows, drawnCapped) = FilePaneDrawing.TreeRows(allRows);
        var filtering = tree.IsFiltering;
        var notes = new List<string>();
        if (tree.FolderErrors.TryGetValue("", out var rootError)) notes.Add(rootError);
        else if (!filtering && tree.Children.TryGetValue("", out var top) && top.Count == 0) notes.Add(Locale.Get("files.tree.empty"));
        else if (!filtering && tree.Truncated.Contains("")) notes.Add(Locale.Get("files.tree.truncated", new Dictionary<string, string> { ["count"] = WorkspaceFiles.MaximumEntriesPerFolder.ToString(CultureInfo.CurrentCulture) }));
        if (filtering && rows.Count == 0) notes.Add(Locale.Get("files.tree.noMatches"));
        if (!filtering && drawnCapped) notes.Add(Locale.Get("files.tree.truncated", new Dictionary<string, string> { ["count"] = FilePaneDrawing.MaximumTreeRows.ToString(CultureInfo.CurrentCulture) }));
        if (filtering && hitCap) notes.Add(Locale.Get("files.tree.moreResults", new Dictionary<string, string> { ["count"] = FilePaneTree.MaximumFilterResults.ToString("N0", CultureInfo.CurrentCulture) }));
        return new
        {
            available = true, filter = tree.Filter, filtering, notes, selected = tree.SelectedPath, preview = tree.PreviewPath,
            rows = rows.Select(row => new
            {
                path = row.Entry.RelativePath, name = row.Entry.Name, depth = row.Depth, dir = row.Entry.IsDirectory, expanded = row.IsExpanded, noise = row.Entry.IsNoise, symlink = row.Entry.IsSymlink,
                symbol = row.Entry.IsDirectory ? null : FileSymbolKind(row.Entry.Name), caption = row.Caption,
                reports = tree.FolderErrors.ContainsKey(row.Entry.RelativePath) || tree.Truncated.Contains(row.Entry.RelativePath),
            }).ToList(),
        };
    }

    /// <summary>
    /// One file's preview as PaneView.Prepare and DrawPreview make it: the text cut to what the pane draws, its coloured spans
    /// (past the highlighter's or the runs' cap the rest stays plain and a note says so), the notes, and a picture as a data URL.
    /// </summary>
    private async Task<object> WebFilePreview(string root, string path)
    {
        FilePreviewData data;
        try { data = await Task.Run(() => FilePreviewLoader.Load(root, path)); }
        catch (Exception) { return new { path, name = "", kind = "unsupported", failure = Locale.Get("files.preview.failed"), banners = Array.Empty<string>() }; }
        if (data.Failure is { } failure) return new { path, name = data.Name, kind = "unsupported", failure, banners = Array.Empty<string>() };
        var kind = data.Kind.Tag switch { FilePreviewKindTag.Source => "source", FilePreviewKindTag.Markdown => "markdown", FilePreviewKindTag.Image => "image", _ => "unsupported" };
        var size = PaneView.WebFormatBytes(data.Size);
        var modified = data.Modified?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        string Count(int value) => value.ToString("N0", CultureInfo.CurrentCulture);
        if (data.Text is { } full)
        {
            var source = await Task.Run(() =>
            {
                var (text, renderCapped) = FilePaneDrawing.SourceText(full);
                if (text.Contains("\r\n", StringComparison.Ordinal)) text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
                var (starts, longest) = SourceLines.Scan(text);
                var language = data.Kind.Tag == FilePreviewKindTag.Source ? data.Kind.Language : SourceLanguage.Plain;
                var tokens = SourceHighlighter.Tokens(text, language);
                var capped = language != SourceLanguage.Plain && text.Length > SourceHighlighter.MaximumUnits;
                var units = SourceHighlighter.MaximumUnits;
                if (tokens.Count > PaneView.WebColouredRuns) { tokens = tokens[..PaneView.WebColouredRuns]; capped = true; units = tokens[^1].Location + tokens[^1].Length; }
                var flat = new int[tokens.Count * 3];
                for (var index = 0; index < tokens.Count; index++) { flat[index * 3] = (int)tokens[index].Kind; flat[index * 3 + 1] = tokens[index].Location; flat[index * 3 + 2] = tokens[index].Length; }
                return (Text: text, Tokens: flat, Lines: starts.Count, Wraps: longest > SourceLines.WrapThreshold, RenderCapped: renderCapped, Capped: capped, Units: units);
            });
            var wrapped = Locale.Get("files.preview.wrapped", new Dictionary<string, string> { ["count"] = Count(SourceLines.WrapThreshold) });
            var banners = new List<string>();
            if (data.Kind.Tag == FilePreviewKindTag.Markdown && !data.MarkdownRenderable) banners.Add(Locale.Get("files.markdown.tooLarge"));
            if (source.RenderCapped) banners.Add(Locale.Get("files.preview.renderCapped", new Dictionary<string, string> { ["count"] = (FilePaneDrawing.MaximumSourceUnits / 1_024).ToString(CultureInfo.CurrentCulture) }));
            else if (data.Truncated) banners.Add(Locale.Get("files.preview.truncated"));
            if (source.Capped) banners.Add(Locale.Get("files.preview.highlightCapped", new Dictionary<string, string> { ["count"] = Count(source.Units) }));
            if (source.Wraps) banners.Add(wrapped);
            return new
            {
                path, name = data.Name, kind, encoding = data.Encoding is { } encoding ? TextEncodings.DisplayName(encoding) : null,
                text = source.Text, tokens = source.Tokens, lines = source.Lines, wraps = source.Wraps, banners,
                renderedBanners = source.Wraps ? new[] { wrapped } : [], markdown = data.Kind.Tag == FilePreviewKindTag.Markdown && data.MarkdownRenderable, size, modified,
            };
        }
        if (data.Kind.Tag == FilePreviewKindTag.Image)
        {
            var (image, reason, fitOnly) = await WebFileImage(data);
            var banners = fitOnly ? new[] { Locale.Get("files.image.fitOnly", new Dictionary<string, string> { ["count"] = (FilePreviewClassifier.MaximumFullPixels / 1_000_000).ToString(CultureInfo.CurrentCulture) }) } : [];
            return new { path, name = data.Name, kind, image, reason, banners, size, modified };
        }
        return new { path, name = data.Name, kind, reason = data.Reason, banners = Array.Empty<string>(), size, modified };
    }

    private static readonly Dictionary<string, string> WebImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["png"] = "image/png", ["jpg"] = "image/jpeg", ["jpeg"] = "image/jpeg", ["gif"] = "image/gif", ["webp"] = "image/webp", ["bmp"] = "image/bmp", ["ico"] = "image/x-icon",
    };

    /// <summary>The largest picture sent as it is; past it the picture is sent at the fit size the pane draws.</summary>
    private const int WebImageBytes = 16 * 1_048_576;

    /// <summary>
    /// A picture as PaneView.ImageView decodes it: an SVG drawn by NativeSvgRaster, a PDF's first page, any other one decoded to
    /// check its pixels. One the page can draw goes as it is; any other, or one too large to draw whole, as a PNG at the fit size.
    /// </summary>
    private static async Task<(object? Image, string? Reason, bool FitOnly)> WebFileImage(FilePreviewData data)
    {
        var bytes = data.ImageBytes ?? [];
        var extension = FilePreviewClassifier.FileExtension(data.Name);
        string Pixels(double width, double height) => Locale.Get("files.image.pixels", new Dictionary<string, string> { ["width"] = ((long)width).ToString(CultureInfo.InvariantCulture), ["height"] = ((long)height).ToString(CultureInfo.InvariantCulture) });
        static string Url(string type, byte[] content) => "data:" + type + ";base64," + Convert.ToBase64String(content);
        try
        {
            if (extension == "svg")
            {
                var raster = await NativeSvgRaster.Render(bytes, FilePreviewClassifier.MaximumFitPixels);
                return (new { url = Url("image/png", raster.Png), width = raster.OriginalWidth, height = raster.OriginalHeight, bitmap = false, label = Pixels(raster.OriginalWidth, raster.OriginalHeight), zoomable = true }, null, false);
            }
            if (extension == "pdf")
            {
                var document = await PdfDocument.LoadFromStreamAsync(await PaneView.WebBuffer(bytes));
                if (document.PageCount == 0) return (null, Locale.Get("files.preview.failed"), false);
                using var page = document.GetPage(0);
                if (!FilePreviewClassifier.IsDrawable(page.Size.Width, page.Size.Height)) return (null, null, false);
                var scale = Math.Min(2.0, FilePreviewClassifier.MaximumFitPixels / Math.Max(page.Size.Width, page.Size.Height));
                using var rendered = new InMemoryRandomAccessStream();
                await page.RenderToStreamAsync(rendered, new PdfPageRenderOptions { DestinationWidth = (uint)Math.Max(1, page.Size.Width * scale), DestinationHeight = (uint)Math.Max(1, page.Size.Height * scale) });
                return (new { url = Url("image/png", await ReadAll(rendered)), width = page.Size.Width, height = page.Size.Height, bitmap = false, label = Pixels(page.Size.Width, page.Size.Height), zoomable = true }, null, false);
            }
            var decoder = await BitmapDecoder.CreateAsync(await PaneView.WebBuffer(bytes));
            long width = decoder.OrientedPixelWidth, height = decoder.OrientedPixelHeight;
            if (FilePreviewClassifier.PixelCount(width, height) is not { } pixels || pixels > FilePreviewClassifier.MaximumDecodePixels)
                return (null, Locale.Get("files.preview.tooManyPixels", new Dictionary<string, string> { ["count"] = (FilePreviewClassifier.MaximumDecodePixels / 1_000_000).ToString(CultureInfo.CurrentCulture) }), false);
            var full = pixels <= FilePreviewClassifier.MaximumFullPixels;
            string url;
            if (full && bytes.Length <= WebImageBytes && WebImageTypes.TryGetValue(extension, out var type)) url = Url(type, bytes);
            else
            {
                // The fit size, at most MaximumFitPixels on the long side, as a PNG.
                var fit = Math.Min(1.0, (double)FilePreviewClassifier.MaximumFitPixels / Math.Max(width, height));
                using var output = new InMemoryRandomAccessStream();
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
                using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, new BitmapTransform { ScaledWidth = (uint)Math.Max(1, decoder.PixelWidth * fit), ScaledHeight = (uint)Math.Max(1, decoder.PixelHeight * fit), InterpolationMode = BitmapInterpolationMode.Fant }, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
                encoder.SetSoftwareBitmap(bitmap); await encoder.FlushAsync();
                url = Url("image/png", await ReadAll(output));
            }
            return (new { url, width, height, bitmap = true, label = Pixels(width, height), zoomable = full }, null, !full);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return (null, Locale.Get("files.preview.failed"), false); }
    }

    private static async Task<byte[]> ReadAll(IRandomAccessStream stream)
    {
        stream.Seek(0);
        var buffer = new byte[stream.Size];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size); reader.ReadBytes(buffer);
        return buffer;
    }

    // ---- Browser ----

    /// <summary>A browser pane's native WebView2 over the page, its history and where the page's slot is (device pixels).</summary>
    private sealed class WebBrowserPane(string id)
    {
        public string Id { get; } = id;
        public WebView2? View;
        public readonly BrowserHistory History = new();
        public string Status = "starting";
        public Task Ready = Task.CompletedTask;
        public (double X, double Y, double Width, double Height) Bounds;
        public bool Shown, Installing, InstallFailed;
        public string? InstallMessage;
    }

    private readonly Dictionary<string, WebBrowserPane> webBrowsers = new(StringComparer.Ordinal);

    private WebBrowserPane WebBrowser(string? id)
    {
        if (webBrowsers.TryGetValue(id ?? "", out var existing)) return existing;
        if (id is null || service.Snapshot.Sessions.FirstOrDefault(p => p.Id == id && p.Kind is "browser" or AgentIOPaneKind.Browser) is not { } session) throw new InvalidOperationException("Session not found.");
        var browser = webBrowsers[id] = new WebBrowserPane(id);
        browser.Ready = StartWebBrowserAsync(browser, session.WorkspaceProfileKey ?? session.WorkspaceId);
        return browser;
    }

    private static object WebBrowserState(WebBrowserPane browser)
    {
        var state = browser.History.State(false);
        return new { status = browser.Status, url = state.Url?.AbsoluteUri, canGoBack = state.CanGoBack, canGoForward = state.CanGoForward, installing = browser.Installing, installMessage = browser.InstallMessage, installFailed = browser.InstallFailed };
    }

    private void PushWebBrowser(WebBrowserPane browser) => webShell?.Bridge.Push(new { kind = "browser", paneId = browser.Id, state = WebBrowserState(browser) });

    /// <summary>
    /// The engine as PaneView.StartBrowserAsync starts it: nothing while the setting is off, the install offer without the runtime,
    /// the workspace's shared environment, then popups, downloads and script dialogs refused and history from the engine's own
    /// navigation events. The control stands in the web shell's grid over the page, sized 0 until the page says where its slot is.
    /// </summary>
    private async Task StartWebBrowserAsync(WebBrowserPane browser, string profileKey)
    {
        void Finish(string status) { browser.Status = status; PushWebBrowser(browser); }
        if (!BrowserEngineEnabled) { Finish("disabled"); return; }
        if (new WebView2RuntimeLocator().GetRuntimeVersion() is null) { Finish("missing"); return; }
        if (webShell is not { } shell || await GetBrowserEnvironmentAsync(profileKey) is not { } environment) { Finish("failed"); return; }
        var view = new WebView2 { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Width = 0, Height = 0 };
        shell.Children.Add(view);
        try { await view.EnsureCoreWebView2Async(environment); }
        catch (Exception) { shell.Children.Remove(view); Finish("failed"); return; }
        if (!webBrowsers.ContainsKey(browser.Id) || closing) { view.Close(); shell.Children.Remove(view); return; }
        var core = view.CoreWebView2;
        core.Settings.AreDefaultScriptDialogsEnabled = false;
        core.NewWindowRequested += (_, args) => args.Handled = true;
        core.DownloadStarting += (_, args) => { args.Cancel = true; args.Handled = true; };
        core.ScriptDialogOpening += (_, _) => { };
        var startedUris = new Dictionary<ulong, string>();
        core.NavigationStarting += (_, args) => startedUris[args.NavigationId] = args.Uri;
        core.NavigationCompleted += (engine, args) =>
        {
            startedUris.Remove(args.NavigationId, out var started);
            var reported = string.IsNullOrEmpty(engine.Source) ? started : engine.Source;
            if (args.IsSuccess && Uri.TryCreate(reported, UriKind.Absolute, out var uri)) browser.History.Visit(uri);
            PushWebBrowser(browser);
        };
        // A press inside the page never reaches the web shell's pane slot: the browser taking focus makes its pane the active one.
        view.GotFocus += (_, _) => ActivatePane(browser.Id);
        browser.View = view;
        PlaceWebBrowser(browser);
        Finish("ready");
    }

    /// <summary>Lays the browser over the page's slot: device pixels to the window's view pixels, collapsed while the slot is hidden.</summary>
    private void PlaceWebBrowser(WebBrowserPane browser)
    {
        if (browser.View is not { } view || webShell is not { } shell) return;
        var scale = shell.XamlRoot?.RasterizationScale ?? 1;
        var (x, y, width, height) = browser.Bounds;
        var shows = browser.Shown && width > 0 && height > 0;
        if (shows)
        {
            view.Margin = new Thickness(x / scale, y / scale, 0, 0); view.Width = width / scale; view.Height = height / scale;
        }
        view.Visibility = shows ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The Microsoft per-user bootstrapper, run only on this click (PaneView.RunBrowserInstallAsync); a success starts the browser.</summary>
    private async Task InstallWebBrowserRuntime(WebBrowserPane browser)
    {
        if (browser.Installing || browser.Status != "missing") return;
        browser.Installing = true; browser.InstallMessage = null; PushWebBrowser(browser);
        var temporary = Path.Combine(Path.GetTempPath(), "MightyClaudeWebView2Setup_" + Wire.Id());
        try
        {
            Directory.CreateDirectory(temporary);
            var outcome = await new BrowserEngineService(new WebView2RuntimeLocator(), new WebView2BootstrapperInstaller()).InstallAsync(temporary, CancellationToken.None);
            browser.InstallFailed = !outcome.Success;
            browser.InstallMessage = outcome.Success ? Locale.Get("browser.runtime.installSuccess") : outcome.Error ?? Locale.Get("browser.runtime.installFailed");
        }
        finally
        {
            browser.Installing = false;
            try { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); } catch { }
        }
        PushWebBrowser(browser);
        if (!browser.InstallFailed && service.Snapshot.Sessions.FirstOrDefault(s => s.Id == browser.Id) is { } session)
        {
            browser.Status = "starting"; browser.Ready = StartWebBrowserAsync(browser, session.WorkspaceProfileKey ?? session.WorkspaceId);
        }
    }

    /// <summary>An agent's link opened in its browser pane while the web shell shows (OpenAgentUrl); false when the browser cannot.</summary>
    private async Task<bool> WebOpenAgentBrowserUrl(AgentIOBinding binding, string paneId, Uri url, CancellationToken cancellation)
    {
        if (webShell is null) return false;
        try
        {
            var browser = WebBrowser(paneId);
            await browser.Ready.WaitAsync(TimeSpan.FromSeconds(20), cancellation);
            // Cancellation and a revoked binding always propagate without opening.
            cancellation.ThrowIfCancellationRequested(); RequireActiveAgentBinding(binding);
            if (browser.View?.CoreWebView2 is not { } core) return false;
            core.Navigate(url.AbsoluteUri); return true;
        }
        catch (Exception ex) when (ex is TimeoutException or System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException or InvalidOperationException) { return false; }
    }

    /// <summary>Closes the browser of every pane that is gone (all of them while the window closes).</summary>
    private void CloseWebBrowsers(AppSnapshot? state)
    {
        foreach (var browser in webBrowsers.Values.Where(b => state is null || !state.Sessions.Any(s => s.Id == b.Id)).ToArray())
        {
            webBrowsers.Remove(browser.Id);
            if (browser.View is { } view) { view.Close(); webShell?.Children.Remove(view); browser.View = null; }
        }
    }

    /// <summary>
    /// The panes' web smoke, inside the smoke profile only: a files pane lists a fixture folder, opens it and previews a file in
    /// colour; a shell pane's xterm (typed into from the page) shows a harmless echo of a marker through ConPTY; a browser pane
    /// browses a local page on a virtual host (no network), its native control lies over the page's slot and is hidden once
    /// another tab shows. Each pane is closed afterwards.
    /// </summary>
    private async Task<bool> RunWebPanesSmoke(CoreWebView2 core, Workspace workspace, Dictionary<string, object?> result)
    {
        async Task<JsonElement> Probe(string script) => JsonDocument.Parse(JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("JSON.stringify(" + script + ")")) ?? "null").RootElement.Clone();
        async Task<bool> True(string script) => (await Probe(script)).ValueKind == JsonValueKind.True;
        async Task<bool> Until(Func<Task<bool>> done, double seconds = 15)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < deadline) { if (await done()) return true; await Task.Delay(50); }
            return false;
        }
        static string Js(string value) => JsonSerializer.Serialize(value);
        string Slot(string id) => "document.querySelector('.pane-slot[data-pane-id=' + " + Js(JsonSerializer.Serialize(id)) + " + ']')";
        var checks = new Dictionary<string, object?>(); result["panes"] = checks;
        var folder = LocalPath(workspace) ?? throw new InvalidOperationException("smoke workspace is not local");

        // Files: the fixture folder in the tree, opened, its file previewed with line numbers and colour.
        Directory.CreateDirectory(Path.Combine(folder, "smoke-folder"));
        await File.WriteAllTextAsync(Path.Combine(folder, "smoke-folder", "hello.ts"), "const smokeMarker = 42; // web files smoke\n");
        await OpenFilePane(workspace.Id);
        var filesId = FilePaneKind.PaneId(workspace.Id);
        string Row(string path) => Slot(filesId) + "?.querySelector('[data-testid=files-tree] [data-path=' + " + Js(JsonSerializer.Serialize(path)) + " + ']')";
        var listed = await Until(() => True("!!" + Row("smoke-folder") + " && !" + Slot(filesId) + ".classList.contains('hidden')"));
        if (listed) await core.ExecuteScriptAsync(Row("smoke-folder") + ".click()");
        var opened = listed && await Until(() => True("!!" + Row("smoke-folder/hello.ts")));
        if (opened) await core.ExecuteScriptAsync(Row("smoke-folder/hello.ts") + ".click()");
        const string Preview = "[data-testid=files-preview][data-preview-path=\"smoke-folder/hello.ts\"]";
        var previewed = opened && await Until(() => True("(() => { const p = " + Slot(filesId) + "?.querySelector('" + Preview + "'); return !!p && (p.querySelector('[data-testid=files-source]')?.textContent ?? '').includes('smokeMarker') && !!p.querySelector('.syntax-keyword') && (p.querySelector('.files-lines')?.textContent ?? '').startsWith('1'); })()"));
        checks["filesTree"] = listed; checks["filesFolderOpened"] = opened; checks["filesPreview"] = previewed;
        await CloseSession(filesId);

        // Terminal: the page's xterm types a command that writes a marker in two halves; the joined marker must come back in its buffer.
        var terminalId = Wire.Id(); PaneView? terminal = null; var echoed = false; var started = false;
        try
        {
            smokeTerminalEnabled = true;
            await AddPane("shell", shape: pane => pane with { Id = terminalId, Title = "Web smoke terminal" });
            started = await Until(() => Task.FromResult(views.TryGetValue(terminalId, out var pane) && pane.CurrentTerminal is not null), 30);
            terminal = views.GetValueOrDefault(terminalId);
            // A marker the terminal must render back through ConPTY.
            var marker = "MIGHTY_WEB_" + Guid.NewGuid().ToString("N");
            var powershell = Path.GetFileNameWithoutExtension(PseudoTerminal.DefaultShell).Equals("powershell", StringComparison.OrdinalIgnoreCase);
            var command = powershell ? "[Console]::WriteLine(('" + marker[..12] + "'+'" + marker[12..] + "'))\r" : "set MIGHTY_SMOKE=" + marker[..12] + "\rset MIGHTY_SMOKE=%MIGHTY_SMOKE%" + marker[12..] + "\recho %MIGHTY_SMOKE%\r";
            var typed = started && await Until(() => True("window.mightyPanes?.terminalInput(" + Js(terminalId) + ", " + Js(command) + ") === true"));
            echoed = typed && await Until(() => True("(window.mightyPanes?.terminalText(" + Js(terminalId) + ") ?? '').includes(" + Js(marker) + ")"), 20);
            checks["terminalTyped"] = typed;
            // What the page's terminal holds, when the marker did not come back: the shell's prompt, an error, or nothing at all.
            if (!echoed && terminal is not null) checks["terminalHost"] = terminal.TerminalDiagnostics();
            if (!echoed) checks["terminalPage"] = await Probe("window.mightyPanes?.terminalOutputs?.() ?? null");
            if (!echoed) checks["terminalText"] = await Probe("(window.mightyPanes?.terminalText(" + Js(terminalId) + ") ?? '(no terminal)').slice(-600)");
            checks["terminalFooter"] = await Probe(Slot(terminalId) + "?.querySelector('[data-testid=terminal-footer]')?.textContent ?? null");
        }
        finally
        {
            smokeTerminalEnabled = false;
            if (terminal is not null) await terminal.DisposeTerminalAsync().WaitAsync(TimeSpan.FromSeconds(20));
            if (service.Snapshot.Sessions.Any(s => s.Id == terminalId)) await CloseSession(terminalId);
        }
        checks["terminalStarted"] = started; checks["terminalEcho"] = echoed;

        // Browser: one tab group, so another tab can be shown over it; the engine counts as on for this smoke only.
        await ApplyLayoutPreset("tabs");
        ForceBrowserEngineForSmoke();
        await AddBrowserPane();
        var browserId = service.Snapshot.Sessions.LastOrDefault(s => s.Kind == "browser" && s.WorkspaceId == workspace.Id)?.Id ?? throw new InvalidOperationException("browser pane missing");
        var created = await Until(() => Task.FromResult(webBrowsers.ContainsKey(browserId)));
        var browser = created ? webBrowsers[browserId] : null;
        if (browser is not null) await browser.Ready.WaitAsync(TimeSpan.FromSeconds(30));
        checks["browserStatus"] = browser?.Status;
        bool placed = false, browsed = false, hidden = false, overlaid = false, wentBack = false, popupBlocked = false;
        var pages = Path.Combine(Path.GetTempPath(), "MightyWebBrowserSmoke_" + Wire.Id());
        try
        {
            if (browser?.View is { CoreWebView2: { } engine } view)
            {
                const string Host = "mighty-smoke.invalid";
                Directory.CreateDirectory(pages);
                await File.WriteAllTextAsync(Path.Combine(pages, "one.html"), "<!doctype html><html><head><meta charset=\"utf-8\"><title>MightyWebSmoke</title></head><body><p>MightyWebSmoke</p></body></html>");
                await File.WriteAllTextAsync(Path.Combine(pages, "two.html"), "<!doctype html><html><head><meta charset=\"utf-8\"><title>MightyWebSmokeTwo</title></head><body><p>MightyWebSmokeTwo</p></body></html>");
                engine.SetVirtualHostNameToFolderMapping(Host, pages, CoreWebView2HostResourceAccessKind.Deny);
                var page = "https://" + Host + "/one.html";
                engine.Navigate(page);
                // The address the page shows came back from the host's history as a push.
                browsed = await Until(() => True("(" + Slot(browserId) + "?.querySelector('[data-testid=browser-address]')?.value ?? '') === " + Js(page)));
                // A second page, then the page's back button: the host's history and the engine go back to the first.
                var second = "https://" + Host + "/two.html";
                engine.Navigate(second);
                var onSecond = browsed && await Until(() => True("(" + Slot(browserId) + "?.querySelector('[data-testid=browser-address]')?.value ?? '') === " + Js(second)));
                wentBack = onSecond && await Until(() => True("(() => { const b = " + Slot(browserId) + "?.querySelector('button.browser-nav'); if (!b || b.disabled) return false; b.click(); return true; })()"))
                    && await Until(async () => browser.History.Current?.AbsoluteUri == page && engine.Source == page && await True("(" + Slot(browserId) + "?.querySelector('[data-testid=browser-address]')?.value ?? '') === " + Js(page)));
                // window.open() from the page: the request arrives and the app ends it Handled, so no window opens and the page stays.
                var popups = new List<bool>();
                void Popup(CoreWebView2 _, CoreWebView2NewWindowRequestedEventArgs args) => popups.Add(args.Handled);
                engine.NewWindowRequested += Popup;
                try
                {
                    var popupOpened = await engine.ExecuteScriptAsync("(() => { window.open('two.html'); return 'opened'; })()") == "\"opened\"";
                    popupBlocked = popupOpened && await Until(() => Task.FromResult(popups.Count > 0)) && popups.All(handled => handled) && browser.History.Current?.AbsoluteUri == page;
                }
                finally { engine.NewWindowRequested -= Popup; }
                checks["browserBackAndPopup"] = new { onSecond, wentBack, popupBlocked, popups = popups.Count };
                // Over the slot: the control's place in device pixels within 2 of the slot's.
                var slot = Slot(browserId) + "?.querySelector('[data-testid=browser-slot]')";
                placed = await Until(async () =>
                {
                    if (view.Visibility != Visibility.Visible || view.ActualWidth <= 0 || view.ActualHeight <= 0) return false;
                    var rect = await Probe("(() => { const s = " + slot + "; if (!s) return null; const r = s.getBoundingClientRect(), d = devicePixelRatio; return [r.left * d, r.top * d, r.width * d, r.height * d]; })()");
                    if (rect.ValueKind != JsonValueKind.Array) return false;
                    var scale = webShell?.XamlRoot?.RasterizationScale ?? 1;
                    double[] native = [view.Margin.Left * scale, view.Margin.Top * scale, view.ActualWidth * scale, view.ActualHeight * scale];
                    var measured = rect.EnumerateArray().Select(n => n.GetDouble()).ToArray();
                    checks["browserBounds"] = new { page = measured, native };
                    return measured.Zip(native).All(pair => Math.Abs(pair.First - pair.Second) <= 2);
                });
                // A page menu opened over the slot (the tab's menu at a point inside it): the native control steps aside, and comes back once the menu closes.
                var menuOpened = placed && await True("(() => { const s = " + slot + "?.getBoundingClientRect(); const h = " + Slot(browserId) + "?.closest('[data-testid=tab-group]')?.querySelector('[data-testid=tab][data-tab-id=' + " + Js(JsonSerializer.Serialize(browserId)) + " + '] .tab-handle'); if (!s || !h) return false; "
                    + "h.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, button: 2, clientX: s.left + 20, clientY: s.top + 20 })); return true; })()");
                var steppedAside = menuOpened && await Until(() => Task.FromResult(view.Visibility == Visibility.Collapsed));
                var menuClosed = steppedAside && await True("(() => { window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true })); return true; })()") && await Until(async () => view.Visibility == Visibility.Visible && await True("!document.querySelector('[data-testid=menu]')"));
                checks["browserMenuOver"] = new { menuOpened, steppedAside, menuClosed };
                overlaid = steppedAside && menuClosed;
                // Another tab of the same group: the slot is hidden, so the native control must be too.
                var other = await Probe("(() => { const group = " + Slot(browserId) + "?.closest('[data-testid=tab-group]'); const tab = [...(group?.querySelectorAll('[data-testid=tab]') ?? [])].find(t => t.dataset.tabId !== " + Js(browserId) + "); tab?.querySelector('.tab-handle')?.click(); return tab?.dataset.tabId ?? null; })()");
                hidden = other.ValueKind == JsonValueKind.String && await Until(() => Task.FromResult(view.Visibility == Visibility.Collapsed));
            }
        }
        finally
        {
            await CloseSession(browserId);
            try { if (Directory.Exists(pages)) Directory.Delete(pages, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        var released = !webBrowsers.ContainsKey(browserId) && (browser?.View is null);
        checks["browserNavigated"] = browsed; checks["browserPlaced"] = placed; checks["browserHiddenUnderMenu"] = overlaid; checks["browserHiddenOnTabSwitch"] = hidden; checks["browserClosed"] = released;
        checks["browserBack"] = wentBack; checks["browserPopupBlocked"] = popupBlocked;
        return listed && opened && previewed && started && echoed && browsed && wentBack && popupBlocked && placed && overlaid && hidden && released;
    }

    private sealed partial class PaneView
    {
        internal const int WebColouredRuns = MaximumColouredRuns;
        internal static string WebFormatBytes(long bytes) => FormatBytes(bytes);
        internal static Task<InMemoryRandomAccessStream> WebBuffer(byte[] bytes) => Buffer(bytes);

        private bool webTerminalWatched, webTerminalFooterQueued;

        /// <summary>The footer the page draws, and from now on a push whenever one of the view's terminal TextBlocks changes.</summary>
        internal object WebTerminalOpen()
        {
            if (!webTerminalWatched && terminalHost is not null)
            {
                webTerminalWatched = true;
                void Watch(DependencyObject? element, DependencyProperty property) => element?.RegisterPropertyChangedCallback(property, (_, _) => QueueWebTerminalFooter());
                Watch(terminalNotice, TextBlock.TextProperty); Watch(terminalNotice, UIElement.VisibilityProperty);
                Watch(terminalRestart, Control.IsEnabledProperty); Watch(terminalRestart, UIElement.VisibilityProperty);
                Watch(terminalTitle, TextBlock.TextProperty); Watch(terminalDirectory, TextBlock.TextProperty); Watch(terminalGrid, TextBlock.TextProperty);
            }
            return WebTerminalFooter();
        }

        private object WebTerminalFooter()
        {
            var shell = Session.Kind == "shell";
            return new
            {
                available = terminalHost is not null, shell,
                notice = terminalNotice is { Visibility: Visibility.Visible } notice ? notice.Text : null,
                restart = terminalRestart is { Visibility: Visibility.Visible }, restartEnabled = terminalRestart?.IsEnabled == true,
                engine = shell ? "ConPTY" : Locale.Get("agentTerminal.terminalPane.title"),
                title = terminalTitle?.Text ?? "", directory = terminalDirectoryPath, grid = terminalGrid?.Text ?? "",
            };
        }

        private void QueueWebTerminalFooter()
        {
            if (webTerminalFooterQueued || owner.webShell is null) return;
            webTerminalFooterQueued = true;
            owner.DispatcherQueue.TryEnqueue(() =>
            {
                webTerminalFooterQueued = false;
                if (!terminalClosed) owner.webShell?.Bridge.Push(new { kind = "terminal", paneId = id, message = new { type = "footer", footer = WebTerminalFooter() } });
            });
        }

        internal Task WebRestartTerminal() => RestartTerminalAsync();
    }
}
