using System.Text;
using TrendReporter2.Core.Configuration;

namespace TrendReporter2.Core.Sources;

public sealed class SourceRegistry : ISourceRegistry
{
    private readonly IReadOnlyList<SourceDefinition> sources;

    public SourceRegistry(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        sources = BuildSources(config);
    }

    public IReadOnlyList<SourceDefinition> GetSources() => sources;

    public IReadOnlyList<SourceDefinition> GetEnabledSources()
        => sources.Where(source => source.Enabled).ToList();

    public IReadOnlyDictionary<string, IReadOnlyList<SourceDefinition>> GetEnabledSourcesByProvider()
        => GetEnabledSources()
            .GroupBy(source => source.Provider, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<SourceDefinition>)group.ToList(),
                StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<SourceDefinition> BuildSources(AppConfig config)
    {
        var result = new List<SourceDefinition>();

        AddConfiguredSources(result, SourceProviders.NewsNow, config.Sources.NewsNow.Items);
        AddConfiguredSources(result, SourceProviders.DailyHotApi, config.Sources.DailyHotApi.Items);
        AddLegacyNewsNowSources(result, config.NewsNow.Sources);

        return result;
    }

    private static void AddConfiguredSources(
        List<SourceDefinition> result,
        string provider,
        IEnumerable<SourceItemConfig> items)
    {
        foreach (var item in items)
        {
            result.Add(new SourceDefinition(
                item.Id,
                provider,
                item.ExternalId,
                item.Category,
                item.DisplayName,
                item.ContentKind,
                item.Enabled,
                item.Weight));
        }
    }

    private static void AddLegacyNewsNowSources(
        List<SourceDefinition> result,
        IReadOnlyDictionary<string, List<string>> legacySources)
    {
        var existingNewsNowKeys = result
            .Where(source => string.Equals(source.Provider, SourceProviders.NewsNow, StringComparison.OrdinalIgnoreCase))
            .Select(source => EquivalentKey(source.Provider, source.ExternalId, source.ContentKind))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (category, sourceNames) in legacySources)
        {
            foreach (var sourceName in sourceNames)
            {
                var key = EquivalentKey(SourceProviders.NewsNow, sourceName, ContentKind.RankedNews);
                if (existingNewsNowKeys.Contains(key))
                {
                    continue;
                }

                result.Add(new SourceDefinition(
                    BuildLegacyNewsNowId(category, sourceName),
                    SourceProviders.NewsNow,
                    sourceName,
                    category,
                    sourceName,
                    ContentKind.RankedNews,
                    true,
                    1.0));

                existingNewsNowKeys.Add(key);
            }
        }
    }

    private static string EquivalentKey(string provider, string externalId, string contentKind)
        => string.Join(':', provider, externalId, contentKind);

    private static string BuildLegacyNewsNowId(string category, string source)
        => $"{SourceProviders.NewsNow}:{NormalizeIdPart(category)}:{NormalizeIdPart(source)}";

    private static string NormalizeIdPart(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim().ToLowerInvariant())
        {
            builder.Append(char.IsLetterOrDigit(character) ? character : '-');
        }

        return builder.ToString().Trim('-');
    }
}
