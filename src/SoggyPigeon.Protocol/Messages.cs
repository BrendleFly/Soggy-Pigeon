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
    };
}
