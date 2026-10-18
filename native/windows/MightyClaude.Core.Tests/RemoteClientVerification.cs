using System.Text.Json;
using MightyClaude.Core;

/// <summary>The Windows client of remote workspaces (docs/remote-workspaces.md): saved connections, keys, addresses, transport, runs.</summary>
internal static partial class RemoteClientVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Reject(Func<Task> action, string message, Func<Exception, bool>? expected = null)
    {
        try { await action(); }
        catch (Exception ex) { if (expected is null || expected(ex)) return; throw new InvalidOperationException(message + " (threw " + ex.GetType().Name + ": " + ex.Message + ")"); }
        throw new InvalidOperationException(message);
    }
    internal static readonly string Key = new('k', 43);

    internal static async Task SavedConnectionsKeepNoKey()
    {
        var directory = Verification.Temp();
        try
        {
            await RemoteConnectionFile.WriteAsync(directory, [new("conn-1", "studio", "100.101.102.103:43137"), new("conn-2", "mac", "[fd7a:115c:a1e0::5]:43137")]);
            var text = await File.ReadAllTextAsync(Path.Combine(directory, RemoteConnectionFile.FileName));
            Check(text.Contains("\"version\":2", StringComparison.Ordinal) && !text.Contains("key", StringComparison.OrdinalIgnoreCase), "the file carries the version and no key: " + text);
            Check(RemoteConnectionFile.Read(directory).Select(c => c.Id).SequenceEqual(["conn-1", "conn-2"]), "read back in order");
            if (!OperatingSystem.IsWindows()) Check(File.GetUnixFileMode(Path.Combine(directory, RemoteConnectionFile.FileName)) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "the file is user-only");
            await File.WriteAllTextAsync(Path.Combine(directory, RemoteConnectionFile.FileName), "{\"version\":1,\"connections\":[{\"id\":\"old\",\"name\":\"x\",\"address\":\"100.1.2.3\"}]}");
            Check(RemoteConnectionFile.Read(directory).Count == 0, "a version 1 file (before 2026-09-28) is not read");
            await File.WriteAllTextAsync(Path.Combine(directory, RemoteConnectionFile.FileName), "{\"version\":2,\"connections\":[{\"id\":\"bad id\",\"name\":\"x\",\"address\":\"a\"},{\"id\":\"ok\",\"name\":\"y\",\"address\":\"b\"},{\"id\":\"ok\",\"name\":\"z\",\"address\":\"c\"}]}");
            Check(RemoteConnectionFile.Read(directory).Select(c => c.Name).SequenceEqual(["y"]), "bad and repeated ids are skipped");
            await File.WriteAllTextAsync(Path.Combine(directory, RemoteConnectionFile.FileName), "{\"version\":2,\"connections\":[null,{\"id\":\"no-address\",\"name\":\"x\"},{\"id\":\"ok\",\"name\":null,\"address\":\"b\",\"key\":\"" + Key + "\"}]}");
            var kept = RemoteConnectionFile.Read(directory);
            Check(kept.Count == 1 && kept[0] is { Id: "ok", Name: "", Address: "b" }, "a null entry or one without an address is skipped alone, and a stray member is dropped");
            await RemoteConnectionFile.WriteAsync(directory, kept);
            Check(!(await File.ReadAllTextAsync(Path.Combine(directory, RemoteConnectionFile.FileName))).Contains(Key, StringComparison.Ordinal), "a key a hand edit left in the file is gone after the next write");
            await File.WriteAllTextAsync(Path.Combine(directory, RemoteConnectionFile.FileName), "{\"version\":2,\"connections\":[{\"id\":\"ok\",\"name\":\"x\",\"address\":\"b\"}");
            Check(RemoteConnectionFile.Read(directory).Count == 0, "a damaged file reads as no connections");
            await File.WriteAllBytesAsync(Path.Combine(directory, RemoteConnectionFile.FileName), new byte[256 * 1024 + 1]);
            Check(RemoteConnectionFile.Read(directory).Count == 0, "an oversize file is not read");
            Check(RemoteConnectionFile.Read(Path.Combine(directory, "missing")).Count == 0, "no file, no connections");

            var name = new string('a', 119) + "\U0001F600";
            await RemoteConnectionFile.WriteAsync(directory, [new("emoji", name, "b"), .. Enumerable.Range(0, 20).Select(i => new RemoteSavedConnection("c" + i, "n", "b")), new("bad id", "x", "b")]);
            var back = RemoteConnectionFile.Read(directory);
            Check(back.Count == RemoteProtocol.MaximumConnections && back[0].Name == new string('a', 119), "a long name is cut outside a surrogate pair and reads back, at most 16 connections are kept: " + back.Count);
            Check(!(await File.ReadAllTextAsync(Path.Combine(directory, RemoteConnectionFile.FileName))).Contains("bad id", StringComparison.Ordinal), "an entry the next read would drop is never written");
        }
        finally { Directory.Delete(directory, true); }
    }

    internal static Task KeysStayInTheVault()
    {
        var directory = Verification.Temp();
        try
        {
            var memory = new MemoryRemoteKeyVault();
            Check(memory.Save("conn-1", Key) && memory.Load("conn-1") == Key, "the memory vault keeps a key");
            Check(!memory.ToString().Contains(Key, StringComparison.Ordinal), "the memory vault never prints its keys");
            memory.Delete("conn-1"); Check(memory.Load("conn-1") is null, "and forgets it");
            var dpapi = new DpapiRemoteKeyVault(directory);
            Check(DpapiRemoteKeyVault.FileName("conn:1") is { Length: 68 } hashed && hashed.EndsWith(".bin", StringComparison.Ordinal) && hashed[..64].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') && hashed != DpapiRemoteKeyVault.FileName("conn:2"), "a key file is named by the id's SHA-256 alone");
            Check(!DpapiRemoteKeyVault.Entropy("conn:1").AsSpan().SequenceEqual(DpapiRemoteKeyVault.Entropy("conn:2")), "each connection's blob is bound to its id");
            if (OperatingSystem.IsWindows())
            {
                Check(dpapi.Save("conn:1", Key), "DPAPI saves for this Windows user");
                var file = Directory.GetFiles(Path.Combine(directory, DpapiRemoteKeyVault.FolderName)).Single();
                Check(!File.ReadAllText(file).Contains(Key, StringComparison.Ordinal) && !Path.GetFileName(file).Contains("conn", StringComparison.Ordinal), "the file is ciphertext under a hashed name");
                Check(new DpapiRemoteKeyVault(directory).Load("conn:1") == Key, "a new vault reads it back");
                var foreign = Path.Combine(Path.GetDirectoryName(file)!, DpapiRemoteKeyVault.FileName("conn:2"));
                File.Copy(file, foreign); Check(dpapi.Load("conn:2") is null, "another connection's blob does not decrypt as this one's");
                File.Delete(foreign);
                File.WriteAllBytes(file, [1, 2, 3]); Check(dpapi.Load("conn:1") is null, "a damaged file reads as no key");
                File.WriteAllBytes(file, new byte[16_385]); Check(dpapi.Load("conn:1") is null, "an oversize file reads as no key");
                dpapi.Delete("conn:1"); Check(!File.Exists(file), "delete removes the file");
                Check(!dpapi.Save("conn:1", "short") && !File.Exists(file), "a key of the wrong shape is never written");
            }
            else Check(!dpapi.Save("conn-1", Key) && dpapi.Load("conn-1") is null && !Directory.Exists(Path.Combine(directory, "remote-keys")), "without DPAPI nothing is written and the key stays in memory");
            Check(!dpapi.ToString().Contains(directory, StringComparison.Ordinal), "the vault prints only its type");
            Check(!memory.Save("conn-1", "short"), "a key of the wrong shape is never saved");
        }
        finally { Directory.Delete(directory, true); }
        return Task.CompletedTask;
    }
}
