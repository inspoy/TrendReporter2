namespace TrendReporter2.Core.Sources;

public interface ISourceRepository
{
    Task UpsertSourcesAsync(IReadOnlyList<SourceDefinition> sources, CancellationToken cancellationToken);
}
