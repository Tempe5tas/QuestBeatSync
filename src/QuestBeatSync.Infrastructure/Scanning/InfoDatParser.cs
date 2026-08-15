using System.Text.Json;

namespace QuestBeatSync.Infrastructure.Scanning;

public sealed record InfoDatMetadata(string? SongTitle, string? Mapper);

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
                metadata = new InfoDatMetadata(null, null);
                warning = "Info.dat root must be a JSON object.";
                return false;
            }

            var root = document.RootElement;
            var title = GetString(root, "_songName", "songName") ??
                        GetNestedString(root, "song", "title");
            var mapper = GetString(root, "_levelAuthorName", "levelAuthorName", "mapAuthor");

            metadata = new InfoDatMetadata(title, mapper);
            warning = null;
            return true;
        }
        catch (JsonException exception)
        {
            metadata = new InfoDatMetadata(null, null);
            warning = $"Info.dat is malformed: {exception.Message}";
            return false;
        }
    }

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

