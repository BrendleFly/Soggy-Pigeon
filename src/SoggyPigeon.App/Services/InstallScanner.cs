using SoggyPigeon.Protocol;

namespace SoggyPigeon.App.Services;

public enum InstallStatus { NotInstalled, VersionMismatch, Ok }

public sealed class InstallResult
{
    public ManifestGame Game { get; init; } = new();
    public InstallStatus Status { get; init; }
    /// <summary>Folder where the game exe lives (null when not found).</summary>
    public string? InstallDir { get; init; }
    /// <summary>Full path to the launchable executable.</summary>
    public string? ExePath { get; init; }
    public string? FoundFingerprint { get; init; }

    public string StatusText => Status switch
    {
        InstallStatus.Ok => "installed",
        InstallStatus.VersionMismatch => $"version differs: update (found {FoundFingerprint}, need {Game.Fingerprint})",
        _ => "not installed",
    };
}

/// <summary>Manifest-driven install detection (§6.2): hint folders + exe names,
/// verified by a pigeon.id fingerprint file next to the exe.</summary>
public static class InstallScanner
{
    public static InstallResult Scan(ManifestGame game, string? overrideDir)
    {
        foreach (var dir in CandidateDirs(game, overrideDir))
        {
            foreach (var exeName in game.Hints.Exe)
            {
                var exe = Path.Combine(dir, exeName);
                if (!File.Exists(exe)) continue;
                var idPath = Path.Combine(dir, "pigeon.id");
                // rescan runs on the UI thread — a transiently locked file
                // must not throw (Haiku round 4)
                string found;
                try
                {
                    found = File.Exists(idPath) ? File.ReadAllText(idPath).Trim() : "";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    found = "";
                }
                return new InstallResult
                {
                    Game = game,
                    Status = found == game.Fingerprint ? InstallStatus.Ok : InstallStatus.VersionMismatch,
                    InstallDir = dir,
                    ExePath = exe,
                    FoundFingerprint = found,
                };
            }
        }
        return new InstallResult { Game = game, Status = InstallStatus.NotInstalled };
    }

    private static IEnumerable<string> CandidateDirs(ManifestGame game, string? overrideDir)
    {
        if (overrideDir is not null) yield return overrideDir;
        foreach (var raw in game.Hints.Folders)
        {
            var expanded = Environment.ExpandEnvironmentVariables(raw);
            if (Directory.Exists(expanded)) yield return expanded;
        }
    }
}
