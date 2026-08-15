using System.IO.Compression;
using System.Text.Json;
using QuestBeatSync.Core.Models;
using QuestBeatSync.Infrastructure.Abstractions;

namespace QuestBeatSync.Infrastructure.Cache;

public sealed class LocalBeatMapCache : IBeatMapCache
{
    private const string CompletionMarker = ".qbsync-complete";
    private readonly string _mapsRoot;
    private readonly IBeatSaverClient _beatSaverClient;

    public LocalBeatMapCache(string mapsRoot, IBeatSaverClient beatSaverClient)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapsRoot);
        _mapsRoot = Path.GetFullPath(mapsRoot);
        _beatSaverClient = beatSaverClient ?? throw new ArgumentNullException(nameof(beatSaverClient));
    }

    public Task<bool> IsCachedAsync(string hash, CancellationToken cancellationToken = default)
    {
        string normalizedHash;
        try
        {
            normalizedHash = ValidateHash(hash);
        }
        catch (ArgumentException)
        {
            return Task.FromResult(false);
        }

        return Task.Run(() => IsCompleteCacheDirectory(GetTargetPath(normalizedHash)), cancellationToken);
    }

    public async Task<BeatMapCacheResult> CacheAsync(
        BeatSaverLookupResult lookup,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        if (!lookup.CanDownload)
        {
            return new BeatMapCacheResult(BeatMapCacheOutcome.Failed, ErrorMessage: "Map is not available for download.");
        }

        string normalizedHash;
        try
        {
            normalizedHash = ValidateHash(lookup.ResolvedHash!);
        }
        catch (ArgumentException exception)
        {
            return new BeatMapCacheResult(BeatMapCacheOutcome.Failed, ErrorMessage: exception.Message);
        }

        var targetPath = GetTargetPath(normalizedHash);
        if (IsCompleteCacheDirectory(targetPath))
        {
            return new BeatMapCacheResult(BeatMapCacheOutcome.AlreadyCached, targetPath);
        }

        if (Directory.Exists(targetPath))
        {
            return new BeatMapCacheResult(
                BeatMapCacheOutcome.Failed,
                ErrorMessage: "An incomplete cache directory already exists and was preserved for inspection.");
        }

        Directory.CreateDirectory(_mapsRoot);
        var workPath = Path.Combine(_mapsRoot, $".qbsync-{normalizedHash}-{Guid.NewGuid():N}.tmp");
        var zipPath = Path.Combine(workPath, "map.zip");
        var extractPath = Path.Combine(workPath, "extract");

        try
        {
            Directory.CreateDirectory(extractPath);
            await using (var destination = new FileStream(
                             zipPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             FileOptions.Asynchronous))
            {
                await _beatSaverClient.DownloadZipAsync(
                    lookup.DownloadUri!,
                    destination,
                    cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            ZipFile.ExtractToDirectory(zipPath, extractPath);
            var infoPath = Directory.EnumerateFiles(extractPath, "*", SearchOption.AllDirectories)
                .FirstOrDefault(path => string.Equals(
                    Path.GetFileName(path),
                    "Info.dat",
                    StringComparison.OrdinalIgnoreCase));
            if (infoPath is null)
            {
                return new BeatMapCacheResult(BeatMapCacheOutcome.Failed, ErrorMessage: "Downloaded map contains no Info.dat.");
            }

            await ValidateInfoDatAsync(infoPath, cancellationToken).ConfigureAwait(false);
            var mapDirectory = Path.GetDirectoryName(infoPath)!;
            if (!Directory.EnumerateFiles(mapDirectory, "*", SearchOption.AllDirectories)
                    .Any(path => !string.Equals(path, infoPath, StringComparison.OrdinalIgnoreCase)))
            {
                return new BeatMapCacheResult(
                    BeatMapCacheOutcome.Failed,
                    ErrorMessage: "Downloaded map has Info.dat but no song or beatmap assets.");
            }

            await File.WriteAllTextAsync(
                Path.Combine(mapDirectory, CompletionMarker),
                normalizedHash,
                cancellationToken).ConfigureAwait(false);

            Directory.Move(mapDirectory, targetPath);
            return new BeatMapCacheResult(BeatMapCacheOutcome.Cached, targetPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new BeatMapCacheResult(BeatMapCacheOutcome.Failed, ErrorMessage: exception.Message);
        }
        finally
        {
            if (Directory.Exists(workPath))
            {
                try
                {
                    Directory.Delete(workPath, recursive: true);
                }
                catch (IOException)
                {
                    // A leftover .tmp directory is never treated as a completed map.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private static async Task ValidateInfoDatAsync(string infoPath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(infoPath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Info.dat is not a JSON object.");
        }
    }

    private bool IsCompleteCacheDirectory(string path)
    {
        try
        {
            var markerPath = Path.Combine(path, CompletionMarker);
            if (!Directory.Exists(path) || !File.Exists(markerPath))
            {
                return false;
            }

            var expectedHash = Path.GetFileName(path);
            if (!string.Equals(File.ReadAllText(markerPath).Trim(), expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).ToArray();
            return files.Any(file => string.Equals(Path.GetFileName(file), "Info.dat", StringComparison.OrdinalIgnoreCase)) &&
                   files.Any(file => !string.Equals(file, markerPath, StringComparison.OrdinalIgnoreCase) &&
                                     !string.Equals(Path.GetFileName(file), "Info.dat", StringComparison.OrdinalIgnoreCase));
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private string GetTargetPath(string normalizedHash) => Path.Combine(_mapsRoot, normalizedHash);

    private static string ValidateHash(string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        var normalized = hash.Trim().ToUpperInvariant();
        if (normalized.Length != 40 || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("BeatSaver hash must be a 40-character SHA1 value.", nameof(hash));
        }

        return normalized;
    }
}
