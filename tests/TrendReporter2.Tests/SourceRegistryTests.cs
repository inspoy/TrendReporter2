using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.Sources;

namespace TrendReporter2.Tests;

public sealed class SourceRegistryTests
{
    [Fact]
    public void GetEnabledSources_MapsLegacyNewsNowSourcesWhenNoEquivalentNewItemExists()
    {
        var config = new AppConfig
        {
            NewsNow = new NewsNowConfig
            {
                BaseUrl = "https://news.local",
                Sources = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["china"] = ["ifeng"]
                }
            }
        };

        var registry = new SourceRegistry(config);

        var source = Assert.Single(registry.GetEnabledSources());
        Assert.Equal("newsnow:china:ifeng", source.Id);
        Assert.Equal(SourceProviders.NewsNow, source.Provider);
        Assert.Equal("ifeng", source.ExternalId);
        Assert.Equal("china", source.Category);
        Assert.Equal("ifeng", source.DisplayName);
        Assert.Equal(ContentKind.RankedNews, source.ContentKind);
        Assert.True(source.Enabled);
        Assert.Equal(1.0, source.Weight);
    }

    [Fact]
    public void GetSources_DoesNotAddLegacyNewsNowSourceWhenEquivalentNewItemExists()
    {
        var config = new AppConfig
        {
            NewsNow = new NewsNowConfig
            {
                BaseUrl = "https://news.local",
                Sources = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["china"] = ["ifeng"]
                }
            },
            Sources = new SourcesConfig
            {
                NewsNow = new SourceProviderConfig
                {
                    BaseUrl = "https://news.local",
                    Items =
                    [
                        new SourceItemConfig
                        {
                            Id = "custom-newsnow-ifeng",
                            ExternalId = "ifeng",
                            Category = "china",
                            DisplayName = "凤凰网",
                            ContentKind = ContentKind.RankedNews,
                            Enabled = false,
                            Weight = 2.0
                        }
                    ]
                }
            }
        };

        var registry = new SourceRegistry(config);

        var source = Assert.Single(registry.GetSources());
        Assert.Equal("custom-newsnow-ifeng", source.Id);
        Assert.False(source.Enabled);
        Assert.Empty(registry.GetEnabledSources());
    }

    [Fact]
    public void GetEnabledSourcesByProvider_GroupsEnabledDailyHotApiRankedAndFlashSources()
    {
        var config = new AppConfig
        {
            NewsNow = new NewsNowConfig { BaseUrl = "https://news.local" },
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
