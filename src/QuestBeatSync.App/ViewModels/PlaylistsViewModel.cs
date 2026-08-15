using System.Collections.ObjectModel;
using QuestBeatSync.Core.Models;
using QuestBeatSync.Infrastructure.Abstractions;
using QuestBeatSync.Infrastructure.Importing;

namespace QuestBeatSync.App.ViewModels;

public sealed class PlaylistsViewModel : ViewModelBase
{
    private readonly ILocalPlaylistImporter _importer;
    private readonly IBeatSaverClient _beatSaver;
    private readonly IBeatMapCache _cache;
    private readonly LibraryViewModel _library;
    private readonly Dictionary<Playlist, IReadOnlyList<PlaylistEntryStatusViewModel>> _statuses = [];
    private LocalPlaylistLibraryState _state = new([]);
    private Playlist? _selected;
    private bool _isImporting;
    private bool _isChecking;

    public PlaylistsViewModel(ILocalPlaylistImporter importer, IBeatSaverClient beatSaver, IBeatMapCache cache, LibraryViewModel library, Func<Exception, Task> errorHandler)
    {
        _importer = importer; _beatSaver = beatSaver; _cache = cache; _library = library;
        CheckSelectedCommand = new AsyncRelayCommand(CheckSelectedAsync, () => SelectedPlaylist is not null && !IsChecking, errorHandler);
        CacheSelectedCommand = new AsyncRelayCommand(CacheSelectedAsync, () => SelectedPlaylist is not null && !IsChecking, errorHandler);
        library.Changed += (_, _) => { UpdateInstalledStatuses(); RequirementsChanged?.Invoke(this, EventArgs.Empty); };
    }

    public ObservableCollection<Playlist> ImportedPlaylists { get; } = [];
    public ObservableCollection<PlaylistEntryStatusViewModel> SelectedEntries { get; } = [];
    public ObservableCollection<string> ImportErrors { get; } = [];
    public AsyncRelayCommand CheckSelectedCommand { get; }
    public AsyncRelayCommand CacheSelectedCommand { get; }
    public event EventHandler? RequirementsChanged;
    public IEnumerable<PlaylistEntryStatusViewModel> AllEntryStatuses => _statuses.Values.SelectMany(value => value);

