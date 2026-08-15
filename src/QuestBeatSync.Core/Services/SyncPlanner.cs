using QuestBeatSync.Core.Models;

namespace QuestBeatSync.Core.Services;

public static class SyncPlanner
{
    public static SyncPlan Build(
        IEnumerable<Playlist> desiredPlaylists,
        QuestLibrary questLibrary,
        IReadOnlySet<string> locallyCachedHashes,
        IReadOnlyDictionary<string, BeatSaverAvailability> beatSaverAvailabilityByHash)
    {
        ArgumentNullException.ThrowIfNull(desiredPlaylists);
        ArgumentNullException.ThrowIfNull(questLibrary);
        ArgumentNullException.ThrowIfNull(locallyCachedHashes);
        ArgumentNullException.ThrowIfNull(beatSaverAvailabilityByHash);

        var playlists = desiredPlaylists.ToArray();
        var cachedHashes = locallyCachedHashes
            .Where(hash => !string.IsNullOrWhiteSpace(hash))
            .Select(NormalizeHash)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var availability = beatSaverAvailabilityByHash
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
            .GroupBy(pair => NormalizeHash(pair.Key), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => ResolveAvailability(group.Select(pair => pair.Value)),
                StringComparer.OrdinalIgnoreCase);
        var questMaps = CollectQuestMaps(questLibrary);
        var questHashes = questMaps
            .Where(map => map.Identity is not null)
            .Select(map => map.Identity!.Hash)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var requiredEntries = playlists
            .SelectMany(playlist => playlist.Entries)
            .Where(entry => entry.Hash is not null)
            .GroupBy(entry => NormalizeHash(entry.Hash!), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var requiredHashes = requiredEntries
            .Select(entry => NormalizeHash(entry.Hash!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var plan = new SyncPlan(
            playlists.Sum(playlist => playlist.EntryCount),
            requiredHashes.Count);

        foreach (var entry in requiredEntries)
        {
            var identity = entry.Identity!;
            var hash = identity.Hash;
            var label = string.IsNullOrWhiteSpace(entry.SongName) ? hash : entry.SongName;

            if (questHashes.Contains(hash))
            {
                plan.Add(new SyncOperation(
                    SyncOperationKind.KeepExisting,
                    $"Keep existing Quest map: {label}",
                    identity));
                continue;
            }

            if (cachedHashes.Contains(hash))
            {
                plan.Add(new SyncOperation(
                    SyncOperationKind.UploadMap,
                    $"Upload cached map to Quest: {label}",
                    identity));
                continue;
            }

            var mapAvailability = availability.GetValueOrDefault(hash, BeatSaverAvailability.Unknown);
            switch (mapAvailability)
            {
                case BeatSaverAvailability.Online:
                    plan.Add(new SyncOperation(
                        SyncOperationKind.DownloadMap,
                        $"Download exact hash from BeatSaver: {label}",
                        identity));
                    plan.Add(new SyncOperation(
                        SyncOperationKind.UploadMap,
                        $"Upload downloaded map to Quest: {label}",
                        identity));
                    break;
                case BeatSaverAvailability.Unavailable:
                    plan.Add(new SyncOperation(
                        SyncOperationKind.SkipUnavailable,
                        $"Skip unavailable exact hash: {label}",
                        identity));
                    break;
                default:
                    plan.Add(new SyncOperation(
                        SyncOperationKind.SkipUnknown,
                        $"Skip map because availability is unknown: {label}",
                        identity));
                    break;
            }
        }

        foreach (var unresolved in playlists
                     .SelectMany(playlist => playlist.Entries)
                     .Where(entry => entry.Hash is null)
                     .GroupBy(UnresolvedEntryKey, StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.First()))
        {
            var label = unresolved.SongName ?? unresolved.Key ?? "unidentified playlist entry";
            plan.Add(new SyncOperation(
                SyncOperationKind.SkipUnknown,
                $"Skip entry without an exact hash: {label}"));
        }

        foreach (var questMap in questMaps.Where(map =>
                     map.Identity is null || !requiredHashes.Contains(map.Identity.Hash)))
        {
            var label = questMap.Title ?? questMap.Identity?.Hash ?? "unidentified Quest map";
            plan.Add(new SyncOperation(
                SyncOperationKind.PreserveQuestOnly,
                $"Preserve Quest-only map: {label}",
                questMap.Identity));
        }

        foreach (var playlist in playlists)
        {
            plan.Add(new SyncOperation(
                SyncOperationKind.ImportPlaylist,
                $"Import playlist: {playlist.Name}",
                PlaylistName: playlist.Name));
        }

        return plan;
    }

    private static IReadOnlyList<QuestMapReference> CollectQuestMaps(QuestLibrary library)
    {
        var identified = library.Maps
            .Select(map => new QuestMapReference(map.Identity, map.SongName))
            .Concat(library.InstalledMaps
                .Where(map => map.IdentityStatus == QuestMapIdentityStatus.HashIdentified && map.Identity is not null)
                .Select(map => new QuestMapReference(map.Identity, map.SongTitle ?? map.FolderName)))
            .GroupBy(map => map.Identity!.Hash, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First());
        var unidentified = library.InstalledMaps
            .Where(map => map.IdentityStatus != QuestMapIdentityStatus.HashIdentified || map.Identity is null)
            .Select(map => new QuestMapReference(null, map.SongTitle ?? map.FolderName));
        return identified.Concat(unidentified).ToArray();
    }

    private static string UnresolvedEntryKey(PlaylistEntry entry) =>
        entry.Key ?? entry.SongName ?? "<anonymous>";

    private static string NormalizeHash(string hash) => hash.Trim().ToUpperInvariant();

    private static BeatSaverAvailability ResolveAvailability(IEnumerable<BeatSaverAvailability> values)
    {
        var availability = values.ToArray();
        if (availability.Contains(BeatSaverAvailability.Online))
        {
            return BeatSaverAvailability.Online;
        }

        return availability.All(value => value == BeatSaverAvailability.Unavailable)
            ? BeatSaverAvailability.Unavailable
            : BeatSaverAvailability.Unknown;
    }

    private sealed record QuestMapReference(BeatMapIdentity? Identity, string? Title);
}
