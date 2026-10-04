using System.Globalization;
using System.Text;

namespace MightyClaude.Core;

public sealed class MobileRequestException(int status, string message) : Exception(message) { public int Status { get; } = status; }
public sealed record MobileUploadTicket(int Protocol, string UploadId, int ChunkSize);
public sealed record MobileUploadAttachment(string Id, string Name, int Size);
public sealed record MobileUploadClaim(string Id, IReadOnlyList<string> UploadIds, IReadOnlyList<RunAttachment> Attachments);

/// Bounded, device-owned uploads; open handles prevent symlink/replacement races.
/// A submit spends its claim only after the desktop has accepted the request.
public sealed class MobileUploads : IDisposable
{
    public const int ChunkSize = 196_608;
    private sealed class Upload(string id, string session, string device, string name, int size, FileStream file)
    {
        public readonly string Id = id, Session = session, Device = device, Name = name;
        public readonly int Size = size;
        public readonly FileStream File = file;
        public int Received, Next;
        public bool Completed;
        public string? Claim;
        public DateTimeOffset Updated = DateTimeOffset.UtcNow;
    }
    private readonly object sync = new();
    private readonly Dictionary<string, Upload> uploads = [];
    private readonly string directory;
    public MobileUploads(string directory)
    {
        this.directory = directory; MobileIdentity.SecureDirectory(directory);
        foreach (var file in Directory.EnumerateFiles(directory, "*.part").Take(1024))
            if (Guid.TryParse(Path.GetFileNameWithoutExtension(file), out _) && !File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)) File.Delete(file);
    }
    private void Sweep()
    { foreach (var item in uploads.Values.Where(u => u.Claim is null && DateTimeOffset.UtcNow - u.Updated > TimeSpan.FromMinutes(10)).ToArray()) Remove(item.Id); }
    private void Remove(string id) { if (uploads.Remove(id, out var item)) item.File.Dispose(); }
    private Upload Owned(string id, string device) => uploads.TryGetValue(id, out var value) && value.Device == device ? value : throw new MobileRequestException(404, "Upload not found.");
    public MobileUploadTicket Begin(string session, string device, string rawName, int size)
    {
        lock (sync)
        {
            Sweep(); var component = rawName.Replace('\\', '/').Split('/').LastOrDefault() ?? "";
            var name = string.Concat(component.EnumerateRunes().Where(r => Rune.GetUnicodeCategory(r) is not (UnicodeCategory.Control or UnicodeCategory.Format)).Select(r => r.ToString())).TrimStart('.').Trim();
            name = string.Concat(name.EnumerateRunes().Take(120).Select(r => r.ToString()));
            if (name.Length == 0 || name.Length > 180 || size <= 0) throw new MobileRequestException(400, "Invalid attachment name or size.");
            if (size > AttachmentSupport.MaximumFileBytes) throw new MobileRequestException(413, "Attachment exceeds 5 MiB.");
            if (uploads.Count >= 64 || uploads.Values.Count(u => u.Session == session) >= 16) throw new MobileRequestException(429, "Too many open uploads.");
            var id = Wire.Id();
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None, Options = FileOptions.DeleteOnClose };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            var stream = new FileStream(Path.Combine(directory, id + ".part"), options);
            uploads.Add(id, new(id, session, device, name, size, stream)); return new(1, id, ChunkSize);
        }
    }
    public int Append(string id, string device, int index, byte[] data)
    {
        lock (sync)
        {
            Sweep(); var u = Owned(id, device);
            if (u.Completed || u.Claim is not null || index != u.Next) throw new MobileRequestException(409, "Chunks must arrive once, in order.");
            if (data.Length != Math.Min(ChunkSize, u.Size - u.Received) || data.Length == 0) throw new MobileRequestException(400, "Chunk size does not match declaration.");
            u.File.Position = u.Received; u.File.Write(data); u.Received += data.Length; u.Next++; u.Updated = DateTimeOffset.UtcNow; return u.Received;
        }
    }
    public MobileUploadAttachment Complete(string id, string device)
    {
        lock (sync)
        {
            Sweep(); var u = Owned(id, device);
            if (u.Completed) throw new MobileRequestException(409, "Upload already completed.");
            if (u.Received != u.Size) throw new MobileRequestException(400, "Upload is incomplete.");
            u.Completed = true; u.Updated = DateTimeOffset.UtcNow; return new(id, u.Name, u.Size);
        }
    }
    public MobileUploadClaim Claim(IReadOnlyList<string> ids, string session, string device)
    {
        lock (sync)
        {
            Sweep();
            if (ids.Count > AttachmentSupport.MaximumCount || ids.Distinct().Count() != ids.Count) throw new MobileRequestException(400, "Invalid attachment list.");
            var selected = ids.Select(id => Owned(id, device)).ToArray();
            if (selected.Any(u => u.Session != session || !u.Completed || u.Claim is not null)) throw new MobileRequestException(400, "Attachment is unavailable for this session.");
            if (selected.Sum(u => u.Size) > AttachmentSupport.MaximumTotalBytes) throw new MobileRequestException(413, "Attachments exceed 8 MiB.");
            var files = selected.Select(u => { u.File.Position = 0; var bytes = new byte[u.Size]; u.File.ReadExactly(bytes); return AttachmentSupport.Make(u.Name, bytes); }).ToArray();
            var claim = Wire.Id(); foreach (var u in selected) u.Claim = claim; return new(claim, ids, files);
        }
    }
    public void Finish(MobileUploadClaim claim, bool accepted)
    { lock (sync) foreach (var id in claim.UploadIds) if (uploads.TryGetValue(id, out var u) && u.Claim == claim.Id) { if (accepted) Remove(id); else u.Claim = null; } }
    public void Cancel(string id, string device) { lock (sync) { var u = Owned(id, device); if (u.Claim is not null) throw new MobileRequestException(409, "Upload is being submitted."); Remove(id); } }
    public void DiscardSession(string session) { lock (sync) foreach (var u in uploads.Values.Where(u => u.Session == session).ToArray()) Remove(u.Id); }
    public void Clear() { lock (sync) foreach (var id in uploads.Keys.ToArray()) Remove(id); }
    public void Dispose() => Clear();
}
