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
    /// <summary>The lobby this player is in, or null (§3.7 — one activity at a time).</summary>
    public Lobby? Lobby { get; set; }
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
    /// <summary>Set when this session is a lobby match; the lobby's line
    /// is re-ordered when the session ends (§3.7 winner stays on).</summary>
    public Lobby? Lobby { get; set; }
}

/// <summary>An arcade room (§3.7): one cabinet — Members[0] is the player
/// Staying on, the rest are Challengers front-to-back. One match runs at a
/// time; the front Challenger gets a Ready prompt when the cabinet is free.</summary>
public sealed class Lobby
{
    public required string Id { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public required string Game { get; init; }
    public required ClientState Creator { get; init; }
    public required List<ClientState> Members { get; init; } = new();
    /// <summary>A lobby session is running (the pair is Members[0] + PromptedFor).</summary>
    public bool InMatch { get; set; }
    /// <summary>The Challenger with an outstanding Ready prompt, if any.</summary>
    public ClientState? Prompted { get; set; }
    public long PromptTicks { get; set; }
}

/// <summary>
/// Authoritative matchmaking state (§6.1). One global lock: v1 scale is tiny,
/// and pairing must be atomic — a single gate is the simplest correct version.
/// </summary>
public sealed class Hub
{
    /// <summary>Faces the internet: an unbounded queue lets one client stall the pairing scan.</summary>
    public const int MaxQueueSize = 500;
    /// <summary>One Staying on + eleven Challengers (§3.7).</summary>
    public const int MaxLobbyMembers = 12;
    /// <summary>Ignore the Ready prompt this long and you are Skipped to the
    /// back. A field (not const) so tests can shorten it.</summary>
    public static int ReadyTimeoutSeconds = 30;

    private readonly object _gate = new();
    private readonly Dictionary<string, ClientState> _clients = new();     // by token
    private readonly List<ClientState> _queue = new();
    private readonly Dictionary<string, Session> _sessions = new();        // by session id
    private readonly Dictionary<string, Lobby> _lobbies = new();           // by lobby id
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
        LobbyRoomInfo[] rooms;
        lock (_gate)
        {
            _clients[client.Token] = client;
            rooms = LobbyListSnapshotLocked();
        }
        // a late joiner would otherwise never see open rooms: the list is
        // broadcast on CHANGE only, so hand the current one over on connect
        _ = client.Connection.TrySendAsync(new LobbyListMsg { Rooms = rooms });
    }

