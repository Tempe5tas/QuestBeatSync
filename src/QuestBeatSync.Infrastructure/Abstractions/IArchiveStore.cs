using QuestBeatSync.Core.Models;

namespace QuestBeatSync.Infrastructure.Abstractions;

public sealed record BlobCommitResult(string BlobId, long Size, bool WasCreated);

public interface IArchiveStore
{
    string CreateExecutionStagingDirectory(Guid executionId);
    Task<BlobCommitResult> CommitBlobAsync(string stagedFile, CancellationToken cancellationToken = default);
    Task PublishSnapshotAsync(ArchiveManifest manifest, string executionStagingDirectory, CancellationToken cancellationToken = default);
    Task<SnapshotVerificationResult> VerifySnapshotAsync(string snapshotId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SnapshotSummary>> ListSnapshotsAsync(CancellationToken cancellationToken = default);
    void CleanupStagingBestEffort(string executionStagingDirectory);
}
