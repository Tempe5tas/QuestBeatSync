using QuestBeatSync.Core.Models;
using QuestBeatSync.Infrastructure.Scanning;
using QuestBeatSync.Tests.Fixtures;

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
        Assert.IsTrue(result.InstalledMaps.All(map => map.IdentityStatus == QuestMapIdentityStatus.LocalOnly));
        Assert.IsTrue(result.InstalledMaps.All(map => map.Identity is null));
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
}
