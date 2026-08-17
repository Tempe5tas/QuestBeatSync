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
        try
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
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ReportError("Import playlist", exception);
        }
    }

    private async void ChooseAdb_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose adb executable",
                AllowMultiple = false
            });
            if (files.Count == 1 && DataContext is MainWindowViewModel viewModel)
                await viewModel.Settings.ChooseExecutableAsync(files[0].Path.LocalPath);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { ReportError("Choose adb executable", exception); }
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
        try
        {
            var files = e.DataTransfer.TryGetFiles();
            e.Handled = true;

            if (files is not null)
            {
                await ImportFilesAsync(files.Select(file => file.Path.LocalPath));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ReportError("Import dropped playlist", exception);
        }
    }

    private Task ImportFilesAsync(IEnumerable<string> filePaths) =>
        DataContext is MainWindowViewModel viewModel
            ? viewModel.Playlists.ImportAsync(filePaths)
            : Task.CompletedTask;

    private void ReportError(string operation, Exception exception)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.ReportOperationError(operation, exception);
        }
    }
}
