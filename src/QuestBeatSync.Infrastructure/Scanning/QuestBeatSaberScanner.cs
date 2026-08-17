using QuestBeatSync.Core.Models;
using QuestBeatSync.Infrastructure.Abstractions;

namespace QuestBeatSync.Infrastructure.Scanning;

public sealed class QuestBeatSaberScanner : IQuestBeatSaberScanner
{
    private readonly IQuestRemoteFileSystem _fileSystem;
    private readonly QuestBeatSaberPaths _paths;

    public QuestBeatSaberScanner(
        IQuestRemoteFileSystem fileSystem,
        QuestBeatSaberPaths paths)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public async Task<QuestBeatSaberScanResult> ScanAsync(
        QuestDevice device,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);

        var beatSaberDetected = await _fileSystem.DirectoryExistsAsync(
            device,
            _paths.BeatSaberModData,
            cancellationToken).ConfigureAwait(false);
        var songCoreDetected = beatSaberDetected && await _fileSystem.DirectoryExistsAsync(
            device,
            _paths.SongCore,
            cancellationToken).ConfigureAwait(false);
        var customLevelsDetected = songCoreDetected && await _fileSystem.DirectoryExistsAsync(
            device,
            _paths.CustomLevels,
            cancellationToken).ConfigureAwait(false);
        var playlistManagerDetected = beatSaberDetected && await _fileSystem.DirectoryExistsAsync(
            device,
            _paths.PlaylistManager,
            cancellationToken).ConfigureAwait(false);
        var playlistsDirectoryDetected = playlistManagerDetected && await _fileSystem.DirectoryExistsAsync(
            device,
            _paths.Playlists,
            cancellationToken).ConfigureAwait(false);

        var warnings = new List<QuestScanWarning>();
        var mapScan = customLevelsDetected
            ? await ScanMapsAsync(device, warnings, cancellationToken).ConfigureAwait(false)
            : new MapScanResult([], 0);
        var playlists = playlistsDirectoryDetected
            ? await ScanPlaylistsAsync(device, warnings, cancellationToken).ConfigureAwait(false)
            : [];

