namespace QuestBeatSync.Core.Models;

public sealed class QuestLibrary
{
    public QuestLibrary(
        IEnumerable<BeatMap>? maps = null,
        IEnumerable<Playlist>? playlists = null)
    {
        Maps = (maps ?? []).ToArray();
        Playlists = (playlists ?? []).ToArray();
    }

    public IReadOnlyList<BeatMap> Maps { get; }

    public IReadOnlyList<Playlist> Playlists { get; }
}

