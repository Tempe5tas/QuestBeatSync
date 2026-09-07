using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuestBeatSync.Core.Models;
using QuestBeatSync.Infrastructure.Abstractions;
using QuestBeatSync.Infrastructure.Execution;

namespace QuestBeatSync.Tests;

[TestClass]
public sealed class BatchedMapExecutionTests
{
    private static readonly QuestDevice Device = new("SERIAL", QuestConnectionState.Device, QuestTransportKind.Usb);
    private static readonly QuestBeatSaberScanResult Scan = QuestBeatSaberScanResult.Empty;

    [TestMethod]
    public async Task ThreeMaps_AllStageBeforeFirstBatchValidationAndPromotion()
    {
        var target = new BatchTarget();
        var result = await ExecuteAsync(3, target);
        CollectionAssert.AreEqual(
            new[] { "stage:0", "upload:0", "stage:1", "upload:1", "stage:2", "upload:2", "validate:0,1,2", "promote:0", "promote:1", "promote:2", "final:0,1,2" },
            target.Events.ToArray());
        Assert.IsTrue(result.Operations.All(operation => operation.Status == SyncOperationStatus.Succeeded));
    }

    [TestMethod]
    public async Task RealScale190Maps_UsesSixValidationBatchesAndIndependentPromotions()
    {
        var target = new BatchTarget();
        var result = await ExecuteAsync(190, target);
        Assert.AreEqual(SyncRunStatus.Completed, result.Status);
        Assert.AreEqual(190, result.Operations.Count); Assert.IsFalse(result.Operations.Any(operation => operation.Status == SyncOperationStatus.Pending));
        Assert.AreEqual(190, target.PushCount); Assert.AreEqual(190, target.PromotionCount);
        Assert.AreEqual(6, target.StagingValidationBatchCount); Assert.AreEqual(6, target.FinalValidationBatchCount);
        Assert.IsTrue(target.MaximumBatchSize <= SyncExecutor.MapValidationBatchSize);
        Assert.AreEqual(1, target.BeginCount); Assert.AreEqual(1, target.EndCount); Assert.AreEqual(0, target.DestructiveFinalCommands);
    }

    [TestMethod]
    public async Task MissingDuplicateAndUnexpectedValidationResults_FailClosedWithoutBlockingOthers()
    {
        var target = new BatchTarget { OmitValidationIndex = 0, DuplicateValidationIndex = 1, InvalidValidationIndex = 2 };
        var result = await ExecuteAsync(4, target);
        Assert.AreEqual(SyncOperationStatus.Failed, result.Operations[0].Status);
        Assert.AreEqual(SyncOperationStatus.Failed, result.Operations[1].Status);
        Assert.AreEqual(SyncOperationStatus.Failed, result.Operations[2].Status);
        Assert.AreEqual(SyncOperationStatus.Succeeded, result.Operations[3].Status);
        CollectionAssert.AreEqual(new[] { 3 }, target.PromotedIndexes.ToArray());
    }

    [TestMethod]
    public async Task OneInvalidStagingMap_DoesNotPreventOtherPromotions()
    {
        var target = new BatchTarget { InvalidValidationIndex = 1 };
        var result = await ExecuteAsync(3, target);
        Assert.AreEqual(SyncOperationStatus.Succeeded, result.Operations[0].Status);
        Assert.AreEqual(SyncOperationStatus.Failed, result.Operations[1].Status);
        Assert.AreEqual(SyncOperationStatus.Succeeded, result.Operations[2].Status);
        CollectionAssert.AreEqual(new[] { 0, 2 }, target.PromotedIndexes.ToArray());
    }

    [TestMethod]
    public async Task PromotionFinalAlreadyExists_IsSkippedAndNeverOverwritten()
    {
        var target = new BatchTarget { FinalExistsOnPromotionIndex = 1 };
        var result = await ExecuteAsync(3, target);
        Assert.AreEqual(SyncOperationStatus.Skipped, result.Operations[1].Status);
        Assert.IsFalse(target.PromotedIndexes.Contains(1)); Assert.AreEqual(0, target.FinalOverwriteCount);
    }

