namespace QuestBeatSync.Core.Models;

public sealed record QuestBeatSaberPaths(
    string BeatSaberModData,
    string SongCore,
    string CustomLevels,
    string PlaylistManager,
    string Playlists)
{
    public static QuestBeatSaberPaths Default { get; } = new(
        "/sdcard/ModData/com.beatgames.beatsaber",
        "/sdcard/ModData/com.beatgames.beatsaber/Mods/SongCore",
        "/sdcard/ModData/com.beatgames.beatsaber/Mods/SongCore/CustomLevels",
        "/sdcard/ModData/com.beatgames.beatsaber/Mods/PlaylistManager",
        "/sdcard/ModData/com.beatgames.beatsaber/Mods/PlaylistManager/Playlists");
}

