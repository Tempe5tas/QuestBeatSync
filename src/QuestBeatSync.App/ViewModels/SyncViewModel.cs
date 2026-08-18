using System.Collections.ObjectModel;
using QuestBeatSync.Core.Models;
using QuestBeatSync.Core.Services;
using QuestBeatSync.Infrastructure.Abstractions;
using QuestBeatSync.Infrastructure.Execution;

namespace QuestBeatSync.App.ViewModels;

public sealed class SyncViewModel : ViewModelBase
{
    private readonly PlaylistsViewModel _playlists;
    private readonly LibraryViewModel _library;
    private readonly IBeatSaverClient _beatSaver;
    private readonly IBeatMapCache _cache;
    private readonly SyncExecutor? _executor;
    private readonly Func<QuestDevice?> _selectedDevice;
    private readonly Func<Task<QuestBeatSaberScanResult>> _scanSelectedDevice;
    private SyncPlan? _plan;
    private SyncExecutionPlan? _executionPlan;
    private SyncExecutionPlan? _confirmationPlan;
    private SyncResult? _lastResult;
    private CancellationTokenSource? _executionCancellation;
    private bool _isBuilding;
    private bool _isExecuting;
    private bool _isConfirmationVisible;
    private string? _message;
    private string? _progressMessage;

    public SyncViewModel(
        PlaylistsViewModel playlists,
        LibraryViewModel library,
        IBeatSaverClient beatSaver,
        IBeatMapCache cache,
        Func<Exception, Task> errorHandler,
        SyncExecutor? executor = null,
        Func<QuestDevice?>? selectedDevice = null,
        Func<Task<QuestBeatSaberScanResult>>? scanSelectedDevice = null)
    {
        _playlists = playlists;
        _library = library;
        _beatSaver = beatSaver;
        _cache = cache;
        _executor = executor;
        _selectedDevice = selectedDevice ?? (() => null);
        _scanSelectedDevice = scanSelectedDevice ?? (() => Task.FromResult(QuestBeatSaberScanResult.Empty));
        BuildCommand = new AsyncRelayCommand(BuildAsync, () => CanBuild && !IsBuilding && !IsExecuting, errorHandler);
        ReviewExecutionCommand = new RelayCommand(RequestConfirmation, () => CanReviewExecution);
        ConfirmExecutionCommand = new AsyncRelayCommand(ExecuteConfirmedAsync, () => CanConfirmExecution, errorHandler);
        CancelConfirmationCommand = new RelayCommand(CancelConfirmation, () => IsConfirmationVisible && !IsExecuting);
        CancelExecutionCommand = new RelayCommand(CancelExecution, () => IsExecuting);
        playlists.RequirementsChanged += (_, _) => InvalidatePlan();
        library.Changed += (_, _) =>
        {
            InvalidatePlan();
            OnPropertyChanged(nameof(CanBuild));
            RaiseCommandStates();
        };
    }

