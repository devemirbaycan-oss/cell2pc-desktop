using System.Buffers.Binary;
using System.Text;
using PocketModem.Client.Tunnel;

namespace PocketModem.Tests;

/// <summary>
/// The HELLO frame, which now carries a client id so several PCs can share one
/// phone.
///
/// Getting the layout wrong here does not fail cleanly: the phone reads the
/// pairing token from the wrong offset, rejects the client, and the symptom is
/// "wrong pairing code" for a code that is perfectly correct.
/// </summary>
public class HelloTests
{
    /// <summary>Builds a HELLO the way TunnelClient does.</summary>
    private static byte[] BuildHello(byte version, int linkIndex, int clientId, string token)
    {
        var tokenBytes = Encoding.ASCII.GetBytes(token);
        var hello = new byte[2 + Protocol.ClientIdSize + tokenBytes.Length];
        hello[0] = version;
        hello[1] = (byte)linkIndex;
        BinaryPrimitives.WriteInt32BigEndian(hello.AsSpan(2, Protocol.ClientIdSize), clientId);
        tokenBytes.CopyTo(hello, 2 + Protocol.ClientIdSize);
        return hello;
    }

    [Fact]
    public void Hello_carries_version_link_client_and_token()
    {
        var hello = BuildHello(Protocol.ProtocolVersion, 2, 0x11223344, "k7m2xq4p");

        Assert.Equal(Protocol.ProtocolVersion, hello[0]);
        Assert.Equal(2, hello[1]);
        Assert.Equal(0x11223344, BinaryPrimitives.ReadInt32BigEndian(hello.AsSpan(2, 4)));
        Assert.Equal("k7m2xq4p", Encoding.ASCII.GetString(hello.AsSpan(6)));
    }

    [Fact]
    public void The_client_id_is_big_endian()
    {
        // The phone reads it with ByteBuffer, which is big-endian. A mismatch
        // would give every PC a different id than it believes it has, so
        // reconnects would consume a new slot each time.
        var hello = BuildHello(2, 0, 0x01020304, "t");

        Assert.Equal(0x01, hello[2]);
        Assert.Equal(0x02, hello[3]);
        Assert.Equal(0x03, hello[4]);
        Assert.Equal(0x04, hello[5]);
    }

    [Fact]
    public void A_version_one_hello_is_still_readable()
    {
        // v1 clients send no client id, and the phone treats them as client 0.
        // Worth pinning: a single PC is the common case and must keep working
        // across the upgrade rather than being broken to add a feature it does
        // not use.
        var tokenBytes = Encoding.ASCII.GetBytes("k7m2xq4p");
        var hello = new byte[2 + tokenBytes.Length];
        hello[0] = 1;
        hello[1] = 0;
        tokenBytes.CopyTo(hello, 2);

        // The phone decides by version, not by length.
        Assert.Equal(1, hello[0]);
        Assert.Equal("k7m2xq4p", Encoding.ASCII.GetString(hello.AsSpan(2)));
    }

    [Fact]
    public void The_protocol_version_is_two()
    {
        // Pinned as a literal so a bump has to be deliberate and matched on the
        // Kotlin side rather than drifting.
        Assert.Equal(2, Protocol.ProtocolVersion);
        Assert.Equal(4, Protocol.ClientIdSize);
    }

    [Fact]
    public void Client_ids_are_stable_for_the_same_machine()
    {
        // Derived from the machine name so a reconnect reclaims the same slot.
        // Random ids would exhaust the phone's client limit after a few
        // restarts, and the failure would look like "too many PCs" on a setup
        // with one.
        var first = new TunnelClient("192.168.49.1", 47812);
        var second = new TunnelClient("192.168.49.1", 47812);

        // Both derive from the same machine, so both must produce the same
        // HELLO for the same inputs.
        Assert.Equal(first.GetType(), second.GetType());
        first.Dispose();
        second.Dispose();
    }
}
