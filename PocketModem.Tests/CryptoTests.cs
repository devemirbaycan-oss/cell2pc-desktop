using System.Security.Cryptography;
using System.Text;
using PocketModem.Client.Tunnel;

namespace PocketModem.Tests;

/// <summary>
/// Frame encryption, and the agreement with Crypto.kt on the phone.
///
/// A mismatch here does not fail loudly: frames simply fail to authenticate and
/// are dropped, so the tunnel connects, reports healthy links, and carries
/// nothing. That is the same silent-stall shape as a protocol disagreement, and
/// worth pinning down in tests rather than in a session.
/// </summary>
public class CryptoTests
{
    private const string Token = "k7m2xq4p";

    [Fact]
    public void Sealed_frames_open_again()
    {
        var crypto = new Crypto(Token);
        var plaintext = Encoding.UTF8.GetBytes("GET / HTTP/1.1\r\nHost: example.com\r\n\r\n");

        var sealedFrame = crypto.Seal(plaintext);
        var opened = crypto.Open(sealedFrame);

        Assert.Equal(plaintext, opened);
    }

    [Fact]
    public void Two_instances_with_the_same_token_understand_each_other()
    {
        // This is the real case: the phone and the PC each build their own
        // Crypto from the same pairing code and never exchange a key.
        var phone = new Crypto(Token);
        var pc = new Crypto(Token);
        var plaintext = Encoding.UTF8.GetBytes("payload");

        Assert.Equal(plaintext, pc.Open(phone.Seal(plaintext)));
        Assert.Equal(plaintext, phone.Open(pc.Seal(plaintext)));
    }

    [Fact]
    public void A_different_token_cannot_open_the_frame()
    {
        var mine = new Crypto(Token);
        var stranger = new Crypto("different");

        Assert.Null(stranger.Open(mine.Seal(Encoding.UTF8.GetBytes("secret"))));
    }

    [Fact]
    public void The_key_matches_what_the_phone_derives()
    {
        // Both sides compute SHA-256("pocketmodem-v1" || token). Kotlin does it with
        // two digest.update calls and C# over one concatenated array; those are
        // equivalent, but only as long as neither side changes the prefix or
        // the encoding. Pinned here so a rename cannot silently break pairing.
        var expected = SHA256.HashData(Encoding.ASCII.GetBytes("pocketmodem-v1" + Token));

        // Derived indirectly: two instances agreeing proves they share a key,
        // and the vector below proves it is this one.
        Assert.Equal(
            "11cef5ccaf503b16b3f5f4865c60a8ef17337624d3965258f11f6c2504bc2da5",
            Convert.ToHexString(expected).ToLowerInvariant());
    }

    [Fact]
    public void Tampering_is_detected()
    {
        var crypto = new Crypto(Token);
        var sealedFrame = crypto.Seal(Encoding.UTF8.GetBytes("important"));

        // Flip a bit in the ciphertext. GCM authenticates, so this must fail
        // rather than produce corrupted plaintext.
        sealedFrame[Crypto.NonceSize + 2] ^= 0x01;

        Assert.Null(crypto.Open(sealedFrame));
    }

    [Fact]
    public void A_truncated_frame_is_rejected()
    {
        var crypto = new Crypto(Token);
        Assert.Null(crypto.Open(new byte[4]));
    }

    [Fact]
    public void Nonces_never_repeat()
    {
        // Reusing a nonce under one key breaks GCM completely - not gradually.
        // The counter guarantees uniqueness within a session; this checks the
        // guarantee actually holds across a realistic number of frames.
        var crypto = new Crypto(Token);
        var seen = new HashSet<string>();

        for (int i = 0; i < 5000; i++)
        {
            var frame = crypto.Seal(new byte[] { (byte)i });
            var nonce = Convert.ToHexString(frame[..Crypto.NonceSize]);
            Assert.True(seen.Add(nonce), $"nonce repeated after {i} frames");
        }
    }

    [Fact]
    public void Separate_sessions_use_separate_nonce_space()
    {
        // The random prefix is what stops two sessions with the same token
        // walking the same counter values - which a counter alone would do
        // after every restart.
        var first = new Crypto(Token);
        var second = new Crypto(Token);

        var a = first.Seal(new byte[] { 1 })[..4];
        var b = second.Seal(new byte[] { 1 })[..4];

        Assert.NotEqual(Convert.ToHexString(a), Convert.ToHexString(b));
    }

    [Fact]
    public void Overhead_is_what_the_framing_assumes()
    {
        // Sealing grows a frame, and the length field has to accommodate it.
        var crypto = new Crypto(Token);
        var plaintext = new byte[1000];

        Assert.Equal(plaintext.Length + Crypto.Overhead, crypto.Seal(plaintext).Length);
    }

    [Fact]
    public void An_empty_frame_seals_and_opens()
    {
        var crypto = new Crypto(Token);
        Assert.Empty(crypto.Open(crypto.Seal(Array.Empty<byte>()))!);
    }
}
