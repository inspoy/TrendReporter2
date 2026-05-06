using System.Net;
using LiteDB;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.Content;
using TrendReporter2.Core.Enrichment;
using TrendReporter2.Core.Events;
using TrendReporter2.Core.News;
using TrendReporter2.Core.Persistence;
using TrendReporter2.Infrastructure.Enrichment;
using TrendReporter2.Infrastructure.News;
using TrendReporter2.Infrastructure.Persistence;
using TrendReporter2.Infrastructure.Push;

namespace TrendReporter2.Tests;

public sealed class InfrastructureAdapterTests
{
    [Fact]
    public async Task NewsNowClient_ParsesSuccessResponseAndFallbackIds()
    {
        var handler = new TestHttpMessageHandler(_ => TestHttpMessageHandler.Json(HttpStatusCode.OK, """
        {
          "status": "cache",
          "items": [
            { "id": "n1", "title": "第一条", "url": "https://example.com/1", "mobileUrl": "https://m.example.com/1", "pubDate": 1710000000, "extra": { "hover": "摘要一" } },
            { "title": "第二条", "url": "https://example.com/2", "extra": { "date": "2026-05-05T08:00:00Z" } },
            "bad",
            { "title": "", "url": "" }
          ]
        }
        """));
        var client = new NewsNowClient(new HttpClient(handler), new AppConfig { NewsNow = new NewsNowConfig { BaseUrl = "https://news.local" } }, NullLoggerFactory.Instance);

        var items = await client.FetchAsync("tech", "source-a", CancellationToken.None);

        Assert.Equal(2, items.Count);
        Assert.Equal("https://news.local/api/s?id=source-a", handler.Requests.Single().RequestUri?.ToString());
        Assert.Equal("n1", items[0].SourceItemId);
        Assert.False(string.IsNullOrWhiteSpace(items[1].SourceItemId));
        Assert.Equal(2, items[1].Rank);
        Assert.Equal(4, items[1].SourceListSize);
        Assert.Equal("摘要一", items[0].HoverText);
    }

