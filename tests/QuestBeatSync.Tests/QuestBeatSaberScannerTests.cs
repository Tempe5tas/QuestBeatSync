using QuestBeatSync.Core.Models;
using QuestBeatSync.Infrastructure.Scanning;
using QuestBeatSync.Tests.Fixtures;
using QuestBeatSync.Core.Services;
using QuestBeatSync.Infrastructure.Adb;

namespace QuestBeatSync.Tests;

[TestClass]
public sealed class QuestBeatSaberScannerTests
{
    private static readonly QuestDevice Device =
        new("QUEST123", QuestConnectionState.Device, QuestTransportKind.Usb, "Quest 3");

    [TestMethod]
    public async Task ScanAsync_HandlesEmptyCustomLevels()
    {
        var fixture = CreateDetectedEnvironment();

        var result = await CreateScanner(fixture).ScanAsync(Device);

        Assert.IsTrue(result.BeatSaberDetected);
        Assert.IsTrue(result.SongCoreDetected);
        Assert.IsTrue(result.PlaylistManagerDetected);
        Assert.AreEqual(0, result.CustomSongCount);
        Assert.AreEqual(0, result.PlaylistCount);
    }

    [TestMethod]
    public async Task ScanAsync_ReadsThreeNormalMaps()
    {
        var fixture = CreateDetectedEnvironment();
        AddMap(fixture, "Map One", "第一首歌", "Mapper A");
        AddMap(fixture, "Map Two", "二曲目", "Mapper B");
        AddMap(fixture, "0123456789ABCDEF0123456789ABCDEF01234567", "Third Song", "Mapper C");

        var result = await CreateScanner(fixture).ScanAsync(Device);

        Assert.AreEqual(3, result.CustomSongCount);
        CollectionAssert.AreEquivalent(
            new[] { "第一首歌", "二曲目", "Third Song" },
            result.InstalledMaps.Select(map => map.SongTitle).ToArray());
        Assert.IsTrue(result.InstalledMaps.All(map => map.InfoDatExists));
        Assert.AreEqual(2, result.InstalledMaps.Count(map => map.IdentityStatus == QuestMapIdentityStatus.LocalOnly));
        var identified = result.InstalledMaps.Single(map => map.IdentityStatus == QuestMapIdentityStatus.HashIdentified);
        Assert.AreEqual("0123456789ABCDEF0123456789ABCDEF01234567", identified.Identity?.Hash);
    }

