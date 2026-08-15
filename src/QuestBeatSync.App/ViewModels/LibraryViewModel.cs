using System.Collections.ObjectModel;
using QuestBeatSync.Core.Models;

namespace QuestBeatSync.App.ViewModels;

public sealed class LibraryViewModel : ViewModelBase
{
    private bool _scanCompleted;

    public ObservableCollection<QuestInstalledMap> InstalledMaps { get; } = [];

    public ObservableCollection<QuestInstalledPlaylist> InstalledPlaylists { get; } = [];

    public ObservableCollection<QuestScanWarning> ScanWarnings { get; } = [];

    public bool ScanCompleted
    {
        get => _scanCompleted;
        private set => SetProperty(ref _scanCompleted, value);
    }

    public int SongCount => InstalledMaps.Count;

    public int PlaylistCount => InstalledPlaylists.Count;

    public int ScanWarningCount => ScanWarnings.Count;

    public bool HasInstalledMaps => InstalledMaps.Count > 0;

    public bool HasInstalledPlaylists => InstalledPlaylists.Count > 0;

    public bool HasScanWarnings => ScanWarnings.Count > 0;

    public event EventHandler? Changed;

    public void Reset(bool scanCompleted = false) => Apply(QuestBeatSaberScanResult.Empty, scanCompleted);

    public void Apply(QuestBeatSaberScanResult result, bool scanCompleted)
    {
        Replace(InstalledMaps, result.InstalledMaps);
        Replace(InstalledPlaylists, result.InstalledPlaylists);
        Replace(ScanWarnings, result.Warnings);
        ScanCompleted = scanCompleted;
        OnPropertyChanged(nameof(SongCount));
        OnPropertyChanged(nameof(PlaylistCount));
        OnPropertyChanged(nameof(ScanWarningCount));
        OnPropertyChanged(nameof(HasInstalledMaps));
        OnPropertyChanged(nameof(HasInstalledPlaylists));
        OnPropertyChanged(nameof(HasScanWarnings));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }
}
