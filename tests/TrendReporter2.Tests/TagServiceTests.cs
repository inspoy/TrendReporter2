using TrendReporter2.Core.Tags;

namespace TrendReporter2.Tests;

public sealed class TagServiceTests
{
    [Fact]
    public void FromWebExtractTags_NormalizesDedupesAndLimitsTags()
    {
        var service = new TagService();

        var tags = service.FromWebExtractTags(["#AI", " AI ", "监管", "金融", "政策", "风险", "市场", "extra", " "]);

        Assert.Equal(6, tags.Count);
        Assert.Contains(tags, tag => tag.Name == "ai" && tag.Source == TagSources.WebExtract && tag.Confidence == 0.9);
        Assert.Contains(tags, tag => tag.DisplayName == "监管" && tag.Category == TagCategories.Topic);
        Assert.Equal(tags.Count, tags.Select(tag => tag.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void FromLlmTags_NormalizesDedupesAndLimitsTags()
    {
        var service = new TagService();

        var tags = service.FromLlmTags([
            new TagLlmTag("#AI"),
            new TagLlmTag("ai"),
            new TagLlmTag("宏观 风险"),
            new TagLlmTag("金融"),
            new TagLlmTag("政策"),
            new TagLlmTag("市场"),
            new TagLlmTag("extra"),
            new TagLlmTag(" "),
        ]);

        Assert.Equal(6, tags.Count);
        Assert.Contains(tags, tag => tag.Name == "ai" && tag.Category == TagCategories.Topic && tag.Source == TagSources.Llm && tag.Confidence == 0.7);
        Assert.Contains(tags, tag => tag.Name == "宏观-风险" && tag.Category == TagCategories.Topic);
        Assert.Equal(tags.Count, tags.Select(tag => tag.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
