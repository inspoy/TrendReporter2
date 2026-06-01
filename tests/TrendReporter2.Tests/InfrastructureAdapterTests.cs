using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.Content;
using TrendReporter2.Core.Enrichment;
using TrendReporter2.Core.Events;
using TrendReporter2.Core.Observability;
using TrendReporter2.Core.Sources;
using TrendReporter2.Core.Tags;
using TrendReporter2.Infrastructure.Enrichment;
using TrendReporter2.Infrastructure.Llm;
using TrendReporter2.Infrastructure.News;
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
        IContentSourceClient client = new NewsNowClient(new HttpClient(handler), Config(newsNowBaseUrl: "https://news.local"), NullLoggerFactory.Instance);

        var source = new SourceDefinition("newsnow:tech:source-a", SourceProviders.NewsNow, "source-a", "tech", "source-a", ContentKind.RankedNews, true, 1.0);
        var items = await client.FetchAsync(source, CancellationToken.None);

        Assert.Equal(2, items.Count);
        Assert.Equal("https://news.local/api/s?id=source-a", handler.Requests.Single().RequestUri?.ToString());
        Assert.Equal("n1", items[0].SourceItemId);
        Assert.False(string.IsNullOrWhiteSpace(items[1].SourceItemId));
        Assert.Equal(2, items[1].Rank);
        Assert.Equal(4, items[1].SourceListSize);
        Assert.Equal("摘要一", items[0].SummaryText);
    }

    [Fact]
    public async Task NewsNowClient_ContentSourcePath_UsesExternalIdAndRankedMetadata()
    {
        var handler = new TestHttpMessageHandler(_ => TestHttpMessageHandler.Json(HttpStatusCode.OK, """
        {
          "status": "success",
          "items": [
            { "id": "n1", "title": "第一条", "url": "https://example.com/1", "mobileUrl": "https://m.example.com/1", "pubDate": 1710000000, "extra": { "hover": "摘要一" } }
          ]
        }
        """));
        IContentSourceClient client = new NewsNowClient(new HttpClient(handler), Config(newsNowBaseUrl: "https://news.local"), NullLoggerFactory.Instance);

        var items = await client.FetchAsync(Source(ContentKind.RankedNews, provider: SourceProviders.NewsNow, externalId: "source-a"), CancellationToken.None);

        var item = Assert.Single(items);
        Assert.Equal("https://news.local/api/s?id=source-a", handler.Requests.Single().RequestUri?.ToString());
        Assert.Equal(SourceProviders.NewsNow, client.Provider);
        Assert.Equal("source-id", item.SourceId);
        Assert.Equal(ContentKind.RankedNews, item.ContentKind);
        Assert.Equal("tech", item.Category);
        Assert.Equal("n1", item.SourceItemId);
        Assert.Equal("source-id:n1", item.DedupKey);
        Assert.Equal(1, item.Rank);
        Assert.Equal(1, item.SourceListSize);
        Assert.Equal("https://m.example.com/1", item.MobileUrl);
        Assert.Equal("摘要一", item.SummaryText);
    }

    [Fact]
    public async Task NewsNowClient_FlashFeed_LeavesRankNullAndParsesPublishedTime()
    {
        var handler = new TestHttpMessageHandler(_ => TestHttpMessageHandler.Json(HttpStatusCode.OK, """
        {
          "status": "success",
          "items": [
            { "id": "flash-1", "title": "快讯一", "url": "https://example.com/flash", "pubDate": 1710000000, "extra": { "hover": "快讯摘要" } }
          ]
        }
        """));
        IContentSourceClient client = new NewsNowClient(new HttpClient(handler), Config(newsNowBaseUrl: "https://news.local"), NullLoggerFactory.Instance);

        var items = await client.FetchAsync(Source(ContentKind.FlashFeed, provider: SourceProviders.NewsNow, externalId: "flash-a"), CancellationToken.None);

        var item = Assert.Single(items);
        Assert.Equal("https://news.local/api/s?id=flash-a", handler.Requests.Single().RequestUri?.ToString());
        Assert.Equal(ContentKind.FlashFeed, item.ContentKind);
        Assert.Null(item.Rank);
        Assert.Null(item.SourceListSize);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1710000000), item.PublishedAt);
        Assert.Equal("快讯摘要", item.SummaryText);
    }

    [Fact]
    public async Task DailyHotApiClient_RankedNews_ParsesItemsAndRequestUri()
    {
        var handler = new TestHttpMessageHandler(_ => TestHttpMessageHandler.Json(HttpStatusCode.OK, """
        {
          "code": 200,
          "data": [
            { "id": "dh-1", "title": "热榜一", "url": "https://example.com/1", "mobileUrl": "https://m.example.com/1", "desc": "摘要一", "extra": { "hover": "悬浮一" } },
            { "name": "热榜二", "link": "https://example.com/2", "hot": "100万" },
            "bad",
            { "title": "", "url": "", "summary": "" }
          ]
        }
        """));
        var client = new DailyHotApiClient(new HttpClient(handler), Config(dailyHotApiBaseUrl: "https://hot.local/api"), NullLoggerFactory.Instance);

        var items = await client.FetchAsync(Source(ContentKind.RankedNews, externalId: "weibo hot"), CancellationToken.None);

        Assert.Equal("https://hot.local/api/weibo%20hot", handler.Requests.Single().RequestUri?.OriginalString);
        Assert.Equal(SourceProviders.DailyHotApi, client.Provider);
        Assert.Equal(2, items.Count);
        Assert.Equal("dh-1", items[0].SourceItemId);
        Assert.Equal("热榜一", items[0].Title);
        Assert.Equal("https://m.example.com/1", items[0].MobileUrl);
        Assert.Equal("摘要一", items[0].SummaryText);
        Assert.Equal(1, items[0].Rank);
        Assert.Equal(4, items[0].SourceListSize);
        Assert.Equal(2, items[1].Rank);
        Assert.True(string.IsNullOrEmpty(items[1].SummaryText));
    }

    [Fact]
    public async Task DailyHotApiClient_FlashFeed_LeavesRankNullAndParsesPublishedTime()
    {
        var handler = new TestHttpMessageHandler(_ => TestHttpMessageHandler.Json(HttpStatusCode.OK, """
        {
          "data": [
            { "title": "快讯一", "url": "https://example.com/flash", "summary": "快讯摘要", "extra": { "date": "2026-05-05T08:00:00Z" } }
          ]
        }
        """));
        var client = new DailyHotApiClient(new HttpClient(handler), Config(dailyHotApiBaseUrl: "https://hot.local"), NullLoggerFactory.Instance);

        var items = await client.FetchAsync(Source(ContentKind.FlashFeed, externalId: "kuaixun"), CancellationToken.None);

        var item = Assert.Single(items);
        Assert.Equal(ContentKind.FlashFeed, item.ContentKind);
        Assert.Null(item.Rank);
        Assert.Null(item.SourceListSize);
        Assert.Equal(new DateTimeOffset(2026, 5, 5, 8, 0, 0, TimeSpan.Zero), item.PublishedAt);
        Assert.False(string.IsNullOrWhiteSpace(item.SourceItemId));
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
        Assert.Equal(["第一段", "第二段"], result.Tags);

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
    public void EnrichmentService_PrefersSuccessfulEnrichmentOverTitleOnlySummary()
    {
        var summary = InvokeBuildPreferredSummary(
            new ContentItem
            {
                Title = "短标题",
                Summary = "短标题",
                SummarySource = SummarySources.TitleOnly
            },
            "网页抽取返回的完整摘要");

        Assert.Equal("网页抽取返回的完整摘要", summary.Value);
        Assert.Equal(SummarySources.Enrichment, summary.Source);
    }

    [Fact]
    public void EnrichmentService_PreservesSourceSummaryOverSuccessfulEnrichment()
    {
        var summary = InvokeBuildPreferredSummary(
            new ContentItem
            {
                Title = "短标题",
                Summary = "来源直接返回的摘要",
                SummarySource = SummarySources.SummaryText
            },
            "网页抽取返回的完整摘要");

        Assert.Equal("来源直接返回的摘要", summary.Value);
        Assert.Equal(SummarySources.SummaryText, summary.Source);
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
    public async Task ClusterLlmClient_RecordsUsageTokensCostAndRetryCount()
    {
        var requestCount = 0;
        var handler = new TestHttpMessageHandler(_ =>
        {
            requestCount++;
            return requestCount == 1
                ? TestHttpMessageHandler.Json(HttpStatusCode.BadGateway, "bad gateway")
                : TestHttpMessageHandler.Json(HttpStatusCode.OK, """
                {
                  "id": "chatcmpl-cluster-1",
                  "choices": [ { "message": { "content": "{\"decision\":\"same_event\",\"eventId\":\"ev-1\",\"confidence\":0.91,\"reason\":\"same story\"}" } } ],
                  "usage": { "prompt_tokens": 1000, "completion_tokens": 500, "prompt_tokens_details": { "cached_tokens": 200 } }
                }
                """);
        });
        var recorder = new TestTelemetryRecorder();
        var client = new ClusterLlmClient(new HttpClient(handler), ConfigWithLlm(), NullLoggerFactory.Instance, recorder);
        var item = ContentItem();
        var candidate = new EventCandidate(new EventAggregate { Id = "ev-1", CanonicalTitle = "事件一", Summary = "摘要" }, 0.8, ["title"]);

        var result = await client.MatchAsync(new ClusterMatchRequest("run-1", item, [candidate]), CancellationToken.None);

        Assert.Equal(ClusterDecisions.SameEvent, result.Decision);
        Assert.Equal(2, handler.Requests.Count);
        var usage = Assert.Single(recorder.LlmUsage);
        Assert.Equal("run-1", usage.RunId);
        Assert.Equal(LlmUsageStages.Cluster, usage.Stage);
        Assert.Equal("chatcmpl-cluster-1", usage.RequestId);
        Assert.Equal(item.Id, usage.ContentItemId);
        Assert.Equal("ev-1", usage.EventId);
        Assert.Equal(1000, usage.InputTokens);
        Assert.Equal(500, usage.OutputTokens);
        Assert.Equal(200, usage.CacheReadTokens);
        Assert.Equal(1, usage.RetryCount);
        Assert.True(usage.Success);
        Assert.Equal(0.0017m, usage.EstimatedCost);
    }


    [Fact]
    public async Task TagLlmClient_ParsesTagsAndRecordsUsage()
    {
        var handler = new TestHttpMessageHandler(_ => TestHttpMessageHandler.Json(HttpStatusCode.OK, """
        {
          "id": "chatcmpl-tag-1",
          "choices": [ { "message": { "content": "{\"tags\":[{\"name\":\"OpenAI\",\"displayName\":\"OpenAI\",\"category\":\"entity\",\"confidence\":0.95},{\"name\":\"监管 风险\",\"category\":\"bad\",\"confidence\":1.2}]}" } } ],
          "usage": { "prompt_tokens": 200, "completion_tokens": 100, "prompt_tokens_details": { "cached_tokens": 50 } }
        }
        """));
        var recorder = new TestTelemetryRecorder();
        var client = new TagLlmClient(new HttpClient(handler), ConfigWithLlm(), new TagService(), NullLoggerFactory.Instance, recorder);
        var item = ContentItem();
        item.Title = "OpenAI 发布新模型引发监管风险讨论";
        item.Summary = "多国监管机构关注 AI 风险。";
        item.Category = "tech";

        var result = await client.GenerateTagsAsync(new TagLlmRequest("run-1", item), CancellationToken.None);

        Assert.Equal(2, result.Tags.Count);
        Assert.Contains(result.Tags, tag => tag.Name == "openai" && tag.Category == TagCategories.Entity && tag.Source == TagSources.Llm);
        Assert.Contains(result.Tags, tag => tag.Name == "监管-风险" && tag.Category == TagCategories.Topic && tag.Confidence == 1);
        Assert.Equal("https://llm.local/v1/chat/completions", handler.Requests.Single().RequestUri?.ToString());
        var usage = Assert.Single(recorder.LlmUsage);
        Assert.Equal(LlmUsageStages.Tagging, usage.Stage);
        Assert.Equal("tag-model", usage.Model);
        Assert.Equal("run-1", usage.RunId);
        Assert.Equal(item.Id, usage.ContentItemId);
        Assert.Equal("chatcmpl-tag-1", usage.RequestId);
        Assert.Equal(200, usage.InputTokens);
        Assert.Equal(100, usage.OutputTokens);
        Assert.Equal(50, usage.CacheReadTokens);
        Assert.True(usage.Success);
        Assert.Equal(0.00035m, usage.EstimatedCost);
    }

    [Fact]
    public async Task TagLlmClient_UnconfiguredAndFailuresReturnNoTags()
    {
        var unconfiguredHandler = new TestHttpMessageHandler(_ => TestHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var unconfigured = new TagLlmClient(new HttpClient(unconfiguredHandler), Config(), new TagService(), NullLoggerFactory.Instance);

        var unconfiguredResult = await unconfigured.GenerateTagsAsync(new TagLlmRequest("run-1", ContentItem()), CancellationToken.None);

        Assert.Empty(unconfiguredResult.Tags);
        Assert.Empty(unconfiguredHandler.Requests);

        var failureHandler = new TestHttpMessageHandler(_ => TestHttpMessageHandler.Json(HttpStatusCode.BadGateway, "bad gateway"));
        var recorder = new TestTelemetryRecorder();
        var failing = new TagLlmClient(new HttpClient(failureHandler), ConfigWithLlm(), new TagService(), NullLoggerFactory.Instance, recorder);

        var failureResult = await failing.GenerateTagsAsync(new TagLlmRequest("run-1", ContentItem()), CancellationToken.None);

        Assert.Empty(failureResult.Tags);
        Assert.Equal(4, failureHandler.Requests.Count);
        var usage = Assert.Single(recorder.LlmUsage);
        Assert.Equal(LlmUsageStages.Tagging, usage.Stage);
        Assert.False(usage.Success);
        Assert.Equal("HTTP 502", usage.Error);
        Assert.Equal(3, usage.RetryCount);
    }

    [Fact]
    public async Task JudgeLlmClient_RecordsFinalFailureAfterRetries()
    {
        var handler = new TestHttpMessageHandler(_ => TestHttpMessageHandler.Json(HttpStatusCode.BadGateway, "bad gateway"));
        var recorder = new TestTelemetryRecorder();
        var client = new JudgeLlmClient(new HttpClient(handler), ConfigWithLlm(), NullLoggerFactory.Instance, recorder);
        var eventAggregate = new EventAggregate { Id = "ev-1", CanonicalTitle = "事件一", Summary = "摘要" };

        var result = await client.JudgeAsync(new JudgeRequest("run-1", eventAggregate, new EventScore { EventId = "ev-1" }, [], []), CancellationToken.None);

        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal(0, result.BoostScore);
        var usage = Assert.Single(recorder.LlmUsage);
        Assert.Equal(LlmUsageStages.Judge, usage.Stage);
        Assert.Equal("ev-1", usage.EventId);
        Assert.Equal(3, usage.RetryCount);
        Assert.False(usage.Success);
        Assert.Equal("HTTP 502", usage.Error);
    }

    private static AppConfig Config(string webExtractUrl = "", string pusherUrl = "", string pusherSecret = "", string pusherCate = "default", string channels = "", string dailyHotApiBaseUrl = "", string newsNowBaseUrl = "")
        => new()
        {
            Database = new DatabaseConfig { Provider = "postgres", ConnectionString = "Host=localhost;Database=trend;Username=trend;Password=secret" },
            Sources = new SourcesConfig
            {
                NewsNow = new SourceProviderConfig { BaseUrl = newsNowBaseUrl },
                DailyHotApi = new SourceProviderConfig { BaseUrl = dailyHotApiBaseUrl }
            },
            Enrichment = new EnrichmentConfig { WebExtractUrl = webExtractUrl, MaxRequestsPerRun = 5, RetryCooldownHours = 12 },
            System = new SystemConfig { MaxParallelEnrichment = 1 },
            Pushers = string.IsNullOrWhiteSpace(pusherUrl) ? [] : [new PusherConfig { Type = "unipush", Url = pusherUrl, Secret = pusherSecret, Cate = pusherCate, Channels = channels }]
        };

    private static (string Value, string Source) InvokeBuildPreferredSummary(ContentItem item, string? enrichmentSummary)
    {
        var method = typeof(EnrichmentService).GetMethod(
            "BuildPreferredSummary",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var result = method.Invoke(null, [item, enrichmentSummary]);
        Assert.NotNull(result);
        return ((string Value, string Source))result;
    }

    private static SourceDefinition Source(string contentKind, string provider = SourceProviders.DailyHotApi, string externalId = "weibo")
        => new("source-id", provider, externalId, "tech", "微博", contentKind, true, 1.0);

    private static ContentItem ContentItem()
        => new() { Id = "ci-1", Title = "原标题", Url = "https://example.com/original" };

    private static AppConfig ConfigWithLlm()
        => new()
        {
            Llm = new LlmConfig
            {
                Cluster = new LlmEndpointConfig
                {
                    BaseUrl = "https://llm.local",
                    Model = "cluster-model",
                    Pricing = new LLmPricingConfig { Input = 1, Output = 1, CacheRead = 1 }
                },
                Judge = new LlmEndpointConfig
                {
                    BaseUrl = "https://llm.local",
                    Model = "judge-model",
                    Pricing = new LLmPricingConfig { Input = 1, Output = 1, CacheRead = 1 }
                },
                Tagging = new LlmEndpointConfig
                {
                    BaseUrl = "https://llm.local",
                    Model = "tag-model",
                    Pricing = new LLmPricingConfig { Input = 1, Output = 1, CacheRead = 1 }
                }
            }
        };

    private sealed class TestTelemetryRecorder : IRunTelemetryRecorder
    {
        public List<LlmUsageRecord> LlmUsage { get; } = [];

        public Task RecordSourceAsync(RunSourceTelemetry telemetry, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RecordStageAsync(RunStageTelemetry telemetry, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RecordLlmUsageAsync(LlmUsageRecord usage, CancellationToken cancellationToken)
        {
            LlmUsage.Add(usage);
            return Task.CompletedTask;
        }

        public Task<LlmUsageSummary> GetLlmUsageSummaryAsync(string runId, CancellationToken cancellationToken)
            => Task.FromResult(new LlmUsageSummary(LlmUsage.Count, LlmUsage.Sum(usage => usage.EstimatedCost)));
    }

}
