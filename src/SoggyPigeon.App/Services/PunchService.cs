using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using SoggyPigeon.Protocol;

namespace SoggyPigeon.App.Services;

public sealed record PunchOutcome(IPEndPoint? Peer, int PingMs, bool SameNat);

/// <summary>What the punch saw when it ended: the inputs the punch verdict
/// (MatchViewModel.NatVerdict, the T6NO natText ladder) reads its rungs
/// from. SeenSelf is our endpoint as the RENDEZVOUS observed it — the
/// difference from LocalPort is the router rewriting the port; PeerSeen
/// says whether the peer's punch reached the rendezvous (it only forwards
/// the peer's endpoint over TCP once it did).</summary>
public sealed record PunchDiag(bool Bound, IPEndPoint? SeenSelf, bool PeerSeen, int LocalPort);

public sealed record PunchResult(PunchOutcome? Outcome, PunchDiag Diag);

/// <summary>
/// The client half of the punch (§4.3): bind the game's UDP port, punch the
/// server's rendezvous until the peer's endpoint arrives, spam direct `hi`
/// datagrams, measure ping (lowest of 6), then CLOSE the socket so the game
/// can rebind the same port with the NAT mapping still warm.
/// </summary>
public static class PunchService
{
    public static async Task<PunchResult> PunchAsync(
        string sessionId, int role, IPEndPoint rendezvous, int localPort,
        Task<PeerMsg>? peerHintTask, double maxSeconds = Proto.PunchMaxSeconds,
        IEnumerable<string>? peerCandidates = null)
    {
        var tag = SessionTag.Compute(sessionId);
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, localPort > 0 ? localPort : 0));
        }
        catch (SocketException)
        {
            // port taken (a leftover game can still hold it): degrade to the
            // relay path instead of wedging the pairing UI (Haiku round 4)
            return new PunchResult(null, new PunchDiag(false, null, false, localPort));
        }
        // the port the punch actually left from (the OS assigns one when the
        // caller passes 0): the verdict compares the rendezvous' view of it
        var boundPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        udp.Client.ReceiveTimeout = 250;

        // 2026-11-20: the addresses the PEER advertised (its LAN addresses plus
        // its STUN-discovered public mapping). The rendezvous' view of the
        // peer is only ONE possible address; a NAT that maps per destination
        // hands out a different one for us than for the rendezvous, which is
        // exactly the case that used to force the relay. We punch every
        // candidate in parallel until one answers, then lock onto it.
        var candidates = new List<IPEndPoint>();
        var seenCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddCandidate(string? ep)
        {
            if (string.IsNullOrWhiteSpace(ep)) return;
            if (!ep.Contains(':')) return;
            var parts = ep.Split(':');
            if (parts.Length != 2) return;
            if (!IPAddress.TryParse(parts[0], out var ip)) return;
            if (!int.TryParse(parts[1], out var port) || port <= 0 || port > 65535) return;
            var e = new IPEndPoint(ip, port);
            if (seenCandidates.Add(e.ToString()))
                candidates.Add(e);
        }

        foreach (var c in peerCandidates ?? Array.Empty<string>())
            AddCandidate(c);

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
                // 2) direct path: spam hi to the peer at EVERY address it
                // advertised, not just the one the rendezvous saw. The first
                // `hi` that comes back tells us which address actually works,
                // and we lock onto that one for pings. Sending to all of them
                // costs a few extra datagrams per tick and is the difference
                // between connecting and falling back to the relay.
                var hi = Datagram.Encode(Proto.DatagramType.Hi, tag, (byte)role);
                await udp.SendAsync(hi, hi.Length, peerPublic);
                for (var ci = 0; ci < candidates.Count; ci++)
                {
                    var c = candidates[ci];
                    if (c.Equals(peerPublic)) continue;     // already sent above
                    try { await udp.SendAsync(hi, hi.Length, c); } catch (SocketException) { }
                }

                if (pings.Count < Proto.PunchPingSamples)
                {
                    var ping = Datagram.Encode(Proto.DatagramType.Ping, tag, (byte)role,
                        BitConverter.GetBytes(Stopwatch.GetTimestamp()));
                    await udp.SendAsync(ping, ping.Length, peerPublic);
                    for (var ci = 0; ci < candidates.Count; ci++)
                    {
                        var c = candidates[ci];
                        if (c.Equals(peerPublic)) continue;
                        try { await udp.SendAsync(ping, ping.Length, c); } catch (SocketException) { }
                    }
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
                catch (ObjectDisposedException)
                {
                    return new PunchResult(null,
                        new PunchDiag(true, seenSelf, peerPublic is not null, boundPort));
                }

                if (!Datagram.TryDecode(data, out var pkt)) continue;
                if (!pkt.SessionTag.AsSpan().SequenceEqual(tag)) continue;

                switch (pkt.Type)
                {
                    case Proto.DatagramType.Seen:
                        seenSelf = DecodeEndpointSafe(pkt.Payload);
                        break;
                    case Proto.DatagramType.Hi:
                        // The peer's `hi` arrived FROM the address that works.
                        // Nominate it (2026-11-20): the rendezvous' view of the
                        // peer can be a different mapping than the one that
                        // actually carries our traffic, and everything after
                        // this point (pings, the outcome the game is handed)
                        // must use the address that demonstrably answers.
                        if (peerPublic is null || !anyEp.Equals(peerPublic))
                            peerPublic = anyEp;
                        break;
                    case Proto.DatagramType.Ping:
                        {
                            // answer whoever asked, from whatever address they
                            // reached us on - that is the pair that works
                            var pong = Datagram.Encode(Proto.DatagramType.Pong, tag, (byte)role, pkt.Payload);
                            try { await udp.SendAsync(pong, pong.Length, anyEp); } catch (SocketException) { }
                            if (peerPublic is null) peerPublic = anyEp;
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
                return new PunchResult(
                    new PunchOutcome(peerPublic, (int)(pings.Min() / 1000.0), sameNat),
                    new PunchDiag(true, seenSelf, true, boundPort));
            }

            await Task.Delay((int)(Proto.PunchIntervalSeconds * 1000));
        }

        // timed out: no direct connection — mock games fall back to the
        // relay, framework games get the punch verdict instead of a boot
        udp.Close();
        return new PunchResult(null, new PunchDiag(true, seenSelf, peerPublic is not null, boundPort));
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
