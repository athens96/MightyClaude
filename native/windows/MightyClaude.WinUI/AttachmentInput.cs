using MightyClaude.Core;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace MightyClaude.WinUI;

internal static class AttachmentInput
{
    internal static bool ContainsFiles(DataPackageView data) => data.Contains(StandardDataFormats.StorageItems) || data.Contains(StandardDataFormats.Bitmap);
    internal static async Task<List<RunAttachment>> ReadFilesAsync(IEnumerable<IStorageItem> items)
    {
        var selected = items.ToArray();
        if (selected.Length > AttachmentSupport.MaximumCount) throw new ArgumentException(Locale.Get("composer.attachment.tooMany"));
        if (selected.Any(item => item is not StorageFile)) throw new ArgumentException(Locale.Get("composer.attachment.folderChosen"));
        var attachments = new List<RunAttachment>(); long total = 0;
        foreach (var file in selected.Cast<StorageFile>())
        {
            using var stream = await file.OpenReadAsync(); var data = await ReadBytesAsync(stream, AttachmentSupport.MaximumFileBytes);
            total += data.Length; if (total > AttachmentSupport.MaximumTotalBytes) throw new ArgumentException(Locale.Get("attachments.error.totalTooLarge"));
            attachments.Add(AttachmentSupport.Make(file.Name, data));
        }
        return attachments;
    }
    internal static async Task<List<RunAttachment>> ReadDataAsync(DataPackageView data)
    {
        if (data.Contains(StandardDataFormats.StorageItems)) return await ReadFilesAsync(await data.GetStorageItemsAsync());
        if (!data.Contains(StandardDataFormats.Bitmap)) return [];
        var reference = await data.GetBitmapAsync(); using var source = await reference.OpenReadAsync();
        if (source.Size > 64 * 1024 * 1024) throw new ArgumentException(Locale.Get("composer.attachment.clipboardImageTooLarge"));
        var decoder = await BitmapDecoder.CreateAsync(source);
        if (decoder.PixelWidth == 0 || decoder.PixelHeight == 0 || (ulong)decoder.PixelWidth * decoder.PixelHeight > 16 * 1024 * 1024) throw new ArgumentException(Locale.Get("composer.attachment.clipboardImagePixels"));
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        using var target = new InMemoryRandomAccessStream(); var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, target); encoder.SetSoftwareBitmap(bitmap); await encoder.FlushAsync();
        return [AttachmentSupport.Make("clipboard-image.png", await ReadBytesAsync(target, AttachmentSupport.MaximumFileBytes))];
    }
    private static async Task<byte[]> ReadBytesAsync(IRandomAccessStream stream, int maximum)
    {
        var length = stream.Size; if (length > (ulong)maximum) throw new ArgumentException(Locale.Get("composer.attachment.fileTooLarge"));
        using var input = stream.GetInputStreamAt(0); using var reader = new DataReader(input); var size = (uint)length;
        var loaded = await reader.LoadAsync(size); if (loaded != size) throw new IOException(Locale.Get("composer.attachment.readIncomplete"));
        var data = new byte[size]; reader.ReadBytes(data); return data;
    }
    internal static async Task<BitmapImage> PreviewAsync(RunAttachment attachment, int maximum = 640)
    {
        var bytes = AttachmentSupport.Decode(attachment); using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0))) { writer.WriteBytes(bytes); await writer.StoreAsync(); await writer.FlushAsync(); }
        stream.Seek(0); var decoder = await BitmapDecoder.CreateAsync(stream);
        if (decoder.PixelWidth == 0 || decoder.PixelHeight == 0 || (ulong)decoder.PixelWidth * decoder.PixelHeight > 16 * 1024 * 1024) throw new ArgumentException(Locale.Get("composer.attachment.previewTooLarge"));
        var scale = Math.Min(1, Math.Min((double)maximum / decoder.PixelWidth, (double)Math.Min(maximum, 420) / decoder.PixelHeight));
        stream.Seek(0); var bitmap = new BitmapImage { DecodePixelWidth = Math.Max(1, (int)(decoder.PixelWidth * scale)), DecodePixelHeight = Math.Max(1, (int)(decoder.PixelHeight * scale)) }; await bitmap.SetSourceAsync(stream); return bitmap;
    }
}
