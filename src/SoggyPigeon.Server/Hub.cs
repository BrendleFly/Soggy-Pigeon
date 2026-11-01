using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using SoggyPigeon.Protocol;

namespace SoggyPigeon.Server;

/// <summary>One connected, greeted client.</summary>
public sealed class ClientState
{
    public required ClientConnection Connection { get; init; }
    public required string Token { get; init; }
    public string Name { get; set; } = "";
    public IPEndPoint EndPoint => Connection.RemoteEndPoint;
    public long LastSeenTicks { get; set; } = Environment.TickCount64;
    /// <summary>Current queue entry (games+mode+policy) or null.</summary>
    public QueueEntry? Queue { get; set; }
    public Session? Session { get; set; }
}

public sealed class QueueEntry
{
    public required QueueGame[] Games { get; init; }
    public required string Mode { get; init; } // "any", "casual", "ranked" (future)
    public required string Policy { get; init; }
    /// <summary>Preferred rollback input delay (frames). Session parameter —
    /// reconciled against the other player at pairing.</summary>
    public required int Delay { get; init; }
    /// <summary>Preferred sync-proof cadence (1 = prove every frame).</summary>
    public required int DigestEvery { get; init; }
}

/// <summary>A paired match waiting to be played.</summary>
public sealed class Session
{
    public required string Id { get; init; }
    public required string Game { get; init; }
    public required uint Seed { get; init; }
    /// <summary>Game-specific params for the boot line (§6.3).</summary>
    public required string Params { get; init; }
    /// <summary>Reconciled session-wide rollback delay (frames): the
    /// higher (more cautious) player's choice — both peers must run the
    /// same value or the frame streams desync.</summary>
    public required int Delay { get; init; }
    /// <summary>Reconciled sync-proof cadence: the lower (more-proving)
    /// player's choice. 1 = prove every frame.</summary>
    public required int DigestEvery { get; init; }
    /// <summary>Index 0 = role 1 (host), index 1 = role 2 (join).</summary>
    public required ClientState[] Clients { get; init; }
    /// <summary>Each side's own rematch policy (§3.3 unilateral), index by role-1.</summary>
    public required string[] Policies { get; init; }
    /// <summary>Precomputed relay tag (relay is the hot path — no per-packet hashing).</summary>
    public required byte[] Tag { get; init; }
    public bool[] Playing { get; } = { false, false };
    public string?[] Results { get; } = { null, null };
    public bool Ended { get; set; }
}

/// <summary>
/// Authoritative matchmaking state (§6.1). One global lock: v1 scale is tiny,
/// and pairing must be atomic — a single gate is the simplest correct version.
/// </summary>
public sealed class Hub
{
    /// <summary>Faces the internet: an unbounded queue lets one client stall the pairing scan.</summary>
    public const int MaxQueueSize = 500;

    private readonly object _gate = new();
    private readonly Dictionary<string, ClientState> _clients = new();     // by token
    private readonly List<ClientState> _queue = new();
    private readonly Dictionary<string, Session> _sessions = new();        // by session id
    private readonly ManifestStore _manifest;
    private readonly TokenStore _tokens;
    private readonly Random _rng = new();

    /// <summary>"ip:port" of the UDP rendezvous, set by the host once bound.</summary>
    public string RelayEndpoint { get; set; } = "-";

    public Hub(ManifestStore manifest, TokenStore tokens)
    {
        _manifest = manifest;
        _tokens = tokens;
    }

    public int OnlineCount { get { lock (_gate) return _clients.Count; } }
    public int QueueCount { get { lock (_gate) return _queue.Count; } }

    // ---- connection lifecycle -------------------------------------------

    public void ClientConnected(ClientState client)
    {
        lock (_gate) _clients[client.Token] = client;
    }

    public void ClientDisconnected(ClientState client)
    {
        lock (_gate)
        {
            _clients.Remove(client.Token);
            RemoveFromQueueLocked(client);
            if (client.Session is { } s && !s.Ended)
                EndSessionLocked(s, "peer_disconnected", notifyIndex: OtherRole(s, client));
        }
        BroadcastCounts();
    }

    // ---- message handlers -------------------------------------------------

    public WelcomeMsg HandleHello(ClientState client)
    {
        lock (_gate)
        {
            client.Name = _tokens.TryLookup(client.Token, out var known) ? known : client.Name;
            if (client.Name.Length > 0) _tokens.Remember(client.Token, client.Name);
            return new WelcomeMsg { Online = _clients.Count, Counts = CountsLocked() };
        }
    }

    public ManifestMsg HandleManifestReq()
        => new() { Doc = _manifest.Current };

    public object? HandleEnterQueue(ClientState client, EnterQueueMsg msg)
    {
        if (client.Session is not null)
            return new ErrorMsg { Kind = "in_session", Detail = "finish the current session first" };
        if (msg.Games.Length == 0)
            return new ErrorMsg { Kind = "bad_queue", Detail = "no games enabled" };

