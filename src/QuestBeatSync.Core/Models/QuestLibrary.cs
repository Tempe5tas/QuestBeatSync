namespace QuestBeatSync.Core.Models;

public sealed class QuestLibrary
{
    public QuestLibrary(
        IEnumerable<QuestInstalledMap>? installedMaps = null,
        IEnumerable<QuestInstalledPlaylist>? installedPlaylists = null)
    {
        InstalledMaps = (installedMaps ?? []).ToArray();
        InstalledPlaylists = (installedPlaylists ?? []).ToArray();
    }

    public IReadOnlyList<QuestInstalledMap> InstalledMaps { get; }

    public IReadOnlyList<QuestInstalledPlaylist> InstalledPlaylists { get; }
}
