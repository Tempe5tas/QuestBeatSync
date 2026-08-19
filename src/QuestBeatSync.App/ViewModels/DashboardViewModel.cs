using System.Collections.ObjectModel;
using QuestBeatSync.Core.Models;
using QuestBeatSync.Infrastructure.Abstractions;
using QuestBeatSync.Infrastructure.Adb;

namespace QuestBeatSync.App.ViewModels;

public sealed class DashboardViewModel : ViewModelBase
{
    private readonly IQuestTransport _transport;
    private readonly IQuestBeatSaberScanner _scanner;
    private readonly object _scanGate = new();
    private CancellationTokenSource? _scanSource;
    private long _scanGeneration;
    private QuestDevice? _selectedDevice;
    private QuestDeviceDiscoveryStatus _discoveryStatus;
    private string? _errorMessage;
    private string? _environmentError;
    private bool _isRefreshing;
    private bool _isEnvironmentScanning;
    private bool _beatSaberDetected;
    private bool _songCoreDetected;
    private bool _playlistManagerDetected;
    private readonly AdbEnvironmentManager? _adbEnvironment;
    private readonly IAdbConnectionService? _connectionService;
    private string _wirelessHost = string.Empty;
    private int _wirelessPort = 5555;
    private string? _wirelessStatus;
    private bool _isConnecting;

