using System.Security.Cryptography;
using QuestBeatSync.Core.Models;
using QuestBeatSync.Infrastructure.Abstractions;

namespace QuestBeatSync.Infrastructure.Execution;

public sealed class LocalPlaylistExecutionWorkspace : IPlaylistExecutionWorkspace
{
    private readonly string _executionsRoot;

    public LocalPlaylistExecutionWorkspace(string executionsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionsRoot);
        _executionsRoot = Path.GetFullPath(executionsRoot);
    }

    public async Task<PreparedPlaylistSource> PrepareAsync(
        Guid executionId,
        PlaylistSourceIdentity source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var playlistRoot = Path.Combine(_executionsRoot, executionId.ToString("N"), "playlists");
        Directory.CreateDirectory(playlistRoot);
        var snapshotPath = Path.Combine(playlistRoot, $"{source.ContentSha256}.bplist");

        try
        {
            if (!File.Exists(snapshotPath))
            {
                await using var original = new FileStream(
                    source.CanonicalPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var snapshot = new FileStream(
                    snapshotPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    FileOptions.Asynchronous);
                await original.CopyToAsync(snapshot, cancellationToken).ConfigureAwait(false);
                await snapshot.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            await using var verificationStream = new FileStream(
                snapshotPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var snapshotHash = Convert.ToHexString(
                await SHA256.HashDataAsync(verificationStream, cancellationToken).ConfigureAwait(false));
            if (!string.Equals(snapshotHash, source.ContentSha256, StringComparison.Ordinal))
            {
                throw new PlaylistSourceStaleException(source.CanonicalPath);
            }

            return new PreparedPlaylistSource(source.CanonicalPath, snapshotPath, snapshotHash);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PlaylistSourceStaleException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new PlaylistSourceStaleException(source.CanonicalPath, exception);
        }
    }

    public Task CleanupAsync(Guid executionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var executionRoot = Path.Combine(_executionsRoot, executionId.ToString("N"));
        if (Directory.Exists(executionRoot)) Directory.Delete(executionRoot, recursive: true);
        return Task.CompletedTask;
    }
}

public sealed class PlaylistSourceStaleException : Exception
{
    public PlaylistSourceStaleException(string sourcePath, Exception? innerException = null)
        : base($"Playlist source changed or could not be snapshotted: {sourcePath}", innerException)
    {
        SourcePath = sourcePath;
    }

    public string SourcePath { get; }
}