        var entry = new QueueEntry
        {
            Games = msg.Games,
            Mode = msg.Mode is "any" or "casual" or "ranked" ? msg.Mode : "casual",
            Policy = msg.Policy == "single" ? "single" : "rematch",
            // session parameters: clamped to sane ranges; the pairing picks
            // the reconciled value (§ Messages.EnterQueueMsg docs)
            Delay = Math.Clamp(msg.Delay, 1, 8),
            DigestEvery = Math.Clamp(msg.DigestEvery, 1, 10),
        };

        lock (_gate)
        {
            // internet-facing guard: the pairing scan is O(n²) under the
            // global lock — an unbounded queue lets one client stall everyone
            if (client.Queue is null && _queue.Count >= MaxQueueSize)
                return new ErrorMsg { Kind = "server_busy", Detail = "queue is full, try again soon" };

            client.LastSeenTicks = Environment.TickCount64;
            RemoveFromQueueLocked(client);
            client.Queue = entry;
            _queue.Add(client);
            TryPairLocked();
        }
        BroadcastCounts();
        lock (_gate) return new QueuedMsg { Counts = CountsLocked() };
    }

    public void HandleLeaveQueue(ClientState client)
    {
        lock (_gate) RemoveFromQueueLocked(client);
        BroadcastCounts();
    }

    public void HandleHeartbeat(ClientState client)
    {
        lock (_gate) client.LastSeenTicks = Environment.TickCount64;
    }

    /// <summary>Any inbound traffic counts as life — "silent" should mean the
    /// TCP peer is mute, not merely that heartbeats paused.</summary>
    public void Touch(ClientState client)
    {
        lock (_gate) client.LastSeenTicks = Environment.TickCount64;
    }


