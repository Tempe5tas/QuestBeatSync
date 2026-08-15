using System.Text.Json;
using QuestBeatSync.Core.Models;

namespace QuestBeatSync.Infrastructure.Scanning;

public sealed record PlaylistFileMetadata(
    string PlaylistTitle,
    int SongReferenceCount,
    string? Warning = null);

public static class PlaylistFileParser
{
    private static readonly string[] TitleProperties =
        ["playlistTitle", "_playlistTitle", "playlistName", "title", "name"];

    private static readonly string[] SongCollectionProperties =
        ["songs", "_songs", "maps", "levels", "songList"];

    public static bool TryParse(
        string filename,
        string json,
        out PlaylistFileMetadata metadata,
        out string? warning)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filename);

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Failed(filename, "Playlist root must be a JSON object.", out metadata, out warning);
            }

            var root = document.RootElement;
            var title = GetString(root, TitleProperties) ?? GetFallbackTitle(filename);
            var songCount = GetArrayLength(root, SongCollectionProperties);
            var schemaWarning = songCount is null
                ? "Playlist JSON has no recognized song reference array."
                : null;

            metadata = new PlaylistFileMetadata(title, songCount ?? 0, schemaWarning);
            warning = schemaWarning;
            return true;
        }
        catch (JsonException exception)
        {
            return Failed(filename, $"Playlist is malformed: {exception.Message}", out metadata, out warning);
        }
    }

    private static bool Failed(
        string filename,
        string message,
        out PlaylistFileMetadata metadata,
        out string? warning)
    {
        metadata = new PlaylistFileMetadata(GetFallbackTitle(filename), 0, message);
        warning = message;
        return false;
    }

    private static string? GetString(JsonElement root, IEnumerable<string> names)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (names.Any(name => string.Equals(name, property.Name, StringComparison.OrdinalIgnoreCase)) &&
                property.Value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(property.Value.GetString()))
            {
                return property.Value.GetString()!.Trim();
            }
        }

        return null;
    }

    private static int? GetArrayLength(JsonElement root, IEnumerable<string> names)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (names.Any(name => string.Equals(name, property.Name, StringComparison.OrdinalIgnoreCase)) &&
                property.Value.ValueKind == JsonValueKind.Array)
            {
                return property.Value.GetArrayLength();
            }
        }

        return null;
    }

    private static string GetFallbackTitle(string filename)
    {
        const string bmbfSuffix = "_BMBF.json";
        return filename.EndsWith(bmbfSuffix, StringComparison.OrdinalIgnoreCase)
            ? filename[..^bmbfSuffix.Length]
            : Path.GetFileNameWithoutExtension(filename);
    }
}