    [TestMethod]
    public async Task AmbiguousPromotion_IsNeverReportedAsSuccess()
    {
        var target = new BatchTarget { AmbiguousPromotionIndex = 0 };
        var result = await ExecuteAsync(2, target);
        Assert.AreEqual(SyncOperationStatus.Failed, result.Operations[0].Status);
        Assert.AreEqual(SyncOperationStatus.Succeeded, result.Operations[1].Status);
    }

    [TestMethod]
    public async Task FinalVerificationFailure_PreservesPromotedFinalAndReportsTruth()
    {
        var target = new BatchTarget { InvalidFinalIndex = 0 };
        var result = await ExecuteAsync(2, target);
        Assert.AreEqual(SyncOperationStatus.Failed, result.Operations[0].Status);
        StringAssert.Contains(result.Operations[0].Message, "promotion succeeded");
        Assert.IsTrue(target.FinalIndexes.Contains(0)); Assert.AreEqual(0, target.DestructiveFinalCommands);
    }

    [TestMethod]
    public async Task CancellationAfterBatchValidation_PromotesNothingAndCleansOwnedStaging()
    {
        using var cancellation = new CancellationTokenSource();
        var target = new BatchTarget { CancelAfterStagingValidation = cancellation };
        var result = await ExecuteAsync(3, target, cancellation.Token);
        Assert.AreEqual(SyncRunStatus.Canceled, result.Status); Assert.AreEqual(0, target.PromotionCount);
        Assert.AreEqual(3, target.CleanupCount); Assert.IsTrue(result.Operations.All(operation => operation.Status == SyncOperationStatus.Canceled));
    }

    [TestMethod]
    public async Task CancellationAfterOnePromotion_PreservesFinalAndCancelsUnpromotedMaps()
    {
        using var cancellation = new CancellationTokenSource();
        var target = new BatchTarget { CancelAfterPromotionIndex = 0, PromotionCancellation = cancellation };
        var result = await ExecuteAsync(3, target, cancellation.Token);
        Assert.AreEqual(SyncRunStatus.Canceled, result.Status);
        Assert.IsTrue(target.FinalIndexes.Contains(0)); Assert.AreEqual(0, target.DestructiveFinalCommands);
        Assert.AreEqual(SyncOperationStatus.Failed, result.Operations[0].Status);
        StringAssert.Contains(result.Operations[0].Message, "promotion succeeded");
        Assert.AreEqual(SyncOperationStatus.Canceled, result.Operations[1].Status);
        Assert.AreEqual(SyncOperationStatus.Canceled, result.Operations[2].Status);
    }

    [TestMethod]
    public async Task UploadFailure_DoesNotPreventLaterMapsFromCompleting()
    {
        var target = new BatchTarget { FailedUploadIndex = 0 };
        var result = await ExecuteAsync(3, target);
        Assert.AreEqual(SyncOperationStatus.Failed, result.Operations[0].Status);
        Assert.AreEqual(SyncOperationStatus.Succeeded, result.Operations[1].Status);
        Assert.AreEqual(SyncOperationStatus.Succeeded, result.Operations[2].Status);
    }

    [TestMethod]
    public void BatchSentinelParser_BindsByExactHashAndFailsMissingDuplicateMalformedClosed()
    {
        var maps = Enumerable.Range(0, 4).Select(index => new MapWritePathBinding(Identity(index), $"/staging/{index}")).ToArray();
        var output = $"__QBSYNC_MAP__|{Identity(2).Hash}|OK\n__QBSYNC_MAP__|{Identity(0).Hash}|MISSING_INFO\n__QBSYNC_MAP__|{Identity(1).Hash}|OK\n__QBSYNC_MAP__|{Identity(1).Hash}|OK\n";
        var parsed = AdbQuestSyncTarget.ParseMapValidationOutput(maps, output);
        Assert.AreEqual(MapStructureValidationStatus.MissingInfoDat, parsed[0].Status);
        Assert.AreEqual(MapStructureValidationStatus.InvalidResponse, parsed[1].Status);
        Assert.AreEqual(MapStructureValidationStatus.Valid, parsed[2].Status);
        Assert.AreEqual(MapStructureValidationStatus.InvalidResponse, parsed[3].Status);
        Assert.IsTrue(AdbQuestSyncTarget.ParseMapValidationOutput(maps, output + "garbage\n").All(item => item.Status == MapStructureValidationStatus.InvalidResponse));
    }

