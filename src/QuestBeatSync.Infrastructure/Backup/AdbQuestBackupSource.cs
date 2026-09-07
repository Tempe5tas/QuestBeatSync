using QuestBeatSync.Core.Models;
using QuestBeatSync.Infrastructure.Abstractions;
using QuestBeatSync.Infrastructure.Adb;
using QuestBeatSync.Infrastructure.Execution;

namespace QuestBeatSync.Infrastructure.Backup;

public sealed class AdbQuestBackupSource(IQuestTransport transport, QuestBeatSaberPaths paths) : IQuestBackupSource
{
    private sealed record Root(ArchiveLogicalRoot LogicalRoot, string RemotePath, bool Optional, bool SingleFile = false);

    public async Task<IReadOnlyList<QuestBackupSourceFile>> DiscoverAsync(QuestDevice device, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        var roots = new[]
        {
            new Root(ArchiveLogicalRoot.CustomLevels, paths.CustomLevels, false),
            new Root(ArchiveLogicalRoot.Playlists, paths.Playlists, false),
            new Root(ArchiveLogicalRoot.Covers, $"{paths.PlaylistManager}/Covers", true),
            new Root(ArchiveLogicalRoot.SongCoreAuxiliary, $"{paths.SongCore}/CachedSongData.json", true, true)
        };
        var discovered = new List<QuestBackupSourceFile>();
        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var script = root.SingleFile
                ? $"test -f {PosixShellEscaping.Quote(root.RemotePath)} && printf '%s\\0' {PosixShellEscaping.Quote(root.RemotePath)}"
                : $"test ! -d {PosixShellEscaping.Quote(root.RemotePath)} || find {PosixShellEscaping.Quote(root.RemotePath)} -type f -print0";
            var result = await transport.ExecuteShellAsync(device, ["sh", "-c", script], cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                if (root.Optional && result.AdbAvailable && !result.TimedOut && result.ExitCode == 1) continue;
                throw new IOException(SelectError(result, $"Could not enumerate backup root {root.RemotePath}."));
            }
            foreach (var remotePath in result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var normalizedRemote = remotePath;
                var relative = root.SingleFile ? Path.GetFileName(root.RemotePath) : GetRelativeRemotePath(root.RemotePath, normalizedRemote);
                relative = ArchivePathSafety.NormalizeRelativePath(relative);
                if (root.LogicalRoot == ArchiveLogicalRoot.CustomLevels && IsOwnedMapStaging(normalizedRemote)) continue;
                if (root.LogicalRoot == ArchiveLogicalRoot.Playlists && QuestExecutionPaths.IsOwnedPlaylistStagingPath(paths, normalizedRemote)) continue;
                discovered.Add(new(root.LogicalRoot, relative, normalizedRemote));
            }
        }
        return discovered.OrderBy(file => file.LogicalRoot).ThenBy(file => file.RelativePath, StringComparer.Ordinal).ToArray();
    }

    public async Task PullAsync(QuestDevice device, QuestBackupSourceFile source, string safeLocalPath, CancellationToken cancellationToken = default)
    {
        var result = await transport.PullAsync(device, source.RemotePath, safeLocalPath, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess) throw new IOException(SelectError(result, $"Could not pull {source.RemotePath}."));
        if (!File.Exists(safeLocalPath)) throw new IOException($"ADB reported success but did not create a local file for {source.RemotePath}.");
    }

    private bool IsOwnedMapStaging(string remoteFile)
    {
        var prefix = paths.CustomLevels.TrimEnd('/') + "/";
        if (!remoteFile.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var slash = remoteFile.IndexOf('/', prefix.Length);
        var folder = slash < 0 ? remoteFile : remoteFile[..slash];
        return QuestExecutionPaths.IsOwnedMapStagingPath(paths, folder);
    }

    private static string GetRelativeRemotePath(string root, string path)
    {
        var prefix = root.TrimEnd('/') + "/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("Remote enumeration escaped its approved backup root.");
        return path[prefix.Length..];
    }

    private static string SelectError(AdbCommandResult result, string fallback) =>
        result.TimedOut ? "ADB backup command timed out." :
        !result.AdbAvailable ? "ADB is unavailable." :
        !string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardError.Trim() : fallback;
}
