using QuestBeatSync.App.ViewModels;
using QuestBeatSync.Core.Models;
using QuestBeatSync.Infrastructure.Abstractions;
using QuestBeatSync.Infrastructure.Importing;

namespace QuestBeatSync.Tests;

[TestClass]
public sealed class WorkflowViewModelTests
{
    private const string Hash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [TestMethod]
    public async Task KeyOnlyDiagnosticResult_CannotEnterMapCache()
    {
        var playlist = new Playlist("Missing hash");
        playlist.Add(new PlaylistEntry("1a2b", null, "Informational only"));
        var cache = new RecordingCache();
        var viewModel = new PlaylistsViewModel(
            new StubImporter(playlist),
            new RecordingClient(),
            cache,
            new LibraryViewModel(),
            _ => Task.CompletedTask);

        await viewModel.ImportAsync(["missing.bplist"]);
        await viewModel.CheckSelectedCommand.ExecuteAsync();
        await viewModel.CacheSelectedCommand.ExecuteAsync();

        Assert.AreEqual(PlaylistEntryIdentityStatus.MissingHash, viewModel.SelectedEntries[0].IdentityStatus);
        Assert.AreEqual(BeatSaverAvailability.Online, viewModel.SelectedEntries[0].Availability);
        Assert.AreEqual(0, cache.CacheCallCount);
    }

    [TestMethod]
    public async Task SyncPreflight_ReusesExactKnownOnlineResult()
    {
        var playlist = new Playlist("Known");
        playlist.Add(new PlaylistEntry("1a2b", Hash, "Known"));
        var client = new RecordingClient();
        var library = new LibraryViewModel();
        library.Apply(QuestBeatSaberScanResult.Empty, scanCompleted: true);
        var playlists = new PlaylistsViewModel(new StubImporter(playlist), client, new RecordingCache(), library, _ => Task.CompletedTask);
        var sync = new SyncViewModel(playlists, library, client, new RecordingCache(), _ => Task.CompletedTask);
        await playlists.ImportAsync(["known.bplist"]);
        await playlists.CheckSelectedCommand.ExecuteAsync();

        await sync.BuildCommand.ExecuteAsync();

        Assert.AreEqual(1, client.LookupCallCount);
        Assert.AreEqual(1, sync.DownloadRequired);
        Assert.AreEqual(0, sync.Unknown);
    }

    [TestMethod]
    public async Task SyncPreflight_CreatesDeviceAndSourceBoundExecutionPlan()
    {
        var playlist = new Playlist(
            "Bound",
            sourcePath: Path.Combine(Path.GetTempPath(), "bound.bplist"),
            sourceContentSha256: new string('A', 64));
        playlist.Add(new PlaylistEntry("1a2b", Hash, "Known"));
        var client = new RecordingClient();
        var library = new LibraryViewModel();
        library.Apply(QuestBeatSaberScanResult.Empty, scanCompleted: true, deviceSerial: "QUEST-BOUND");
        var playlists = new PlaylistsViewModel(new StubImporter(playlist), client, new RecordingCache(), library, _ => Task.CompletedTask);
        var sync = new SyncViewModel(playlists, library, client, new RecordingCache(), _ => Task.CompletedTask);
        await playlists.ImportAsync([playlist.SourceIdentity!.CanonicalPath]);

        await sync.BuildCommand.ExecuteAsync();

        Assert.IsNotNull(sync.ExecutionPlan);
        Assert.AreEqual("QUEST-BOUND", sync.ExecutionPlan.Target.DeviceSerial);
        Assert.AreEqual(playlist.SourceIdentity, AssertSingle(sync.ExecutionPlan.PlaylistSources));
    }