        return new QuestBeatSaberScanResult(
            beatSaberDetected,
            songCoreDetected,
            customLevelsDetected,
            playlistManagerDetected,
            playlistsDirectoryDetected,
            mapScan.Maps,
            playlists,
            warnings,
            mapScan.FolderCount);
    }

    private async Task<MapScanResult> ScanMapsAsync(
        QuestDevice device,
        ICollection<QuestScanWarning> warnings,
        CancellationToken cancellationToken)
    {
        var folders = await _fileSystem.ListDirectoriesAsync(
            device,
            _paths.CustomLevels,
            cancellationToken).ConfigureAwait(false);
        var maps = new List<QuestInstalledMap>(folders.Count);

        foreach (var folderPath in folders)
        {
            if (GetRemoteName(folderPath).StartsWith(".qbsync-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            maps.Add(await ScanMapAsync(device, folderPath, warnings, cancellationToken).ConfigureAwait(false));
        }

        return new MapScanResult(maps, folders.Count);
    }

    private async Task<QuestInstalledMap> ScanMapAsync(
        QuestDevice device,
        string folderPath,
        ICollection<QuestScanWarning> warnings,
        CancellationToken cancellationToken)
    {
        var folderName = GetRemoteName(folderPath);
        IReadOnlyList<string> files;

        try
        {
            files = await _fileSystem.ListFilesAsync(device, folderPath, cancellationToken).ConfigureAwait(false);
        }
        catch (QuestRemoteFileSystemException exception)
        {
            warnings.Add(new QuestScanWarning(folderPath, exception.Message));
            return new QuestInstalledMap(
                folderPath,
                folderName,
                false,
                null,
                null,
                QuestMapIdentityStatus.Unknown,
                Warning: exception.Message);
        }

        var infoDatPath = files.FirstOrDefault(path =>
            string.Equals(GetRemoteName(path), "Info.dat", StringComparison.OrdinalIgnoreCase));
        if (infoDatPath is null)
        {
            const string message = "Info.dat was not found.";
            warnings.Add(new QuestScanWarning(folderPath, message));
            return new QuestInstalledMap(
                folderPath,
                folderName,
                false,
                null,
                null,
                QuestMapIdentityStatus.Unknown,
                Warning: message);
        }

        try
        {
            var json = await _fileSystem.ReadTextFileAsync(
                device,
                infoDatPath,
                cancellationToken).ConfigureAwait(false);
            if (!InfoDatParser.TryParse(json, out var metadata, out var warning))
            {
                warnings.Add(new QuestScanWarning(infoDatPath, warning!));
                return new QuestInstalledMap(
                    folderPath,
                    folderName,
                    true,
                    null,
                    null,
                    QuestMapIdentityStatus.Unknown,
                    Warning: warning);
            }

            var identity = TryGetIdentityFromFolderName(folderName);

            return new QuestInstalledMap(
                folderPath,
                folderName,
                true,
                metadata.SongTitle,
                metadata.Mapper,
                identity is null
                    ? QuestMapIdentityStatus.LocalOnly
                    : QuestMapIdentityStatus.HashIdentified,
                identity);
        }
        catch (QuestRemoteFileSystemException exception)
        {
            warnings.Add(new QuestScanWarning(infoDatPath, exception.Message));
            return new QuestInstalledMap(
                folderPath,
                folderName,
                true,
                null,
                null,
                QuestMapIdentityStatus.Unknown,
                Warning: exception.Message);
        }
    }

    private async Task<IReadOnlyList<QuestInstalledPlaylist>> ScanPlaylistsAsync(
        QuestDevice device,
        ICollection<QuestScanWarning> warnings,
        CancellationToken cancellationToken)
    {
        var files = await _fileSystem.ListFilesAsync(
            device,
            _paths.Playlists,
            cancellationToken).ConfigureAwait(false);
        var playlists = new List<QuestInstalledPlaylist>();

        foreach (var remotePath in files)
        {
            var filename = GetRemoteName(remotePath);
            var format = GetPlaylistFormat(filename);
            if (format is null)
            {
                continue;
            }

            playlists.Add(await ScanPlaylistAsync(
                device,
                remotePath,
                filename,
                format.Value,
                warnings,
                cancellationToken).ConfigureAwait(false));
        }

        return playlists;
    }

    private async Task<QuestInstalledPlaylist> ScanPlaylistAsync(
        QuestDevice device,
        string remotePath,
        string filename,
        QuestPlaylistFormat format,
        ICollection<QuestScanWarning> warnings,
        CancellationToken cancellationToken)
    {
        try
        {
            var json = await _fileSystem.ReadTextFileAsync(
                device,
                remotePath,
                cancellationToken).ConfigureAwait(false);
            PlaylistFileParser.TryParse(filename, json, out var metadata, out var warning);
            if (warning is not null)
            {
                warnings.Add(new QuestScanWarning(remotePath, warning));
            }

            return new QuestInstalledPlaylist(
                remotePath,
                filename,
                metadata.PlaylistTitle,
                metadata.SongReferenceCount,
                format,
                metadata.Warning);
        }
        catch (QuestRemoteFileSystemException exception)
        {
            warnings.Add(new QuestScanWarning(remotePath, exception.Message));
            return new QuestInstalledPlaylist(
                remotePath,
                filename,
                GetFallbackPlaylistTitle(filename),
                0,
                format,
                exception.Message);
        }
    }

    private static QuestPlaylistFormat? GetPlaylistFormat(string filename)
    {
        if (filename.EndsWith(".bplist", StringComparison.OrdinalIgnoreCase))
        {
            return QuestPlaylistFormat.Bplist;
        }

        if (filename.EndsWith("_BMBF.json", StringComparison.OrdinalIgnoreCase))
        {
            return QuestPlaylistFormat.BmbfJson;
        }

        return filename.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? QuestPlaylistFormat.Json
            : null;
    }

    private static string GetFallbackPlaylistTitle(string filename)
    {
        const string bmbfSuffix = "_BMBF.json";
        return filename.EndsWith(bmbfSuffix, StringComparison.OrdinalIgnoreCase)
            ? filename[..^bmbfSuffix.Length]
            : Path.GetFileNameWithoutExtension(filename);
    }

    private static BeatMapIdentity? TryGetIdentityFromFolderName(string folderName) =>
        BeatSaverHash.TryNormalize(folderName, out var hash)
            ? new BeatMapIdentity(hash)
            : null;

    private static string GetRemoteName(string remotePath)
    {
        var normalized = remotePath.TrimEnd('/');
        var separatorIndex = normalized.LastIndexOf('/');
        return separatorIndex < 0 ? normalized : normalized[(separatorIndex + 1)..];
    }

    private sealed record MapScanResult(IReadOnlyList<QuestInstalledMap> Maps, int FolderCount);
}