    public ObservableCollection<SyncOperation> Operations { get; } = [];
    public ObservableCollection<SyncOperationResult> OperationResults { get; } = [];
    public AsyncRelayCommand BuildCommand { get; }
    public RelayCommand ReviewExecutionCommand { get; }
    public AsyncRelayCommand ConfirmExecutionCommand { get; }
    public RelayCommand CancelConfirmationCommand { get; }
    public RelayCommand CancelExecutionCommand { get; }
    public bool CanBuild => _selectedDevice()?.IsConnected == true;
    public bool CanReviewExecution => ExecutionPlan is not null && _executor is not null && !IsBuilding && !IsExecuting;
    public bool CanConfirmExecution => IsConfirmationVisible && ReferenceEquals(_confirmationPlan, ExecutionPlan) && !IsExecuting;
    public bool HasPlan => Plan is not null;
    public bool HasResult => LastResult is not null;
    public bool HasDiagnosticWarnings => LastResult?.DiagnosticWarnings.Count > 0;
    public bool IsBuilding { get => _isBuilding; private set { if (SetProperty(ref _isBuilding, value)) RaiseCommandStates(); } }
    public bool IsExecuting { get => _isExecuting; private set { if (SetProperty(ref _isExecuting, value)) RaiseCommandStates(); } }
    public bool IsConfirmationVisible { get => _isConfirmationVisible; private set { if (SetProperty(ref _isConfirmationVisible, value)) RaiseCommandStates(); } }
    public SyncExecutionPlan? ExecutionPlan { get => _executionPlan; private set { if (SetProperty(ref _executionPlan, value)) { OnPropertyChanged(nameof(CanReviewExecution)); OnPropertyChanged(nameof(TargetSerial)); RaiseCommandStates(); } } }
    public SyncPlan? Plan { get => _plan; private set { if (SetProperty(ref _plan, value)) { OnPropertyChanged(nameof(HasPlan)); NotifyPlanCounts(); } } }
    public SyncResult? LastResult { get => _lastResult; private set { if (SetProperty(ref _lastResult, value)) NotifyResult(); } }
    public string? ResolutionMessage { get => _message; private set => SetProperty(ref _message, value); }
    public string? ProgressMessage { get => _progressMessage; private set => SetProperty(ref _progressMessage, value); }
    public string TargetSerial => ExecutionPlan?.Target.DeviceSerial ?? string.Empty;
    public string DiagnosticWarningsText => LastResult is null ? string.Empty : string.Join(Environment.NewLine, LastResult.DiagnosticWarnings);
    public string ResultSummary => LastResult is null
        ? string.Empty
        : $"{LastResult.Status}: {SucceededCount} succeeded, {FailedCount} failed, {SkippedCount} skipped, {CanceledCount} canceled. 0 maps deleted.";
    public int PlaylistReferences => Plan?.PlaylistReferenceCount ?? 0;
    public int UniqueMaps => Plan?.UniqueMapCount ?? 0;
    public int AlreadyInstalled => Plan?.AlreadyInstalledCount ?? 0;
    public int DownloadRequired => Plan?.DownloadRequiredCount ?? 0;
    public int UploadRequired => Plan?.UploadRequiredCount ?? 0;
    public int PlaylistsToTransfer => Plan?.Count(SyncOperationKind.ImportPlaylist) ?? 0;
    public int Unavailable => Plan?.UnavailableCount ?? 0;
    public int Unknown => Plan?.UnknownCount ?? 0;
    public int QuestOnlyPreserved => Plan?.QuestOnlyPreservedCount ?? 0;
    public int DeletionCount => 0;
    public int SucceededCount => LastResult?.Operations.Count(result => result.Status == SyncOperationStatus.Succeeded) ?? 0;
    public int FailedCount => LastResult?.Operations.Count(result => result.Status == SyncOperationStatus.Failed) ?? 0;
    public int SkippedCount => LastResult?.Operations.Count(result => result.Status == SyncOperationStatus.Skipped) ?? 0;
    public int CanceledCount => LastResult?.Operations.Count(result => result.Status == SyncOperationStatus.Canceled) ?? 0;
    public string ActionOperationsHeader => $"Actions ({Operations.Count})";

