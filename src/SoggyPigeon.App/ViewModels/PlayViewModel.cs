using System.Collections.ObjectModel;
using SoggyPigeon.App.Infrastructure;
using SoggyPigeon.App.Services;
using SoggyPigeon.Protocol;

namespace SoggyPigeon.App.ViewModels;

/// <summary>One row in the game list: switch, install state, searching hint.</summary>
public class GameItemViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    public ManifestGame Game { get; }

    private InstallResult _install = new() { Status = InstallStatus.NotInstalled };
    public InstallResult Install
    {
        get => _install;
        private set
        {
            if (Set(ref _install, value))
            {
                Raise(nameof(StatusText));
                Raise(nameof(IsUsable));
                Raise(nameof(IsInstalled));
            }
        }
    }

    public string Name => Game.Name;
    public string StatusText => Install.StatusText;
    public bool IsInstalled => Install.Status == InstallStatus.Ok;
    public bool IsUsable => IsInstalled;

    private bool _on;
    public bool On
    {
        get => _on;
        set
        {
            if (!Set(ref _on, value)) return;
            AppServices.Data.SetGameEnabled(Game.Id, value);
            _main.Play.RefreshSearchability();
        }
    }

    private int _searching;
    public int Searching
    {
        get => _searching;
        private set
        {
            if (Set(ref _searching, value)) Raise(nameof(CountText));
        }
    }

    public string CountText => Searching > 0
        ? $"{Searching} player{(Searching == 1 ? "" : "s")} searching"
        : "";

    public GameItemViewModel(MainViewModel main, ManifestGame game)
    {
        _main = main;
        Game = game;
        _on = AppServices.Data.IsGameEnabled(game.Id, installed: false);
    }

    public void Rescan()
    {
        Install = InstallScanner.Scan(Game, AppServices.Data.GamePath(Game.Id));
        Raise(nameof(StatusText));
        Raise(nameof(IsUsable));
        Raise(nameof(IsInstalled));
        if (Install.Status == InstallStatus.Ok && !_on)
            _on = AppServices.Data.IsGameEnabled(Game.Id, installed: true);
        Raise(nameof(On));
    }

    public void ApplyCount(int n) => Searching = n;

    public QueueGame ToQueueGame() => new(Game.Id, Game.Fingerprint);
}

