using System.Collections.ObjectModel;
using SoggyPigeon.App.Infrastructure;
using SoggyPigeon.App.Services;
using SoggyPigeon.Protocol;

namespace SoggyPigeon.App.ViewModels;

/// <summary>One per-game settings row: the install-folder override for a manifest game.</summary>
public class GamePathSetting : ObservableObject
{
    private readonly SettingsViewModel _owner;
    public ManifestGame Game { get; }

    public string Name => Game.Name;

    public string Path_
    {
        get => AppServices.Data.GamePath(Game.Id) ?? "";
        set
        {
            AppServices.Data.SetGamePath(Game.Id, string.IsNullOrWhiteSpace(value) ? null : value);
            Raise();
            _owner.RescanGames();
        }
    }

    public GamePathSetting(SettingsViewModel owner, ManifestGame game)
    {
        _owner = owner;
        Game = game;
    }
}

public class SettingsViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public string Name
    {
        get => AppServices.Data.Name;
        set
        {
            AppServices.Data.Name = value.Trim();
            AppServices.Data.Save();
            Raise();
            Raise(nameof(NameValid));
        }
    }

    public bool NameValid => AppServices.Data.IsOnboarded;

    public string ServerAddress
    {
        get => AppServices.Data.ServerAddress;
        set
        {
            AppServices.Data.ServerAddress = value.Trim();
            AppServices.Data.Save();
            Raise();
        }
    }

    public string CertPin
    {
        get => AppServices.Data.CertPin;
        set
        {
            AppServices.Data.CertPin = value.Trim();
            AppServices.Data.Save();
            Raise();
            Raise(nameof(TlsLabel));
        }
    }

    public string TlsLabel => string.IsNullOrWhiteSpace(AppServices.Data.CertPin)
        ? "TLS off — plain TCP (fine for the home LAN)"
        : "TLS on — the server's certificate must match this pin";

    // ---- netplay comfort (§ session parameters, reconciled per session) ----

    /// <summary>Rollback input delay in frames. Higher = calmer play on
    /// shaky connections, slower buttons. Reconciled: the higher of the two
    /// players' choices wins for the session.</summary>
    public int InputDelay
    {
        get => AppServices.Data.InputDelay;
        set
        {
            AppServices.Data.InputDelay = Math.Clamp(value, 1, 8);
            AppServices.Data.Save();
            Raise();
            Raise(nameof(InputDelayLabel));
        }
    }

    public string InputDelayLabel => InputDelay switch
    {
        1 => "1 frame — instant, needs a very good connection",
        2 => "2 frames — the classic default",
        _ => $"{InputDelay} frames — calmer, for shaky connections",
    };

    /// <summary>Sync-proof cadence: prove every Nth frame. 1 = full proof
    /// (default). Lower = stronger proof, more CPU. Reconciled: the lower
    /// (more-proving) player's choice wins for the session. Only honoured
    /// by games whose build supports the switch.</summary>
    public int DigestEvery
    {
        get => AppServices.Data.DigestEvery;
        set
        {
            AppServices.Data.DigestEvery = Math.Clamp(value, 1, 10);
            AppServices.Data.Save();
            Raise();
            Raise(nameof(DigestEveryLabel));
        }
    }

    public string DigestEveryLabel => DigestEvery == 1
        ? "Every frame — full proof (default)"
        : $"Every {DigestEvery}th frame — lighter, slightly slower to catch a desync";

    // radio bindings (bool wrappers, same pattern as the Play page's mode radios)
    public bool DelayIs1 { get => InputDelay == 1; set { if (value) InputDelay = 1; } }
    public bool DelayIs2 { get => InputDelay == 2; set { if (value) InputDelay = 2; } }
    public bool DelayIs3 { get => InputDelay == 3; set { if (value) InputDelay = 3; } }
    public bool DelayIs4 { get => InputDelay == 4; set { if (value) InputDelay = 4; } }
    public bool DelayIs5 { get => InputDelay == 5; set { if (value) InputDelay = 5; } }
    public bool ProofIs1 { get => DigestEvery == 1; set { if (value) DigestEvery = 1; } }
    public bool ProofIs2 { get => DigestEvery == 2; set { if (value) DigestEvery = 2; } }
    public bool ProofIs4 { get => DigestEvery == 4; set { if (value) DigestEvery = 4; } }
    public bool ProofIs10 { get => DigestEvery == 10; set { if (value) DigestEvery = 10; } }

    public ObservableCollection<GamePathSetting> GamePaths { get; } = new();

    public string Version => $"Soggy Pigeon {Proto.ClientVersion} · {Proto.Name} · free, no ads";

    public SettingsViewModel(MainViewModel main) { _main = main; }

    public void OnNavigatedTo()
    {
        var games = _main.Play.Games.Select(g => g.Game).ToList();
        if (games.Count == 0) return; // manifest not fetched yet; refills on next visit
        var byId = GamePaths.ToDictionary(p => p.Game.Id, p => p);
        GamePaths.Clear();
        foreach (var g in games)
            GamePaths.Add(byId.TryGetValue(g.Id, out var existing) ? existing : new GamePathSetting(this, g));
    }

    public void RescanGames()
    {
        MainViewModel.Ui(() =>
        {
            foreach (var g in _main.Play.Games) g.Rescan();
        });
    }
}
