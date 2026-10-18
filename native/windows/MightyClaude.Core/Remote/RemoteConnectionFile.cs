using System.Text.Json;

namespace MightyClaude.Core;

/// <summary>One saved connection: id, name, address. Never its key (that is in the IRemoteKeyVault).</summary>
public sealed record RemoteSavedConnection(string Id, string Name, string Address);

/// <summary>
/// The data folder's remote-connections.json in the Mac's shape (M/Remote/RemoteClientService.swift SavedFile, version 2):
/// <c>{"version":2,"connections":[{"id","name","address"}]}</c>. It is local state, written atomically and user-only on every OS
/// StateStore writes to. A file of another version (the remote workspaces removed on 2026-09-28 wrote version 1, with keys), a
/// damaged or oversize one reads as no connections; an entry with a bad or repeated id or no address is skipped alone.
/// </summary>
public static class RemoteConnectionFile
{
    public const string FileName = "remote-connections.json";
    public const int MaximumFileBytes = 256 * 1024, MaximumName = 120, MaximumAddress = 300;
    private sealed record SavedFile(int Version, IReadOnlyList<RemoteSavedConnection?>? Connections);

    public static IReadOnlyList<RemoteSavedConnection> Read(string directory)
    {
        try
        {
            var path = Path.Combine(directory, FileName);
            if (!File.Exists(path) || new FileInfo(path).Length > MaximumFileBytes) return [];
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length > MaximumFileBytes) return [];
            var file = JsonSerializer.Deserialize<SavedFile>(bytes, Wire.Json);
            if (file is not { Version: RemoteProtocol.Version, Connections: { } connections }) return [];
            return Usable(connections);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException) { return []; }
    }

    /// <summary>Writes what Read would keep, in order, so the file never holds an entry the next launch drops.</summary>
    public static Task WriteAsync(string directory, IEnumerable<RemoteSavedConnection> connections) =>
        StateStore.AtomicWriteAsync(Path.Combine(directory, FileName), JsonSerializer.SerializeToUtf8Bytes(new SavedFile(RemoteProtocol.Version, Usable(connections)), Wire.Json));

    private static List<RemoteSavedConnection> Usable(IEnumerable<RemoteSavedConnection?> connections)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return connections.OfType<RemoteSavedConnection>()
            .Where(c => Wire.Identifier(c.Id) && c.Address is { Length: > 0 and <= MaximumAddress } && seen.Add(c.Id))
            .Take(RemoteProtocol.MaximumConnections)
            .Select(c => new RemoteSavedConnection(c.Id, Name(c.Name), c.Address)).ToList();
    }

    /// <summary>Control characters out and at most 120 UTF-16 units, never ending inside a surrogate pair (a lone one would make the whole file unreadable).</summary>
    private static string Name(string? value)
    {
        var clean = Wire.Clean(value, MaximumName);
        return clean.Length > 0 && char.IsHighSurrogate(clean[^1]) ? clean[..^1] : clean;
    }
}
