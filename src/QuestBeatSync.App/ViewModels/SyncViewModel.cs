using System.Collections.ObjectModel;
using QuestBeatSync.Core.Models;
using QuestBeatSync.Core.Services;
using QuestBeatSync.Infrastructure.Abstractions;

namespace QuestBeatSync.App.ViewModels;

public sealed class SyncViewModel : ViewModelBase
{
    private readonly PlaylistsViewModel _playlists; private readonly LibraryViewModel _library; private readonly IBeatSaverClient _beatSaver; private readonly IBeatMapCache _cache;
    private SyncPlan? _plan; private bool _isBuilding; private string? _message;

    public SyncViewModel(PlaylistsViewModel playlists, LibraryViewModel library, IBeatSaverClient beatSaver, IBeatMapCache cache, Func<Exception, Task> errorHandler)
    {
        _playlists = playlists; _library = library; _beatSaver = beatSaver; _cache = cache;
        BuildCommand = new AsyncRelayCommand(BuildAsync, () => CanBuild && !IsBuilding, errorHandler);
        playlists.RequirementsChanged += (_, _) => Invalidate(); library.Changed += (_, _) => { Invalidate(); OnPropertyChanged(nameof(CanBuild)); BuildCommand.RaiseCanExecuteChanged(); };
    }

    public ObservableCollection<SyncOperation> Operations { get; } = [];
    public AsyncRelayCommand BuildCommand { get; }
    public bool CanBuild => _library.ScanCompleted;
    public bool IsBuilding { get => _isBuilding; private set { if (SetProperty(ref _isBuilding, value)) BuildCommand.RaiseCanExecuteChanged(); } }
    public bool HasPlan => Plan is not null;
    public SyncPlan? Plan { get => _plan; private set { if (SetProperty(ref _plan, value)) { OnPropertyChanged(nameof(HasPlan)); NotifyCounts(); } } }
    public string? ResolutionMessage { get => _message; private set => SetProperty(ref _message, value); }
    public int PlaylistReferences => Plan?.PlaylistReferenceCount ?? 0; public int UniqueMaps => Plan?.UniqueMapCount ?? 0;
    public int AlreadyInstalled => Plan?.AlreadyInstalledCount ?? 0; public int DownloadRequired => Plan?.DownloadRequiredCount ?? 0; public int UploadRequired => Plan?.UploadRequiredCount ?? 0;
    public int Unavailable => Plan?.UnavailableCount ?? 0; public int Unknown => Plan?.UnknownCount ?? 0; public int QuestOnlyPreserved => Plan?.QuestOnlyPreservedCount ?? 0; public int DeletionCount => 0;

    private async Task BuildAsync()
    {
        IsBuilding = true; Invalidate();
        try
        {
            var requirements = _playlists.ImportedPlaylists.SelectMany(p => p.Entries).Where(e => e.Hash is not null).GroupBy(e => e.Hash!, StringComparer.OrdinalIgnoreCase).Select(g => (Hash: g.Key, Key: g.Select(e => e.Key).FirstOrDefault(k => k is not null))).ToArray();
            var cached = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var availability = new Dictionary<string, BeatSaverAvailability>(StringComparer.OrdinalIgnoreCase);
            var installed = _library.InstalledMaps.Where(m => m.IdentityStatus == QuestMapIdentityStatus.HashIdentified && m.Identity is not null).Select(m => m.Identity!.Hash).ToHashSet(StringComparer.OrdinalIgnoreCase);
            ResolutionMessage = $"Resolving {requirements.Length} unique maps..."; var completed = 0;
            foreach (var requirement in requirements)
            {
                if (!installed.Contains(requirement.Hash))
                {
                    if (await _cache.IsCachedAsync(requirement.Hash)) { cached.Add(requirement.Hash); _playlists.UpdateRequirement(requirement.Hash, "Yes"); }
                    else
                    {
                        var lookup = FindReusable(requirement.Hash) ?? await _beatSaver.LookupAsync(new(requirement.Hash, requirement.Key));
                        availability[requirement.Hash] = lookup.Availability; _playlists.UpdateRequirement(requirement.Hash, "No", lookup);
                    }
                }
                ResolutionMessage = $"Resolving {++completed}/{requirements.Length} unique maps...";
            }
            var plan = SyncPlanner.Build(_playlists.ImportedPlaylists, new QuestLibrary(_library.InstalledMaps, _library.InstalledPlaylists), cached, availability);
            Replace(Operations, plan.Operations); Plan = plan; ResolutionMessage = $"Resolved {plan.UniqueMapCount} unique maps across {plan.PlaylistReferenceCount} playlist references.";
        }
        finally { if (Plan is null) ResolutionMessage = "Sync requirement resolution did not complete."; IsBuilding = false; }
    }

    private BeatSaverLookupResult? FindReusable(string hash) =>
        _playlists.AllEntryStatuses
            .Select(status => status.LookupResult)
            .FirstOrDefault(result =>
                result is not null
                && string.Equals(result.RequestedHash, hash, StringComparison.OrdinalIgnoreCase)
                && (result.Availability == BeatSaverAvailability.Unavailable
                    || (result.Availability == BeatSaverAvailability.Online
                        && result.ExactHashMatched
                        && string.Equals(result.ResolvedHash, hash, StringComparison.OrdinalIgnoreCase))));
    private void Invalidate() { Plan = null; Operations.Clear(); ResolutionMessage = null; }
    private void NotifyCounts() { OnPropertyChanged(nameof(PlaylistReferences)); OnPropertyChanged(nameof(UniqueMaps)); OnPropertyChanged(nameof(AlreadyInstalled)); OnPropertyChanged(nameof(DownloadRequired)); OnPropertyChanged(nameof(UploadRequired)); OnPropertyChanged(nameof(Unavailable)); OnPropertyChanged(nameof(Unknown)); OnPropertyChanged(nameof(QuestOnlyPreserved)); OnPropertyChanged(nameof(DeletionCount)); }
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items) { target.Clear(); foreach (var item in items) target.Add(item); }
}
