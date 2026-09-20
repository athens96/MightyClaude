using System.Numerics;
using System.Security.Cryptography;

// Test-only Ed25519 signer. The client never signs — signing exists here so the
// checks can produce a fixture-signed manifest from a key pair generated for the
// tests only, the way the release job signs latest.json with the real key.
// SignerAgreesWithTheVectors proves this fixture against RFC 8032 before any
// manifest check relies on it.
internal static class Ed25519Fixture
{
    private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;
    private static readonly BigInteger L =
        BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493");
    private static readonly BigInteger D = Mod(BigInteger.MinusOne * 121665 * BigInteger.ModPow(Mod(121666), P - 2, P));

    private readonly record struct Point(BigInteger X, BigInteger Y, BigInteger Z, BigInteger T);
    private static readonly Point Zero = new(BigInteger.Zero, BigInteger.One, BigInteger.One, BigInteger.Zero);
    private static readonly Point Base = MakeBase();

    /// The 32-byte public key for a 32-byte seed.
    internal static byte[] PublicKey(byte[] seed)
    {
        var scalar = Clamp(SHA512.HashData(seed).AsSpan(0, 32).ToArray());
        return Encode(Multiply(Base, scalar));
    }

    /// The 64-byte signature of a message under a 32-byte seed.
    internal static byte[] Sign(byte[] seed, ReadOnlySpan<byte> message)
    {
        var h = SHA512.HashData(seed);
        var scalar = Clamp(h.AsSpan(0, 32).ToArray());
        var publicKey = Encode(Multiply(Base, scalar));

        var prefix = new byte[32 + message.Length];
        h.AsSpan(32, 32).CopyTo(prefix);
        message.CopyTo(prefix.AsSpan(32));
        var r = Mod(Little(SHA512.HashData(prefix)), L);
        var rPoint = Encode(Multiply(Base, r));

        var challenge = new byte[64 + message.Length];
        rPoint.CopyTo(challenge, 0);
        publicKey.CopyTo(challenge, 32);
        message.CopyTo(challenge.AsSpan(64));
        var k = Mod(Little(SHA512.HashData(challenge)), L);

        var s = Mod(r + k * scalar, L);
        var signature = new byte[64];
        rPoint.CopyTo(signature, 0);
        WriteLittle(s, signature.AsSpan(32));
        return signature;
    }

    private static BigInteger Clamp(byte[] bytes)
    {
        bytes[0] &= 248;
        bytes[31] &= 127;
        bytes[31] |= 64;
        return Little(bytes);
    }

    private static Point MakeBase()
    {
        var y = Mod(4 * BigInteger.ModPow(5, P - 2, P));
        var x = RecoverX(y, false);
        return new(x, y, BigInteger.One, Mod(x * y));
    }

    private static BigInteger RecoverX(BigInteger y, bool odd)
    {
        var u = Mod(y * y - 1);
        var v = Mod(D * y * y + 1);
        var v3 = Mod(v * v * v);
        var v7 = Mod(v3 * v3 * v);
        var x = Mod(u * v3 * BigInteger.ModPow(Mod(u * v7), (P - 5) / 8, P));
        if (Mod(v * x * x) != Mod(u)) x = Mod(x * BigInteger.ModPow(2, (P - 1) / 4, P));
        if (!x.IsEven != odd) x = Mod(P - x);
        return x;
    }

    private static byte[] Encode(Point point)
    {
        var inverse = BigInteger.ModPow(point.Z, P - 2, P);
        var x = Mod(point.X * inverse);
        var y = Mod(point.Y * inverse);
        var bytes = new byte[32];
        WriteLittle(y, bytes);
        if (!x.IsEven) bytes[31] |= 0x80;
        return bytes;
    }

    private static Point Add(Point one, Point two)
    {
        var a = Mod((one.Y - one.X) * (two.Y - two.X));
        var b = Mod((one.Y + one.X) * (two.Y + two.X));
        var c = Mod(one.T * 2 * D * two.T);
        var d = Mod(one.Z * 2 * two.Z);
        var e = b - a;
        var f = d - c;
        var g = d + c;
        var h = b + a;
        return new(Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h));
    }

    private static Point Multiply(Point point, BigInteger scalar)
    {
        var result = Zero;
        var addend = point;
        while (scalar > BigInteger.Zero)
        {
            if (!scalar.IsEven) result = Add(result, addend);
            addend = Add(addend, addend);
            scalar >>= 1;
        }
        return result;
    }

    private static BigInteger Mod(BigInteger value) => Mod(value, P);
    private static BigInteger Mod(BigInteger value, BigInteger modulus)
    {
        var remainder = value % modulus;
        return remainder.Sign < 0 ? remainder + modulus : remainder;
    }

    private static BigInteger Little(ReadOnlySpan<byte> bytes) => new(bytes, isUnsigned: true, isBigEndian: false);

    private static void WriteLittle(BigInteger value, Span<byte> destination)
    {
        destination.Clear();
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: false);
        bytes.AsSpan(0, Math.Min(bytes.Length, destination.Length)).CopyTo(destination);
    }
}
