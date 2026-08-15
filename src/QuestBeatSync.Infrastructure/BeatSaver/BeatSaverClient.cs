using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using QuestBeatSync.Core.Models;
using QuestBeatSync.Infrastructure.Abstractions;

namespace QuestBeatSync.Infrastructure.BeatSaver;

public sealed class BeatSaverClient : IBeatSaverClient
{
    private readonly HttpClient _httpClient;
    private readonly BeatSaverClientOptions _options;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    public BeatSaverClient(
        HttpClient httpClient,
        BeatSaverClientOptions? options = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? new BeatSaverClientOptions();
        _delayAsync = delayAsync ?? Task.Delay;
    }

    public async Task<BeatSaverLookupResult> LookupAsync(
        BeatSaverLookupRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var requestedHash = Normalize(request.Hash);
        var requestedKey = Normalize(request.Key);
        if (requestedHash is null && requestedKey is null)
        {
            return BeatSaverLookupResult.Unknown(request, "Playlist entry has neither hash nor key.");
        }

        var relativePath = requestedHash is not null
            ? $"maps/hash/{Uri.EscapeDataString(requestedHash)}"
            : $"maps/id/{Uri.EscapeDataString(requestedKey!)}";

        try
        {
            using var response = await SendWithRateLimitRetryAsync(
                () => CreateRequest(new Uri(_options.BaseUri, relativePath)),
                HttpCompletionOption.ResponseContentRead,
                cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return BeatSaverLookupResult.Unavailable(
                    request,
                    requestedHash is not null
                        ? "BeatSaver explicitly reported that the requested hash does not exist."
                        : "BeatSaver explicitly reported that the requested key does not exist.");
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return BeatSaverLookupResult.Unknown(request, "BeatSaver rate limit remained active after retries.");
            }

            if ((int)response.StatusCode >= 500)
            {
                return BeatSaverLookupResult.Unknown(
                    request,
                    $"BeatSaver server error {(int)response.StatusCode}.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return BeatSaverLookupResult.Unknown(
                    request,
                    $"Unexpected BeatSaver response {(int)response.StatusCode}.");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseLookupResponse(json, request, requestedHash, requestedKey);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return BeatSaverLookupResult.Unknown(request, "BeatSaver request timed out.");
        }
        catch (HttpRequestException exception)
        {
            return BeatSaverLookupResult.Unknown(request, $"BeatSaver network error: {exception.Message}");
        }
        catch (JsonException exception)
        {
            return BeatSaverLookupResult.Unknown(request, $"BeatSaver returned invalid JSON: {exception.Message}");
        }
    }

    public async Task DownloadZipAsync(
        Uri downloadUri,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(downloadUri);
        ArgumentNullException.ThrowIfNull(destination);

        try
        {
            using var downloadTimeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            downloadTimeoutSource.CancelAfter(_options.RequestTimeout);
            using var response = await SendWithRateLimitRetryAsync(
                () => CreateRequest(downloadUri),
                HttpCompletionOption.ResponseHeadersRead,
                downloadTimeoutSource.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new BeatSaverRequestException(
                    $"BeatSaver download failed with HTTP {(int)response.StatusCode}.");
            }

            await using var source = await response.Content.ReadAsStreamAsync(downloadTimeoutSource.Token).ConfigureAwait(false);
            await source.CopyToAsync(destination, downloadTimeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BeatSaverRequestException("BeatSaver download timed out.");
        }
        catch (HttpRequestException exception)
        {
            throw new BeatSaverRequestException($"BeatSaver download network error: {exception.Message}", exception);
        }
    }

    private async Task<HttpResponseMessage> SendWithRateLimitRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        var backoff = _options.InitialBackoff;

        for (var attempt = 0; ; attempt++)
        {
            using var request = requestFactory();
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(_options.RequestTimeout);
            var response = await _httpClient.SendAsync(
                request,
                completionOption,
                timeoutSource.Token).ConfigureAwait(false);

            if (response.StatusCode != HttpStatusCode.TooManyRequests ||
                attempt >= _options.MaxRateLimitRetries)
            {
                return response;
            }

            var delay = GetRetryDelay(response.Headers.RetryAfter, backoff);
            response.Dispose();
            await _delayAsync(delay, cancellationToken).ConfigureAwait(false);
            backoff = TimeSpan.FromMilliseconds(Math.Min(
                Math.Max(1, backoff.TotalMilliseconds * 2),
                _options.MaxBackoff.TotalMilliseconds));
        }
    }

    private static BeatSaverLookupResult ParseLookupResponse(
        string json,
        BeatSaverLookupRequest request,
        string? requestedHash,
        string? requestedKey)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return BeatSaverLookupResult.Unknown(request, "BeatSaver response was not a map object.");
        }

        var resolvedKey = GetString(root, "id") ?? requestedKey;
        if (!root.TryGetProperty("versions", out var versions) || versions.ValueKind != JsonValueKind.Array)
        {
            return BeatSaverLookupResult.Unknown(request, "BeatSaver map response had no versions array.");
        }

        JsonElement? selectedVersion = null;
        foreach (var version in versions.EnumerateArray())
        {
            if (version.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var versionHash = Normalize(GetString(version, "hash"));
            if (requestedHash is null || StringComparer.OrdinalIgnoreCase.Equals(versionHash, requestedHash))
            {
                selectedVersion = version;
                break;
            }
        }

        if (selectedVersion is null)
        {
            return requestedHash is not null
                ? BeatSaverLookupResult.Unavailable(
                    request,
                    "BeatSaver returned the key/map, but not the playlist's exact requested hash.")
                : BeatSaverLookupResult.Unknown(request, "BeatSaver map had no downloadable version.");
        }

        var selectedHash = Normalize(GetString(selectedVersion.Value, "hash"));
        var downloadUrl = GetString(selectedVersion.Value, "downloadURL");
        if (selectedHash is null ||
            !Uri.TryCreate(downloadUrl, UriKind.Absolute, out var downloadUri))
        {
            return BeatSaverLookupResult.Unknown(request, "BeatSaver version had no valid hash or download URL.");
        }

        return new BeatSaverLookupResult(
            BeatSaverAvailability.Online,
            requestedHash,
            requestedKey,
            selectedHash,
            Normalize(resolvedKey),
            downloadUri,
            requestedHash is not null && StringComparer.OrdinalIgnoreCase.Equals(selectedHash, requestedHash));
    }

    private static HttpRequestMessage CreateRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("QuestBeatSync", "0.1"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static TimeSpan GetRetryDelay(RetryConditionHeaderValue? retryAfter, TimeSpan fallback)
    {
        if (retryAfter?.Delta is { } delta)
        {
            return delta;
        }

        if (retryAfter?.Date is { } date)
        {
            return date > DateTimeOffset.UtcNow ? date - DateTimeOffset.UtcNow : TimeSpan.Zero;
        }

        return fallback;
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();
}
