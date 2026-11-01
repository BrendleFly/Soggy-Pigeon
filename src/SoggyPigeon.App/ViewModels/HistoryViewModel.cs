using System.Collections.ObjectModel;
using SoggyPigeon.App.Infrastructure;
using SoggyPigeon.App.Services;

namespace SoggyPigeon.App.ViewModels;

public class HistoryViewModel : ObservableObject
{
    public ObservableCollection<HistoryEntry> Entries => AppServices.History.Entries;

    public bool HasEntries => Entries.Count > 0;

    public void OnNavigatedTo()
    {
        Raise(nameof(Entries));
        Raise(nameof(HasEntries));
    }
}
