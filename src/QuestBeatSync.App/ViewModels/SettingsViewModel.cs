using QuestBeatSync.Infrastructure.Adb;

namespace QuestBeatSync.App.ViewModels;

public sealed class SettingsViewModel : ViewModelBase
{
    private readonly AdbQuestTransportOptions _options;
    private readonly AdbSettingsStore _store;
    private readonly Func<Task> _refreshDevices;
    private readonly AdbEnvironmentManager? _environment;
    private string? _configuredAdbPath;
    private string? _message;

    public SettingsViewModel(AdbQuestTransportOptions options, AdbSettingsStore store, Func<Task> refreshDevices, Func<Exception, Task> errorHandler, AdbEnvironmentManager? environment = null)
    {
        _options = options; _store = store; _refreshDevices = refreshDevices; _environment = environment; _configuredAdbPath = store.LoadConfiguredPath();
        SaveCommand = new(SaveAsync, errorHandler: errorHandler);
        RecheckCommand = new(RecheckAsync, errorHandler: errorHandler);
        DownloadCommand = new(DownloadAsync, () => !IsBusy, errorHandler: errorHandler);
        UseAutomaticCommand = new(UseAutomaticAsync, errorHandler: errorHandler);
        if (_environment is not null) _environment.StatusChanged += (_, _) => NotifyStatus();
    }

    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand RecheckCommand { get; }
    public AsyncRelayCommand DownloadCommand { get; }
    public AsyncRelayCommand UseAutomaticCommand { get; }
    public string? ConfiguredAdbPath { get => _configuredAdbPath; set { if (SetProperty(ref _configuredAdbPath, value)) Message = null; } }
    public string? Message { get => _message; private set => SetProperty(ref _message, value); }
    public bool IsBusy => _environment?.Current.State == AdbEnvironmentState.Installing;
    public string EnvironmentStatus => _environment?.Current.State.ToString() ?? "Not checked";
    public string EnvironmentSource => _environment?.Current.Source.ToString() ?? "None";
    public string EnvironmentVersion => _environment?.Current.Version ?? string.Empty;
    public string EnvironmentPath => _environment?.Current.ExecutablePath ?? string.Empty;
    public bool IsEnvironmentReady => _environment?.Current.IsReady == true;

    public async Task ChooseExecutableAsync(string path) { ConfiguredAdbPath = path; await SaveAsync(); }

    private async Task SaveAsync()
    {
        if (_environment is not null && !string.IsNullOrWhiteSpace(ConfiguredAdbPath))
        {
            var result = await _environment.SelectUserExecutableAsync(ConfiguredAdbPath);
            Message = result.IsReady ? "ADB executable selected." : result.ValidationError;
            if (result.IsReady) await _refreshDevices();
            return;
        }
        _store.SaveConfiguredPath(ConfiguredAdbPath);
        _options.ConfiguredExecutablePath = string.IsNullOrWhiteSpace(ConfiguredAdbPath) ? null : ConfiguredAdbPath.Trim();
        Message = "ADB path saved.";
        await _refreshDevices();
    }

    private async Task RecheckAsync() { if (_environment is not null) await _environment.DiscoverAsync(); NotifyStatus(); await _refreshDevices(); }
    private async Task DownloadAsync() { if (_environment is null) return; var result = await _environment.InstallManagedAsync(); Message = result.ValidationError ?? (result.IsReady ? "Official Platform-Tools downloaded and configured." : "ADB installation failed."); NotifyStatus(); if (result.IsReady) await _refreshDevices(); }
    private async Task UseAutomaticAsync() { ConfiguredAdbPath = null; if (_environment is not null) await _environment.UseAutomaticSelectionAsync(); else { _store.SaveConfiguredPath(null); _options.ConfiguredExecutablePath = null; } Message = "Automatic ADB selection enabled."; NotifyStatus(); await _refreshDevices(); }
    private void NotifyStatus() { OnPropertyChanged(nameof(IsBusy)); OnPropertyChanged(nameof(EnvironmentStatus)); OnPropertyChanged(nameof(EnvironmentSource)); OnPropertyChanged(nameof(EnvironmentVersion)); OnPropertyChanged(nameof(EnvironmentPath)); OnPropertyChanged(nameof(IsEnvironmentReady)); DownloadCommand.RaiseCanExecuteChanged(); }
}
