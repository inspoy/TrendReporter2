namespace TrendReporter2.Core.Sources;

public interface IContentSourceClient
{
    string Provider { get; }

    Task<IReadOnlyList<FetchedContentItem>> FetchAsync(
        SourceDefinition source,
        CancellationToken cancellationToken);
}
