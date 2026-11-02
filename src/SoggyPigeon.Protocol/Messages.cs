using System.Text.Json.Serialization;

namespace SoggyPigeon.Protocol;

// ---- shared DTOs ---------------------------------------------------------

public sealed class GameHints
{
    /// <summary>Folder candidates for install scan. Env vars (%LOCALAPPDATA%) expanded.</summary>
    public string[] Folders { get; set; } = System.Array.Empty<string>();

    /// <summary>Executable names to look for inside a candidate folder.</summary>
    public string[] Exe { get; set; } = System.Array.Empty<string>();
}

public sealed class ManifestGame
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Supported build fingerprint. The install must carry a pigeon.id
    /// file (next to the exe) whose content equals this to be usable.</summary>
    public string Fingerprint { get; set; } = "";
    public GameHints Hints { get; set; } = new();
    /// <summary>Game-specific param keys this game requires on the boot line
    /// (e.g. "laps", "class" for MockKart; "chars", "ismode" for HSFA's Hyper
    /// mix). Both paired players must have manifest-compatible settings; v1
    /// leaves them to the manifest.</summary>
    public string[] Params { get; set; } = System.Array.Empty<string>();
    /// <summary>The param values the server hands both players on the boot
    /// line ("k=v;k=v"). v1 has no per-player param selection, so both sides
    /// of a match receive the same string.</summary>
    public string ParamsDefault { get; set; } = "";
    public string? Icon { get; set; }
}

public sealed class ManifestDoc
{
    public string Proto { get; set; } = SoggyPigeon.Protocol.Proto.Name;
    public int Rev { get; set; }
    public ManifestGame[] Games { get; set; } = System.Array.Empty<ManifestGame>();
}

/// <summary>A game the client puts into its search set: id + installed fingerprint.</summary>
public sealed class QueueGame
{
    public QueueGame() { }
    public QueueGame(string id, string fingerprint) { Id = id; Fingerprint = fingerprint; }
    public string Id { get; set; } = "";
    public string Fingerprint { get; set; } = "";
}

// ---- client -> server ----------------------------------------------------

