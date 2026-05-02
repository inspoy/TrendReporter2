using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.Content;

namespace TrendReporter2.Core.Events;

public sealed class EventMatcher : IEventMatcher
{
    private const int RepresentativeTitleLimit = 3;
    private const int KeyTermLimit = 12;
    private const int EntityLimit = 8;
    private const int AliasLimit = 8;
    private const int StableAnchorLimit = 8;
    private const int MinCjkAnchorLength = 2;
    private const int MaxMixedCjkAnchorLength = 4;

    private static readonly Regex StableAnchorTokenRegex = new(@"\b[\p{L}\p{N}][\p{L}\p{N}'&.-]*\b", RegexOptions.Compiled);

    private readonly AppConfig _config;
    private readonly IEventRepository _repository;
    private readonly IEventCandidateService _candidateService;
    private readonly IClusterLlmClient _clusterLlmClient;

    public EventMatcher(
        AppConfig config,
        IEventRepository repository,
        IEventCandidateService candidateService,
        IClusterLlmClient clusterLlmClient)
    {
        _config = config;
        _repository = repository;
        _candidateService = candidateService;
        _clusterLlmClient = clusterLlmClient;
    }

    public async Task<EventMatchRunResult> MatchRunAsync(string runId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var items = await _repository.LoadUnmappedRunContentItemsAsync(runId, cancellationToken);
        var created = 0;
        var merged = 0;
        var reactivated = 0;
        var mapped = 0;
        var skipped = 0;

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var candidates = await _candidateService.RecallAsync(item, now, cancellationToken);
            var match = candidates.Count > 0 && _clusterLlmClient.IsConfigured
                ? await _clusterLlmClient.MatchAsync(new ClusterMatchRequest(item, candidates), cancellationToken)
                : ClusterMatchResult.CreateNew(candidates.Count == 0 ? "no recalled candidates" : "cluster llm is not configured");
            var targetEvent = await ResolveTargetEventAsync(item, candidates, match, now, cancellationToken);
            if (targetEvent.CreatedNew)
            {
                created++;
            }
            else
            {
                merged++;
                if (targetEvent.Reactivated)
                {
                    reactivated++;
                }
            }

            var mappedNow = await _repository.MapEventItemIfMissingAsync(new EventItem
            {
                Id = BuildEventItemId(targetEvent.Event.Id, item.Id),
                EventId = targetEvent.Event.Id,
                ContentItemId = item.Id,
                Confidence = targetEvent.Confidence,
                MatchedAt = now,
                MatchReason = targetEvent.Reason
            }, cancellationToken);

            if (mappedNow)
            {
                mapped++;
            }
            else
            {
                skipped++;
            }
        }

