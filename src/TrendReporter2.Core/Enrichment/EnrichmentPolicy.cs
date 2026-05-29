using System.Text.RegularExpressions;
using System.Text;
using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.Content;
using TrendReporter2.Core.News;
using TrendReporter2.Core.Sources;

namespace TrendReporter2.Core.Enrichment;

public sealed class EnrichmentPolicy : IEnrichmentPolicy
{
    private static readonly string[] IncompleteTitleKeywords =
    [
        "详情",
        "来了",
        "突发",
        "更新中",
        "最新",
        "快讯",
        "发生了什么",
        "怎么回事",
        "一图看懂"
    ];

    private static readonly Regex EntityLikeText = new(
        @"[\p{IsCJKUnifiedIdeographs}]{2,}|[A-Z][A-Za-z0-9_-]{2,}",
        RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private readonly AppConfig _config;
    private readonly HashSet<string> _enabledSources;
    private readonly HashSet<string> _disabledSources;

    public EnrichmentPolicy(AppConfig config)
    {
        _config = config;
        _enabledSources = new HashSet<string>(
            config.Enrichment.EnabledSources.Where(source => !string.IsNullOrWhiteSpace(source)),
            StringComparer.OrdinalIgnoreCase);
        _disabledSources = new HashSet<string>(
            config.Enrichment.DisabledSources.Where(source => !string.IsNullOrWhiteSpace(source)),
            StringComparer.OrdinalIgnoreCase);
    }

    public bool NeedEnrichment(NewsItem item)
    {
        return NeedEnrichment(
            item.Source,
            item.Title,
            item.HoverText);
    }

    public bool NeedEnrichment(FetchedContentItem item)
    {
        return NeedEnrichment(
            item.SourceId,
            item.Title,
            item.HoverText ?? item.SummaryText);
    }

    public bool NeedEnrichment(ContentItem item)
    {
        return NeedEnrichment(
            item.Source,
            item.Title,
            item.HoverText);
    }

    private bool NeedEnrichment(string source, string title, string? hoverText)
    {
        if (_disabledSources.Contains(source))
        {
            return false;
        }

        if (_enabledSources.Contains(source))
        {
            return true;
        }

        if (HoverLooksComplete(hoverText))
        {
            return false;
        }

        var normalizedTitle = Normalize(title);
        if (TextLength(normalizedTitle) < _config.Enrichment.MinTitleLength)
        {
            return true;
        }

        if (IncompleteTitleKeywords.Any(keyword => normalizedTitle.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return !HasRecognizableSubject(normalizedTitle) && !HoverLooksComplete(hoverText);
    }

    private static bool HoverLooksComplete(string? hoverText)
    {
        var normalizedHover = Normalize(hoverText);
        return TextLength(normalizedHover) >= 40 && HasRecognizableSubject(normalizedHover);
    }

    private static bool HasRecognizableSubject(string text)
        => EntityLikeText.IsMatch(text);

    private static string Normalize(string? value)
        => Whitespace.Replace(value ?? string.Empty, " ").Trim();

    private static int TextLength(string value)
        => value.EnumerateRunes().Count();
}
