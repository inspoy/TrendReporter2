namespace TrendReporter2.Core.Sources;

public interface ISourceRegistry
{
    IReadOnlyList<SourceDefinition> GetSources();

    IReadOnlyList<SourceDefinition> GetEnabledSources();

    IReadOnlyDictionary<string, IReadOnlyList<SourceDefinition>> GetEnabledSourcesByProvider();
}
