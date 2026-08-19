using System.Text.RegularExpressions;
using QuestBeatSync.Core.Models;
using QuestBeatSync.Infrastructure.Abstractions;

namespace QuestBeatSync.Infrastructure.Scanning;

public sealed partial class AdbBeatSaberPackageInspector(IQuestTransport transport) : IBeatSaberPackageInspector
{
    public async Task<BeatSaberPackageVersion?> InspectAsync(QuestDevice device, CancellationToken cancellationToken = default)
    {
        var result = await transport.ExecuteShellAsync(
            device,
            ["dumpsys", "package", "com.beatgames.beatsaber"],
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess) return null;
        var name = VersionNameRegex().Match(result.StandardOutput);
        var code = VersionCodeRegex().Match(result.StandardOutput);
        if (!name.Success && !code.Success) return null;
        return BeatSaberPackageVersion.Create(
            name.Success ? name.Groups[1].Value : null,
            code.Success && long.TryParse(code.Groups[1].Value, out var parsedCode) ? parsedCode : null);
    }

    [GeneratedRegex(@"(?m)^\s*versionName=([^\s]+)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionNameRegex();

    [GeneratedRegex(@"(?m)^\s*versionCode=(\d+)\b", RegexOptions.CultureInvariant)]
    private static partial Regex VersionCodeRegex();
}

public sealed class LocalMapCompatibilityInspector : ILocalMapCompatibilityInspector
{
    public async Task<MapCompatibilityResult> InspectAsync(
        string localMapDirectory,
        BeatSaberPackageVersion? target,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localMapDirectory);
        var infoPath = Directory.EnumerateFiles(localMapDirectory)
            .FirstOrDefault(path => string.Equals(Path.GetFileName(path), "Info.dat", StringComparison.OrdinalIgnoreCase));
        if (infoPath is null)
            return MapCompatibilityPolicy.Evaluate(BeatMapFormatInfo.Unknown, target);
        var json = await File.ReadAllTextAsync(infoPath, cancellationToken).ConfigureAwait(false);
        return InfoDatParser.TryParse(json, out var metadata, out _)
            ? MapCompatibilityPolicy.Evaluate(metadata.Format, target)
            : MapCompatibilityPolicy.Evaluate(BeatMapFormatInfo.Unknown, target);
    }
}
