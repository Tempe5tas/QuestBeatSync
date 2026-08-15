using System.Security.Cryptography;
using QuestBeatSync.Core.Models;
using QuestBeatSync.Infrastructure.Abstractions;

namespace QuestBeatSync.Infrastructure.Execution;

public sealed class LocalPlaylistSourceVerifier : IPlaylistSourceVerifier
{
    public async Task<bool> MatchesAsync(
        PlaylistSourceIdentity source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        try
        {
            await using var stream = new FileStream(
                source.CanonicalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return string.Equals(Convert.ToHexString(hash), source.ContentSha256, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
