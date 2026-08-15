using System.IO.Compression;
using QuestBeatSync.Core.Models;
using QuestBeatSync.Infrastructure.Abstractions;
using QuestBeatSync.Infrastructure.Cache;

namespace QuestBeatSync.Tests;

[TestClass]
public sealed class LocalBeatMapCacheTests
{
    private const string Hash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private string _testRoot = null!;

    [TestInitialize]
    public void Initialize()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"qbsync-cache-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testRoot);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task CacheAsync_ValidZip_PublishesCompletedHashDirectory()
    {
        var cache = CreateCache(CreateZip(("Info.dat", "{}"), ("song.ogg", "audio")));

        var result = await cache.CacheAsync(OnlineLookup());

        Assert.AreEqual(BeatMapCacheOutcome.Cached, result.Outcome);
        Assert.IsTrue(await cache.IsCachedAsync(Hash));
        Assert.IsTrue(File.Exists(Path.Combine(_testRoot, Hash, "Info.dat")));
        Assert.IsFalse(Directory.EnumerateDirectories(_testRoot, "*.tmp").Any());
    }

    [TestMethod]
    public async Task CacheAsync_CorruptZip_DoesNotPublishPartialCache()
    {
        var cache = CreateCache([1, 2, 3, 4]);

        var result = await cache.CacheAsync(OnlineLookup());

        Assert.AreEqual(BeatMapCacheOutcome.Failed, result.Outcome);
        Assert.IsFalse(Directory.Exists(Path.Combine(_testRoot, Hash)));
        Assert.IsFalse(Directory.EnumerateDirectories(_testRoot, "*.tmp").Any());
    }

    [TestMethod]
    public async Task CacheAsync_ZipWithoutInfoDat_DoesNotPublishPartialCache()
    {
        var cache = CreateCache(CreateZip(("song.ogg", "audio")));

        var result = await cache.CacheAsync(OnlineLookup());

        Assert.AreEqual(BeatMapCacheOutcome.Failed, result.Outcome);
        StringAssert.Contains(result.ErrorMessage!, "Info.dat");
        Assert.IsFalse(Directory.Exists(Path.Combine(_testRoot, Hash)));
    }

    private LocalBeatMapCache CreateCache(byte[] archive) =>
        new(_testRoot, new DownloadStub(archive));

    private static BeatSaverLookupResult OnlineLookup() =>
        new(
            BeatSaverAvailability.Online,
            Hash,
            "1A2B",
            Hash,
            "1A2B",
            new Uri("https://example.test/map.zip"),
            true);

    private static byte[] CreateZip(params (string Name, string Content)[] files)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                var entry = archive.CreateEntry(file.Name);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(file.Content);
            }
        }

        return output.ToArray();
    }

    private sealed class DownloadStub(byte[] archive) : IBeatSaverClient
    {
        public Task<BeatSaverLookupResult> LookupAsync(
            BeatSaverLookupRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(OnlineLookup());

        public Task DownloadZipAsync(
            Uri downloadUri,
            Stream destination,
            CancellationToken cancellationToken = default) =>
            destination.WriteAsync(archive, cancellationToken).AsTask();
    }
}
