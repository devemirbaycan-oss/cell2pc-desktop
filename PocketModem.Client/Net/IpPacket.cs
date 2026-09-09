using System.Buffers.Binary;

namespace PocketModem.Client.Net;

/// <summary>
/// Minimal IPv4/TCP/UDP reader over a raw packet from Wintun.
///
/// Only what the tunnel needs to classify a packet and rebuild it (DESIGN.md
/// §7.1). Deliberately not a general IP stack: it reads headers in place with
/// no allocation, because this sits on the hot path for every packet the PC
/// sends.
/// </summary>
internal readonly ref struct IpPacket
{
    public readonly ReadOnlySpan<byte> Data;

    public IpPacket(ReadOnlySpan<byte> data) => Data = data;

    public int Version => Data.Length > 0 ? Data[0] >> 4 : 0;
    public bool IsIpv4 => Version == 4;
    public bool IsIpv6 => Version == 6;

    /// <summary>
    /// IPv6 has a fixed 40-byte header; IPv4's is variable and given in
    /// 32-bit words.
    /// </summary>
    public int HeaderLength => IsIpv6 ? 40 : (Data[0] & 0x0F) * 4;

    /// <summary>
    /// The transport protocol. IPv6 calls it Next Header and puts it at a
    /// different offset; extension headers are not followed, which is why
    /// packets carrying them are skipped rather than misread (see IsValid).
    /// </summary>
    public byte ProtocolNumber => IsIpv6 ? Data[6] : Data[9];

    public const byte ProtocolTcp = 6;
    public const byte ProtocolUdp = 17;
    public const byte ProtocolIcmp = 1;

    public ReadOnlySpan<byte> SourceIp => IsIpv6 ? Data.Slice(8, 16) : Data.Slice(12, 4);
    public ReadOnlySpan<byte> DestinationIp => IsIpv6 ? Data.Slice(24, 16) : Data.Slice(16, 4);

    /// <summary>IPv6's field is the payload length, excluding its header.</summary>
    public int TotalLength => IsIpv6
        ? 40 + BinaryPrimitives.ReadUInt16BigEndian(Data.Slice(4, 2))
        : BinaryPrimitives.ReadUInt16BigEndian(Data.Slice(2, 2));

    /// <summary>Payload after the IP header (the TCP or UDP segment).</summary>
    public ReadOnlySpan<byte> Payload => Data[HeaderLength..];

    public int SourcePort => BinaryPrimitives.ReadUInt16BigEndian(Payload.Slice(0, 2));
    public int DestinationPort => BinaryPrimitives.ReadUInt16BigEndian(Payload.Slice(2, 4 - 2));

    // --- TCP ---

    public uint TcpSequence => BinaryPrimitives.ReadUInt32BigEndian(Payload.Slice(4, 4));
    public uint TcpAck => BinaryPrimitives.ReadUInt32BigEndian(Payload.Slice(8, 4));
    public int TcpHeaderLength => (Payload[12] >> 4) * 4;
    public byte TcpFlags => Payload[13];

    public bool TcpFin => (TcpFlags & 0x01) != 0;
    public bool TcpSyn => (TcpFlags & 0x02) != 0;
    public bool TcpRst => (TcpFlags & 0x04) != 0;
    public bool TcpPsh => (TcpFlags & 0x08) != 0;
    public bool TcpAckFlag => (TcpFlags & 0x10) != 0;

    public ReadOnlySpan<byte> TcpPayload
    {
        get
        {
            var seg = Payload;
            int off = TcpHeaderLength;
            return off >= seg.Length ? ReadOnlySpan<byte>.Empty : seg[off..];
        }
    }

    // --- UDP ---

    public int UdpLength => BinaryPrimitives.ReadUInt16BigEndian(Payload.Slice(4, 2));

    public ReadOnlySpan<byte> UdpPayload
    {
        get
        {
            var seg = Payload;
            return seg.Length <= 8 ? ReadOnlySpan<byte>.Empty : seg[8..];
        }
    }

    /// <summary>
    /// A packet this code can actually forward.
    ///
    /// An IPv6 packet whose Next Header is an extension rather than TCP or UDP
    /// is treated as invalid: following the extension chain is real work, and
    /// misreading one would mean parsing an arbitrary offset as a port number.
    /// Dropping is honest; guessing is not.
    /// </summary>
    public bool IsValid => IsIpv6
        ? Data.Length >= 40 &&
          (ProtocolNumber is ProtocolTcp or ProtocolUdp or ProtocolIcmpV6)
        : Data.Length >= 20 && IsIpv4 && HeaderLength >= 20 && HeaderLength <= Data.Length;

    public const byte ProtocolIcmpV6 = 58;

    public string DestinationAddress => Format(DestinationIp);
    public string SourceAddress => Format(SourceIp);

    private static string Format(ReadOnlySpan<byte> ip) => ip.Length == 16
        ? new System.Net.IPAddress(ip).ToString()
        : $"{ip[0]}.{ip[1]}.{ip[2]}.{ip[3]}";
}
