using QuestBeatSync.Core.Models;
using QuestBeatSync.Core.Services;

namespace QuestBeatSync.Tests;

[TestClass]
public sealed class SyncPlannerTests
{
    private const string HashA = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string HashB = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    [TestMethod]
    [DataRow("empty", 0, 0, 0, 0, 0, 0, 0, 0)]
    [DataRow("all-installed", 1, 0, 0, 0, 0, 0, 1, 1)]
    [DataRow("all-missing", 0, 1, 1, 0, 0, 0, 1, 1)]
    [DataRow("duplicates", 0, 1, 1, 0, 0, 0, 1, 2)]
    [DataRow("local-only-map", 0, 0, 0, 0, 0, 1, 0, 0)]
    [DataRow("unavailable", 0, 0, 0, 1, 0, 0, 1, 1)]
    [DataRow("network-unknown", 0, 0, 0, 0, 1, 0, 1, 1)]
    [DataRow("same-key-different-hash", 0, 1, 1, 0, 0, 1, 1, 1)]
    [DataRow("same-title-different-hash", 0, 1, 1, 0, 0, 1, 1, 1)]
    [DataRow("same-hash-multiple-playlists", 0, 1, 1, 0, 0, 0, 2, 2)]
    [DataRow("cached-locally", 0, 0, 1, 0, 0, 0, 1, 1)]
    public void Build_TableDrivenRulesProduceExpectedNonDestructivePlan(
        string scenario,
        int keep,
        int download,
        int upload,
        int unavailable,
        int unknown,
        int preserve,
        int import,
        int references)
    {
        var input = CreateScenario(scenario);

        var plan = SyncPlanner.Build(input.Playlists, input.Library, input.Cached, input.Availability);

        Assert.AreEqual(keep, plan.AlreadyInstalledCount, scenario);
        Assert.AreEqual(download, plan.DownloadRequiredCount, scenario);
        Assert.AreEqual(upload, plan.UploadRequiredCount, scenario);
        Assert.AreEqual(unavailable, plan.UnavailableCount, scenario);
        Assert.AreEqual(unknown, plan.UnknownCount, scenario);
        Assert.AreEqual(preserve, plan.QuestOnlyPreservedCount, scenario);
        Assert.AreEqual(import, plan.Count(SyncOperationKind.ImportPlaylist), scenario);
        Assert.AreEqual(references, plan.PlaylistReferenceCount, scenario);
        Assert.AreEqual(0, plan.DeletionCount, scenario);
    }

    [TestMethod]
    public void Build_SameKeyDifferentHash_KeepsExactHashesSeparate()
    {
        var input = CreateScenario("same-key-different-hash");

        var plan = SyncPlanner.Build(input.Playlists, input.Library, input.Cached, input.Availability);
        var hashes = plan.Operations
            .Where(operation => operation.Kind == SyncOperationKind.DownloadMap)
            .Select(operation => operation.MapIdentity!.Hash)
            .ToArray();

        CollectionAssert.AreEqual(new[] { HashB }, hashes);
        Assert.AreEqual(1, plan.UniqueMapCount);
        Assert.AreEqual(HashA, plan.Operations
            .Single(operation => operation.Kind == SyncOperationKind.PreserveQuestOnly)
            .MapIdentity!.Hash);
    }

    [TestMethod]
    public void Build_QuestUnknownIdentity_IsAlwaysPreserved()
    {
        var unknownMap = new QuestInstalledMap(
            "/custom/local",
            "local",
            true,
            "手制作品",
            "Mapper",
            QuestMapIdentityStatus.LocalOnly);
        var library = new QuestLibrary(installedMaps: [unknownMap]);

        var plan = SyncPlanner.Build([], library, EmptySet(), EmptyAvailability());

        Assert.AreEqual(1, plan.QuestOnlyPreservedCount);
        Assert.IsNull(plan.Operations.Single().MapIdentity);
    }

    [TestMethod]
    public void Build_EntryWithoutHash_IsSkippedAsUnknownInsteadOfGuessedByTitleOrKey()
    {
        var playlist = PlaylistWith(new PlaylistEntry("1a2b", null, "Same title"));

        var plan = SyncPlanner.Build([playlist], new QuestLibrary(), EmptySet(), EmptyAvailability());

        Assert.AreEqual(1, plan.UnknownCount);
        Assert.AreEqual(0, plan.UniqueMapCount);
        Assert.IsNull(plan.Operations.Single(operation => operation.Kind == SyncOperationKind.SkipUnknown).MapIdentity);
    }

