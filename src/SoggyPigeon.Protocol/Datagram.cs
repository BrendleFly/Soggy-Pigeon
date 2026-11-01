using System.Security.Cryptography;
using System.Text;

namespace SoggyPigeon.Protocol;

/// <summary>
/// The single binary header every UDP datagram shares — punch, ping and relay.
/// This layout is part of the frozen v1 surface: the C SDK (libpigeon) emits and
/// consumes relay datagrams with exactly these bytes.
///
///   offset 0        magic 'P'
///   offset 1        type (Proto.DatagramType)
///   offset 2..9     session tag (8 bytes, SessionTag.Compute)
///   offset 10       role (0 unknown, 1 host, 2 join)
///   offset 11..     payload (type-specific; relay = opaque game datagram)
/// </summary>
public static class SessionTag
{
    public static byte[] Compute(string sessionId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sessionId));
        var tag = new byte[Proto.RelaySessionTagBytes];
        Array.Copy(hash, tag, tag.Length);
        return tag;
    }

    /// <summary>Hex form handed to the game on the boot line (--pigeon-tag).</summary>
    public static string Hex(string sessionId)
        => Convert.ToHexString(Compute(sessionId)).ToLowerInvariant();
}

public static class Datagram
{
    public const int HeaderSize = 11;

    public sealed class Packet
    {
        public Proto.DatagramType Type { get; init; }
        public byte[] SessionTag { get; init; } = new byte[Proto.RelaySessionTagBytes];
        public byte Role { get; init; }
        public byte[] Payload { get; init; } = Array.Empty<byte>();
    }

    public static byte[] Encode(Proto.DatagramType type, byte[] sessionTag, byte role, ReadOnlySpan<byte> payload = default)
    {
        if (sessionTag.Length != Proto.RelaySessionTagBytes)
            throw new ArgumentException("session tag must be 8 bytes");
        var buf = new byte[HeaderSize + payload.Length];
        buf[0] = Proto.DatagramMagic;
        buf[1] = (byte)type;
        sessionTag.AsSpan(0, Proto.RelaySessionTagBytes).CopyTo(buf.AsSpan(2));
        buf[10] = role;
        payload.CopyTo(buf.AsSpan(HeaderSize));
        return buf;
    }

    public static bool TryDecode(ReadOnlySpan<byte> data, out Packet packet)
    {
        packet = null!;
        if (data.Length < HeaderSize) return false;
        if (data[0] != Proto.DatagramMagic) return false;
        var type = (Proto.DatagramType)data[1];
        if (type is < Proto.DatagramType.Punch or > Proto.DatagramType.RelayFrom) return false;
        var tag = data.Slice(2, Proto.RelaySessionTagBytes).ToArray();
        var payload = data.Slice(HeaderSize).ToArray();
        packet = new Packet { Type = type, SessionTag = tag, Role = data[10], Payload = payload };
        return true;
    }

    /// <summary>"ip:port" seen payload carried in Seen packets (IPv4).</summary>
    public static byte[] EncodeEndpoint(string ip, int port)
    {
        var ipBytes = System.Net.IPAddress.Parse(ip).GetAddressBytes();
        if (ipBytes.Length != 4) throw new ArgumentException("v1 seen payload carries IPv4");
        var buf = new byte[6];
        ipBytes.CopyTo(buf, 0);
        BitConverter.GetBytes((ushort)port).CopyTo(buf, 4);
        return buf;
    }

    public static (string Ip, int Port) DecodeEndpoint(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 6) throw new InvalidDataException("endpoint payload too short");
        var ip = new System.Net.IPAddress(payload.Slice(0, 4).ToArray()).ToString();
        var port = BitConverter.ToUInt16(payload.Slice(4, 2).ToArray(), 0);
        return (ip, port);
    }
}
