using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using SoggyPigeon.Protocol;

namespace SoggyPigeon.Server;

/// <summary>Fault injection for relayed game traffic — proves the netcode
/// under real internet conditions (loss, jitter, reordering).</summary>
public sealed class ChaosConfig
{
    /// <summary>Percent of forwarded datagrams to silently drop (0–100).</summary>
    public double DropPercent { get; init; }
    /// <summary>Max added forwarding latency in ms (uniform 0..N; also reorders).</summary>
    public int JitterMs { get; init; }
    public Random Rng { get; init; } = new();
}

/// <summary>
/// The server's single UDP socket: punch rendezvous + relay (§4.3).
/// All datagrams share the frozen binary header (Protocol/Datagram.cs).
/// </summary>
public sealed class Rendezvous : IDisposable
{
    private readonly UdpClient _udp;
    private readonly Hub _hub;
    private readonly CancellationTokenSource _cts = new();
    private readonly ChaosConfig? _chaos;

    // (session tag hex, role) -> source endpoint, for punch replies and relay targets
    private readonly ConcurrentDictionary<(string TagHex, int Role), IPEndPoint> _endpoints = new();

    public Rendezvous(int port, Hub hub, ChaosConfig? chaos = null)
    {
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        _hub = hub;
        _chaos = chaos;
    }

    public IPEndPoint LocalEndPoint => (IPEndPoint)_udp.Client.LocalEndPoint!;

    public Task RunAsync() => Task.Run(LoopAsync);

