using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using SoggyPigeon.Protocol;

namespace SoggyPigeon.App.Services;

public sealed record PunchOutcome(IPEndPoint? Peer, int PingMs, bool SameNat);

/// <summary>
/// The client half of the punch (§4.3): bind the game's UDP port, punch the
/// server's rendezvous until the peer's endpoint arrives, spam direct `hi`
/// datagrams, measure ping (lowest of 6), then CLOSE the socket so the game
/// can rebind the same port with the NAT mapping still warm.
/// </summary>
public static class PunchService
{
    public static async Task<PunchOutcome?> PunchAsync(
        string sessionId, int role, IPEndPoint rendezvous, int localPort,
        Task<PeerMsg>? peerHintTask, double maxSeconds = Proto.PunchMaxSeconds)
    {
        var tag = SessionTag.Compute(sessionId);
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, localPort > 0 ? localPort : 0));
        udp.Client.ReceiveTimeout = 250;

        IPEndPoint? peerPublic = null;

        IPEndPoint? TakeHint(PeerMsg? hint)
        {
            if (hint?.Public is { Length: 2 } pub
                && IPAddress.TryParse(pub[0], out var pip) && int.TryParse(pub[1], out var pport))
                return new IPEndPoint(pip, pport);
            return null;
        }

        if (peerHintTask is { IsCompleted: true })
            peerPublic = TakeHint(peerHintTask.Result);

        var sw = Stopwatch.StartNew();
        var pings = new List<long>();      // elapsed microseconds per completed echo
        IPEndPoint? seenSelf = null;

        while (sw.Elapsed.TotalSeconds < maxSeconds)
        {
            // 0) the peer's address arrives over TCP at any moment
            if (peerPublic is null && peerHintTask is { IsCompleted: true })
                peerPublic = TakeHint(peerHintTask.Result);

            // 1) keep punching the rendezvous until the peer's address arrived
            if (peerPublic is null)
            {
                var buf = Datagram.Encode(Proto.DatagramType.Punch, tag, (byte)role);
                await udp.SendAsync(buf, buf.Length, rendezvous);
            }
            else
            {
                // 2) direct path: spam hi, and ping until we have enough samples
                var hi = Datagram.Encode(Proto.DatagramType.Hi, tag, (byte)role);
                await udp.SendAsync(hi, hi.Length, peerPublic);

                if (pings.Count < Proto.PunchPingSamples)
                {
                    var ping = Datagram.Encode(Proto.DatagramType.Ping, tag, (byte)role,
                        BitConverter.GetBytes(Stopwatch.GetTimestamp()));
                    await udp.SendAsync(ping, ping.Length, peerPublic);
                }
            }

            // 3) drain replies
            while (true)
            {
                var anyEp = new IPEndPoint(IPAddress.Any, 0);
                byte[] data;
                try
                {
                    data = udp.Receive(ref anyEp);
                }
                catch (SocketException)
                {
                    break; // receive timeout -> next tick
                }
                catch (ObjectDisposedException) { return null; }

                if (!Datagram.TryDecode(data, out var pkt)) continue;
                if (!pkt.SessionTag.AsSpan().SequenceEqual(tag)) continue;

                switch (pkt.Type)
                {
                    case Proto.DatagramType.Seen:
                        seenSelf = DecodeEndpointSafe(pkt.Payload);
                        break;
                    case Proto.DatagramType.Hi:
                        break; // direct path is open
                    case Proto.DatagramType.Ping:
                        if (peerPublic is not null)
                        {
                            var pong = Datagram.Encode(Proto.DatagramType.Pong, tag, (byte)role, pkt.Payload);
                            await udp.SendAsync(pong, pong.Length, peerPublic);
                        }
                        break;
                    case Proto.DatagramType.Pong:
                        if (pkt.Payload.Length >= 8)
                        {
                            var sent = BitConverter.ToInt64(pkt.Payload, 0);
                            var us = (Stopwatch.GetTimestamp() - sent) * 1_000_000.0 / Stopwatch.Frequency;
                            pings.Add((long)us);
                        }
                        break;
                }
            }

            // 4) enough echo samples: report the direct outcome
            if (peerPublic is not null && pings.Count >= Proto.PunchPingSamples)
            {
                var sameNat = seenSelf is not null && peerPublic.Address.Equals(seenSelf.Address);
                udp.Close(); // hand the port back to the game
                return new PunchOutcome(peerPublic, (int)(pings.Min() / 1000.0), sameNat);
            }

            await Task.Delay((int)(Proto.PunchIntervalSeconds * 1000));
        }

        // timed out: no direct connection — the game will use the relay
        udp.Close();
        return null;
    }

    private static IPEndPoint? DecodeEndpointSafe(byte[] payload)
    {
        try
        {
            var (ip, port) = Datagram.DecodeEndpoint(payload);
            return new IPEndPoint(IPAddress.Parse(ip), port);
        }
        catch { return null; }
    }
}
