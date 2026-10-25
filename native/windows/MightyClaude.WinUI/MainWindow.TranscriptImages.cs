using MightyClaude.Core;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private sealed partial class PaneView
    {
        /// <summary>A picture as the viewer holds it: the verified bytes, their kind and size, and what is drawn (an SVG rendered to PNG).</summary>
        internal sealed record ShownImage(byte[] Bytes, string MediaType, int Width, int Height, byte[] Display);

        /// <summary>Reads and checks a picture the way the viewer opens it (stored, a file under the workspace or a temporary root, or inline); an SVG is drawn to PNG no larger than <paramref name="maximumEdge"/>.</summary>
        internal async Task<ShownImage> LoadTranscriptImage(TranscriptAction action, int maximumEdge = 4096)
        {
            var root = LocalPath(Workspace);
            var bytes = await Task.Run(() => action.Image is { } image ? owner.service.Images.Data(image) : AgentImagePaths.Locate(action.Value, root) switch
            {
                AgentImageLocation.File file => AgentImagePaths.Read(file.Path, file.Root),
                AgentImageLocation.Inline inline => AgentImageSupport.DecodeBase64(inline.Base64),
                _ => null,
            });
            if (bytes is null) throw new IOException(Locale.Get("images.missing"));
            var declared = action.Image?.MediaType ?? AgentImageSupport.MediaTypeForFileName(action.Value) ?? (AgentImageSupport.SvgDocument(bytes) ? "image/svg+xml" : "image/png");
            var info = AgentImageSupport.Inspect(bytes, declared);
            var display = info.MediaType == "image/svg+xml" ? (await NativeSvgRaster.Render(bytes, maximumEdge)).Png : bytes;
            return new(bytes, info.MediaType, info.Width, info.Height, display);
        }

        /// <summary>
        /// The viewer's buttons, the WinUI dialog's and the web viewer's (native/windows/web/src/pictures/PictureViewer.tsx):
        /// <c>copy</c> puts the drawn picture on the clipboard, <c>save</c> writes the original through the save picker,
        /// <c>reveal</c> selects the original file (else a verified cache copy) in Explorer, <c>openDefault</c> hands the default
        /// app a verified snapshot.
        /// </summary>
        internal async Task TranscriptImageAction(TranscriptAction action, ShownImage shown, string name)
        {
            var root = LocalPath(Workspace);
            switch (name)
            {
                case "copy":
                {
                    using var stream = await Buffer(shown.Display); var data = new DataPackage(); data.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream)); Clipboard.SetContent(data); Clipboard.Flush();
                    break;
                }
                case "save":
                {
                    var extension = "." + AgentImageSupport.Extensions.GetValueOrDefault(shown.MediaType, "png");
                    var picker = new FileSavePicker { SuggestedFileName = "image" }; picker.FileTypeChoices.Add(shown.MediaType, [extension]);
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(owner));
                    if (await picker.PickSaveFileAsync() is { } file) await FileIO.WriteBytesAsync(file, shown.Bytes);
                    break;
                }
                case "reveal":
                {
                    var path = await Task.Run(() => ImageExternalPath(action, root, shown.Bytes, shown.MediaType, revealOriginal: true));
                    var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = false };
                    start.ArgumentList.Add("/select,"); start.ArgumentList.Add(path); using var process = Process.Start(start);
                    break;
                }
                case "openDefault":
                {
                    // The default viewer receives the verified snapshot. SVG is
                    // rendered to PNG, so a generic .svg browser association cannot
                    // execute script from the agent's document.
                    var externalType = shown.MediaType == "image/svg+xml" ? "image/png" : shown.MediaType;
                    var path = await Task.Run(() => ImageExternalPath(action, root, shown.Display, externalType, revealOriginal: false));
                    using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                    break;
                }
            }
        }

        private Task OpenTranscriptImage(TranscriptAction action) => owner.Act(async () =>
        {
            if (owner.dialogOpen) return;
            owner.dialogOpen = true;
            try
            {
                var shown = await LoadTranscriptImage(action);
                var bitmap = new BitmapImage();
                if (Math.Max(shown.Width, shown.Height) > 4096) { if (shown.Width >= shown.Height) bitmap.DecodePixelWidth = 4096; else bitmap.DecodePixelHeight = 4096; }
                using (var stream = await Buffer(shown.Display)) await bitmap.SetSourceAsync(stream);
                var picture = new Image { Source = bitmap, Stretch = Stretch.Uniform };
                AutomationProperties.SetName(picture, action.Image?.Source ?? Locale.Get("images.open"));
                var viewer = new ScrollViewer { Content = picture, MinZoomFactor = .25f, MaxZoomFactor = 4, ZoomMode = ZoomMode.Enabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
                var body = new Grid { Width = Math.Clamp(owner.root.ActualWidth - 120, 280, 850), Height = Math.Clamp(owner.root.ActualHeight - 180, 260, 650), RowSpacing = DesignMetrics.Spacing.Sm };
                body.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); body.RowDefinitions.Add(new() { Height = GridLength.Auto }); body.Children.Add(viewer);
                var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm };
                controls.Children.Add(Button("−", () => { viewer.ChangeView(null, null, Math.Max(.25f, viewer.ZoomFactor / 1.25f)); return Task.CompletedTask; }));
                controls.Children.Add(Button(Locale.Get("files.image.fit"), () => { viewer.ChangeView(0, 0, 1); return Task.CompletedTask; }));
                controls.Children.Add(Button("+", () => { viewer.ChangeView(null, null, Math.Min(4, viewer.ZoomFactor * 1.25f)); return Task.CompletedTask; }));
                foreach (var (key, name) in new[] { ("images.copy", "copy"), ("images.save", "save"), ("menu.showInExplorer", "reveal"), ("images.openDefault", "openDefault") })
                    controls.Children.Add(Button(Locale.Get(key), () => owner.Act(() => TranscriptImageAction(action, shown, name))));
                var actionsScroll = new ScrollViewer { Content = controls, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Disabled };
                Grid.SetRow(actionsScroll, 1); body.Children.Add(actionsScroll);
                await owner.ShowDialogAsync(owner.StyledDialog(new ContentDialog { Title = Locale.Get("images.open"), Content = body, CloseButtonText = Locale.Get("settings.closeButton"), XamlRoot = owner.root.XamlRoot }));
            }
            finally { owner.dialogOpen = false; }
        });
        private string ImageExternalPath(TranscriptAction action, string? workspace, byte[] snapshot, string mediaType, bool revealOriginal)
        {
            if (revealOriginal && AgentImagePaths.Locate(action.Image?.Path ?? action.Value, workspace) is AgentImageLocation.File original)
            {
                using var file = WorkspaceFiles.OpenFile(Path.GetRelativePath(original.Root, original.Path).Replace(Path.DirectorySeparatorChar, '/'), original.Root);
                return file.Path;
            }
            var image = owner.service.Images.Store(snapshot, mediaType, action.Image?.Source ?? "image");
            var path = owner.service.Images.PathFor(image) ?? throw new IOException(Locale.Get("images.missing"));
            var cacheRoot = WorkspaceFiles.RealPath(owner.service.Images.Directory) ?? throw new IOException(Locale.Get("images.missing"));
            using var cached = WorkspaceFiles.OpenFile(Path.GetRelativePath(cacheRoot, path).Replace(Path.DirectorySeparatorChar, '/'), cacheRoot);
            if (cached.Size != snapshot.Length || AgentImageSupport.Sha256(FilePreviewClassifier.ReadPrefix(cached.Stream, AgentImageSupport.MaximumImageBytes + 1)) != image.Hash)
                throw new IOException(Locale.Get("images.missing"));
            return cached.Path;
        }
    }
}
