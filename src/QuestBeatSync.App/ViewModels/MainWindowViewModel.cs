using System.Collections.ObjectModel;
using QuestBeatSync.Core.Models;
using QuestBeatSync.Infrastructure.Abstractions;
using QuestBeatSync.Infrastructure.Adb;

namespace QuestBeatSync.App.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    private readonly IQuestTransport _questTransport;
    private readonly AdbQuestTransportOptions _adbOptions;
    private readonly AdbSettingsStore _settingsStore;
    private NavigationItemViewModel? _selectedPage;
    private QuestDevice? _selectedDevice;
    private QuestDeviceDiscoveryStatus _discoveryStatus = QuestDeviceDiscoveryStatus.Success;
    private string? _errorMessage;
    private string? _configuredAdbPath;
    private string? _settingsMessage;
    private bool _isRefreshing;

    public MainWindowViewModel(
        IQuestTransport questTransport,
        AdbQuestTransportOptions adbOptions,
        AdbSettingsStore settingsStore)
    {
        _questTransport = questTransport ?? throw new ArgumentNullException(nameof(questTransport));
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
        _selectedPage = NavigationItems[0];

        RefreshDevicesCommand = new AsyncRelayCommand(RefreshDevicesAsync, () => !IsRefreshing);
        OpenSettingsCommand = new RelayCommand(OpenSettings);
        SaveAdbPathCommand = new AsyncRelayCommand(SaveAdbPathAsync);

    }

    public Task InitializeAsync() => RefreshDevicesAsync();

    public ObservableCollection<NavigationItemViewModel> NavigationItems { get; }

    public ObservableCollection<QuestDevice> Devices { get; }

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
            OnPropertyChanged(nameof(IsSettings));
            OnPropertyChanged(nameof(IsPlaceholderPage));
        }
    }

    public QuestDevice? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (Equals(_selectedDevice, value))
            {
                return;
            }

            _selectedDevice = value;
            OnPropertyChanged();
            NotifyDeviceDetailsChanged();
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

    public bool IsSettings => CurrentPageTitle == "Settings";

    public bool IsPlaceholderPage => !IsDashboard && !IsSettings;

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

    public bool HasDevices => Devices.Count > 0;

    public bool HasMultipleDevices => Devices.Count > 1;

    public bool HasSelectedDevice => SelectedDevice is not null;

    public bool IsAdbUnavailable => _discoveryStatus == QuestDeviceDiscoveryStatus.AdbNotAvailable;

    public bool HasErrorMessage => !string.IsNullOrWhiteSpace(ErrorMessage);

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

    public int SongCount => 0;

    public int PlaylistCount => 0;

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

            SelectedDevice = Devices.Count == 1 ? Devices[0] : null;
            NotifyDiscoveryChanged();
        }
        catch (Exception exception)
        {
            _discoveryStatus = QuestDeviceDiscoveryStatus.Error;
            ErrorMessage = exception.Message;
            Devices.Clear();
            SelectedDevice = null;
            NotifyDiscoveryChanged();
        }
        finally
        {
            IsRefreshing = false;
        }
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
}
