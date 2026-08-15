using System.Security.Cryptography;
using System.Text;
using QuestBeatSync.Core.Models;

namespace QuestBeatSync.Infrastructure.Execution;

public static class QuestExecutionPaths
{
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
        var safeName = MakeSafeName(Path.GetFileNameWithoutExtension(canonicalSourcePath));
        return $"qbsync-{StableSourceId(canonicalSourcePath)}-{safeName}.bplist";
    }

    public static bool IsOwnedMapStagingPath(QuestBeatSaberPaths paths, string remotePath) =>
        TryGetDirectChild(paths.CustomLevels, remotePath, out var name) &&
        name.Length == 81 &&
        name.StartsWith(".qbsync-", StringComparison.Ordinal) &&
        BeatSaverHash.IsValid(name.Substring(8, 40)) &&
        name[48] == '-' &&
        name[49..].All(Uri.IsHexDigit);

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

    private static string MakeSafeName(string value)
    {
        var characters = value.Select(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '-').ToArray();
        var normalized = new string(characters).Trim('-');
        return string.IsNullOrEmpty(normalized) ? "playlist" : normalized[..Math.Min(normalized.Length, 48)];
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
