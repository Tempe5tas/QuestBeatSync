using System.Collections.ObjectModel;
using QuestBeatSync.Core.Models;

namespace QuestBeatSync.App.ViewModels;

public sealed class LibraryViewModel : ViewModelBase
{
    private bool _scanCompleted;
    private QuestBeatSaberScanResult _scanResult = QuestBeatSaberScanResult.Empty;
    private bool _isScanCurrent;
    private string? _scanStateMessage;

    public ObservableCollection<QuestInstalledMap> InstalledMaps { get; } = [];

    public ObservableCollection<QuestInstalledPlaylist> InstalledPlaylists { get; } = [];

    public ObservableCollection<QuestScanWarning> ScanWarnings { get; } = [];

    public bool ScanCompleted
    {
        get => _scanCompleted;
        private set => SetProperty(ref _scanCompleted, value);
    }

    public bool IsScanCurrent { get => _isScanCurrent; private set => SetProperty(ref _isScanCurrent, value); }

    public string ScanStateMessage => _scanStateMessage ?? (IsScanCurrent ? "Scan is current." : ScanCompleted ? "Last successful scan is stale." : "Quest library has not been scanned.");

    public int SongCount => InstalledMaps.Count;

    public int PlaylistCount => InstalledPlaylists.Count;

    public int ScanWarningCount => ScanWarnings.Count;

    public string CustomLevelsDiagnostic => !ScanCompleted ? "Not scanned" : _scanResult.CustomLevelsDetected ? "Detected" : "Not detected";

    public int FoldersDiscovered => _scanResult.CustomLevelFolderCount;

    public int MapsScanned => InstalledMaps.Count;

    public int HashIdentifiedCount => InstalledMaps.Count(map => map.IdentityStatus == QuestMapIdentityStatus.HashIdentified);

    public int LocalOrUnknownCount => InstalledMaps.Count(map => map.IdentityStatus != QuestMapIdentityStatus.HashIdentified);

    public bool HasInstalledMaps => InstalledMaps.Count > 0;

    public bool HasInstalledPlaylists => InstalledPlaylists.Count > 0;

    public bool HasScanWarnings => ScanWarnings.Count > 0;

    public QuestScanBinding? ScanBinding { get; private set; }

    public QuestBeatSaberScanResult ScanResult => _scanResult;

    public event EventHandler? Changed;

    public void MarkStale(string? message = null)
    {
        IsScanCurrent = false;
        _scanStateMessage = message;
        OnPropertyChanged(nameof(ScanStateMessage));
        OnPropertyChanged(nameof(CustomLevelsDiagnostic));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void MarkScanFailed(string message)
    {
        IsScanCurrent = false;
        _scanStateMessage = $"Scan failed: {message}";
        OnPropertyChanged(nameof(ScanStateMessage));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Reset(bool scanCompleted = false) => Apply(QuestBeatSaberScanResult.Empty, scanCompleted);

    public void Apply(QuestBeatSaberScanResult result, bool scanCompleted, string? deviceSerial = null)
    {
        _scanResult = result;
        ScanBinding = scanCompleted && !string.IsNullOrWhiteSpace(deviceSerial)
            ? QuestScanBinding.Capture(deviceSerial, result)
            : null;
        Replace(InstalledMaps, result.InstalledMaps);
        Replace(InstalledPlaylists, result.InstalledPlaylists);
        Replace(ScanWarnings, result.Warnings);
        ScanCompleted = scanCompleted;
        IsScanCurrent = scanCompleted;
        _scanStateMessage = scanCompleted ? "Scan completed successfully." : null;
        OnPropertyChanged(nameof(SongCount));
        OnPropertyChanged(nameof(PlaylistCount));
        OnPropertyChanged(nameof(ScanWarningCount));
        OnPropertyChanged(nameof(CustomLevelsDiagnostic));
        OnPropertyChanged(nameof(FoldersDiscovered));
        OnPropertyChanged(nameof(MapsScanned));
        OnPropertyChanged(nameof(HashIdentifiedCount));
        OnPropertyChanged(nameof(LocalOrUnknownCount));
        OnPropertyChanged(nameof(HasInstalledMaps));
        OnPropertyChanged(nameof(HasInstalledPlaylists));
        OnPropertyChanged(nameof(HasScanWarnings));
        OnPropertyChanged(nameof(ScanBinding));
        OnPropertyChanged(nameof(ScanResult));
        OnPropertyChanged(nameof(ScanStateMessage));
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
