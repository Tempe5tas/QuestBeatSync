using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using QuestBeatSync.App.ViewModels;

namespace QuestBeatSync.App.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow() => AvaloniaXamlLoader.Load(this);

    private async void ImportPlaylist_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import BeatSaver playlists",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("BeatSaver playlist")
                {
                    Patterns = ["*.bplist"]
                }
            ]
        });

        await ImportFilesAsync(files.Select(file => file.Path.LocalPath));
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Formats.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        e.Handled = true;

        if (files is not null)
        {
            await ImportFilesAsync(files.Select(file => file.Path.LocalPath));
        }
    }

    private Task ImportFilesAsync(IEnumerable<string> filePaths) =>
        DataContext is MainWindowViewModel viewModel
            ? viewModel.ImportPlaylistFilesAsync(filePaths)
            : Task.CompletedTask;
}
