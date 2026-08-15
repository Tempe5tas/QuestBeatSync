using QuestBeatSync.Core.Models;

namespace QuestBeatSync.Infrastructure.Abstractions;

public interface IPlaylistExecutionWorkspace
{
    // Snapshots are execution-owned immutable inputs. Cleanup is best-effort after the run;
    // leftovers are diagnostic artifacts only and must never be resumed as a SyncPlan.
    Task<PreparedPlaylistSource> PrepareAsync(
        Guid executionId,
        PlaylistSourceIdentity source,
        CancellationToken cancellationToken = default);

    Task CleanupAsync(Guid executionId, CancellationToken cancellationToken = default);
}

public interface ISyncMapSourceProvider
{
    Task<string?> GetCachedMapDirectoryAsync(BeatMapIdentity identity, CancellationToken cancellationToken = default);

    Task<string> DownloadExactMapAsync(
        BeatMapIdentity identity,
        BeatSaverLookupResult exactLookup,
        CancellationToken cancellationToken = default);
}

public interface IQuestSyncTarget
{
    IReadOnlyList<string> DrainDiagnosticWarnings();

    Task<QuestWritePreparationResult> BeginWriteSessionAsync(
        QuestDevice device,
        Guid executionId,
        CancellationToken cancellationToken = default);

    Task EndWriteSessionAsync(
        QuestDevice device,
        QuestWriteSession session,
        CancellationToken cancellationToken = default);

    Task<bool> DirectoryExistsAsync(QuestDevice device, string remotePath, CancellationToken cancellationToken = default);

    Task CreateStagingDirectoryAsync(QuestDevice device, string stagingPath, CancellationToken cancellationToken = default);

    Task UploadMapDirectoryAsync(
        QuestDevice device,
        string localMapDirectory,
        string stagingPath,
        IReadOnlySet<string> excludedFileNames,
        CancellationToken cancellationToken = default);

    // This verifies staged transfer completeness and required map structure only.
    // It does not recompute BeatSaver's version SHA1 from map contents.
    Task<bool> VerifyStagedMapStructureAsync(
        QuestDevice device,
        string stagingPath,
        BeatMapIdentity expectedIdentity,
        CancellationToken cancellationToken = default);

    Task<bool> TryPromoteStagingAsync(
        QuestDevice device,
        string stagingPath,
        string finalPath,
        CancellationToken cancellationToken = default);

    Task AbandonStagingAsync(QuestDevice device, string stagingPath, CancellationToken cancellationToken = default);

    Task ImportPlaylistAsync(
        QuestDevice device,
        PreparedPlaylistSource source,
        CancellationToken cancellationToken = default);
}

public interface ISyncExecutionJournal
{
    Task WriteAsync(SyncResult result, CancellationToken cancellationToken = default);
}
