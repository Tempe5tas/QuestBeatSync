using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuestBeatSync.Core.Models;
using QuestBeatSync.Infrastructure.Abstractions;

namespace QuestBeatSync.Infrastructure.Backup;

public sealed class ContentAddressedArchiveStore : IArchiveStore
{
    private readonly string _root;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public ContentAddressedArchiveStore(string archiveRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveRoot);
        _root = Path.GetFullPath(archiveRoot);
    }

    public string CreateExecutionStagingDirectory(Guid executionId)
    {
        var path = UnderRoot("staging", executionId.ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public async Task<BlobCommitResult> CommitBlobAsync(string stagedFile, CancellationToken cancellationToken = default)
    {
        var (hash, size) = await HashAsync(stagedFile, cancellationToken).ConfigureAwait(false);
        var destination = BlobPath(hash);
        if (File.Exists(destination))
        {
            var existing = await HashAsync(destination, cancellationToken).ConfigureAwait(false);
            if (existing.Hash != hash || existing.Size != size)
                throw new InvalidDataException($"Archive blob {hash} does not match its content address.");
            return new(hash, size, false);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".new-" + Guid.NewGuid().ToString("N");
        try
        {
            await CopyAsync(stagedFile, temporary, cancellationToken).ConfigureAwait(false);
            var copied = await HashAsync(temporary, cancellationToken).ConfigureAwait(false);
            if (copied.Hash != hash || copied.Size != size) throw new InvalidDataException("Blob changed while it was being committed.");
            try { File.Move(temporary, destination, false); }
            catch (IOException) when (File.Exists(destination))
            {
                var raced = await HashAsync(destination, cancellationToken).ConfigureAwait(false);
                if (raced.Hash != hash || raced.Size != size) throw new InvalidDataException($"Archive blob {hash} is inconsistent.");
                return new(hash, size, false);
            }
            return new(hash, size, true);
        }
        finally { TryDeleteFile(temporary); }
    }

    public async Task PublishSnapshotAsync(ArchiveManifest manifest, string executionStagingDirectory, CancellationToken cancellationToken = default)
    {
        ValidateManifest(manifest);
        var stagingRoot = Path.GetFullPath(executionStagingDirectory);
        EnsureUnder(UnderRoot("staging"), stagingRoot);
        var publish = Path.Combine(stagingRoot, "snapshot");
        if (Directory.Exists(publish)) throw new IOException("Snapshot publication staging already exists.");
        Directory.CreateDirectory(publish);
        await using (var stream = new FileStream(Path.Combine(publish, "manifest.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            await JsonSerializer.SerializeAsync(stream, manifest, _json, cancellationToken).ConfigureAwait(false);
        var destination = UnderRoot("snapshots", ValidateSnapshotId(manifest.SnapshotId));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (Directory.Exists(destination)) throw new IOException("Snapshot destination already exists; refusing to merge.");
        Directory.Move(publish, destination);
    }

    public async Task<SnapshotVerificationResult> VerifySnapshotAsync(string snapshotId, CancellationToken cancellationToken = default)
    {
        var manifestPath = UnderRoot("snapshots", ValidateSnapshotId(snapshotId), "manifest.json");
        if (!File.Exists(manifestPath)) return new(SnapshotVerificationStatus.NotFound, "Snapshot manifest was not found.", []);
        ArchiveManifest? manifest;
        try
        {
            await using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            manifest = await JsonSerializer.DeserializeAsync<ArchiveManifest>(stream, _json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        { return new(SnapshotVerificationStatus.Corrupted, "Snapshot manifest is invalid.", [exception.Message]); }
        if (manifest is null) return new(SnapshotVerificationStatus.Corrupted, "Snapshot manifest is empty.", ["No manifest object."]);
        if (manifest.FormatVersion != ArchiveManifest.CurrentFormatVersion)
            return new(SnapshotVerificationStatus.UnsupportedFormat, "Snapshot format is not supported.", [$"Format {manifest.FormatVersion}."]);
        var errors = new List<string>();
        try { ValidateManifest(manifest); } catch (Exception exception) { errors.Add(exception.Message); }
        foreach (var entry in manifest.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Sha256.Length != 64 || !entry.Sha256.All(Uri.IsHexDigit) || !string.Equals(entry.BlobId, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            { errors.Add($"Invalid blob identity for {entry.LogicalRoot}/{entry.RelativePath}."); continue; }
            var blob = BlobPath(entry.BlobId.ToUpperInvariant());
            if (!File.Exists(blob)) { errors.Add($"Missing blob {entry.BlobId}."); continue; }
            var actual = await HashAsync(blob, cancellationToken).ConfigureAwait(false);
            if (actual.Size != entry.Size) errors.Add($"Wrong size for blob {entry.BlobId}.");
            if (!string.Equals(actual.Hash, entry.Sha256, StringComparison.OrdinalIgnoreCase)) errors.Add($"Wrong hash for blob {entry.BlobId}.");
        }
        return errors.Count == 0
            ? new(SnapshotVerificationStatus.Valid, "Snapshot verified successfully.", [])
            : new(SnapshotVerificationStatus.Corrupted, "Snapshot verification found corruption.", errors);
    }

    public async Task<IReadOnlyList<SnapshotSummary>> ListSnapshotsAsync(CancellationToken cancellationToken = default)
    {
        var snapshots = UnderRoot("snapshots");
        if (!Directory.Exists(snapshots)) return [];
        var results = new List<SnapshotSummary>();
        foreach (var directory in Directory.EnumerateDirectories(snapshots))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = Path.GetFileName(directory);
            var verification = await VerifySnapshotAsync(id, cancellationToken).ConfigureAwait(false);
            try
            {
                await using var stream = File.OpenRead(Path.Combine(directory, "manifest.json"));
                var manifest = await JsonSerializer.DeserializeAsync<ArchiveManifest>(stream, _json, cancellationToken).ConfigureAwait(false);
                if (manifest is null) continue;
                results.Add(new(manifest.CreatedAtUtc, manifest.SnapshotId, manifest.Device.Serial,
                    manifest.BeatSaber.VersionName,
                    manifest.Entries.Where(e => e.LogicalRoot == ArchiveLogicalRoot.CustomLevels)
                        .Select(e => e.RelativePath.Split('/')[0]).Distinct(StringComparer.Ordinal).Count(),
                    manifest.Entries.Count(e => e.LogicalRoot == ArchiveLogicalRoot.Playlists),
                    manifest.Entries.Sum(e => e.Size), verification.Status));
            }
            catch { /* malformed snapshots remain discoverable through explicit verification */ }
        }
        return results.OrderByDescending(item => item.CreatedAtUtc).ToArray();
    }

    public void CleanupStagingBestEffort(string executionStagingDirectory)
    {
        try
        {
            var full = Path.GetFullPath(executionStagingDirectory);
            EnsureUnder(UnderRoot("staging"), full);
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }
        catch { }
    }

    private void ValidateManifest(ArchiveManifest manifest)
    {
        if (manifest.FormatVersion != ArchiveManifest.CurrentFormatVersion) throw new InvalidDataException("Unsupported archive manifest format.");
        ValidateSnapshotId(manifest.SnapshotId);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in manifest.Entries)
            if (!paths.Add(ArchivePathSafety.LogicalKey(entry.LogicalRoot, entry.RelativePath)))
                throw new InvalidDataException($"Duplicate logical archive path: {entry.LogicalRoot}/{entry.RelativePath}");
    }

    private string BlobPath(string hash) => UnderRoot("blobs", "sha256", hash[..2], hash);
    private string UnderRoot(params string[] parts) { var path = Path.GetFullPath(Path.Combine([_root, .. parts])); EnsureUnder(_root, path); return path; }
    private static void EnsureUnder(string root, string candidate)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(normalizedRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Local archive path escaped its root.");
    }
    private static string ValidateSnapshotId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (id.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))) throw new InvalidDataException("Unsafe snapshot id.");
        return id;
    }
    private static async Task<(string Hash, long Size)> HashAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920]; long size = 0; int read;
        while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0) { hash.AppendData(buffer, 0, read); size += read; }
        return (Convert.ToHexString(hash.GetHashAndReset()), size);
    }
    private static async Task CopyAsync(string source, string destination, CancellationToken token)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        await input.CopyToAsync(output, 81920, token).ConfigureAwait(false);
        await output.FlushAsync(token).ConfigureAwait(false);
    }
    private static void TryDeleteFile(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
