using QuestBeatSync.Infrastructure.Adb;

namespace QuestBeatSync.App.ViewModels;

public sealed class SettingsViewModel : ViewModelBase
{
    private readonly AdbQuestTransportOptions _options;
    private readonly AdbSettingsStore _store;
    private readonly Func<Task> _refreshDevices;
    private string? _configuredAdbPath;
    private string? _message;

    public SettingsViewModel(AdbQuestTransportOptions options, AdbSettingsStore store, Func<Task> refreshDevices, Func<Exception, Task> errorHandler)
    {
        _options = options; _store = store; _refreshDevices = refreshDevices; _configuredAdbPath = options.ConfiguredExecutablePath;
        SaveCommand = new(SaveAsync, errorHandler: errorHandler);
    }

    public AsyncRelayCommand SaveCommand { get; }
    public string? ConfiguredAdbPath { get => _configuredAdbPath; set { if (SetProperty(ref _configuredAdbPath, value)) Message = null; } }
    public string? Message { get => _message; private set => SetProperty(ref _message, value); }

    private async Task SaveAsync()
    {
        _store.SaveConfiguredPath(ConfiguredAdbPath);
        _options.ConfiguredExecutablePath = string.IsNullOrWhiteSpace(ConfiguredAdbPath) ? null : ConfiguredAdbPath.Trim();
        Message = "ADB path saved.";
        await _refreshDevices();
    }
}
