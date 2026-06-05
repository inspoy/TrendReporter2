using Microsoft.Extensions.Logging.Abstractions;
using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.Content;
using TrendReporter2.Core.Events;
using TrendReporter2.Infrastructure.Configuration;
using TrendReporter2.Infrastructure.Llm;

namespace TrendReporter2.Tests;

public sealed class LocalLlmSmokeTests
{
    private const string RunEnvVar = "TRENDREPORTER2_RUN_LOCAL_LLM_SMOKE";

    [Fact]
    public async Task ClusterLlmClient_UsesLocalConfigForSimpleModelCall()
    {
        if (Environment.GetEnvironmentVariable(RunEnvVar) != "1")
        {
            return;
        }

        var configPath = Path.Combine(FindRepositoryRoot(), "config.yaml");
        Assert.True(File.Exists(configPath), $"设置 {RunEnvVar}=1 时必须存在 {configPath}");

        var config = new YamlAppConfigLoader().Load(configPath);
        AssertConfigured(config.Llm.Cluster);

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };
        var client = new ClusterLlmClient(httpClient, config, NullLoggerFactory.Instance);
        var result = await client.MatchAsync(new ClusterMatchRequest(
            "local-llm-smoke",
            new ContentItem
            {
                Id = "ci:local-smoke",
                Title = "某公司发布新一代 AI 芯片",
                Summary = "该公司称新芯片将提升大模型推理性能。",
                Source = "smoke"
            },
            [new EventCandidate(
                new EventAggregate
                {
                    Id = "evt:local-smoke",
                    CanonicalTitle = "某公司发布新一代 AI 芯片",
                    Summary = "新芯片用于提升 AI 推理性能",
                    KeyTerms = ["AI芯片", "推理性能"],
                    RepresentativeTitles = ["某公司发布新一代 AI 芯片"]
                },
                0.9,
                ["title"])]), CancellationToken.None);

        Assert.Contains(result.Decision, new[]
        {
            ClusterDecisions.SameEvent,
            ClusterDecisions.FollowUp,
            ClusterDecisions.RelatedButDistinct,
            ClusterDecisions.Unrelated
        });
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TrendReporter2.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }

    private static void AssertConfigured(LlmEndpointConfig config)
    {
        Assert.False(string.IsNullOrWhiteSpace(config.BaseUrl), "config.yaml 必须配置 llm.cluster.baseUrl");
        Assert.False(string.IsNullOrWhiteSpace(config.Model), "config.yaml 必须配置 llm.cluster.model");
    }
}