    [TestMethod]
    public async Task Reimport_SamePathAndSameHash_IsANoOp()
    {
        var path = Path.Combine(Path.GetTempPath(), "same-source.bplist");
        var first = PlaylistWithEntries("Original", path, new string('A', 64), 2);
        var duplicate = PlaylistWithEntries("Ignored duplicate", path, new string('A', 64), 3);
        var importer = new BatchImporter([first], [duplicate]);
        var viewModel = new PlaylistsViewModel(importer, new RecordingClient(), new RecordingCache(), new LibraryViewModel(), _ => Task.CompletedTask);
        var changes = 0;
        viewModel.RequirementsChanged += (_, _) => changes++;

        await viewModel.ImportAsync([path]);
        await viewModel.ImportAsync([path]);

        Assert.AreEqual(1, viewModel.ImportedPlaylists.Count);
        Assert.AreSame(first, viewModel.ImportedPlaylists[0]);
        Assert.AreEqual(1, changes);
    }

    [TestMethod]
    public async Task Reimport_SamePathWithChangedHash_RefreshesAndInvalidatesPlans()
    {
        var path = Path.Combine(Path.GetTempPath(), "updated-source.bplist");
        var original = PlaylistWithEntries("ACG", path, new string('A', 64), 40);
        var updated = PlaylistWithEntries("ACG Updated", path, new string('B', 64), 50);
        var importer = new BatchImporter([original], [updated]);
        var library = new LibraryViewModel();
        library.Apply(QuestBeatSaberScanResult.Empty, scanCompleted: true, deviceSerial: "QUEST");
        var client = new RecordingClient();
        var cache = new RecordingCache();
        var playlists = new PlaylistsViewModel(importer, client, cache, library, _ => Task.CompletedTask);
        var sync = new SyncViewModel(playlists, library, client, cache, _ => Task.CompletedTask);
        await playlists.ImportAsync([path]);
        await sync.BuildCommand.ExecuteAsync();
        Assert.IsNotNull(sync.ExecutionPlan);

        await playlists.ImportAsync([path]);

        Assert.AreEqual(1, playlists.ImportedPlaylists.Count);
        Assert.AreSame(updated, playlists.ImportedPlaylists[0]);
        Assert.AreSame(updated, playlists.SelectedPlaylist);
        Assert.AreEqual(50, playlists.SelectedEntryCount);
        Assert.AreEqual(50, playlists.TotalPlaylistReferences);
        Assert.AreEqual(50, playlists.AllEntryStatuses.Count());
        Assert.IsTrue(playlists.AllEntryStatuses.All(status => status.LookupResult is null));
        Assert.IsNull(sync.Plan);
        Assert.IsNull(sync.ExecutionPlan);
    }

    [TestMethod]
    public async Task Reimport_StatusPreparationFailure_PreservesOriginalPlaylistAndStatuses()
    {
        var path = Path.Combine(Path.GetTempPath(), "failed-refresh.bplist");
        var original = PlaylistWithEntries("Original", path, new string('A', 64), 1);
        var updated = PlaylistWithEntries("Updated", path, new string('B', 64), 2);
        var cache = new SwitchableFailingCache();
        var viewModel = new PlaylistsViewModel(
            new BatchImporter([original], [updated]),
            new RecordingClient(),
            cache,
            new LibraryViewModel(),
            _ => Task.CompletedTask);
        var changes = 0;
        viewModel.RequirementsChanged += (_, _) => changes++;
        await viewModel.ImportAsync([path]);
        var originalStatuses = viewModel.AllEntryStatuses.ToArray();
        originalStatuses[0].StatusMessage = "keep me";
        cache.Fail = true;

        await viewModel.ImportAsync([path]);

        Assert.AreEqual(1, viewModel.ImportedPlaylists.Count);
        Assert.AreSame(original, viewModel.ImportedPlaylists[0]);
        Assert.AreSame(original, viewModel.SelectedPlaylist);
        Assert.AreEqual(1, viewModel.SelectedEntryCount);
        Assert.AreEqual(1, viewModel.TotalPlaylistReferences);
        Assert.AreSame(originalStatuses[0], AssertSingle(viewModel.AllEntryStatuses.ToArray()));
        Assert.AreEqual("keep me", originalStatuses[0].StatusMessage);
        Assert.AreEqual(1, changes);
        Assert.IsTrue(viewModel.HasImportErrors);
    }