        return new EventMatchRunResult(items.Count, created, merged, reactivated, mapped, skipped);
    }

    private async Task<EventMatchOutcome> ResolveTargetEventAsync(
        ContentItem item,
        IReadOnlyList<EventCandidate> candidates,
        ClusterMatchResult match,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var matchedCandidate = FindMatchedCandidate(candidates, match);
        var canMergeSameEvent = match.Decision == ClusterDecisions.SameEvent &&
            match.Confidence >= _config.Analysis.Event.MergeThreshold &&
            matchedCandidate is not null;
        var canMergeFollowUp = match.Decision == ClusterDecisions.FollowUp &&
            match.Confidence >= _config.Analysis.Event.StaleMergeThreshold &&
            matchedCandidate is not null &&
            HasConservativeFollowUpSignal(item, matchedCandidate.Event);

        if (!canMergeSameEvent && !canMergeFollowUp)
        {
            var newEvent = CreateEvent(item, match, now);
            await _repository.UpsertEventAsync(newEvent, cancellationToken);
            return new EventMatchOutcome(newEvent, true, false, 1, match.Reason ?? "created new event");
        }

        var eventAggregate = await _repository.GetEventAsync(matchedCandidate!.Event.Id, cancellationToken) ?? matchedCandidate.Event;
        var reactivated = eventAggregate.LastSeenAt < now.AddHours(-Math.Max(1, _config.Analysis.Event.StaleHours));
        UpdateExistingEvent(eventAggregate, item, match, now, reactivated);
        await _repository.UpsertEventAsync(eventAggregate, cancellationToken);
        return new EventMatchOutcome(eventAggregate, false, reactivated, match.Confidence, match.Reason ?? match.Decision);
    }

    private static EventCandidate? FindMatchedCandidate(IReadOnlyList<EventCandidate> candidates, ClusterMatchResult match)
        => string.IsNullOrWhiteSpace(match.EventId)
            ? null
            : candidates.FirstOrDefault(candidate => candidate.Event.Id == match.EventId);

    private static bool HasConservativeFollowUpSignal(ContentItem item, EventAggregate eventAggregate)
    {
        var incomingAnchors = ExtractStableAnchors(item.Title, item.Summary, item.HoverText);
        if (incomingAnchors.Count == 0)
        {
            return false;
        }

        return HasAnchorOverlap(incomingAnchors, eventAggregate.Entities) ||
            HasAnchorOverlap(incomingAnchors, eventAggregate.Aliases);
    }

    private static EventAggregate CreateEvent(ContentItem item, ClusterMatchResult match, DateTimeOffset now)
    {
        var title = FirstNonEmpty(match.CanonicalTitle, item.Title, item.Summary) ?? "Untitled event";
        var summary = FirstNonEmpty(match.Summary, item.Summary, item.HoverText, item.Title) ?? title;
        var entities = ExtractStableAnchors(title, summary, item.Title, item.Summary, item.HoverText);
        var aliases = ExtractStableAnchors(item.Title, match.CanonicalTitle, title);
        return new EventAggregate
        {
            Id = BuildEventId(item),
            Type = EventType.NewsEvent,
            CanonicalTitle = title,
            Summary = summary,
            Entities = entities,
            Aliases = aliases,
            KeyTerms = ExtractKeyTerms(title, summary),
            RepresentativeTitles = string.IsNullOrWhiteSpace(item.Title) ? [] : [item.Title.Trim()],
            Status = EventStatus.Active,
            FirstSeenAt = now,
            LastSeenAt = now,
            LastActivatedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static void UpdateExistingEvent(
        EventAggregate eventAggregate,
        ContentItem item,
        ClusterMatchResult match,
        DateTimeOffset now,
        bool reactivated)
    {
        if (!string.IsNullOrWhiteSpace(match.CanonicalTitle))
        {
            eventAggregate.CanonicalTitle = match.CanonicalTitle.Trim();
        }

        if (!string.IsNullOrWhiteSpace(match.Summary))
        {
            eventAggregate.Summary = match.Summary.Trim();
        }

        eventAggregate.LastSeenAt = now;
        eventAggregate.UpdatedAt = now;
        if (reactivated)
        {
            eventAggregate.Status = EventStatus.Active;
            eventAggregate.LastActivatedAt = now;
        }

        AddUnique(eventAggregate.RepresentativeTitles, item.Title, RepresentativeTitleLimit);
        var terms = ExtractKeyTerms(item.Title, item.Summary, item.HoverText);
        foreach (var term in terms)
        {
            AddUnique(eventAggregate.KeyTerms, term, KeyTermLimit);
        }

        foreach (var entity in ExtractStableAnchors(item.Title, item.Summary, item.HoverText))
        {
            AddUnique(eventAggregate.Entities, entity, EntityLimit);
        }

        foreach (var alias in ExtractStableAnchors(item.Title, match.CanonicalTitle, match.Summary, eventAggregate.CanonicalTitle))
        {
            AddUnique(eventAggregate.Aliases, alias, AliasLimit);
        }
    }

    private static void AddUnique(List<string> values, string? value, int limit)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var normalized = value.Trim();
        if (values.Any(existing => string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        values.Insert(0, normalized);
        if (values.Count > limit)
        {
            values.RemoveRange(limit, values.Count - limit);
        }
    }

    private static List<string> ExtractKeyTerms(params string?[] values)
        => EventCandidateService.Tokenize(string.Join(' ', values.Where(value => !string.IsNullOrWhiteSpace(value))))
            .OrderByDescending(token => token.Length)
            .ThenBy(token => token, StringComparer.OrdinalIgnoreCase)
            .Take(KeyTermLimit)
            .ToList();

    private static List<string> ExtractStableAnchors(params string?[] values)
    {
        var anchors = new List<string>();

        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            foreach (var anchor in ExtractStableAnchorsFromText(value))
            {
                AddUnique(anchors, anchor, StableAnchorLimit);
            }
        }

        return anchors;
    }

    private static IEnumerable<string> ExtractStableAnchorsFromText(string text)
    {
        var tokens = StableAnchorTokenRegex.Matches(text)
            .Select(match => match.Value.Trim())
            .Where(token => token.Length > 0)
            .ToList();

        var run = new List<string>();
        foreach (var token in tokens)
        {
            if (IsStableAnchorToken(token))
            {
                run.Add(token);
                yield return token;
                continue;
            }

            foreach (var phrase in BuildStableAnchorPhrases(run))
            {
                yield return phrase;
            }

            run.Clear();
        }

        foreach (var phrase in BuildStableAnchorPhrases(run))
        {
            yield return phrase;
        }
    }

    private static IEnumerable<string> BuildStableAnchorPhrases(IReadOnlyList<string> run)
    {
        if (run.Count < 2)
        {
            yield break;
        }

        yield return string.Join(' ', run);
    }

    private static bool IsStableAnchorToken(string token)
    {
        if (token.Length < 2)
        {
            return false;
        }

        var lower = token.ToLowerInvariant();
        if (StableAnchorStopWords.Contains(lower))
        {
            return false;
        }

        if (ContainsCjkCharacter(token))
        {
            var cjkCount = CountCjkCharacters(token);
            if (cjkCount >= MinCjkAnchorLength)
            {
                return true;
            }

            return cjkCount == 1 && token.Length <= MaxMixedCjkAnchorLength && ContainsAsciiLetterOrDigit(token);
        }

        if (token.Any(char.IsDigit))
        {
            return true;
        }

        var upperCount = token.Count(char.IsUpper);
        if (upperCount >= 2)
        {
            return true;
        }

        var hasUpper = token.Any(char.IsUpper);
        var hasLower = token.Any(char.IsLower);
        if (hasUpper && hasLower)
        {
            return true;
        }

        return token.Length >= 3 && char.IsUpper(token[0]) && token.Skip(1).All(character => char.IsLower(character));
    }

    private static bool ContainsCjkCharacter(string token)
        => token.Any(IsCjkCharacter);

    private static int CountCjkCharacters(string token)
        => token.Count(IsCjkCharacter);

    private static bool ContainsAsciiLetterOrDigit(string token)
        => token.Any(character =>
            character is >= '0' and <= '9' ||
            character is >= 'A' and <= 'Z' ||
            character is >= 'a' and <= 'z');

    private static bool IsCjkCharacter(char character)
        => (character >= '\u4E00' && character <= '\u9FFF') ||
            (character >= '\u3400' && character <= '\u4DBF') ||
            (character >= '\u3040' && character <= '\u30FF') ||
            (character >= '\uAC00' && character <= '\uD7AF');

    private static bool HasAnchorOverlap(IReadOnlyCollection<string> incomingAnchors, IReadOnlyCollection<string> existingAnchors)
        => incomingAnchors.Any(anchor => existingAnchors.Any(existing => string.Equals(anchor, existing, StringComparison.OrdinalIgnoreCase)));

    private static readonly HashSet<string> StableAnchorStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "from", "this", "that", "into", "after", "about", "news", "update", "over", "under", "into", "between"
    };

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string BuildEventId(ContentItem item)
        => $"evt:{ShortHash($"{item.Source}|{item.SourceItemId}|{item.Title}")}";

    private static string BuildEventItemId(string eventId, string contentItemId)
        => $"ei:{ShortHash($"{eventId}|{contentItemId}")}";

    private static string ShortHash(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim()));
        return Convert.ToHexString(hash)[..20].ToLowerInvariant();
    }

    private sealed record EventMatchOutcome(
        EventAggregate Event,
        bool CreatedNew,
        bool Reactivated,
        double Confidence,
        string Reason);
}