    public Playlist? SelectedPlaylist
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            Replace(SelectedEntries, value is not null && _statuses.TryGetValue(value, out var items) ? items : []);
            NotifySelected();
        }
    }

    public bool IsImporting { get => _isImporting; private set => SetProperty(ref _isImporting, value); }
    public bool IsChecking { get => _isChecking; private set { if (SetProperty(ref _isChecking, value)) { CheckSelectedCommand.RaiseCanExecuteChanged(); CacheSelectedCommand.RaiseCanExecuteChanged(); } } }
    public bool HasImportedPlaylists => ImportedPlaylists.Count > 0;
    public bool HasSelectedPlaylist => SelectedPlaylist is not null;
    public bool HasImportErrors => ImportErrors.Count > 0;
    public string ImportErrorText => string.Join(Environment.NewLine, ImportErrors);
    public int TotalPlaylistReferences => _state.TotalPlaylistReferences;
    public int UniqueRequiredHashes => _state.UniqueRequiredHashes;
    public int DuplicateReferences => _state.DuplicateReferences;
    public string SelectedAuthorDisplay => string.IsNullOrWhiteSpace(SelectedPlaylist?.Author) ? "by Unknown author" : $"by {SelectedPlaylist.Author}";
    public int SelectedEntryCount => SelectedPlaylist?.EntryCount ?? 0;
    public int SelectedUniqueHashCount => SelectedPlaylist?.UniqueHashCount ?? 0;
    public int SelectedDuplicateReferenceCount => SelectedPlaylist?.DuplicateReferenceCount ?? 0;

    public async Task ImportAsync(IEnumerable<string> paths)
    {
        IsImporting = true; ImportErrors.Clear();
        try
        {
            var results = await _importer.ImportAsync(paths);
            Playlist? first = null;
            foreach (var result in results)
            {
                if (!result.IsSuccess) { ImportErrors.Add($"{Path.GetFileName(result.FilePath)}: {result.ErrorMessage}"); continue; }
                var playlist = result.Playlist!;
                if (playlist.SourceIdentity is not null && ImportedPlaylists.Any(existing =>
                        existing.SourceIdentity is not null && SyncExecutionPlan.SourcePathComparer.Equals(
                            existing.SourceIdentity.CanonicalPath,
                            playlist.SourceIdentity.CanonicalPath)))
                {
                    continue;
                }

                first ??= playlist; ImportedPlaylists.Add(playlist);
                var statuses = playlist.Entries.Select(entry => new PlaylistEntryStatusViewModel(entry)).ToArray();
                _statuses[playlist] = statuses;
                foreach (var item in statuses) item.CachedLocally = item.Hash is not null && await _cache.IsCachedAsync(item.Hash) ? "Yes" : item.Hash is null ? "Unknown" : "No";
            }
            if (first is not null) SelectedPlaylist = first;
        }
        catch (Exception exception) { ImportErrors.Add($"Playlist import failed: {exception.Message}"); }
        finally { IsImporting = false; NotifyState(); RequirementsChanged?.Invoke(this, EventArgs.Empty); }
    }

    private async Task CheckSelectedAsync()
    {
        IsChecking = true;
        try
        {
            var results = new Dictionary<string, BeatSaverLookupResult>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in SelectedEntries)
            {
                var request = BeatSaverLookupRequest.FromEntry(item.Entry);
                var key = request.Hash is not null ? $"h:{request.Hash}" : $"k:{request.Key}";
                if (!results.TryGetValue(key, out var result)) results[key] = result = await _beatSaver.LookupAsync(request);
                item.LookupResult = result; item.Availability = result.Availability; item.StatusMessage = result.Message;
                if (result.ResolvedHash is not null) item.CachedLocally = await _cache.IsCachedAsync(result.ResolvedHash) ? "Yes" : "No";
            }
        }
        finally { IsChecking = false; RequirementsChanged?.Invoke(this, EventArgs.Empty); }
    }

    private async Task CacheSelectedAsync()
    {
        IsChecking = true;
        try
        {
            foreach (var item in SelectedEntries.Where(item => item.Entry.IdentityStatus == PlaylistEntryIdentityStatus.HashIdentified && item.LookupResult?.CanDownload == true && item.LookupResult.ExactHashMatched))
            {
                var result = await _cache.CacheAsync(item.LookupResult!); item.CachedLocally = result.IsSuccess ? "Yes" : "No";
                if (!result.IsSuccess) item.StatusMessage = result.ErrorMessage;
            }
        }
        finally { IsChecking = false; RequirementsChanged?.Invoke(this, EventArgs.Empty); }
    }

    public void UpdateRequirement(string hash, string cached, BeatSaverLookupResult? lookup = null)
    {
        foreach (var item in AllEntryStatuses.Where(item => string.Equals(item.Hash, hash, StringComparison.OrdinalIgnoreCase)))
        { item.CachedLocally = cached; if (lookup is not null) { item.LookupResult = lookup; item.Availability = lookup.Availability; item.StatusMessage = lookup.Message; } }
    }

    private void UpdateInstalledStatuses()
    {
        var known = _library.InstalledMaps.Where(map => map.IdentityStatus == QuestMapIdentityStatus.HashIdentified && map.Identity is not null).Select(map => map.Identity!.Hash).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var proveAbsent = _library.ScanCompleted && _library.InstalledMaps.All(map => map.IdentityStatus == QuestMapIdentityStatus.HashIdentified && map.Identity is not null);
        foreach (var item in AllEntryStatuses) item.InstalledOnQuest = item.Hash is null ? "Unknown" : known.Contains(item.Hash) ? "Yes" : proveAbsent ? "No" : "Unknown";
    }

    private void NotifyState() { _state = new(ImportedPlaylists); OnPropertyChanged(nameof(HasImportedPlaylists)); OnPropertyChanged(nameof(HasImportErrors)); OnPropertyChanged(nameof(ImportErrorText)); OnPropertyChanged(nameof(TotalPlaylistReferences)); OnPropertyChanged(nameof(UniqueRequiredHashes)); OnPropertyChanged(nameof(DuplicateReferences)); }
    private void NotifySelected() { OnPropertyChanged(nameof(HasSelectedPlaylist)); OnPropertyChanged(nameof(SelectedAuthorDisplay)); OnPropertyChanged(nameof(SelectedEntryCount)); OnPropertyChanged(nameof(SelectedUniqueHashCount)); OnPropertyChanged(nameof(SelectedDuplicateReferenceCount)); CheckSelectedCommand.RaiseCanExecuteChanged(); CacheSelectedCommand.RaiseCanExecuteChanged(); }
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items) { target.Clear(); foreach (var item in items) target.Add(item); }
}
