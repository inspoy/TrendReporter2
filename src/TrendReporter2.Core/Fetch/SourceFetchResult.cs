using TrendReporter2.Core.Sources;

namespace TrendReporter2.Core.Fetch;

public sealed record SourceFetchResult(
    SourceDefinition Definition,
    string Category,
    string Source,
    bool Success,
    IReadOnlyList<FetchedContentItem> Items,
    string? Error)
{
    public static SourceFetchResult Succeeded(SourceDefinition definition, IReadOnlyList<FetchedContentItem> items)
        => new(definition, definition.Category, definition.ExternalId, true, items, null);

    public static SourceFetchResult Failed(SourceDefinition definition, string error)
        => new(definition, definition.Category, definition.ExternalId, false, [], error);

    public static SourceFetchResult Failed(SourceDefinition definition, Exception exception)
        => Failed(definition, exception.Message);
}
