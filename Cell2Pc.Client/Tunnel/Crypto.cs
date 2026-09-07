using System.Security.Cryptography;
using System.Text;

namespace Cell2Pc.Client.Tunnel;

/// <summary>
/// Session encryption for tunnel frames (DESIGN.md §5.3).
///
/// Mirror of Crypto.kt on the phone; any change must be made in both at once.
///
/// WPA2 protects the radio hop, but the group passphrase is shared with every
/// device that has ever paired, so it is not a session secret. The pairing
/// token authorises a device; this makes the traffic unreadable to everything
/// else on the link.
///
/// AES-GCM with a key derived from the pairing token, which both sides already
/// know:
///
///     key = SHA-256("cell2pc-v1" || token)
///
/// Every frame is sealed independently with its own nonce, so a lost or
/// reordered frame cannot corrupt its neighbours - which matters because frames
/// travel over four parallel links and arrive interleaved.
/// </summary>
public sealed class Crypto
{
    private const string KeyInfo = "cell2pc-v1";
    public const int NonceSize = 12;
    public const int TagSize = 16;

    /// <summary>Bytes added to every frame: nonce + authentication tag.</summary>
    public const int Overhead = NonceSize + TagSize;

    private readonly byte[] _key;
    private readonly byte[] _sendPrefix = new byte[4];
    private long _sendCounter;
    private readonly Lock _nonceLock = new();

    public Crypto(string token)
    {
        _key = DeriveKey(token);
        RandomNumberGenerator.Fill(_sendPrefix);
    }

    private static byte[] DeriveKey(string token)
    {
        var input = Encoding.ASCII.GetBytes(KeyInfo + token);
        return SHA256.HashData(input);
    }

    /// <summary>Seal a frame as nonce(12) || ciphertext || tag(16).</summary>
    public byte[] Seal(ReadOnlySpan<byte> plaintext)
    {
        var nonce = NextNonce();
        var output = new byte[NonceSize + plaintext.Length + TagSize];

        nonce.CopyTo(output.AsSpan(0, NonceSize));
        var ciphertext = output.AsSpan(NonceSize, plaintext.Length);
        var tag = output.AsSpan(NonceSize + plaintext.Length, TagSize);

        using var gcm = new AesGcm(_key, TagSize);
        gcm.Encrypt(nonce, plaintext, ciphertext, tag);
        return output;
    }

    /// <summary>
    /// Open a sealed frame, or null if it fails to authenticate.
    ///
    /// Null means tampered, corrupted, or sealed under a different key - all
    /// reasons to drop the frame rather than guess at its contents.
    /// </summary>
    public byte[]? Open(ReadOnlySpan<byte> sealedFrame)
    {
        if (sealedFrame.Length < NonceSize + TagSize) return null;

        try
        {
            var nonce = sealedFrame[..NonceSize];
            int bodyLength = sealedFrame.Length - NonceSize - TagSize;
            var ciphertext = sealedFrame.Slice(NonceSize, bodyLength);
            var tag = sealedFrame.Slice(NonceSize + bodyLength, TagSize);

            var plaintext = new byte[bodyLength];
            using var gcm = new AesGcm(_key, TagSize);
            gcm.Decrypt(nonce, ciphertext, tag, plaintext);
            return plaintext;
        }
        catch (CryptographicException)
        {
            // Authentication failure. Quiet by design: a wrong key would
            // otherwise log once per frame.
            return null;
        }
    }

    /// <summary>
    /// 4-byte random prefix + 8-byte counter.
    ///
    /// Reusing a nonce under one key breaks GCM completely, so both halves
    /// matter: the prefix keeps two sessions with the same token in disjoint
    /// nonce space, and the counter guarantees no repeat within a session.
    /// </summary>
    private byte[] NextNonce()
    {
        var nonce = new byte[NonceSize];
        _sendPrefix.CopyTo(nonce, 0);

        long counter;
        lock (_nonceLock) { counter = _sendCounter++; }

        for (int i = 11; i >= 4; i--)
        {
            nonce[i] = (byte)(counter & 0xFF);
            counter >>= 8;
        }
        return nonce;
    }
}
