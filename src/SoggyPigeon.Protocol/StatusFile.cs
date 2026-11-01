using System.Globalization;

namespace SoggyPigeon.Protocol;

/// <summary>
/// The status file — the "out" half of the frozen game↔app interface (§6.3).
/// libpigeon rewrites the whole file (key value lines, UTF-8); the app polls it.
/// </summary>
public static class StatusFile
{
    public enum MatchState { Searching, Connected, Loading, Fight, Result, Exiting }

    public sealed class Snapshot
    {
        public MatchState State { get; init; } = MatchState.Searching;
        public int PingMs { get; init; }
        /// <summary>Rounds won p1 / p2.</summary>
        public int RoundP1 { get; init; }
        public int RoundP2 { get; init; }
        /// <summary>0 none, 1/2 winner, -1 draw.</summary>
        public int Winner { get; init; }
        public bool Disconnected { get; init; }
        public string Message { get; init; } = "";
        public long ReadTicks { get; init; }
    }

    /// <summary>Encode a snapshot to the exact lines the SDK writes.</summary>
    public static string Encode(Snapshot s)
    {
        var lines = new List<string>
        {
            $"state {StateName(s.State)}",
            $"ping {Inv(s.PingMs)}",
            $"round {Inv(s.RoundP1)} {Inv(s.RoundP2)}",
            $"winner {Inv(s.Winner)}",
        };
        if (s.Disconnected) lines.Add("disconnected 1");
        if (s.Message.Length > 0) lines.Add($"message {s.Message}");
        return string.Join("\n", lines) + "\n";
    }

    /// <summary>Parse status file content; malformed lines are skipped.</summary>
    public static Snapshot Parse(string content)
    {
        var state = MatchState.Searching;
        var ping = 0; var r1 = 0; var r2 = 0; var winner = 0;
        var disconnected = false; var message = "";
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;
            var sp = line.IndexOf(' ');
            var key = sp < 0 ? line : line[..sp];
            var val = sp < 0 ? "" : line[(sp + 1)..].Trim();
            switch (key)
            {
                case "state": state = ParseState(val); break;
                case "ping": ping = ParseInt(val); break;
                case "round":
                    var parts = val.Split(' ');
                    if (parts.Length == 2)
                    {
                        r1 = ParseInt(parts[0]);
                        r2 = ParseInt(parts[1]);
                    }
                    break;
                case "winner": winner = ParseInt(val); break;
                case "disconnected": disconnected = true; break;
                case "message": message = val; break;
            }
        }
        return new Snapshot
        {
            State = state, PingMs = ping, RoundP1 = r1, RoundP2 = r2,
            Winner = winner, Disconnected = disconnected, Message = message,
            ReadTicks = Environment.TickCount64,
        };
    }

    public static string StateName(MatchState s) => s switch
    {
        MatchState.Searching => "searching",
        MatchState.Connected => "connected",
        MatchState.Loading => "loading",
        MatchState.Fight => "fight",
        MatchState.Result => "result",
        MatchState.Exiting => "exiting",
        _ => "searching",
    };

    private static MatchState ParseState(string v) => v switch
    {
        "connected" => MatchState.Connected,
        "loading" => MatchState.Loading,
        "fight" => MatchState.Fight,
        "result" => MatchState.Result,
        "exiting" => MatchState.Exiting,
        _ => MatchState.Searching,
    };
    private static string Inv(int v) => v.ToString(CultureInfo.InvariantCulture);

    private static int ParseInt(string s)
        => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

}
