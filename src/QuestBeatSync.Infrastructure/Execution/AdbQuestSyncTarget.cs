using QuestBeatSync.Core.Models;
using QuestBeatSync.Infrastructure.Abstractions;
using QuestBeatSync.Infrastructure.Adb;
using QuestBeatSync.Infrastructure.Scanning;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace QuestBeatSync.Infrastructure.Execution;

public sealed class AdbQuestSyncTarget : IQuestSyncTarget
{
    private const string BeatSaberPackage = "com.beatgames.beatsaber";
    private readonly IQuestTransport _transport;
    private readonly QuestBeatSaberPaths _paths;
    private readonly ConcurrentQueue<string> _diagnosticWarnings = new();
    private readonly ConcurrentDictionary<Guid, byte> _ownedWriteSessions = new();
    private readonly Func<int, bool> _isProcessAlive;

    public AdbQuestSyncTarget(IQuestTransport transport, QuestBeatSaberPaths paths, Func<int, bool>? isProcessAlive = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _isProcessAlive = isProcessAlive ?? IsProcessAlive;
    }

    public IReadOnlyList<string> DrainDiagnosticWarnings()
    {
        var warnings = new List<string>();
        while (_diagnosticWarnings.TryDequeue(out var warning)) warnings.Add(warning);
        return warnings;
    }

