using System.Collections.ObjectModel;
using QuestBeatSync.Core.Models;
using QuestBeatSync.Infrastructure.Backup;

namespace QuestBeatSync.App.ViewModels;

public sealed class BackupViewModel : ViewModelBase
{
    private readonly IQuestBackupService? _service;
    private readonly LibraryViewModel _library;
    private readonly Func<QuestDevice?> _selectedDevice;
    private CancellationTokenSource? _cancellation;
    private bool _isRunning;
    private string _status = "Ready to create a read-only backup.";
    private QuestBackupProgress _progress = new(0, 0, 0, 0, 0, null);
    private string? _snapshotId;
    private string? _createdTime;
    private string _verificationState = "Not verified";
    private readonly Dictionary<string, SnapshotVerificationStatus> _verifiedStatuses = new(StringComparer.Ordinal);
    private SnapshotSummary? _selectedSnapshot;

    public BackupViewModel(IQuestBackupService? service, LibraryViewModel library, Func<QuestDevice?> selectedDevice, Func<Exception, Task> errorHandler)
    {
        _service = service; _library = library; _selectedDevice = selectedDevice;
        CreateBackupCommand = new(CreateBackupAsync, () => CanCreate, errorHandler: errorHandler);
        CancelBackupCommand = new(Cancel, () => IsRunning);
        VerifyBackupCommand = new(VerifyAsync, () => CanVerify, errorHandler: errorHandler);
        _library.Changed += (_, _) => NotifyContextChanged();
    }

    public ObservableCollection<SnapshotSummary> Snapshots { get; } = [];
    public AsyncRelayCommand CreateBackupCommand { get; }
    public RelayCommand CancelBackupCommand { get; }
    public AsyncRelayCommand VerifyBackupCommand { get; }
    public bool IsRunning { get => _isRunning; private set { if (SetProperty(ref _isRunning, value)) NotifyCommands(); } }
    public bool CanCreate => !IsRunning && _service is not null && _selectedDevice() is { IsConnected: true } && _library.IsScanCurrent && _library.ScanBinding is not null;
    public bool CanVerify => !IsRunning && _service is not null && !string.IsNullOrWhiteSpace(SnapshotId);
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public int FilesDiscovered => _progress.FilesDiscovered;
    public int FilesArchived => _progress.FilesArchived;
    public long BytesArchived => _progress.BytesArchived;
    public int NewBlobsWritten => _progress.NewBlobsWritten;
    public int ExistingBlobsReused => _progress.ExistingBlobsReused;
    public string CurrentFile => _progress.CurrentFile ?? string.Empty;
    public string SnapshotId { get => _snapshotId ?? string.Empty; private set { if (SetProperty(ref _snapshotId, value)) NotifyCommands(); } }
    public string CreatedTime { get => _createdTime ?? string.Empty; private set => SetProperty(ref _createdTime, value); }
    public string VerificationState { get => _verificationState; private set => SetProperty(ref _verificationState, value); }
    public SnapshotSummary? SelectedSnapshot
    {
        get => _selectedSnapshot;
        set
        {
            if (!SetProperty(ref _selectedSnapshot, value) || value is null) return;
            SnapshotId = value.SnapshotId; CreatedTime = value.CreatedAtUtc.ToLocalTime().ToString("g");
            VerificationState = value.VerificationStatus == SnapshotVerificationStatus.Unverified
                ? "Not verified"
                : value.VerificationStatus.ToString();
        }
    }
    public string SelectedQuest => _selectedDevice()?.Serial ?? "No connected Quest selected";
    public string BeatSaberVersion => _library.ScanResult.BeatSaberPackageVersion?.VersionName ?? "Not scanned";

    public void NotifyContextChanged()
    {
        OnPropertyChanged(nameof(SelectedQuest)); OnPropertyChanged(nameof(BeatSaberVersion)); OnPropertyChanged(nameof(CanCreate));
        CreateBackupCommand.RaiseCanExecuteChanged();
    }

    public async Task RefreshSnapshotsAsync()
    {
        if (_service is null) return;
        var snapshots = await _service.ListSnapshotsAsync().ConfigureAwait(false);
        var enrichedSnapshots = snapshots.Select(s =>
            _verifiedStatuses.TryGetValue(s.SnapshotId, out var status)
                ? s with { VerificationStatus = status }
                : s).ToArray();
        await OnUiThreadAsync(() =>
        {
            var selectedId = SnapshotId;
            Replace(Snapshots, enrichedSnapshots);
            SelectedSnapshot = Snapshots.FirstOrDefault(item => string.Equals(item.SnapshotId, selectedId, StringComparison.Ordinal)) ?? Snapshots.FirstOrDefault();
        });
    }

    private async Task CreateBackupAsync()
    {
        var device = _selectedDevice(); var binding = _library.ScanBinding;
        if (device is null || binding is null) throw new InvalidOperationException("Scan the selected connected Quest before creating a backup.");
        _cancellation = new();
        await OnUiThreadAsync(() => { IsRunning = true; Status = "Creating read-only backup..."; VerificationState = "Not verified"; ApplyProgress(new(0, 0, 0, 0, 0, null)); });
        try
        {
            var reporter = new Progress<QuestBackupProgress>(value => _ = OnUiThreadAsync(() => ApplyProgress(value)));
            var result = await _service!.CreateBackupAsync(device, binding, reporter, _cancellation.Token).ConfigureAwait(false);
            await OnUiThreadAsync(() =>
            {
                Status = result.Message;
                if (result.Progress is not null) ApplyProgress(result.Progress);
                if (result.Manifest is not null) { SnapshotId = result.Manifest.SnapshotId; CreatedTime = result.Manifest.CreatedAtUtc.ToLocalTime().ToString("g"); }
            });
            await RefreshSnapshotsAsync().ConfigureAwait(false);
        }
        finally { _cancellation.Dispose(); _cancellation = null; await OnUiThreadAsync(() => IsRunning = false); }
    }

    private async Task VerifyAsync()
    {
        var result = await _service!.VerifySnapshotAsync(SnapshotId).ConfigureAwait(false);
        _verifiedStatuses[SnapshotId] = result.Status;
        await RefreshSnapshotsAsync().ConfigureAwait(false);
        await OnUiThreadAsync(() => VerificationState = $"{result.Status}: {result.Message}");
    }

    private void Cancel() => _cancellation?.Cancel();
    private void ApplyProgress(QuestBackupProgress value)
    {
        _progress = value;
        OnPropertyChanged(nameof(FilesDiscovered)); OnPropertyChanged(nameof(FilesArchived)); OnPropertyChanged(nameof(BytesArchived));
        OnPropertyChanged(nameof(NewBlobsWritten)); OnPropertyChanged(nameof(ExistingBlobsReused)); OnPropertyChanged(nameof(CurrentFile));
    }
    private void NotifyCommands() { OnPropertyChanged(nameof(CanCreate)); OnPropertyChanged(nameof(CanVerify)); CreateBackupCommand.RaiseCanExecuteChanged(); CancelBackupCommand.RaiseCanExecuteChanged(); VerifyBackupCommand.RaiseCanExecuteChanged(); }
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values) { target.Clear(); foreach (var value in values) target.Add(value); }
}