    private static async Task<SyncResult> ExecuteAsync(int count, BatchTarget target, CancellationToken cancellationToken = default)
    {
        var plan = new SyncPlan(uniqueMapCount: count);
        for (var index = 0; index < count; index++) plan.Add(new(SyncOperationKind.UploadMap, $"Upload {index}", Identity(index)));
        var execution = new SyncExecutionPlan(plan, QuestScanBinding.Capture(Device.Serial, Scan), []);
        var executor = new SyncExecutor(new Scanner(), new Workspace(), new MapSources(), target, new Journal(), QuestBeatSaberPaths.Default, new Compatibility());
        return await executor.ExecuteAsync(execution, Device, cancellationToken);
    }

    private static BeatMapIdentity Identity(int index) => new(index.ToString("X40"));
    private static int Index(BeatMapIdentity identity) => Convert.ToInt32(identity.Hash[^8..], 16);

    private sealed class Scanner : IQuestBeatSaberScanner { public Task<QuestBeatSaberScanResult> ScanAsync(QuestDevice device, CancellationToken cancellationToken = default) => Task.FromResult(Scan); }
    private sealed class Workspace : IPlaylistExecutionWorkspace { public Task<PreparedPlaylistSource> PrepareAsync(Guid executionId, PlaylistSourceIdentity source, CancellationToken cancellationToken = default) => throw new NotSupportedException(); public Task CleanupAsync(Guid executionId, CancellationToken cancellationToken = default) => Task.CompletedTask; }
    private sealed class MapSources : ISyncMapSourceProvider { public Task<string?> GetCachedMapDirectoryAsync(BeatMapIdentity identity, CancellationToken cancellationToken = default) => Task.FromResult<string?>("C:/cache/" + identity.Hash); public Task<string> DownloadExactMapAsync(BeatMapIdentity identity, BeatSaverLookupResult exactLookup, CancellationToken cancellationToken = default) => throw new NotSupportedException(); }
    private sealed class Compatibility : ILocalMapCompatibilityInspector { public Task<MapCompatibilityResult> InspectAsync(string localMapDirectory, BeatSaberPackageVersion? target, CancellationToken cancellationToken = default) => Task.FromResult(new MapCompatibilityResult(MapCompatibilityStatus.Compatible, BeatMapFormatInfo.Unknown, target, "compatible")); }
    private sealed class Journal : ISyncExecutionJournal { public Task WriteAsync(SyncResult result, CancellationToken cancellationToken = default) => Task.CompletedTask; }

