namespace QuestBeatSync.Core.Models;

public sealed class QuestLibrary
{
    public QuestLibrary(
        IEnumerable<BeatMap>? maps = null,
        IEnumerable<Playlist>? playlists = null,
        IEnumerable<QuestInstalledMap>? installedMaps = null)
    {
        Maps = (maps ?? []).ToArray();
        Playlists = (playlists ?? []).ToArray();
        InstalledMaps = (installedMaps ?? []).ToArray();
    }

    public IReadOnlyList<BeatMap> Maps { get; }

    public IReadOnlyList<Playlist> Playlists { get; }

    public IReadOnlyList<QuestInstalledMap> InstalledMaps { get; }
}
