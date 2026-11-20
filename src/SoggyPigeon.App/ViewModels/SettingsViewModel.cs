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

    private string _nameError = "";

    public string Name
    {
        get => AppServices.Data.Name;
        set
        {
            var name = value.Trim();
            // Content filter (owner request 2026-11-06): don't save an
            // offensive name; say so next to the box. The server rejects it
            // too at Hello — this is just the friendly first line.
            if (NameFilter.IsOffensive(name))
            {
                NameError = "not allowed: pick a different name";
                // revert the box to the saved name so the refusal is obvious
                // (the typed text would otherwise linger, Haiku round 4)
                Raise(nameof(Name));
                return;
            }
            NameError = "";
            AppServices.Data.Name = name;
            AppServices.Data.Save();
            Raise();
            Raise(nameof(NameValid));
        }
    }

    public bool NameValid => AppServices.Data.IsOnboarded;

    /// <summary>Shown under the name box when an edit was refused.</summary>
    public string NameError
    {
        get => _nameError;
        private set { if (Set(ref _nameError, value)) Raise(nameof(HasNameError)); }
    }

    public bool HasNameError => _nameError.Length > 0;

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
        }
    }

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
            RaiseDelayFlags();
        }
    }

    public string InputDelayLabel => InputDelay switch
    {
        1 => "1 frame: instant, needs a very good connection",
        2 => "2 frames: default",
        _ => $"{InputDelay} frames: safer for shaky connections",
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
            RaiseProofFlags();
        }
    }

    /// <summary>Re-read the checked-state getters on every radio. Without
    /// this the group's own uncheck (a no-op `false` write) can leave a
    /// radio visually checked while the VM disagrees — and clicking that
    /// button then fires no change, so the hint label froze (owner bug
    /// report 2026-11-06). Same treatment as the delay radios.</summary>
    private void RaiseProofFlags()
    {
        Raise(nameof(ProofIs1));
        Raise(nameof(ProofIs2));
        Raise(nameof(ProofIs4));
        Raise(nameof(ProofIs10));
    }

    private void RaiseDelayFlags()
    {
        Raise(nameof(DelayIs1));
        Raise(nameof(DelayIs2));
        Raise(nameof(DelayIs3));
        Raise(nameof(DelayIs4));
        Raise(nameof(DelayIs5));
    }

    public string DigestEveryLabel => DigestEvery == 1
        ? "Every frame: a desync is caught instantly. Costs the most CPU."
        : $"Every {DigestEvery} frames: cheaper on the CPU; a desync can take up to {DigestEvery} frames to be caught.";

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

    /// <summary>Owner toggle (2026-11-06): boot every launched game fullscreen.
    /// Default on — delivered as PSX_FULLSCREEN to the framework's runtime;
    /// games that don't know the variable simply ignore it.</summary>
    public bool StartFullscreen
    {
        get => AppServices.Data.StartFullscreen;
        set
        {
            AppServices.Data.StartFullscreen = value;
            AppServices.Data.Save();
            Raise();
        }
    }

    public ObservableCollection<GamePathSetting> GamePaths { get; } = new();

    public string Version => $"Soggy Pigeon {Proto.ClientVersion}";

    // ---- updates (2026-11-20) ----------------------------------------------

    /// <summary>The result of the most recent "check for updates", worded for
    /// the UI. Empty means "not checked yet".</summary>
    private string _updateStatus = "";
    public string UpdateStatus { get => _updateStatus; private set => Set(ref _updateStatus, value); }

    private bool _updateBusy;
    public bool UpdateBusy { get => _updateBusy; private set => Set(ref _updateBusy, value); }

    public Command CheckUpdateCommand { get; }

    /// <summary>A newer version is downloaded and verified, ready to install.
    /// Shown as a button rather than applied automatically: applying it
    /// restarts the app, and that is the player's call.</summary>
    private UpdateInfo? _updateReady;
    public bool HasUpdateReady => _updateReady is not null;
    public string UpdateReadyLabel => _updateReady is null
        ? ""
        : $"Version {_updateReady.Version} is downloaded. Restart to install it.";

    public Command InstallUpdateCommand { get; }

    public SettingsViewModel(MainViewModel main)
    {
        _main = main;
        CheckUpdateCommand = new Command(async () => await CheckForUpdateAsync(), () => !UpdateBusy);
        InstallUpdateCommand = new Command(InstallUpdate);
    }

    /// <summary>Ask GitHub whether a newer release exists, and if so download
    /// and verify it. Failures are quiet: an unreachable update feed is a
    /// normal condition (offline laptop, blocked network), not an error to put
    /// in the player's face.</summary>
    public async Task CheckForUpdateAsync()
    {
        if (UpdateBusy) return;
        UpdateBusy = true;
        UpdateStatus = "checking…";
        try
        {
            var info = await UpdateService.CheckAsync();
            if (info is null)
            {
                UpdateStatus = "could not reach the update server";
                return;
            }
            if (info.Version.Length == 0)
            {
                UpdateStatus = $"version {Proto.ClientVersion} (no public releases yet)";
                return;
            }
            if (UpdateService.CompareVersions(info.Version, Proto.ClientVersion) <= 0)
            {
                UpdateStatus = $"you're up to date (version {Proto.ClientVersion})";
                return;
            }
            UpdateStatus = $"downloading version {info.Version}…";
            var path = await UpdateService.DownloadAsync(info);
            if (path is null)
            {
                UpdateStatus = $"version {info.Version} is available, but the download failed";
                return;
            }
            _updateReady = info;
            UpdateStatus = $"version {info.Version} is ready to install";
            Raise(nameof(HasUpdateReady));
            Raise(nameof(UpdateReadyLabel));
        }
        catch (Exception ex)
        {
            // The updater must never be the reason the app misbehaves.
            UpdateStatus = $"could not check for updates ({ex.Message})";
        }
        finally
        {
            UpdateBusy = false;
            CheckUpdateCommand.RaiseCanExecute();
        }
    }

    /// <summary>Launch the verified installer and close the app: from here the
    /// installer owns the files, so a half-swapped install is not possible.</summary>
    private void InstallUpdate()
    {
        if (_updateReady is null) return;
        var path = Path.Combine(Path.GetTempPath(), "soggy-pigeon-update",
            $"SoggyPigeon-Setup-{_updateReady.Version}.exe");
        if (!File.Exists(path)) { UpdateStatus = "the downloaded installer is missing"; return; }
        if (!UpdateService.LaunchInstaller(path))
        {
            UpdateStatus = "could not start the installer";
            return;
        }
        // The installer closes the running app itself; shutting down after a
        // successful hand-off is the polite thing to do.
        if (Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            desktop.TryShutdown();
    }

    public void OnNavigatedTo()
    {
        // The two mock games are pipeline fixtures, not installs the owner
        // manages (owner 2026-11-06) — no location rows for them.
        var games = _main.Play.Games.Select(g => g.Game)
            .Where(g => g.Id is not ("mock" or "kart"))
            .ToList();
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
