namespace SoggyPigeon.Protocol;

/// <summary>
/// The boot handoff (design doc §6.3, frozen v1): the exact command-line
/// arguments Soggy Pigeon launches a game with. The C SDK parses these too —
/// keep the spelling byte-identical to sdk/libpigeon/pigeon.h.
/// </summary>
public sealed class BootInfo
{
    public string Session { get; set; } = "";
    /// <summary>Datagram session tag, hex (SessionTag.Hex) — lets the C SDK
    /// address the relay without hashing.</summary>
    public string Tag { get; set; } = "";
    /// <summary>"host" (role 1) or "join" (role 2).</summary>
    public string Role { get; set; } = "join";
    /// <summary>Peer endpoint "ip:port" for direct play; "-" until punch succeeds.</summary>
    public string Endpoint { get; set; } = "-";
    /// <summary>Relay endpoint "ip:port", or "-" for none.</summary>
    public string Relay { get; set; } = "-";
    /// <summary>"rematch" or "single".</summary>
    public string Policy { get; set; } = "rematch";
    /// <summary>Game-specific params "k=v;k=v".</summary>
    public string Params { get; set; } = "";
    /// <summary>Path of the status file the SDK writes.</summary>
    public string StatusPath { get; set; } = "";
    /// <summary>Local UDP port the game should bind (the punched port).</summary>
    public int LocalPort { get; set; }
    /// <summary>Reconciled session-wide rollback input delay (frames).</summary>
    public int Delay { get; set; } = 2;
    /// <summary>Reconciled session-wide sync-proof cadence (1 = every frame).</summary>
    public int DigestEvery { get; set; } = 1;

    public int RoleNumber => Role == "host" ? 1 : 2;
}

public static class BootArgs
{
    public const int ProtoVersion = 1;

    /// <summary>Build the full argument list, prefixed with --pigeon-proto 1.</summary>
    public static string[] Build(BootInfo info)
    {
        var args = new List<string>
        {
            "--pigeon-proto", ProtoVersion.ToString(),
            "--pigeon-session", info.Session,
            "--pigeon-tag", info.Tag,
            "--pigeon-role", info.Role,
            "--pigeon-endpoint", info.Endpoint,
            "--pigeon-relay", info.Relay,
            "--pigeon-policy", info.Policy,
            "--pigeon-status", info.StatusPath,
            "--pigeon-local-port", info.LocalPort.ToString(),
        };
        if (info.Params.Length > 0)
            args.AddRange(new[] { "--pigeon-params", info.Params });
        return args.ToArray();
    }

    /// <summary>
    /// Parse --pigeon-* arguments out of a full command line. Foreign arguments
    /// (the game's own) are ignored; unknown --pigeon-* arguments are kept as
    /// extras so a v1 parser never chokes on a newer peer.
    /// </summary>
    public static BootInfo Parse(IEnumerable<string> args)
    {
        var info = new BootInfo();
        var extras = new List<string>();
        string? pending = null;
        foreach (var raw in args)
        {
            if (pending is not null) { Assign(info, pending, raw, extras); pending = null; continue; }
            if (raw.StartsWith("--pigeon-")) { pending = raw; continue; }
            // foreign game argument — ignored
        }
        return info;
    }

    private static void Assign(BootInfo info, string flag, string value, List<string> extras)
    {
        switch (flag)
        {
            case "--pigeon-proto":
                if (int.TryParse(value, out var v) && v != ProtoVersion)
                    throw new NotSupportedException($"pigeon boot proto {v} not supported (this build: {ProtoVersion})");
                break;
            case "--pigeon-session": info.Session = value; break;
            case "--pigeon-tag": info.Tag = value; break;
            case "--pigeon-role": info.Role = value; break;
            case "--pigeon-endpoint": info.Endpoint = value; break;
            case "--pigeon-relay": info.Relay = value; break;
            case "--pigeon-policy": info.Policy = value; break;
            case "--pigeon-params": info.Params = value; break;
            case "--pigeon-status": info.StatusPath = value; break;
            case "--pigeon-local-port": info.LocalPort = int.TryParse(value, out var p) ? p : 0; break;
            default: extras.Add($"{flag}={value}"); break;
        }
    }
}