    [Fact]
    public async Task WebExtractEnrichmentClient_ParsesResponsesBeforeConsideringHttpStatus()
    {
        var successHandler = new TestHttpMessageHandler(_ => TestHttpMessageHandler.Json(HttpStatusCode.OK, """
        { "success": true, "data": { "title": "抽取标题", "url": "https://example.com/final", "insights": ["第一段", "第二段"] } }
        """));
        var client = new WebExtractEnrichmentClient(new HttpClient(successHandler), Config(webExtractUrl: "extract.local"), NullLoggerFactory.Instance);

        var result = await client.EnrichAsync(ContentItem(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("http://extract.local/fetch", successHandler.Requests.Single().RequestUri?.ToString());
        Assert.Equal("抽取标题", result.Title);
        Assert.Equal("第一段 第二段", result.Summary);

        var nonSuccessHandler = new TestHttpMessageHandler(_ => TestHttpMessageHandler.Json(HttpStatusCode.BadGateway, """
        { "success": true, "summary": "网关返回了可用正文", "title": "非 2xx 可用标题" }
        """));
        var nonSuccessClient = new WebExtractEnrichmentClient(new HttpClient(nonSuccessHandler), Config(webExtractUrl: "https://extract.local"), NullLoggerFactory.Instance);

        var nonSuccessResult = await nonSuccessClient.EnrichAsync(ContentItem(), CancellationToken.None);
        Assert.NotNull(nonSuccessResult);
        Assert.Equal("网关返回了可用正文", nonSuccessResult.Summary);

        var failedBodyHandler = new TestHttpMessageHandler(_ => TestHttpMessageHandler.Json(HttpStatusCode.BadGateway, """
        { "success": false, "message": "extract failed" }
        """));
        var failedBodyClient = new WebExtractEnrichmentClient(new HttpClient(failedBodyHandler), Config(webExtractUrl: "https://extract.local"), NullLoggerFactory.Instance);

        Assert.Null(await failedBodyClient.EnrichAsync(ContentItem(), CancellationToken.None));
    }

    [Fact]
    public async Task UnipushPusher_BuildsRequestPayloadAndHeader()
    {
        var handler = new TestHttpMessageHandler(_ => TestHttpMessageHandler.Json(HttpStatusCode.OK, "ok"));
        var pusher = new UnipushPusher(new HttpClient(handler), Config(pusherUrl: "https://push.local/send", pusherSecret: "secret", pusherCate: "trend", channels: "main"), NullLoggerFactory.Instance);

        var result = await pusher.PushAsync(new PushMessage
        {
            EventId = "event-1",
            Title = "推送标题",
            Message = "推送内容",
            Link = "https://example.com"
        }, CancellationToken.None);

        Assert.True(result.Success);
        var request = handler.Requests.Single();
        Assert.Equal("https://push.local/send?channels=main", request.RequestUri?.ToString());
        Assert.True(request.Headers.TryGetValues("Push-Key", out var values));
        Assert.Equal("secret", values.Single());
        var payload = JObject.Parse(result.Payload);
        Assert.Equal("trend", payload.Value<string>("cate"));
        Assert.Equal("推送标题", payload.Value<string>("title"));
        Assert.Equal("推送内容", payload.Value<string>("msg"));
        Assert.Equal("https://example.com", payload.Value<string>("link"));
    }

    [Fact]
    public async Task LiteDbInitializerAndContentIngest_AreIdempotentAndWriteSnapshots()
    {
        var path = TempDbPath();
        var config = Config(databasePath: path);
        var factory = new LiteDbConnectionFactory(config);
        var initializer = new LiteDbInitializer(config, factory, NullLoggerFactory.Instance);
        initializer.Initialize();
        initializer.Initialize();
        var ingest = new ContentIngestService(factory, new StubEnrichmentPolicy(true), NullLoggerFactory.Instance);
        var capturedAt = DateTimeOffset.Parse("2026-05-05T08:00:00Z");
        var news = new NewsItem
        {
            Source = "source-a",
            Category = "tech",
            SourceItemId = "item-1",
            Title = "短标题",
            Url = "https://example.com/1",
            Rank = 1,
            SourceListSize = 2,
            RawPayload = "{}"
        };

        var first = await ingest.IngestAsync("run-1", [news], capturedAt, CancellationToken.None);
        var second = await ingest.IngestAsync("run-2", [new NewsItem
        {
            Source = news.Source,
            Category = news.Category,
            SourceItemId = news.SourceItemId,
            Title = "短标题更新",
            Url = news.Url,
            Rank = news.Rank,
            SourceListSize = news.SourceListSize,
            RawPayload = news.RawPayload
        }], capturedAt.AddMinutes(1), CancellationToken.None);

        Assert.Equal(1, first.InsertedCount);
        Assert.Equal(1, second.UpdatedCount);
        using var database = new LiteDatabase($"Filename={path};Connection=shared");
        foreach (var collectionName in TrendCollectionNames.All)
        {
            Assert.True(database.CollectionExists(collectionName));
        }

        var contentItems = database.GetCollection<ContentItem>(TrendCollectionNames.ContentItem).FindAll().ToList();
        var snapshots = database.GetCollection<ContentSnapshot>(TrendCollectionNames.ContentSnapshot).FindAll().ToList();
        Assert.Single(contentItems);
        Assert.Equal("短标题更新", contentItems.Single().Title);
        Assert.Equal(EnrichmentStatuses.Pending, contentItems.Single().EnrichmentStatus);
        Assert.Equal(2, snapshots.Count);
        Assert.Contains(snapshots, snapshot => snapshot.RunId == "run-1" && snapshot.NormalizedRankScore == 1);
        Assert.Contains(snapshots, snapshot => snapshot.RunId == "run-2" && snapshot.NormalizedRankScore == 1);
    }

    [Fact]
    public async Task ContentIngest_DisabledEnrichmentSourceFallsBackToTitleOnly()
    {
        var path = TempDbPath();
        var config = Config(databasePath: path, disabledSources: ["source-a"]);
        var factory = new LiteDbConnectionFactory(config);
        new LiteDbInitializer(config, factory, NullLoggerFactory.Instance).Initialize();
        var ingest = new ContentIngestService(factory, new EnrichmentPolicy(config), NullLoggerFactory.Instance);
        var capturedAt = DateTimeOffset.Parse("2026-05-05T08:00:00Z");

        await ingest.IngestAsync("run-1", [new NewsItem
        {
            Source = "source-a",
            Category = "tech",
            SourceItemId = "item-1",
            Title = "突发",
            Url = "https://example.com/1",
            Rank = 1,
            SourceListSize = 1,
            RawPayload = "{}"
        }], capturedAt, CancellationToken.None);

        using var database = factory.Open();
        var item = database.GetCollection<ContentItem>(TrendCollectionNames.ContentItem).FindAll().Single();
        Assert.False(item.NeedEnrichment);
        Assert.Equal(EnrichmentStatuses.Skipped, item.EnrichmentStatus);
        Assert.Equal("突发", item.Summary);
        Assert.Equal(SummarySources.TitleOnly, item.SummarySource);
    }

    [Fact]
    public async Task EnrichmentService_WritesBackSuccessfulClientResult()
    {
        var path = TempDbPath();
        var config = Config(databasePath: path, webExtractUrl: "https://extract.local");
        var factory = new LiteDbConnectionFactory(config);
        new LiteDbInitializer(config, factory, NullLoggerFactory.Instance).Initialize();
        var startedAt = DateTimeOffset.Parse("2026-05-05T08:00:00Z");
        using (var database = factory.Open())
        {
            database.GetCollection<ContentItem>(TrendCollectionNames.ContentItem).Insert(new ContentItem
            {
                Id = "ci-1",
                DedupKey = "source|1",
                Source = "source",
                Category = "tech",
                SourceItemId = "1",
                Title = "短标题",
                Url = "https://example.com/1",
                NeedEnrichment = true,
                EnrichmentStatus = EnrichmentStatuses.Pending,
                LastSeenRunId = "run-1",
                LastSeenRank = 1,
                CreatedAt = startedAt,
                UpdatedAt = startedAt
            });
        }

        var service = new EnrichmentService(config, factory, new StubEnrichmentClient(), NullLoggerFactory.Instance);

        var result = await service.EnrichRunAsync("run-1", startedAt, CancellationToken.None);

        Assert.Equal(new EnrichmentRunResult(1, 1, 1, 0, 0), result);
        using var verify = factory.Open();
        var item = verify.GetCollection<ContentItem>(TrendCollectionNames.ContentItem).FindById("ci-1");
        Assert.Equal("富化标题", item.Title);
        Assert.Equal("富化摘要", item.Summary);
        Assert.Equal(SummarySources.Enrichment, item.SummarySource);
        Assert.Equal(EnrichmentStatuses.Succeeded, item.EnrichmentStatus);
    }

    private static AppConfig Config(string databasePath = "unused.db", string webExtractUrl = "", string pusherUrl = "", string pusherSecret = "", string pusherCate = "default", string channels = "", List<string>? disabledSources = null)
        => new()
        {
            Database = new DatabaseConfig { Path = databasePath },
            Enrichment = new EnrichmentConfig { WebExtractUrl = webExtractUrl, DisabledSources = disabledSources ?? [], MaxRequestsPerRun = 5, RetryCooldownHours = 12 },
            System = new SystemConfig { MaxParallelEnrichment = 1 },
            Pushers = string.IsNullOrWhiteSpace(pusherUrl) ? [] : [new PusherConfig { Type = "unipush", Url = pusherUrl, Secret = pusherSecret, Cate = pusherCate, Channels = channels }]
        };

    private static ContentItem ContentItem()
        => new() { Id = "ci-1", Title = "原标题", Url = "https://example.com/original" };

    private static string TempDbPath()
        => Path.Combine(Path.GetTempPath(), "TrendReporter2.Tests", Guid.NewGuid().ToString("N"), "trend.db");

    private sealed class StubEnrichmentPolicy : IEnrichmentPolicy
    {
        private readonly bool _needEnrichment;
        public StubEnrichmentPolicy(bool needEnrichment) => _needEnrichment = needEnrichment;
        public bool NeedEnrichment(NewsItem item) => _needEnrichment;
        public bool NeedEnrichment(ContentItem item) => _needEnrichment;
    }

    private sealed class StubEnrichmentClient : IEnrichmentClient
    {
        public Task<EnrichmentResult?> EnrichAsync(ContentItem item, CancellationToken cancellationToken)
            => Task.FromResult<EnrichmentResult?>(new EnrichmentResult { Title = "富化标题", Summary = "富化摘要", Url = item.Url, RawPayload = "{}" });
    }
}
