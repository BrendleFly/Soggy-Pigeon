using System.Net;
using System.Net.Sockets;
using SoggyPigeon.App.Infrastructure;
using SoggyPigeon.App.Services;
using SoggyPigeon.Protocol;

namespace SoggyPigeon.App.ViewModels;

public enum MatchPhase
{
    Idle,        // nothing happening
    Searching,   // in queue
    Paired,      // server paired us; punch in progress
    Launching,   // punching done, game is starting
    InMatch,     // game running, status file flowing
    Result,      // match over, back in the app
}

/// <summary>
/// The whole session lifecycle: queue → paired → punch → boot game → status
/// file → result → foreground return. Also owns Cancel.
/// </summary>
public class MatchViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private PigeonClient Client => AppServices.Client;

    private MatchPhase _phase = MatchPhase.Idle;
    public MatchPhase Phase
    {
        get => _phase;
        private set
        {
            if (Set(ref _phase, value))
            {
                Raise(nameof(IsBusy));
                Raise(nameof(ShowBanner));
                Raise(nameof(ShowSearchButton));
                CancelCommand.RaiseCanExecute();   // the Cancel button binds to this — a stale CanExecute left it dead until re-navigation
                _main.Play.RefreshSearchability(); // the Match page's SEARCH button depends on !IsBusy
            }
        }
    }

    public bool IsBusy => Phase is not (MatchPhase.Idle or MatchPhase.Result);

    /// <summary>The status banner shows everything except the idle state —
    /// when idle, the page offers the SEARCH button instead.</summary>
    public bool ShowBanner => Phase != MatchPhase.Idle;

    /// <summary>Idle or just finished: offer the search button (Play-page style).</summary>
    public bool ShowSearchButton => !IsBusy;

    /// <summary>Same command instance as the Play page's SEARCH.</summary>
    public System.Windows.Input.ICommand SearchCommand => _main.Play.SearchCommand;

    private string _headline = "Not searching";
    public string Headline { get => _headline; private set => Set(ref _headline, value); }

    private string _detail = "";
    public string Detail { get => _detail; private set => Set(ref _detail, value); }

    private string _opponent = "";
    public string Opponent { get => _opponent; private set => Set(ref _opponent, value); }

    private string _gameName = "";
    public string GameName { get => _gameName; private set => Set(ref _gameName, value); }

    private int _pingMs;
    public int PingMs { get => _pingMs; private set => Set(ref _pingMs, value); }

    private bool _relayed;
    public bool Relayed
    {
        get => _relayed;
        private set
        {
            if (Set(ref _relayed, value)) Raise(nameof(ConnectionLine));
        }
    }

    public string ConnectionLine => Relayed ? "server relay" : $"direct — {PingMs} ms";

    private string _policyLabel = "";
    public string PolicyLabel { get => _policyLabel; private set => Set(ref _policyLabel, value); }

    private double ElapsedS => _matchStartUtc == default ? 0 : (DateTime.UtcNow - _matchStartUtc).TotalSeconds;

    /// <summary>Set by CancelInternal; the background boot task checks it before
    /// creating or launching a game, so a cancel during the punch window cannot
    /// resurrect the match afterwards.</summary>
    private volatile bool _bootCancelled;

    private string _seatLabel = "";
    /// <summary>Which seat you drew this session: Pong's menus only listen to
    /// player 1, so this is the "why doesn't my pad navigate" answer.</summary>
    public string SeatLabel { get => _seatLabel; private set => Set(ref _seatLabel, value); }

    public Command CancelCommand { get; }

    // live session data
    private PairedMsg? _paired;
    private PeerMsg? _peer;
    private TaskCompletionSource<PeerMsg>? _peerTcs;
    private GameLauncher? _launcher;
    private DateTime _matchStartUtc;
    private InstallResult? _install;
    private IPEndPoint? _rendezvous;

    public MatchViewModel(MainViewModel main)
    {
        _main = main;
        CancelCommand = new Command(Cancel, () => Phase is MatchPhase.Searching or MatchPhase.Paired or MatchPhase.Launching);

        var c = Client;
        c.Queued += q => Ui(() =>
        {
            if (Phase == MatchPhase.Idle) Phase = MatchPhase.Searching;
            Headline = "Searching…";
            Detail = CountsLine(q.Counts);
        });
        c.Counts += m => Ui(() =>
        {
            if (Phase == MatchPhase.Searching) Detail = CountsLine(m.Counts);
        });
        c.Paired += p => Ui(() => OnPaired(p));
        c.Peer += m => Ui(() =>
        {
            _peer = m;
            _peerTcs?.TrySetResult(m);
        });
        c.SessionEnd += e => Ui(() =>
        {
            if (Phase == MatchPhase.Searching)
            {
                Reset("Session ended", e.Reason == "cancel" ? "cancelled" : e.Reason);
                Phase = MatchPhase.Idle;
            }
            else if (Phase is MatchPhase.Paired)
            {
                Reset("Session ended", e.Reason);
            }
        });
    }

    private string CountsLine(Dictionary<string, int> counts)
    {
        if (counts.Count == 0) return "no one else is searching yet";
        return string.Join(", ", counts.Select(kv =>
        {
            var name = _main.Play.Games.FirstOrDefault(g => g.Game.Id == kv.Key)?.Name ?? kv.Key;
            return $"{kv.Value} player{(kv.Value == 1 ? "" : "s")} in {name}";
        }));
    }

    private static void Ui(Action a) => MainViewModel.Ui(a);

    private void OnPaired(PairedMsg p)
    {
        if (Phase is MatchPhase.InMatch or MatchPhase.Launching) return; // stale event
        _paired = p;
        _peerTcs = new TaskCompletionSource<PeerMsg>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_peer is { } already) _peerTcs.TrySetResult(already);
        Phase = MatchPhase.Paired;
        _bootCancelled = false;
        Opponent = p.OpponentName;
        // display name from the manifest (the wire carries the internal id)
        GameName = _main.Play.Games.FirstOrDefault(g => g.Game.Id == p.Game)?.Name ?? p.Game;
        SeatLabel = p.Role == 1 ? "Player 1 — your pad drives the menus" : "Player 2 — player 1 navigates the menus";
        PolicyLabel = p.Policy == "single" ? "single" : "session";
        Headline = $"Paired with {p.OpponentName}";
        Detail = $"game: {p.Game} — connecting…";
        CancelCommand.RaiseCanExecute();

        _ = Task.Run(() => ConnectAndBootAsync(p));
    }

    private async Task ConnectAndBootAsync(PairedMsg p)
    {
        // 1) resolve the rendezvous address from the relay endpoint string
        IPEndPoint? rendezvous = null;
        if (p.Relay is { } relay && relay != "-" && relay.Split(':') is { Length: 2 } rp
            && IPAddress.TryParse(rp[0], out var rip) && int.TryParse(rp[1], out var rport))
            rendezvous = new IPEndPoint(rip, rport);
        _rendezvous = rendezvous;

        // 2) punch for a direct connection (up to ~10 s), else fall back to relay.
        //    Punch from the port the game will rebind, so the NAT mapping stays warm.
        var localPort = Random.Shared.Next(40000, 45000);
        Ui(() => Detail = "connecting (direct attempt)…");
        PunchOutcome? punch = null;
        if (rendezvous is not null)
        {
            punch = await PunchService.PunchAsync(p.Session, p.Role, rendezvous, localPort,
                _peerTcs?.Task);
        }

        var endpoint = punch?.Peer?.ToString() ?? "-";
        Ui(() =>
        {
            Relayed = punch is null;
            PingMs = punch?.PingMs ?? 0;
            Detail = punch is null ? "direct failed — using server relay" : $"direct — {punch.PingMs} ms";
            Phase = MatchPhase.Launching;
            Headline = $"Launching {GameName}…";
        });

        // 3) find the installed game
        var install = _main.Play.Games.FirstOrDefault(g => g.Game.Id == p.Game)?.Install;
        if (install?.ExePath is null)
        {
            Ui(() =>
            {
                Reset("Game not installed", $"cannot find a usable install of {p.Game}");
                CancelInternal();
                Phase = MatchPhase.Idle;   // release the UI; user is back to idle, ready to search
            });
            return;
        }
        _install = install;
        if (_bootCancelled) return;   // cancelled during the punch: stand down

        // 4) boot with the frozen handoff
        var statusPath = Path.Combine(Path.GetTempPath(), $"soggy-pigeon-status-{p.Session[..8]}.txt");
        var boot = new BootInfo
        {
            Session = p.Session,
            Tag = SessionTag.Hex(p.Session),
            Role = p.Role == 1 ? "host" : "join",
            Endpoint = endpoint,
            Relay = rendezvous?.ToString() ?? "-",
            Policy = p.Policy,
            Params = p.Params,
            StatusPath = statusPath,
            LocalPort = localPort,
            // session-wide netplay parameters (server-reconciled)
            Delay = p.Delay is >= 1 and <= 8 ? p.Delay : 2,
            DigestEvery = p.DigestEvery is >= 1 and <= 10 ? p.DigestEvery : 1,
        };

        var launcher = new GameLauncher();
        DisposeLauncher();         // a finished match's launcher: dispose before
                                   // replacing (also cleans its temp status file)
        _launcher = launcher;
        _matchStartUtc = DateTime.UtcNow;
        launcher.StatusChanged += snap => Ui(() => OnStatus(snap));
        launcher.GameExited += snap => Ui(() => OnGameExited(snap));

        if (_bootCancelled)        // cancelled while we prepared: stand down
        {
            DisposeLauncher();
            return;
        }
        Ui(() => Detail = punch is null ? "booting game (relay)…" : "booting game…");
        if (launcher.Launch(boot, install, p.Role))
        {
            // §6.1: tell the server the game actually launched, so the session
            // is live server-side (and ends properly when results arrive).
            _ = Client.SendAsync(new PlayingMsg { Session = p.Session });
        }
        else
        {
            Ui(() =>
            {
                _main.Toast($"failed to start {GameName}");
                CancelInternal();
                Phase = MatchPhase.Idle;   // release the UI; user is back to idle, ready to search
            });
        }
    }

    private void OnStatus(StatusFile.Snapshot snap)
    {
        if (Phase is MatchPhase.Idle or MatchPhase.Result) return;   // stale tick after cancel/finish
        Phase = MatchPhase.InMatch;
        Headline = snap.State switch
        {
            StatusFile.MatchState.Searching => $"Connecting to {Opponent}…",
            StatusFile.MatchState.Connected => $"Connected to {Opponent}",
            StatusFile.MatchState.Loading => "Loading…",
            StatusFile.MatchState.Fight => $"In match — Round {snap.RoundP1}-{snap.RoundP2}",
            StatusFile.MatchState.Result => "Match over",
            _ => Headline,
        };
        if (snap.PingMs > 0) PingMs = snap.PingMs;
        Detail = $"in match — {GameName}"
                 + (Relayed ? " (relay)" : "")
                 + (snap.Message.Length > 0 ? $" — {snap.Message}" : "");
    }

    private void OnGameExited(StatusFile.Snapshot snap)
    {
        if (Phase == MatchPhase.Idle) return;   // cancelled before the game reported anything
        var winnerWord = snap.Winner switch
        {
            0 => "no result",
            -1 => "draw",
            _ => "",
        };
        var outcome = snap.Disconnected ? "disconnect"
            : snap.Winner switch { 1 => "win", 2 => "loss", _ => "draw" };

        // role 1 = host = "p1" in the status file's eyes
        if (_paired is { } p && snap.Winner is 1 or 2)
            outcome = (snap.Winner == p.Role) ? "win" : "loss";

        // §6.1: report the result so the server ends the session — without
        // this the session lingers and the next SEARCH gets "in_session".
        var sessionId = _paired?.Session ?? "";
        var durationS = ElapsedS;

        AppServices.History.Add(new HistoryEntry
        {
            When = DateTime.Now,
            GameId = _paired?.Game ?? GameName,
            GameName = GameName,
            Opponent = Opponent,
            Outcome = outcome,
            DurationS = ElapsedS,
            PingMs = PingMs,
            Relayed = Relayed,
        });

        if (sessionId.Length > 0)
            _ = Client.SendAsync(new ResultMsg { Session = sessionId, Outcome = outcome, DurationS = durationS });

        Phase = MatchPhase.Result;
        Headline = outcome == "disconnect" ? "Opponent disconnected"
            : outcome == "win" ? "You won!"
            : outcome == "loss" ? "You lost"
            : "Match finished";
        Detail = winnerWord.Length > 0 ? winnerWord
            : "returned from the game — press Search to find the next opponent";
        CancelInternal(keepResult: true);
        // §2.8: Soggy Pigeon comes back to the foreground, ready to search again
        ForegroundReturned?.Invoke();
    }

    /// <summary>Raised so MainWindow can re-activate itself after a match.</summary>
    public event Action? ForegroundReturned;

    public void OnConnectionLost()
    {
        if (Phase == MatchPhase.Searching)
        {
            Reset("Disconnected", "lost the server while searching");
            Phase = MatchPhase.Idle;
        }
    }

    private void Cancel()
    {
        switch (Phase)
        {
            case MatchPhase.Searching:
                _ = Client.LeaveQueueAsync();
                Reset("Not searching", "cancelled");
                Phase = MatchPhase.Idle;
                break;
            case MatchPhase.Paired or MatchPhase.Launching:
                CancelInternal();
                Reset("Not searching", "cancelled before launch");
                Phase = MatchPhase.Idle;
                break;
            case MatchPhase.InMatch:
                // closing the game is the way out of a live match
                if (_paired is { } sp)
                    _ = Client.SendAsync(new ResultMsg { Session = sp.Session, Outcome = "disconnect", DurationS = ElapsedS });
                DisposeLauncher();
                Reset("Not searching", "left the match");
                Phase = MatchPhase.Idle;
                break;
        }
        CancelCommand.RaiseCanExecute();
    }

    private void CancelInternal(bool keepResult = false)
    {
        _bootCancelled = true;   // the background boot task must stand down
        _paired = null;
        _peer = null;
        _peerTcs = null;
        if (!keepResult) DisposeLauncher();
        CancelCommand.RaiseCanExecute();
    }

    /// <summary>Dispose-and-drop the launcher. Disposing also deletes its temp
    /// status file, so the next pairing starts clean.</summary>
    private void DisposeLauncher()
    {
        _launcher?.Dispose();
        _launcher = null;
    }

    private void Reset(string headline, string detail)
    {
        Headline = headline;
        Detail = detail;
    }
}
