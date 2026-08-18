using QuestBeatSync.Core.Models;
using QuestBeatSync.Infrastructure.Abstractions;

namespace QuestBeatSync.Infrastructure.Execution;

public sealed class SyncExecutor
{
    private static readonly IReadOnlySet<string> ExcludedMapFiles =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".qbsync-complete" };

    private readonly IQuestBeatSaberScanner _scanner;
    private readonly IPlaylistExecutionWorkspace _playlistWorkspace;
    private readonly ISyncMapSourceProvider _mapSources;
    private readonly IQuestSyncTarget _target;
    private readonly ISyncExecutionJournal _journal;
    private readonly QuestBeatSaberPaths _paths;

    public SyncExecutor(
        IQuestBeatSaberScanner scanner,
        IPlaylistExecutionWorkspace playlistWorkspace,
        ISyncMapSourceProvider mapSources,
        IQuestSyncTarget target,
        ISyncExecutionJournal journal,
        QuestBeatSaberPaths paths)
    {
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _playlistWorkspace = playlistWorkspace ?? throw new ArgumentNullException(nameof(playlistWorkspace));
        _mapSources = mapSources ?? throw new ArgumentNullException(nameof(mapSources));
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public async Task<SyncResult> ExecuteAsync(
        SyncExecutionPlan executionPlan,
        QuestDevice selectedDevice,
        CancellationToken cancellationToken = default,
        IProgress<SyncProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(executionPlan);
        ArgumentNullException.ThrowIfNull(selectedDevice);
        var executionId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;
        var results = executionPlan.Plan.Operations
            .Select((operation, index) => new SyncOperationResult(index, operation, SyncOperationStatus.Pending))
            .ToArray();
        var diagnosticWarnings = new List<string>();
        var preparedPlaylists = new Dictionary<string, PreparedPlaylistSource>(SyncExecutionPlan.SourcePathComparer);
        var workspaceTouched = false;
        var workspaceCleaned = false;
        progress?.Report(new SyncProgress("Preparing", 0, results.Length, "Validating target Quest and execution inputs."));

        if (!selectedDevice.IsConnected || !string.Equals(selectedDevice.Serial, executionPlan.Target.DeviceSerial, StringComparison.Ordinal))
        {
            return await RefuseAsync("The selected Quest does not match the device bound to this plan.").ConfigureAwait(false);
        }

        var currentScan = await _scanner.ScanAsync(selectedDevice, cancellationToken).ConfigureAwait(false);
        if (!executionPlan.Target.Matches(selectedDevice.Serial, currentScan))
        {
            return await RefuseAsync("Quest state changed after plan creation. Rebuild the sync plan.").ConfigureAwait(false);
        }

        try
        {
            progress?.Report(new SyncProgress("Preparing", 0, executionPlan.PlaylistSources.Count, "Preparing immutable playlist snapshots."));
            workspaceTouched = executionPlan.PlaylistSources.Count > 0;
            foreach (var source in executionPlan.PlaylistSources)
            {
                var prepared = await _playlistWorkspace.PrepareAsync(executionId, source, cancellationToken).ConfigureAwait(false);
                preparedPlaylists.Add(source.CanonicalPath, prepared);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CancelFrom(0);
            return await FinishAsync(SyncRunStatus.Canceled, "Sync was canceled while preparing playlist snapshots.").ConfigureAwait(false);
        }
        catch (PlaylistSourceStaleException exception)
        {
            return await RefuseAsync(exception.Message).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return await RefuseAsync($"Playlist snapshot preparation failed: {exception.Message}").ConfigureAwait(false);
        }

        var preparedMaps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var blockedMaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await TryWriteJournalAsync(SyncRunStatus.Running, null).ConfigureAwait(false);

        // Phase C: all network/cache preparation completes while Beat Saber is still running
        // and before the cooperative remote writer lock is acquired.
        foreach (var index in Enumerable.Range(0, results.Length)
                     .Where(index => results[index].Operation.Kind == SyncOperationKind.DownloadMap))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                CancelPending();
                return await FinishAsync(SyncRunStatus.Canceled, "Sync was canceled.").ConfigureAwait(false);
            }

            var operation = results[index].Operation;
            progress?.Report(new SyncProgress("Downloading", index + 1, results.Length, operation.Description, operation));
            results[index] = results[index] with { Status = SyncOperationStatus.Running };
            await TryWriteJournalAsync(SyncRunStatus.Running, null).ConfigureAwait(false);
            try
            {
                results[index] = await DownloadAsync(results[index], executionPlan, preparedMaps, blockedMaps, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                results[index] = results[index] with { Status = SyncOperationStatus.Canceled, Message = "Operation was canceled." };
                CancelPending();
                return await FinishAsync(SyncRunStatus.Canceled, "Sync was canceled.").ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                if (operation.MapIdentity is not null) blockedMaps.Add(operation.MapIdentity.Hash);
                results[index] = results[index] with { Status = SyncOperationStatus.Failed, Message = exception.Message };
            }
            await TryWriteJournalAsync(SyncRunStatus.Running, null).ConfigureAwait(false);
        }

        // Resolve every upload's local source before the Quest write window. This includes
        // uploads backed by an existing cache entry without a DownloadMap operation.
        foreach (var index in Enumerable.Range(0, results.Length)
                     .Where(index => results[index].Operation.Kind == SyncOperationKind.UploadMap))
        {
            var identity = results[index].Operation.MapIdentity
                ?? throw new InvalidOperationException("UploadMap requires an exact map identity.");
            if (blockedMaps.Contains(identity.Hash))
            {
                results[index] = results[index] with { Status = SyncOperationStatus.Skipped, Message = "The exact map source could not be prepared." };
                continue;
            }
            if (preparedMaps.ContainsKey(identity.Hash)) continue;

            try
            {
                var localPath = await _mapSources.GetCachedMapDirectoryAsync(identity, cancellationToken).ConfigureAwait(false);
                if (localPath is null)
                {
                    blockedMaps.Add(identity.Hash);
                    results[index] = results[index] with { Status = SyncOperationStatus.Failed, Message = "The exact map is not available in the local cache." };
                }
                else
                {
                    preparedMaps[identity.Hash] = localPath;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancelPending();
                return await FinishAsync(SyncRunStatus.Canceled, "Sync was canceled while preparing local map inputs.").ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                blockedMaps.Add(identity.Hash);
                results[index] = results[index] with { Status = SyncOperationStatus.Failed, Message = exception.Message };
            }
        }

        var writeIndexes = Enumerable.Range(0, results.Length)
            .Where(index => results[index].Status == SyncOperationStatus.Pending &&
                            results[index].Operation.Kind is SyncOperationKind.UploadMap or SyncOperationKind.ImportPlaylist)
            .ToArray();
        QuestWriteSession? writeSession = null;
        var canceledDuringWrites = false;
        if (writeIndexes.Length > 0)
        {
            progress?.Report(new SyncProgress("Preparing write session", 0, writeIndexes.Length, "Revalidating Quest state before writes."));
            if (!selectedDevice.IsConnected || !string.Equals(selectedDevice.Serial, executionPlan.Target.DeviceSerial, StringComparison.Ordinal))
                return await RefusePendingAsync("The selected Quest changed before the write phase. Rebuild the sync plan.").ConfigureAwait(false);

            QuestBeatSaberScanResult writePhaseScan;
            try
            {
                writePhaseScan = await _scanner.ScanAsync(selectedDevice, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancelPending();
                return await FinishAsync(SyncRunStatus.Canceled, "Sync was canceled before Quest writes began.").ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                return await RefusePendingAsync($"Could not revalidate Quest state before writing: {exception.Message}").ConfigureAwait(false);
            }

            if (!executionPlan.Target.Matches(selectedDevice.Serial, writePhaseScan))
                return await RefusePendingAsync("Quest state changed during local preparation. Rebuild the sync plan before writing.").ConfigureAwait(false);

            progress?.Report(new SyncProgress("Stopping Beat Saber", 0, writeIndexes.Length, "Acquiring the QBSync writer lock and stopping Beat Saber."));
            try
            {
                var preparation = await _target.BeginWriteSessionAsync(selectedDevice, executionId, cancellationToken).ConfigureAwait(false);
                foreach (var warning in _target.DrainDiagnosticWarnings()) diagnosticWarnings.Add(warning);
                if (!preparation.IsReady || preparation.Session is null)
                    return await RefusePendingAsync(preparation.Message ?? "Quest write preparation was refused.").ConfigureAwait(false);
                writeSession = preparation.Session;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                foreach (var warning in _target.DrainDiagnosticWarnings()) diagnosticWarnings.Add(warning);
                CancelPending();
                return await FinishAsync(SyncRunStatus.Canceled, "Sync was canceled before Quest writes began.").ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                foreach (var warning in _target.DrainDiagnosticWarnings()) diagnosticWarnings.Add(warning);
                return await RefusePendingAsync($"Could not prepare Quest writes: {exception.Message}").ConfigureAwait(false);
            }

            string? lockedValidationRefusal = null;
            var lockedValidationCanceled = false;
            try
            {
                progress?.Report(new SyncProgress("Validating locked Quest state", 0, writeIndexes.Length, "Revalidating Quest state while the QBSync writer lock is held."));
                QuestBeatSaberScanResult lockedScan;
                try
                {
                    lockedScan = await _scanner.ScanAsync(selectedDevice, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    CancelPending();
                    lockedValidationCanceled = true;
                    lockedScan = QuestBeatSaberScanResult.Empty;
                }
                catch (Exception exception)
                {
                    lockedValidationRefusal = $"Could not validate Quest state after writer lock acquisition: {exception.Message}";
                    lockedScan = QuestBeatSaberScanResult.Empty;
                }

                if (!lockedValidationCanceled && lockedValidationRefusal is null &&
                    !executionPlan.Target.Matches(selectedDevice.Serial, lockedScan))
                    lockedValidationRefusal = "Quest state changed after writer lock acquisition. Rebuild the plan.";

                foreach (var index in lockedValidationCanceled || lockedValidationRefusal is not null ? [] : writeIndexes)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        CancelPending();
                        canceledDuringWrites = true;
                        break;
                    }

                    var operation = results[index].Operation;
                    progress?.Report(new SyncProgress(ProgressPhase(operation.Kind), index + 1, results.Length, operation.Description, operation));
                    results[index] = results[index] with { Status = SyncOperationStatus.Running };
                    await TryWriteJournalAsync(SyncRunStatus.Running, null).ConfigureAwait(false);
                    try
                    {
                        results[index] = operation.Kind switch
                        {
                            SyncOperationKind.UploadMap => await UploadAsync(results[index], selectedDevice, executionId, preparedMaps, blockedMaps, diagnosticWarnings, cancellationToken).ConfigureAwait(false),
                            SyncOperationKind.ImportPlaylist => await ImportPlaylistAsync(results[index], selectedDevice, preparedPlaylists, cancellationToken).ConfigureAwait(false),
                            _ => throw new InvalidOperationException("Only Quest write operations may enter the write phase.")
                        };
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        results[index] = results[index] with { Status = SyncOperationStatus.Canceled, Message = "Operation was canceled." };
                        CancelPending();
                        canceledDuringWrites = true;
                        break;
                    }
                    catch (Exception exception)
                    {
                        if (operation.MapIdentity is not null) blockedMaps.Add(operation.MapIdentity.Hash);
                        results[index] = results[index] with { Status = SyncOperationStatus.Failed, Message = exception.Message };
                    }
                    finally
                    {
                        foreach (var warning in _target.DrainDiagnosticWarnings()) diagnosticWarnings.Add(warning);
                    }

                    await TryWriteJournalAsync(SyncRunStatus.Running, null).ConfigureAwait(false);
                }
            }
            finally
            {
                try
                {
                    await _target.EndWriteSessionAsync(selectedDevice, writeSession, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    diagnosticWarnings.Add($"QBSync writer lock could not be released: {exception.Message}");
                }
                foreach (var warning in _target.DrainDiagnosticWarnings()) diagnosticWarnings.Add(warning);
            }

            if (lockedValidationCanceled)
                return await FinishAsync(SyncRunStatus.Canceled, "Sync was canceled before Quest writes began.").ConfigureAwait(false);
            if (lockedValidationRefusal is not null)
                return await RefusePendingAsync(lockedValidationRefusal).ConfigureAwait(false);
        }

        foreach (var index in Enumerable.Range(0, results.Length).Where(index => results[index].Status == SyncOperationStatus.Pending))
            results[index] = results[index] with { Status = SyncOperationStatus.Skipped, Message = "This operation does not write to the Quest." };

        if (canceledDuringWrites)
            return await FinishAsync(SyncRunStatus.Canceled, "Sync was canceled.").ConfigureAwait(false);

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

        async Task<SyncResult> RefusePendingAsync(string message)
        {
            for (var index = 0; index < results.Length; index++)
            {
                if (results[index].Status == SyncOperationStatus.Pending)
                    results[index] = results[index] with { Status = SyncOperationStatus.Skipped, Message = message };
            }
            return await FinishAsync(SyncRunStatus.Refused, message).ConfigureAwait(false);
        }

        void CancelFrom(int start)
        {
            for (var index = start; index < results.Length; index++)
                results[index] = results[index] with { Status = SyncOperationStatus.Canceled, Message = "Sync was canceled before this operation ran." };
        }

        void CancelPending()
        {
            for (var index = 0; index < results.Length; index++)
            {
                if (results[index].Status == SyncOperationStatus.Pending)
                    results[index] = results[index] with { Status = SyncOperationStatus.Canceled, Message = "Sync was canceled before this operation ran." };
            }
        }

        async Task TryWriteJournalAsync(SyncRunStatus status, string? message)
        {
            try
            {
                await _journal.WriteAsync(CreateResult(status, message), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                diagnosticWarnings.Add($"Diagnostic journal could not be written: {exception.Message}");
            }
        }

        async Task<SyncResult> FinishAsync(SyncRunStatus status, string? message)
        {
            if (workspaceTouched && !workspaceCleaned)
            {
                workspaceCleaned = true;
                try
                {
                    await _playlistWorkspace.CleanupAsync(executionId, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    diagnosticWarnings.Add($"Execution playlist snapshots could not be cleaned: {exception.Message}");
                }
            }

            await TryWriteJournalAsync(status, message).ConfigureAwait(false);
            return CreateResult(status, message);
        }

        SyncResult CreateResult(SyncRunStatus status, string? message) =>
            new(executionId, status, selectedDevice.Serial, startedAt, DateTimeOffset.UtcNow, results.ToArray(), diagnosticWarnings.ToArray(), message);
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
        ICollection<string> diagnosticWarnings,
        CancellationToken cancellationToken)
    {
        var identity = result.Operation.MapIdentity ?? throw new InvalidOperationException("UploadMap requires an exact map identity.");
        if (blockedMaps.Contains(identity.Hash))
            return result with { Status = SyncOperationStatus.Skipped, Message = "The exact map source could not be prepared." };

        var finalPath = QuestExecutionPaths.MapFinal(_paths, identity);
        if (await _target.DirectoryExistsAsync(device, finalPath, cancellationToken).ConfigureAwait(false))
            return result with { Status = SyncOperationStatus.Skipped, Message = "Final map directory already exists; it was preserved." };

        if (!preparedMaps.TryGetValue(identity.Hash, out var localPath))
            return result with { Status = SyncOperationStatus.Failed, Message = "The exact map source was not prepared before the Quest write session." };

        var stagingPath = QuestExecutionPaths.MapStaging(_paths, identity, executionId);
        var stagingCreated = false;
        try
        {
            await _target.CreateStagingDirectoryAsync(device, stagingPath, cancellationToken).ConfigureAwait(false);
            stagingCreated = true;
            await _target.UploadMapDirectoryAsync(device, localPath, stagingPath, ExcludedMapFiles, cancellationToken).ConfigureAwait(false);
            if (!await _target.VerifyStagedMapStructureAsync(device, stagingPath, identity, cancellationToken).ConfigureAwait(false))
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
            {
                try
                {
                    await _target.AbandonStagingAsync(device, stagingPath, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    diagnosticWarnings.Add($"Current map staging could not be cleaned: {exception.Message}");
                }
            }
        }
    }

    private async Task<SyncOperationResult> ImportPlaylistAsync(
        SyncOperationResult result,
        QuestDevice device,
        IReadOnlyDictionary<string, PreparedPlaylistSource> preparedPlaylists,
        CancellationToken cancellationToken)
    {
        var source = result.Operation.PlaylistSource ?? throw new InvalidOperationException("ImportPlaylist requires a source identity.");
        if (!preparedPlaylists.TryGetValue(source.CanonicalPath, out var prepared) ||
            !string.Equals(prepared.ContentSha256, source.ContentSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The approved playlist snapshot is unavailable.");
        }

        await _target.ImportPlaylistAsync(device, prepared, cancellationToken).ConfigureAwait(false);
        return result with { Status = SyncOperationStatus.Succeeded };
    }

    private static bool IsExactLookup(BeatMapIdentity identity, BeatSaverLookupResult lookup) =>
        lookup.Availability == BeatSaverAvailability.Online &&
        lookup.ExactHashMatched &&
        string.Equals(lookup.RequestedHash, identity.Hash, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(lookup.ResolvedHash, identity.Hash, StringComparison.OrdinalIgnoreCase) &&
        lookup.DownloadUri is not null;

    private static string ProgressPhase(SyncOperationKind kind) => kind switch
    {
        SyncOperationKind.DownloadMap => "Downloading",
        SyncOperationKind.UploadMap => "Uploading and verifying",
        SyncOperationKind.ImportPlaylist => "Transferring playlist",
        _ => "Reviewing"
    };
}
