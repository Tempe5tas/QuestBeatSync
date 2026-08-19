using System.Security.Cryptography;
using System.Text;
using QuestBeatSync.Core.Models;

namespace QuestBeatSync.Infrastructure.Execution;

public static class QuestExecutionPaths
{
    public static string WriterLock(QuestBeatSaberPaths paths) =>
        Join(paths.BeatSaberModData, ".qbsync-write-lock");

    public static string MapFinal(QuestBeatSaberPaths paths, BeatMapIdentity identity) =>
        Join(paths.CustomLevels, identity.Hash);

    public static string MapStaging(QuestBeatSaberPaths paths, BeatMapIdentity identity, Guid executionId) =>
        Join(paths.CustomLevels, $".qbsync-{identity.Hash}-{executionId:N}");

    public static string PlaylistFinal(QuestBeatSaberPaths paths, PreparedPlaylistSource source) =>
        Join(paths.Playlists, BuildManagedPlaylistFileName(source.OriginalCanonicalPath));

    public static string PlaylistStaging(QuestBeatSaberPaths paths, PreparedPlaylistSource source, Guid transferId) =>
        Join(paths.Playlists, $".qbsync-playlist-{StableSourceId(source.OriginalCanonicalPath)}-{transferId:N}.tmp");

    public static string BuildManagedPlaylistFileName(string canonicalSourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalSourcePath);
        var basename = Path.GetFileName(canonicalSourcePath);
        if (string.IsNullOrWhiteSpace(basename) || basename is "." or ".." ||
            !basename.EndsWith(".bplist", StringComparison.OrdinalIgnoreCase) ||
            basename.Any(character => character == '\0' || char.IsControl(character) || character is '/' or '\\'))
            throw new InvalidOperationException("Playlist source basename is unsafe for the Quest PlaylistManager ingress directory.");
        return basename;
    }

    public static bool IsOwnedMapStagingPath(QuestBeatSaberPaths paths, string remotePath) =>
        TryParseOwnedMapStagingPath(paths, remotePath, out _, out _);

    public static bool TryParseOwnedMapStagingPath(
        QuestBeatSaberPaths paths,
        string remotePath,
        out BeatMapIdentity? identity,
        out Guid executionId)
    {
        identity = null;
        executionId = default;
        if (!TryGetDirectChild(paths.CustomLevels, remotePath, out var name) ||
            name.Length != 81 ||
            !name.StartsWith(".qbsync-", StringComparison.Ordinal) ||
            !BeatSaverHash.IsValid(name.Substring(8, 40)) ||
            name[48] != '-' ||
            !Guid.TryParseExact(name[49..], "N", out executionId))
        {
            return false;
        }

        identity = new BeatMapIdentity(name.Substring(8, 40));
        return true;
    }

    public static bool TryParseMapFinalPath(
        QuestBeatSaberPaths paths,
        string remotePath,
        out BeatMapIdentity? identity)
    {
        identity = null;
        if (!TryGetDirectChild(paths.CustomLevels, remotePath, out var name) || !BeatSaverHash.IsValid(name))
            return false;
        identity = new BeatMapIdentity(name);
        return true;
    }

    public static bool IsOwnedPlaylistStagingPath(QuestBeatSaberPaths paths, string remotePath) =>
        TryGetDirectChild(paths.Playlists, remotePath, out var name) &&
        name.Length == 70 &&
        name.StartsWith(".qbsync-playlist-", StringComparison.Ordinal) &&
        name.Substring(17, 16).All(Uri.IsHexDigit) &&
        name[33] == '-' &&
        name.Substring(34, 32).All(Uri.IsHexDigit) &&
        name.EndsWith(".tmp", StringComparison.Ordinal);

    private static string StableSourceId(string canonicalPath)
    {
        var stablePath = OperatingSystem.IsWindows() ? canonicalPath.ToUpperInvariant() : canonicalPath;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stablePath)))[..16];
    }

    private static bool TryGetDirectChild(string parent, string path, out string name)
    {
        var normalizedParent = parent.TrimEnd('/');
        if (!path.StartsWith($"{normalizedParent}/", StringComparison.Ordinal))
        {
            name = string.Empty;
            return false;
        }

        name = path[(normalizedParent.Length + 1)..];
        return !name.Contains('/');
    }

    private static string Join(string parent, string child) => $"{parent.TrimEnd('/')}/{child}";
}
