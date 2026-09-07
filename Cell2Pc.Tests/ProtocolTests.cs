using Cell2Pc.Client.Tunnel;

namespace Cell2Pc.Tests;

/// <summary>
/// The wire format, which has to agree byte-for-byte with Protocol.kt on the
/// phone. A disagreement here is not a crash but a stall: frames decode into
/// nonsense and the tunnel carries nothing while looking healthy, which is the
/// hardest kind of failure to diagnose from the outside.
/// </summary>
public class ProtocolTests
{
    [Fact]
    public void Header_round_trips()
    {
        var buffer = new byte[Protocol.HeaderSize];
        Protocol.WriteHeader(buffer, Protocol.TcpData, 0x02, 12345, 678);

        var (type, flags, streamId, length) = Protocol.ReadHeader(buffer);

        Assert.Equal(Protocol.TcpData, type);
        Assert.Equal(0x02, flags);
        Assert.Equal(12345, streamId);
        Assert.Equal(678, length);
    }

    [Fact]
    public void Header_is_big_endian()
    {
        // The Kotlin side reads these with ByteBuffer, which is big-endian by
        // default. Getting this wrong would swap every stream id.
        var buffer = new byte[Protocol.HeaderSize];
        Protocol.WriteHeader(buffer, Protocol.TcpOpen, 0, 0x01020304, 0x0506);

        Assert.Equal(0x01, buffer[2]);
        Assert.Equal(0x02, buffer[3]);
        Assert.Equal(0x03, buffer[4]);
        Assert.Equal(0x04, buffer[5]);
        Assert.Equal(0x05, buffer[6]);
        Assert.Equal(0x06, buffer[7]);
    }

    [Fact]
    public void Header_rejects_an_oversized_payload()
    {
        // The length field is 16 bits. Silently truncating would desynchronise
        // the stream for every frame after it.
        var buffer = new byte[Protocol.HeaderSize];
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Protocol.WriteHeader(buffer, Protocol.TcpData, 0, 1, Protocol.MaxPayload + 1));
    }

    [Fact]
    public void Open_round_trips_ipv4()
    {
        byte[] address = { 93, 184, 216, 34 };
        var payload = Protocol.EncodeOpen(address, 443);

        Assert.Equal(6, payload.Length);
        Assert.Equal(address, payload[..4]);
        Assert.Equal(443, (payload[4] << 8) | payload[5]);
    }

    [Fact]
    public void Open_round_trips_ipv6()
    {
        // The family is carried by length rather than a flag, so a 16-byte
        // address must produce an 18-byte payload for the phone to read it as
        // v6 (Protocol.kt decodeOpen switches on exactly this).
        var address = System.Net.IPAddress.Parse("2606:4700:4700::1111").GetAddressBytes();
        var payload = Protocol.EncodeOpen(address, 443);

        Assert.Equal(18, payload.Length);
        Assert.Equal(address, payload[..16]);
    }

    [Fact]
    public void Open_rejects_a_nonsense_address_length()
    {
        Assert.Throws<ArgumentException>(() => Protocol.EncodeOpen(new byte[7], 80));
    }

    [Fact]
    public void Datagram_round_trips_ipv4()
    {
        byte[] address = { 1, 1, 1, 1 };
        byte[] data = { 0xDE, 0xAD, 0xBE, 0xEF };

        var payload = Protocol.EncodeDatagram(54321, address, 53, data);
        var (srcPort, dstIp, dstPort, decoded) = Protocol.DecodeDatagram(payload);

        Assert.Equal(54321, srcPort);
        Assert.Equal(address, dstIp);
        Assert.Equal(53, dstPort);
        Assert.Equal(data, decoded);
    }

    [Fact]
    public void Datagram_round_trips_ipv6()
    {
        var address = System.Net.IPAddress.Parse("2001:4860:4860::8888").GetAddressBytes();
        byte[] data = { 1, 2, 3 };

        var payload = Protocol.EncodeDatagram(1234, address, 53, data);
        var (srcPort, dstIp, dstPort, decoded) = Protocol.DecodeDatagram(payload, ipv6: true);

        Assert.Equal(1234, srcPort);
        Assert.Equal(address, dstIp);
        Assert.Equal(53, dstPort);
        Assert.Equal(data, decoded);
    }

    [Fact]
    public void Datagram_family_cannot_be_inferred_from_length()
    {
        // Why UDP needs a flag where TCP_OPEN does not: the payload after the
        // address is variable, so a v4 datagram with 12 bytes of data is the
        // same total length as a v6 one with none.
        var v4 = Protocol.EncodeDatagram(1, new byte[4], 53, new byte[12]);
        var v6 = Protocol.EncodeDatagram(1, new byte[16], 53, Array.Empty<byte>());

        Assert.Equal(v4.Length, v6.Length);
    }

    [Fact]
    public void Datagram_carries_an_empty_payload()
    {
        // Zero-length UDP is legal and does occur; an off-by-one in the
        // header maths would surface here first.
        var payload = Protocol.EncodeDatagram(9999, new byte[] { 8, 8, 8, 8 }, 53,
            Array.Empty<byte>());
        var (_, _, _, data) = Protocol.DecodeDatagram(payload);

        Assert.Empty(data);
    }

    [Fact]
    public void Decoding_a_truncated_datagram_throws()
    {
        // Better to fail loudly than to read past the end and forward a
        // datagram assembled from whatever followed in memory.
        Assert.Throws<ArgumentException>(() => Protocol.DecodeDatagram(new byte[3]));
    }

    [Theory]
    [InlineData(Protocol.CloseReason.Normal)]
    [InlineData(Protocol.CloseReason.Refused)]
    [InlineData(Protocol.CloseReason.Timeout)]
    [InlineData(Protocol.CloseReason.Reset)]
    [InlineData(Protocol.CloseReason.Unreachable)]
    public void Every_close_reason_describes_itself(byte reason)
    {
        // These reach the user through the drop diagnostics, so an
        // unrecognised one would show as a bare number at the moment someone
        // is trying to work out what went wrong.
        var text = Protocol.CloseReason.Describe(reason);
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.DoesNotContain("reason", text);
    }
}
