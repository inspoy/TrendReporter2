using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.Sources;

namespace TrendReporter2.Tests;

public sealed class SourceRegistryTests
{
    [Fact]
    public void GetEnabledSourcesByProvider_GroupsEnabledDailyHotApiRankedAndFlashSources()
    {
        var config = new AppConfig
        {
            Sources = new SourcesConfig
            {
                DailyHotApi = new SourceProviderConfig
                {
                    BaseUrl = "https://hot.local",
                    Items =
                    [
                        new SourceItemConfig
                        {
                            Id = "dailyhotapi:social:weibo",
                            ExternalId = "weibo",
                            Category = "social",
                            DisplayName = "微博热搜",
                            ContentKind = ContentKind.RankedNews,
                            Enabled = true,
                            Weight = 1.0
                        },
                        new SourceItemConfig
                        {
                            Id = "dailyhotapi:finance:cls-telegraph",
                            ExternalId = "cls-telegraph",
                            Category = "finance",
                            DisplayName = "财联社电报",
                            ContentKind = ContentKind.FlashFeed,
                            Enabled = true,
                            Weight = 1.0
                        },
                        new SourceItemConfig
                        {
                            Id = "dailyhotapi:social:douyin",
                            ExternalId = "douyin",
                            Category = "social",
                            DisplayName = "抖音热点",
                            ContentKind = ContentKind.RankedNews,
                            Enabled = false,
                            Weight = 1.0
                        }
                    ]
                }
            }
        };

        var registry = new SourceRegistry(config);

        var groupedSources = registry.GetEnabledSourcesByProvider();
        var dailyHotApiSources = Assert.Single(groupedSources, pair => pair.Key == SourceProviders.DailyHotApi).Value;
        Assert.Equal(2, dailyHotApiSources.Count);
        Assert.Contains(dailyHotApiSources, source => source.ExternalId == "weibo" && source.ContentKind == ContentKind.RankedNews);
        Assert.Contains(dailyHotApiSources, source => source.ExternalId == "cls-telegraph" && source.ContentKind == ContentKind.FlashFeed);
        Assert.DoesNotContain(dailyHotApiSources, source => source.ExternalId == "douyin");
    }
}
