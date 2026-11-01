using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SoggyPigeon.App.ViewModels;

namespace SoggyPigeon.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: GamePathSetting row } || TopLevel.GetTopLevel(this) is not { } top)
            return;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = $"Choose the install folder for {row.Name}",
            AllowMultiple = false,
        });
        if (folders.Count > 0)
            row.Path_ = folders[0].Path.LocalPath;
    }
}
