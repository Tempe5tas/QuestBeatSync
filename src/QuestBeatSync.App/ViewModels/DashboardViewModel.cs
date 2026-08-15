using System.Collections.ObjectModel;
using QuestBeatSync.Core.Models;
using QuestBeatSync.Infrastructure.Abstractions;

namespace QuestBeatSync.App.ViewModels;

public sealed class DashboardViewModel : ViewModelBase
{
    private readonly IQuestTransport _transport;
    private readonly IQuestBeatSaberScanner _scanner;
    private readonly object _scanGate = new();
    private CancellationTokenSource? _scanSource;
    private QuestDevice? _selectedDevice;
    private QuestDeviceDiscoveryStatus _discoveryStatus;
    private string? _errorMessage;
    private string? _environmentError;
    private bool _isRefreshing;
    private bool _isEnvironmentScanning;
    private bool _beatSaberDetected;
    private bool _songCoreDetected;
    private bool _playlistManagerDetected;

    public DashboardViewModel(
        IQuestTransport transport,
        IQuestBeatSaberScanner scanner,
        LibraryViewModel library,
        Func<Exception, Task> errorHandler)
    {
        _transport = transport;
        _scanner = scanner;
        Library = library;
        RefreshDevicesCommand = new AsyncRelayCommand(RefreshDevicesAsync, () => !IsRefreshing, errorHandler: errorHandler);
    }

    public LibraryViewModel Library { get; }
    public ObservableCollection<QuestDevice> Devices { get; } = [];
    public AsyncRelayCommand RefreshDevicesCommand { get; }
    public event EventHandler? AdbUnavailable;

    public QuestDevice? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (SetProperty(ref _selectedDevice, value))
            {
                NotifyDevice();
                _ = RefreshEnvironmentAsync(value);
            }
        }
    }

    public bool IsRefreshing { get => _isRefreshing; private set { if (SetProperty(ref _isRefreshing, value)) { OnPropertyChanged(nameof(DeviceStatus)); RefreshDevicesCommand.RaiseCanExecuteChanged(); } } }
    public bool IsEnvironmentScanning { get => _isEnvironmentScanning; private set => SetProperty(ref _isEnvironmentScanning, value); }
    public bool HasDevices => Devices.Count > 0;
    public bool HasMultipleDevices => Devices.Count > 1;
    public bool HasSelectedDevice => SelectedDevice is not null;
    public bool IsAdbUnavailable => _discoveryStatus == QuestDeviceDiscoveryStatus.AdbNotAvailable;
    public bool HasErrorMessage => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasEnvironmentError => !string.IsNullOrWhiteSpace(EnvironmentError);
    public string? ErrorMessage { get => _errorMessage; private set { if (SetProperty(ref _errorMessage, value)) OnPropertyChanged(nameof(HasErrorMessage)); } }
    public string? EnvironmentError { get => _environmentError; private set { if (SetProperty(ref _environmentError, value)) OnPropertyChanged(nameof(HasEnvironmentError)); } }
    public string DeviceStatus => IsRefreshing ? "Checking for devices..." : _discoveryStatus switch
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
    public string SelectedSerial => SelectedDevice?.Serial ?? string.Empty;
    public string SelectedConnectionState => SelectedDevice?.ConnectionState.ToString() ?? string.Empty;
    public string SelectedTransport => SelectedDevice?.TransportKind switch { QuestTransportKind.Usb => "USB", QuestTransportKind.Network => "Network", _ => "Unknown" };
    public string SelectedModel => string.IsNullOrWhiteSpace(SelectedDevice?.AndroidModel) ? "Unknown" : SelectedDevice.AndroidModel;
    public string BeatSaberStatus => !Library.ScanCompleted ? "Beat Saber not scanned" : _beatSaberDetected ? "Beat Saber detected" : "Beat Saber not detected";
    public string SongCoreStatus => !Library.ScanCompleted ? "SongCore not scanned" : _songCoreDetected ? "SongCore detected" : "SongCore not detected";
    public string PlaylistManagerStatus => !Library.ScanCompleted ? "PlaylistManager not scanned" : _playlistManagerDetected ? "PlaylistManager detected" : "PlaylistManager not detected";

    public async Task InitializeAsync() => await RefreshDevicesAsync();

    public async Task RefreshDevicesAsync()
    {
        IsRefreshing = true;
        ErrorMessage = null;
        try
        {
            var result = await _transport.GetDevicesAsync();
            _discoveryStatus = result.Status;
            ErrorMessage = result.ErrorMessage;
            Replace(Devices, result.Devices);
            SetSelectedDevice(Devices.Count == 1 ? Devices[0] : null);
            NotifyDiscovery();
            if (IsAdbUnavailable) AdbUnavailable?.Invoke(this, EventArgs.Empty);
            await RefreshEnvironmentAsync(SelectedDevice);
        }
        catch (Exception exception)
        {
            _discoveryStatus = QuestDeviceDiscoveryStatus.Error;
            ErrorMessage = exception.Message;
            Devices.Clear();
            SetSelectedDevice(null);
            Library.Reset();
        }
        finally { IsRefreshing = false; }
    }

    private async Task RefreshEnvironmentAsync(QuestDevice? device)
    {
        var current = new CancellationTokenSource();
        CancellationTokenSource? previous;
        lock (_scanGate) { previous = _scanSource; _scanSource = current; }
        try
        {
            try { previous?.Cancel(); } catch (ObjectDisposedException) { }
            EnvironmentError = null;
            Library.Reset();
            SetDetection(false, false, false);
            if (device?.IsConnected != true) return;
            IsEnvironmentScanning = true;
            var result = await _scanner.ScanAsync(device, current.Token);
            if (!current.IsCancellationRequested)
            {
                SetDetection(result.BeatSaberDetected, result.SongCoreDetected, result.PlaylistManagerDetected);
                Library.Apply(result, scanCompleted: true, device.Serial);
            }
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested) { }
        catch (Exception exception) { if (!current.IsCancellationRequested) EnvironmentError = exception.Message; }
        finally
        {
            var owner = false;
            lock (_scanGate) { if (ReferenceEquals(_scanSource, current)) { _scanSource = null; owner = true; } }
            if (owner) IsEnvironmentScanning = false;
            current.Dispose();
        }
    }

    private void SetSelectedDevice(QuestDevice? value) { _selectedDevice = value; OnPropertyChanged(nameof(SelectedDevice)); NotifyDevice(); }
    private void SetDetection(bool beatSaber, bool songCore, bool playlistManager) { _beatSaberDetected = beatSaber; _songCoreDetected = songCore; _playlistManagerDetected = playlistManager; OnPropertyChanged(nameof(BeatSaberStatus)); OnPropertyChanged(nameof(SongCoreStatus)); OnPropertyChanged(nameof(PlaylistManagerStatus)); }
    private void NotifyDiscovery() { OnPropertyChanged(nameof(HasDevices)); OnPropertyChanged(nameof(HasMultipleDevices)); OnPropertyChanged(nameof(IsAdbUnavailable)); OnPropertyChanged(nameof(DeviceStatus)); NotifyDevice(); }
    private void NotifyDevice() { OnPropertyChanged(nameof(HasSelectedDevice)); OnPropertyChanged(nameof(SelectedSerial)); OnPropertyChanged(nameof(SelectedConnectionState)); OnPropertyChanged(nameof(SelectedTransport)); OnPropertyChanged(nameof(SelectedModel)); OnPropertyChanged(nameof(DeviceStatus)); }
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items) { target.Clear(); foreach (var item in items) target.Add(item); }
}
