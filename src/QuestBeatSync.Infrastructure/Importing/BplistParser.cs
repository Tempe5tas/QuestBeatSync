using System.Text.Json;
using QuestBeatSync.Core.Models;

namespace QuestBeatSync.Infrastructure.Importing;

public static class BplistParser
{
    public static Playlist Parse(string json, string? sourcePath = null)
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new BplistParseException("Playlist root must be a JSON object.");
            }

            var root = document.RootElement;
            var title = GetRequiredString(root, "playlistTitle");
            var playlist = new Playlist(
                title,
                GetOptionalString(root, "playlistAuthor"),
                GetOptionalString(root, "playlistDescription"),
                GetOptionalString(root, "image"),
                GetNestedOptionalString(root, "customData", "syncURL"),
                sourcePath);

            if (!root.TryGetProperty("songs", out var songs) || songs.ValueKind != JsonValueKind.Array)
            {
                throw new BplistParseException("Playlist property 'songs' must be a JSON array.");
            }

            foreach (var song in songs.EnumerateArray())
            {
                if (song.ValueKind != JsonValueKind.Object)
                {
                    playlist.Add(new PlaylistEntry(null, null, null));
                    continue;
                }

                playlist.Add(new PlaylistEntry(
                    GetOptionalString(song, "key"),
                    GetOptionalString(song, "hash"),
                    GetOptionalString(song, "songName")));
            }

            return playlist;
        }
        catch (JsonException exception)
        {
            throw new BplistParseException(
                $"Malformed .bplist JSON at line {exception.LineNumber}, byte {exception.BytePositionInLine}: {exception.Message}",
                exception);
        }
    }

    private static string GetRequiredString(JsonElement element, string propertyName)
    {
        var value = GetOptionalString(element, propertyName);
        return value ?? throw new BplistParseException(
            $"Playlist property '{propertyName}' is required and must be a non-empty string.");
    }

    private static string? GetOptionalString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = property.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? GetNestedOptionalString(
        JsonElement element,
        string objectPropertyName,
        string stringPropertyName)
    {
        return element.TryGetProperty(objectPropertyName, out var nested) &&
               nested.ValueKind == JsonValueKind.Object
            ? GetOptionalString(nested, stringPropertyName)
            : null;
    }
}