    [TestMethod]
    public void SemanticallyEqualBmbfPlaylist_IsKeptWithoutDuplicateImport()
    {
        var desired = PlaylistWithSource("ROCK", "BeatSaver - ROCK.bplist", HashA);
        var quest = new QuestInstalledPlaylist(
            "/playlists/BeatSaver - ROCK.bplist_BMBF.json",
            "BeatSaver - ROCK.bplist_BMBF.json",
            "ROCK",
            1,
            QuestPlaylistFormat.BmbfJson,
            NormalizedSongIdentities: [$"H:{HashA}"],
            SemanticIdentityComplete: true,
            FilenameLineage: "BeatSaver - ROCK.bplist");

        var plan = SyncPlanner.Build([desired], new QuestLibrary(installedPlaylists: [quest]), EmptySet(), OnlineAvailability(HashA));

        Assert.AreEqual(0, plan.Count(SyncOperationKind.ImportPlaylist));
        Assert.AreEqual(1, plan.ExistingPlaylistCount);
    }

    [TestMethod]
    public void SameTitleAndCountWithDifferentHashes_IsConflictNotEquality()
    {
        var desired = PlaylistWithSource("ROCK", "BeatSaver - ROCK.bplist", HashA);
        var quest = new QuestInstalledPlaylist(
            "/playlists/BeatSaver - ROCK.bplist_BMBF.json",
            "BeatSaver - ROCK.bplist_BMBF.json",
            "ROCK",
            1,
            QuestPlaylistFormat.BmbfJson,
            NormalizedSongIdentities: [$"H:{HashB}"],
            SemanticIdentityComplete: true,
            FilenameLineage: "BeatSaver - ROCK.bplist");

        var plan = SyncPlanner.Build([desired], new QuestLibrary(installedPlaylists: [quest]), EmptySet(), OnlineAvailability(HashA));

        Assert.AreEqual(0, plan.Count(SyncOperationKind.ImportPlaylist));
        Assert.AreEqual(1, plan.PlaylistConflictCount);
    }

    [TestMethod]
    public void ChangedExistingPlaylist_IsPreservedWithoutSecondIngressFile()
    {
        var desired = new Playlist("ACG", sourcePath: Path.GetFullPath("BeatSaver - ACG.bplist"), sourceContentSha256: new string('A', 64));
        for (var index = 0; index < 42; index++) desired.Add(new PlaylistEntry(index.ToString("X"), HashA, $"Song {index}"));
        var questIdentities = Enumerable.Repeat($"H:{HashA}", 40).ToArray();
        var quest = new QuestInstalledPlaylist(
            "/playlists/BeatSaver - ACG.bplist_BMBF.json",
            "BeatSaver - ACG.bplist_BMBF.json",
            "ACG",
            40,
            QuestPlaylistFormat.BmbfJson,
            NormalizedSongIdentities: questIdentities,
            SemanticIdentityComplete: true,
            FilenameLineage: "BeatSaver - ACG.bplist");

        var plan = SyncPlanner.Build([desired], new QuestLibrary(installedPlaylists: [quest]), EmptySet(), OnlineAvailability(HashA));

        Assert.AreEqual(1, plan.PlaylistConflictCount);
        Assert.AreEqual(0, plan.Count(SyncOperationKind.ImportPlaylist));
        StringAssert.Contains(plan.Operations.Single(operation => operation.Kind == SyncOperationKind.PlaylistConflict).Description, "Quest: 40 songs; desired: 42 songs");
    }