    [TestMethod]
    public async Task Import_SameTitleFromDifferentPaths_KeepsBothSources()
    {
        var first = PlaylistWithEntries("Same title", Path.Combine(Path.GetTempPath(), "A", "foo.bplist"), new string('A', 64), 1);
        var second = PlaylistWithEntries("Same title", Path.Combine(Path.GetTempPath(), "B", "foo.bplist"), new string('B', 64), 1);
        var viewModel = new PlaylistsViewModel(
            new BatchImporter([first, second]),
            new RecordingClient(),
            new RecordingCache(),
            new LibraryViewModel(),
            _ => Task.CompletedTask);

        await viewModel.ImportAsync([first.SourcePath!, second.SourcePath!]);

        Assert.AreEqual(2, viewModel.ImportedPlaylists.Count);
        Assert.AreEqual(2, viewModel.ImportedPlaylists.Select(playlist => playlist.SourceIdentity!.CanonicalPath).Distinct(SyncExecutionPlan.SourcePathComparer).Count());
    }

    private static T AssertSingle<T>(IReadOnlyList<T> items)
    {
        Assert.HasCount(1, items);
        return items[0];
    }

    private static Playlist PlaylistWithEntries(string title, string path, string sha256, int entryCount)
    {
        var playlist = new Playlist(title, sourcePath: path, sourceContentSha256: sha256);
        for (var index = 0; index < entryCount; index++)
            playlist.Add(new PlaylistEntry(null, index.ToString("X40"), $"Map {index}"));
        return playlist;
    }

    private sealed class StubImporter(Playlist playlist) : ILocalPlaylistImporter
    {
        public Task<IReadOnlyList<PlaylistImportResult>> ImportAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlaylistImportResult>>([new("fixture.bplist", playlist, null)]);
    }

    private sealed class BatchImporter(params IReadOnlyList<Playlist>[] batches) : ILocalPlaylistImporter
    {
        private readonly Queue<IReadOnlyList<Playlist>> _batches = new(batches);

        public Task<IReadOnlyList<PlaylistImportResult>> ImportAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default)
        {
            var batch = _batches.Dequeue();
            return Task.FromResult<IReadOnlyList<PlaylistImportResult>>(
                batch.Select(playlist => new PlaylistImportResult(playlist.SourcePath!, playlist, null)).ToArray());
        }
    }

    private sealed class RecordingClient : IBeatSaverClient
    {
        public int LookupCallCount { get; private set; }
        public Task<BeatSaverLookupResult> LookupAsync(BeatSaverLookupRequest request, CancellationToken cancellationToken = default)
        {
            LookupCallCount++;
            return Task.FromResult(new BeatSaverLookupResult(BeatSaverAvailability.Online, request.Hash, request.Key, Hash, request.Key, new("https://example.test/map.zip"), request.Hash is not null));
        }
        public Task DownloadZipAsync(Uri downloadUri, Stream destination, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class RecordingCache : IBeatMapCache
    {
        public int CacheCallCount { get; private set; }
        public Task<bool> IsCachedAsync(string hash, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<BeatMapCacheResult> CacheAsync(BeatSaverLookupResult lookup, CancellationToken cancellationToken = default)
        {
            CacheCallCount++;
            return Task.FromResult(new BeatMapCacheResult(BeatMapCacheOutcome.Cached));
        }
    }

    private sealed class SwitchableFailingCache : IBeatMapCache
    {
        public bool Fail { get; set; }

        public Task<bool> IsCachedAsync(string hash, CancellationToken cancellationToken = default) =>
            Fail ? throw new IOException("Fixture cache lookup failure.") : Task.FromResult(false);

        public Task<BeatMapCacheResult> CacheAsync(BeatSaverLookupResult lookup, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
