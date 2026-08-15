using QuestBeatSync.Core.Models;

namespace QuestBeatSync.App.ViewModels;

public sealed class PlaylistEntryStatusViewModel : ViewModelBase
{
    private string _installedOnQuest = "Unknown";
    private string _cachedLocally = "Unknown";
    private BeatSaverAvailability _availability = BeatSaverAvailability.Unknown;
    private string? _statusMessage;

    public PlaylistEntryStatusViewModel(PlaylistEntry entry) =>
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));

    public PlaylistEntry Entry { get; }

    public string? Key => Entry.Key;

    public string? Hash => Entry.Hash;

    public string? SongName => Entry.SongName;

    public PlaylistEntryIdentityStatus IdentityStatus => Entry.IdentityStatus;

    public string InstalledOnQuest
    {
        get => _installedOnQuest;
        set => SetProperty(ref _installedOnQuest, value);
    }

    public string CachedLocally
    {
        get => _cachedLocally;
        set => SetProperty(ref _cachedLocally, value);
    }

    public BeatSaverAvailability Availability
    {
        get => _availability;
        set
        {
            if (SetProperty(ref _availability, value))
            {
                OnPropertyChanged(nameof(AvailabilityDisplay));
            }
        }
    }

    public string AvailabilityDisplay => Availability switch
    {
        BeatSaverAvailability.Online => "Available online",
        BeatSaverAvailability.Unavailable => "Unavailable",
        _ => "Unknown"
    };

    public string? StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public BeatSaverLookupResult? LookupResult { get; set; }
}
