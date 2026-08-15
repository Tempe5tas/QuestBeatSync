using QuestBeatSync.Core.Models;
using QuestBeatSync.Infrastructure.Abstractions;

namespace QuestBeatSync.Infrastructure.Execution;

public sealed class SyncExecutor
{
    private static readonly IReadOnlySet<string> ExcludedMapFiles =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".qbsync-complete" };

    private readonly IQuestBeatSaberScanner _scanner;
    private readonly IPlaylistSourceVerifier _playlistVerifier;
    private readonly ISyncMapSourceProvider _mapSources;
    private readonly IQuestSyncTarget _target;
    private readonly ISyncExecutionJournal _journal;
    private readonly QuestBeatSaberPaths _paths;

    public SyncExecutor(
        IQuestBeatSaberScanner scanner,
        IPlaylistSourceVerifier playlistVerifier,
        ISyncMapSourceProvider mapSources,
        IQuestSyncTarget target,
        ISyncExecutionJournal journal,
        QuestBeatSaberPaths paths)
    {
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _playlistVerifier = playlistVerifier ?? throw new ArgumentNullException(nameof(playlistVerifier));
        _mapSources = mapSources ?? throw new ArgumentNullException(nameof(mapSources));
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public async Task<SyncResult> ExecuteAsync(
        SyncExecutionPlan executionPlan,
        QuestDevice selectedDevice,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executionPlan);
        ArgumentNullException.ThrowIfNull(selectedDevice);
        var executionId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;
        var results = executionPlan.Plan.Operations
            .Select((operation, index) => new SyncOperationResult(index, operation, SyncOperationStatus.Pending))
            .ToArray();

        if (!selectedDevice.IsConnected || !string.Equals(selectedDevice.Serial, executionPlan.Target.DeviceSerial, StringComparison.Ordinal))
        {
            return await RefuseAsync("The selected Quest does not match the device bound to this plan.").ConfigureAwait(false);
        }

        var currentScan = await _scanner.ScanAsync(selectedDevice, cancellationToken).ConfigureAwait(false);
        if (!executionPlan.Target.Matches(selectedDevice.Serial, currentScan))
        {
            return await RefuseAsync("Quest state changed after plan creation. Rebuild the sync plan.").ConfigureAwait(false);
        }

        foreach (var source in executionPlan.PlaylistSources)
        {
            if (!await _playlistVerifier.MatchesAsync(source, cancellationToken).ConfigureAwait(false))
            {
                return await RefuseAsync($"Playlist source changed or is unavailable: {source.CanonicalPath}").ConfigureAwait(false);
            }
        }

        var preparedMaps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var blockedMaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await WriteJournalAsync(SyncRunStatus.Running, null).ConfigureAwait(false);

        for (var index = 0; index < results.Length; index++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                CancelFrom(index);
                return await FinishAsync(SyncRunStatus.Canceled, "Sync was canceled.").ConfigureAwait(false);
            }

            var operation = results[index].Operation;
            results[index] = results[index] with { Status = SyncOperationStatus.Running };
            await WriteJournalAsync(SyncRunStatus.Running, null).ConfigureAwait(false);

            try
            {
                results[index] = operation.Kind switch
                {
                    SyncOperationKind.DownloadMap => await DownloadAsync(results[index], executionPlan, preparedMaps, blockedMaps, cancellationToken).ConfigureAwait(false),
                    SyncOperationKind.UploadMap => await UploadAsync(results[index], selectedDevice, executionId, preparedMaps, blockedMaps, cancellationToken).ConfigureAwait(false),
                    SyncOperationKind.ImportPlaylist => await ImportPlaylistAsync(results[index], selectedDevice, cancellationToken).ConfigureAwait(false),
                    _ => results[index] with { Status = SyncOperationStatus.Skipped, Message = "This operation does not write to the Quest." }
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                results[index] = results[index] with { Status = SyncOperationStatus.Canceled, Message = "Operation was canceled." };
                CancelFrom(index + 1);
                return await FinishAsync(SyncRunStatus.Canceled, "Sync was canceled.").ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                if (operation.MapIdentity is not null) blockedMaps.Add(operation.MapIdentity.Hash);
                results[index] = results[index] with { Status = SyncOperationStatus.Failed, Message = exception.Message };
            }

            await WriteJournalAsync(SyncRunStatus.Running, null).ConfigureAwait(false);
        }

        return await FinishAsync(
            results.Any(result => result.Status == SyncOperationStatus.Failed)
                ? SyncRunStatus.CompletedWithFailures
                : SyncRunStatus.Completed,
            null).ConfigureAwait(false);

        async Task<SyncResult> RefuseAsync(string message)
        {
            for (var index = 0; index < results.Length; index++)
                results[index] = results[index] with { Status = SyncOperationStatus.Skipped, Message = message };
            return await FinishAsync(SyncRunStatus.Refused, message).ConfigureAwait(false);
        }

        void CancelFrom(int start)
        {
            for (var index = start; index < results.Length; index++)
                results[index] = results[index] with { Status = SyncOperationStatus.Canceled, Message = "Sync was canceled before this operation ran." };
        }

        Task WriteJournalAsync(SyncRunStatus status, string? message) =>
            _journal.WriteAsync(CreateResult(status, message), CancellationToken.None);

        async Task<SyncResult> FinishAsync(SyncRunStatus status, string? message)
        {
            var result = CreateResult(status, message);
            await _journal.WriteAsync(result, CancellationToken.None).ConfigureAwait(false);
            return result;
        }

        SyncResult CreateResult(SyncRunStatus status, string? message) =>
            new(executionId, status, selectedDevice.Serial, startedAt, DateTimeOffset.UtcNow, results.ToArray(), message);
    }

    private async Task<SyncOperationResult> DownloadAsync(
        SyncOperationResult result,
        SyncExecutionPlan plan,
        IDictionary<string, string> preparedMaps,
        ISet<string> blockedMaps,
        CancellationToken cancellationToken)
    {
        var identity = result.Operation.MapIdentity ?? throw new InvalidOperationException("DownloadMap requires an exact map identity.");
        if (!plan.ExactLookups.TryGetValue(identity.Hash, out var lookup) || !IsExactLookup(identity, lookup))
        {
            blockedMaps.Add(identity.Hash);
            return result with { Status = SyncOperationStatus.Failed, Message = "Exact-hash BeatSaver evidence is missing or mismatched." };
        }

        preparedMaps[identity.Hash] = await _mapSources.DownloadExactMapAsync(identity, lookup, cancellationToken).ConfigureAwait(false);
        return result with { Status = SyncOperationStatus.Succeeded };
    }

    private async Task<SyncOperationResult> UploadAsync(
        SyncOperationResult result,
        QuestDevice device,
        Guid executionId,
        IDictionary<string, string> preparedMaps,
        ISet<string> blockedMaps,
        CancellationToken cancellationToken)
    {
        var identity = result.Operation.MapIdentity ?? throw new InvalidOperationException("UploadMap requires an exact map identity.");
        if (blockedMaps.Contains(identity.Hash))
            return result with { Status = SyncOperationStatus.Skipped, Message = "The exact map source could not be prepared." };

        var finalPath = JoinRemote(_paths.CustomLevels, identity.Hash);
        if (await _target.DirectoryExistsAsync(device, finalPath, cancellationToken).ConfigureAwait(false))
            return result with { Status = SyncOperationStatus.Skipped, Message = "Final map directory already exists; it was preserved." };

        if (!preparedMaps.TryGetValue(identity.Hash, out var localPath))
        {
            localPath = await _mapSources.GetCachedMapDirectoryAsync(identity, cancellationToken).ConfigureAwait(false);
            if (localPath is null)
                return result with { Status = SyncOperationStatus.Failed, Message = "The exact map is not available in the local cache." };
        }

        var stagingPath = JoinRemote(_paths.CustomLevels, $".qbsync-{identity.Hash}-{executionId:N}");
        var stagingCreated = false;
        try
        {
            await _target.CreateStagingDirectoryAsync(device, stagingPath, cancellationToken).ConfigureAwait(false);
            stagingCreated = true;
            await _target.UploadMapDirectoryAsync(device, localPath, stagingPath, ExcludedMapFiles, cancellationToken).ConfigureAwait(false);
            if (!await _target.VerifyStagedMapAsync(device, stagingPath, identity, cancellationToken).ConfigureAwait(false))
                return result with { Status = SyncOperationStatus.Failed, Message = "Staged map verification failed." };
            if (await _target.DirectoryExistsAsync(device, finalPath, cancellationToken).ConfigureAwait(false))
                return result with { Status = SyncOperationStatus.Skipped, Message = "Final map directory appeared during upload; it was preserved." };

            cancellationToken.ThrowIfCancellationRequested();
            if (!await _target.TryPromoteStagingAsync(device, stagingPath, finalPath, CancellationToken.None).ConfigureAwait(false))
                return result with { Status = SyncOperationStatus.Skipped, Message = "Final map directory already exists; it was preserved." };

            stagingCreated = false;
            return result with { Status = SyncOperationStatus.Succeeded };
        }
        finally
        {
            if (stagingCreated)
                await _target.AbandonStagingAsync(device, stagingPath, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<SyncOperationResult> ImportPlaylistAsync(
        SyncOperationResult result,
        QuestDevice device,
        CancellationToken cancellationToken)
    {
        var source = result.Operation.PlaylistSource ?? throw new InvalidOperationException("ImportPlaylist requires a source identity.");
        await _target.ImportPlaylistAsync(device, source, cancellationToken).ConfigureAwait(false);
        return result with { Status = SyncOperationStatus.Succeeded };
    }

    private static bool IsExactLookup(BeatMapIdentity identity, BeatSaverLookupResult lookup) =>
        lookup.Availability == BeatSaverAvailability.Online &&
        lookup.ExactHashMatched &&
        string.Equals(lookup.RequestedHash, identity.Hash, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(lookup.ResolvedHash, identity.Hash, StringComparison.OrdinalIgnoreCase) &&
        lookup.DownloadUri is not null;

    private static string JoinRemote(string parent, string child) => $"{parent.TrimEnd('/')}/{child}";
}
