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

        return result;
    }

    private static void AddConfiguredSources(
        List<SourceDefinition> result,
        string provider,
        IEnumerable<SourceItemConfig> items)
    {
        foreach (var item in items)
        {
            var id = item.Id.Length > 0
                ? item.Id
                : $"{provider}:{item.Category}:{item.ExternalId}";

            result.Add(new SourceDefinition(
                id,
                provider,
                item.ExternalId,
                item.Category,
                item.DisplayName,
                item.ContentKind,
                item.Enabled,
                item.Weight,
                item.Param));
        }
    }
}
