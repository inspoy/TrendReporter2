using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TrendReporter2.Core.Tags;

public sealed class TagService : ITagService
{
    private const int MaxTags = 6;

    public IReadOnlyList<TagAssignment> FromWebExtractTags(IEnumerable<string> tags)
    {
        return tags
            .Select(tag => Normalize(tag, TagCategories.Topic, TagSources.WebExtract, 0.9))
            .Where(tag => tag is not null)
            .Select(tag => tag!)
            .DistinctBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxTags)
            .ToList();
    }

    public IReadOnlyList<TagAssignment> FromLlmTags(IEnumerable<TagLlmTag> tags)
    {
        return tags
            .Select(tag => Normalize(
                string.IsNullOrWhiteSpace(tag.DisplayName) ? tag.Name : tag.DisplayName,
                NormalizeCategory(tag.Category),
                TagSources.Llm,
                tag.Confidence ?? 0.7))
            .Where(tag => tag is not null)
            .Select(tag => tag!)
            .DistinctBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxTags)
            .ToList();
    }

    private static TagAssignment? Normalize(string? value, string category, string source, double confidence)
    {
        var displayName = NormalizeDisplay(value);
        if (string.IsNullOrWhiteSpace(displayName) || IsWeakToken(displayName))
        {
            return null;
        }

        var name = BuildStableName(displayName);
        return string.IsNullOrWhiteSpace(name)
            ? null
            : new TagAssignment(name, displayName, category, source, Math.Clamp(confidence, 0, 1));
    }

    private static string NormalizeCategory(string? category)
    {
        var value = category?.Trim().ToLowerInvariant();
        return value is TagCategories.Topic or TagCategories.Entity or TagCategories.Domain or TagCategories.Risk
            ? value
            : TagCategories.Topic;
    }

    private static string NormalizeDisplay(string? value)
        => Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim().Trim('#', '＃', '，', ',', '。', '.', '：', ':');

    private static string BuildStableName(string value)
    {
        var normalized = value.ToLower(CultureInfo.InvariantCulture);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (char.IsLetterOrDigit(ch) || ch >= 0x4e00 && ch <= 0x9fff)
            {
                builder.Append(ch);
            }
            else if (char.IsWhiteSpace(ch) || ch is '-' or '_' or '.')
            {
                builder.Append('-');
            }
        }

        return Regex.Replace(builder.ToString(), "-+", "-").Trim('-');
    }

    private static bool IsWeakToken(string value)
    {
        if (value.Length < 2 || value.Length > 32)
        {
            return true;
        }

        var lower = value.ToLower(CultureInfo.InvariantCulture);
        return lower is "http" or "https" or "www" or "com" or "news" or "the" or "and" or "with" or "from";
    }
}
