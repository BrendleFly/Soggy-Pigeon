using SoggyPigeon.App.Services;

namespace SoggyPigeon.App.ViewModels;

/// <summary>App-wide singletons, wired once at startup.</summary>
public static class AppServices
{
    public static AppData Data { get; private set; } = new();
    public static HistoryStore History { get; private set; } = new();
    public static PigeonClient Client => PigeonClient.Instance;

    public static void LoadAll()
    {
        Data = AppData.Load();
        Data.EnsureToken();
        History = HistoryStore.Load();
    }
}
