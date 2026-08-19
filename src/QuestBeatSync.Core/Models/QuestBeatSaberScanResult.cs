namespace QuestBeatSync.Core.Models;

public sealed record QuestScanWarning(string RemotePath, string Message);

public sealed class QuestBeatSaberScanResult
{
    public QuestBeatSaberScanResult(
        bool beatSaberDetected,
        bool songCoreDetected,
        bool customLevelsDetected,
        bool playlistManagerDetected,
        bool playlistsDirectoryDetected,
        IEnumerable<QuestInstalledMap>? installedMaps = null,
        IEnumerable<QuestInstalledPlaylist>? installedPlaylists = null,
        IEnumerable<QuestScanWarning>? warnings = null,
        int? customLevelFolderCount = null,
        BeatSaberPackageVersion? beatSaberPackageVersion = null)
    {
        BeatSaberDetected = beatSaberDetected;
        SongCoreDetected = songCoreDetected;
        CustomLevelsDetected = customLevelsDetected;
        PlaylistManagerDetected = playlistManagerDetected;
        PlaylistsDirectoryDetected = playlistsDirectoryDetected;
        InstalledMaps = (installedMaps ?? []).ToArray();
        InstalledPlaylists = (installedPlaylists ?? []).ToArray();
        Warnings = (warnings ?? []).ToArray();
        CustomLevelFolderCount = customLevelFolderCount ?? InstalledMaps.Count;
        BeatSaberPackageVersion = beatSaberPackageVersion;
    }

    public bool BeatSaberDetected { get; }

    public bool SongCoreDetected { get; }

    public bool CustomLevelsDetected { get; }

    public bool PlaylistManagerDetected { get; }

    public bool PlaylistsDirectoryDetected { get; }

    public IReadOnlyList<QuestInstalledMap> InstalledMaps { get; }

    public IReadOnlyList<QuestInstalledPlaylist> InstalledPlaylists { get; }

    public IReadOnlyList<QuestScanWarning> Warnings { get; }

    public int CustomLevelFolderCount { get; }

    public BeatSaberPackageVersion? BeatSaberPackageVersion { get; }

    public int CustomSongCount => InstalledMaps.Count;

    public int PlaylistCount => InstalledPlaylists.Count;

    public static QuestBeatSaberScanResult Empty { get; } =
        new(false, false, false, false, false);
}
