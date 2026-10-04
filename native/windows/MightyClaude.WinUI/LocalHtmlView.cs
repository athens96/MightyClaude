using MightyClaude.Core;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Storage.Streams;

namespace MightyClaude.WinUI;

/// <summary>A separate WebView profile with no application bridge or remote requests.</summary>
internal sealed class LocalHtmlView : Grid, IDisposable
{
    private readonly LocalHtmlDocument document;
    private readonly WebView2 browser = new();
    private readonly string profile;
    private readonly SemaphoreSlim reads = new(4, 4);
    private bool initialized, closed;
    internal LocalHtmlView(LocalHtmlDocument document, string profile)
    {
        this.document = document; this.profile = profile; Children.Add(browser);
        Loaded += async (_, _) => { if (!initialized && !closed) { initialized = true; await Initialize(); } };
    }
    private async Task Initialize()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync("", profile, new());
            if (closed) return;
            await browser.EnsureCoreWebView2Async(environment);
            if (closed) return;
            var core = browser.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false; core.Settings.IsWebMessageEnabled = false;
            core.Settings.AreDevToolsEnabled = false; core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false; core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.NavigationStarting += (_, args) => args.Cancel = !document.Allows(args.Uri);
            core.FrameNavigationStarting += (_, args) => args.Cancel = true;
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.DownloadStarting += (_, args) => args.Cancel = true;
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += async (_, args) =>
            {
                using var deferral = args.GetDeferral();
                try
                {
                    LocalHtmlDocument.Resource? resource = null;
                    if (!closed)
                    {
                        await reads.WaitAsync();
                        try { var address = args.Request.Uri; var method = args.Request.Method; resource = await Task.Run(() => document.Read(address, method)); }
                        finally { reads.Release(); }
                    }
                    if (closed) return;
                    var stream = new InMemoryRandomAccessStream();
                    using (var writer = new DataWriter(stream.GetOutputStreamAt(0))) { writer.WriteBytes(resource?.Bytes ?? []); await writer.StoreAsync(); writer.DetachStream(); }
                    stream.Seek(0);
                    args.Response = environment.CreateWebResourceResponse(stream, resource is null ? 403 : 200, resource is null ? "Forbidden" : "OK",
                        "Content-Type: " + (resource?.MediaType ?? "text/plain") + "\r\nContent-Security-Policy: " + LocalHtmlDocument.ContentPolicy + "\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n");
                }
                catch { if (!closed) args.Response = environment.CreateWebResourceResponse(null, 403, "Forbidden", ""); }
                finally { deferral.Complete(); }
            };
            core.Navigate(document.Address);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { if (!closed) { Children.Clear(); Children.Add(new TextBlock { Text = Locale.Get("files.preview.failed"), TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap }); } }
    }
    public void Dispose() { if (closed) return; closed = true; browser.Close(); }
}
