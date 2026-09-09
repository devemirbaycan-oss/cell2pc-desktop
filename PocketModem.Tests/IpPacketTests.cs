using System.Buffers.Binary;
using PocketModem.Client.Net;

namespace PocketModem.Tests;

/// <summary>
/// Reading IP packets off the adapter.
///
/// Misreading a header does not throw - it produces a plausible-looking port
/// or address from the wrong offset, and the connection goes somewhere
/// unintended or nowhere at all. The v6 cases matter most, since almost every
/// field moved.
/// </summary>
public class IpPacketTests
{
    private static byte[] BuildIpv4Tcp(string src, string dst, int srcPort, int dstPort,
        byte[]? payload = null)
    {
        payload ??= Array.Empty<byte>();
        var packet = new byte[20 + 20 + payload.Length];

        packet[0] = 0x45;                                // version 4, 5-word header
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)packet.Length);
        packet[8] = 64;                                  // TTL
        packet[9] = 6;                                   // TCP
        System.Net.IPAddress.Parse(src).GetAddressBytes().CopyTo(packet, 12);
        System.Net.IPAddress.Parse(dst).GetAddressBytes().CopyTo(packet, 16);

        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(20), (ushort)srcPort);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22), (ushort)dstPort);
        packet[32] = 5 << 4;                             // data offset
        payload.CopyTo(packet, 40);

        return packet;
    }

    private static byte[] BuildIpv6Udp(string src, string dst, int srcPort, int dstPort,
        byte[]? payload = null)
    {
        payload ??= Array.Empty<byte>();
        var packet = new byte[40 + 8 + payload.Length];

        packet[0] = 0x60;                                // version 6
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), (ushort)(8 + payload.Length));
        packet[6] = 17;                                  // next header: UDP
        packet[7] = 64;                                  // hop limit
        System.Net.IPAddress.Parse(src).GetAddressBytes().CopyTo(packet, 8);
        System.Net.IPAddress.Parse(dst).GetAddressBytes().CopyTo(packet, 24);

        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(40), (ushort)srcPort);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(42), (ushort)dstPort);
        payload.CopyTo(packet, 48);

        return packet;
    }

    [Fact]
    public void Reads_an_ipv4_tcp_packet()
    {
        var raw = BuildIpv4Tcp("10.87.0.2", "93.184.216.34", 51000, 443);
        var packet = new IpPacket(raw);

        Assert.True(packet.IsValid);
        Assert.True(packet.IsIpv4);
        Assert.False(packet.IsIpv6);
        Assert.Equal(IpPacket.ProtocolTcp, packet.ProtocolNumber);
        Assert.Equal(51000, packet.SourcePort);
        Assert.Equal(443, packet.DestinationPort);
        Assert.Equal("93.184.216.34", packet.DestinationAddress);
    }

    [Fact]
    public void Reads_an_ipv6_udp_packet()
    {
        // Everything moved relative to v4: the protocol lives at offset 6, the
        // addresses at 8 and 24, and the header is a fixed 40 bytes.
        var raw = BuildIpv6Udp("fd87::2", "2001:4860:4860::8888", 51000, 53);
        var packet = new IpPacket(raw);

        Assert.True(packet.IsValid);
        Assert.True(packet.IsIpv6);
        Assert.Equal(IpPacket.ProtocolUdp, packet.ProtocolNumber);
        Assert.Equal(40, packet.HeaderLength);
        Assert.Equal(51000, packet.SourcePort);
        Assert.Equal(53, packet.DestinationPort);
        Assert.Equal("2001:4860:4860::8888", packet.DestinationAddress);
    }

    [Fact]
    public void Ipv6_total_length_includes_the_header()
    {
        // The v6 field is the payload length only, unlike v4's total length.
        var raw = BuildIpv6Udp("fd87::2", "2001:4860:4860::8888", 1, 53, new byte[100]);
        var packet = new IpPacket(raw);

        Assert.Equal(raw.Length, packet.TotalLength);
    }

    [Fact]
    public void Ipv6_extension_headers_are_refused_rather_than_misread()
    {
        // Following the extension chain is real work. Treating an extension as
        // if it were TCP would parse an arbitrary offset as a port number and
        // open a connection to somewhere unintended - so these are dropped.
        var raw = BuildIpv6Udp("fd87::2", "2001:db8::1", 1, 53);
        raw[6] = 43;   // routing header

        Assert.False(new IpPacket(raw).IsValid);
    }

    [Fact]
    public void Reads_the_tcp_payload_past_a_variable_header()
    {
        byte[] body = { 0x47, 0x45, 0x54 };   // "GET"
        var raw = BuildIpv4Tcp("10.87.0.2", "1.1.1.1", 40000, 80, body);

        Assert.Equal(body, new IpPacket(raw).TcpPayload.ToArray());
    }

    [Fact]
    public void Reads_tcp_flags()
    {
        var raw = BuildIpv4Tcp("10.87.0.2", "1.1.1.1", 40000, 80);
        raw[33] = 0x02;   // SYN

        var packet = new IpPacket(raw);
        Assert.True(packet.TcpSyn);
        Assert.False(packet.TcpAckFlag);
        Assert.False(packet.TcpRst);
        Assert.False(packet.TcpFin);
    }

    [Fact]
    public void A_runt_packet_is_invalid()
    {
        Assert.False(new IpPacket(new byte[8]).IsValid);
    }

    [Fact]
    public void A_packet_claiming_a_header_longer_than_itself_is_invalid()
    {
        // Trusting this would read past the end of the buffer.
        var raw = BuildIpv4Tcp("10.87.0.2", "1.1.1.1", 1, 80);
        raw[0] = 0x4F;   // 15-word header, longer than the packet

        Assert.False(new IpPacket(raw).IsValid);
    }
}