    private async Task BuildAsync()
    {
        IsBuilding = true;
        InvalidatePlan();
        try
        {
            var selectedDevice = _selectedDevice();
            if (selectedDevice?.IsConnected != true) throw new InvalidOperationException("Select a connected Quest before building a sync plan.");
            ResolutionMessage = "Scanning the selected Quest library...";
            var freshScan = await _scanSelectedDevice();
            if (_selectedDevice() is not { IsConnected: true } currentDevice || !string.Equals(currentDevice.Serial, selectedDevice.Serial, StringComparison.Ordinal))
                throw new InvalidOperationException("The selected Quest changed during the planning scan. Rebuild the sync plan.");

            var requirements = _playlists.ImportedPlaylists
                .SelectMany(playlist => playlist.Entries)
                .Where(entry => entry.Hash is not null)
                .GroupBy(entry => entry.Hash!, StringComparer.OrdinalIgnoreCase)
                .Select(group => (Hash: group.Key, Key: group.Select(entry => entry.Key).FirstOrDefault(key => key is not null)))
                .ToArray();
            var cached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var availability = new Dictionary<string, BeatSaverAvailability>(StringComparer.OrdinalIgnoreCase);
            var exactLookups = new Dictionary<string, BeatSaverLookupResult>(StringComparer.OrdinalIgnoreCase);
            var installed = freshScan.InstalledMaps
                .Where(map => map.IdentityStatus == QuestMapIdentityStatus.HashIdentified && map.Identity is not null)
                .Select(map => map.Identity!.Hash)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            ResolutionMessage = $"Resolving {requirements.Length} unique maps...";
            var completed = 0;
            foreach (var requirement in requirements)
            {
                if (!installed.Contains(requirement.Hash))
                {
                    if (await _cache.IsCachedAsync(requirement.Hash))
                    {
                        cached.Add(requirement.Hash);
                        _playlists.UpdateRequirement(requirement.Hash, "Yes");
                    }
                    else
                    {
                        var lookup = FindReusable(requirement.Hash) ?? await _beatSaver.LookupAsync(new(requirement.Hash, requirement.Key));
                        availability[requirement.Hash] = lookup.Availability;
                        _playlists.UpdateRequirement(requirement.Hash, "No", lookup);
                        if (lookup.ExactHashMatched) exactLookups[requirement.Hash] = lookup;
                    }
                }

                ResolutionMessage = $"Resolving {++completed}/{requirements.Length} unique maps...";
            }

            var plan = SyncPlanner.Build(
                _playlists.ImportedPlaylists,
                new QuestLibrary(freshScan.InstalledMaps, freshScan.InstalledPlaylists),
                cached,
                availability);
            Replace(Operations, plan.Operations.Where(operation => IsActionable(operation.Kind)));
            OnPropertyChanged(nameof(ActionOperationsHeader));
            Plan = plan;
            var sources = _playlists.ImportedPlaylists
                .Select(playlist => playlist.SourceIdentity)
                .Where(source => source is not null)
                .Cast<PlaylistSourceIdentity>()
                .ToArray();
            if (sources.Length == _playlists.ImportedPlaylists.Count)
                ExecutionPlan = new SyncExecutionPlan(plan, QuestScanBinding.Capture(selectedDevice.Serial, freshScan), sources, exactLookups);
            ResolutionMessage = $"Resolved {plan.UniqueMapCount} unique maps across {plan.PlaylistReferenceCount} playlist references.";
        }
        finally
        {
            if (Plan is null) ResolutionMessage = "Sync requirement resolution did not complete.";
            IsBuilding = false;
        }
    }

    private void RequestConfirmation()
    {
        if (!CanReviewExecution) return;
        _confirmationPlan = ExecutionPlan;
        IsConfirmationVisible = true;
    }

    private void CancelConfirmation()
    {
        _confirmationPlan = null;
        IsConfirmationVisible = false;
    }

