using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using BcChaCha = Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305;

namespace MightyClaude.Core;

/// The existing mobile relay v1 cipher. Cryptographic primitives come from
/// Bouncy Castle and .NET; only the established nonce/wire framing lives here.
public sealed class RelayCipher : IDisposable
{
    private readonly byte[] key;
    private readonly byte sendDirection, receiveDirection;
    private readonly object sync = new();
    private bool disposed;
    private ulong sent;
    private long received = -1;
    public RelayCipher(byte[] secret, byte[] peer, byte[] clientNonce, byte[] serverNonce, bool host)
    {
        if (secret.Length != 32 || peer.Length != 32 || clientNonce.Length != 16 || serverNonce.Length != 16) throw new CryptographicException("Invalid relay handshake lengths.");
        var shared = new byte[32];
        try
        {
            new X25519PrivateKeyParameters(secret).GenerateSecret(new X25519PublicKeyParameters(peer), shared, 0);
            if (CryptographicOperations.FixedTimeEquals(shared, new byte[32])) throw new CryptographicException("Invalid relay agreement.");
            key = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, clientNonce.Concat(serverNonce).ToArray(), Encoding.UTF8.GetBytes("mightyclaude-relay-v1"));
        }
        finally { CryptographicOperations.ZeroMemory(shared); }
        sendDirection = host ? (byte)2 : (byte)1; receiveDirection = host ? (byte)1 : (byte)2;
    }
    public static byte[] PublicKey(byte[] secret) => new X25519PrivateKeyParameters(secret).GeneratePublicKey().GetEncoded();
    public byte[] Seal(byte[] plaintext)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (plaintext.Length > 1024 * 1024 - 28) throw new CryptographicException("Relay payload exceeds limit.");
            if (sent >= long.MaxValue) throw new CryptographicException("Relay counter exhausted.");
            var nonce = new byte[12]; nonce[0] = sendDirection; BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), sent++);
            var cipher = new BcChaCha(); cipher.Init(true, new AeadParameters(new KeyParameter(key), 128, nonce));
            var output = new byte[12 + cipher.GetOutputSize(plaintext.Length)]; nonce.CopyTo(output, 0);
            var used = cipher.ProcessBytes(plaintext, 0, plaintext.Length, output, 12); cipher.DoFinal(output, 12 + used); return output;
        }
    }
    public byte[] Open(byte[] frame)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (frame.Length > 1024 * 1024 || frame.Length < 28 || frame[0] != receiveDirection || frame[1] != 0 || frame[2] != 0 || frame[3] != 0) throw new CryptographicException("Invalid relay frame.");
            var counter = BinaryPrimitives.ReadUInt64BigEndian(frame.AsSpan(4, 8));
            if (counter > long.MaxValue || (long)counter <= received) throw new CryptographicException("Replayed relay frame.");
            var cipher = new BcChaCha(); cipher.Init(false, new AeadParameters(new KeyParameter(key), 128, frame[..12]));
            var result = new byte[cipher.GetOutputSize(frame.Length - 12)];
            var used = cipher.ProcessBytes(frame, 12, frame.Length - 12, result, 0); cipher.DoFinal(result, used);
            received = (long)counter; return result;
        }
    }
    public void Dispose() { lock (sync) { disposed = true; CryptographicOperations.ZeroMemory(key); } }
}
