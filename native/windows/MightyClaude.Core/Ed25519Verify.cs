using System.Numerics;
using System.Security.Cryptography;

namespace MightyClaude.Core;

/// Verify-only Ed25519 (RFC 8032, PureEdDSA over edwards25519).
///
/// Why this file exists: .NET 10 exposes no Ed25519 primitive on any of the
/// three targets this app is built for (Windows x64, Windows arm64, macOS) —
/// `System.Security.Cryptography` ships ECDsa/ECDiffieHellman and the new
/// post-quantum types, but nothing on curve25519 — and the constraint on this
/// work forbids pulling in a package the Mac-side verification cannot build.
/// So the client carries a small verify-only implementation, proven against the
/// RFC 8032 section 7.1 test vectors by "app update ed25519 …" in Core.Tests.
/// The reason and the alternatives are written down in docs/windows-app-update.md.
///
/// Signing and key generation are deliberately absent: the client never signs.
public static class Ed25519Verify
{
    public const int PublicKeyBytes = 32;
    public const int SignatureBytes = 64;

    private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;
    private static readonly BigInteger GroupOrder =
        BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493");
    private static readonly BigInteger D =
        Modulo(BigInteger.MinusOne * 121665 * Inverse(121666));
    private static readonly BigInteger SqrtMinusOne = BigInteger.ModPow(2, (P - 1) / 4, P);

    /// Extended homogeneous coordinates (X : Y : Z : T), X/Z and Y/Z affine.
    private readonly record struct Point(BigInteger X, BigInteger Y, BigInteger Z, BigInteger T);

    private static readonly Point Identity = new(BigInteger.Zero, BigInteger.One, BigInteger.One, BigInteger.Zero);
    private static readonly Point Base = MakeBase();

    /// True only when <paramref name="signature"/> is a valid Ed25519 signature
    /// of <paramref name="message"/> under <paramref name="publicKey"/>. Every
    /// malformed input answers false rather than throwing, so a caller cannot
    /// mistake a decoding failure for anything but a refusal.
    public static bool Verify(ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature, ReadOnlySpan<byte> publicKey)
    {
        if (signature.Length != SignatureBytes || publicKey.Length != PublicKeyBytes) return false;

        var s = LittleEndian(signature[32..]);
        // RFC 8032 §5.1.7: a signature whose S is not reduced is rejected, so a
        // valid signature has exactly one encoding.
        if (s >= GroupOrder) return false;

        if (!TryDecode(signature[..32], out var r)) return false;
        if (!TryDecode(publicKey, out var a)) return false;

        var prefix = new byte[32 + PublicKeyBytes + message.Length];
        signature[..32].CopyTo(prefix);
        publicKey.CopyTo(prefix.AsSpan(32));
        message.CopyTo(prefix.AsSpan(32 + PublicKeyBytes));
        var k = Modulo(LittleEndian(SHA512.HashData(prefix)), GroupOrder);

        // [S]B == R + [k]A
        var left = Multiply(Base, s);
        var right = Add(r, Multiply(a, k));
        return SameAffinePoint(left, right);
    }

    private static bool SameAffinePoint(Point left, Point right) =>
        Modulo(left.X * right.Z - right.X * left.Z).IsZero &&
        Modulo(left.Y * right.Z - right.Y * left.Z).IsZero;

    private static Point MakeBase()
    {
        var y = Modulo(4 * Inverse(5));
        Span<byte> encoded = stackalloc byte[32];
        WriteLittleEndian(y, encoded);
        // The base point's x is the even root (sign bit 0).
        if (!TryDecode(encoded, out var point)) throw new InvalidOperationException("edwards25519 base point did not decode");
        return point;
    }

    /// RFC 8032 §5.1.3 point decompression. Returns false for any encoding that
    /// is not a point on the curve, including a non-canonical y.
    private static bool TryDecode(ReadOnlySpan<byte> encoded, out Point point)
    {
        point = Identity;
        if (encoded.Length != 32) return false;
        Span<byte> bytes = stackalloc byte[32];
        encoded.CopyTo(bytes);
        var sign = (bytes[31] & 0x80) != 0;
        bytes[31] &= 0x7f;
        var y = LittleEndian(bytes);
        if (y >= P) return false;

        var u = Modulo(y * y - 1);
        var v = Modulo(D * y * y + 1);
        if (v.IsZero) return false;
        // x = (u/v)^((p+3)/8), corrected by sqrt(-1) when needed.
        var v3 = Modulo(v * v * v);
        var v7 = Modulo(v3 * v3 * v);
        var x = Modulo(u * v3 * BigInteger.ModPow(Modulo(u * v7), (P - 5) / 8, P));
        var check = Modulo(v * x * x);
        if (check != Modulo(u))
        {
            if (check != Modulo(BigInteger.MinusOne * u)) return false;
            x = Modulo(x * SqrtMinusOne);
        }
        if (x.IsZero && sign) return false;
        // The encoded sign bit is the low bit of x; flip to the other root when it differs.
        if (!x.IsEven != sign) x = Modulo(P - x);

        point = new(x, y, BigInteger.One, Modulo(x * y));
        return true;
    }

    private static Point Add(Point one, Point two)
    {
        var a = Modulo((one.Y - one.X) * (two.Y - two.X));
        var b = Modulo((one.Y + one.X) * (two.Y + two.X));
        var c = Modulo(one.T * 2 * D * two.T);
        var d = Modulo(one.Z * 2 * two.Z);
        var e = b - a;
        var f = d - c;
        var g = d + c;
        var h = b + a;
        return new(Modulo(e * f), Modulo(g * h), Modulo(f * g), Modulo(e * h));
    }

    private static Point Multiply(Point point, BigInteger scalar)
    {
        var result = Identity;
        var addend = point;
        while (scalar > BigInteger.Zero)
        {
            if (!scalar.IsEven) result = Add(result, addend);
            addend = Add(addend, addend);
            scalar >>= 1;
        }
        return result;
    }

    private static BigInteger Inverse(BigInteger value) => BigInteger.ModPow(Modulo(value), P - 2, P);
    private static BigInteger Modulo(BigInteger value) => Modulo(value, P);
    private static BigInteger Modulo(BigInteger value, BigInteger modulus)
    {
        var remainder = value % modulus;
        return remainder.Sign < 0 ? remainder + modulus : remainder;
    }

    private static BigInteger LittleEndian(ReadOnlySpan<byte> bytes) => new(bytes, isUnsigned: true, isBigEndian: false);

    private static void WriteLittleEndian(BigInteger value, Span<byte> destination)
    {
        destination.Clear();
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: false);
        bytes.AsSpan(0, Math.Min(bytes.Length, destination.Length)).CopyTo(destination);
    }
}
