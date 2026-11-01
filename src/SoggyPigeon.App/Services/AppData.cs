using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SoggyPigeon.App.Services;

/// <summary>Local settings (§3.4 identity): screen name + silent device token,
/// server address, per-game path overrides. Nothing else ever leaves the machine.</summary>
public sealed class AppData
{
    public string Name { get; set; } = "";
    public string Token { get; set; } = "";
    public string ServerAddress { get; set; } = "127.0.0.1:7200";
    /// <summary>SHA-256 of the server's certificate. When set, the app speaks
    /// TLS and refuses any cert whose pin differs. Empty = plain TCP (LAN).</summary>
    public string CertPin { get; set; } = "";
    /// <summary>Preferred rollback input delay in frames (1..8, default 2).
    /// A session parameter: the server reconciles both players (higher wins)
    /// and both peers run the reconciled value.</summary>
    public int InputDelay { get; set; } = 2;
    /// <summary>Preferred sync-proof cadence: prove every Nth frame (1..10;
    /// 1 = prove every frame, the default). Server reconciles (lower wins).
    /// Framework games only honour this once the upstream switch exists;
    /// until then the boot argument is passed and politely ignored.</summary>
    public int DigestEvery { get; set; } = 1;
    public Dictionary<string, string> GamePaths { get; set; } = new();   // gameId -> install folder
    public Dictionary<string, bool> GameEnabled { get; set; } = new();   // gameId -> switch state

    [JsonIgnore]
    public static string Dir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SoggyPigeon");

    private static string Path_ => System.IO.Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static AppData Load()
    {
        try
        {
            if (File.Exists(Path_))
            {
                var d = JsonSerializer.Deserialize<AppData>(File.ReadAllText(Path_), JsonOpts);
                if (d is not null) return d;
            }
        }
        catch { /* corrupt settings: start fresh */ }
        return new AppData();
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        var tmp = Path_ + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts));
        File.Move(tmp, Path_, overwrite: true);
    }

    /// <summary>Generate the device token once (§3.4: silent, no UI).</summary>
    public void EnsureToken()
    {
        if (Token.Length == 0)
            Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    }

    public bool IsOnboarded => Name.Trim().Length >= 3;

    public string? GamePath(string gameId) => GamePaths.GetValueOrDefault(gameId) is { } p && p.Length > 0 ? p : null;
    public void SetGamePath(string gameId, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) GamePaths.Remove(gameId);
        else GamePaths[gameId] = path;
        Save();
    }

    public bool IsGameEnabled(string gameId, bool installed) =>
        installed && GameEnabled.GetValueOrDefault(gameId, true);

    public void SetGameEnabled(string gameId, bool on)
    {
        GameEnabled[gameId] = on;
        Save();
    }
}

/// <summary>One past match, recorded locally (never sent anywhere in v1).</summary>
public sealed class HistoryEntry
{
    public DateTime When { get; set; }
    public string GameId { get; set; } = "";
    public string GameName { get; set; } = "";
    public string Opponent { get; set; } = "";
    /// <summary>"win" | "loss" | "draw" | "disconnect"</summary>
    public string Outcome { get; set; } = "draw";
    public double DurationS { get; set; }
    public int PingMs { get; set; }
    public bool Relayed { get; set; }

    // presentation helpers for the History page
    public bool IsWin => Outcome == "win";
    public bool IsLoss => Outcome == "loss";
    public string WhenText => When.ToString("yyyy-MM-dd HH:mm");
    public string DetailText => $"{WhenText} · {PingMs} ms · {(Relayed ? "server relay" : "direct")}";
}

public sealed class HistoryStore
{
    private static string Path_ => Path.Combine(AppData.Dir, "history.json");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public ObservableCollection<HistoryEntry> Entries { get; private set; } = new();

    public static HistoryStore Load()
    {
        var store = new HistoryStore();
        try
        {
            if (File.Exists(Path_))
            {
                var list = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(Path_));
                if (list is not null)
                    foreach (var e in list) store.Entries.Add(e);
            }
        }
        catch { /* corrupt history: start fresh */ }
        return store;
    }

    public void Add(HistoryEntry entry)
    {
        Entries.Insert(0, entry);
        while (Entries.Count > 100) Entries.RemoveAt(Entries.Count - 1);
        try
        {
            Directory.CreateDirectory(AppData.Dir);
            var tmp = Path_ + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Entries.ToList(), JsonOpts));
            File.Move(tmp, Path_, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the in-memory entry is already inserted — the UI stays
            // consistent and the next Add retries the file write; letting
            // this throw would crash on the UI thread after every match
        }
    }
}