    [TestMethod]
    public async Task ScanAsync_RealCustomLevelsPathPreservesShCArgumentAndDiscoversMaps()
    {
        var runner = new RealStyleShellRunner();
        var options = new AdbQuestTransportOptions
        {
            ConfiguredExecutablePath = "test-adb",
            AppDataToolsDirectory = "unused"
        };
        var transport = new AdbQuestTransport(
            options,
            new AdbExecutableResolver(path => path == "test-adb", () => null),
            runner);
        var scanner = new QuestBeatSaberScanner(new AdbQuestRemoteFileSystem(transport), QuestBeatSaberPaths.Default);

        var result = await scanner.ScanAsync(Device);

        Assert.IsTrue(result.CustomLevelsDetected);
        Assert.AreEqual(2, result.CustomLevelFolderCount);
        Assert.AreEqual(2, result.CustomSongCount);
        Assert.AreEqual(1, result.InstalledMaps.Count(map => map.IdentityStatus == QuestMapIdentityStatus.HashIdentified));
        Assert.AreEqual(1, result.InstalledMaps.Count(map => map.IdentityStatus == QuestMapIdentityStatus.LocalOnly));
        Assert.IsTrue(runner.RemoteCommands.All(command => command.StartsWith("'sh' '-c' '", StringComparison.Ordinal)));
        Assert.IsTrue(runner.RemoteCommands.Any(command => command.Contains($"test -d '\"'\"'{QuestBeatSaberPaths.Default.CustomLevels}'\"'\"'", StringComparison.Ordinal)));
        Assert.IsTrue(runner.RemoteCommands.Any(command => command.Contains($"find '\"'\"'{QuestBeatSaberPaths.Default.CustomLevels}'\"'\"' -mindepth 1 -maxdepth 1 -type d -print", StringComparison.Ordinal)));
        Assert.IsFalse(runner.RemoteCommands.Any(command => command.StartsWith("sh -c test", StringComparison.Ordinal) || command.StartsWith("sh -c find", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ScanAsync_MarksMapWithoutInfoDatAsUnknown()
    {
        var fixture = CreateDetectedEnvironment();
        fixture.AddSubdirectory(QuestBeatSaberPaths.Default.CustomLevels, "Missing Info");

        var result = await CreateScanner(fixture).ScanAsync(Device);

        Assert.HasCount(1, result.InstalledMaps);
        var map = result.InstalledMaps[0];
        Assert.IsFalse(map.InfoDatExists);
        Assert.AreEqual(QuestMapIdentityStatus.Unknown, map.IdentityStatus);
        Assert.IsNotNull(map.Warning);
    }

    [TestMethod]
    public async Task ScanAsync_DoesNotGuessHashFromNonHashFolder()
    {
        var fixture = CreateDetectedEnvironment();
        AddMap(fixture, "My Favorite Song (Custom)", "Favorite", "Mapper");

        var result = await CreateScanner(fixture).ScanAsync(Device);

        Assert.HasCount(1, result.InstalledMaps);
        var map = result.InstalledMaps[0];
        Assert.AreEqual("My Favorite Song (Custom)", map.FolderName);
        Assert.AreEqual(QuestMapIdentityStatus.LocalOnly, map.IdentityStatus);
        Assert.IsNull(map.Identity);
    }

    [TestMethod]
    public async Task ScanAsync_IgnoresQbSyncStagingDirectories()
    {
        var fixture = CreateDetectedEnvironment();
        AddMap(fixture, ".qbsync-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA-run", "Incomplete", "QBSync");
        AddMap(fixture, "Normal Folder", "Visible", "Mapper");

        var result = await CreateScanner(fixture).ScanAsync(Device);

        Assert.HasCount(1, result.InstalledMaps);
        Assert.AreEqual("Normal Folder", result.InstalledMaps[0].FolderName);
    }

    [TestMethod]
    public async Task ScanAsync_ExactFortyHexFolderFlowsIntoPlannerAsKeepExisting()
    {
        const string hash = "0123456789ABCDEF0123456789ABCDEF01234567";
        var fixture = CreateDetectedEnvironment();
        AddMap(fixture, hash.ToLowerInvariant(), "Already Here", "Mapper");

        var scan = await CreateScanner(fixture).ScanAsync(Device);
        var installed = AssertSingle(scan.InstalledMaps);
        Assert.AreEqual(QuestMapIdentityStatus.HashIdentified, installed.IdentityStatus);
        Assert.AreEqual(hash, installed.Identity?.Hash);

        var desired = new Playlist("Desired");
        desired.Add(new PlaylistEntry("different-key", hash, "Already Here"));
        var plan = SyncPlanner.Build(
            [desired],
            new QuestLibrary(installedMaps: scan.InstalledMaps),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, BeatSaverAvailability>(StringComparer.OrdinalIgnoreCase)
            {
                [hash] = BeatSaverAvailability.Online
            });

        Assert.AreEqual(1, plan.AlreadyInstalledCount);
        Assert.AreEqual(0, plan.DownloadRequiredCount);
        Assert.AreEqual(0, plan.UploadRequiredCount);
        Assert.AreEqual(0, plan.QuestOnlyPreservedCount);
        Assert.AreEqual(SyncOperationKind.KeepExisting, plan.Operations[0].Kind);
    }

    [TestMethod]
    public async Task ScanAsync_FortyNonHexCharactersRemainLocalOnly()
    {
        var fixture = CreateDetectedEnvironment();
        AddMap(fixture, "GGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG", "Not a hash", "Mapper");

        var result = await CreateScanner(fixture).ScanAsync(Device);

        var map = AssertSingle(result.InstalledMaps);
        Assert.AreEqual(QuestMapIdentityStatus.LocalOnly, map.IdentityStatus);
        Assert.IsNull(map.Identity);
    }

    [TestMethod]
    public async Task ScanAsync_KeepsMalformedPlaylistAndAddsWarning()
    {
        var fixture = CreateDetectedEnvironment();
        fixture.AddFile(QuestBeatSaberPaths.Default.Playlists, "broken.bplist", "{ not valid json");

        var result = await CreateScanner(fixture).ScanAsync(Device);

        Assert.HasCount(1, result.InstalledPlaylists);
        var playlist = result.InstalledPlaylists[0];
        Assert.AreEqual("broken.bplist", playlist.Filename);
        Assert.AreEqual(0, playlist.SongReferenceCount);
        Assert.IsNotNull(playlist.Warning);
        Assert.HasCount(1, result.Warnings);
    }

    [TestMethod]
    public async Task ScanAsync_ReadsUtf8PlaylistsAndSupportedFormats()
    {
        var fixture = CreateDetectedEnvironment();
        fixture.AddFile(
            QuestBeatSaberPaths.Default.Playlists,
            "中文歌单.bplist",
            """{"playlistTitle":"中文健身歌单","songs":[{"hash":"A"},{"hash":"B"}]}""");
        fixture.AddFile(
            QuestBeatSaberPaths.Default.Playlists,
            "日本語_BMBF.json",
            """{"playlistTitle":"日本語プレイリスト","songs":[{"hash":"C"}]}""");
        fixture.AddFile(
            QuestBeatSaberPaths.Default.Playlists,
            "favorites.json",
            """{"title":"Favorites","maps":[]}""");

        var result = await CreateScanner(fixture).ScanAsync(Device);

        Assert.HasCount(3, result.InstalledPlaylists);
        Assert.AreEqual("中文健身歌单", result.InstalledPlaylists[0].PlaylistTitle);
        Assert.AreEqual(2, result.InstalledPlaylists[0].SongReferenceCount);
        Assert.AreEqual("日本語プレイリスト", result.InstalledPlaylists[1].PlaylistTitle);
        CollectionAssert.AreEquivalent(
            new[] { QuestPlaylistFormat.Bplist, QuestPlaylistFormat.BmbfJson, QuestPlaylistFormat.Json },
            result.InstalledPlaylists.Select(playlist => playlist.Format).ToArray());
    }

    private static FixtureQuestRemoteFileSystem CreateDetectedEnvironment()
    {
        var fixture = new FixtureQuestRemoteFileSystem();
        var paths = QuestBeatSaberPaths.Default;
        fixture.AddDirectory(paths.BeatSaberModData);
        fixture.AddDirectory(paths.SongCore);
        fixture.AddDirectory(paths.CustomLevels);
        fixture.AddDirectory(paths.PlaylistManager);
        fixture.AddDirectory(paths.Playlists);
        return fixture;
    }

    private static void AddMap(
        FixtureQuestRemoteFileSystem fixture,
        string folderName,
        string title,
        string mapper)
    {
        var folder = fixture.AddSubdirectory(QuestBeatSaberPaths.Default.CustomLevels, folderName);
        fixture.AddFile(
            folder,
            "Info.dat",
            $$"""{"_songName":"{{title}}","_levelAuthorName":"{{mapper}}"}""");
    }

    private static QuestBeatSaberScanner CreateScanner(FixtureQuestRemoteFileSystem fixture) =>
        new(fixture, QuestBeatSaberPaths.Default);

    private static T AssertSingle<T>(IReadOnlyList<T> items)
    {
        Assert.HasCount(1, items);
        return items[0];
    }

    private sealed class RealStyleShellRunner : IAdbProcessRunner
    {
        private const string Hash = "0123456789ABCDEF0123456789ABCDEF01234567";
        private static readonly string HashFolder = $"{QuestBeatSaberPaths.Default.CustomLevels}/{Hash}";
        private static readonly string LocalFolder = $"{QuestBeatSaberPaths.Default.CustomLevels}/Legacy Map";
        public List<string> RemoteCommands { get; } = [];

        public Task<AdbProcessResult> RunAsync(string executablePath, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            CollectionAssert.AreEqual(new[] { "-s", Device.Serial, "shell" }, arguments.Take(3).ToArray());
            Assert.AreEqual(4, arguments.Count, "adb shell must receive exactly one serialized remote command.");
            var command = arguments[3];
            RemoteCommands.Add(command);

            if (command.Contains("test -d", StringComparison.Ordinal)) return Success();
            if (command.Contains(QuestBeatSaberPaths.Default.CustomLevels, StringComparison.Ordinal) && command.Contains("-type d", StringComparison.Ordinal))
                return Success($"{HashFolder}\n{LocalFolder}\n");
            if (command.Contains(QuestBeatSaberPaths.Default.Playlists, StringComparison.Ordinal) && command.Contains("-type f", StringComparison.Ordinal))
                return Success();
            if (command.Contains("-type f", StringComparison.Ordinal))
            {
                var folder = command.Contains(Hash, StringComparison.Ordinal) ? HashFolder : LocalFolder;
                return Success($"{folder}/Info.dat\n");
            }
            if (command.Contains("cat ", StringComparison.Ordinal)) return Success("{\"_songName\":\"Real map\",\"_levelAuthorName\":\"Mapper\"}");
            return Task.FromResult(new AdbProcessResult(true, false, 1, "", "unexpected remote command"));
        }

        private static Task<AdbProcessResult> Success(string output = "") =>
            Task.FromResult(new AdbProcessResult(true, false, 0, output, ""));
    }
}
