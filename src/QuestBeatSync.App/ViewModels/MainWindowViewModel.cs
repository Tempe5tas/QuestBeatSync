using System.Collections.ObjectModel;
using QuestBeatSync.Infrastructure.Abstractions;

namespace QuestBeatSync.App.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    private NavigationItemViewModel? _selectedPage;

    public MainWindowViewModel(IQuestTransport questTransport)
    {
        ArgumentNullException.ThrowIfNull(questTransport);
        QuestTransport = questTransport;

        NavigationItems = new ObservableCollection<NavigationItemViewModel>
        {
            new("Dashboard"),
            new("Playlists"),
            new("Library"),
            new("Backup"),
            new("Settings")
        };

        _selectedPage = NavigationItems[0];
    }

    public IQuestTransport QuestTransport { get; }

    public ObservableCollection<NavigationItemViewModel> NavigationItems { get; }

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
        }
    }

    public string CurrentPageTitle => SelectedPage?.Title ?? "Dashboard";

    public bool IsDashboard => CurrentPageTitle == "Dashboard";

    public string DeviceStatus => "No Quest connected";

    public int SongCount => 0;

    public int PlaylistCount => 0;
}
