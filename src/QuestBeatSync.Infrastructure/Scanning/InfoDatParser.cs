using System.Text.Json;
using QuestBeatSync.Core.Models;

namespace QuestBeatSync.Infrastructure.Scanning;

public sealed record InfoDatMetadata(string? SongTitle, string? Mapper, BeatMapFormatInfo Format);

public static class InfoDatParser
{
    public static bool TryParse(
        string json,
        out InfoDatMetadata metadata,
        out string? warning)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                metadata = new InfoDatMetadata(null, null, BeatMapFormatInfo.Unknown);
                warning = "Info.dat root must be a JSON object.";
                return false;
            }

            var root = document.RootElement;
            var title = GetString(root, "_songName", "songName") ??
                        GetNestedString(root, "song", "title");
            var mapper = GetString(root, "_levelAuthorName", "levelAuthorName", "mapAuthor");

            metadata = new InfoDatMetadata(title, mapper, ParseFormat(root));
            warning = null;
            return true;
        }
        catch (JsonException exception)
        {
            metadata = new InfoDatMetadata(null, null, BeatMapFormatInfo.Unknown);
            warning = $"Info.dat is malformed: {exception.Message}";
            return false;
        }
    }

    private static BeatMapFormatInfo ParseFormat(JsonElement root)
    {
        var legacy = GetRootString(root, "_version");
        var modern = GetRootString(root, "version");
        if (legacy is not null && modern is not null) return BeatMapFormatInfo.Unknown;
        if (legacy is not null && Version.TryParse(legacy, out var legacyVersion))
            return legacyVersion.Major switch
            {
                2 => new(BeatMapFormatKind.LegacyV2, legacyVersion, "_version"),
                3 => new(BeatMapFormatKind.LegacyV3, legacyVersion, "_version"),
                _ => BeatMapFormatInfo.Unknown
            };
        if (modern is not null && Version.TryParse(modern, out var modernVersion) && modernVersion.Major == 4 &&
            HasProperty(root, "song") && HasProperty(root, "audio") && HasProperty(root, "difficultyBeatmaps"))
            return new(BeatMapFormatKind.V4, modernVersion, "version");
        return BeatMapFormatInfo.Unknown;
    }

    private static string? GetRootString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? Normalize(value.GetString())
            : null;

    private static bool HasProperty(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.Object or JsonValueKind.Array;

    private static string? GetString(JsonElement element, params string[] propertyNames)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (propertyNames.Any(name => string.Equals(name, property.Name, StringComparison.OrdinalIgnoreCase)) &&
                property.Value.ValueKind == JsonValueKind.String)
            {
                return Normalize(property.Value.GetString());
            }
        }

        return null;
    }

    private static string? GetNestedString(JsonElement root, string objectName, string propertyName)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, objectName, StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.Object)
            {
                return GetString(property.Value, propertyName);
            }
        }

        return null;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

