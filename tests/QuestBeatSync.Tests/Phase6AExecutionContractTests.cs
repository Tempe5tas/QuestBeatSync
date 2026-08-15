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
        var fixture = CreateFixture(EmptyScan(), plan, verifier: new LocalPlaylistSourceVerifier());
        await File.WriteAllTextAsync(path, "changed");

        var result = await fixture.Executor.ExecuteAsync(fixture.Plan, Device());

        Assert.AreEqual(SyncRunStatus.Refused, result.Status);
        StringAssert.Contains(result.Message, "Playlist source changed");
        Assert.AreEqual(0, fixture.Target.MutationCount);
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
            "FakeQuestTransport.cs"
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
            DateTimeOffset.UtcNow, [], null);

        await journal.WriteAsync(result);

        var file = Directory.GetFiles(_temporaryRoot, "*.json").Single();
        StringAssert.Contains(await File.ReadAllTextAsync(file), result.ExecutionId.ToString());
        Assert.IsFalse(typeof(ISyncExecutionJournal).GetMethods().Any(method => method.Name.Contains("Read", StringComparison.OrdinalIgnoreCase) || method.Name.Contains("Resume", StringComparison.OrdinalIgnoreCase)));
    }

    private static ExecutionFixture CreateFixture(
        QuestBeatSaberScanResult boundScan,
        SyncPlan plan,
        QuestBeatSaberScanResult? currentScan = null,
        IPlaylistSourceVerifier? verifier = null,
        IReadOnlyDictionary<string, BeatSaverLookupResult>? lookups = null)
    {
        var scanner = new StubScanner(currentScan ?? boundScan);
        var target = new RecordingTarget();
        var mapSources = new RecordingMapSources();
        var journal = new MemoryJournal();
        var sources = plan.Operations
            .Where(operation => operation.PlaylistSource is not null)
            .Select(operation => operation.PlaylistSource!)
            .ToArray();
        var executionPlan = new SyncExecutionPlan(plan, QuestScanBinding.Capture("SERIAL", boundScan), sources, lookups);
        var executor = new SyncExecutor(scanner, verifier ?? new AlwaysMatchingVerifier(), mapSources, target, journal, QuestBeatSaberPaths.Default);
        return new ExecutionFixture(executor, executionPlan, scanner, target, mapSources, journal);
    }

    private static SyncPlan Plan(params SyncOperation[] operations)
    {
        var plan = new SyncPlan();
        foreach (var operation in operations) plan.Add(operation);
        return plan;
    }

    private static SyncOperation Operation(SyncOperationKind kind, string hash) =>
        new(kind, kind.ToString(), new BeatMapIdentity(hash));

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
        MemoryJournal Journal);

    private sealed class StubScanner(QuestBeatSaberScanResult result) : IQuestBeatSaberScanner
    {
        public int CallCount { get; private set; }
        public Task<QuestBeatSaberScanResult> ScanAsync(QuestDevice device, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class AlwaysMatchingVerifier : IPlaylistSourceVerifier
    {
        public Task<bool> MatchesAsync(PlaylistSourceIdentity source, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class RecordingMapSources : ISyncMapSourceProvider
    {
        public int DownloadCount { get; private set; }
        public Task<string?> GetCachedMapDirectoryAsync(BeatMapIdentity identity, CancellationToken cancellationToken = default) => Task.FromResult<string?>($"C:/cache/{identity.Hash}");
        public Task<string> DownloadExactMapAsync(BeatMapIdentity identity, BeatSaverLookupResult exactLookup, CancellationToken cancellationToken = default)
        {
            DownloadCount++;
            return Task.FromResult($"C:/cache/{identity.Hash}");
        }
    }

    private sealed class RecordingTarget : IQuestSyncTarget
    {
        public HashSet<string> Directories { get; } = new(StringComparer.Ordinal);
        public int CreateStagingCount { get; private set; }
        public int PromoteCount { get; private set; }
        public int ImportCount { get; private set; }
        public int MutationCount => CreateStagingCount + PromoteCount + ImportCount;
        public CancellationTokenSource? CancelUploadUsing { get; set; }
        public string? FailUploadContaining { get; set; }
        public IReadOnlySet<string> LastExcludedFiles { get; private set; } = new HashSet<string>();

        public Task<bool> DirectoryExistsAsync(QuestDevice device, string remotePath, CancellationToken cancellationToken = default) => Task.FromResult(Directories.Contains(remotePath));
        public Task CreateStagingDirectoryAsync(QuestDevice device, string stagingPath, CancellationToken cancellationToken = default) { CreateStagingCount++; Directories.Add(stagingPath); return Task.CompletedTask; }
        public Task UploadMapDirectoryAsync(QuestDevice device, string localMapDirectory, string stagingPath, IReadOnlySet<string> excludedFileNames, CancellationToken cancellationToken = default)
        {
            LastExcludedFiles = excludedFileNames;
            if (FailUploadContaining is not null && stagingPath.Contains(FailUploadContaining, StringComparison.Ordinal)) throw new IOException("Fixture upload failure.");
            if (CancelUploadUsing is not null) { CancelUploadUsing.Cancel(); throw new OperationCanceledException(cancellationToken); }
            return Task.CompletedTask;
        }
        public Task<bool> VerifyStagedMapAsync(QuestDevice device, string stagingPath, BeatMapIdentity expectedIdentity, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> TryPromoteStagingAsync(QuestDevice device, string stagingPath, string finalPath, CancellationToken cancellationToken = default)
        {
            if (Directories.Contains(finalPath)) return Task.FromResult(false);
            PromoteCount++;
            Directories.Remove(stagingPath);
            Directories.Add(finalPath);
            return Task.FromResult(true);
        }
        public Task AbandonStagingAsync(QuestDevice device, string stagingPath, CancellationToken cancellationToken = default) { Directories.Remove(stagingPath); return Task.CompletedTask; }
        public Task ImportPlaylistAsync(QuestDevice device, PlaylistSourceIdentity source, CancellationToken cancellationToken = default) { ImportCount++; return Task.CompletedTask; }
    }

    private sealed class MemoryJournal : ISyncExecutionJournal
    {
        public List<SyncResult> Entries { get; } = [];
        public Task WriteAsync(SyncResult result, CancellationToken cancellationToken = default) { Entries.Add(result); return Task.CompletedTask; }
    }
}