/// <summary>The Play (home) page: games, mode + policy, the Search button.</summary>
public class PlayViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public ObservableCollection<GameItemViewModel> Games { get; } = new();

    private bool _scanned;
    public bool Scanned { get => _scanned; private set => Set(ref _scanned, value); }

    private string _scanSummary = "";
    public string ScanSummary { get => _scanSummary; private set => Set(ref _scanSummary, value); }

    public bool HasUsableGames => Games.Any(g => g.IsUsable);

    /// <summary>False until the manifest arrives — the Games page shows a loading hint.</summary>
    public bool HasGames => Games.Count > 0;

    /// <summary>The session state machine — status banner + Cancel live on the Play page.</summary>
    public MatchViewModel Match => _main.Match;

    /// <summary>The Play page's summary line — the switches themselves live on the GAMES page.</summary>
    public string GamesLine
    {
        get
        {
            var on = Games.Count(g => g.On && g.IsUsable);
            return on switch
            {
                0 => "No games activated — activate some in GAMES first",
                1 => "1 game activated",
                _ => $"{on} games activated",
            };
        }
    }

    private string _mode = "any";
    /// <summary>"any" (default — first available match of any type) or "casual". Wire value carried by the queue.</summary>
    public string Mode
    {
        get => _mode;
        set
        {
            if (Set(ref _mode, value))
            {
                Raise(nameof(AnyTypeMode));
                Raise(nameof(CasualMode));
            }
        }
    }

    /// <summary>Radio binding: "Any type" — the default match type.</summary>
    public bool AnyTypeMode
    {
        get => _mode == "any";
        set { if (value) Mode = "any"; }
    }

    /// <summary>Radio binding: "Casual".</summary>
    public bool CasualMode
    {
        get => _mode == "casual";
        set { if (value) Mode = "casual"; }
    }

    private bool _rematch = true;
    /// <summary>true = session (rematch — the wire value, §3.3), false = single match. Displayed as "Session".</summary>
    public bool Rematch
    {
        get => _rematch;
        set => Set(ref _rematch, value);
    }

    public string Policy => Rematch ? "rematch" : "single";

    public Command SearchCommand { get; }
    public Command RescanCommand { get; }

    public PlayViewModel(MainViewModel main)
    {
        _main = main;
        SearchCommand = new Command(Search, CanSearch);
        RescanCommand = new Command(Rescan);
    }

    public void OnNavigatedTo()
    {
        if (!Scanned) Rescan();
    }

    public void ApplyManifest(ManifestDoc doc)
    {
        Ui(() =>
        {
            Games.Clear();
            foreach (var g in doc.Games)
            {
                var item = new GameItemViewModel(_main, g);
                item.Rescan();
                item.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName is nameof(GameItemViewModel.IsUsable)) RefreshSearchability();
                };
                Games.Add(item);
            }
            UpdateScanSummary();
            Raise(nameof(HasUsableGames));
            RefreshSearchability();
        });
    }

    /// <summary>Recompute the "N installed, M wrong version" line (call after
    /// the manifest lands or a rescan runs — the summary must follow both).</summary>
    private void UpdateScanSummary()
    {
        var ok = Games.Count(g => g.IsInstalled);
        var mismatch = Games.Count(g => g.Install.Status == InstallStatus.VersionMismatch);
        ScanSummary = $"{ok} installed, {mismatch} wrong version";
        Raise(nameof(HasGames));
    }

    public void ApplyCounts(Dictionary<string, int> counts)
    {
        Ui(() =>
        {
            foreach (var g in Games)
                g.ApplyCount(counts.GetValueOrDefault(g.Game.Id, 0));
        });
    }

    public void Rescan()
    {
        Ui(() =>
        {
            foreach (var g in Games) g.Rescan();
            Scanned = true;
            UpdateScanSummary();
            Raise(nameof(HasUsableGames));
            RefreshSearchability();
        });
    }

    public void RefreshSearchability()
    {
        Raise(nameof(HasUsableGames));
        Raise(nameof(GamesLine));
        SearchCommand.RaiseCanExecute();
    }

    private bool CanSearch() => HasUsableGames && !_main.Match.IsBusy && !AppServices.Client.IsConnecting;

    /// <summary>Connect and fetch the manifest without joining the queue —
    /// done at startup so the game list is visible immediately.</summary>
    public async Task ConnectForManifestAsync()
    {
        var client = AppServices.Client;
        if (client.IsConnected || client.IsConnecting) return;
        try
        {
            _main.ConnectionState = "connecting…";
            await client.ConnectAsync(AppServices.Data.ServerAddress,
                AppServices.Data.Token, AppServices.Data.Name, AppServices.Data.CertPin);
            client.RequestManifestAsync();
            // small pause so the welcome/manifest round-trip lands first
            await Task.Delay(150);
        }
        catch (Exception ex)
        {
            _main.ConnectionState = "offline";
            _main.Toast($"cannot reach server: {ex.Message}");
        }
        finally
        {
            // the connect attempt flipped IsConnecting (and possibly IsConnected)
            RefreshSearchability();
        }
    }

    private async void Search()
    {
        var client = AppServices.Client;
        var queueGames = Games.Where(g => g.On && g.IsUsable).Select(g => g.ToQueueGame()).ToArray();
        if (queueGames.Length == 0) return;

        try
        {
            await ConnectForManifestAsync();
            await client.EnterQueueAsync(queueGames, Mode, Policy,
                AppServices.Data.InputDelay, AppServices.Data.DigestEvery);
        }
        catch (Exception ex)
        {
            _main.ConnectionState = "offline";
            _main.Toast($"cannot reach server: {ex.Message}");
        }
    }

    public void OnConnectionLost()
    {
        // raised on PigeonClient's read-loop thread — command notifications
        // must reach the UI thread before Avalonia touches bindings
        MainViewModel.Ui(() => SearchCommand.RaiseCanExecute());
    }

    private static void Ui(Action a) => MainViewModel.Ui(a);
}
