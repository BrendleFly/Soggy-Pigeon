using System.Collections.ObjectModel;
using SoggyPigeon.App.Infrastructure;
using SoggyPigeon.App.Services;

namespace SoggyPigeon.App.ViewModels;

public class HistoryViewModel : ObservableObject
{
    public ObservableCollection<HistoryEntry> Entries => AppServices.History.Entries;

    public bool HasEntries => Entries.Count > 0;

    /// <summary>Owner request (2026-11-06): wipe the local match list. The
    /// file is rewritten immediately (no later Add to retry the write).</summary>
    public Command ClearHistoryCommand { get; }

    public HistoryViewModel()
    {
        ClearHistoryCommand = new Command(ClearHistory, () => HasEntries);
    }

    private void ClearHistory()
    {
        AppServices.History.Clear();
        Raise(nameof(HasEntries));
        ClearHistoryCommand.RaiseCanExecute();
    }

    public void OnNavigatedTo()
    {
        Raise(nameof(Entries));
        Raise(nameof(HasEntries));
        ClearHistoryCommand.RaiseCanExecute();
    }
}
