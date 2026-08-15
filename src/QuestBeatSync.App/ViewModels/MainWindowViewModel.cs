using System.Collections.ObjectModel;
using QuestBeatSync.Core.Models;
using QuestBeatSync.Infrastructure.Abstractions;
using QuestBeatSync.Infrastructure.Adb;
using QuestBeatSync.Infrastructure.Importing;

namespace QuestBeatSync.App.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    private readonly IQuestTransport _questTransport;
    private readonly IQuestBeatSaberScanner _beatSaberScanner;
    private readonly ILocalPlaylistImporter _playlistImporter;
    private readonly AdbQuestTransportOptions _adbOptions;
    private readonly AdbSettingsStore _settingsStore;
    private LocalPlaylistLibraryState _localPlaylistState = new([]);
    private CancellationTokenSource? _scanCancellationSource;
    private NavigationItemViewModel? _selectedPage;
    private QuestDevice? _selectedDevice;
    private Playlist? _selectedImportedPlaylist;
    private QuestDeviceDiscoveryStatus _discoveryStatus = QuestDeviceDiscoveryStatus.Success;
    private string? _errorMessage;
    private string? _environmentError;
    private string? _configuredAdbPath;
    private string? _settingsMessage;
    private bool _isRefreshing;
    private bool _isEnvironmentScanning;
    private bool _beatSaberDetected;
    private bool _songCoreDetected;
    private bool _playlistManagerDetected;
    private bool _environmentScanCompleted;
    private bool _isImportingPlaylists;

    public MainWindowViewModel(
        IQuestTransport questTransport,
        IQuestBeatSaberScanner beatSaberScanner,
        ILocalPlaylistImporter playlistImporter,
        AdbQuestTransportOptions adbOptions,
        AdbSettingsStore settingsStore)
    {
        _questTransport = questTransport ?? throw new ArgumentNullException(nameof(questTransport));
        _beatSaberScanner = beatSaberScanner ?? throw new ArgumentNullException(nameof(beatSaberScanner));
        _playlistImporter = playlistImporter ?? throw new ArgumentNullException(nameof(playlistImporter));
        _adbOptions = adbOptions ?? throw new ArgumentNullException(nameof(adbOptions));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _configuredAdbPath = adbOptions.ConfiguredExecutablePath;

        NavigationItems = new ObservableCollection<NavigationItemViewModel>
        {
            new("Dashboard"),
            new("Playlists"),
            new("Library"),
            new("Backup"),
            new("Settings")
        };
        Devices = [];
        InstalledMaps = [];
        InstalledPlaylists = [];
        ImportedPlaylists = [];
        PlaylistImportErrors = [];
        ScanWarnings = [];
        _selectedPage = NavigationItems[0];

        RefreshDevicesCommand = new AsyncRelayCommand(RefreshDevicesAsync, () => !IsRefreshing);
        OpenSettingsCommand = new RelayCommand(OpenSettings);
        SaveAdbPathCommand = new AsyncRelayCommand(SaveAdbPathAsync);
    }

    public Task InitializeAsync() => RefreshDevicesAsync();

    public ObservableCollection<NavigationItemViewModel> NavigationItems { get; }

    public ObservableCollection<QuestDevice> Devices { get; }

    public ObservableCollection<QuestInstalledMap> InstalledMaps { get; }

    public ObservableCollection<QuestInstalledPlaylist> InstalledPlaylists { get; }

    public ObservableCollection<Playlist> ImportedPlaylists { get; }

    public ObservableCollection<string> PlaylistImportErrors { get; }

    public ObservableCollection<QuestScanWarning> ScanWarnings { get; }

    public AsyncRelayCommand RefreshDevicesCommand { get; }

    public RelayCommand OpenSettingsCommand { get; }

    public AsyncRelayCommand SaveAdbPathCommand { get; }

    public NavigationItemViewModel? SelectedPage
    {
        get => _selectedPage;
        set
        {
            if (Equals(_selectedPage, value))
            {
                return;
            }

            _selectedPage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CurrentPageTitle));
            OnPropertyChanged(nameof(IsDashboard));
            OnPropertyChanged(nameof(IsPlaylists));
            OnPropertyChanged(nameof(IsLibrary));
            OnPropertyChanged(nameof(IsSettings));
            OnPropertyChanged(nameof(IsPlaceholderPage));
        }
    }

    public QuestDevice? SelectedDevice
    {
        get => _selectedDevice;
        set => SetSelectedDevice(value, startScan: true);
    }

    public Playlist? SelectedImportedPlaylist
    {
        get => _selectedImportedPlaylist;
        set
        {
            if (ReferenceEquals(_selectedImportedPlaylist, value))
            {
                return;
            }

            _selectedImportedPlaylist = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedImportedPlaylist));
            OnPropertyChanged(nameof(SelectedPlaylistAuthorDisplay));
            OnPropertyChanged(nameof(SelectedPlaylistEntryCount));
            OnPropertyChanged(nameof(SelectedPlaylistUniqueHashCount));
            OnPropertyChanged(nameof(SelectedPlaylistDuplicateReferenceCount));
        }
    }

    public string? ConfiguredAdbPath
    {
        get => _configuredAdbPath;
        set
        {
            if (_configuredAdbPath == value)
            {
                return;
            }

            _configuredAdbPath = value;
            SettingsMessage = null;
            OnPropertyChanged();
        }
    }

    public string CurrentPageTitle => SelectedPage?.Title ?? "Dashboard";

    public bool IsDashboard => CurrentPageTitle == "Dashboard";

    public bool IsPlaylists => CurrentPageTitle == "Playlists";

    public bool IsLibrary => CurrentPageTitle == "Library";

    public bool IsSettings => CurrentPageTitle == "Settings";

    public bool IsPlaceholderPage => !IsDashboard && !IsPlaylists && !IsLibrary && !IsSettings;

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (_isRefreshing == value)
            {
                return;
            }

            _isRefreshing = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DeviceStatus));
            RefreshDevicesCommand?.RaiseCanExecuteChanged();
        }
    }

    public bool IsEnvironmentScanning
    {
        get => _isEnvironmentScanning;
        private set
        {
            if (_isEnvironmentScanning == value)
            {
                return;
            }

            _isEnvironmentScanning = value;
            OnPropertyChanged();
        }
    }

    public bool IsImportingPlaylists
    {
        get => _isImportingPlaylists;
        private set
        {
            if (_isImportingPlaylists == value)
            {
                return;
            }

            _isImportingPlaylists = value;
            OnPropertyChanged();
        }
    }

    public bool HasDevices => Devices.Count > 0;

    public bool HasMultipleDevices => Devices.Count > 1;

    public bool HasSelectedDevice => SelectedDevice is not null;

    public bool IsAdbUnavailable => _discoveryStatus == QuestDeviceDiscoveryStatus.AdbNotAvailable;

    public bool HasErrorMessage => !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool HasEnvironmentError => !string.IsNullOrWhiteSpace(EnvironmentError);

    public bool HasInstalledMaps => InstalledMaps.Count > 0;

    public bool HasInstalledPlaylists => InstalledPlaylists.Count > 0;

    public bool HasScanWarnings => ScanWarnings.Count > 0;

    public bool HasImportedPlaylists => ImportedPlaylists.Count > 0;

    public bool HasSelectedImportedPlaylist => SelectedImportedPlaylist is not null;

    public bool HasPlaylistImportErrors => PlaylistImportErrors.Count > 0;

    public string PlaylistImportErrorText => string.Join(Environment.NewLine, PlaylistImportErrors);

    public bool EnvironmentScanCompleted => _environmentScanCompleted;

    public string DeviceStatus
    {
        get
        {
            if (IsRefreshing)
            {
                return "Checking for devices...";
            }

            return _discoveryStatus switch
            {
                QuestDeviceDiscoveryStatus.AdbNotAvailable => "ADB not available",
                QuestDeviceDiscoveryStatus.TimedOut => "ADB command timed out",
                QuestDeviceDiscoveryStatus.Error => "ADB error",
                _ when Devices.Count == 0 => "No Quest connected",
                _ when Devices.Count > 1 && SelectedDevice is null => "Select a device",
                _ when SelectedDevice?.ConnectionState == QuestConnectionState.Unauthorized => "Device unauthorized",
                _ when SelectedDevice?.ConnectionState == QuestConnectionState.Offline => "Device offline",
                _ when SelectedDevice?.IsConnected == true => "Device connected",
                _ => "Device state unknown"
            };
        }
    }

    public string BeatSaberStatus => !EnvironmentScanCompleted
        ? "Beat Saber not scanned"
        : BeatSaberDetected ? "Beat Saber detected" : "Beat Saber not detected";

    public string SongCoreStatus => !EnvironmentScanCompleted
        ? "SongCore not scanned"
        : SongCoreDetected ? "SongCore detected" : "SongCore not detected";

    public string PlaylistManagerStatus => !EnvironmentScanCompleted
        ? "PlaylistManager not scanned"
        : PlaylistManagerDetected
            ? "PlaylistManager detected"
            : "PlaylistManager not detected";

    public bool BeatSaberDetected
    {
        get => _beatSaberDetected;
        private set
        {
            if (_beatSaberDetected == value)
            {
                return;
            }

            _beatSaberDetected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BeatSaberStatus));
        }
    }

    public bool SongCoreDetected
    {
        get => _songCoreDetected;
        private set
        {
            if (_songCoreDetected == value)
            {
                return;
            }

            _songCoreDetected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SongCoreStatus));
        }
    }

    public bool PlaylistManagerDetected
    {
        get => _playlistManagerDetected;
        private set
        {
            if (_playlistManagerDetected == value)
            {
                return;
            }

            _playlistManagerDetected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PlaylistManagerStatus));
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (_errorMessage == value)
            {
                return;
            }

            _errorMessage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasErrorMessage));
        }
    }

    public string? EnvironmentError
    {
        get => _environmentError;
        private set
        {
            if (_environmentError == value)
            {
                return;
            }

            _environmentError = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasEnvironmentError));
        }
    }

    public string? SettingsMessage
    {
        get => _settingsMessage;
        private set
        {
            if (_settingsMessage == value)
            {
                return;
            }

            _settingsMessage = value;
            OnPropertyChanged();
        }
    }

    public string SelectedSerial => SelectedDevice?.Serial ?? string.Empty;

    public string SelectedConnectionState => SelectedDevice?.ConnectionState.ToString() ?? string.Empty;

    public string SelectedTransport => SelectedDevice?.TransportKind switch
    {
        QuestTransportKind.Usb => "USB",
        QuestTransportKind.Network => "Network",
        _ => "Unknown"
    };

    public string SelectedModel => string.IsNullOrWhiteSpace(SelectedDevice?.AndroidModel)
        ? "Unknown"
        : SelectedDevice.AndroidModel;

    public int SongCount => InstalledMaps.Count;

    public int PlaylistCount => InstalledPlaylists.Count;

    public int ScanWarningCount => ScanWarnings.Count;

    public int TotalPlaylistReferences => _localPlaylistState.TotalPlaylistReferences;

    public int UniqueRequiredHashes => _localPlaylistState.UniqueRequiredHashes;

    public int DuplicateReferences => _localPlaylistState.DuplicateReferences;

    public string SelectedPlaylistAuthorDisplay => string.IsNullOrWhiteSpace(SelectedImportedPlaylist?.Author)
        ? "by Unknown author"
        : $"by {SelectedImportedPlaylist.Author}";

    public int SelectedPlaylistEntryCount => SelectedImportedPlaylist?.EntryCount ?? 0;

    public int SelectedPlaylistUniqueHashCount => SelectedImportedPlaylist?.UniqueHashCount ?? 0;

    public int SelectedPlaylistDuplicateReferenceCount =>
        SelectedImportedPlaylist?.DuplicateReferenceCount ?? 0;

    public async Task ImportPlaylistFilesAsync(IEnumerable<string> filePaths)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        IsImportingPlaylists = true;
        PlaylistImportErrors.Clear();
        OnPropertyChanged(nameof(HasPlaylistImportErrors));
        OnPropertyChanged(nameof(PlaylistImportErrorText));

        try
        {
            var results = await _playlistImporter.ImportAsync(filePaths);
            Playlist? firstImported = null;

            foreach (var result in results)
            {
                if (result.IsSuccess)
                {
                    firstImported ??= result.Playlist;
                    ImportedPlaylists.Add(result.Playlist!);
                }
                else
                {
                    var filename = string.IsNullOrWhiteSpace(result.FilePath)
                        ? "Playlist"
                        : Path.GetFileName(result.FilePath);
                    PlaylistImportErrors.Add($"{filename}: {result.ErrorMessage}");
                }
            }

            if (firstImported is not null)
            {
                SelectedImportedPlaylist = firstImported;
            }

            NotifyLocalPlaylistStateChanged();
        }
        catch (Exception exception)
        {
            PlaylistImportErrors.Add($"Playlist import failed: {exception.Message}");
            NotifyLocalPlaylistStateChanged();
        }
        finally
        {
            IsImportingPlaylists = false;
        }
    }

    private async Task RefreshDevicesAsync()
    {
        IsRefreshing = true;
        ErrorMessage = null;

        try
        {
            var result = await _questTransport.GetDevicesAsync();
            _discoveryStatus = result.Status;
            ErrorMessage = result.ErrorMessage;

            Devices.Clear();
            foreach (var device in result.Devices)
            {
                Devices.Add(device);
            }

            SetSelectedDevice(Devices.Count == 1 ? Devices[0] : null, startScan: false);
            NotifyDiscoveryChanged();
            await RefreshEnvironmentAsync(SelectedDevice);
        }
        catch (Exception exception)
        {
            _discoveryStatus = QuestDeviceDiscoveryStatus.Error;
            ErrorMessage = exception.Message;
            Devices.Clear();
            SetSelectedDevice(null, startScan: false);
            ApplyScanResult(QuestBeatSaberScanResult.Empty);
            NotifyDiscoveryChanged();
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private async Task RefreshEnvironmentAsync(QuestDevice? device)
    {
        _scanCancellationSource?.Cancel();
        var cancellationSource = new CancellationTokenSource();
        _scanCancellationSource = cancellationSource;

        EnvironmentError = null;
        SetEnvironmentScanCompleted(false);
        ApplyScanResult(QuestBeatSaberScanResult.Empty);
        if (device?.IsConnected != true)
        {
            return;
        }

        IsEnvironmentScanning = true;
        try
        {
            var result = await _beatSaberScanner.ScanAsync(device, cancellationSource.Token);
            if (!cancellationSource.IsCancellationRequested)
            {
                SetEnvironmentScanCompleted(true);
                ApplyScanResult(result);
            }
        }
        catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!cancellationSource.IsCancellationRequested)
            {
                EnvironmentError = exception.Message;
            }
        }
        finally
        {
            if (ReferenceEquals(_scanCancellationSource, cancellationSource))
            {
                IsEnvironmentScanning = false;
            }

            cancellationSource.Dispose();
        }
    }

    private void ApplyScanResult(QuestBeatSaberScanResult result)
    {
        BeatSaberDetected = result.BeatSaberDetected;
        SongCoreDetected = result.SongCoreDetected;
        PlaylistManagerDetected = result.PlaylistManagerDetected;

        ReplaceContents(InstalledMaps, result.InstalledMaps);
        ReplaceContents(InstalledPlaylists, result.InstalledPlaylists);
        ReplaceContents(ScanWarnings, result.Warnings);

        OnPropertyChanged(nameof(SongCount));
        OnPropertyChanged(nameof(PlaylistCount));
        OnPropertyChanged(nameof(ScanWarningCount));
        OnPropertyChanged(nameof(HasInstalledMaps));
        OnPropertyChanged(nameof(HasInstalledPlaylists));
        OnPropertyChanged(nameof(HasScanWarnings));
    }

    private void SetEnvironmentScanCompleted(bool value)
    {
        if (_environmentScanCompleted == value)
        {
            return;
        }

        _environmentScanCompleted = value;
        OnPropertyChanged(nameof(EnvironmentScanCompleted));
        OnPropertyChanged(nameof(BeatSaberStatus));
        OnPropertyChanged(nameof(SongCoreStatus));
        OnPropertyChanged(nameof(PlaylistManagerStatus));
    }

    private async Task SaveAdbPathAsync()
    {
        try
        {
            _settingsStore.SaveConfiguredPath(ConfiguredAdbPath);
            _adbOptions.ConfiguredExecutablePath = string.IsNullOrWhiteSpace(ConfiguredAdbPath)
                ? null
                : ConfiguredAdbPath.Trim();
            SettingsMessage = "ADB path saved.";
            await RefreshDevicesAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SettingsMessage = $"Could not save ADB path: {exception.Message}";
        }
    }

    private void SetSelectedDevice(QuestDevice? value, bool startScan)
    {
        if (Equals(_selectedDevice, value))
        {
            return;
        }

        _selectedDevice = value;
        OnPropertyChanged(nameof(SelectedDevice));
        NotifyDeviceDetailsChanged();

        if (startScan)
        {
            _ = RefreshEnvironmentAsync(value);
        }
    }

    private void OpenSettings() =>
        SelectedPage = NavigationItems.First(item => item.Title == "Settings");

    private void NotifyDiscoveryChanged()
    {
        OnPropertyChanged(nameof(HasDevices));
        OnPropertyChanged(nameof(HasMultipleDevices));
        OnPropertyChanged(nameof(IsAdbUnavailable));
        OnPropertyChanged(nameof(DeviceStatus));
        NotifyDeviceDetailsChanged();
    }

    private void NotifyDeviceDetailsChanged()
    {
        OnPropertyChanged(nameof(HasSelectedDevice));
        OnPropertyChanged(nameof(SelectedSerial));
        OnPropertyChanged(nameof(SelectedConnectionState));
        OnPropertyChanged(nameof(SelectedTransport));
        OnPropertyChanged(nameof(SelectedModel));
        OnPropertyChanged(nameof(DeviceStatus));
    }

    private static void ReplaceContents<T>(ObservableCollection<T> collection, IEnumerable<T> items)
    {
        collection.Clear();
        foreach (var item in items)
        {
            collection.Add(item);
        }
    }

    private void NotifyLocalPlaylistStateChanged()
    {
        _localPlaylistState = new LocalPlaylistLibraryState(ImportedPlaylists);
        OnPropertyChanged(nameof(HasImportedPlaylists));
        OnPropertyChanged(nameof(HasPlaylistImportErrors));
        OnPropertyChanged(nameof(PlaylistImportErrorText));
        OnPropertyChanged(nameof(TotalPlaylistReferences));
        OnPropertyChanged(nameof(UniqueRequiredHashes));
        OnPropertyChanged(nameof(DuplicateReferences));
    }
}
