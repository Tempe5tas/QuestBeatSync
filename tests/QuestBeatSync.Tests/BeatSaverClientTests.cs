using System.Net;
using System.Net.Http.Headers;
using QuestBeatSync.Core.Models;
using QuestBeatSync.Infrastructure.BeatSaver;

namespace QuestBeatSync.Tests;

[TestClass]
public sealed class BeatSaverClientTests
{
    private const string HashA = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string HashB = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    [TestMethod]
    public async Task LookupAsync_ExactHash_ReturnsOnlineDownload()
    {
        var handler = new QueueHandler(Response(HttpStatusCode.OK, MapJson(HashA)));
        var client = CreateClient(handler);

        var result = await client.LookupAsync(new BeatSaverLookupRequest(HashA, "1a2b"));

        Assert.AreEqual(BeatSaverAvailability.Online, result.Availability);
        Assert.IsTrue(result.ExactHashMatched);
        Assert.AreEqual(HashA, result.ResolvedHash);
        Assert.IsTrue(result.CanDownload);
        StringAssert.Contains(handler.RequestUris.Single().AbsolutePath, $"/maps/hash/{HashA}");
    }

    [TestMethod]
    public async Task LookupAsync_NotFound_ReturnsUnavailable()
    {
        var client = CreateClient(new QueueHandler(Response(HttpStatusCode.NotFound)));

        var result = await client.LookupAsync(new BeatSaverLookupRequest(HashA, "1a2b"));

        Assert.AreEqual(BeatSaverAvailability.Unavailable, result.Availability);
    }

    [TestMethod]
    public async Task LookupAsync_MissingHash_UsesKeyEndpoint()
    {
        var handler = new QueueHandler(Response(HttpStatusCode.OK, MapJson(HashA)));
        var client = CreateClient(handler);

        var result = await client.LookupAsync(new BeatSaverLookupRequest(null, "1a2b"));

        Assert.AreEqual(BeatSaverAvailability.Online, result.Availability);
        Assert.AreEqual(HashA, result.ResolvedHash);
        StringAssert.Contains(handler.RequestUris.Single().AbsolutePath, "/maps/id/1A2B");
    }

    [TestMethod]
    public async Task LookupAsync_Timeout_ReturnsUnknown()
    {
        var client = CreateClient(new QueueHandler(_ => throw new TaskCanceledException("timeout")));

        var result = await client.LookupAsync(new BeatSaverLookupRequest(HashA, null));

        Assert.AreEqual(BeatSaverAvailability.Unknown, result.Availability);
        StringAssert.Contains(result.Message!, "timed out");
    }

    [TestMethod]
    public async Task LookupAsync_RateLimit_RetriesThenSucceeds()
    {
        var rateLimited = Response(HttpStatusCode.TooManyRequests);
        rateLimited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
        var handler = new QueueHandler(rateLimited, Response(HttpStatusCode.OK, MapJson(HashA)));
        var delays = new List<TimeSpan>();
        var client = CreateClient(handler, (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        });

        var result = await client.LookupAsync(new BeatSaverLookupRequest(HashA, null));

        Assert.AreEqual(BeatSaverAvailability.Online, result.Availability);
        Assert.HasCount(1, delays);
        Assert.HasCount(2, handler.RequestUris);
    }

    [TestMethod]
    public async Task LookupAsync_RateLimitAfterRetries_ReturnsUnknown()
    {
        var responses = Enumerable.Range(0, 4)
            .Select(_ => Response(HttpStatusCode.TooManyRequests))
            .ToArray();
        var handler = new QueueHandler(responses);
        var client = CreateClient(handler, (_, _) => Task.CompletedTask);

        var result = await client.LookupAsync(new BeatSaverLookupRequest(HashA, null));

        Assert.AreEqual(BeatSaverAvailability.Unknown, result.Availability);
        Assert.HasCount(4, handler.RequestUris);
    }

    [TestMethod]
    public async Task LookupAsync_RequestedHashMissingFromReturnedVersions_DoesNotSubstituteNewVersion()
    {
        var handler = new QueueHandler(Response(HttpStatusCode.OK, MapJson(HashB)));
        var client = CreateClient(handler);

        var result = await client.LookupAsync(new BeatSaverLookupRequest(HashA, "1a2b"));

        Assert.AreEqual(BeatSaverAvailability.Unavailable, result.Availability);
        Assert.IsFalse(result.ExactHashMatched);
        Assert.IsNull(result.DownloadUri);
        Assert.HasCount(1, handler.RequestUris);
        StringAssert.Contains(handler.RequestUris[0].AbsolutePath, "/maps/hash/");
    }

    [TestMethod]
    public async Task LookupAsync_ServerFailure_ReturnsUnknown()
    {
        var client = CreateClient(new QueueHandler(Response(HttpStatusCode.ServiceUnavailable)));

        var result = await client.LookupAsync(new BeatSaverLookupRequest(HashA, null));

        Assert.AreEqual(BeatSaverAvailability.Unknown, result.Availability);
    }

    private static BeatSaverClient CreateClient(
        HttpMessageHandler handler,
        Func<TimeSpan, CancellationToken, Task>? delay = null) =>
        new(
            new HttpClient(handler),
            new BeatSaverClientOptions
            {
                RequestTimeout = TimeSpan.FromSeconds(1),
                InitialBackoff = TimeSpan.Zero,
                MaxBackoff = TimeSpan.Zero
            },
            delay);

    private static HttpResponseMessage Response(HttpStatusCode status, string? content = null) =>
        new(status)
        {
            Content = new StringContent(content ?? string.Empty)
        };

    private static string MapJson(string hash) => $$"""
        {
          "id": "1a2b",
          "versions": [
            {
              "hash": "{{hash}}",
              "downloadURL": "https://cdn.beatsaver.com/{{hash}}.zip"
            }
          ]
        }
        """;

    private sealed class QueueHandler : HttpMessageHandler
    {
        private readonly Queue<Func<CancellationToken, HttpResponseMessage>> _responses;

        public QueueHandler(params HttpResponseMessage[] responses) :
            this(responses.Select<HttpResponseMessage, Func<CancellationToken, HttpResponseMessage>>(
                response => _ => response).ToArray())
        {
        }

        public QueueHandler(params Func<CancellationToken, HttpResponseMessage>[] responses) =>
            _responses = new Queue<Func<CancellationToken, HttpResponseMessage>>(responses);

        public List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!);
            return Task.FromResult(_responses.Dequeue()(cancellationToken));
        }
    }
}