    private async Task ExecuteConfirmedAsync()
    {
        var plan = _confirmationPlan;
        var device = _selectedDevice();
        if (plan is null || !ReferenceEquals(plan, ExecutionPlan) || device is null || _executor is null)
            throw new InvalidOperationException("The confirmed plan or target device is no longer current. Rebuild and confirm the plan.");

        CancelConfirmation();
        LastResult = null;
        OperationResults.Clear();
        ProgressMessage = "Preparing execution...";
        IsExecuting = true;
        var current = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _executionCancellation, current);
        previous?.Cancel();
        previous?.Dispose();
        try
        {
            var progress = new Progress<SyncProgress>(update =>
                ProgressMessage = update.Total > 0
                    ? $"{update.Phase} {update.Current}/{update.Total}: {update.Message}"
                    : $"{update.Phase}: {update.Message}");
            var result = await _executor.ExecuteAsync(plan, device, current.Token, progress);
            if (result.Operations.Any(item =>
                    item.Operation.Kind == SyncOperationKind.UploadMap &&
                    item.Status == SyncOperationStatus.Succeeded))
            {
                ProgressMessage = "Refreshing Quest library after execution...";
                await _scanSelectedDevice();
                var installedHashes = _library.InstalledMaps
                    .Where(map => map.IdentityStatus == QuestMapIdentityStatus.HashIdentified && map.Identity is not null)
                    .Select(map => map.Identity!.Hash)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var missing = result.Operations
                    .Where(item => item.Operation.Kind == SyncOperationKind.UploadMap && item.Status == SyncOperationStatus.Succeeded && item.Operation.MapIdentity is not null)
                    .Select(item => item.Operation.MapIdentity!.Hash)
                    .Where(hash => !installedHashes.Contains(hash))
                    .ToArray();
                if (missing.Length > 0)
                    result = result with { DiagnosticWarnings = [.. result.DiagnosticWarnings, $"Post-execution scan did not find expected hash-named map directories: {string.Join(", ", missing)}"] };
            }

            LastResult = result;
            Replace(OperationResults, result.Operations.Where(item => IsActionable(item.Operation.Kind)));
            ProgressMessage = result.Status is SyncRunStatus.Completed or SyncRunStatus.CompletedWithFailures
                ? "Sync finished. Start Beat Saber to load transferred maps and playlists."
                : result.Message;
        }
        finally
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref _executionCancellation, null, current), current))
                current.Dispose();
            IsExecuting = false;
        }
    }

    private void CancelExecution() => _executionCancellation?.Cancel();

    private BeatSaverLookupResult? FindReusable(string hash) =>
        _playlists.AllEntryStatuses
            .Select(status => status.LookupResult)
            .FirstOrDefault(result =>
                result is not null &&
                string.Equals(result.RequestedHash, hash, StringComparison.OrdinalIgnoreCase) &&
                (result.Availability == BeatSaverAvailability.Unavailable ||
                 (result.Availability == BeatSaverAvailability.Online && result.ExactHashMatched &&
                  string.Equals(result.ResolvedHash, hash, StringComparison.OrdinalIgnoreCase))));

    private void InvalidatePlan()
    {
        Plan = null;
        ExecutionPlan = null;
        _confirmationPlan = null;
        IsConfirmationVisible = false;
        Operations.Clear();
        OnPropertyChanged(nameof(ActionOperationsHeader));
        ResolutionMessage = null;
    }

    private void RaiseCommandStates()
    {
        BuildCommand.RaiseCanExecuteChanged();
        ReviewExecutionCommand.RaiseCanExecuteChanged();
        ConfirmExecutionCommand.RaiseCanExecuteChanged();
        CancelConfirmationCommand.RaiseCanExecuteChanged();
        CancelExecutionCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanReviewExecution));
        OnPropertyChanged(nameof(CanConfirmExecution));
    }

    private void NotifyPlanCounts()
    {
        OnPropertyChanged(nameof(PlaylistReferences)); OnPropertyChanged(nameof(UniqueMaps));
        OnPropertyChanged(nameof(AlreadyInstalled)); OnPropertyChanged(nameof(DownloadRequired));
        OnPropertyChanged(nameof(UploadRequired)); OnPropertyChanged(nameof(PlaylistsToTransfer));
        OnPropertyChanged(nameof(Unavailable)); OnPropertyChanged(nameof(Unknown));
        OnPropertyChanged(nameof(QuestOnlyPreserved)); OnPropertyChanged(nameof(DeletionCount));
    }

    private void NotifyResult()
    {
        OnPropertyChanged(nameof(HasResult)); OnPropertyChanged(nameof(HasDiagnosticWarnings));
        OnPropertyChanged(nameof(DiagnosticWarningsText)); OnPropertyChanged(nameof(ResultSummary));
        OnPropertyChanged(nameof(SucceededCount)); OnPropertyChanged(nameof(FailedCount));
        OnPropertyChanged(nameof(SkippedCount)); OnPropertyChanged(nameof(CanceledCount));
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }

    private static bool IsActionable(SyncOperationKind kind) =>
        kind is SyncOperationKind.DownloadMap or SyncOperationKind.UploadMap or SyncOperationKind.ImportPlaylist;
}
