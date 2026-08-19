using QuestBeatSync.Core.Models;
using QuestBeatSync.Infrastructure.Scanning;

namespace QuestBeatSync.Tests;

[TestClass]
public sealed class MapCompatibilityTests
{
    [TestMethod]
    public void RootLegacyVersion_WinsOverNestedEditorVersion()
    {
        const string json = """{"_version":"2.0.0","customData":{"editor":{"version":"123"}}}""";

        Assert.IsTrue(InfoDatParser.TryParse(json, out var metadata, out _));

        Assert.AreEqual(BeatMapFormatKind.LegacyV2, metadata.Format.Kind);
        Assert.AreEqual(new Version(2, 0, 0), metadata.Format.ParsedVersion);
        Assert.AreEqual("_version", metadata.Format.RawRootVersionProperty);
    }

    [TestMethod]
    public void RootV4Version_WithV4Structure_IsV4()
    {
        const string json = """{"version":"4.0.1","song":{},"audio":{},"difficultyBeatmaps":[]}""";

        Assert.IsTrue(InfoDatParser.TryParse(json, out var metadata, out _));

        Assert.AreEqual(BeatMapFormatKind.V4, metadata.Format.Kind);
        Assert.AreEqual(new Version(4, 0, 1), metadata.Format.ParsedVersion);
    }

    [TestMethod]
    public void NestedVersionOnly_IsNeverV4()
    {
        const string json = """{"customData":{"editors":{"ChroMapper":{"version":"4.0.1"}}}}""";

        Assert.IsTrue(InfoDatParser.TryParse(json, out var metadata, out _));

        Assert.AreEqual(BeatMapFormatKind.Unknown, metadata.Format.Kind);
    }

    [TestMethod]
    public void AmbiguousOrMalformedRoot_IsUnknown()
    {
        const string ambiguous = """{"_version":"2.0.0","version":"4.0.1","song":{},"audio":{},"difficultyBeatmaps":[]}""";
        Assert.IsTrue(InfoDatParser.TryParse(ambiguous, out var metadata, out _));
        Assert.AreEqual(BeatMapFormatKind.Unknown, metadata.Format.Kind);
        Assert.IsFalse(InfoDatParser.TryParse("{", out metadata, out _));
        Assert.AreEqual(BeatMapFormatKind.Unknown, metadata.Format.Kind);
    }

    [TestMethod]
    public void BeatSaber135_V4RegressionMap_IsIncompatible()
    {
        var target = BeatSaberPackageVersion.Create("1.35.0_8016709773", 1130);
        var format = new BeatMapFormatInfo(BeatMapFormatKind.V4, new Version(4, 0, 1), "version");

        var result = MapCompatibilityPolicy.Evaluate(format, target);

        Assert.AreEqual(MapCompatibilityStatus.Incompatible, result.Status,
            "Regression: 73F85C364E9C4EF7EE99FFF551BD3431D237B209 must be blocked by format policy, not by hash.");
    }

    [TestMethod]
    public void BeatSaber135_KnownGoodLegacyV2_IsCompatible()
    {
        var target = BeatSaberPackageVersion.Create("1.35.0_8016709773", 1130);
        var format = new BeatMapFormatInfo(BeatMapFormatKind.LegacyV2, new Version(2, 0, 0), "_version");

        var result = MapCompatibilityPolicy.Evaluate(format, target);

        Assert.AreEqual(MapCompatibilityStatus.Compatible, result.Status,
            "Regression: 85C435F964D5B0F2BD6A616FDE10E9BD0BD14EB2 is a known-good Legacy V2 example.");
    }

    [TestMethod]
    public void V4WithoutProvenSongCoreCapability_RemainsUnknownEvenOnNewerApk()
    {
        var result = MapCompatibilityPolicy.Evaluate(
            new BeatMapFormatInfo(BeatMapFormatKind.V4, new Version(4, 0, 1), "version"),
            BeatSaberPackageVersion.Create("1.36.0", 1200));

        Assert.AreEqual(MapCompatibilityStatus.Unknown, result.Status);
    }

    [TestMethod]
    public async Task LocalPreflight_ParsesActualInfoDatRootBeforeWriterSession()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"qbsync-v4-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(directory, "Info.dat"),
                """{"version":"4.0.1","song":{},"audio":{},"difficultyBeatmaps":[]}""");

            var result = await new LocalMapCompatibilityInspector().InspectAsync(
                directory,
                BeatSaberPackageVersion.Create("1.35.0_8016709773", 1130));

            Assert.AreEqual(BeatMapFormatKind.V4, result.Format.Kind);
            Assert.AreEqual(MapCompatibilityStatus.Incompatible, result.Status);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
