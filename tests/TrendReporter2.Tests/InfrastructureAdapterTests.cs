using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.Content;
using TrendReporter2.Core.Events;
using TrendReporter2.Core.Observability;
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

    private static AppConfig Config(string webExtractUrl = "", string pusherUrl = "", string pusherSecret = "", string pusherCate = "default", string channels = "")
        => new()
        {
            Database = new DatabaseConfig { Provider = "postgres", ConnectionString = "Host=localhost;Database=trend;Username=trend;Password=secret" },
            Enrichment = new EnrichmentConfig { WebExtractUrl = webExtractUrl, MaxRequestsPerRun = 5, RetryCooldownHours = 12 },
            System = new SystemConfig { MaxParallelEnrichment = 1 },
            Pushers = string.IsNullOrWhiteSpace(pusherUrl) ? [] : [new PusherConfig { Type = "unipush", Url = pusherUrl, Secret = pusherSecret, Cate = pusherCate, Channels = channels }]
        };

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
