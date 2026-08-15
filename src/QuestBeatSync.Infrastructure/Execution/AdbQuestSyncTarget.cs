using QuestBeatSync.Core.Models;
using QuestBeatSync.Infrastructure.Abstractions;
using QuestBeatSync.Infrastructure.Scanning;
using System.Collections.Concurrent;

namespace QuestBeatSync.Infrastructure.Execution;

public sealed class AdbQuestSyncTarget : IQuestSyncTarget
{
    private const string BeatSaberPackage = "com.beatgames.beatsaber";
    private readonly IQuestTransport _transport;
    private readonly QuestBeatSaberPaths _paths;
    private readonly ConcurrentQueue<string> _diagnosticWarnings = new();

    public AdbQuestSyncTarget(IQuestTransport transport, QuestBeatSaberPaths paths)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public IReadOnlyList<string> DrainDiagnosticWarnings()
    {
        var warnings = new List<string>();
        while (_diagnosticWarnings.TryDequeue(out var warning)) warnings.Add(warning);
        return warnings;
    }

    public async Task<QuestWritePreparationResult> PrepareForWritesAsync(
        QuestDevice device,
        CancellationToken cancellationToken = default)
    {
        var help = await _transport.ExecuteShellAsync(
            device,
            ["toybox", "mv", "--help"],
            cancellationToken).ConfigureAwait(false);
        var helpText = $"{help.StandardOutput}\n{help.StandardError}";
        if (!help.IsSuccess || !helpText.Contains("-n", StringComparison.Ordinal) || !helpText.Contains("-T", StringComparison.Ordinal))
        {
            return QuestWritePreparationResult.Refused(
                "Quest toybox mv does not advertise the required -n and -T no-clobber promotion options.");
        }

        var stop = await _transport.ExecuteShellAsync(
            device,
            ["am", "force-stop", BeatSaberPackage],
            cancellationToken).ConfigureAwait(false);
        return stop.IsSuccess
            ? QuestWritePreparationResult.Ready
            : QuestWritePreparationResult.Refused($"Could not stop Beat Saber: {Error(stop)}");
    }

    public Task<bool> DirectoryExistsAsync(
        QuestDevice device,
        string remotePath,
        CancellationToken cancellationToken = default) =>
        TestPathAsync(device, "-d", remotePath, cancellationToken);

    public async Task CreateStagingDirectoryAsync(
        QuestDevice device,
        string stagingPath,
        CancellationToken cancellationToken = default)
    {
        EnsureOwnedMapStaging(stagingPath);
        if (await DirectoryExistsAsync(device, stagingPath, cancellationToken).ConfigureAwait(false))
            throw new QuestSyncTargetException($"Map staging directory already exists: {stagingPath}");
        await RunRequiredScriptAsync(device, $"mkdir {Quote(stagingPath)}", "Could not create map staging directory.", cancellationToken).ConfigureAwait(false);
    }

