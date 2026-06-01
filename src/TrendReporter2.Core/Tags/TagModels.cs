using TrendReporter2.Core.Content;

namespace TrendReporter2.Core.Tags;

public sealed class Tag
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Category { get; init; } = TagCategories.Topic;

    public DateTimeOffset CreatedAt { get; init; }
}

public sealed record TagAssignment(
    string Name,
    string DisplayName,
    string Category,
    string Source,
    double Confidence);

public sealed record ContentItemTag(
    string ContentItemId,
    Tag Tag,
    double Confidence,
    string Source,
    DateTimeOffset CreatedAt);

public sealed record EventTag(
    string EventId,
    Tag Tag,
    double Confidence,
    string Source,
    DateTimeOffset CreatedAt);

public sealed record TagLlmRequest(string RunId, ContentItem ContentItem);

public sealed record TagLlmTag(string Name, string? DisplayName = null, string? Category = null, double? Confidence = null);

public sealed record TagLlmResult(IReadOnlyList<TagAssignment> Tags);

public interface ITagLlmClient
{
    bool IsConfigured { get; }

    Task<TagLlmResult> GenerateTagsAsync(TagLlmRequest request, CancellationToken cancellationToken);
}

public interface ITagService
{
    IReadOnlyList<TagAssignment> FromWebExtractTags(IEnumerable<string> tags);

    IReadOnlyList<TagAssignment> FromLlmTags(IEnumerable<TagLlmTag> tags);
}

public interface ITagRepository
{
    Task UpsertContentTagsAsync(string contentItemId, IReadOnlyList<TagAssignment> tags, DateTimeOffset now, CancellationToken cancellationToken);

    Task<IReadOnlyList<ContentItem>> LoadRunContentItemsWithoutTagsAsync(string runId, CancellationToken cancellationToken);

    Task RefreshEventTagsForRunAsync(string runId, DateTimeOffset now, CancellationToken cancellationToken);

    Task UpsertEventTagsAsync(string eventId, IReadOnlyList<TagAssignment> tags, DateTimeOffset now, CancellationToken cancellationToken);

    Task<IReadOnlyList<EventTag>> LoadEventTagsAsync(IReadOnlyList<string> eventIds, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> LoadEventIdsByTagAsync(string tagName, CancellationToken cancellationToken);
}

public static class TagCategories
{
    public const string Topic = "topic";
    public const string Entity = "entity";
    public const string Domain = "domain";
    public const string Risk = "risk";
}

public static class TagSources
{
    public const string WebExtract = "web_extract";
    public const string Rule = "rule";
    public const string Llm = "llm";
    public const string Manual = "manual";
}
