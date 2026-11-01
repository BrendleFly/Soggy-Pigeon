using System.Collections.ObjectModel;
using SoggyPigeon.App.Infrastructure;
using SoggyPigeon.App.Services;

namespace SoggyPigeon.App.ViewModels;

public enum AppPage { Play, Games, History, Settings }

/// <summary>Shell state: current page, online count, connection errors.</summary>
public class MainViewModel : ObservableObject
{
    public PlayViewModel Play { get; }
    public MatchViewModel Match { get; }
    public HistoryViewModel HistoryVm { get; }
    public SettingsViewModel Settings { get; }

    private AppPage _page = AppPage.Play;
    public AppPage Page
    {
        get => _page;
        set
        {
            if (Set(ref _page, value))
            {
                Raise(nameof(PageTitle));
                (value switch
                {
                    AppPage.Play => (Action?)Play.OnNavigatedTo,
                    AppPage.Games => Play.OnNavigatedTo,   // same VM backs both pages
                    AppPage.History => HistoryVm.OnNavigatedTo,
                    AppPage.Settings => Settings.OnNavigatedTo,
                    _ => null,
                })?.Invoke();
            }
        }
    }

    public string PageTitle => Page switch
    {
        AppPage.Play => "Play",
        AppPage.Games => "Games",
        AppPage.History => "History",
        AppPage.Settings => "Settings",
        _ => "",
    };

    private int _onlineCount;
    public int OnlineCount { get => _onlineCount; set => Set(ref _onlineCount, value); }

    private string _connectionState = "offline";
    public string ConnectionState
    {
        get => _connectionState;
        set
        {
            if (Set(ref _connectionState, value)) Raise(nameof(OnlineLabel));
        }
    }

    public string OnlineLabel => ConnectionState == "online"
        ? $"● online — {OnlineCount} connected"
        : $"● {ConnectionState}";

    /// <summary>True once the name dialog has completed; gates the pages.</summary>
    public bool Onboarded => AppServices.Data.IsOnboarded;

    public void RaiseOnboarded() => Raise(nameof(Onboarded));

    public ObservableCollection<string> Toasts { get; } = new();

    public MainViewModel()
    {
        Play = new PlayViewModel(this);
        Match = new MatchViewModel(this);
        HistoryVm = new HistoryViewModel();
        Settings = new SettingsViewModel(this);

        var client = AppServices.Client;
        client.Welcome += w => Ui(() => { OnlineCount = w.Online; ConnectionState = "online"; });
        client.Counts += c => Ui(() =>
        {
            OnlineCount = Math.Max(OnlineCount, 1);
            Play.ApplyCounts(c.Counts);
        });
        client.Manifest += doc => Ui(() => Play.ApplyManifest(doc));
        client.Disconnected += () => Ui(() =>
        {
            ConnectionState = "offline";
            Match.OnConnectionLost();
            Play.OnConnectionLost();
        });
        client.Error += e => Ui(() =>
        {
            if (e.Kind == "version_mismatch")
                Toast($"version mismatch: {e.Detail}");
            else
                Toast($"server: {e.Kind} — {e.Detail}");
        });
    }

    public void Toast(string message)
    {
        Toasts.Add(message);
        while (Toasts.Count > 3) Toasts.RemoveAt(0);
        _ = Task.Run(async () =>
        {
            await Task.Delay(6000);
            Ui(() => Toasts.Remove(message));
        });
    }

    /// <summary>Marshal an action onto the UI thread.</summary>
    public static void Ui(Action action)
    {
        var top = Avalonia.Application.Current?.ApplicationLifetime switch
        {
            Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime d => d.MainWindow,
            _ => null,
        };
        if (top is not null)
            Avalonia.Threading.Dispatcher.UIThread.Post(action, Avalonia.Threading.DispatcherPriority.Normal);
        else
            action();
    }
}
