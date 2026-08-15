using System.Text;
using System.Text.Json;
using QuestBeatSync.Core.Models;
using QuestBeatSync.Infrastructure.Importing;

namespace QuestBeatSync.Tests;

[TestClass]
public sealed class BplistImportTests
{
    [TestMethod]
    public async Task ImportAsync_ParsesUtf8MetadataDuplicatesAndMissingIdentityFields()
    {
        var result = AssertSingle(await new LocalBplistImporter().ImportAsync([FixturePath("unicode-identity.bplist")]));

        Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
        var playlist = result.Playlist!;
        Assert.AreEqual("ACG", playlist.Name);
        Assert.AreEqual("Dana_Iclucia", playlist.Author);
        Assert.AreEqual("中文说明と日本語の説明", playlist.Description);
        Assert.AreEqual("data:image/png;base64,QUJDRA==", playlist.Image);
        Assert.AreEqual("https://example.test/acg.bplist", playlist.SyncUrl);
        Assert.AreEqual(5, playlist.EntryCount);
        Assert.AreEqual(3, playlist.UniqueHashCount);
        Assert.AreEqual(1, playlist.DuplicateReferenceCount);
        Assert.AreEqual("中文歌曲", playlist.Entries[0].SongName);
        Assert.AreEqual("日本語の曲", playlist.Entries[1].SongName);
        Assert.IsNull(playlist.Entries[3].Key);
        Assert.AreEqual(PlaylistEntryIdentityStatus.HashIdentified, playlist.Entries[3].IdentityStatus);
        Assert.IsNull(playlist.Entries[4].Hash);
        Assert.AreEqual(PlaylistEntryIdentityStatus.MissingHash, playlist.Entries[4].IdentityStatus);
    }

    [TestMethod]
    public void LibraryState_DeduplicatesHashesAcrossMultiplePlaylists()
    {
        var first = BplistParser.Parse(File.ReadAllText(FixturePath("unicode-identity.bplist"), Encoding.UTF8));
        var second = new Playlist("Second");
        second.Add(new PlaylistEntry("shared", "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB", "Shared"));
        second.Add(new PlaylistEntry("new", "DDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDD", "New"));
        second.Add(new PlaylistEntry("unknown", null, "No hash"));

        var state = new LocalPlaylistLibraryState([first, second]);

        Assert.AreEqual(8, state.TotalPlaylistReferences);
        Assert.AreEqual(4, state.UniqueRequiredHashes);
        Assert.AreEqual(2, state.DuplicateReferences);
    }

    [TestMethod]
    public async Task ImportAsync_ReturnsClearMalformedJsonErrorWithoutBlockingOtherFiles()
    {
        var results = await new LocalBplistImporter().ImportAsync(
            [FixturePath("malformed.bplist"), FixturePath("unicode-identity.bplist")]);

        Assert.HasCount(2, results);
        Assert.IsFalse(results[0].IsSuccess);
        StringAssert.Contains(results[0].ErrorMessage, "Malformed .bplist JSON");
        Assert.IsTrue(results[1].IsSuccess);
    }

    [TestMethod]
    public async Task ImportAsync_HandlesLargeBase64CoverWithoutDecodingIt()
    {
        var cover = $"data:image/png;base64,{Convert.ToBase64String(new byte[1_500_000])}";
        var json = JsonSerializer.Serialize(new
        {
            playlistTitle = "Large Cover",
            playlistAuthor = "Test",
            image = cover,
            songs = Array.Empty<object>()
        });
        var tempPath = Path.Combine(Path.GetTempPath(), $"qbsync-{Guid.NewGuid():N}.bplist");

        try
        {
            await File.WriteAllTextAsync(tempPath, json, new UTF8Encoding(false, true));
            var result = AssertSingle(await new LocalBplistImporter().ImportAsync([tempPath]));

            Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
            Assert.AreEqual(cover.Length, result.Playlist!.Image!.Length);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    private static PlaylistImportResult AssertSingle(IReadOnlyList<PlaylistImportResult> results)
    {
        Assert.HasCount(1, results);
        return results[0];
    }

    private static string FixturePath(string filename) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", filename);
}
