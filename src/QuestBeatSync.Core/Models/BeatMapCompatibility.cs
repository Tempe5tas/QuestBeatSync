namespace QuestBeatSync.Core.Models;

public enum BeatMapFormatKind
{
    LegacyV2,
    LegacyV3,
    V4,
    Unknown
}

public sealed record BeatMapFormatInfo(
    BeatMapFormatKind Kind,
    Version? ParsedVersion = null,
    string? RawRootVersionProperty = null)
{
    public static BeatMapFormatInfo Unknown { get; } = new(BeatMapFormatKind.Unknown);
    public string DisplayName => ParsedVersion is null ? Kind.ToString() : $"{Kind} {ParsedVersion}";
}

public sealed record BeatSaberPackageVersion(string VersionName, long? VersionCode, Version? ParsedVersion)
{
    public static BeatSaberPackageVersion Create(string? versionName, long? versionCode)
    {
        var normalized = string.IsNullOrWhiteSpace(versionName) ? string.Empty : versionName.Trim();
        var numeric = normalized.Split('_', 2)[0];
        return new BeatSaberPackageVersion(
            normalized,
            versionCode,
            Version.TryParse(numeric, out var parsed) ? parsed : null);
    }
}

public enum MapCompatibilityStatus
{
    Compatible,
    Incompatible,
    Unknown
}

public sealed record MapCompatibilityResult(
    MapCompatibilityStatus Status,
    BeatMapFormatInfo Format,
    BeatSaberPackageVersion? Target,
    string Message);

public static class MapCompatibilityPolicy
{
    private static readonly Version V4MinimumBeatSaber = new(1, 36);

    public static MapCompatibilityResult Evaluate(
        BeatMapFormatInfo format,
        BeatSaberPackageVersion? target)
    {
        ArgumentNullException.ThrowIfNull(format);
        if (format.Kind is BeatMapFormatKind.LegacyV2 or BeatMapFormatKind.LegacyV3)
            return new(MapCompatibilityStatus.Compatible, format, target, "Legacy map format is supported by the current compatibility policy.");
        if (format.Kind == BeatMapFormatKind.Unknown)
            return new(MapCompatibilityStatus.Unknown, format, target, "Map format could not be established safely.");
        if (target?.ParsedVersion is null)
            return new(MapCompatibilityStatus.Unknown, format, target, "Target Beat Saber version is unavailable or unparseable.");
        if (target.ParsedVersion < V4MinimumBeatSaber)
            return new(MapCompatibilityStatus.Incompatible, format, target, $"V4 maps are unsafe on Beat Saber {target.ParsedVersion}; Beat Saber 1.36 or later is required by current evidence.");
        return new(MapCompatibilityStatus.Unknown, format, target, "V4 support cannot be proven from the Beat Saber package version alone because SongCore capability is unknown.");
    }
}
