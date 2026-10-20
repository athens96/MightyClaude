using System.Text.Json;

namespace MightyClaude.Core;

/// <summary>One saved connection: id, name, address. Never its key (that is in the IRemoteKeyVault).</summary>
public sealed record RemoteSavedConnection(string Id, string Name, string Address);

/// <summary>
/// The data folder's remote-connections.json in the Mac's shape (M/Remote/RemoteClientService.swift SavedFile, version 2):
/// <c>{"version":2,"connections":[{"id","name","address"}]}</c>. It is local state, written atomically and user-only on every OS
/// StateStore writes to. A file of another version (the remote workspaces removed on 2026-09-28 wrote version 1, with keys), a
/// damaged or oversize one reads as no connections; an entry with a bad or repeated id, or an address that does not parse or repeats an
/// earlier entry's, is skipped alone.
/// </summary>
public static class RemoteConnectionFile
{
    public const string FileName = "remote-connections.json";
    public const int MaximumFileBytes = 256 * 1024, MaximumName = 120, MaximumAddress = 300;
    private sealed record SavedFile(int Version, IReadOnlyList<RemoteSavedConnection?>? Connections);

    public static IReadOnlyList<RemoteSavedConnection> Read(string directory) => TryRead(directory, out var connections) ? connections : [];

    /// <summary>
    /// Reads the file; false when it is there but cannot be read now (held open by another program, as an antivirus scan does, or
    /// refused), so the caller reads again later and never writes over connections it has not read. No file, or a damaged, oversize or
    /// other-version one, reads as none (true).
    /// </summary>
    public static bool TryRead(string directory, out IReadOnlyList<RemoteSavedConnection> connections) => TryRead(directory, out connections, out _);

    /// <summary><see cref="TryRead(string, out IReadOnlyList{RemoteSavedConnection})"/>, with what kept the file from being read.</summary>
    public static bool TryRead(string directory, out IReadOnlyList<RemoteSavedConnection> connections, out Exception? failure)
    {
        connections = []; failure = null;
        try
        {
            var path = Path.Combine(directory, FileName);
            if (!File.Exists(path) || new FileInfo(path).Length > MaximumFileBytes) return true;
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length > MaximumFileBytes) return true;
            var file = JsonSerializer.Deserialize<SavedFile>(bytes, Wire.Json);
            if (file is not { Version: RemoteProtocol.Version, Connections: { } saved }) return true;
            // As the Mac reads the file: its first 16 entries, then each is checked; the write caps what it keeps instead.
            connections = Usable(saved.Take(RemoteProtocol.MaximumConnections));
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failure = ex; return false; }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException) { return true; }
    }

    /// <summary>Writes what Read would keep, in order, so the file never holds an entry the next launch drops.</summary>
    public static Task WriteAsync(string directory, IEnumerable<RemoteSavedConnection> connections) =>
        StateStore.AtomicWriteAsync(Path.Combine(directory, FileName), JsonSerializer.SerializeToUtf8Bytes(new SavedFile(RemoteProtocol.Version, Usable(connections)), Wire.Json));

    /// <summary>
    /// As the Mac's loadSaved: an entry is kept when its id is good and new and its address parses (<see cref="RemoteAddress.Parse"/>)
    /// to one no earlier entry has; the address is kept in its shown form. At most 16 kept, in order.
    /// </summary>
    private static List<RemoteSavedConnection> Usable(IEnumerable<RemoteSavedConnection?> connections)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var addresses = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<RemoteSavedConnection>();
        foreach (var c in connections.OfType<RemoteSavedConnection>())
        {
            if (kept.Count == RemoteProtocol.MaximumConnections) break;
            if (!Wire.Identifier(c.Id) || ids.Contains(c.Id) || c.Address is not { Length: > 0 and <= MaximumAddress } || Address(c.Address) is not { } address || !addresses.Add(address)) continue;
            ids.Add(c.Id);
            kept.Add(new RemoteSavedConnection(c.Id, Name(c.Name), address));
        }
        return kept;
    }

    private static string? Address(string value)
    {
        try { return RemoteAddress.Parse(value).Display; }
        catch (ArgumentException) { return null; }
    }

    /// <summary>
    /// A connection's name as it is kept: control characters out, then at most 120 characters as the Mac counts them (its loadSaved
    /// <c>prefix(120)</c>), with no lone surrogate (one would make the whole file unreadable). Connect names a connection by this too,
    /// so it reads the same after a restart.
    /// </summary>
    public static string Name(string? value) => RemoteText.Cut(Wire.Clean(value), MaximumName);
}
