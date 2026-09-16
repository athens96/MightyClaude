using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace MightyClaude.Core;

public interface ISecretProtector
{
    byte[] Protect(string value);
    string Unprotect(byte[] value);
}

/// <summary>Current Windows user DPAPI, without UI or a plaintext fallback.</summary>
public sealed class WindowsSecretProtector : ISecretProtector
{
    public byte[] Protect(string value) => Transform(Encoding.UTF8.GetBytes(value), false);
    public string Unprotect(byte[] value) { var plain = Transform(value, true); try { return Encoding.UTF8.GetString(plain); } finally { CryptographicOperations.ZeroMemory(plain); } }
    private static byte[] Transform(byte[] value, bool decrypt)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows DPAPI가 필요합니다.");
        var pointer = Marshal.AllocHGlobal(value.Length); Blob result = default;
        try
        {
            Marshal.Copy(value, 0, pointer, value.Length); var input = new Blob { Size = value.Length, Data = pointer };
            var ok = decrypt ? CryptUnprotectData(ref input, 0, 0, 0, 0, 1, out result) : CryptProtectData(ref input, "MightyClaude remote key", 0, 0, 0, 1, out result);
            if (!ok) throw new Win32Exception();
            if (result.Size is < 1 or > 16384) throw new CryptographicException("암호화 데이터 크기가 올바르지 않습니다.");
            var output = new byte[result.Size]; Marshal.Copy(result.Data, output, 0, output.Length); return output;
        }
        finally
        {
            for (var i = 0; i < value.Length; i++) Marshal.WriteByte(pointer, i, 0);
            Marshal.FreeHGlobal(pointer); if (result.Data != 0) { for (var i = 0; i < result.Size; i++) Marshal.WriteByte(result.Data, i, 0); LocalFree(result.Data); }
            if (!decrypt) CryptographicOperations.ZeroMemory(value);
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public nint Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptProtectData(ref Blob input, string description, nint entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptUnprotectData(ref Blob input, nint description, nint entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
}
