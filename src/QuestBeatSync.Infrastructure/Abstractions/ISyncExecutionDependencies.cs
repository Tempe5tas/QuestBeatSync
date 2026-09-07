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

    async Task<IReadOnlyList<MapStructureValidationResult>> ValidateStagedMapsAsync(
        QuestDevice device,
        IReadOnlyList<MapWritePathBinding> maps,
        CancellationToken cancellationToken = default)
    {
        var results = new List<MapStructureValidationResult>(maps.Count);
        foreach (var map in maps)
        {
            var valid = await VerifyStagedMapStructureAsync(device, map.Path, map.Identity, cancellationToken).ConfigureAwait(false);
            results.Add(new(map.Identity, valid ? MapStructureValidationStatus.Valid : MapStructureValidationStatus.InvalidStructure));
        }
        return results;
    }

    async Task<MapPromotionResult> PromoteStagingCompactAsync(
        QuestDevice device,
        MapPromotionRequest map,
        CancellationToken cancellationToken = default)
    {
        if (await DirectoryExistsAsync(device, map.FinalPath, cancellationToken).ConfigureAwait(false))
            return new(map.Identity, MapPromotionStatus.FinalAlreadyExists);
        var promoted = await TryPromoteStagingAsync(device, map.StagingPath, map.FinalPath, cancellationToken).ConfigureAwait(false);
        return new(map.Identity, promoted ? MapPromotionStatus.Promoted : MapPromotionStatus.FinalAlreadyExists);
    }

    Task<IReadOnlyList<MapStructureValidationResult>> VerifyFinalMapsAsync(
        QuestDevice device,
        IReadOnlyList<MapWritePathBinding> maps,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<MapStructureValidationResult>>(
            maps.Select(map => new MapStructureValidationResult(map.Identity, MapStructureValidationStatus.Valid)).ToArray());

    Task AbandonStagingAsync(QuestDevice device, string stagingPath, CancellationToken cancellationToken = default);

    Task ImportPlaylistAsync(
        QuestDevice device,
        PreparedPlaylistSource source,
        CancellationToken cancellationToken = default);
}

public sealed record MapWritePathBinding(BeatMapIdentity Identity, string Path);

public sealed record MapPromotionRequest(BeatMapIdentity Identity, string StagingPath, string FinalPath);

public enum MapStructureValidationStatus
{
    Valid,
    MissingDirectory,
    MissingInfoDat,
    NoOtherFiles,
    InvalidStructure,
    InvalidResponse
}

public sealed record MapStructureValidationResult(
    BeatMapIdentity Identity,
    MapStructureValidationStatus Status,
    string? Diagnostic = null)
{
    public bool IsValid => Status == MapStructureValidationStatus.Valid;
}

public enum MapPromotionStatus
{
    Promoted,
    FinalAlreadyExists,
    StagingMissing,
    Ambiguous,
    Failed
}

public sealed record MapPromotionResult(
    BeatMapIdentity Identity,
    MapPromotionStatus Status,
    string? Diagnostic = null)
{
    public bool WasPromoted => Status == MapPromotionStatus.Promoted;
}

public interface ISyncExecutionJournal
{
    Task WriteAsync(SyncResult result, CancellationToken cancellationToken = default);
}