    public DashboardViewModel(
        IQuestTransport transport,
        IQuestBeatSaberScanner scanner,
        LibraryViewModel library,
        Func<Exception, Task> errorHandler,
        AdbEnvironmentManager? adbEnvironment = null,
        IAdbConnectionService? connectionService = null)
    {
        _transport = transport;
        _scanner = scanner;
        Library = library;
        _adbEnvironment = adbEnvironment;
        _connectionService = connectionService;
        RefreshDevicesCommand = new AsyncRelayCommand(RefreshDevicesAsync, () => !IsRefreshing, errorHandler: errorHandler);
        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => CanConnect, errorHandler: errorHandler);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, () => CanConnect, errorHandler: errorHandler);
        EnableWirelessCommand = new AsyncRelayCommand(EnableWirelessAsync, () => CanEnableWireless, errorHandler: errorHandler);
        ScanLibraryCommand = new AsyncRelayCommand(ScanSelectedDeviceAsync, () => CanScanLibrary, errorHandler: errorHandler);
    }

    public LibraryViewModel Library { get; }
    public ObservableCollection<QuestDevice> Devices { get; } = [];
    public AsyncRelayCommand RefreshDevicesCommand { get; }
    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand DisconnectCommand { get; }
    public AsyncRelayCommand EnableWirelessCommand { get; }
    public AsyncRelayCommand ScanLibraryCommand { get; }
    public event EventHandler? AdbUnavailable;

    public QuestDevice? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (SetProperty(ref _selectedDevice, value))
            {
                CancelActiveScan();
                IsEnvironmentScanning = false;
                Library.MarkStale(value is null ? "No Quest is selected." : "Selected Quest changed; scan again before planning.");
                NotifyDevice();
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
    public string BeatSaberVersionStatus => Library.ScanResult.BeatSaberPackageVersion is { } version
        ? $"Beat Saber {version.VersionName} (versionCode {version.VersionCode?.ToString() ?? "unknown"})"
        : "Beat Saber package version not scanned";
    public string SongCoreStatus => !Library.ScanCompleted ? "SongCore not scanned" : _songCoreDetected ? "SongCore detected" : "SongCore not detected";
    public string PlaylistManagerStatus => !Library.ScanCompleted ? "PlaylistManager not scanned" : _playlistManagerDetected ? "PlaylistManager detected" : "PlaylistManager not detected";
    public string WirelessHost { get => _wirelessHost; set { if (SetProperty(ref _wirelessHost, value)) ConnectCommand.RaiseCanExecuteChanged(); } }
    public int WirelessPort { get => _wirelessPort; set { if (SetProperty(ref _wirelessPort, value)) { ConnectCommand.RaiseCanExecuteChanged(); EnableWirelessCommand.RaiseCanExecuteChanged(); } } }
    public string? WirelessStatus { get => _wirelessStatus; private set { if (SetProperty(ref _wirelessStatus, value)) OnPropertyChanged(nameof(HasWirelessStatus)); } }
    public bool HasWirelessStatus => !string.IsNullOrWhiteSpace(WirelessStatus);
    public bool CanConnect => !_isConnecting && _connectionService is not null && _adbEnvironment?.Current.IsReady == true;
    public bool CanEnableWireless => !_isConnecting && _connectionService is not null && _adbEnvironment?.Current.IsReady == true && SelectedDevice is { IsConnected: true, TransportKind: QuestTransportKind.Usb };
    public bool CanScanLibrary => !IsEnvironmentScanning && SelectedDevice?.IsConnected == true;

    public async Task InitializeAsync() => await RefreshDevicesAsync();

    public async Task<QuestBeatSaberScanResult> ScanSelectedDeviceAsync()
    {
        var device = SelectedDevice;
        if (device?.IsConnected != true) throw new InvalidOperationException("Select a connected Quest before scanning its library.");
        return await ScanDeviceAsync(device);
    }

    public async Task RefreshDevicesAsync()
    {
        await OnUiThreadAsync(() => { IsRefreshing = true; ErrorMessage = null; });
        try
        {
            var result = await _transport.GetDevicesAsync();
            var previousSerial = SelectedDevice?.Serial;
            await OnUiThreadAsync(() =>
            {
                _discoveryStatus = result.Status;
                ErrorMessage = result.ErrorMessage;
                Replace(Devices, result.Devices);
                var selection = previousSerial is null ? (Devices.Count == 1 ? Devices[0] : null) : Devices.FirstOrDefault(device => string.Equals(device.Serial, previousSerial, StringComparison.Ordinal));
                SetSelectedDevice(selection);
                NotifyDiscovery();
                if (IsAdbUnavailable) AdbUnavailable?.Invoke(this, EventArgs.Empty);
            });
        }
        catch (Exception exception)
        {
            await OnUiThreadAsync(() =>
            {
                _discoveryStatus = QuestDeviceDiscoveryStatus.Error;
                ErrorMessage = exception.Message;
                Devices.Clear();
                SetSelectedDevice(null);
                Library.MarkStale("Device discovery failed; the displayed library is retained but stale.");
            });
        }
        finally { await OnUiThreadAsync(() => IsRefreshing = false); }
    }

    private async Task ConnectAsync()
    {
        if (!AdbNetworkEndpoint.TryCreate(WirelessHost, WirelessPort, out var endpoint, out var error)) { WirelessStatus = error; return; }
        _isConnecting = true; NotifyWirelessCommands();
        try { var result = await _connectionService!.ConnectAsync(endpoint!); await OnUiThreadAsync(() => WirelessStatus = result.IsSuccess ? (result.Outcome == AdbConnectionOutcome.AlreadyConnected ? "Already connected." : "Connected.") : result.ErrorMessage); if (result.IsSuccess) await RefreshDevicesAsync(); }
        finally { await OnUiThreadAsync(() => { _isConnecting = false; NotifyWirelessCommands(); }); }
    }

    private async Task DisconnectAsync()
    {
        if (!AdbNetworkEndpoint.TryCreate(WirelessHost, WirelessPort, out var endpoint, out var error)) { WirelessStatus = error; return; }
        _isConnecting = true; NotifyWirelessCommands();
        try { var result = await _connectionService!.DisconnectAsync(endpoint!); await OnUiThreadAsync(() => WirelessStatus = result.IsSuccess ? "Disconnected." : result.ErrorMessage); if (result.IsSuccess) await RefreshDevicesAsync(); }
        finally { await OnUiThreadAsync(() => { _isConnecting = false; NotifyWirelessCommands(); }); }
    }

    private async Task EnableWirelessAsync()
    {
        var result = await _connectionService!.EnableWirelessAdbAsync(SelectedDevice!, WirelessPort);
        await OnUiThreadAsync(() => WirelessStatus = result.IsSuccess ? $"Wireless ADB has been enabled on port {WirelessPort}. Enter the Quest's Wi-Fi IP address to connect." : result.ErrorMessage);
    }

    private async Task<QuestBeatSaberScanResult> ScanDeviceAsync(QuestDevice device)
    {
        var current = new CancellationTokenSource();
        CancellationTokenSource? previous;
        long generation;
        lock (_scanGate) { previous = _scanSource; _scanSource = current; generation = ++_scanGeneration; }
        try
        {
            try { previous?.Cancel(); } catch (ObjectDisposedException) { }
            await OnUiThreadAsync(() =>
            {
                ThrowIfScanIsObsolete(current, generation);
                EnvironmentError = null;
                IsEnvironmentScanning = true;
            });
            var result = await _scanner.ScanAsync(device, current.Token);
            current.Token.ThrowIfCancellationRequested();
            ThrowIfScanIsObsolete(current, generation);
            if (!string.Equals(SelectedDevice?.Serial, device.Serial, StringComparison.Ordinal)) throw new OperationCanceledException("Selected Quest changed during scan.", current.Token);
            await OnUiThreadAsync(() =>
            {
                ThrowIfScanIsObsolete(current, generation);
                SetDetection(result.BeatSaberDetected, result.SongCoreDetected, result.PlaylistManagerDetected);
                Library.Apply(result, scanCompleted: true, device.Serial);
            });
            return result;
        }
        catch (OperationCanceledException)
        {
            if (OwnsScan(current, generation))
                await OnUiThreadAsync(() =>
                {
                    if (OwnsScan(current, generation))
                        Library.MarkStale("Quest library scan was canceled; the previous result is retained.");
                });
            throw;
        }
        catch (Exception exception)
        {
            if (!OwnsScan(current, generation))
                throw new OperationCanceledException("Quest library scan was superseded.", exception, current.Token);
            await OnUiThreadAsync(() =>
            {
                ThrowIfScanIsObsolete(current, generation);
                EnvironmentError = exception.Message;
                Library.MarkScanFailed(exception.Message);
            });
            throw;
        }
        finally
        {
            var owner = false;
            lock (_scanGate) { if (ReferenceEquals(_scanSource, current) && _scanGeneration == generation) { _scanSource = null; owner = true; } }
            if (owner) await OnUiThreadAsync(() => IsEnvironmentScanning = false);
            current.Dispose();
        }
    }

    private void SetSelectedDevice(QuestDevice? value)
    {
        var changed = !string.Equals(_selectedDevice?.Serial, value?.Serial, StringComparison.Ordinal);
        if (changed)
        {
            CancelActiveScan();
            IsEnvironmentScanning = false;
        }
        _selectedDevice = value;
        OnPropertyChanged(nameof(SelectedDevice));
        if (changed) Library.MarkStale(value is null ? "No Quest is selected; the displayed library is retained but stale." : "Device selected. Scan the Quest library before planning.");
        NotifyDevice();
    }
    private void SetDetection(bool beatSaber, bool songCore, bool playlistManager) { _beatSaberDetected = beatSaber; _songCoreDetected = songCore; _playlistManagerDetected = playlistManager; OnPropertyChanged(nameof(BeatSaberStatus)); OnPropertyChanged(nameof(BeatSaberVersionStatus)); OnPropertyChanged(nameof(SongCoreStatus)); OnPropertyChanged(nameof(PlaylistManagerStatus)); }
    private void NotifyDiscovery() { OnPropertyChanged(nameof(HasDevices)); OnPropertyChanged(nameof(HasMultipleDevices)); OnPropertyChanged(nameof(IsAdbUnavailable)); OnPropertyChanged(nameof(DeviceStatus)); NotifyDevice(); }
    private void NotifyDevice() { OnPropertyChanged(nameof(HasSelectedDevice)); OnPropertyChanged(nameof(SelectedSerial)); OnPropertyChanged(nameof(SelectedConnectionState)); OnPropertyChanged(nameof(SelectedTransport)); OnPropertyChanged(nameof(SelectedModel)); OnPropertyChanged(nameof(DeviceStatus)); NotifyWirelessCommands(); }
    private void NotifyWirelessCommands() { OnPropertyChanged(nameof(CanConnect)); OnPropertyChanged(nameof(CanEnableWireless)); OnPropertyChanged(nameof(CanScanLibrary)); ConnectCommand.RaiseCanExecuteChanged(); DisconnectCommand.RaiseCanExecuteChanged(); EnableWirelessCommand.RaiseCanExecuteChanged(); ScanLibraryCommand.RaiseCanExecuteChanged(); }
    private void CancelActiveScan()
    {
        lock (_scanGate)
        {
            ++_scanGeneration;
            try { _scanSource?.Cancel(); } catch (ObjectDisposedException) { }
            _scanSource = null;
        }
    }
    private bool OwnsScan(CancellationTokenSource source, long generation)
    {
        lock (_scanGate) return ReferenceEquals(_scanSource, source) && _scanGeneration == generation;
    }
    private void ThrowIfScanIsObsolete(CancellationTokenSource source, long generation)
    {
        if (!OwnsScan(source, generation)) throw new OperationCanceledException("Quest library scan was superseded.", source.Token);
    }
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items) { target.Clear(); foreach (var item in items) target.Add(item); }
}
