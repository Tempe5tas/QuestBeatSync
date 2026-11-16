using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuestBeatSync.Core.Models;
using QuestBeatSync.Infrastructure.Abstractions;
using QuestBeatSync.Infrastructure.Backup;

namespace QuestBeatSync.Tests;

[TestClass]
public sealed class ArchiveBackupTests
{
    private static readonly QuestDevice Device = new("SERIAL", QuestConnectionState.Device, QuestTransportKind.Usb, "Quest 3");
    private string _root = null!;

    [TestInitialize] public void Initialize() { _root = Path.Combine(Path.GetTempPath(), "qbsync-archive-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root); }
    [TestCleanup] public void Cleanup() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }

    [TestMethod]
    public async Task Backup_ArchivesHashLegacyUnknownPlaylistCoverAndOptionalState()
    {
        var files = new Dictionary<(ArchiveLogicalRoot, string), byte[]>
        {
            [(ArchiveLogicalRoot.CustomLevels, $"{new string('A', 40)}/Info.dat")] = Bytes("hash map"),
            [(ArchiveLogicalRoot.CustomLevels, "Legacy Map/song.ogg")] = Bytes("legacy"),
            [(ArchiveLogicalRoot.Playlists, "unknown.format")] = Bytes("not parsed"),
            [(ArchiveLogicalRoot.Covers, "nested/封面.png")] = [1, 2, 3],
            [(ArchiveLogicalRoot.SongCoreAuxiliary, "CachedSongData.json")] = Bytes("{}")
        };
        var result = await CreateService(new FakeSource(files), new SequenceScanner(Scan(), Scan())).CreateBackupAsync(Device, Binding(Scan()));
        Assert.AreEqual(QuestBackupStatus.Completed, result.Status);
        CollectionAssert.AreEquivalent(files.Keys.Select(key => $"{key.Item1}:{key.Item2}").ToArray(), result.Manifest!.Entries.Select(e => $"{e.LogicalRoot}:{e.RelativePath}").ToArray());
        Assert.AreEqual(SnapshotVerificationStatus.Valid, (await Store().VerifySnapshotAsync(result.Manifest.SnapshotId)).Status);
    }

    [TestMethod]
    public async Task Backup_MissingOptionalRootsIsNotFatal()
    {
        var result = await CreateService(new FakeSource(EmptyFiles()), new SequenceScanner(Scan(), Scan())).CreateBackupAsync(Device, Binding(Scan()));
        Assert.AreEqual(QuestBackupStatus.Completed, result.Status);
        Assert.AreEqual(0, result.Manifest!.Entries.Count);
    }

    [TestMethod]
    public async Task IdenticalFilesAcrossSnapshots_ReuseOneBlob()
    {
        var source = new FakeSource(new Dictionary<(ArchiveLogicalRoot, string), byte[]> { [(ArchiveLogicalRoot.CustomLevels, "Map/song.ogg")] = new byte[200_000] });
        var service = CreateService(source, new SequenceScanner(Scan(), Scan(), Scan(), Scan()));
        var first = await service.CreateBackupAsync(Device, Binding(Scan()));
        var second = await service.CreateBackupAsync(Device, Binding(Scan()));
        Assert.AreEqual(1, first.Progress!.NewBlobsWritten);
        Assert.AreEqual(1, second.Progress!.ExistingBlobsReused);
        Assert.AreEqual(first.Manifest!.Entries[0].BlobId, second.Manifest!.Entries[0].BlobId);
        Assert.AreEqual(1, Directory.GetFiles(Path.Combine(_root, "blobs"), "*", SearchOption.AllDirectories).Length);
    }

    [TestMethod]
    public async Task ExistingBlobWithWrongBytes_FailsClosed()
    {
        var staged = Store().CreateExecutionStagingDirectory(Guid.NewGuid());
        var file = Path.Combine(staged, "file"); await File.WriteAllTextAsync(file, "correct");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Bytes("correct")));
        var blob = Path.Combine(_root, "blobs", "sha256", hash[..2], hash); Directory.CreateDirectory(Path.GetDirectoryName(blob)!); await File.WriteAllTextAsync(blob, "wrong");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Store().CommitBlobAsync(file));
    }

    [TestMethod]
    public async Task CanceledOrFailedPull_PublishesNoSnapshot()
    {
        var files = new Dictionary<(ArchiveLogicalRoot, string), byte[]> { [(ArchiveLogicalRoot.Playlists, "one.json")] = Bytes("x") };
        var canceled = await CreateService(new FakeSource(files) { CancelPull = true }, new SequenceScanner(Scan())).CreateBackupAsync(Device, Binding(Scan()));
        var failed = await CreateService(new FakeSource(files) { FailPull = true }, new SequenceScanner(Scan())).CreateBackupAsync(Device, Binding(Scan()));
        Assert.AreEqual(QuestBackupStatus.Canceled, canceled.Status); Assert.AreEqual(QuestBackupStatus.Failed, failed.Status);
        Assert.AreEqual(0, (await Store().ListSnapshotsAsync()).Count);
    }

    [TestMethod]
    public async Task ChangedFingerprintOrPackageVersion_PublishesNoSnapshot()
    {
        var changedMap = Scan(mapPath: "/CustomLevels/B");
        var stateChanged = await CreateService(new FakeSource(EmptyFiles()), new SequenceScanner(Scan(), changedMap)).CreateBackupAsync(Device, Binding(Scan()));
        var versionChanged = await CreateService(new FakeSource(EmptyFiles()), new SequenceScanner(Scan(), Scan(versionCode: 999))).CreateBackupAsync(Device, Binding(Scan()));
        Assert.AreEqual(QuestBackupStatus.SourceChanged, stateChanged.Status);
        Assert.AreEqual(QuestBackupStatus.SourceChanged, versionChanged.Status);
        Assert.AreEqual(0, (await Store().ListSnapshotsAsync()).Count);
    }

    [TestMethod]
    public async Task WrongSelectedSerial_RefusesBeforeScanOrPull()
    {
        var scanner = new SequenceScanner(Scan()); var source = new FakeSource(EmptyFiles());
        var result = await CreateService(source, scanner).CreateBackupAsync(Device, QuestScanBinding.Capture("OTHER", Scan()));
        Assert.AreEqual(QuestBackupStatus.Refused, result.Status); Assert.AreEqual(0, scanner.CallCount); Assert.AreEqual(0, source.PullCount);
    }

    [TestMethod]
    public async Task IllegalWindowsFilename_IsArchivedViaGeneratedTemporaryName()
    {
        var source = new FakeSource(new Dictionary<(ArchiveLogicalRoot, string), byte[]> { [(ArchiveLogicalRoot.Playlists, "bad:name?.json")] = Bytes("raw") });
        var result = await CreateService(source, new SequenceScanner(Scan(), Scan())).CreateBackupAsync(Device, Binding(Scan()));
        Assert.AreEqual(QuestBackupStatus.Completed, result.Status);
        Assert.AreEqual("bad:name?.json", result.Manifest!.Entries.Single().RelativePath);
        Assert.IsTrue(source.LocalPaths.All(path => !Path.GetFileName(path).Contains("bad:name", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ManifestTraversalAndDuplicateLogicalPaths_AreRejected()
    {
        var store = Store(); var staging = store.CreateExecutionStagingDirectory(Guid.NewGuid());
        var traversal = Manifest("traversal", [new(ArchiveLogicalRoot.Playlists, "../evil", ArchiveEntryKind.File, 0, new string('A', 64), new string('A', 64))]);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.PublishSnapshotAsync(traversal, staging));
        store.CleanupStagingBestEffort(staging); staging = store.CreateExecutionStagingDirectory(Guid.NewGuid());
        var entry = new ArchiveEntry(ArchiveLogicalRoot.Playlists, "same.json", ArchiveEntryKind.File, 0, new string('A', 64), new string('A', 64));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.PublishSnapshotAsync(Manifest("duplicate", [entry, entry]), staging));
    }

    [TestMethod]
    public async Task VerifySnapshot_DetectsMissingAndModifiedBlob()
    {
        var result = await CreateService(new FakeSource(new Dictionary<(ArchiveLogicalRoot, string), byte[]> { [(ArchiveLogicalRoot.Covers, "cover.png")] = Bytes("image") }), new SequenceScanner(Scan(), Scan())).CreateBackupAsync(Device, Binding(Scan()));
        var entry = result.Manifest!.Entries.Single(); var blob = Path.Combine(_root, "blobs", "sha256", entry.BlobId[..2], entry.BlobId);
        File.Delete(blob);
        Assert.AreEqual(SnapshotVerificationStatus.Corrupted, (await Store().VerifySnapshotAsync(result.Manifest.SnapshotId)).Status);
        await File.WriteAllTextAsync(blob, "different-size-and-hash");
        var verification = await Store().VerifySnapshotAsync(result.Manifest.SnapshotId);
        Assert.AreEqual(SnapshotVerificationStatus.Corrupted, verification.Status);
        Assert.IsTrue(verification.Errors.Any(error => error.Contains("size", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(verification.Errors.Any(error => error.Contains("hash", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task ExistingCorrectBlob_IsReusedWithoutRewrite()
    {
        var store = Store(); var staging = store.CreateExecutionStagingDirectory(Guid.NewGuid()); var file = Path.Combine(staging, "one");
        await File.WriteAllTextAsync(file, "same"); var first = await store.CommitBlobAsync(file); var timestamp = File.GetLastWriteTimeUtc(Path.Combine(_root, "blobs", "sha256", first.BlobId[..2], first.BlobId));
        var second = await store.CommitBlobAsync(file);
        Assert.IsFalse(second.WasCreated); Assert.AreEqual(timestamp, File.GetLastWriteTimeUtc(Path.Combine(_root, "blobs", "sha256", first.BlobId[..2], first.BlobId)));
    }

    [TestMethod]
    public async Task InitialFreshScanMismatch_RefusesWithoutDiscovery()
    {
        var source = new FakeSource(EmptyFiles());
        var result = await CreateService(source, new SequenceScanner(Scan(mapPath: "/changed"))).CreateBackupAsync(Device, Binding(Scan()));
        Assert.AreEqual(QuestBackupStatus.Refused, result.Status); Assert.AreEqual(0, source.DiscoveryCount);
    }

    [TestMethod]
    public async Task DisconnectedDevice_RefusesBeforeInfrastructureWork()
    {
        var scanner = new SequenceScanner(Scan()); var source = new FakeSource(EmptyFiles());
        var disconnected = Device with { ConnectionState = QuestConnectionState.Offline };
        var result = await CreateService(source, scanner).CreateBackupAsync(disconnected, Binding(Scan()));
        Assert.AreEqual(QuestBackupStatus.Refused, result.Status); Assert.AreEqual(0, scanner.CallCount);
    }

    [TestMethod]
    public async Task SnapshotList_ReportsMapFoldersPlaylistsBytesAndDeviceIdentity()
    {
        var files = new Dictionary<(ArchiveLogicalRoot, string), byte[]> { [(ArchiveLogicalRoot.CustomLevels, "Legacy/a")] = Bytes("aa"), [(ArchiveLogicalRoot.CustomLevels, "Legacy/b")] = Bytes("bbb"), [(ArchiveLogicalRoot.Playlists, "p.json")] = Bytes("p") };
        await CreateService(new FakeSource(files), new SequenceScanner(Scan(), Scan())).CreateBackupAsync(Device, Binding(Scan()));
        var summary = (await Store().ListSnapshotsAsync()).Single();
        Assert.AreEqual(Device.Serial, summary.DeviceSerial); Assert.AreEqual(1, summary.MapFolderCount); Assert.AreEqual(1, summary.PlaylistFileCount); Assert.AreEqual(6, summary.TotalLogicalBytes);
        Assert.AreEqual(SnapshotVerificationStatus.Unverified, summary.VerificationStatus);
    }

    [TestMethod]
    public async Task SnapshotList_DoesNotVerifyBlobs_ReportsUnverifiedEvenIfBlobCorrupted()
    {
        var files = new Dictionary<(ArchiveLogicalRoot, string), byte[]> { [(ArchiveLogicalRoot.CustomLevels, "Legacy/a")] = Bytes("aa") };
        var result = await CreateService(new FakeSource(files), new SequenceScanner(Scan(), Scan())).CreateBackupAsync(Device, Binding(Scan()));
        var entry = result.Manifest!.Entries.Single();
        var blob = Path.Combine(_root, "blobs", "sha256", entry.BlobId[..2], entry.BlobId);
        await File.WriteAllTextAsync(blob, "corrupted content");

        // Listing must NOT verify blob hashes and must return Unverified
        var summaries = await Store().ListSnapshotsAsync();
        Assert.AreEqual(1, summaries.Count);
        Assert.AreEqual(SnapshotVerificationStatus.Unverified, summaries[0].VerificationStatus);

        // Explicit verification detects corruption
        var verification = await Store().VerifySnapshotAsync(result.Manifest.SnapshotId);
        Assert.AreEqual(SnapshotVerificationStatus.Corrupted, verification.Status);
    }

    [TestMethod]
    public async Task PublicationCollision_RefusesToMergeSnapshots()
    {
        var store = Store(); var first = store.CreateExecutionStagingDirectory(Guid.NewGuid()); var second = store.CreateExecutionStagingDirectory(Guid.NewGuid()); var manifest = Manifest("same-id", []);
        await store.PublishSnapshotAsync(manifest, first);
        await Assert.ThrowsExactlyAsync<IOException>(() => store.PublishSnapshotAsync(manifest, second));
    }

    [TestMethod]
    public async Task VerifySnapshot_RejectsUnsupportedFormat()
    {
        var manifest = Manifest("unsupported", []) with { FormatVersion = 999 };
        await WriteRawManifestAsync(manifest);
        Assert.AreEqual(SnapshotVerificationStatus.UnsupportedFormat, (await Store().VerifySnapshotAsync(manifest.SnapshotId)).Status);
    }

    [TestMethod]
    public async Task VerifySnapshot_RejectsExternallyInjectedDuplicatePaths()
    {
        var entry = new ArchiveEntry(ArchiveLogicalRoot.Playlists, "same", ArchiveEntryKind.File, 0, new string('A', 64), new string('A', 64));
        var manifest = Manifest("external-duplicate", [entry, entry]); await WriteRawManifestAsync(manifest);
        var result = await Store().VerifySnapshotAsync(manifest.SnapshotId);
        Assert.AreEqual(SnapshotVerificationStatus.Corrupted, result.Status); Assert.IsTrue(result.Errors.Any(error => error.Contains("Duplicate", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task VerifySnapshot_DetectsWrongHashEvenWhenSizeMatches()
    {
        var result = await CreateService(new FakeSource(new Dictionary<(ArchiveLogicalRoot, string), byte[]> { [(ArchiveLogicalRoot.Playlists, "p")] = Bytes("12345") }), new SequenceScanner(Scan(), Scan())).CreateBackupAsync(Device, Binding(Scan()));
        var entry = result.Manifest!.Entries.Single(); var blob = Path.Combine(_root, "blobs", "sha256", entry.BlobId[..2], entry.BlobId); await File.WriteAllTextAsync(blob, "abcde");
        var verification = await Store().VerifySnapshotAsync(result.Manifest.SnapshotId);
        Assert.IsTrue(verification.Errors.Any(error => error.Contains("hash", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(verification.Errors.Any(error => error.Contains("size", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void ArchivePathSafety_PreservesUnicodeAndCaseButRejectsAbsoluteNulAndTraversal()
    {
        Assert.AreEqual("Folder/谱面.DAT", ArchivePathSafety.NormalizeRelativePath("Folder/谱面.DAT"));
        Assert.ThrowsExactly<InvalidDataException>(() => ArchivePathSafety.NormalizeRelativePath("../x"));
        Assert.ThrowsExactly<InvalidDataException>(() => ArchivePathSafety.NormalizeRelativePath("/absolute"));
        Assert.ThrowsExactly<InvalidDataException>(() => ArchivePathSafety.NormalizeRelativePath("bad\0name"));
    }

    private async Task WriteRawManifestAsync(ArchiveManifest manifest)
    {
        var directory = Path.Combine(_root, "snapshots", manifest.SnapshotId); Directory.CreateDirectory(directory);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
        await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(manifest, options));
    }

    private ContentAddressedArchiveStore Store() => new(_root);
    private QuestBackupService CreateService(IQuestBackupSource source, IQuestBeatSaberScanner scanner) => new(scanner, source, Store());
    private static QuestScanBinding Binding(QuestBeatSaberScanResult scan) => QuestScanBinding.Capture(Device.Serial, scan);
    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
    private static IReadOnlyDictionary<(ArchiveLogicalRoot Root, string Path), byte[]> EmptyFiles() => new Dictionary<(ArchiveLogicalRoot, string), byte[]>();
    private static QuestBeatSaberScanResult Scan(string mapPath = "/CustomLevels/A", long versionCode = 1130) => new(true, true, true, true, true,
        [new QuestInstalledMap(mapPath, "A", true, "Song", "Mapper", QuestMapIdentityStatus.LocalOnly)], beatSaberPackageVersion: new("1.35.0", versionCode, new Version(1, 35)));
    private static ArchiveManifest Manifest(string id, IReadOnlyList<ArchiveEntry> entries) => new(ArchiveManifest.CurrentFormatVersion, id, DateTimeOffset.UtcNow,
        new(Device.Serial, Device.TransportKind, Device.AndroidModel), new("1.35.0", 1130), new string('A', 64), new string('A', 64), ArchiveConsistencyStatus.Consistent, entries);

    private sealed class SequenceScanner(params QuestBeatSaberScanResult[] scans) : IQuestBeatSaberScanner
    {
        private readonly Queue<QuestBeatSaberScanResult> _scans = new(scans); public int CallCount { get; private set; }
        public Task<QuestBeatSaberScanResult> ScanAsync(QuestDevice device, CancellationToken cancellationToken = default) { CallCount++; return Task.FromResult(_scans.Dequeue()); }
    }
    private sealed class FakeSource(IReadOnlyDictionary<(ArchiveLogicalRoot Root, string Path), byte[]> files) : IQuestBackupSource
    {
        public bool CancelPull { get; init; } public bool FailPull { get; init; } public int PullCount { get; private set; } public int DiscoveryCount { get; private set; } public List<string> LocalPaths { get; } = [];
        public Task<IReadOnlyList<QuestBackupSourceFile>> DiscoverAsync(QuestDevice device, CancellationToken cancellationToken = default) { DiscoveryCount++; return Task.FromResult<IReadOnlyList<QuestBackupSourceFile>>(files.Keys.Select(key => new QuestBackupSourceFile(key.Root, key.Path, "/remote/" + key.Path)).ToArray()); }
        public async Task PullAsync(QuestDevice device, QuestBackupSourceFile source, string safeLocalPath, CancellationToken cancellationToken = default)
        {
            PullCount++; LocalPaths.Add(safeLocalPath); if (CancelPull) throw new OperationCanceledException(); if (FailPull) throw new IOException("pull failed");
            await File.WriteAllBytesAsync(safeLocalPath, files[(source.LogicalRoot, source.RelativePath)], cancellationToken);
        }
    }
}
