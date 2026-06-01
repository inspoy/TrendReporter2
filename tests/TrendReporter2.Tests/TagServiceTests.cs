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
    public void FromLlmTags_NormalizesCategoriesConfidenceDedupeAndLimitsTags()
    {
        var service = new TagService();

        var tags = service.FromLlmTags([
            new TagLlmTag("AI", "#AI", TagCategories.Entity, 1.5),
            new TagLlmTag("ai", "AI", TagCategories.Topic, 0.1),
            new TagLlmTag("宏观 风险", null, TagCategories.Risk, -0.2),
            new TagLlmTag("无效分类", null, "bad", 0.8),
            new TagLlmTag("金融", null, TagCategories.Domain, 0.7),
            new TagLlmTag("政策", null, TagCategories.Topic, 0.7),
            new TagLlmTag("市场", null, TagCategories.Topic, 0.7),
            new TagLlmTag("extra", null, TagCategories.Topic, 0.7)
        ]);

        Assert.Equal(6, tags.Count);
        Assert.Contains(tags, tag => tag.Name == "ai" && tag.Category == TagCategories.Entity && tag.Source == TagSources.Llm && tag.Confidence == 1);
        Assert.Contains(tags, tag => tag.Name == "宏观-风险" && tag.Category == TagCategories.Risk && tag.Confidence == 0);
        Assert.Contains(tags, tag => tag.Name == "无效分类" && tag.Category == TagCategories.Topic);
        Assert.Equal(tags.Count, tags.Select(tag => tag.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
