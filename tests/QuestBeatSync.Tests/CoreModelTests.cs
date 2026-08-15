using QuestBeatSync.Core.Models;

namespace QuestBeatSync.Tests;

[TestClass]
public sealed class CoreModelTests
{
    [TestMethod]
    public void BeatMapIdentity_Equality_UsesHashInsteadOfMapKey()
    {
        var first = new BeatMapIdentity("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "1a2b");
        var second = new BeatMapIdentity("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "different-key");

        Assert.AreEqual(first, second);
        Assert.AreEqual(first.GetHashCode(), second.GetHashCode());
    }

    [TestMethod]
    public void Playlist_CanStoreMultipleMaps()
    {
        var playlist = new Playlist("Workout");
        playlist.Add(new PlaylistEntry(new BeatMapIdentity("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"), "First song"));
        playlist.Add(new PlaylistEntry(new BeatMapIdentity("BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB"), "Second song"));

        Assert.HasCount(2, playlist.Entries);
    }

    [TestMethod]
    public void BeatMapIdentity_RejectsMalformedHash()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new BeatMapIdentity("not-a-sha1"));

        var entry = new PlaylistEntry("key", "GGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG", "Invalid");
        Assert.AreEqual(PlaylistEntryIdentityStatus.InvalidHash, entry.IdentityStatus);
        Assert.IsNull(entry.Identity);
        Assert.IsNull(entry.Hash);
    }

    [TestMethod]
    public void SyncPlan_ReportsOperationCount()
    {
        var plan = new SyncPlan();
        plan.Add(new SyncOperation(SyncOperationKind.UploadMap, "Copy first map"));
        plan.Add(new SyncOperation(SyncOperationKind.ImportPlaylist, "Import playlist"));

        Assert.AreEqual(2, plan.OperationCount);
    }
}