    public void HandlePlaying(ClientState client, string sessionId)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(sessionId, out var s) && !s.Ended)
                for (var i = 0; i < 2; i++)
                    if (ReferenceEquals(s.Clients[i], client))
                        s.Playing[i] = true;
        }
    }

    public void HandleResult(ClientState client, string sessionId, string outcome, double durationS)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var s) || s.Ended) return;
            for (var i = 0; i < 2; i++)
                if (ReferenceEquals(s.Clients[i], client))
                {
                    s.Results[i] = outcome;
                    // §3.3: session teardown must remember the opponent's identity
                    // for a future "play same opponent again" feature — v1 keeps
                    // it in memory only (the client records its own history).
                }
            if (s.Results[0] is not null && s.Results[1] is not null)
                EndSessionLocked(s, "complete", notifyIndex: null);
        }
    }

    /// <summary>Drop queued clients that went silent (§6.1 heartbeats), and
    /// evict abandoned sessions: a player who closes their game without the
    /// app reporting results used to linger forever, blocking the next
    /// search with "in_session". A session with no results and neither side
    /// reporting "playing" is abandoned (mid-match sessions have both
    /// playing, protected by the same cutoff grace period).</summary>
    public void SweepSilent(double timeoutSeconds)
    {
        List<ClientState>? dropped = null;
        List<Session>? abandoned = null;
        lock (_gate)
        {
            var cutoff = Environment.TickCount64 - (long)(timeoutSeconds * 1000);
            for (var i = _queue.Count - 1; i >= 0; i--)
                if (_queue[i].LastSeenTicks < cutoff)
                {
                    var c = _queue[i];
                    RemoveFromQueueLocked(c);
                    dropped ??= new List<ClientState>();
                    dropped.Add(c);
                }
            foreach (var s in _sessions.Values)
            {
                // a match in play is never "abandoned": Playing means both apps
                // launched the game; it ends via results or disconnect instead
                if (s.Playing[0] || s.Playing[1]) continue;
                if (s.Clients[0].LastSeenTicks < cutoff && s.Clients[1].LastSeenTicks < cutoff)
                {
                    abandoned ??= new List<Session>();
                    abandoned.Add(s);
                }
            }
            if (abandoned is not null)
                foreach (var s in abandoned)
                    EndSessionLocked(s, "abandoned", notifyIndex: null);
        }
        if (dropped is not null)
        {
            BroadcastCounts();
            foreach (var c in dropped)
                _ = c.Connection.TrySendAsync(new SessionEndMsg { Session = "", Reason = "queue_timeout" });
        }
    }

    // ---- pairing -----------------------------------------------------------

    /// <summary>Mode compatibility per design:
    /// - "any" pairs with anything (any, casual, ranked)
    /// - "casual" pairs with "casual" and "any"
    /// - "ranked" pairs with "ranked" and "any"
    /// Different concrete modes never pair (casual != ranked).
    /// </summary>
    private static bool ModesCompatible(string a, string b)
    {
        if (a == "any" || b == "any") return true;
        return a == b;
    }

    private void TryPairLocked()
    {
        for (var i = 0; i < _queue.Count; i++)
            for (var j = i + 1; j < _queue.Count; j++)
            {
                var a = _queue[i];
                var b = _queue[j];

                if (!ModesCompatible(a.Queue!.Mode, b.Queue!.Mode)) continue;

                var shared = SharedGames(a.Queue!, b.Queue!);
                if (shared.Count == 0) continue;

                var game = shared[_rng.Next(shared.Count)]; // §3.1: uniform random pick
                var manifestGame = _manifest.Current.Games.FirstOrDefault(m => m.Id == game.Id);
                var id = NewSessionId();
                var session = new Session
                {
                    Id = id,
                    Tag = SessionTag.Compute(id),
                    Game = game.Id,
                    Seed = (uint)_rng.Next(1, int.MaxValue),
                    Clients = new[] { a, b },
                    Policies = new[] { a.Queue!.Policy, b.Queue!.Policy },
                    Params = manifestGame?.ParamsDefault ?? "",
                    // session-wide parameters both peers must agree on:
                    // delay = the more cautious player; cadence = the
                    // more-proving player (lower N proves more often)
                    Delay = Math.Max(a.Queue!.Delay, b.Queue!.Delay),
                    DigestEvery = Math.Min(a.Queue!.DigestEvery, b.Queue!.DigestEvery),
                };
                _queue.RemoveAt(j); // remove higher index first
                _queue.RemoveAt(i);
                a.Queue = null;
                b.Queue = null;
                a.Session = session;
                b.Session = session;
                _sessions[session.Id] = session;

                SendPaired(a, b, session, role: 1);
                SendPaired(b, a, session, role: 2);
                return; // one pair per tick; the next queue event re-scans
            }
    }

    private static List<ManifestGame> SharedGames(QueueEntry a, QueueEntry b)
    {
        var shared = new List<ManifestGame>();
        foreach (var ga in a.Games)
            foreach (var gb in b.Games)
                if (ga.Id == gb.Id && ga.Fingerprint == gb.Fingerprint)   // §3.2 mandatory
                {
                    var m = new ManifestGame { Id = ga.Id, Fingerprint = ga.Fingerprint };
                    shared.Add(m);
                    break;
                }
        return shared;
    }

    private void SendPaired(ClientState to, ClientState opponent, Session s, int role)
    {
    _ = to.Connection.TrySendAsync(new PairedMsg
    {
        Session = s.Id,
        Game = s.Game,
        Role = role,
        Seed = s.Seed,
        Policy = s.Policies[role - 1],           // §3.3: unilateral — your own choice
        OpponentName = opponent.Name,
        Relay = RelayEndpoint,
        Params = s.Params,
        Delay = s.Delay,
        DigestEvery = s.DigestEvery,
    });
    }

    private void EndSessionLocked(Session s, string reason, int? notifyIndex)
    {
        s.Ended = true;
        _sessions.Remove(s.Id);
        foreach (var c in s.Clients)
            if (c.Session == s) c.Session = null;
        if (notifyIndex is { } idx && idx >= 0 && idx < 2)
            _ = s.Clients[idx].Connection.TrySendAsync(new SessionEndMsg { Session = s.Id, Reason = reason });
    }

    private static int OtherRole(Session s, ClientState c)
        => ReferenceEquals(s.Clients[0], c) ? 1 : 0;

    private void RemoveFromQueueLocked(ClientState client)
    {
        if (client.Queue is null) return;
        _queue.Remove(client);
        client.Queue = null;
    }

    // ---- counts ------------------------------------------------------------

    /// <summary>Returns counts per game per mode for the client to filter by their own mode.</summary>
    private Dictionary<string, Dictionary<string, int>> CurrentCountsByMode()
    {
        lock (_gate)
        {
            var counts = new Dictionary<string, Dictionary<string, int>>();
            foreach (var c in _queue)
                foreach (var g in c.Queue!.Games)
                {
                    if (!counts.TryGetValue(g.Id, out var modeCounts))
                    {
                        modeCounts = new Dictionary<string, int>();
                        counts[g.Id] = modeCounts;
                    }
                    var mode = c.Queue!.Mode;
                    modeCounts[mode] = modeCounts.GetValueOrDefault(mode) + 1;
                }
            return counts;
        }
    }

    private Dictionary<string, int> CountsLocked()
    {
        var counts = new Dictionary<string, int>();
        foreach (var c in _queue)
            foreach (var g in c.Queue!.Games)
                counts[g.Id] = counts.GetValueOrDefault(g.Id) + 1;
        return counts;
    }

    private void BroadcastCounts()
    {
        var countsByMode = CurrentCountsByMode();
        ClientState[] snapshot;
        lock (_gate) snapshot = _clients.Values.ToArray();
        foreach (var c in snapshot)
        {
            var mode = c.Queue?.Mode ?? "any";
            var filtered = new Dictionary<string, int>();
            foreach (var kv in countsByMode)
            {
                int total = 0;
                foreach (var modeKv in kv.Value)
                {
                    if (ModesCompatible(mode, modeKv.Key))
                        total += modeKv.Value;
                }
                if (total > 0) filtered[kv.Key] = total;
            }
            _ = c.Connection.TrySendAsync(new CountsMsg { Counts = filtered });
        }
    }

    public Session? FindSessionByTag(byte[] tag)
    {
        lock (_gate)
            foreach (var s in _sessions.Values)
                if (tag.AsSpan().SequenceEqual(s.Tag))
                    return s;
        return null;
    }

    private static string NewSessionId()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(Proto.SessionIdBytes)).ToLowerInvariant();
}