    public async Task<QuestWritePreparationResult> BeginWriteSessionAsync(
        QuestDevice device,
        Guid executionId,
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

        var lockPath = QuestExecutionPaths.WriterLock(_paths);
        var acquireScript =
            $"if mkdir {Quote(lockPath)}; then printf '%s' '__QBSYNC_ACQUIRED__'; " +
            $"else rc=$?; if test -d {Quote(lockPath)}; then printf '%s' '__QBSYNC_LOCKED__'; " +
            "else printf '__QBSYNC_ERROR__:%s' \"$rc\"; fi; fi";
        var acquire = await RunScriptAsync(device, acquireScript, cancellationToken).ConfigureAwait(false);
        EnsureProbeCommandSucceeded(acquire, "Could not acquire the QBSync writer lock.");
        var outcome = acquire.StandardOutput.Trim();
        if (outcome == "__QBSYNC_LOCKED__")
        {
            var recovery = await TryRecoverStaleWriterLockAsync(device, lockPath, cancellationToken).ConfigureAwait(false);
            if (!recovery.Recovered)
                return QuestWritePreparationResult.Refused(recovery.Message);
            outcome = "__QBSYNC_ACQUIRED__";
            _diagnosticWarnings.Enqueue("Recovered a stale QBSync writer lock left by a terminated process on this computer.");
        }
        if (outcome != "__QBSYNC_ACQUIRED__")
            throw new QuestSyncTargetException($"Writer lock acquisition returned an unexpected result: {outcome}");

        var session = new QuestWriteSession(executionId, device.Serial, lockPath);
        if (!_ownedWriteSessions.TryAdd(executionId, 0))
        {
            await RunScriptAsync(device, $"rm -r {Quote(lockPath)}", CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException("This execution already owns a writer session.");
        }
        try
        {
            await TryWriteLockMetadataAsync(device, session, cancellationToken).ConfigureAwait(false);
            var stop = await _transport.ExecuteShellAsync(
                device,
                ["am", "force-stop", BeatSaberPackage],
                cancellationToken).ConfigureAwait(false);
            if (stop.IsSuccess) return QuestWritePreparationResult.Ready(session);

            await EndWriteSessionAsync(device, session, CancellationToken.None).ConfigureAwait(false);
            return QuestWritePreparationResult.Refused($"Could not stop Beat Saber: {Error(stop)}");
        }
        catch
        {
            await EndWriteSessionAsync(device, session, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task EndWriteSessionAsync(
        QuestDevice device,
        QuestWriteSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var expectedPath = QuestExecutionPaths.WriterLock(_paths);
        if (!string.Equals(session.DeviceSerial, device.Serial, StringComparison.Ordinal) ||
            !string.Equals(session.LockPath, expectedPath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to release a writer lock not owned by this device/session.");
        }
        if (!_ownedWriteSessions.TryRemove(session.ExecutionId, out _))
            throw new InvalidOperationException("Refusing to release a writer lock not acquired by this target instance.");

        try
        {
            var cleanup = await RunScriptAsync(device, $"rm -r {Quote(session.LockPath)}", cancellationToken).ConfigureAwait(false);
            if (!cleanup.IsSuccess || await DirectoryExistsAsync(device, session.LockPath, cancellationToken).ConfigureAwait(false))
                _diagnosticWarnings.Enqueue($"QBSync writer lock could not be released: {Error(cleanup)}");
        }
        catch (Exception exception)
        {
            _diagnosticWarnings.Enqueue($"QBSync writer lock could not be released: {exception.Message}");
        }
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
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        var stagingIdentity = ParseOwnedMapStaging(stagingPath);
        if (stagingIdentity != expectedIdentity)
            throw new InvalidOperationException("Map staging identity does not match the requested map identity.");
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
        var stagingIdentity = ParseOwnedMapStaging(stagingPath);
        var finalIdentity = ParseMapFinal(finalPath);
        if (stagingIdentity != finalIdentity)
            throw new InvalidOperationException("Map staging and final destination identities do not match.");
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

    public Task<IReadOnlyList<MapStructureValidationResult>> ValidateStagedMapsAsync(
        QuestDevice device,
        IReadOnlyList<MapWritePathBinding> maps,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(maps);
        foreach (var map in maps)
        {
            var identity = ParseOwnedMapStaging(map.Path);
            if (identity != map.Identity)
                throw new InvalidOperationException("Map staging identity does not match the requested map identity.");
        }
        return ValidateMapStructuresAsync(device, maps, requireOwnedStaging: true, cancellationToken);
    }

    public async Task<MapPromotionResult> PromoteStagingCompactAsync(
        QuestDevice device,
        MapPromotionRequest map,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(map);
        var stagingIdentity = ParseOwnedMapStaging(map.StagingPath);
        var finalIdentity = ParseMapFinal(map.FinalPath);
        if (stagingIdentity != map.Identity || finalIdentity != map.Identity)
            throw new InvalidOperationException("Map staging and final destination identities do not match.");

        var hash = map.Identity.Hash;
        var script =
            $"if test -e {Quote(map.FinalPath)}; then printf '%s\\n' {Quote($"__QBSYNC_PROMOTE__|{hash}|FINAL_ALREADY_EXISTS")}; " +
            $"elif test ! -d {Quote(map.StagingPath)}; then printf '%s\\n' {Quote($"__QBSYNC_PROMOTE__|{hash}|STAGING_MISSING")}; " +
            $"else toybox mv -nT {Quote(map.StagingPath)} {Quote(map.FinalPath)}; rc=$?; " +
            $"if test -d {Quote(map.StagingPath)}; then s=1; else s=0; fi; " +
            $"if test -d {Quote(map.FinalPath)}; then f=1; else f=0; fi; " +
            $"if [ \"$rc\" -eq 0 ] && [ \"$s\" -eq 0 ] && [ \"$f\" -eq 1 ]; then printf '%s\\n' {Quote($"__QBSYNC_PROMOTE__|{hash}|PROMOTED")}; " +
            $"elif [ \"$s\" -eq \"$f\" ]; then printf '%s\\n' {Quote($"__QBSYNC_PROMOTE__|{hash}|AMBIGUOUS")}; " +
            $"else printf '__QBSYNC_PROMOTE__|{hash}|ERROR:%s\\n' \"$rc\"; fi; fi";
        var command = await RunScriptAsync(device, script, cancellationToken).ConfigureAwait(false);
        EnsureProbeCommandSucceeded(command, "Map promotion transaction failed.");
        var prefix = $"__QBSYNC_PROMOTE__|{hash}|";
        var lines = command.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length != 1 || !lines[0].StartsWith(prefix, StringComparison.Ordinal))
            return new(map.Identity, MapPromotionStatus.Ambiguous, "Promotion returned malformed or ambiguous sentinel output.");
        var value = lines[0][prefix.Length..];
        return value switch
        {
            "PROMOTED" => new(map.Identity, MapPromotionStatus.Promoted),
            "FINAL_ALREADY_EXISTS" => new(map.Identity, MapPromotionStatus.FinalAlreadyExists),
            "STAGING_MISSING" => new(map.Identity, MapPromotionStatus.StagingMissing),
            "AMBIGUOUS" => new(map.Identity, MapPromotionStatus.Ambiguous),
            _ when value.StartsWith("ERROR:", StringComparison.Ordinal) => new(map.Identity, MapPromotionStatus.Failed, value),
            _ => new(map.Identity, MapPromotionStatus.Ambiguous, $"Unexpected promotion sentinel: {value}")
        };
    }

    public Task<IReadOnlyList<MapStructureValidationResult>> VerifyFinalMapsAsync(
        QuestDevice device,
        IReadOnlyList<MapWritePathBinding> maps,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(maps);
        foreach (var map in maps)
        {
            var identity = ParseMapFinal(map.Path);
            if (identity != map.Identity)
                throw new InvalidOperationException("Final map path does not match the requested map identity.");
        }
        return ValidateMapStructuresAsync(device, maps, requireOwnedStaging: false, cancellationToken);
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
                await AbandonPlaylistStagingFailSoftAsync(device, stagingPath).ConfigureAwait(false);
        }
    }

    private async Task AbandonPlaylistStagingFailSoftAsync(QuestDevice device, string stagingPath)
    {
        try
        {
            if (!QuestExecutionPaths.IsOwnedPlaylistStagingPath(_paths, stagingPath))
                throw new InvalidOperationException("Refusing to clean a playlist path not owned by this execution.");
            var cleanup = await RunScriptAsync(device, $"rm -f {Quote(stagingPath)}", CancellationToken.None).ConfigureAwait(false);
            if (!cleanup.IsSuccess || await FileExistsAsync(device, stagingPath, CancellationToken.None).ConfigureAwait(false))
                _diagnosticWarnings.Enqueue($"Current playlist staging could not be cleaned: {Error(cleanup)}");
        }
        catch (Exception exception)
        {
            _diagnosticWarnings.Enqueue($"Current playlist staging could not be cleaned: {exception.Message}");
        }
    }

    private Task<bool> FileExistsAsync(QuestDevice device, string path, CancellationToken cancellationToken) =>
        TestPathAsync(device, "-e", path, cancellationToken);

    private async Task<bool> TestPathAsync(QuestDevice device, string test, string path, CancellationToken cancellationToken)
    {
        var script =
            $"if test {test} {Quote(path)}; then printf '%s' '__QBSYNC_EXISTS__'; " +
            "else rc=$?; if [ \"$rc\" -eq 1 ]; then printf '%s' '__QBSYNC_MISSING__'; " +
            "else printf '__QBSYNC_ERROR__:%s' \"$rc\"; fi; fi";
        var result = await RunScriptAsync(device, script, cancellationToken).ConfigureAwait(false);
        EnsureProbeCommandSucceeded(result, $"Could not inspect remote path {path}.");
        return result.StandardOutput.Trim() switch
        {
            "__QBSYNC_EXISTS__" => true,
            "__QBSYNC_MISSING__" => false,
            var output => throw new QuestSyncTargetException($"Remote path probe returned an unexpected result: {output}")
        };
    }

    private async Task TryWriteLockMetadataAsync(
        QuestDevice device,
        QuestWriteSession session,
        CancellationToken cancellationToken)
    {
        try
        {
            var metadata = JsonSerializer.Serialize(new
            {
                executionId = session.ExecutionId,
                deviceSerial = session.DeviceSerial,
                startedAtUtc = DateTimeOffset.UtcNow,
                host = Environment.MachineName,
                pid = Environment.ProcessId
            });
            var result = await RunScriptAsync(
                device,
                $"printf '%s' {Quote(metadata)} > {Quote($"{session.LockPath}/session.json")}",
                cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess)
                _diagnosticWarnings.Enqueue($"QBSync writer lock metadata could not be written: {Error(result)}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _diagnosticWarnings.Enqueue($"QBSync writer lock metadata could not be written: {exception.Message}");
        }
    }

    private async Task<IReadOnlyList<string>> ListFilesRecursivelyAsync(QuestDevice device, string path, CancellationToken cancellationToken)
    {
        var result = await RunScriptAsync(device, $"find {Quote(path)} -type f -print", cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, $"Could not inspect transferred files under {path}.");
        return result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private async Task<StaleLockRecoveryResult> TryRecoverStaleWriterLockAsync(
        QuestDevice device,
        string lockPath,
        CancellationToken cancellationToken)
    {
        var metadataPath = $"{lockPath}/session.json";
        var read = await _transport.ExecuteShellAsync(device, ["cat", metadataPath], cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess || string.IsNullOrWhiteSpace(read.StandardOutput) || read.StandardOutput.Length > 8192)
            return StaleLockRecoveryResult.Refused("Another QBSync write session lock exists, but its ownership metadata cannot be safely verified. No lock was removed.");

        WriterLockMetadata? metadata;
        try { metadata = JsonSerializer.Deserialize<WriterLockMetadata>(read.StandardOutput, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (JsonException) { metadata = null; }
        if (metadata is null || metadata.ExecutionId == Guid.Empty || metadata.StartedAtUtc == default || metadata.Pid <= 0 ||
            string.IsNullOrWhiteSpace(metadata.Host) || string.IsNullOrWhiteSpace(metadata.DeviceSerial))
            return StaleLockRecoveryResult.Refused("A QBSync writer lock exists with invalid ownership metadata. No lock was removed.");
        if (!string.Equals(metadata.Host, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            return StaleLockRecoveryResult.Refused("A QBSync writer lock belongs to another computer and was preserved.");
        if (!string.Equals(metadata.DeviceSerial, device.Serial, StringComparison.Ordinal))
            return StaleLockRecoveryResult.Refused("A QBSync writer lock does not match the selected device identity and was preserved.");
        if (_isProcessAlive(metadata.Pid))
            return StaleLockRecoveryResult.Refused("Another QBSync writer process on this computer still appears to be active.");

        cancellationToken.ThrowIfCancellationRequested();
        var exactMetadata = read.StandardOutput;
        var recoveryScript =
            $"if test ! -f {Quote(metadataPath)}; then printf '%s' '__QBSYNC_RECOVERY_UNVERIFIABLE__'; " +
            $"elif [ \"$(cat {Quote(metadataPath)})\" != {Quote(exactMetadata)} ]; then printf '%s' '__QBSYNC_RECOVERY_CHANGED__'; " +
            $"elif rm -r {Quote(lockPath)} && mkdir {Quote(lockPath)}; then printf '%s' '__QBSYNC_RECOVERED__'; " +
            $"elif test -d {Quote(lockPath)}; then printf '%s' '__QBSYNC_RECOVERY_LOCKED__'; " +
            "else printf '%s' '__QBSYNC_RECOVERY_ERROR__'; fi";
        // Once started, observe this short remove-and-reacquire transaction to avoid leaving an
        // empty, ownerless lock if cancellation arrives between its two remote operations.
        var recovery = await RunScriptAsync(device, recoveryScript, CancellationToken.None).ConfigureAwait(false);
        EnsureProbeCommandSucceeded(recovery, "Could not safely recover the stale QBSync writer lock.");
        return recovery.StandardOutput.Trim() switch
        {
            "__QBSYNC_RECOVERED__" => StaleLockRecoveryResult.Success,
            "__QBSYNC_RECOVERY_CHANGED__" => StaleLockRecoveryResult.Refused("The QBSync writer lock changed during recovery and was preserved."),
            "__QBSYNC_RECOVERY_LOCKED__" => StaleLockRecoveryResult.Refused("Another QBSync writer acquired the lock during recovery."),
            "__QBSYNC_RECOVERY_UNVERIFIABLE__" => StaleLockRecoveryResult.Refused("The QBSync writer lock became unverifiable during recovery."),
            _ => StaleLockRecoveryResult.Refused("The stale QBSync writer lock could not be safely recovered.")
        };
    }

    private static bool IsProcessAlive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
        catch { return true; }
    }

    private sealed record WriterLockMetadata(Guid ExecutionId, string DeviceSerial, DateTimeOffset StartedAtUtc, string Host, int Pid);
    private sealed record StaleLockRecoveryResult(bool Recovered, string Message)
    {
        public static StaleLockRecoveryResult Success { get; } = new(true, "Stale writer lock recovered.");
        public static StaleLockRecoveryResult Refused(string message) => new(false, message);
    }

    private async Task<IReadOnlyList<MapStructureValidationResult>> ValidateMapStructuresAsync(
        QuestDevice device,
        IReadOnlyList<MapWritePathBinding> maps,
        bool requireOwnedStaging,
        CancellationToken cancellationToken)
    {
        if (maps.Count == 0) return [];
        if (maps.Select(map => map.Identity.Hash).Distinct(StringComparer.OrdinalIgnoreCase).Count() != maps.Count)
            throw new InvalidOperationException("A map validation batch cannot contain duplicate identities.");

        var script = string.Join(' ', maps.Select(map =>
        {
            if (requireOwnedStaging) EnsureOwnedMapStaging(map.Path); else _ = ParseMapFinal(map.Path);
            var prefix = $"__QBSYNC_MAP__|{map.Identity.Hash}|";
            return $"if test ! -d {Quote(map.Path)}; then printf '%s\\n' {Quote(prefix + "MISSING_DIRECTORY")}; " +
                   $"elif ! find {Quote(map.Path)} -type f -iname 'Info.dat' -print -quit | grep -q .; then printf '%s\\n' {Quote(prefix + "MISSING_INFO")}; " +
                   $"elif ! find {Quote(map.Path)} -type f ! -iname 'Info.dat' -print -quit | grep -q .; then printf '%s\\n' {Quote(prefix + "NO_OTHER_FILES")}; " +
                   $"else printf '%s\\n' {Quote(prefix + "OK")}; fi;";
        }));
        var command = await RunScriptAsync(device, script, cancellationToken).ConfigureAwait(false);
        EnsureProbeCommandSucceeded(command, "Batched map structure validation failed.");
        return ParseMapValidationOutput(maps, command.StandardOutput);
    }

    internal static IReadOnlyList<MapStructureValidationResult> ParseMapValidationOutput(
        IReadOnlyList<MapWritePathBinding> maps,
        string output)
    {
        const string prefix = "__QBSYNC_MAP__|";
        var expected = maps.ToDictionary(map => map.Identity.Hash, StringComparer.OrdinalIgnoreCase);
        var values = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var malformed = false;
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith(prefix, StringComparison.Ordinal)) { malformed = true; continue; }
            var parts = line[prefix.Length..].Split('|');
            if (parts.Length != 2 || !expected.ContainsKey(parts[0])) { malformed = true; continue; }
            if (!values.TryGetValue(parts[0], out var statuses)) values[parts[0]] = statuses = [];
            statuses.Add(parts[1]);
        }

        return maps.Select(map =>
        {
            if (malformed) return new MapStructureValidationResult(map.Identity, MapStructureValidationStatus.InvalidResponse, "Unexpected batch sentinel output.");
            if (!values.TryGetValue(map.Identity.Hash, out var statuses))
                return new(map.Identity, MapStructureValidationStatus.InvalidResponse, "Validation result was missing.");
            if (statuses.Count != 1)
                return new(map.Identity, MapStructureValidationStatus.InvalidResponse, "Validation result was duplicated.");
            return statuses[0] switch
            {
                "OK" => new(map.Identity, MapStructureValidationStatus.Valid),
                "MISSING_DIRECTORY" => new(map.Identity, MapStructureValidationStatus.MissingDirectory),
                "MISSING_INFO" => new(map.Identity, MapStructureValidationStatus.MissingInfoDat),
                "NO_OTHER_FILES" => new(map.Identity, MapStructureValidationStatus.NoOtherFiles),
                _ => new(map.Identity, MapStructureValidationStatus.InvalidResponse, $"Unexpected validation sentinel: {statuses[0]}")
            };
        }).ToArray();
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

    private BeatMapIdentity ParseOwnedMapStaging(string path)
    {
        if (!QuestExecutionPaths.TryParseOwnedMapStagingPath(_paths, path, out var identity, out _))
            throw new InvalidOperationException("Refusing to use a map path not owned by this execution.");
        return identity!;
    }

    private BeatMapIdentity ParseMapFinal(string path)
    {
        if (!QuestExecutionPaths.TryParseMapFinalPath(_paths, path, out var identity))
            throw new InvalidOperationException("Refusing to promote into a non-hash CustomLevels path.");
        return identity!;
    }

    private static void EnsureProbeCommandSucceeded(AdbCommandResult result, string message)
    {
        if (!result.IsSuccess) throw new QuestSyncTargetException($"{message} {Error(result)}");
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
