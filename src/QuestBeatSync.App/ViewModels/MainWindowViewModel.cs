using System.Collections.ObjectModel;
using QuestBeatSync.Infrastructure.Abstractions;
using QuestBeatSync.Infrastructure.Adb;

namespace QuestBeatSync.App.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    private NavigationItemViewModel? _selectedPage;
    private OperationError? _operationError;

    public MainWindowViewModel(IQuestTransport transport, IQuestBeatSaberScanner scanner, ILocalPlaylistImporter playlistImporter, IBeatSaverClient beatSaverClient, IBeatMapCache beatMapCache, AdbQuestTransportOptions adbOptions, AdbSettingsStore settingsStore)
    {
        NavigationItems = [new("Dashboard"), new("Playlists"), new("Library"), new("Sync"), new("Backup"), new("Settings")]; _selectedPage = NavigationItems[0];
        Library = new();
        Dashboard = new(transport, scanner, Library, exception => ReportAsync("Refresh devices", exception));
        Playlists = new(playlistImporter, beatSaverClient, beatMapCache, Library, exception => ReportAsync("Playlist operation", exception));
        Sync = new(Playlists, Library, beatSaverClient, beatMapCache, exception => ReportAsync("Build Sync Plan", exception));
        Settings = new(adbOptions, settingsStore, Dashboard.RefreshDevicesAsync, exception => ReportAsync("Save ADB settings", exception));
        Dashboard.AdbUnavailable += (_, _) => OpenSettings();
        OpenSettingsCommand = new(OpenSettings);
        DismissOperationErrorCommand = new(() => OperationError = null);
    }

    public ObservableCollection<NavigationItemViewModel> NavigationItems { get; }
    public DashboardViewModel Dashboard { get; }
    public LibraryViewModel Library { get; }
    public PlaylistsViewModel Playlists { get; }
    public SyncViewModel Sync { get; }
    public SettingsViewModel Settings { get; }
    public RelayCommand DismissOperationErrorCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public NavigationItemViewModel? SelectedPage { get => _selectedPage; set { if (SetProperty(ref _selectedPage, value)) NotifyNavigation(); } }
    public string CurrentPageTitle => SelectedPage?.Title ?? "Dashboard";
    public bool IsDashboard => CurrentPageTitle == "Dashboard"; public bool IsPlaylists => CurrentPageTitle == "Playlists"; public bool IsLibrary => CurrentPageTitle == "Library"; public bool IsSync => CurrentPageTitle == "Sync"; public bool IsSettings => CurrentPageTitle == "Settings";
    public bool IsPlaceholderPage => !IsDashboard && !IsPlaylists && !IsLibrary && !IsSync && !IsSettings;
    public OperationError? OperationError { get => _operationError; private set { if (SetProperty(ref _operationError, value)) { OnPropertyChanged(nameof(HasOperationError)); OnPropertyChanged(nameof(OperationErrorText)); } } }
    public bool HasOperationError => OperationError is not null;
    public string? OperationErrorText => OperationError is null ? null : $"{OperationError.Operation}: {OperationError.Message}";
    public Task InitializeAsync() => Dashboard.InitializeAsync();
    public void ReportOperationError(string operation, Exception exception) => OperationError = new(operation, exception.Message);

    private Task ReportAsync(string operation, Exception exception) { ReportOperationError(operation, exception); return Task.CompletedTask; }
    private void OpenSettings() => SelectedPage = NavigationItems.First(item => item.Title == "Settings");
    private void NotifyNavigation() { OnPropertyChanged(nameof(CurrentPageTitle)); OnPropertyChanged(nameof(IsDashboard)); OnPropertyChanged(nameof(IsPlaylists)); OnPropertyChanged(nameof(IsLibrary)); OnPropertyChanged(nameof(IsSync)); OnPropertyChanged(nameof(IsSettings)); OnPropertyChanged(nameof(IsPlaceholderPage)); }
}
