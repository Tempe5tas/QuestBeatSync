namespace QuestBeatSync.Core.Models;

public enum QuestPlaylistFormat
{
    Bplist,
    Json,
    BmbfJson
}

public sealed record QuestInstalledPlaylist(
    string RemotePath,
    string Filename,
    string PlaylistTitle,
    int SongReferenceCount,
    QuestPlaylistFormat Format,
    string? Warning = null,
    IReadOnlyList<string>? NormalizedSongIdentities = null,
    bool SemanticIdentityComplete = false,
    string? FilenameLineage = null);

