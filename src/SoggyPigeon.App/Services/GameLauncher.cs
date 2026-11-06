using System.Diagnostics;
using SoggyPigeon.App.Infrastructure;
using SoggyPigeon.App.ViewModels;
using SoggyPigeon.Protocol;

namespace SoggyPigeon.App.Services;

/// <summary>What the app is doing with a booted game, mirrored to the Match page.</summary>
public enum LiveState { Idle, Booting, InGame, Finished }

/// <summary>
/// Launches the game with the frozen boot handoff (§6.3) and polls the status
/// file the SDK writes. One instance per match.
/// </summary>
public sealed class GameLauncher : ObservableObject
{
    private Process? _process;
    private CancellationTokenSource? _pollCts;

    public LiveState State { get; private set; } = LiveState.Idle;
    public StatusFile.Snapshot? Latest { get; private set; }

    public event Action<StatusFile.Snapshot>? StatusChanged;
    public event Action<StatusFile.Snapshot>? GameExited;   // final snapshot + exit code known

    public string ExePath { get; private set; } = "";
    /// <summary>Temp status file this launcher polls (deleted on Dispose).</summary>
    public string? StatusPath { get; private set; }

    /// <summary>Boot the game. Returns immediately; poll events carry progress.</summary>
    public bool Launch(BootInfo boot, InstallResult install, int role)
    {
        if (install.ExePath is null) return false;
        ExePath = install.ExePath;

        var statusPath = boot.StatusPath;
        StatusPath = statusPath;
        // seed the status file so the app never reads a stale one
        File.WriteAllText(statusPath, StatusFile.Encode(new StatusFile.Snapshot
        {
            State = StatusFile.MatchState.Searching,
            Message = "waiting for game",
        }));

        var installDir = install.InstallDir ?? Path.GetDirectoryName(install.ExePath) ?? ".";
        var psi = new ProcessStartInfo
        {
            FileName = install.ExePath,
            WorkingDirectory = installDir,
            UseShellExecute = false,
            // Capture the game's own output to files next to the status file:
            // the only way to diagnose a boot failure on a machine you're not
            // sitting at (buffered stdout is lost on kill otherwise).
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // Owner preference (default on): boot fullscreen. The framework's
        // runtime honours PSX_FULLSCREEN; other games ignore it harmlessly.
        psi.EnvironmentVariables["PSX_FULLSCREEN"] = AppServices.Data.StartFullscreen ? "1" : "0";

        var gameToml = Path.Combine(installDir, "game.toml");
        var frameworkPort = File.Exists(gameToml);
        if (frameworkPort)
        {
            // Framework port (psxrecomp): the session rides the framework's
            // own proven netplay stack (recomp-net, LAN direct). Soggy Pigeon
            // IS the lobby — it pairs the players, then hands the session
            // over as boot args. No lobby server, no launcher UI, no disc
            // wizard (PSX_NO_LAUNCHER + --game + --disc). The status shim
            // inside the game writes the §6.3 status file from the
            // framework's netplay state.
            psi.ArgumentList.Add("--game");
            psi.ArgumentList.Add(gameToml);
            psi.EnvironmentVariables["PSX_NO_LAUNCHER"] = "1";
            psi.ArgumentList.Add("--net-bind");
            psi.ArgumentList.Add($"0.0.0.0:{boot.LocalPort}");
            // Without a real punched endpoint the framework cannot link
            // LAN-direct — abort rather than launch a session that can
            // never connect ("-" is the punch-failed placeholder).
            if (string.IsNullOrWhiteSpace(boot.Endpoint) || boot.Endpoint == "-")
            {
                try { File.Delete(statusPath); } catch { }   // don't litter temp
                return false;
            }
            psi.ArgumentList.Add("--net-peer");
            psi.ArgumentList.Add(boot.Endpoint);
            psi.ArgumentList.Add("--net-slot");
            psi.ArgumentList.Add((role - 1).ToString());
            psi.ArgumentList.Add("--net-input-player");
            psi.ArgumentList.Add("0");
            // Reconciled session parameters (the server merged both players'
            // preferences). The delay switch exists upstream today; the
            // digest-cadence switch is future-proofing — current builds
            // ignore it and prove every frame.
            psi.ArgumentList.Add("--net-delay");
            psi.ArgumentList.Add(boot.Delay.ToString());
            if (boot.DigestEvery > 1)
            {
                psi.ArgumentList.Add("--net-digest-every");
                psi.ArgumentList.Add(boot.DigestEvery.ToString());
            }
            // --net-session-id is a uint32 both peers must share: FNV-1a the
            // session string.
            uint sessionId = 2166136261u;
            foreach (var ch in boot.Session) { sessionId ^= ch; sessionId *= 16777619u; }
            psi.ArgumentList.Add("--net-session-id");
            psi.ArgumentList.Add(sessionId.ToString());
            var discLine = File.ReadAllLines(gameToml)
                .FirstOrDefault(l => l.TrimStart().StartsWith("disc ", StringComparison.Ordinal));
            if (discLine is not null)
            {
                var eq = discLine.IndexOf('=');
                var discPath = eq > 0 ? discLine[(eq + 1)..].Trim().Trim('"') : "";
                if (discPath.Length > 0 && File.Exists(discPath))
                {
                    psi.ArgumentList.Add("--disc");
                    psi.ArgumentList.Add(discPath);
                }
            }
        psi.EnvironmentVariables["SOGGY_PIGEON_STATUS"] = statusPath;
        psi.EnvironmentVariables["SOGGY_PIGEON_ROLE"] = role == 1 ? "host" : "join";
            // Owner-curated opening (NETPLAY_START_STATE.md): a saved machine
            // state next to the game is loaded at netplay boot on BOTH peers,
            // starting the match exactly at the owner's chosen point.
            var startScript = Path.Combine(installDir, "saves", "netplay-start.state");
            if (File.Exists(startScript))
            {
                psi.EnvironmentVariables["SOGGY_NETPLAY_STATE"] = startScript;
                // The start state arms inside the rollback engine
                // (psx_netplay_rb_start reads it when building RNetRbConfig),
                // which only runs in rollback mode. Default is delay-sync —
                // request rollback whenever a start state is present so the
                // loader actually engages (both peers get the same env).
                psi.EnvironmentVariables["PSX_NET_MODE"] = "rollback";
            }
            // Sync-proof cadence: Pong's runtime (our build) samples the CPU
            // authority raster every Nth sim tick when the title is proven
            // GPUREAD-free — the reconciled session value drives both peers.
            if (boot.DigestEvery > 1)
                psi.EnvironmentVariables["PSX_AUTHORITY_EVERY"] = boot.DigestEvery.ToString();
        }
        else
        {
            // libpigeon ports (the mock games): the original §6.3 boot handoff.
            foreach (var arg in BootArgs.Build(boot))
                psi.ArgumentList.Add(arg);
        }
        try
        {
            _process = Process.Start(psi);
        }
        catch
        {
            try { File.Delete(statusPath); } catch { }   // don't litter temp
            return false;
        }
        if (_process is null) return false;

        // Pump the game's output into log files (one per stream, timestamped
        // per line) so a remote boot failure can be read after the fact.
        StartOutputPump(_process.StandardOutput, statusPath + ".game-out.log");
        StartOutputPump(_process.StandardError, statusPath + ".game-err.log");

        State = LiveState.Booting;
        Raise(nameof(State));
        var cts = new CancellationTokenSource();
        _pollCts = cts;
        // capture the local: a cancel racing Launch can Dispose/null _pollCts
        // between these lines, and the task evaluates the field when it runs
        _ = Task.Run(() => PollLoopAsync(statusPath, _process, cts.Token));
        return true;
    }

    private static void StartOutputPump(System.IO.StreamReader reader, string logPath)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var writer = new StreamWriter(logPath, append: false);
                writer.AutoFlush = true;
                while (await reader.ReadLineAsync() is { } line)
                    await writer.WriteLineAsync(
                        $"{DateTime.Now:HH:mm:ss.fff} {line}");
            }
            catch { /* process gone or stream closed */ }
        });
    }

    /// <summary>Reads the status file; null while the SDK is mid-rewrite
    /// (tmp+rename makes the window tiny — caller retries or skips).</summary>
    private static string? TryReadStatus(string statusPath)
    {
        try { return File.ReadAllText(statusPath); }
        catch (IOException) { return null; }
    }

    private async Task PollLoopAsync(string statusPath, Process process, CancellationToken ct)
    {
        string? lastText = null;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / Proto.StatusPollHz));
            while (await timer.WaitForNextTickAsync(ct))
            {
                var text = TryReadStatus(statusPath);
                if (text is null) continue; // SDK mid-rewrite; next tick

                if (!string.Equals(text, lastText, StringComparison.Ordinal))
                {
                    lastText = text;
                    var snap = StatusFile.Parse(text);
                    Latest = snap;
                    State = LiveState.InGame;
                    Raise(nameof(State));
                    StatusChanged?.Invoke(snap);
                }

                if (!ct.IsCancellationRequested && process.HasExited) break;
            }
        }
        // InvalidOperationException: Dispose() cancels then disposes the
        // Process; a poll tick between the two throws on the disposed object
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException) { }

        if (ct.IsCancellationRequested) return;   // cancelled: the caller reset, don't report a phantom exit
        // final read: the game's last status write may postdate the final poll
        // tick by up to a second — the recorded result must match the file
        var finalText = TryReadStatus(statusPath);
        if (finalText is not null && !string.Equals(finalText, lastText, StringComparison.Ordinal))
            Latest = StatusFile.Parse(finalText);
        var finalSnap = Latest ?? new StatusFile.Snapshot();
        State = LiveState.Finished;
        Raise(nameof(State));
        GameExited?.Invoke(finalSnap);
    }

    public void Dispose()
    {
        _pollCts?.Cancel();
        try { if (StatusPath is { } sp && File.Exists(sp)) File.Delete(sp); } catch { }
        _pollCts?.Dispose();
        _pollCts = null;
        try { if (_process is { HasExited: false } p) p.Kill(); } catch { }
        _process?.Dispose();
        _process = null;
    }
}
