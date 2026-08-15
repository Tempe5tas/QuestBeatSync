using QuestBeatSync.Core.Models;

namespace QuestBeatSync.Infrastructure.Abstractions;

public interface IPlaylistSourceVerifier
{
    Task<bool> MatchesAsync(PlaylistSourceIdentity source, CancellationToken cancellationToken = default);
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
    Task<bool> DirectoryExistsAsync(QuestDevice device, string remotePath, CancellationToken cancellationToken = default);

    Task CreateStagingDirectoryAsync(QuestDevice device, string stagingPath, CancellationToken cancellationToken = default);

    Task UploadMapDirectoryAsync(
        QuestDevice device,
        string localMapDirectory,
        string stagingPath,
        IReadOnlySet<string> excludedFileNames,
        CancellationToken cancellationToken = default);

    Task<bool> VerifyStagedMapAsync(
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
        PlaylistSourceIdentity source,
        CancellationToken cancellationToken = default);
}

public interface ISyncExecutionJournal
{
    Task WriteAsync(SyncResult result, CancellationToken cancellationToken = default);
}
