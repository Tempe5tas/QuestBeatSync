using System.Text.RegularExpressions;
using QuestBeatSync.Core.Models;
using QuestBeatSync.Infrastructure.Abstractions;
using QuestBeatSync.Infrastructure.Execution;

namespace QuestBeatSync.Tests;

[TestClass]
public sealed class AdbQuestSyncTargetTests
{
    private const string Hash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static readonly QuestDevice Device = new("QUEST", QuestConnectionState.Device, QuestTransportKind.Usb);
    private string _temporaryRoot = null!;

    [TestInitialize]
    public void Initialize()
    {
        _temporaryRoot = Path.Combine(Path.GetTempPath(), $"qbsync-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temporaryRoot);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_temporaryRoot)) Directory.Delete(_temporaryRoot, recursive: true);
    }

    [TestMethod]
    public async Task MapUpload_FiltersLocalMarkerVerifiesStructureAndPromotes()
    {
        var localMap = CreateMapCache();
        var transport = new StatefulTransport();
        var target = CreateTarget(transport);
        var identity = new BeatMapIdentity(Hash);
        var staging = QuestExecutionPaths.MapStaging(QuestBeatSaberPaths.Default, identity, Guid.NewGuid());
        var final = QuestExecutionPaths.MapFinal(QuestBeatSaberPaths.Default, identity);

        await target.CreateStagingDirectoryAsync(Device, staging);
        await target.UploadMapDirectoryAsync(Device, localMap, staging,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".qbsync-complete" });
        var verified = await target.VerifyStagedMapStructureAsync(Device, staging, identity);
        var promoted = await target.TryPromoteStagingAsync(Device, staging, final);

        Assert.IsTrue(verified);
        Assert.IsTrue(promoted);
        Assert.AreEqual(2, transport.Pushes.Count);
        Assert.IsFalse(transport.Pushes.Any(push => Path.GetFileName(push.LocalPath) == ".qbsync-complete"));
        Assert.IsTrue(transport.Directories.Contains(final));
        Assert.IsFalse(transport.Directories.Contains(staging));
        Assert.IsTrue(transport.RemoteFiles.Keys.Any(path => path.StartsWith(final, StringComparison.Ordinal) && path.EndsWith("Info.dat", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Promotion_WhenFinalAppearsAfterUpload_DoesNotClobber()
    {
        var transport = new StatefulTransport();
        var target = CreateTarget(transport);
        var identity = new BeatMapIdentity(Hash);
        var staging = QuestExecutionPaths.MapStaging(QuestBeatSaberPaths.Default, identity, Guid.NewGuid());
        var final = QuestExecutionPaths.MapFinal(QuestBeatSaberPaths.Default, identity);
        transport.Directories.Add(staging);
        transport.RemoteFiles[$"{staging}/Info.dat"] = [1];
        transport.BeforeMove = () => transport.Directories.Add(final);

        var promoted = await target.TryPromoteStagingAsync(Device, staging, final);

        Assert.IsFalse(promoted);
        Assert.IsTrue(transport.Directories.Contains(final));
        Assert.IsTrue(transport.Directories.Contains(staging));
        Assert.IsFalse(transport.RemoteFiles.Keys.Any(path => path.StartsWith($"{final}/.qbsync-", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task UploadFailure_LeavesNoFinalMap()
    {
        var localMap = CreateMapCache();
        var transport = new StatefulTransport { FailPush = true };
        var target = CreateTarget(transport);
        var identity = new BeatMapIdentity(Hash);
        var staging = QuestExecutionPaths.MapStaging(QuestBeatSaberPaths.Default, identity, Guid.NewGuid());
        var final = QuestExecutionPaths.MapFinal(QuestBeatSaberPaths.Default, identity);
        await target.CreateStagingDirectoryAsync(Device, staging);

        await Assert.ThrowsAsync<QuestSyncTargetException>(() =>
            target.UploadMapDirectoryAsync(Device, localMap, staging, new HashSet<string>()));

        Assert.IsFalse(transport.Directories.Contains(final));
    }

    [TestMethod]
    public async Task StructuralVerificationFailure_DoesNotCreateFinalMap()
    {
        var transport = new StatefulTransport();
        var target = CreateTarget(transport);
        var identity = new BeatMapIdentity(Hash);
        var staging = QuestExecutionPaths.MapStaging(QuestBeatSaberPaths.Default, identity, Guid.NewGuid());
        var final = QuestExecutionPaths.MapFinal(QuestBeatSaberPaths.Default, identity);
        transport.Directories.Add(staging);
        transport.RemoteFiles[$"{staging}/Info.dat"] = [1];

        Assert.IsFalse(await target.VerifyStagedMapStructureAsync(Device, staging, identity));
        Assert.IsFalse(transport.Directories.Contains(final));
    }

    [TestMethod]
    public async Task PromotionFailure_LeavesUnrelatedExistingMapsUntouched()
    {
        var transport = new StatefulTransport { FailMove = true };
        var target = CreateTarget(transport);
        var identity = new BeatMapIdentity(Hash);
        var staging = QuestExecutionPaths.MapStaging(QuestBeatSaberPaths.Default, identity, Guid.NewGuid());
        var final = QuestExecutionPaths.MapFinal(QuestBeatSaberPaths.Default, identity);
        const string unrelated = "/sdcard/ModData/com.beatgames.beatsaber/Mods/SongCore/CustomLevels/BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
        transport.Directories.UnionWith([staging, unrelated]);

        await Assert.ThrowsAsync<QuestSyncTargetException>(() => target.TryPromoteStagingAsync(Device, staging, final));

        Assert.IsTrue(transport.Directories.Contains(staging));
        Assert.IsTrue(transport.Directories.Contains(unrelated));
        Assert.IsFalse(transport.Directories.Contains(final));
    }

    [TestMethod]
    public async Task CancelDuringPush_DoesNotCreateFinalMap()
    {
        var localMap = CreateMapCache();
        var transport = new StatefulTransport { CancelPush = true };
        var target = CreateTarget(transport);
        var identity = new BeatMapIdentity(Hash);
        var staging = QuestExecutionPaths.MapStaging(QuestBeatSaberPaths.Default, identity, Guid.NewGuid());
        var final = QuestExecutionPaths.MapFinal(QuestBeatSaberPaths.Default, identity);
        await target.CreateStagingDirectoryAsync(Device, staging);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            target.UploadMapDirectoryAsync(Device, localMap, staging, new HashSet<string>(), new CancellationToken(true)));

        Assert.IsFalse(transport.Directories.Contains(final));
    }

    [TestMethod]
    public async Task PreparedPlaylistSnapshot_IsTransferredAndStableSourceNamesDoNotCollide()
    {
        var snapshot = Path.Combine(_temporaryRoot, "snapshot.bplist");
        await File.WriteAllTextAsync(snapshot, "approved bytes");
        var first = new PreparedPlaylistSource(Path.Combine(_temporaryRoot, "A", "foo.bplist"), snapshot, new string('A', 64));
        var second = new PreparedPlaylistSource(Path.Combine(_temporaryRoot, "B", "foo.bplist"), snapshot, new string('A', 64));
        var firstName = QuestExecutionPaths.BuildManagedPlaylistFileName(first.OriginalCanonicalPath);
        var secondName = QuestExecutionPaths.BuildManagedPlaylistFileName(second.OriginalCanonicalPath);
        var transport = new StatefulTransport();
        var target = CreateTarget(transport);

        await target.ImportPlaylistAsync(Device, first);
        await target.ImportPlaylistAsync(Device, second);

        Assert.AreNotEqual(firstName, secondName);
        Assert.AreEqual(2, transport.Pushes.Count);
        Assert.IsTrue(transport.Pushes.All(push => push.LocalPath == snapshot));
        Assert.IsTrue(transport.RemoteFiles.Keys.Any(path => path.EndsWith(firstName, StringComparison.Ordinal)));
        Assert.IsTrue(transport.RemoteFiles.Keys.Any(path => path.EndsWith(secondName, StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ExistingManagedPlaylist_IsPreservedWithoutSilentReplacement()
    {
        var snapshot = Path.Combine(_temporaryRoot, "snapshot.bplist");
        await File.WriteAllTextAsync(snapshot, "approved bytes");
        var source = new PreparedPlaylistSource(Path.Combine(_temporaryRoot, "source.bplist"), snapshot, new string('A', 64));
        var transport = new StatefulTransport();
        var finalPath = QuestExecutionPaths.PlaylistFinal(QuestBeatSaberPaths.Default, source);
        transport.RemoteFiles[finalPath] = "existing"u8.ToArray();
        var target = CreateTarget(transport);

        await Assert.ThrowsAsync<QuestSyncTargetException>(() => target.ImportPlaylistAsync(Device, source));

        Assert.AreEqual(0, transport.Pushes.Count);
        CollectionAssert.AreEqual("existing"u8.ToArray(), transport.RemoteFiles[finalPath]);
    }

    [TestMethod]
    public async Task PlaylistStagingCleanupFailure_IsExposedAsDiagnosticWarning()
    {
        var snapshot = Path.Combine(_temporaryRoot, "snapshot.bplist");
        await File.WriteAllTextAsync(snapshot, "approved bytes");
        var source = new PreparedPlaylistSource(Path.Combine(_temporaryRoot, "source.bplist"), snapshot, new string('A', 64));
        var transport = new StatefulTransport { FailPush = true, LeavePartialOnFailedPush = true, FailRemove = true };
        var target = CreateTarget(transport);

        await Assert.ThrowsAsync<QuestSyncTargetException>(() => target.ImportPlaylistAsync(Device, source));

        Assert.IsTrue(target.DrainDiagnosticWarnings().Any(warning => warning.Contains("staging", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task WritePreparation_RequiresNoClobberSupportAndSuccessfulForceStop()
    {
        var readyTransport = new StatefulTransport();
        Assert.IsTrue((await CreateTarget(readyTransport).PrepareForWritesAsync(Device)).IsReady);
        CollectionAssert.Contains(readyTransport.DirectCommands.ToArray(), "am force-stop com.beatgames.beatsaber");

        var unsupported = new StatefulTransport { MoveHelp = "usage: mv [-fin] SOURCE DEST" };
        Assert.IsFalse((await CreateTarget(unsupported).PrepareForWritesAsync(Device)).IsReady);
        Assert.IsFalse(unsupported.DirectCommands.Any(command => command.StartsWith("am force-stop", StringComparison.Ordinal)));

        var stopFailure = new StatefulTransport { ForceStopSucceeds = false };
        Assert.IsFalse((await CreateTarget(stopFailure).PrepareForWritesAsync(Device)).IsReady);
    }

    private string CreateMapCache()
    {
        var root = Path.Combine(_temporaryRoot, "map");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Info.dat"), "{}");
        File.WriteAllText(Path.Combine(root, "song.ogg"), "audio");
        File.WriteAllText(Path.Combine(root, ".qbsync-complete"), Hash);
        return root;
    }

    private static AdbQuestSyncTarget CreateTarget(StatefulTransport transport) =>
        new(transport, QuestBeatSaberPaths.Default);

    private sealed class StatefulTransport : IQuestTransport
    {
        private static readonly Regex QuotedPath = new("'([^']*)'", RegexOptions.Compiled);
        public HashSet<string> Directories { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, byte[]> RemoteFiles { get; } = new(StringComparer.Ordinal);
        public List<(string LocalPath, string RemotePath)> Pushes { get; } = [];
        public List<string> DirectCommands { get; } = [];
        public string MoveHelp { get; set; } = "usage: mv [-FfinTvx] SOURCE DEST\n-n No clobber\n-T exact destination";
        public bool ForceStopSucceeds { get; set; } = true;
        public bool FailPush { get; set; }
        public bool CancelPush { get; set; }
        public bool LeavePartialOnFailedPush { get; set; }
        public bool FailMove { get; set; }
        public bool FailRemove { get; set; }
        public Action? BeforeMove { get; set; }

        public Task<QuestDeviceDiscoveryResult> GetDevicesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AdbCommandResult> ExecuteShellAsync(QuestDevice device, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            if (arguments.SequenceEqual(["toybox", "mv", "--help"])) return Success(MoveHelp);
            if (arguments.Count >= 3 && arguments[0] == "am")
            {
                DirectCommands.Add(string.Join(' ', arguments));
                return Task.FromResult(ForceStopSucceeds ? Ok() : Failed("force-stop failed"));
            }

            var script = arguments[2];
            var paths = QuotedPath.Matches(script).Select(match => match.Groups[1].Value).ToArray();
            if (script.StartsWith("test -d", StringComparison.Ordinal)) return Exit(Directories.Contains(paths[0]));
            if (script.StartsWith("test -e", StringComparison.Ordinal)) return Exit(RemoteFiles.ContainsKey(paths[0]) || Directories.Contains(paths[0]));
            if (script.StartsWith("test -s", StringComparison.Ordinal)) return Exit(RemoteFiles.TryGetValue(paths[0], out var bytes) && bytes.Length > 0);
            if (script.StartsWith("mkdir", StringComparison.Ordinal)) { Directories.Add(paths[0]); return Success(); }
            if (script.StartsWith("find", StringComparison.Ordinal))
            {
                var prefix = $"{paths[0].TrimEnd('/')}/";
                return Success(string.Join('\n', RemoteFiles.Keys.Where(path => path.StartsWith(prefix, StringComparison.Ordinal))) + "\n");
            }
            if (script.StartsWith("toybox mv -nT", StringComparison.Ordinal))
            {
                BeforeMove?.Invoke();
                if (FailMove) return Task.FromResult(Failed("move failed"));
                Move(paths[0], paths[1]);
                return Success();
            }
            if (script.StartsWith("rm -r", StringComparison.Ordinal)) { if (FailRemove) return Task.FromResult(Failed("remove failed")); RemoveTree(paths[0]); return Success(); }
            if (script.StartsWith("rm -f", StringComparison.Ordinal)) { if (FailRemove) return Task.FromResult(Failed("remove failed")); RemoteFiles.Remove(paths[0]); return Success(); }
            return Task.FromResult(Failed($"Unexpected script: {script}"));
        }

        public async Task<AdbCommandResult> PushAsync(QuestDevice device, string localPath, string remotePath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (CancelPush) throw new OperationCanceledException(cancellationToken);
            Pushes.Add((localPath, remotePath));
            if (FailPush)
            {
                if (LeavePartialOnFailedPush) RemoteFiles[remotePath] = [1];
                return Failed("push failed");
            }
            RemoteFiles[remotePath] = await File.ReadAllBytesAsync(localPath, cancellationToken);
            return Ok();
        }

        public Task<AdbCommandResult> PullAsync(QuestDevice device, string remotePath, string localPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        private void Move(string source, string destination)
        {
            if (Directories.Contains(destination) || RemoteFiles.ContainsKey(destination)) return;
            if (Directories.Remove(source))
            {
                Directories.Add(destination);
                foreach (var file in RemoteFiles.Keys.Where(path => path.StartsWith($"{source}/", StringComparison.Ordinal)).ToArray())
                {
                    var bytes = RemoteFiles[file]; RemoteFiles.Remove(file); RemoteFiles[$"{destination}{file[source.Length..]}"] = bytes;
                }
            }
            else if (RemoteFiles.Remove(source, out var bytes)) RemoteFiles[destination] = bytes;
        }

        private void RemoveTree(string root)
        {
            Directories.Remove(root);
            foreach (var directory in Directories.Where(path => path.StartsWith($"{root}/", StringComparison.Ordinal)).ToArray()) Directories.Remove(directory);
            foreach (var file in RemoteFiles.Keys.Where(path => path.StartsWith($"{root}/", StringComparison.Ordinal)).ToArray()) RemoteFiles.Remove(file);
        }

        private static Task<AdbCommandResult> Success(string output = "") => Task.FromResult(Ok(output));
        private static Task<AdbCommandResult> Exit(bool success) => Task.FromResult(new AdbCommandResult(true, false, success ? 0 : 1, "", ""));
        private static AdbCommandResult Ok(string output = "") => new(true, false, 0, output, "");
        private static AdbCommandResult Failed(string error) => new(true, false, 1, "", error);
    }
}
