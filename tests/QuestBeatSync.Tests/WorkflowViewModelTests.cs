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

    private static T AssertSingle<T>(IReadOnlyList<T> items)
    {
        Assert.HasCount(1, items);
        return items[0];
    }

    private sealed class StubImporter(Playlist playlist) : ILocalPlaylistImporter
    {
        public Task<IReadOnlyList<PlaylistImportResult>> ImportAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlaylistImportResult>>([new("fixture.bplist", playlist, null)]);
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
}
