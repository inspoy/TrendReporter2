namespace TrendReporter2.Core.News;

public interface INewsSourceClient
{
    Task<IReadOnlyList<NewsItem>> FetchAsync(string category, string source, CancellationToken cancellationToken);
}
