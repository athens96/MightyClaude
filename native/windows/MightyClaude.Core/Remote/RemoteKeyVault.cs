using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace MightyClaude.Core;

/// <summary>Where the connection keys of saved remote connections live: never in workspace state or remote-connections.json.</summary>
public interface IRemoteKeyVault
{
    /// <summary>The key saved for this connection, or null when there is none or it cannot be read back (a damaged or foreign file).</summary>
    string? Load(string id);
    /// <summary>False when the key is not of the protocol's shape or could not be kept beyond this launch.</summary>
    bool Save(string id, string key);
    void Delete(string id);
}

/// <summary>Keys for this launch only: tests, and the fallback when DPAPI refuses.</summary>
public sealed class MemoryRemoteKeyVault : IRemoteKeyVault
{
    private readonly ConcurrentDictionary<string, string> keys = new(StringComparer.Ordinal);
    public string? Load(string id) => keys.TryGetValue(id, out var key) ? key : null;
    public bool Save(string id, string key) { if (!RemoteWorkspaceValidation.Key(key)) return false; keys[id] = key; return true; }
    public void Delete(string id) => keys.TryRemove(id, out _);
    /// <summary>The type name only: a vault that reaches a log or an exception message never carries a key.</summary>
    public override string ToString() => nameof(MemoryRemoteKeyVault);
}

/// <summary>
/// One file per connection under &lt;profile&gt;\remote-keys, encrypted with DPAPI for the current Windows user (CryptProtectData
/// without CRYPTPROTECT_LOCAL_MACHINE: what ProtectedData.Protect(…, DataProtectionScope.CurrentUser) calls, without adding the
/// package). Only this user on this PC can read a key back. The optional entropy is this app's label and the connection id, so a
/// blob another program protected for this user, or one copied from another connection's file, does not decrypt here. The folder
/// is user-only (protected ACL, this user as owner) and each file is written atomically. A file that does not decrypt to a key of
/// the protocol's shape reads as no key and stays in place: DPAPI can refuse for a passing reason, and saving a new key replaces it.
/// A key is read on a connection's first use, never at launch.
/// </summary>
public sealed class DpapiRemoteKeyVault(string directory) : IRemoteKeyVault
{
    public const string FolderName = "remote-keys";
    /// <summary>Larger than any blob DPAPI makes for a 128-character key; a bigger file is not ours.</summary>
    private const int MaximumBlobBytes = 16_384;
    private const string Purpose = "MightyClaude remote key v2";

    private string Folder => Path.Combine(directory, FolderName);
    private string FileFor(string id) => Path.Combine(Folder, FileName(id));

    /// <summary>sha256(id) in lower-case hex: the file name says nothing about the connection.</summary>
    internal static string FileName(string id) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id))) + ".bin";

    /// <summary>The DPAPI entropy for one connection: the app's purpose label, a NUL, and the id.</summary>
    internal static byte[] Entropy(string id) => Encoding.UTF8.GetBytes(Purpose + "\0" + id);

    public string? Load(string id)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var blob = ReadBounded(FileFor(id));
            if (blob is null) return null;
            var plain = Transform(blob, Entropy(id), decrypt: true);
            try
            {
                var key = new UTF8Encoding(false, true).GetString(plain);
                return RemoteWorkspaceValidation.Key(key) ? key : null;
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (Exception ex) when (Refused(ex)) { return null; }
    }

    public bool Save(string id, string key)
    {
        if (!OperatingSystem.IsWindows() || !RemoteWorkspaceValidation.Key(key)) return false;
        try
        {
            PrepareFolder();
            var blob = Transform(Encoding.UTF8.GetBytes(key), Entropy(id), decrypt: false);
            // Off the caller's synchronization context: StateStore awaits without ConfigureAwait, and Save may be called from the UI thread.
            Task.Run(() => StateStore.AtomicWriteAsync(FileFor(id), blob)).GetAwaiter().GetResult();
            return true;
        }
        catch (Exception ex) when (Refused(ex)) { return false; }
    }

    public void Delete(string id) { try { File.Delete(FileFor(id)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }

    /// <summary>The type name only: the vault holds no key, and its folder is not worth a log line.</summary>
    public override string ToString() => nameof(DpapiRemoteKeyVault);

    /// <summary>Why a key is not read or kept. None of these messages carries the key.</summary>
    private static bool Refused(Exception ex) => ex is IOException or UnauthorizedAccessException or CryptographicException or Win32Exception
        or ArgumentException or DllNotFoundException or EntryPointNotFoundException or PrivilegeNotHeldException or InvalidOperationException;

    private static byte[]? ReadBounded(string path)
    {
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buffer = new byte[MaximumBlobBytes + 1]; var length = 0;
        while (length < buffer.Length && stream.Read(buffer, length, buffer.Length - length) is var read and > 0) length += read;
        return length is 0 or > MaximumBlobBytes ? null : buffer[..length];
    }

    /// <summary>The folder is a plain folder this user owns, and its access list is put back to this user alone before every write.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void PrepareFolder()
    {
        Directory.CreateDirectory(directory);
        var info = new DirectoryInfo(Folder);
        var user = WindowsIdentity.GetCurrent().User ?? throw new UnauthorizedAccessException("no user identity");
        if (!info.Exists) info.Create(UserOnly(user));
        info.Refresh();
        if (!info.Exists || info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("the key folder is not a plain folder");
        if (info.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || owner != user) throw new UnauthorizedAccessException("the key folder belongs to another user");
        info.SetAccessControl(UserOnly(user));
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static DirectorySecurity UserOnly(SecurityIdentifier user)
    {
        var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.SetOwner(user);
        return security;
    }

    /// <summary>CryptProtectData / CryptUnprotectData for the current user, never with UI. Every unmanaged copy is wiped before it is freed, and so is <paramref name="value"/>.</summary>
    private static byte[] Transform(byte[] value, byte[] entropy, bool decrypt)
    {
        if (value.Length == 0) throw new CryptographicException("DPAPI input is empty");
        var pointer = Marshal.AllocHGlobal(value.Length); var salt = Marshal.AllocHGlobal(entropy.Length); Blob result = default;
        try
        {
            Marshal.Copy(value, 0, pointer, value.Length); Marshal.Copy(entropy, 0, salt, entropy.Length);
            var input = new Blob { Size = value.Length, Data = pointer }; var extra = new Blob { Size = entropy.Length, Data = salt };
            var ok = decrypt ? CryptUnprotectData(ref input, 0, ref extra, 0, 0, UiForbidden, out result) : CryptProtectData(ref input, Purpose, ref extra, 0, 0, UiForbidden, out result);
            if (!ok) throw new Win32Exception();
            if (result.Size is < 1 or > MaximumBlobBytes) throw new CryptographicException("DPAPI output size");
            var output = new byte[result.Size]; Marshal.Copy(result.Data, output, 0, output.Length); return output;
        }
        finally
        {
            for (var i = 0; i < value.Length; i++) Marshal.WriteByte(pointer, i, 0);
            Marshal.FreeHGlobal(pointer); Marshal.FreeHGlobal(salt);
            if (result.Data != 0) { for (var i = 0; i < result.Size; i++) Marshal.WriteByte(result.Data, i, 0); LocalFree(result.Data); }
            CryptographicOperations.ZeroMemory(value);
        }
    }

    private const uint UiForbidden = 1; // CRYPTPROTECT_UI_FORBIDDEN
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public nint Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptProtectData(ref Blob input, string description, ref Blob entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptUnprotectData(ref Blob input, nint description, ref Blob entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
}