    private static Scenario CreateScenario(string scenario) => scenario switch
    {
        "empty" => new([], new QuestLibrary(), EmptySet(), EmptyAvailability()),
        "all-installed" => new(
            [PlaylistWith(Entry(HashA))],
            LibraryWith(Map(HashA)),
            EmptySet(),
            EmptyAvailability()),
        "all-missing" => OnlineScenario([PlaylistWith(Entry(HashA))]),
        "duplicates" => OnlineScenario([PlaylistWith(Entry(HashA), Entry(HashA))]),
        "local-only-map" => new(
            [],
            LibraryWith(LocalMap("Local folder")),
            EmptySet(),
            EmptyAvailability()),
        "unavailable" => AvailabilityScenario(BeatSaverAvailability.Unavailable),
        "network-unknown" => AvailabilityScenario(BeatSaverAvailability.Unknown),
        "same-key-different-hash" => new(
            [PlaylistWith(Entry(HashB, "same-key"))],
            LibraryWith(Map(HashA, "same-key")),
            EmptySet(),
            OnlineAvailability(HashB)),
        "same-title-different-hash" => new(
            [PlaylistWith(Entry(HashB, "new-key", "Same title"))],
            LibraryWith(Map(HashA, "old-key", "Same title")),
            EmptySet(),
            OnlineAvailability(HashB)),
        "same-hash-multiple-playlists" => OnlineScenario(
            [PlaylistWith(Entry(HashA), name: "One"), PlaylistWith(Entry(HashA), name: "Two")]),
        "cached-locally" => new(
            [PlaylistWith(Entry(HashA))],
            new QuestLibrary(),
            HashSet(HashA),
            EmptyAvailability()),
        _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null)
    };

    private static Scenario OnlineScenario(IReadOnlyList<Playlist> playlists, params string[] hashes) =>
        new(
            playlists,
            new QuestLibrary(),
            EmptySet(),
            (hashes.Length == 0 ? [HashA] : hashes)
            .ToDictionary(hash => hash, _ => BeatSaverAvailability.Online, StringComparer.OrdinalIgnoreCase));

    private static Scenario AvailabilityScenario(BeatSaverAvailability availability) =>
        new(
            [PlaylistWith(Entry(HashA))],
            new QuestLibrary(),
            EmptySet(),
            new Dictionary<string, BeatSaverAvailability>(StringComparer.OrdinalIgnoreCase)
            {
                [HashA] = availability
            });

    private static Playlist PlaylistWith(PlaylistEntry entry, string name = "Desired") =>
        PlaylistWith([entry], name);

    private static Playlist PlaylistWith(PlaylistEntry first, PlaylistEntry second, string name = "Desired") =>
        PlaylistWith([first, second], name);

    private static Playlist PlaylistWith(IEnumerable<PlaylistEntry> entries, string name = "Desired")
    {
        var playlist = new Playlist(name);
        foreach (var entry in entries)
        {
            playlist.Add(entry);
        }

        return playlist;
    }

    private static PlaylistEntry Entry(string hash, string? key = null, string? title = null) =>
        new(key, hash, title ?? hash);

    private static Playlist PlaylistWithSource(string name, string basename, string hash)
    {
        var playlist = new Playlist(name, sourcePath: Path.GetFullPath(basename), sourceContentSha256: new string('A', 64));
        playlist.Add(Entry(hash));
        return playlist;
    }

    private static QuestInstalledMap Map(
        string hash,
        string? key = null,
        string title = "Map") =>
        new(
            $"/custom/{hash}",
            hash,
            true,
            title,
            "Mapper",
            QuestMapIdentityStatus.HashIdentified,
            new BeatMapIdentity(hash, key));

    private static QuestInstalledMap LocalMap(string folder) =>
        new($"/custom/{folder}", folder, true, folder, "Mapper", QuestMapIdentityStatus.LocalOnly);

    private static QuestLibrary LibraryWith(params QuestInstalledMap[] maps) => new(maps);

    private static HashSet<string> HashSet(params string[] hashes) =>
        new(hashes, StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> EmptySet() => HashSet();

    private static Dictionary<string, BeatSaverAvailability> EmptyAvailability() =>
        new(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, BeatSaverAvailability> OnlineAvailability(params string[] hashes) =>
        hashes.ToDictionary(hash => hash, _ => BeatSaverAvailability.Online, StringComparer.OrdinalIgnoreCase);

    private sealed record Scenario(
        IReadOnlyList<Playlist> Playlists,
        QuestLibrary Library,
        IReadOnlySet<string> Cached,
        IReadOnlyDictionary<string, BeatSaverAvailability> Availability);
}
