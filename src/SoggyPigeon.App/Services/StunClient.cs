using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;

namespace SoggyPigeon.App.Services;

/// <summary>One address the peer can try to reach us on: "ip:port".</summary>
public sealed record Candidate(string Endpoint, CandidateKind Kind);

public enum CandidateKind
{
    /// <summary>Our own LAN address (RFC 1918 / link-local). Lets two players
    /// behind the same router connect directly even when the router will not
    /// hairpin a public address back inside.</summary>
    Host,
    /// <summary>Our public mapping as seen by a STUN server on the internet
    /// (RFC 5389 XOR-MAPPED-ADDRESS). Can differ from the address the
    /// rendezvous sees, which is the whole point.</summary>
    ServerReflexive,
}

/// <summary>
/// Minimal STUN client (RFC 5389) — just enough for "what does the internet
/// think my address is?".
///
/// We only send Binding Requests and read XOR-MAPPED-ADDRESS (or the legacy
/// MAPPED-ADDRESS). No MESSAGE-INTEGRITY, no FINGERPRINT, no ICE, no TURN:
/// this is not an ICE implementation, it is the one question ICE would ask
/// first. Full ICE arrives later (option B); this is the cheap, dependency-
/// free version that already fixes the symmetric-mapping punch failures.
///
/// The exchange is a single UDP round trip with a short budget, and every
/// failure path returns null rather than throwing: a blocked or slow STUN
/// server must degrade to "no reflexive candidate", never to "no match".
/// </summary>
public static class StunClient
{
    private const ushort BindingRequest = 0x0001;
    private const ushort BindingSuccess = 0x0101;
    private const uint MagicCookie = 0x2112A442;
    private const ushort AttrMappedAddress = 0x0001;
    private const ushort AttrXorMappedAddress = 0x0020;

    /// <summary>Public STUN servers, tried in order. Two providers so that one
    /// being down or blocked does not leave us without a candidate.</summary>
    public static readonly (string Host, int Port)[] DefaultServers =
    {
        ("stun.l.google.com", 19302),
        ("stun.cloudflare.com", 3478),
    };

    /// <summary>Ask `server` what our public socket address looks like from
    /// where it sits. `localPort` is the port we intend to punch from; the
    /// caller binds it first so the mapping matches the one the game will use.
    /// Returns null on any failure.</summary>
    public static async Task<IPEndPoint?> QueryAsync(
        UdpClient boundSocket, (string Host, int Port) server,
        CancellationToken ct = default, int timeoutMs = 1200)
    {
        try
        {
            // 20-byte header + no attributes: a bare Binding Request is legal
            // and needs no integrity protection for our purposes.
            var txid = new byte[12];
            System.Security.Cryptography.RandomNumberGenerator.Fill(txid);
            var req = new byte[20];
            WriteUInt16(req, 0, BindingRequest);
            WriteUInt16(req, 2, 0);                 // length: no attributes
            WriteUInt32(req, 4, MagicCookie);
            Array.Copy(txid, 0, req, 8, 12);

            await boundSocket.SendAsync(req, req.Length, server.Host, server.Port)
                               .ConfigureAwait(false);

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(timeoutMs);
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            while (DateTime.UtcNow < deadline)
            {
                var anyEp = new IPEndPoint(IPAddress.Any, 0);
                var data = await ReceiveAsync(boundSocket, budget.Token).ConfigureAwait(false);
                if (data is null) return null;      // cancelled / socket closed
                if (data.Length < 20) continue;
                if (ReadUInt16(data, 0) != BindingSuccess) continue;
                if (!data.AsSpan(8, 12).SequenceEqual(txid)) continue;   // our request
                return ParseAddress(data);
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
        return null;
    }

    /// <summary>Every LAN IPv4 we can advertise with `port`. Loopback, link-
    /// local and anything that is not a real interface address is skipped:
    /// a candidate nobody can dial is worse than no candidate, because the
    /// peer burns punch time on it.</summary>
    public static List<Candidate> LocalCandidates(int port)
    {
        var list = new List<Candidate>();
        if (port <= 0 || port > 65535) return list;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ip = ua.Address;
                    if (IPAddress.IsLoopback(ip)) continue;
                    if (!IsRoutablePrivate(ip)) continue;   // skips 169.254/0.0.0.0 etc.
                    var ep = new IPEndPoint(ip, port).ToString();
                    if (seen.Add(ep)) list.Add(new Candidate(ep, CandidateKind.Host));
                }
            }
        }
        catch (NetworkInformationException) { }
        catch (SocketException) { }
        return list;
    }

    /// <summary>RFC 1918 + CGNAT (100.64/10): addresses that only mean
    /// something inside one network. A CGNAT address is still worth offering
    /// when both peers are behind the same carrier NAT, which does happen.</summary>
    private static bool IsRoutablePrivate(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        if (b.Length != 4) return false;
        if (b[0] == 10) return true;                                   // 10/8
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;      // 172.16/12
        if (b[0] == 192 && b[1] == 168) return true;                   // 192.168/16
        if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;     // 100.64/10 CGNAT
        return false;
    }

    private static IPEndPoint? ParseAddress(byte[] msg)
    {
        var len = ReadUInt16(msg, 2);
        var end = Math.Min(msg.Length, 20 + len);
        var i = 20;
        while (i + 4 <= end)
        {
            var type = ReadUInt16(msg, i);
            var alen = ReadUInt16(msg, i + 2);
            var v = i + 4;
            if (v + alen > msg.Length) break;
            if (type == AttrXorMappedAddress || type == AttrMappedAddress)
                return DecodeAddress(msg, v, alen, type == AttrXorMappedAddress);
            i = v + ((alen + 3) & ~3);      // attributes are padded to 4 bytes
        }
        return null;
    }

    private static IPEndPoint? DecodeAddress(byte[] msg, int at, int len, bool xor)
    {
        if (len < 4) return null;
        var family = msg[at + 1];
        ushort port = ReadUInt16(msg, at + 2);
        if (xor) port = (ushort)(port ^ (MagicCookie >> 16));
        if (family != 0x01) return null;    // IPv4 only (this stack is IPv4-only)
        if (len < 8) return null;
        var addr = new byte[4];
        Array.Copy(msg, at + 4, addr, 0, 4);
        if (xor)
            for (var k = 0; k < 4; k++) addr[k] ^= msg[4 + k];
        return new IPEndPoint(new IPAddress(addr), port);
    }

    private static async Task<byte[]?> ReceiveAsync(UdpClient udp, CancellationToken ct)
    {
        try
        {
            var r = await udp.ReceiveAsync(ct).ConfigureAwait(false);
            return r.Buffer;
        }
        catch (OperationCanceledException) { return null; }
        catch (SocketException) { return null; }
        catch (ObjectDisposedException) { return null; }
    }

    private static void WriteUInt16(byte[] b, int at, ushort v)
    {
        b[at] = (byte)(v >> 8); b[at + 1] = (byte)v;      // network order
    }

    private static void WriteUInt32(byte[] b, int at, uint v)
    {
        b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16);
        b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v;
    }

    private static ushort ReadUInt16(byte[] b, int at) => (ushort)((b[at] << 8) | b[at + 1]);
    private static uint ReadUInt32(byte[] b, int at) =>
        (uint)((b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3]);
}