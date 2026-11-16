using QuestBeatSync.Core.Models;
using QuestBeatSync.Infrastructure.Abstractions;

namespace QuestBeatSync.Infrastructure.Scanning;

public sealed class QuestBeatSaberScanner : IQuestBeatSaberScanner
{
    private readonly IQuestRemoteFileSystem _fileSystem;
    private readonly QuestBeatSaberPaths _paths;
    private readonly IBeatSaberPackageInspector? _packageInspector;

    public QuestBeatSaberScanner(
        IQuestRemoteFileSystem fileSystem,
        QuestBeatSaberPaths paths,
        IBeatSaberPackageInspector? packageInspector = null)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _packageInspector = packageInspector;
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
        var packageVersion = beatSaberDetected && _packageInspector is not null
            ? await _packageInspector.InspectAsync(device, cancellationToken).ConfigureAwait(false)
            : null;
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
            mapScan.FolderCount,
            packageVersion);
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

        var validFolders = folders
            .Where(folderPath => !GetRemoteName(folderPath).StartsWith(".qbsync-", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        IReadOnlyList<string> infoDatFiles;
        try
        {
            infoDatFiles = await _fileSystem.FindFilesAsync(
                device,
                _paths.CustomLevels,
                2,
                2,
                "Info.dat",
                cancellationToken).ConfigureAwait(false);
        }
        catch (QuestRemoteFileSystemException exception)
        {
            warnings.Add(new QuestScanWarning(_paths.CustomLevels, exception.Message));
            infoDatFiles = [];
        }

        var folderToInfoDat = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var infoPath in infoDatFiles)
        {
            var folder = GetParentRemotePath(infoPath);
            folderToInfoDat.TryAdd(folder, infoPath);
        }

        var filesToRead = validFolders
            .Where(f => folderToInfoDat.ContainsKey(f))
            .Select(f => folderToInfoDat[f])
            .ToArray();

        IReadOnlyDictionary<string, string> fileContents;
        try
        {
            fileContents = await _fileSystem.ReadTextFilesAsync(
                device,
                filesToRead,
                cancellationToken).ConfigureAwait(false);
        }
        catch (QuestRemoteFileSystemException exception)
        {
            warnings.Add(new QuestScanWarning(_paths.CustomLevels, exception.Message));
            fileContents = new Dictionary<string, string>();
        }

        var maps = new List<QuestInstalledMap>(validFolders.Length);
        foreach (var folderPath in validFolders)
        {
            var folderName = GetRemoteName(folderPath);
            if (!folderToInfoDat.TryGetValue(folderPath, out var infoDatPath))
            {
                const string message = "Info.dat was not found.";
                warnings.Add(new QuestScanWarning(folderPath, message));
                maps.Add(new QuestInstalledMap(
                    folderPath,
                    folderName,
                    false,
                    null,
                    null,
                    QuestMapIdentityStatus.Unknown,
                    Warning: message));
                continue;
            }

            if (!fileContents.TryGetValue(infoDatPath, out var json))
            {
                var message = $"Could not read {infoDatPath}.";
                warnings.Add(new QuestScanWarning(infoDatPath, message));
                maps.Add(new QuestInstalledMap(
                    folderPath,
                    folderName,
                    true,
                    null,
                    null,
                    QuestMapIdentityStatus.Unknown,
                    Warning: message));
                continue;
            }

            if (!InfoDatParser.TryParse(json, out var metadata, out var parseWarning))
            {
                warnings.Add(new QuestScanWarning(infoDatPath, parseWarning!));
                maps.Add(new QuestInstalledMap(
                    folderPath,
                    folderName,
                    true,
                    null,
                    null,
                    QuestMapIdentityStatus.Unknown,
                    Warning: parseWarning));
                continue;
            }

            var identity = TryGetIdentityFromFolderName(folderName);
            maps.Add(new QuestInstalledMap(
                folderPath,
                folderName,
                true,
                metadata.SongTitle,
                metadata.Mapper,
                identity is null
                    ? QuestMapIdentityStatus.LocalOnly
                    : QuestMapIdentityStatus.HashIdentified,
                identity,
                Format: metadata.Format));
        }

        return new MapScanResult(maps, folders.Count);
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

        var playlistFiles = new List<(string RemotePath, string Filename, QuestPlaylistFormat Format)>();
        foreach (var remotePath in files)
        {
            var filename = GetRemoteName(remotePath);
            var format = GetPlaylistFormat(filename);
            if (format is not null)
            {
                playlistFiles.Add((remotePath, filename, format.Value));
            }
        }

        if (playlistFiles.Count == 0) return [];

        IReadOnlyDictionary<string, string> fileContents;
        try
        {
            fileContents = await _fileSystem.ReadTextFilesAsync(
                device,
                playlistFiles.Select(p => p.RemotePath).ToArray(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (QuestRemoteFileSystemException exception)
        {
            warnings.Add(new QuestScanWarning(_paths.Playlists, exception.Message));
            fileContents = new Dictionary<string, string>();
        }

        var playlists = new List<QuestInstalledPlaylist>(playlistFiles.Count);
        foreach (var (remotePath, filename, format) in playlistFiles)
        {
            if (!fileContents.TryGetValue(remotePath, out var json))
            {
                var message = $"Could not read {remotePath}.";
                warnings.Add(new QuestScanWarning(remotePath, message));
                playlists.Add(new QuestInstalledPlaylist(
                    remotePath,
                    filename,
                    GetFallbackPlaylistTitle(filename),
                    0,
                    format,
                    message,
                    [],
                    false,
                    PlaylistFileParser.GetFilenameLineage(filename)));
                continue;
            }

            PlaylistFileParser.TryParse(filename, json, out var metadata, out var warning);
            if (warning is not null)
            {
                warnings.Add(new QuestScanWarning(remotePath, warning));
            }

            playlists.Add(new QuestInstalledPlaylist(
                remotePath,
                filename,
                metadata.PlaylistTitle,
                metadata.SongReferenceCount,
                format,
                metadata.Warning,
                metadata.NormalizedSongIdentities,
                metadata.SemanticIdentityComplete,
                metadata.FilenameLineage));
        }

        return playlists
            .GroupBy(PlaylistLogicalKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.Format == QuestPlaylistFormat.BmbfJson).First())
            .ToArray();
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

    private static string PlaylistLogicalKey(QuestInstalledPlaylist playlist)
    {
        var semantic = playlist.SemanticIdentityComplete
            ? string.Join("|", playlist.NormalizedSongIdentities ?? [])
            : $"path:{playlist.RemotePath}";
        return $"{playlist.PlaylistTitle.Trim()}\n{semantic}";
    }

    private static string GetRemoteName(string remotePath)
    {
        var normalized = remotePath.TrimEnd('/');
        var separatorIndex = normalized.LastIndexOf('/');
        return separatorIndex < 0 ? normalized : normalized[(separatorIndex + 1)..];
    }

    private static string GetParentRemotePath(string remotePath)
    {
        var normalized = remotePath.TrimEnd('/');
        var separatorIndex = normalized.LastIndexOf('/');
        return separatorIndex < 0 ? string.Empty : normalized[..separatorIndex];
    }

    private sealed record MapScanResult(IReadOnlyList<QuestInstalledMap> Maps, int FolderCount);
}