    private sealed class BatchTarget : IQuestSyncTarget
    {
        public List<string> Events { get; } = []; public List<int> PromotedIndexes { get; } = []; public HashSet<int> FinalIndexes { get; } = [];
        public int PushCount { get; private set; } public int PromotionCount { get; private set; } public int StagingValidationBatchCount { get; private set; }
        public int FinalValidationBatchCount { get; private set; } public int MaximumBatchSize { get; private set; } public int BeginCount { get; private set; }
        public int EndCount { get; private set; } public int CleanupCount { get; private set; } public int DestructiveFinalCommands { get; private set; } public int FinalOverwriteCount { get; private set; }
        public int? OmitValidationIndex { get; init; } public int? DuplicateValidationIndex { get; init; } public int? InvalidValidationIndex { get; init; }
        public int? FailedUploadIndex { get; init; } public int? FinalExistsOnPromotionIndex { get; init; } public int? AmbiguousPromotionIndex { get; init; }
        public int? InvalidFinalIndex { get; init; } public CancellationTokenSource? CancelAfterStagingValidation { get; init; }
        public int? CancelAfterPromotionIndex { get; init; } public CancellationTokenSource? PromotionCancellation { get; init; }
        public IReadOnlyList<string> DrainDiagnosticWarnings() => [];
        public Task<QuestWritePreparationResult> BeginWriteSessionAsync(QuestDevice device, Guid executionId, CancellationToken cancellationToken = default) { BeginCount++; return Task.FromResult(QuestWritePreparationResult.Ready(new(executionId, device.Serial, "lock"))); }
        public Task EndWriteSessionAsync(QuestDevice device, QuestWriteSession session, CancellationToken cancellationToken = default) { EndCount++; return Task.CompletedTask; }
        public Task<bool> DirectoryExistsAsync(QuestDevice device, string remotePath, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task CreateStagingDirectoryAsync(QuestDevice device, string stagingPath, CancellationToken cancellationToken = default) { Events.Add("stage:" + PathIndex(stagingPath)); return Task.CompletedTask; }
        public Task UploadMapDirectoryAsync(QuestDevice device, string localMapDirectory, string stagingPath, IReadOnlySet<string> excludedFileNames, CancellationToken cancellationToken = default) { var index = PathIndex(stagingPath); Events.Add("upload:" + index); PushCount++; if (index == FailedUploadIndex) throw new IOException("upload failure"); return Task.CompletedTask; }
        public Task<bool> VerifyStagedMapStructureAsync(QuestDevice device, string stagingPath, BeatMapIdentity expectedIdentity, CancellationToken cancellationToken = default) => throw new AssertFailedException("Executor must use batch validation.");
        public Task<bool> TryPromoteStagingAsync(QuestDevice device, string stagingPath, string finalPath, CancellationToken cancellationToken = default) => throw new AssertFailedException("Executor must use compact promotion.");
        public Task<IReadOnlyList<MapStructureValidationResult>> ValidateStagedMapsAsync(QuestDevice device, IReadOnlyList<MapWritePathBinding> maps, CancellationToken cancellationToken = default)
        {
            StagingValidationBatchCount++; MaximumBatchSize = Math.Max(MaximumBatchSize, maps.Count); Events.Add("validate:" + string.Join(',', maps.Select(map => Index(map.Identity))));
            var result = maps.Where(map => Index(map.Identity) != OmitValidationIndex).Select(map => new MapStructureValidationResult(map.Identity, Index(map.Identity) == InvalidValidationIndex ? MapStructureValidationStatus.MissingInfoDat : MapStructureValidationStatus.Valid)).ToList();
            if (DuplicateValidationIndex is int duplicate && maps.Any(map => Index(map.Identity) == duplicate)) result.Add(new(Identity(duplicate), MapStructureValidationStatus.Valid));
            CancelAfterStagingValidation?.Cancel(); return Task.FromResult<IReadOnlyList<MapStructureValidationResult>>(result);
        }
        public Task<MapPromotionResult> PromoteStagingCompactAsync(QuestDevice device, MapPromotionRequest map, CancellationToken cancellationToken = default)
        {
            var index = Index(map.Identity); Events.Add("promote:" + index); PromotionCount++;
            if (index == FinalExistsOnPromotionIndex) return Task.FromResult(new MapPromotionResult(map.Identity, MapPromotionStatus.FinalAlreadyExists));
            if (index == AmbiguousPromotionIndex) return Task.FromResult(new MapPromotionResult(map.Identity, MapPromotionStatus.Ambiguous));
            PromotedIndexes.Add(index); FinalIndexes.Add(index); if (index == CancelAfterPromotionIndex) PromotionCancellation?.Cancel(); return Task.FromResult(new MapPromotionResult(map.Identity, MapPromotionStatus.Promoted));
        }
        public Task<IReadOnlyList<MapStructureValidationResult>> VerifyFinalMapsAsync(QuestDevice device, IReadOnlyList<MapWritePathBinding> maps, CancellationToken cancellationToken = default)
        {
            FinalValidationBatchCount++; MaximumBatchSize = Math.Max(MaximumBatchSize, maps.Count); Events.Add("final:" + string.Join(',', maps.Select(map => Index(map.Identity))));
            return Task.FromResult<IReadOnlyList<MapStructureValidationResult>>(maps.Select(map => new MapStructureValidationResult(map.Identity, Index(map.Identity) == InvalidFinalIndex ? MapStructureValidationStatus.MissingInfoDat : MapStructureValidationStatus.Valid)).ToArray());
        }
        public Task AbandonStagingAsync(QuestDevice device, string stagingPath, CancellationToken cancellationToken = default) { CleanupCount++; return Task.CompletedTask; }
        public Task ImportPlaylistAsync(QuestDevice device, PreparedPlaylistSource source, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        private static int PathIndex(string path) => Convert.ToInt32(path.Split('/').Last().Split('-')[1][^8..], 16);
    }
}
