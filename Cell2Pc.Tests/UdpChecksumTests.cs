using System.Buffers.Binary;

namespace Cell2Pc.Tests;

/// <summary>
/// The UDP checksum on synthesised replies.
///
/// Leaving it zero is legal over IPv4 and most stacks accept it, so ordinary
/// traffic works and the omission looks harmless. QUIC implementations commonly
/// reject datagrams without one, so HTTP/3 sites break while everything else is
/// fine - which presents as a site that will not finish loading rather than as
/// a network fault.
/// </summary>
public class UdpChecksumTests
{
    /// <summary>
    /// The standard one's-complement sum over a packet plus its pseudo-header.
    /// A correct checksum makes the whole thing sum to zero, which is the
    /// property a receiver actually checks.
    /// </summary>
    private static ushort Verify(ReadOnlySpan<byte> packet, int udpOffset, int udpLength)
    {
        uint sum = 0;
        // Pseudo-header: source and destination addresses from the IP header.
        for (int i = 12; i < 20; i += 2) sum += BinaryPrimitives.ReadUInt16BigEndian(packet[i..]);
        sum += 17;                    // UDP
        sum += (uint)udpLength;

        var udp = packet.Slice(udpOffset, udpLength);
        for (int i = 0; i + 1 < udpLength; i += 2) sum += BinaryPrimitives.ReadUInt16BigEndian(udp[i..]);
        if ((udpLength & 1) != 0) sum += (uint)(udp[udpLength - 1] << 8);

        while (sum >> 16 != 0) sum = (sum & 0xFFFF) + (sum >> 16);
        return (ushort)sum;
    }

    private static byte[] BuildReply(byte[] fromIp, byte[] toIp, int fromPort, int toPort, byte[] data)
    {
        // Mirrors PacketPump.BuildUdpPacket so the checksum rule can be tested
        // without exposing the pump's internals.
        int udpLength = 8 + data.Length;
        var buf = new byte[20 + udpLength];

        buf[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2), (ushort)buf.Length);
        buf[8] = 64;
        buf[9] = 17;
        fromIp.CopyTo(buf, 12);
        toIp.CopyTo(buf, 16);

        var udp = buf.AsSpan(20);
        BinaryPrimitives.WriteUInt16BigEndian(udp, (ushort)fromPort);
        BinaryPrimitives.WriteUInt16BigEndian(udp[2..], (ushort)toPort);
        BinaryPrimitives.WriteUInt16BigEndian(udp[4..], (ushort)udpLength);
        data.CopyTo(udp[8..]);

        uint sum = 0;
        for (int i = 0; i < 4; i += 2) sum += BinaryPrimitives.ReadUInt16BigEndian(fromIp.AsSpan(i));
        for (int i = 0; i < 4; i += 2) sum += BinaryPrimitives.ReadUInt16BigEndian(toIp.AsSpan(i));
        sum += 17;
        sum += (uint)udpLength;
        for (int i = 0; i + 1 < udpLength; i += 2) sum += BinaryPrimitives.ReadUInt16BigEndian(udp[i..]);
        if ((udpLength & 1) != 0) sum += (uint)(udp[udpLength - 1] << 8);
        while (sum >> 16 != 0) sum = (sum & 0xFFFF) + (sum >> 16);
        ushort value = (ushort)~sum;
        BinaryPrimitives.WriteUInt16BigEndian(udp[6..], value == 0 ? (ushort)0xFFFF : value);

        return buf;
    }

    [Fact]
    public void A_reply_carries_a_checksum()
    {
        // Zero means "not computed", which QUIC stacks reject.
        var packet = BuildReply(new byte[] { 8, 8, 8, 8 }, new byte[] { 10, 87, 0, 2 },
            53, 51000, new byte[] { 1, 2, 3, 4 });

        ushort checksum = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(26));
        Assert.NotEqual(0, checksum);
    }

    [Fact]
    public void The_checksum_verifies()
    {
        var packet = BuildReply(new byte[] { 8, 8, 8, 8 }, new byte[] { 10, 87, 0, 2 },
            53, 51000, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });

        Assert.Equal(0xFFFF, Verify(packet, 20, packet.Length - 20));
    }

    [Fact]
    public void An_odd_length_payload_checksums_correctly()
    {
        // The trailing byte needs padding, and getting that wrong produces a
        // checksum that fails only for odd-sized datagrams.
        var packet = BuildReply(new byte[] { 1, 1, 1, 1 }, new byte[] { 10, 87, 0, 2 },
            443, 51000, new byte[] { 1, 2, 3, 4, 5 });

        Assert.Equal(0xFFFF, Verify(packet, 20, packet.Length - 20));
    }

    [Fact]
    public void An_empty_payload_checksums_correctly()
    {
        var packet = BuildReply(new byte[] { 1, 1, 1, 1 }, new byte[] { 10, 87, 0, 2 },
            443, 51000, Array.Empty<byte>());

        Assert.Equal(0xFFFF, Verify(packet, 20, packet.Length - 20));
    }
}