public sealed class HelloMsg
{
    public string T { get; } = "hello";
    public string Proto { get; set; } = SoggyPigeon.Protocol.Proto.Name;
    public string Ver { get; set; } = SoggyPigeon.Protocol.Proto.ClientVersion;
    /// <summary>Random device token (hex). The server's identity key.</summary>
    public string Token { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class ManifestReqMsg { public string T { get; } = "manifest_req"; }

public sealed class EnterQueueMsg
{
    public string T { get; } = "enter_queue";
    public QueueGame[] Games { get; set; } = System.Array.Empty<QueueGame>();
    /// <summary>"any" (default — first available match of any type), "casual".</summary>
    public string Mode { get; set; } = "casual";
    /// <summary>"rematch" (session) or "single".</summary>
    public string Policy { get; set; } = "rematch";
    /// <summary>Preferred rollback input delay in frames (1..8; default 2).
    /// A session parameter: the server reconciles both players' preferences
    /// (higher wins) so both peers run the same value.</summary>
    public int Delay { get; set; } = 2;
    /// <summary>Preferred sync-proof cadence: prove every Nth frame (1..10;
    /// 1 = prove every frame). Session parameter — server reconciles
    /// (lower/more-proving wins).</summary>
    public int DigestEvery { get; set; } = 1;
}

public sealed class LeaveQueueMsg { public string T { get; } = "leave_queue"; }

public sealed class HeartbeatMsg { public string T { get; } = "heartbeat"; }

public sealed class PlayingMsg
{
    public string T { get; } = "playing";
    public string Session { get; set; } = "";
}

public sealed class ResultMsg
{
    public string T { get; } = "result";
    public string Session { get; set; } = "";
    /// <summary>"win" | "loss" | "draw" | "disconnect"</summary>
    public string Outcome { get; set; } = "draw";
    public double DurationS { get; set; }
}

// ---- server -> client ----------------------------------------------------

public sealed class WelcomeMsg
{
    public string T { get; } = "welcome";
    public int Online { get; set; }
    public Dictionary<string, int> Counts { get; set; } = new();
}

public sealed class ManifestMsg
{
    public string T { get; } = "manifest";
    public ManifestDoc Doc { get; set; } = new();
}

public sealed class QueuedMsg
{
    public string T { get; } = "queued";
    public Dictionary<string, int> Counts { get; set; } = new();
}

public sealed class CountsMsg
{
    public string T { get; } = "counts";
    public Dictionary<string, int> Counts { get; set; } = new();
}

public sealed class PairedMsg
{
    public string T { get; } = "paired";
    public string Session { get; set; } = "";
    public string Game { get; set; } = "";
    /// <summary>Server-authoritative: 1 = host, 2 = join.</summary>
    public int Role { get; set; }
    /// <summary>Shared RNG seed for the match.</summary>
    public uint Seed { get; set; }
    public string Policy { get; set; } = "rematch";
    public string OpponentName { get; set; } = "";
    /// <summary>"ip:port" of the server's relay endpoint ('-' if none).</summary>
    public string Relay { get; set; } = "-";
    /// <summary>Game-specific params "k=v;k=v" for the boot line.</summary>
    public string Params { get; set; } = "";
    /// <summary>Reconciled rollback input delay for this session (frames).</summary>
    public int Delay { get; set; } = 2;
    /// <summary>Reconciled sync-proof cadence for this session (1 = every frame).</summary>
    public int DigestEvery { get; set; } = 1;
}

public sealed class PeerMsg
{
    public string T { get; } = "peer";
    public string Session { get; set; } = "";
    /// <summary>Peer's public [ip, port] observed at the rendezvous.</summary>
    public string[] Public { get; set; } = System.Array.Empty<string>();
    /// <summary>True when both sides share a public address (LAN shortcut).</summary>
    public bool SameNat { get; set; }
}

public sealed class SessionEndMsg
{
    public string T { get; } = "session_end";
    public string Session { get; set; } = "";
    public string Reason { get; set; } = "";
}

public sealed class ErrorMsg
{
    public string T { get; } = "error";
    /// <summary>"version_mismatch" | "bad_token" | "bad_queue" | "unknown" ...</summary>
    public string Kind { get; set; } = "unknown";
    public string Detail { get; set; } = "";
}

// ---- lobbies (§3.7) -------------------------------------------------------

/// <summary>One open lobby as shown in the room list.</summary>
public sealed class LobbyRoomInfo
{
    public string Name { get; set; } = "";
    public string Game { get; set; } = "";
    public int Count { get; set; }
    public bool Full { get; set; }
    /// <summary>Short join code so a friend can find this specific room.</summary>
    public string Code { get; set; } = "";
}

/// <summary>One lobby member, in line order. The first entry is the player
/// Staying on (§3.7); the rest are Challengers front-to-back.</summary>
public sealed class LobbyMemberInfo
{
    public string Name { get; set; } = "";
    public bool You { get; set; }
    public bool StayingOn { get; set; }
}

// client -> server

public sealed class CreateLobbyMsg
{
    public string T { get; } = "create_lobby";
    public string Game { get; set; } = "";
    /// <summary>Room name; server falls back to "<name> lobby".</summary>
    public string Name { get; set; } = "";
    /// <summary>Preferred rollback input delay (frames) for lobby matches.</summary>
    public int Delay { get; set; } = 2;
    /// <summary>Preferred sync-proof cadence for lobby matches.</summary>
    public int DigestEvery { get; set; } = 1;
}

public sealed class JoinLobbyMsg
{
    public string T { get; } = "join_lobby";
    public string Code { get; set; } = "";
}

public sealed class LeaveLobbyMsg { public string T { get; } = "leave_lobby"; }

/// <summary>Answer to a Ready prompt ("You're up"). Carries the sender's
/// session-parameter preferences, reconciled like queue preferences.</summary>
public sealed class LobbyReadyMsg
{
    public string T { get; } = "lobby_ready";
    public int Delay { get; set; } = 2;
    public int DigestEvery { get; set; } = 1;
}

// server -> client

/// <summary>The full state of the lobby you are in; sent to every member on
/// each change. Outside a match the first member is Staying on and no pair
/// is playing; during a match Pair holds the two players' names.</summary>
public sealed class LobbyStateMsg
{
    public string T { get; } = "lobby_state";
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Game { get; set; } = "";
    public string Code { get; set; } = "";
    public LobbyMemberInfo[] Members { get; set; } = System.Array.Empty<LobbyMemberInfo>();
    public bool Playing { get; set; }
    /// <summary>The two names currently playing, [0] = Staying on (P1).</summary>
    public string[] Pair { get; set; } = System.Array.Empty<string>();
    /// <summary>True when the receiving client created this lobby.</summary>
    public bool Creator { get; set; }
    /// <summary>True when the receiving client currently has an outstanding
    /// Ready prompt (they're up next).</summary>
    public bool YourTurn { get; set; }
}

/// <summary>All open lobbies; broadcast to everyone on any change.</summary>
public sealed class LobbyListMsg
{
    public string T { get; } = "lobby_list";
    public LobbyRoomInfo[] Rooms { get; set; } = System.Array.Empty<LobbyRoomInfo>();
}

/// <summary>Your turn to challenge: the Ready prompt. The server skips the
/// player to the back of the line if no lobby_ready arrives in time.</summary>
public sealed class YourTurnMsg
{
    public string T { get; } = "your_turn";
    public string LobbyId { get; set; } = "";
}

/// <summary>The lobby is gone (creator closed it, or it emptied).</summary>
public sealed class LobbyEndMsg
{
    public string T { get; } = "lobby_end";
    public string Reason { get; set; } = "";
}

// ---- codec ---------------------------------------------------------------

public static class PigeonMessages
{
    /// <summary>All concrete message types, for decoding.</summary>
    public static readonly Dictionary<string, Type> Types = new()
    {
        ["hello"] = typeof(HelloMsg),
        ["manifest_req"] = typeof(ManifestReqMsg),
        ["enter_queue"] = typeof(EnterQueueMsg),
        ["leave_queue"] = typeof(LeaveQueueMsg),
        ["heartbeat"] = typeof(HeartbeatMsg),
        ["playing"] = typeof(PlayingMsg),
        ["result"] = typeof(ResultMsg),
        ["welcome"] = typeof(WelcomeMsg),
        ["manifest"] = typeof(ManifestMsg),
        ["queued"] = typeof(QueuedMsg),
        ["counts"] = typeof(CountsMsg),
        ["paired"] = typeof(PairedMsg),
        ["peer"] = typeof(PeerMsg),
        ["session_end"] = typeof(SessionEndMsg),
        ["error"] = typeof(ErrorMsg),
        ["create_lobby"] = typeof(CreateLobbyMsg),
        ["join_lobby"] = typeof(JoinLobbyMsg),
        ["leave_lobby"] = typeof(LeaveLobbyMsg),
        ["lobby_ready"] = typeof(LobbyReadyMsg),
        ["lobby_state"] = typeof(LobbyStateMsg),
        ["lobby_list"] = typeof(LobbyListMsg),
        ["your_turn"] = typeof(YourTurnMsg),
        ["lobby_end"] = typeof(LobbyEndMsg),
    };
}