    public void ClientDisconnected(ClientState client)
    {
        lock (_gate)
        {
            _clients.Remove(client.Token);
            RemoveFromQueueLocked(client);
            if (client.Session is { } s && !s.Ended)
                EndSessionLocked(s, "peer_disconnected", notifyIndex: OtherRole(s, client));
            if (client.Lobby is { } lobby)
                LeaveLobbyLocked(client, notify: false);
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

            // §3.7 one activity at a time: searching leaves any lobby
            if (client.Lobby is { }) LeaveLobbyLocked(client, notify: true);

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
            {
                EndSessionLocked(s, "complete", notifyIndex: null);
                return;
            }
            // single result: if the match never fully started (either side
            // never launched its game), this is a boot failure or pre-launch
            // cancel — end it now instead of pinning the session waiting for
            // a second result that can only come from a game stuck at a menu
            if (!(s.Playing[0] && s.Playing[1]))
            {
                var waiting = s.Results[0] is null ? 0 : 1;
                EndSessionLocked(s, "opponent could not start the game", notifyIndex: waiting);
            }
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
            SweepLobbyPromptsLocked(Environment.TickCount64);
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
        // §3.7: a lobby match re-orders the line when it ends
        if (s.Lobby is { } lobby && _lobbies.ContainsKey(lobby.Id))
            LobbyMatchEndedLocked(lobby, s);
    }

    // ---- lobbies (§3.7) -----------------------------------------------------

    /// <summary>Unambiguous alphabet for spoken/hand-typed join codes.</summary>
    private const string LobbyCodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public void HandleCreateLobby(ClientState client, CreateLobbyMsg msg)
    {
        lock (_gate)
        {
            if (client.Session is not null)
            {
                Error(client, "in_session", "finish the current session first");
                return;
            }
            if (_manifest.Current.Games.All(m => m.Id != msg.Game))
            {
                Error(client, "bad_lobby", $"unknown game '{msg.Game}'");
                return;
            }
            if (client.Lobby is { }) LeaveLobbyLocked(client, notify: false);
            RemoveFromQueueLocked(client);

            var name = msg.Name.Trim();
            if (name.Length == 0) name = $"{client.Name} lobby";
            if (name.Length > 40) name = name[..40];

            var lobby = new Lobby
            {
                Id = NewSessionId(),
                Code = NewLobbyCodeLocked(),
                Name = name,
                Game = msg.Game,
                Creator = client,
                Members = new List<ClientState> { client },
            };
            client.Lobby = lobby;
            _lobbies[lobby.Id] = lobby;
            SendLobbyStateLocked(lobby);
        }
        BroadcastLobbyList();
    }

    public void HandleJoinLobby(ClientState client, JoinLobbyMsg msg)
    {
        lock (_gate)
        {
            if (client.Session is not null)
            {
                Error(client, "in_session", "finish the current session first");
                return;
            }
            var code = msg.Code.Trim().ToUpperInvariant();
            Lobby? lobby = null;
            foreach (var l in _lobbies.Values)
                if (l.Code == code) { lobby = l; break; }
            if (lobby is null)
            {
                Error(client, "bad_lobby", $"no lobby with code '{msg.Code}'");
                return;
            }
            if (lobby.Members.Count >= MaxLobbyMembers)
            {
                Error(client, "lobby_full", "that lobby is full");
                return;
            }
            if (client.Lobby == lobby) return;
            if (client.Lobby is { }) LeaveLobbyLocked(client, notify: false);
            RemoveFromQueueLocked(client);

            lobby.Members.Add(client);
            client.Lobby = lobby;
            SendLobbyStateLocked(lobby);
            LobbyPromptNextLocked(lobby);
        }
        BroadcastLobbyList();
    }

    public void HandleLeaveLobby(ClientState client)
    {
        lock (_gate)
        {
            if (client.Lobby is not { } lobby) return;
            // §3.7: the creator leaving CLOSES the lobby (members returned to
            // idle); a regular member simply leaves the line
            if (ReferenceEquals(client, lobby.Creator))
            {
                foreach (var m in lobby.Members.ToArray())
                {
                    m.Lobby = null;
                    _ = m.Connection.TrySendAsync(new LobbyEndMsg { Reason = "closed" });
                }
                lobby.Members.Clear();
                lobby.Prompted = null;
                _lobbies.Remove(lobby.Id);
            }
            else
                LeaveLobbyLocked(client, notify: true);
        }
        BroadcastLobbyList();
    }

    public void HandleLobbyReady(ClientState client, LobbyReadyMsg msg)
    {
        lock (_gate)
        {
            var lobby = client.Lobby;
            if (lobby is null || lobby.Prompted != client) return;
            lobby.Prompted = null;
            StartLobbyMatchLocked(lobby, client,
                Math.Clamp(msg.Delay, 1, 8), Math.Clamp(msg.DigestEvery, 1, 10));
            SendLobbyStateLocked(lobby);
        }
        BroadcastLobbyList();
    }

    /// <summary>Remove a player from their lobby. The holder leaving between
    /// matches hands the cabinet to the head of the line (§3.7 inherit); a
    /// holder leaving mid-match has their seat decided by the result routing.</summary>
    private void LeaveLobbyLocked(ClientState client, bool notify)
    {
        var lobby = client.Lobby;
        if (lobby is null) return;
        client.Lobby = null;
        var idx = lobby.Members.IndexOf(client);
        if (idx >= 0) lobby.Members.RemoveAt(idx);
        if (lobby.Prompted == client) lobby.Prompted = null;
        if (lobby.Members.Count == 0)
        {
            _lobbies.Remove(lobby.Id);
            if (notify)
                _ = client.Connection.TrySendAsync(new LobbyEndMsg { Reason = "closed" });
            return;
        }
        if (notify)
            SendLobbyStateLocked(lobby);
        LobbyPromptNextLocked(lobby);
        SendLobbyStateLocked(lobby);
    }

    /// <summary>The cabinet is free and someone is waiting: prompt the front
    /// Challenger. Holder is presumed ready (§3.7 — no prompt for them).</summary>
    private void LobbyPromptNextLocked(Lobby lobby)
    {
        if (lobby.InMatch || lobby.Prompted is not null || lobby.Members.Count < 2) return;
        lobby.Prompted = lobby.Members[1];
        lobby.PromptTicks = Environment.TickCount64;
        _ = lobby.Prompted.Connection.TrySendAsync(new YourTurnMsg { LobbyId = lobby.Id });
        // the state must carry YourTurn=true for the READY button to appear —
        // the join-time state predates the prompt, so resend to everyone
        SendLobbyStateLocked(lobby);
    }

    private void StartLobbyMatchLocked(Lobby lobby, ClientState challenger, int delay, int digest)
    {
        var holder = lobby.Members[0];
        var manifestGame = _manifest.Current.Games.FirstOrDefault(m => m.Id == lobby.Game);
        var id = NewSessionId();
        var session = new Session
        {
            Id = id,
            Tag = SessionTag.Compute(id),
            Game = lobby.Game,
            Seed = (uint)_rng.Next(1, int.MaxValue),
            Clients = new[] { holder, challenger },
            // the lobby IS the policy (§3.7): both sides stay in the flow
            Policies = new[] { "rematch", "rematch" },
            Params = manifestGame?.ParamsDefault ?? "",
            Delay = delay,
            DigestEvery = digest,
            Lobby = lobby,
        };
        holder.Session = session;
        challenger.Session = session;
        _sessions[session.Id] = session;
        lobby.InMatch = true;
        SendPaired(holder, challenger, session, role: 1);
        SendPaired(challenger, holder, session, role: 2);
    }

    /// <summary>Winner stays on: re-order the line from the match results and
    /// offer the cabinet to the next Challenger. No winner → same pair again.</summary>
    private void LobbyMatchEndedLocked(Lobby lobby, Session s)
    {
        lobby.InMatch = false;
        // winner by role index (holder = 0). "win" is explicit; a reported
        // "disconnect" loses; anything else (both draw / abandoned) replays.
        int? winner = null;
        if (s.Results[0] == "win") winner = 0;
        else if (s.Results[1] == "win") winner = 1;
        else if (s.Results[0] == "disconnect" && s.Results[1] != "win") winner = 1;
        else if (s.Results[1] == "disconnect" && s.Results[0] != "win") winner = 0;

        if (winner is { } w)
        {
            // the loser rejoins at the back — only if still a member
            var loser = s.Clients[1 - w];
            var li = lobby.Members.IndexOf(loser);
            if (li >= 0)
            {
                lobby.Members.RemoveAt(li);
                lobby.Members.Add(loser);
            }
            // the winner must hold the cabinet: move them to the front
            var wi = lobby.Members.IndexOf(s.Clients[w]);
            if (wi > 0)
            {
                lobby.Members.RemoveAt(wi);
                lobby.Members.Insert(0, s.Clients[w]);
            }
        }
        SendLobbyStateLocked(lobby);
        LobbyPromptNextLocked(lobby);
        SendLobbyStateLocked(lobby);
    }

    /// <summary>Ready-prompt expiry (checked by the 5 s sweep): the prompted
    /// Challenger is Skipped to the back and the next one is prompted.</summary>
    private void SweepLobbyPromptsLocked(long nowMs)
    {
        foreach (var lobby in _lobbies.Values)
        {
            if (lobby.Prompted is not { } p) continue;
            if (nowMs - lobby.PromptTicks < ReadyTimeoutSeconds * 1000L) continue;
            lobby.Prompted = null;
            var idx = lobby.Members.IndexOf(p);
            if (idx >= 0)
            {
                lobby.Members.RemoveAt(idx);
                lobby.Members.Add(p);
            }
            SendLobbyStateLocked(lobby);
            LobbyPromptNextLocked(lobby);
            SendLobbyStateLocked(lobby);
        }
    }

    private void SendLobbyStateLocked(Lobby lobby)
    {
        // pair names while a match runs: holder + whoever they play
        var playing = System.Array.Empty<string>();
        if (lobby.InMatch)
        {
            var pair = new List<string> { lobby.Members[0].Name };
            foreach (var m in lobby.Members)
                if (m.Session?.Lobby == lobby && m != lobby.Members[0]) { pair.Add(m.Name); break; }
            playing = pair.ToArray();
        }
        foreach (var m in lobby.Members)
        {
            var members = lobby.Members.Select(x => new LobbyMemberInfo
            {
                Name = x.Name,
                You = ReferenceEquals(x, m),
                StayingOn = ReferenceEquals(x, lobby.Members[0]),
            }).ToArray();
            _ = m.Connection.TrySendAsync(new LobbyStateMsg
            {
                Id = lobby.Id,
                Name = lobby.Name,
                Game = lobby.Game,
                Code = lobby.Code,
                Members = members,
                Playing = lobby.InMatch,
                Pair = playing,
                Creator = ReferenceEquals(m, lobby.Creator),
                YourTurn = ReferenceEquals(m, lobby.Prompted),
            });
        }
    }

    private LobbyRoomInfo[] LobbyListSnapshotLocked()
        => _lobbies.Values
            .Select(l => new LobbyRoomInfo
            {
                Name = l.Name,
                Game = l.Game,
                Count = l.Members.Count,
                Full = l.Members.Count >= MaxLobbyMembers,
                Code = l.Code,
            })
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private void BroadcastLobbyList()
    {
        LobbyRoomInfo[] rooms;
        lock (_gate) rooms = LobbyListSnapshotLocked();
        ClientState[] snapshot;
        lock (_gate) snapshot = _clients.Values.ToArray();
        foreach (var c in snapshot)
            _ = c.Connection.TrySendAsync(new LobbyListMsg { Rooms = rooms });
    }

    private string NewLobbyCodeLocked()
    {
        while (true)
        {
            var chars = new char[4];
            for (var i = 0; i < chars.Length; i++)
                chars[i] = LobbyCodeAlphabet[_rng.Next(LobbyCodeAlphabet.Length)];
            var code = new string(chars);
            var clash = false;
            foreach (var l in _lobbies.Values)
                if (l.Code == code) { clash = true; break; }
            if (!clash) return code;
        }
    }

    private static void Error(ClientState client, string kind, string detail)
        => _ = client.Connection.TrySendAsync(new ErrorMsg { Kind = kind, Detail = detail });

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
