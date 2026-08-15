using System.Security.Cryptography;
using QuestBeatSync.App.ViewModels;
using QuestBeatSync.Core.Models;
using QuestBeatSync.Infrastructure.Abstractions;
using QuestBeatSync.Infrastructure.Execution;
using QuestBeatSync.Infrastructure.Importing;

namespace QuestBeatSync.Tests;

[TestClass]
public sealed class SyncExecutionViewModelTests
{
    private const string Hash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static readonly QuestDevice Device = new("QUEST", QuestConnectionState.Device, QuestTransportKind.Usb);
    private string _temporaryRoot = null!;

    [TestInitialize]
    public void Initialize()
    {
        _temporaryRoot = Path.Combine(Path.GetTempPath(), $"qbsync-vm-execution-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temporaryRoot);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_temporaryRoot)) Directory.Delete(_temporaryRoot, recursive: true);
    }

    [TestMethod]
    public async Task BuildAndReview_DoNotWrite_OnlyExplicitConfirmExecutesBoundPlan()
    {
        var fixture = await CreateFixtureAsync();

        await fixture.Sync.BuildCommand.ExecuteAsync();
        Assert.AreEqual(0, fixture.Target.MutationCount);
        Assert.IsNotNull(fixture.Sync.ExecutionPlan);

        fixture.Sync.ReviewExecutionCommand.Execute(null);
        Assert.IsTrue(fixture.Sync.IsConfirmationVisible);
        Assert.AreEqual(0, fixture.Target.MutationCount);

        await fixture.Sync.ConfirmExecutionCommand.ExecuteAsync();

        Assert.AreEqual(1, fixture.Target.PrepareCount);
        Assert.AreEqual(1, fixture.Target.PromoteCount);
        Assert.AreEqual(1, fixture.Target.PlaylistCount);
        Assert.IsNotNull(fixture.Sync.LastResult);
        Assert.AreEqual(SyncRunStatus.Completed, fixture.Sync.LastResult.Status);
        Assert.AreEqual(0, fixture.Sync.DeletionCount);
    }

    [TestMethod]
    public async Task Confirmation_IsInvalidatedWhenQuestStateChangesBeforeExecution()
    {
        var fixture = await CreateFixtureAsync();
        await fixture.Sync.BuildCommand.ExecuteAsync();
        fixture.Sync.ReviewExecutionCommand.Execute(null);
        Assert.IsTrue(fixture.Sync.IsConfirmationVisible);

        fixture.Library.Apply(ScanWithMap(Hash), scanCompleted: true, deviceSerial: Device.Serial);

        Assert.IsFalse(fixture.Sync.IsConfirmationVisible);
        Assert.IsNull(fixture.Sync.ExecutionPlan);
        Assert.IsFalse(fixture.Sync.ConfirmExecutionCommand.CanExecute(null));
        await fixture.Sync.ConfirmExecutionCommand.ExecuteAsync();
        Assert.AreEqual(0, fixture.Target.MutationCount);
    }

    private async Task<Fixture> CreateFixtureAsync()
    {
        var sourcePath = Path.Combine(_temporaryRoot, "playlist.bplist");
        await File.WriteAllTextAsync(sourcePath, "approved playlist bytes");
        var sourceHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sourcePath)));
        var playlist = new Playlist("Canary", sourcePath: sourcePath, sourceContentSha256: sourceHash);
        playlist.Add(new PlaylistEntry(null, Hash, "Canary map"));
        var library = new LibraryViewModel();
        library.Apply(EmptyScan(), scanCompleted: true, deviceSerial: Device.Serial);
        var cache = new CachedMapProvider(Path.Combine(_temporaryRoot, "map"));
        var playlists = new PlaylistsViewModel(
            new StubImporter(playlist),
            new NeverBeatSaverClient(),
            cache,
            library,
            _ => Task.CompletedTask);
        await playlists.ImportAsync([sourcePath]);
        var target = new RecordingTarget();
        var executor = new SyncExecutor(
            new StubScanner(EmptyScan()),
            new LocalPlaylistExecutionWorkspace(Path.Combine(_temporaryRoot, "executions")),
            cache,
            target,
            new MemoryJournal(),
            QuestBeatSaberPaths.Default);
        var sync = new SyncViewModel(
            playlists,
            library,
            new NeverBeatSaverClient(),
            cache,
            _ => Task.CompletedTask,
            executor,
            () => Device,
            () =>
            {
                library.Apply(ScanWithMap(Hash), scanCompleted: true, deviceSerial: Device.Serial);
                return Task.CompletedTask;
            });
        return new Fixture(sync, library, target);
    }

    private static QuestBeatSaberScanResult EmptyScan() => new(true, true, true, true, true);

    private static QuestBeatSaberScanResult ScanWithMap(string hash) =>
        new(true, true, true, true, true,
            [new($"/maps/{hash}", hash, true, "Map", "Mapper", QuestMapIdentityStatus.HashIdentified, new BeatMapIdentity(hash))]);

    private sealed record Fixture(SyncViewModel Sync, LibraryViewModel Library, RecordingTarget Target);

    private sealed class StubImporter(Playlist playlist) : ILocalPlaylistImporter
    {
        public Task<IReadOnlyList<PlaylistImportResult>> ImportAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlaylistImportResult>>([new(playlist.SourcePath!, playlist, null)]);
    }

    private sealed class NeverBeatSaverClient : IBeatSaverClient
    {
        public Task<BeatSaverLookupResult> LookupAsync(BeatSaverLookupRequest request, CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("Cached maps must not require a BeatSaver lookup.");
        public Task DownloadZipAsync(Uri downloadUri, Stream destination, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class CachedMapProvider : IBeatMapCache, ISyncMapSourceProvider
    {
        private readonly string _path;
        public CachedMapProvider(string path) { _path = path; Directory.CreateDirectory(path); }
        public Task<bool> IsCachedAsync(string hash, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<BeatMapCacheResult> CacheAsync(BeatSaverLookupResult lookup, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string?> GetCachedMapDirectoryAsync(BeatMapIdentity identity, CancellationToken cancellationToken = default) => Task.FromResult<string?>(_path);
        public Task<string> DownloadExactMapAsync(BeatMapIdentity identity, BeatSaverLookupResult exactLookup, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class StubScanner(QuestBeatSaberScanResult result) : IQuestBeatSaberScanner
    {
        public Task<QuestBeatSaberScanResult> ScanAsync(QuestDevice device, CancellationToken cancellationToken = default) => Task.FromResult(result);
    }

    private sealed class RecordingTarget : IQuestSyncTarget
    {
        private readonly HashSet<string> _directories = new(StringComparer.Ordinal);
        public int PrepareCount { get; private set; }
        public int StagingCount { get; private set; }
        public int PromoteCount { get; private set; }
        public int PlaylistCount { get; private set; }
        public int MutationCount => StagingCount + PromoteCount + PlaylistCount;
        public IReadOnlyList<string> DrainDiagnosticWarnings() => [];
        public Task<QuestWritePreparationResult> BeginWriteSessionAsync(QuestDevice device, Guid executionId, CancellationToken cancellationToken = default) { PrepareCount++; return Task.FromResult(QuestWritePreparationResult.Ready(new QuestWriteSession(executionId, device.Serial, QuestExecutionPaths.WriterLock(QuestBeatSaberPaths.Default)))); }
        public Task EndWriteSessionAsync(QuestDevice device, QuestWriteSession session, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> DirectoryExistsAsync(QuestDevice device, string remotePath, CancellationToken cancellationToken = default) => Task.FromResult(_directories.Contains(remotePath));
        public Task CreateStagingDirectoryAsync(QuestDevice device, string stagingPath, CancellationToken cancellationToken = default) { StagingCount++; _directories.Add(stagingPath); return Task.CompletedTask; }
        public Task UploadMapDirectoryAsync(QuestDevice device, string localMapDirectory, string stagingPath, IReadOnlySet<string> excludedFileNames, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> VerifyStagedMapStructureAsync(QuestDevice device, string stagingPath, BeatMapIdentity expectedIdentity, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> TryPromoteStagingAsync(QuestDevice device, string stagingPath, string finalPath, CancellationToken cancellationToken = default) { PromoteCount++; _directories.Remove(stagingPath); _directories.Add(finalPath); return Task.FromResult(true); }
        public Task AbandonStagingAsync(QuestDevice device, string stagingPath, CancellationToken cancellationToken = default) { _directories.Remove(stagingPath); return Task.CompletedTask; }
        public Task ImportPlaylistAsync(QuestDevice device, PreparedPlaylistSource source, CancellationToken cancellationToken = default) { PlaylistCount++; Assert.IsTrue(File.Exists(source.SnapshotPath)); return Task.CompletedTask; }
    }

    private sealed class MemoryJournal : ISyncExecutionJournal
    {
        public Task WriteAsync(SyncResult result, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
