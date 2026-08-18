using System.Security.Cryptography;
using QuestBeatSync.Core.Models;
using QuestBeatSync.Core.Services;
using QuestBeatSync.Infrastructure.Abstractions;
using QuestBeatSync.Infrastructure.Execution;

namespace QuestBeatSync.Tests;

[TestClass]
public sealed class Phase6AExecutionContractTests
{
    private const string HashA = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string HashB = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
    private const string ShaA = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string ShaB = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
    private string _temporaryRoot = null!;

    [TestInitialize]
    public void Initialize()
    {
        _temporaryRoot = Path.Combine(Path.GetTempPath(), $"qbsync-phase6a-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temporaryRoot);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_temporaryRoot)) Directory.Delete(_temporaryRoot, recursive: true);
    }

    [TestMethod]
    public async Task WrongDeviceSerial_RefusesBeforeRescanOrWrite()
    {
        var fixture = CreateFixture(EmptyScan(), Plan(Operation(SyncOperationKind.UploadMap, HashA)));

        var result = await fixture.Executor.ExecuteAsync(fixture.Plan, Device("OTHER"));

        Assert.AreEqual(SyncRunStatus.Refused, result.Status);
        Assert.AreEqual(0, fixture.Scanner.CallCount);
        Assert.AreEqual(0, fixture.Target.MutationCount);
    }

    [TestMethod]
    public async Task ChangedQuestScan_RefusesAsStaleBeforeWrite()
    {
        var original = EmptyScan();
        var changed = ScanWithMap(HashB);
        var fixture = CreateFixture(original, Plan(Operation(SyncOperationKind.UploadMap, HashA)), currentScan: changed);

        var result = await fixture.Executor.ExecuteAsync(fixture.Plan, Device());

        Assert.AreEqual(SyncRunStatus.Refused, result.Status);
        StringAssert.Contains(result.Message, "Quest state changed");
        Assert.AreEqual(0, fixture.Target.MutationCount);
    }

    [TestMethod]
    public async Task ChangedPlaylistFile_RefusesAsStaleBeforeWrite()
    {
        var path = Path.Combine(_temporaryRoot, "source.bplist");
        await File.WriteAllTextAsync(path, "original");
        var source = new PlaylistSourceIdentity(Path.GetFullPath(path), await Sha256Async(path));
        var plan = Plan(new SyncOperation(SyncOperationKind.ImportPlaylist, "Import", PlaylistName: "Same", PlaylistSource: source));
        var fixture = CreateFixture(EmptyScan(), plan);
        await File.WriteAllTextAsync(path, "changed");

        var result = await fixture.Executor.ExecuteAsync(fixture.Plan, Device());

        Assert.AreEqual(SyncRunStatus.Refused, result.Status);
        StringAssert.Contains(result.Message, "Playlist source changed");
        Assert.AreEqual(0, fixture.Target.MutationCount);
    }

    [TestMethod]
    public async Task SourceChangesAfterSnapshot_ImportUsesApprovedSnapshotBytes()
    {
        var path = Path.Combine(_temporaryRoot, "approved.bplist");
        await File.WriteAllTextAsync(path, "approved-A");
        var source = new PlaylistSourceIdentity(path, await Sha256Async(path));
        var plan = Plan(new SyncOperation(SyncOperationKind.ImportPlaylist, "Import", PlaylistName: "Approved", PlaylistSource: source));
        var innerWorkspace = new LocalPlaylistExecutionWorkspace(Path.Combine(_temporaryRoot, "executions"));
        var workspace = new MutatingWorkspace(innerWorkspace, () => File.WriteAllText(path, "changed-B"));
        var fixture = CreateFixture(EmptyScan(), plan, workspace: workspace);

        var result = await fixture.Executor.ExecuteAsync(fixture.Plan, Device());

        Assert.AreEqual(SyncRunStatus.Completed, result.Status);
        Assert.AreEqual(SyncOperationStatus.Succeeded, result.Operations[0].Status);
        CollectionAssert.AreEqual(new[] { "approved-A" }, fixture.Target.ImportedPlaylistContents.ToArray());
        Assert.AreEqual("changed-B", await File.ReadAllTextAsync(path));
        Assert.IsNotNull(workspace.Prepared);
        Assert.IsFalse(File.Exists(workspace.Prepared.SnapshotPath));
    }

    [TestMethod]
    public async Task CancellationDuringSnapshotPreparation_CancelsBeforeQuestWriteAndCleansSnapshot()
    {
        var path = Path.Combine(_temporaryRoot, "cancel.bplist");
        await File.WriteAllTextAsync(path, "approved");
        var source = new PlaylistSourceIdentity(path, await Sha256Async(path));
        var plan = Plan(new SyncOperation(SyncOperationKind.ImportPlaylist, "Import", PlaylistName: "Cancel", PlaylistSource: source));
        using var cancellation = new CancellationTokenSource();
        var innerWorkspace = new LocalPlaylistExecutionWorkspace(Path.Combine(_temporaryRoot, "executions"));
        var workspace = new CancelingWorkspace(innerWorkspace, cancellation);
        var fixture = CreateFixture(EmptyScan(), plan, workspace: workspace);

        var result = await fixture.Executor.ExecuteAsync(fixture.Plan, Device(), cancellation.Token);

        Assert.AreEqual(SyncRunStatus.Canceled, result.Status);
        Assert.AreEqual(0, fixture.Target.MutationCount);
        Assert.IsNotNull(workspace.Prepared);
        Assert.IsFalse(File.Exists(workspace.Prepared.SnapshotPath));
    }

    [TestMethod]
    public void SamePathWithConflictingContentHashes_FailsExecutionPlanConstruction()
    {
        var first = new PlaylistSourceIdentity(Path.Combine(_temporaryRoot, "conflict.bplist"), ShaA);
        var conflicting = new PlaylistSourceIdentity(first.CanonicalPath, ShaB);
        var plan = Plan(new SyncOperation(SyncOperationKind.ImportPlaylist, "Import", PlaylistName: "Conflict", PlaylistSource: first));

        var exception = Assert.Throws<ArgumentException>(() =>
            new SyncExecutionPlan(plan, QuestScanBinding.Capture("SERIAL", EmptyScan()), [first, conflicting]));

        StringAssert.Contains(exception.Message, "conflicting content SHA256");
    }

    [TestMethod]
    public void DuplicateTitles_WithDifferentSourceFilesRemainDistinct()
    {
        var first = PlaylistWithSource("Same title", "C:/playlists/first.bplist", ShaA);
        var second = PlaylistWithSource("Same title", "C:/playlists/second.bplist", ShaB);

        var plan = SyncPlanner.Build([first, second], new QuestLibrary(), EmptyHashes(), EmptyAvailability());
        var imports = plan.Operations.Where(operation => operation.Kind == SyncOperationKind.ImportPlaylist).ToArray();

        Assert.AreEqual(2, imports.Length);
        Assert.AreNotEqual(imports[0].PlaylistSource, imports[1].PlaylistSource);
    }

    [TestMethod]
    public void SameCanonicalSourceFile_IsOnlyScheduledOnce()
    {
        var first = PlaylistWithSource("First title", "C:/playlists/same.bplist", ShaA);
        var duplicate = PlaylistWithSource("Renamed title", "C:/playlists/same.bplist", ShaA);

        var plan = SyncPlanner.Build([first, duplicate], new QuestLibrary(), EmptyHashes(), EmptyAvailability());

        Assert.AreEqual(1, plan.Count(SyncOperationKind.ImportPlaylist));
    }

    [TestMethod]
    public async Task ExistingFinalMapDirectory_IsPreservedWithoutStagingOrOverwrite()
    {
        var fixture = CreateFixture(EmptyScan(), Plan(Operation(SyncOperationKind.UploadMap, HashA)));
        fixture.Target.Directories.Add($"{QuestBeatSaberPaths.Default.CustomLevels}/{HashA}");

        var result = await fixture.Executor.ExecuteAsync(fixture.Plan, Device());

        Assert.AreEqual(SyncOperationStatus.Skipped, result.Operations[0].Status);
        Assert.AreEqual(0, fixture.Target.CreateStagingCount);
        Assert.AreEqual(0, fixture.Target.PromoteCount);
    }

    [TestMethod]
    public async Task FinalAppearingAfterStaging_IsPreservedWithoutPromotion()
    {
        var fixture = CreateFixture(EmptyScan(), Plan(Operation(SyncOperationKind.UploadMap, HashA)));
        fixture.Target.FinalAppearsOnSecondCheck = true;

        var result = await fixture.Executor.ExecuteAsync(fixture.Plan, Device());

        Assert.AreEqual(SyncOperationStatus.Skipped, result.Operations[0].Status);
        Assert.AreEqual(0, fixture.Target.PromoteCount);
    }

    [TestMethod]
    public async Task FailedStagedStructureVerification_NeverPromotesFinal()
    {
        var fixture = CreateFixture(EmptyScan(), Plan(Operation(SyncOperationKind.UploadMap, HashA)));
        fixture.Target.StructureVerified = false;

        var result = await fixture.Executor.ExecuteAsync(fixture.Plan, Device());

        Assert.AreEqual(SyncOperationStatus.Failed, result.Operations[0].Status);
        Assert.AreEqual(0, fixture.Target.PromoteCount);
    }

    [TestMethod]
    public async Task PromotionFailure_IsReportedWithoutRollbackOrDelete()
    {
        var fixture = CreateFixture(EmptyScan(), Plan(Operation(SyncOperationKind.UploadMap, HashA)));
        fixture.Target.FailPromotion = true;

        var result = await fixture.Executor.ExecuteAsync(fixture.Plan, Device());

        Assert.AreEqual(SyncRunStatus.CompletedWithFailures, result.Status);
        Assert.AreEqual(SyncOperationStatus.Failed, result.Operations[0].Status);
        Assert.AreEqual(0, fixture.Target.PromoteCount);
    }

    [TestMethod]
    public async Task ForceStopRefusal_PreventsAllQuestWrites()
    {
        var fixture = CreateFixture(EmptyScan(), Plan(Operation(SyncOperationKind.UploadMap, HashA)));
        fixture.Target.WritePreparation = QuestWritePreparationResult.Refused("force-stop failed");

        var result = await fixture.Executor.ExecuteAsync(fixture.Plan, Device());

        Assert.AreEqual(SyncRunStatus.Refused, result.Status);
        Assert.AreEqual(0, fixture.Target.MutationCount);
    }

    [TestMethod]
    public async Task DownloadsAreGroupedBeforeSingleWriteSession()
    {
        var plan = Plan(
            Operation(SyncOperationKind.DownloadMap, HashA),
            Operation(SyncOperationKind.UploadMap, HashA),
            Operation(SyncOperationKind.DownloadMap, HashB),
            Operation(SyncOperationKind.UploadMap, HashB));
        var lookups = new Dictionary<string, BeatSaverLookupResult>(StringComparer.OrdinalIgnoreCase)
        {
            [HashA] = ExactLookup(HashA),
            [HashB] = ExactLookup(HashB)
        };
        var fixture = CreateFixture(EmptyScan(), plan, lookups: lookups);

        var result = await fixture.Executor.ExecuteAsync(fixture.Plan, Device());

        Assert.AreEqual(SyncRunStatus.Completed, result.Status);
        CollectionAssert.AreEqual(
            new[] { $"download:{HashA}", $"download:{HashB}", "begin", $"upload:{HashA}", $"upload:{HashB}", "end" },
            fixture.Events.ToArray());
        Assert.AreEqual(1, fixture.Target.BeginCount);
        Assert.AreEqual(1, fixture.Target.EndCount);
    }

    [TestMethod]
    public async Task CancellationBeforeLaterGroupedDownload_CancelsEveryPendingOperation()
    {
        using var cancellation = new CancellationTokenSource();
        var plan = Plan(
            Operation(SyncOperationKind.DownloadMap, HashA),
            Operation(SyncOperationKind.UploadMap, HashA),
            Operation(SyncOperationKind.DownloadMap, HashB),
            Operation(SyncOperationKind.UploadMap, HashB));
        var fixture = CreateFixture(EmptyScan(), plan, lookups: new Dictionary<string, BeatSaverLookupResult>
        {
            [HashA] = ExactLookup(HashA), [HashB] = ExactLookup(HashB)
        });
        fixture.MapSources.CancelAfterDownloadUsing = cancellation;

        var result = await fixture.Executor.ExecuteAsync(fixture.Plan, Device(), cancellation.Token);

        CollectionAssert.AreEqual(
            new[] { SyncOperationStatus.Succeeded, SyncOperationStatus.Canceled, SyncOperationStatus.Canceled, SyncOperationStatus.Canceled },
            result.Operations.Select(operation => operation.Status).ToArray());
        Assert.IsFalse(result.Operations.Any(operation => operation.Status == SyncOperationStatus.Pending));
        Assert.AreEqual(0, fixture.Target.MutationCount);
    }

    [TestMethod]
    public async Task QuestChangeAfterWriterLock_RefusesAndReleasesWithoutContentWrites()
    {
        var stateA = EmptyScan();
        var fixture = CreateFixture(
            stateA,
            Plan(Operation(SyncOperationKind.UploadMap, HashA)),
            currentScan: stateA,
            writePhaseScan: stateA,
            lockedScan: ScanWithMap(HashB));
        fixture.Target.EndWarning = "lock release diagnostic";

        var result = await fixture.Executor.ExecuteAsync(fixture.Plan, Device());

        Assert.AreEqual(SyncRunStatus.Refused, result.Status);
        StringAssert.Contains(result.Message, "after writer lock acquisition");
        Assert.AreEqual(0, fixture.Target.MutationCount);
        Assert.AreEqual(1, fixture.Target.BeginCount);
        Assert.AreEqual(1, fixture.Target.EndCount);
        Assert.IsTrue(result.DiagnosticWarnings.Any(warning => warning.Contains("lock release diagnostic", StringComparison.Ordinal)));
        StringAssert.Contains(result.Message, "after writer lock acquisition");
        Assert.IsFalse(fixture.Target.Directories.Contains($"{QuestBeatSaberPaths.Default.CustomLevels}/{HashA}"));
    }

    [TestMethod]
    public async Task UnchangedQuestUnderWriterLock_ProceedsWithWrites()
    {
        var stateA = EmptyScan();
        var fixture = CreateFixture(
            stateA,
            Plan(Operation(SyncOperationKind.UploadMap, HashA)),
            currentScan: stateA,
            writePhaseScan: stateA,
            lockedScan: stateA);

        var result = await fixture.Executor.ExecuteAsync(fixture.Plan, Device());

        Assert.AreEqual(SyncRunStatus.Completed, result.Status);
        Assert.AreEqual(SyncOperationStatus.Succeeded, result.Operations[0].Status);
        Assert.AreEqual(1, fixture.Target.PromoteCount);
        Assert.AreEqual(1, fixture.Target.EndCount);
    }

    [TestMethod]
    public async Task QuestChangeAfterDownloadsRefusesWriteAndKeepsPreparedCache()
    {
        var plan = Plan(
            Operation(SyncOperationKind.DownloadMap, HashA),
            Operation(SyncOperationKind.UploadMap, HashA));
        var fixture = CreateFixture(
            EmptyScan(),
            plan,
            writePhaseScan: ScanWithMap(HashB),
            lookups: new Dictionary<string, BeatSaverLookupResult> { [HashA] = ExactLookup(HashA) });

        var result = await fixture.Executor.ExecuteAsync(fixture.Plan, Device());

        Assert.AreEqual(SyncRunStatus.Refused, result.Status);
        Assert.AreEqual(SyncOperationStatus.Succeeded, result.Operations[0].Status);
        Assert.AreEqual(SyncOperationStatus.Skipped, result.Operations[1].Status);
        Assert.AreEqual(1, fixture.MapSources.DownloadCount);
        Assert.AreEqual(0, fixture.Target.BeginCount);
        Assert.AreEqual(0, fixture.Target.MutationCount);
    }

    [TestMethod]
    public async Task WriterLockCleanupFailureIsWarningAndDoesNotRewriteSuccess()
    {
        var fixture = CreateFixture(EmptyScan(), Plan(Operation(SyncOperationKind.UploadMap, HashA)));
        fixture.Target.EndWarning = "writer lock cleanup failed";

        var result = await fixture.Executor.ExecuteAsync(fixture.Plan, Device());

        Assert.AreEqual(SyncRunStatus.Completed, result.Status);
        Assert.AreEqual(SyncOperationStatus.Succeeded, result.Operations[0].Status);
        Assert.IsTrue(result.DiagnosticWarnings.Any(warning => warning.Contains("writer lock", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task CanceledStagingUpload_NeverPromotesFinalDirectory()
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = CreateFixture(EmptyScan(), Plan(Operation(SyncOperationKind.UploadMap, HashA)));
        fixture.Target.CancelUploadUsing = cancellation;

        var result = await fixture.Executor.ExecuteAsync(fixture.Plan, Device(), cancellation.Token);

        Assert.AreEqual(SyncRunStatus.Canceled, result.Status);
        Assert.AreEqual(0, fixture.Target.PromoteCount);
        Assert.IsFalse(fixture.Target.Directories.Contains($"{QuestBeatSaberPaths.Default.CustomLevels}/{HashA}"));
        Assert.IsFalse(fixture.Target.Directories.Any(path => path.Contains("/.qbsync-", StringComparison.Ordinal)));
        CollectionAssert.Contains(fixture.Target.LastExcludedFiles.ToArray(), ".qbsync-complete");
    }

    [TestMethod]
    public async Task ExactHashMismatch_BlocksDownloadAndFollowingUpload()
    {
        var invalidLookups = new[]
        {
            new BeatSaverLookupResult(BeatSaverAvailability.Online, HashA, null, HashA, null, new Uri("https://example.test/map.zip"), false),
            new BeatSaverLookupResult(BeatSaverAvailability.Online, HashB, null, HashA, null, new Uri("https://example.test/map.zip"), true),
            new BeatSaverLookupResult(BeatSaverAvailability.Online, HashA, null, HashB, null, new Uri("https://example.test/map.zip"), true)
        };

        foreach (var mismatch in invalidLookups)
        {
            var plan = Plan(
                Operation(SyncOperationKind.DownloadMap, HashA),
                Operation(SyncOperationKind.UploadMap, HashA));
            var fixture = CreateFixture(EmptyScan(), plan, lookups: new Dictionary<string, BeatSaverLookupResult> { [HashA] = mismatch });

            var result = await fixture.Executor.ExecuteAsync(fixture.Plan, Device());

            Assert.AreEqual(SyncOperationStatus.Failed, result.Operations[0].Status);
            Assert.AreEqual(SyncOperationStatus.Skipped, result.Operations[1].Status);
            Assert.AreEqual(0, fixture.MapSources.DownloadCount);
            Assert.AreEqual(0, fixture.Target.MutationCount);
        }
    }

    [TestMethod]
    public async Task IndependentMapFailure_DoesNotRollbackOrBlockOtherMap()
    {
        var fixture = CreateFixture(EmptyScan(), Plan(
            Operation(SyncOperationKind.UploadMap, HashA),
            Operation(SyncOperationKind.UploadMap, HashB)));
        fixture.Target.FailUploadContaining = HashA;

        var result = await fixture.Executor.ExecuteAsync(fixture.Plan, Device());

        Assert.AreEqual(SyncRunStatus.CompletedWithFailures, result.Status);
        Assert.AreEqual(SyncOperationStatus.Failed, result.Operations[0].Status);
        Assert.AreEqual(SyncOperationStatus.Succeeded, result.Operations[1].Status);
        Assert.IsTrue(fixture.Target.Directories.Contains($"{QuestBeatSaberPaths.Default.CustomLevels}/{HashB}"));
        CollectionAssert.AreEqual(
            new[] { SyncOperationStatus.Failed, SyncOperationStatus.Succeeded },
            fixture.Journal.Entries[^1].Operations.Select(operation => operation.Status).ToArray());
    }

    [TestMethod]
    public async Task PlaylistTransferFailure_DoesNotRewriteSuccessfulMapResult()
    {
        var path = Path.Combine(_temporaryRoot, "playlist-failure.bplist");
        await File.WriteAllTextAsync(path, "approved");
        var source = new PlaylistSourceIdentity(path, await Sha256Async(path));
        var fixture = CreateFixture(EmptyScan(), Plan(
            Operation(SyncOperationKind.UploadMap, HashA),
            new SyncOperation(SyncOperationKind.ImportPlaylist, "Import", PlaylistName: "Playlist", PlaylistSource: source)));
        fixture.Target.FailPlaylistImport = true;

        var result = await fixture.Executor.ExecuteAsync(fixture.Plan, Device());

        Assert.AreEqual(SyncRunStatus.CompletedWithFailures, result.Status);
        Assert.AreEqual(SyncOperationStatus.Succeeded, result.Operations[0].Status);
        Assert.AreEqual(SyncOperationStatus.Failed, result.Operations[1].Status);
        Assert.IsTrue(fixture.Target.Directories.Contains($"{QuestBeatSaberPaths.Default.CustomLevels}/{HashA}"));
    }

    [TestMethod]
    public async Task CancelAfterCompletedMap_PreservesSucceededResultWithoutRollback()
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = CreateFixture(EmptyScan(), Plan(
            Operation(SyncOperationKind.UploadMap, HashA),
            Operation(SyncOperationKind.UploadMap, HashB)));
        fixture.Target.CancelUploadUsing = cancellation;
        fixture.Target.CancelUploadContaining = HashB;

        var result = await fixture.Executor.ExecuteAsync(fixture.Plan, Device(), cancellation.Token);

        Assert.AreEqual(SyncRunStatus.Canceled, result.Status);
        Assert.AreEqual(SyncOperationStatus.Succeeded, result.Operations[0].Status);
        Assert.AreEqual(SyncOperationStatus.Canceled, result.Operations[1].Status);
        Assert.IsTrue(fixture.Target.Directories.Contains($"{QuestBeatSaberPaths.Default.CustomLevels}/{HashA}"));
        Assert.IsFalse(fixture.Target.Directories.Contains($"{QuestBeatSaberPaths.Default.CustomLevels}/{HashB}"));
        Assert.AreEqual(1, fixture.Target.EndCount);
    }

    [TestMethod]
    public async Task InitialJournalFailure_DoesNotPreventExecution()
    {
        var journal = new SelectiveFailJournal(1);
        var fixture = CreateFixture(EmptyScan(), Plan(Operation(SyncOperationKind.UploadMap, HashA)), journal: journal);

        var result = await fixture.Executor.ExecuteAsync(fixture.Plan, Device());

        Assert.AreEqual(SyncRunStatus.Completed, result.Status);
        Assert.AreEqual(SyncOperationStatus.Succeeded, result.Operations[0].Status);
        Assert.IsTrue(result.DiagnosticWarnings.Any(warning => warning.Contains("journal", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task JournalFailureAfterSuccessfulUpload_PreservesTruthAndContinues()
    {
        var journal = new SelectiveFailJournal(3);
        var fixture = CreateFixture(EmptyScan(), Plan(
            Operation(SyncOperationKind.UploadMap, HashA),
            Operation(SyncOperationKind.UploadMap, HashB)), journal: journal);

        var result = await fixture.Executor.ExecuteAsync(fixture.Plan, Device());

        Assert.AreEqual(SyncRunStatus.Completed, result.Status);
        CollectionAssert.AreEqual(
            new[] { SyncOperationStatus.Succeeded, SyncOperationStatus.Succeeded },
            result.Operations.Select(operation => operation.Status).ToArray());
        Assert.AreEqual(2, fixture.Target.PromoteCount);
        Assert.IsNotEmpty(result.DiagnosticWarnings);
    }

    [TestMethod]
    public async Task FinalJournalFailure_DoesNotChangeSuccessfulResult()
    {
        var journal = new SelectiveFailJournal(4);
        var fixture = CreateFixture(EmptyScan(), Plan(Operation(SyncOperationKind.UploadMap, HashA)), journal: journal);

        var result = await fixture.Executor.ExecuteAsync(fixture.Plan, Device());

        Assert.AreEqual(SyncRunStatus.Completed, result.Status);
        Assert.AreEqual(SyncOperationStatus.Succeeded, result.Operations[0].Status);
        Assert.IsNotEmpty(result.DiagnosticWarnings);
    }

    [TestMethod]
    public async Task SnapshotCleanupFailure_IsDiagnosticOnly()
    {
        var path = Path.Combine(_temporaryRoot, "cleanup-warning.bplist");
        await File.WriteAllTextAsync(path, "approved");
        var source = new PlaylistSourceIdentity(path, await Sha256Async(path));
        var plan = Plan(new SyncOperation(SyncOperationKind.ImportPlaylist, "Import", PlaylistName: "Cleanup", PlaylistSource: source));
        var workspace = new CleanupFailingWorkspace(
            new LocalPlaylistExecutionWorkspace(Path.Combine(_temporaryRoot, "executions")));
        var fixture = CreateFixture(EmptyScan(), plan, workspace: workspace);

        var result = await fixture.Executor.ExecuteAsync(fixture.Plan, Device());

        Assert.AreEqual(SyncRunStatus.Completed, result.Status);
        Assert.AreEqual(SyncOperationStatus.Succeeded, result.Operations[0].Status);
        Assert.IsTrue(result.DiagnosticWarnings.Any(warning => warning.Contains("snapshots", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void ViewModelsContainNoDirectPushInvocation()
    {
        var root = FindRepositoryRoot();
        var viewModelRoot = Path.Combine(root, "src", "QuestBeatSync.App", "ViewModels");
        var offenders = Directory.EnumerateFiles(viewModelRoot, "*.cs")
            .Where(path => File.ReadAllText(path).Contains("PushAsync(", StringComparison.Ordinal))
            .ToArray();

        Assert.AreEqual(0, offenders.Length, string.Join(Environment.NewLine, offenders));
    }

    [TestMethod]
    public void PushAsync_HasNoWorkflowCallerInProductionCode()
    {
        var root = FindRepositoryRoot();
        var sourceRoot = Path.Combine(root, "src");
        var allowedDeclarations = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "IQuestTransport.cs",
            "AdbQuestTransport.cs",
            "FakeQuestTransport.cs",
            "AdbQuestSyncTarget.cs"
        };
        var offenders = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains("PushAsync(", StringComparison.Ordinal))
            .Where(path => !allowedDeclarations.Contains(Path.GetFileName(path)))
            .ToArray();

        Assert.AreEqual(0, offenders.Length, string.Join(Environment.NewLine, offenders));
    }

    [TestMethod]
    public void SyncOperationKinds_HaveNoDeleteMap()
    {
        CollectionAssert.DoesNotContain(Enum.GetNames<SyncOperationKind>(), "DeleteMap");
    }

    [TestMethod]
    public async Task JsonJournal_WritesDiagnosticSnapshotWithoutResumeApi()
    {
        var journal = new JsonSyncExecutionJournal(_temporaryRoot);
        var result = new SyncResult(Guid.NewGuid(), SyncRunStatus.Completed, "SERIAL", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, [], [], null);

        await journal.WriteAsync(result);

        var file = Directory.GetFiles(_temporaryRoot, "*.json").Single();
        StringAssert.Contains(await File.ReadAllTextAsync(file), result.ExecutionId.ToString());
        Assert.IsFalse(typeof(ISyncExecutionJournal).GetMethods().Any(method => method.Name.Contains("Read", StringComparison.OrdinalIgnoreCase) || method.Name.Contains("Resume", StringComparison.OrdinalIgnoreCase)));
    }

    private ExecutionFixture CreateFixture(
        QuestBeatSaberScanResult boundScan,
        SyncPlan plan,
        QuestBeatSaberScanResult? currentScan = null,
        IPlaylistExecutionWorkspace? workspace = null,
        ISyncExecutionJournal? journal = null,
        QuestBeatSaberScanResult? writePhaseScan = null,
        QuestBeatSaberScanResult? lockedScan = null,
        IReadOnlyDictionary<string, BeatSaverLookupResult>? lookups = null)
    {
        var events = new List<string>();
        var firstScan = currentScan ?? boundScan;
        var secondScan = writePhaseScan ?? firstScan;
        var scanner = new StubScanner(firstScan, secondScan, lockedScan ?? secondScan);
        var target = new RecordingTarget(events);
        var mapSources = new RecordingMapSources(events);
        var recordingJournal = journal as MemoryJournal ?? new MemoryJournal();
        var sources = plan.Operations
            .Where(operation => operation.PlaylistSource is not null)
            .Select(operation => operation.PlaylistSource!)
            .ToArray();
        var executionPlan = new SyncExecutionPlan(plan, QuestScanBinding.Capture("SERIAL", boundScan), sources, lookups);
        var executor = new SyncExecutor(
            scanner,
            workspace ?? new LocalPlaylistExecutionWorkspace(Path.Combine(_temporaryRoot, "executions")),
            mapSources,
            target,
            journal ?? recordingJournal,
            QuestBeatSaberPaths.Default);
        return new ExecutionFixture(executor, executionPlan, scanner, target, mapSources, recordingJournal, events);
    }

    private static SyncPlan Plan(params SyncOperation[] operations)
    {
        var plan = new SyncPlan();
        foreach (var operation in operations) plan.Add(operation);
        return plan;
    }

    private static SyncOperation Operation(SyncOperationKind kind, string hash) =>
        new(kind, kind.ToString(), new BeatMapIdentity(hash));

    private static BeatSaverLookupResult ExactLookup(string hash) =>
        new(BeatSaverAvailability.Online, hash, null, hash, null, new Uri("https://example.test/map.zip"), true);

    private static Playlist PlaylistWithSource(string title, string path, string sha)
    {
        var playlist = new Playlist(title, sourcePath: path, sourceContentSha256: sha);
        playlist.Add(new PlaylistEntry(null, HashA, "Map"));
        return playlist;
    }

    private static QuestDevice Device(string serial = "SERIAL") =>
        new(serial, QuestConnectionState.Device, QuestTransportKind.Usb);

    private static QuestBeatSaberScanResult EmptyScan() => new(true, true, true, true, true);

    private static QuestBeatSaberScanResult ScanWithMap(string hash) =>
        new(true, true, true, true, true,
            [new($"/maps/{hash}", hash, true, "Map", "Mapper", QuestMapIdentityStatus.HashIdentified, new BeatMapIdentity(hash))]);

    private static IReadOnlySet<string> EmptyHashes() => new HashSet<string>();
    private static IReadOnlyDictionary<string, BeatSaverAvailability> EmptyAvailability() => new Dictionary<string, BeatSaverAvailability>();

    private static async Task<string> Sha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "QuestBeatSync.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }

    private sealed record ExecutionFixture(
        SyncExecutor Executor,
        SyncExecutionPlan Plan,
        StubScanner Scanner,
        RecordingTarget Target,
        RecordingMapSources MapSources,
        MemoryJournal Journal,
        List<string> Events);

    private sealed class StubScanner(params QuestBeatSaberScanResult[] results) : IQuestBeatSaberScanner
    {
        public int CallCount { get; private set; }
        public Task<QuestBeatSaberScanResult> ScanAsync(QuestDevice device, CancellationToken cancellationToken = default)
        {
            var result = results[Math.Min(CallCount, results.Length - 1)];
            CallCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingMapSources(List<string> events) : ISyncMapSourceProvider
    {
        public int DownloadCount { get; private set; }
        public CancellationTokenSource? CancelAfterDownloadUsing { get; set; }
        public Task<string?> GetCachedMapDirectoryAsync(BeatMapIdentity identity, CancellationToken cancellationToken = default) => Task.FromResult<string?>($"C:/cache/{identity.Hash}");
        public Task<string> DownloadExactMapAsync(BeatMapIdentity identity, BeatSaverLookupResult exactLookup, CancellationToken cancellationToken = default)
        {
            DownloadCount++;
            events.Add($"download:{identity.Hash}");
            CancelAfterDownloadUsing?.Cancel();
            return Task.FromResult($"C:/cache/{identity.Hash}");
        }
    }

    private sealed class RecordingTarget(List<string> events) : IQuestSyncTarget
    {
        public HashSet<string> Directories { get; } = new(StringComparer.Ordinal);
        public int CreateStagingCount { get; private set; }
        public int PromoteCount { get; private set; }
        public int ImportCount { get; private set; }
        public int MutationCount => CreateStagingCount + PromoteCount + ImportCount;
        public CancellationTokenSource? CancelUploadUsing { get; set; }
        public string? CancelUploadContaining { get; set; }
        public string? FailUploadContaining { get; set; }
        public bool FailPlaylistImport { get; set; }
        public IReadOnlySet<string> LastExcludedFiles { get; private set; } = new HashSet<string>();
        public List<string> ImportedPlaylistContents { get; } = [];
        public QuestWritePreparationResult? WritePreparation { get; set; }
        public bool FinalAppearsOnSecondCheck { get; set; }
        public bool StructureVerified { get; set; } = true;
        public bool FailPromotion { get; set; }
        public int BeginCount { get; private set; }
        public int EndCount { get; private set; }
        public string? EndWarning { get; set; }
        private readonly Queue<string> _warnings = new();
        private int _finalDirectoryChecks;

        public IReadOnlyList<string> DrainDiagnosticWarnings()
        {
            var warnings = _warnings.ToArray();
            _warnings.Clear();
            return warnings;
        }

        public Task<QuestWritePreparationResult> BeginWriteSessionAsync(QuestDevice device, Guid executionId, CancellationToken cancellationToken = default)
        {
            BeginCount++;
            events.Add("begin");
            return Task.FromResult(WritePreparation ?? QuestWritePreparationResult.Ready(
                new QuestWriteSession(executionId, device.Serial, QuestExecutionPaths.WriterLock(QuestBeatSaberPaths.Default))));
        }

        public Task EndWriteSessionAsync(QuestDevice device, QuestWriteSession session, CancellationToken cancellationToken = default)
        {
            EndCount++;
            events.Add("end");
            if (EndWarning is not null) _warnings.Enqueue(EndWarning);
            return Task.CompletedTask;
        }

        public Task<bool> DirectoryExistsAsync(QuestDevice device, string remotePath, CancellationToken cancellationToken = default)
        {
            var isFinal = BeatSaverHash.IsValid(remotePath[(remotePath.LastIndexOf('/') + 1)..]);
            if (FinalAppearsOnSecondCheck && isFinal && ++_finalDirectoryChecks >= 2) return Task.FromResult(true);
            return Task.FromResult(Directories.Contains(remotePath));
        }
        public Task CreateStagingDirectoryAsync(QuestDevice device, string stagingPath, CancellationToken cancellationToken = default) { CreateStagingCount++; Directories.Add(stagingPath); return Task.CompletedTask; }
        public Task UploadMapDirectoryAsync(QuestDevice device, string localMapDirectory, string stagingPath, IReadOnlySet<string> excludedFileNames, CancellationToken cancellationToken = default)
        {
            var stagingIdentity = QuestExecutionPaths.TryParseOwnedMapStagingPath(QuestBeatSaberPaths.Default, stagingPath, out var identity, out _) ? identity : null;
            events.Add($"upload:{stagingIdentity?.Hash}");
            LastExcludedFiles = excludedFileNames;
            if (FailUploadContaining is not null && stagingPath.Contains(FailUploadContaining, StringComparison.Ordinal)) throw new IOException("Fixture upload failure.");
            if (CancelUploadUsing is not null && (CancelUploadContaining is null || stagingPath.Contains(CancelUploadContaining, StringComparison.Ordinal))) { CancelUploadUsing.Cancel(); throw new OperationCanceledException(cancellationToken); }
            return Task.CompletedTask;
        }
        public Task<bool> VerifyStagedMapStructureAsync(QuestDevice device, string stagingPath, BeatMapIdentity expectedIdentity, CancellationToken cancellationToken = default) => Task.FromResult(StructureVerified);
        public Task<bool> TryPromoteStagingAsync(QuestDevice device, string stagingPath, string finalPath, CancellationToken cancellationToken = default)
        {
            if (FailPromotion) throw new IOException("Fixture promotion failure.");
            if (Directories.Contains(finalPath)) return Task.FromResult(false);
            PromoteCount++;
            Directories.Remove(stagingPath);
            Directories.Add(finalPath);
            return Task.FromResult(true);
        }
        public Task AbandonStagingAsync(QuestDevice device, string stagingPath, CancellationToken cancellationToken = default) { Directories.Remove(stagingPath); return Task.CompletedTask; }
        public async Task ImportPlaylistAsync(QuestDevice device, PreparedPlaylistSource source, CancellationToken cancellationToken = default)
        {
            if (FailPlaylistImport) throw new IOException("Fixture playlist transfer failure.");
            ImportCount++;
            ImportedPlaylistContents.Add(await File.ReadAllTextAsync(source.SnapshotPath, cancellationToken));
        }
    }

    private sealed class MemoryJournal : ISyncExecutionJournal
    {
        public List<SyncResult> Entries { get; } = [];
        public Task WriteAsync(SyncResult result, CancellationToken cancellationToken = default) { Entries.Add(result); return Task.CompletedTask; }
    }

    private sealed class SelectiveFailJournal(params int[] failedCalls) : ISyncExecutionJournal
    {
        private readonly HashSet<int> _failedCalls = [.. failedCalls];
        private int _callCount;

        public Task WriteAsync(SyncResult result, CancellationToken cancellationToken = default)
        {
            if (_failedCalls.Contains(++_callCount)) throw new IOException($"Journal failure {_callCount}.");
            return Task.CompletedTask;
        }
    }

    private sealed class MutatingWorkspace(
        IPlaylistExecutionWorkspace inner,
        Action afterPreparation) : IPlaylistExecutionWorkspace
    {
        public PreparedPlaylistSource? Prepared { get; private set; }

        public async Task<PreparedPlaylistSource> PrepareAsync(Guid executionId, PlaylistSourceIdentity source, CancellationToken cancellationToken = default)
        {
            Prepared = await inner.PrepareAsync(executionId, source, cancellationToken);
            afterPreparation();
            return Prepared;
        }

        public Task CleanupAsync(Guid executionId, CancellationToken cancellationToken = default) =>
            inner.CleanupAsync(executionId, cancellationToken);
    }

    private sealed class CancelingWorkspace(
        IPlaylistExecutionWorkspace inner,
        CancellationTokenSource cancellation) : IPlaylistExecutionWorkspace
    {
        public PreparedPlaylistSource? Prepared { get; private set; }

        public async Task<PreparedPlaylistSource> PrepareAsync(Guid executionId, PlaylistSourceIdentity source, CancellationToken cancellationToken = default)
        {
            Prepared = await inner.PrepareAsync(executionId, source, cancellationToken);
            cancellation.Cancel();
            throw new OperationCanceledException(cancellationToken);
        }

        public Task CleanupAsync(Guid executionId, CancellationToken cancellationToken = default) =>
            inner.CleanupAsync(executionId, cancellationToken);
    }

    private sealed class CleanupFailingWorkspace(IPlaylistExecutionWorkspace inner) : IPlaylistExecutionWorkspace
    {
        public Task<PreparedPlaylistSource> PrepareAsync(Guid executionId, PlaylistSourceIdentity source, CancellationToken cancellationToken = default) =>
            inner.PrepareAsync(executionId, source, cancellationToken);

        public Task CleanupAsync(Guid executionId, CancellationToken cancellationToken = default) =>
            throw new IOException("Fixture cleanup failure.");
    }
}
