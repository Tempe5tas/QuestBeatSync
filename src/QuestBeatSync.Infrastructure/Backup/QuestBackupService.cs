using QuestBeatSync.Core.Models;
using QuestBeatSync.Infrastructure.Abstractions;

namespace QuestBeatSync.Infrastructure.Backup;

public interface IQuestBackupService
{
    Task<QuestBackupResult> CreateBackupAsync(QuestDevice selectedDevice, QuestScanBinding expectedDeviceBinding, IProgress<QuestBackupProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<SnapshotVerificationResult> VerifySnapshotAsync(string snapshotId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SnapshotSummary>> ListSnapshotsAsync(CancellationToken cancellationToken = default);
}

public sealed class QuestBackupService(
    IQuestBeatSaberScanner scanner,
    IQuestBackupSource source,
    IArchiveStore archiveStore) : IQuestBackupService
{
    public async Task<QuestBackupResult> CreateBackupAsync(
        QuestDevice selectedDevice,
        QuestScanBinding expectedDeviceBinding,
        IProgress<QuestBackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selectedDevice);
        ArgumentNullException.ThrowIfNull(expectedDeviceBinding);
        if (!selectedDevice.IsConnected)
            return new(QuestBackupStatus.Refused, "Select a connected Quest before creating a backup.");
        if (!string.Equals(selectedDevice.Serial, expectedDeviceBinding.DeviceSerial, StringComparison.Ordinal))
            return new(QuestBackupStatus.Refused, "The selected Quest does not match the requested backup binding.");

        var executionId = Guid.NewGuid();
        var staging = archiveStore.CreateExecutionStagingDirectory(executionId);
        var state = new QuestBackupProgress(0, 0, 0, 0, 0, null);
        try
        {
            var beforeScan = await scanner.ScanAsync(selectedDevice, cancellationToken).ConfigureAwait(false);
            var before = QuestScanBinding.Capture(selectedDevice.Serial, beforeScan);
            if (!expectedDeviceBinding.Matches(selectedDevice.Serial, beforeScan))
                return new(QuestBackupStatus.Refused, "Quest state changed before backup started. Scan again and retry.");

            var files = await source.DiscoverAsync(selectedDevice, cancellationToken).ConfigureAwait(false);
            state = state with { FilesDiscovered = files.Count };
            progress?.Report(state);
            var entries = new List<ArchiveEntry>(files.Count);
            for (var index = 0; index < files.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = files[index];
                state = state with { CurrentFile = $"{file.LogicalRoot}/{file.RelativePath}" };
                progress?.Report(state);
                var safeTemporary = Path.Combine(staging, $"pull-{index:D8}-{Guid.NewGuid():N}.bin");
                await source.PullAsync(selectedDevice, file, safeTemporary, cancellationToken).ConfigureAwait(false);
                var blob = await archiveStore.CommitBlobAsync(safeTemporary, cancellationToken).ConfigureAwait(false);
                TryDelete(safeTemporary);
                entries.Add(new(file.LogicalRoot, ArchivePathSafety.NormalizeRelativePath(file.RelativePath), ArchiveEntryKind.File, blob.Size, blob.BlobId, blob.BlobId));
                state = state with
                {
                    FilesArchived = state.FilesArchived + 1,
                    BytesArchived = state.BytesArchived + blob.Size,
                    NewBlobsWritten = state.NewBlobsWritten + (blob.WasCreated ? 1 : 0),
                    ExistingBlobsReused = state.ExistingBlobsReused + (blob.WasCreated ? 0 : 1)
                };
                progress?.Report(state);
            }

            var afterScan = await scanner.ScanAsync(selectedDevice, cancellationToken).ConfigureAwait(false);
            var after = QuestScanBinding.Capture(selectedDevice.Serial, afterScan);
            if (!before.Matches(selectedDevice.Serial, afterScan))
                return new(QuestBackupStatus.SourceChanged, "Quest library or Beat Saber version changed during capture. No snapshot was published; retry the backup.", null, state);

            var snapshotId = $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}";
            var package = beforeScan.BeatSaberPackageVersion;
            var manifest = new ArchiveManifest(
                ArchiveManifest.CurrentFormatVersion, snapshotId, DateTimeOffset.UtcNow,
                new(selectedDevice.Serial, selectedDevice.TransportKind, selectedDevice.AndroidModel),
                new(package?.VersionName, package?.VersionCode),
                before.StateFingerprint, after.StateFingerprint, ArchiveConsistencyStatus.Consistent,
                entries.OrderBy(e => e.LogicalRoot).ThenBy(e => e.RelativePath, StringComparer.Ordinal).ToArray());
            await archiveStore.PublishSnapshotAsync(manifest, staging, cancellationToken).ConfigureAwait(false);
            state = state with { CurrentFile = null };
            progress?.Report(state);
            return new(QuestBackupStatus.Completed, "Backup snapshot created successfully.", manifest, state);
        }
        catch (OperationCanceledException)
        {
            return new(QuestBackupStatus.Canceled, "Backup was canceled. No snapshot was published.", null, state);
        }
        catch (Exception exception)
        {
            return new(QuestBackupStatus.Failed, exception.Message, null, state);
        }
        finally { archiveStore.CleanupStagingBestEffort(staging); }
    }

    public Task<SnapshotVerificationResult> VerifySnapshotAsync(string snapshotId, CancellationToken cancellationToken = default) =>
        archiveStore.VerifySnapshotAsync(snapshotId, cancellationToken);

    public Task<IReadOnlyList<SnapshotSummary>> ListSnapshotsAsync(CancellationToken cancellationToken = default) =>
        archiveStore.ListSnapshotsAsync(cancellationToken);

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