    public async Task UploadMapDirectoryAsync(
        QuestDevice device,
        string localMapDirectory,
        string stagingPath,
        IReadOnlySet<string> excludedFileNames,
        CancellationToken cancellationToken = default)
    {
        EnsureOwnedMapStaging(stagingPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(localMapDirectory);
        ArgumentNullException.ThrowIfNull(excludedFileNames);
        var root = Path.GetFullPath(localMapDirectory);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Local cached map was not found: {root}");

        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !excludedFileNames.Contains(Path.GetFileName(path)))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (files.Length == 0) throw new InvalidDataException("Local cached map contains no transferable files.");

        var createdParents = new HashSet<string>(StringComparer.Ordinal) { stagingPath };
        foreach (var localPath in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(root, localPath);
            if (relativePath == ".." || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                throw new InvalidDataException("A cached map file resolves outside the map root.");
            var remotePath = $"{stagingPath}/{relativePath.Replace(Path.DirectorySeparatorChar, '/')}";
            var remoteParent = remotePath[..remotePath.LastIndexOf('/')];
            if (createdParents.Add(remoteParent))
                await RunRequiredScriptAsync(device, $"mkdir -p {Quote(remoteParent)}", "Could not create staged map subdirectory.", cancellationToken).ConfigureAwait(false);

            var push = await _transport.PushAsync(device, localPath, remotePath, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(push, $"Could not upload map file '{relativePath}'.");
        }
    }

    public async Task<bool> VerifyStagedMapStructureAsync(
        QuestDevice device,
        string stagingPath,
        BeatMapIdentity expectedIdentity,
        CancellationToken cancellationToken = default)
    {
        EnsureOwnedMapStaging(stagingPath);
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        if (!await DirectoryExistsAsync(device, stagingPath, cancellationToken).ConfigureAwait(false)) return false;
        var files = await ListFilesRecursivelyAsync(device, stagingPath, cancellationToken).ConfigureAwait(false);
        return files.Any(path => string.Equals(RemoteName(path), "Info.dat", StringComparison.OrdinalIgnoreCase)) &&
               files.Any(path => !string.Equals(RemoteName(path), "Info.dat", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<bool> TryPromoteStagingAsync(
        QuestDevice device,
        string stagingPath,
        string finalPath,
        CancellationToken cancellationToken = default)
    {
        EnsureOwnedMapStaging(stagingPath);
        EnsureMapFinal(finalPath);
        if (await DirectoryExistsAsync(device, finalPath, cancellationToken).ConfigureAwait(false)) return false;

        var move = await RunScriptAsync(
            device,
            $"toybox mv -nT {Quote(stagingPath)} {Quote(finalPath)}",
            cancellationToken).ConfigureAwait(false);
        var stagingExists = await DirectoryExistsAsync(device, stagingPath, cancellationToken).ConfigureAwait(false);
        var finalExists = await DirectoryExistsAsync(device, finalPath, cancellationToken).ConfigureAwait(false);
        if (stagingExists && finalExists) return false;
        if (!move.IsSuccess) throw new QuestSyncTargetException($"Map promotion failed or was ambiguous: {Error(move)}");
        if (stagingExists || !finalExists) throw new QuestSyncTargetException("Map promotion did not produce an unambiguous final directory.");

        var finalFiles = await ListFilesRecursivelyAsync(device, finalPath, cancellationToken).ConfigureAwait(false);
        if (!finalFiles.Any(path => string.Equals(RemoteName(path), "Info.dat", StringComparison.OrdinalIgnoreCase)))
            throw new QuestSyncTargetException("Promoted map final directory does not contain Info.dat.");
        return true;
    }

    public async Task AbandonStagingAsync(
        QuestDevice device,
        string stagingPath,
        CancellationToken cancellationToken = default)
    {
        EnsureOwnedMapStaging(stagingPath);
        var result = await RunScriptAsync(device, $"rm -r {Quote(stagingPath)}", cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess && await DirectoryExistsAsync(device, stagingPath, cancellationToken).ConfigureAwait(false))
            throw new QuestSyncTargetException($"Current execution staging cleanup failed: {Error(result)}");
    }

    public async Task ImportPlaylistAsync(
        QuestDevice device,
        PreparedPlaylistSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!File.Exists(source.SnapshotPath)) throw new FileNotFoundException("Prepared playlist snapshot was not found.", source.SnapshotPath);
        if (new FileInfo(source.SnapshotPath).Length == 0) throw new InvalidDataException("Prepared playlist snapshot is empty.");
        var finalPath = QuestExecutionPaths.PlaylistFinal(_paths, source);
        var stagingPath = QuestExecutionPaths.PlaylistStaging(_paths, source, Guid.NewGuid());
        if (await FileExistsAsync(device, finalPath, cancellationToken).ConfigureAwait(false))
            throw new QuestSyncTargetException($"Managed playlist destination already exists and was preserved: {finalPath}");

        var stagingActive = false;
        try
        {
            stagingActive = true;
            var push = await _transport.PushAsync(device, source.SnapshotPath, stagingPath, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(push, "Could not transfer prepared playlist snapshot.");
            if (!await TestPathAsync(device, "-s", stagingPath, cancellationToken).ConfigureAwait(false))
                throw new QuestSyncTargetException("Transferred playlist staging file is empty or missing.");
            if (await FileExistsAsync(device, finalPath, cancellationToken).ConfigureAwait(false))
                throw new QuestSyncTargetException($"Managed playlist destination appeared during transfer and was preserved: {finalPath}");

            var move = await RunScriptAsync(device, $"toybox mv -nT {Quote(stagingPath)} {Quote(finalPath)}", cancellationToken).ConfigureAwait(false);
            var stagingExists = await FileExistsAsync(device, stagingPath, cancellationToken).ConfigureAwait(false);
            var finalExists = await TestPathAsync(device, "-s", finalPath, cancellationToken).ConfigureAwait(false);
            if (!move.IsSuccess || stagingExists || !finalExists)
                throw new QuestSyncTargetException($"Playlist promotion failed or was ambiguous: {Error(move)}");
            stagingActive = false;
        }
        finally
        {
            if (stagingActive)
                await AbandonPlaylistStagingAsync(device, stagingPath).ConfigureAwait(false);
        }
    }

    private async Task AbandonPlaylistStagingAsync(QuestDevice device, string stagingPath)
    {
        if (!QuestExecutionPaths.IsOwnedPlaylistStagingPath(_paths, stagingPath))
            throw new InvalidOperationException("Refusing to clean a playlist path not owned by this execution.");
        var cleanup = await RunScriptAsync(device, $"rm -f {Quote(stagingPath)}", CancellationToken.None).ConfigureAwait(false);
        if (!cleanup.IsSuccess && await FileExistsAsync(device, stagingPath, CancellationToken.None).ConfigureAwait(false))
            _diagnosticWarnings.Enqueue($"Current playlist staging could not be cleaned: {Error(cleanup)}");
    }

    private Task<bool> FileExistsAsync(QuestDevice device, string path, CancellationToken cancellationToken) =>
        TestPathAsync(device, "-e", path, cancellationToken);

    private async Task<bool> TestPathAsync(QuestDevice device, string test, string path, CancellationToken cancellationToken)
    {
        var result = await RunScriptAsync(device, $"test {test} {Quote(path)}", cancellationToken).ConfigureAwait(false);
        if (!result.AdbAvailable || result.TimedOut || result.ExitCode is null)
            throw new QuestSyncTargetException(Error(result));
        return result.ExitCode switch
        {
            0 => true,
            1 => false,
            _ => throw new QuestSyncTargetException(Error(result))
        };
    }

    private async Task<IReadOnlyList<string>> ListFilesRecursivelyAsync(QuestDevice device, string path, CancellationToken cancellationToken)
    {
        var result = await RunScriptAsync(device, $"find {Quote(path)} -type f -print", cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, $"Could not inspect transferred files under {path}.");
        return result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private async Task RunRequiredScriptAsync(QuestDevice device, string script, string message, CancellationToken cancellationToken)
    {
        var result = await RunScriptAsync(device, script, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, message);
    }

    private Task<AdbCommandResult> RunScriptAsync(QuestDevice device, string script, CancellationToken cancellationToken) =>
        _transport.ExecuteShellAsync(device, ["sh", "-c", script], cancellationToken);

    private void EnsureOwnedMapStaging(string path)
    {
        if (!QuestExecutionPaths.IsOwnedMapStagingPath(_paths, path))
            throw new InvalidOperationException("Refusing to write or clean a map path not owned by this execution.");
    }

    private void EnsureMapFinal(string path)
    {
        var parent = _paths.CustomLevels.TrimEnd('/');
        var name = path.StartsWith($"{parent}/", StringComparison.Ordinal) ? path[(parent.Length + 1)..] : string.Empty;
        if (!BeatSaverHash.IsValid(name) || name.Contains('/'))
            throw new InvalidOperationException("Refusing to promote into a non-hash CustomLevels path.");
    }

    private static void EnsureSuccess(AdbCommandResult result, string message)
    {
        if (!result.IsSuccess) throw new QuestSyncTargetException($"{message} {Error(result)}");
    }

    private static string Error(AdbCommandResult result) =>
        result.TimedOut ? "ADB command timed out." :
        !result.AdbAvailable ? "ADB is unavailable." :
        !string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardError.Trim() :
        !string.IsNullOrWhiteSpace(result.StandardOutput) ? result.StandardOutput.Trim() :
        $"ADB command exited with code {result.ExitCode?.ToString() ?? "unknown"}.";

    private static string Quote(string value) => PosixShellEscaping.Quote(value);
    private static string RemoteName(string path) => path[(path.LastIndexOf('/') + 1)..];
}

public sealed class QuestSyncTargetException(string message) : IOException(message);