    private async Task LoopAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var res = await _udp.ReceiveAsync(ct).ConfigureAwait(false);
                Handle(res.Buffer, res.RemoteEndPoint);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { /* transient */ }
            catch (ObjectDisposedException) { break; }
        }
    }

    internal static readonly bool Debug = Environment.GetEnvironmentVariable("PIGEON_SERVER_DEBUG") == "1";

    private static void Dbg(string line)
    {
        if (!Debug) return;
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "sp-rendezvous.log"), line + "\n"); } catch { }
    }

    private void Handle(byte[] data, IPEndPoint from)
    {
        if (!Datagram.TryDecode(data, out var pkt)) { Dbg($"DROP undecodable {data.Length}B from {from}"); return; }
        var key = (Convert.ToHexString(pkt.SessionTag), (int)pkt.Role);

        switch (pkt.Type)
        {
            case Proto.DatagramType.Punch:
            {
                if (Authorized(pkt.SessionTag, pkt.Role, from) is null) break;
                // re-notify on ANY address change, not only the first punch:
                // after a NAT rebinding the peer would otherwise keep a dead
                // endpoint (with a wrong boot arg) for the rest of the match
                var changed = !_endpoints.TryGetValue(key, out var prev) || !prev.Equals(from);
                _endpoints[key] = from;
                Dbg($"punch {key.Item1}[{pkt.Role}] from {from}{(changed ? "" : " (unchanged)")}"
                    + (changed && prev is { } ? $" — endpoint changed, was {prev}; re-notifying peers" : ""));
                // tell the puncher its public endpoint
                _ = SendAsync(Proto.DatagramType.Seen, pkt.SessionTag, pkt.Role,
                    Datagram.EncodeEndpoint(from.Address.ToString(), from.Port), from);

                if (changed) NotifyPeers(pkt.SessionTag, key.Item1, (int)pkt.Role);
                break;
            }

            case Proto.DatagramType.Relay:
            {
                if (Authorized(pkt.SessionTag, pkt.Role, from) is not { } sess) break;
                _endpoints[key] = from; // keep the freshest return address
                var other = pkt.Role == 1 ? 2 : 1;
                var otherKey = (key.Item1, other);
                if (_endpoints.TryGetValue(otherKey, out var target))
                {
                    Dbg($"relay f={pkt.Role}->{other} sess={sess.Game}");
                    // forward the inner datagram (pkt.Payload) — the receiving SDK
                    // strips the RelayFrom header and expects exactly this beneath
                    ForwardRelay(Proto.DatagramType.RelayFrom, pkt.SessionTag, (byte)other, pkt.Payload, target);
                }
                else Dbg($"relay NO-TARGET role={pkt.Role} (peer never seen)");
                break;
            }

            // direct client-to-client datagrams (Hi/Ping/Pong) never reach the
            // server by design — if they do, ignore them.
        }
    }

    /// <summary>A datagram may bind or rebind a session's UDP endpoint only if
    /// it comes from the same public address as that role's TLS-authenticated
    /// TCP control connection. The session tag alone proves nothing: it is a
    /// plain hash of the session id, visible to both peers and passed to the
    /// games on their command lines — anyone who learns it could otherwise
    /// overwrite `_endpoints` and hijack (or silently drop) the relayed input
    /// stream. Returns false for unknown/ended sessions and bad roles, and
    /// true only when the source address matches.</summary>
    private Session? Authorized(byte[] tag, byte role, IPEndPoint from)
    {
        var s = _hub.FindSessionByTag(tag);
        if (s is not { Ended: false } sess || role is not (1 or 2)
            || !sess.Clients[role - 1].EndPoint.Address.Equals(from.Address))
        {
            Dbg($"DROP unauthorized tag={Convert.ToHexString(tag)}[{role}] from {from} (the tag is known to both peers; the source address must match the role's control connection)");
            return null;
        }
        return sess;
    }

    /// <summary>Both roles of a session are visible: send each side the peer's address.</summary>
    private void NotifyPeers(byte[] tag, string tagHex, int justSeenRole)
    {
        var s = _hub.FindSessionByTag(tag);
        if (s is null || s.Ended) return;
        var otherRole = justSeenRole == 1 ? 2 : 1;
        if (!_endpoints.TryGetValue((tagHex, otherRole), out var toEndpoint)) return;

        // role 1 is session index 0; role 2 is index 1
        var fromClient = s.Clients[justSeenRole - 1];
        var toClient = s.Clients[otherRole - 1];
        var fromEndpoint = _endpoints[(tagHex, justSeenRole)];
        var sameNat = fromClient.EndPoint.Address.Equals(toClient.EndPoint.Address);

        // the earlier-punched side ("to") learns the puncher's endpoint…
        _ = toClient.Connection.TrySendAsync(new PeerMsg
        {
            Session = s.Id,
            Public = new[] { fromEndpoint.Address.ToString(), fromEndpoint.Port.ToString() },
            SameNat = sameNat,
        });
        // …and the puncher learns the earlier side's endpoint
        _ = fromClient.Connection.TrySendAsync(new PeerMsg
        {
            Session = s.Id,
            Public = new[] { toEndpoint.Address.ToString(), toEndpoint.Port.ToString() },
            SameNat = sameNat,
        });
    }

    /// <summary>Forward with optional fault injection: random drops and
    /// random added latency (which also reorders). Punch traffic is exempt —
    /// pairing should stay reliable; the input path is what we stress.</summary>
    private void ForwardRelay(Proto.DatagramType type, byte[] tag, byte role, byte[] payload, IPEndPoint to)
    {
        if (_chaos is { } chaos)
        {
            if (chaos.Rng.NextDouble() * 100.0 < chaos.DropPercent)
            {
                Dbg($"chaos DROP {payload.Length}B -> {to}");
                return;
            }
            var delay = chaos.Rng.Next(chaos.JitterMs + 1);
            if (delay > 0)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(delay).ConfigureAwait(false);
                        await SendAsync(type, tag, role, payload, to).ConfigureAwait(false);
                    }
                    catch { /* shutting down */ }
                });
                return;
            }
        }
        _ = SendAsync(type, tag, role, payload, to);
    }

    private async Task SendAsync(Proto.DatagramType type, byte[] tag, byte role, byte[] payload, IPEndPoint to)
    {
        try
        {
            var buf = Datagram.Encode(type, tag, role, payload);
            await _udp.SendAsync(buf, buf.Length, to).ConfigureAwait(false);
        }
        catch { /* transient */ }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp.Dispose();
        _cts.Dispose();
    }
}
