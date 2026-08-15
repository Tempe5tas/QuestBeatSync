using System.Text;
using QuestBeatSync.Infrastructure.Abstractions;

namespace QuestBeatSync.Infrastructure.Importing;

public sealed class LocalBplistImporter : ILocalPlaylistImporter
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public async Task<IReadOnlyList<PlaylistImportResult>> ImportAsync(
        IEnumerable<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        var results = new List<PlaylistImportResult>();

        foreach (var filePath in filePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await ImportOneAsync(filePath, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    private static async Task<PlaylistImportResult> ImportOneAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return new PlaylistImportResult(string.Empty, null, "Playlist path is empty.");
        }

        if (!filePath.EndsWith(".bplist", StringComparison.OrdinalIgnoreCase))
        {
            return new PlaylistImportResult(filePath, null, "Only .bplist files can be imported.");
        }

        try
        {
            var json = await File.ReadAllTextAsync(filePath, StrictUtf8, cancellationToken).ConfigureAwait(false);
            var playlist = await Task.Run(
                () => BplistParser.Parse(json, filePath),
                cancellationToken).ConfigureAwait(false);
            return new PlaylistImportResult(filePath, playlist, null);
        }
        catch (BplistParseException exception)
        {
            return new PlaylistImportResult(filePath, null, exception.Message);
        }
        catch (DecoderFallbackException exception)
        {
            return new PlaylistImportResult(filePath, null, $"Playlist is not valid UTF-8: {exception.Message}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new PlaylistImportResult(filePath, null, $"Could not read playlist: {exception.Message}");
        }
    }
}
