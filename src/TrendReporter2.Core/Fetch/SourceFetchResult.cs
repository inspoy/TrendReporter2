using TrendReporter2.Core.News;

namespace TrendReporter2.Core.Fetch;

public sealed record SourceFetchResult(
    string Category,
    string Source,
    bool Success,
    IReadOnlyList<NewsItem> Items,
    string? Error)
{
    public static SourceFetchResult Succeeded(string category, string source, IReadOnlyList<NewsItem> items)
        => new(category, source, true, items, null);

    public static SourceFetchResult Failed(string category, string source, Exception exception)
        => new(category, source, false, [], exception.Message);
}
