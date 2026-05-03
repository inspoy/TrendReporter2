using System.Globalization;
using System.Text;
using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.Content;

namespace TrendReporter2.Core.Events;

public sealed class EventCandidateService : IEventCandidateService
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "from", "this", "that", "into", "after", "about", "news", "update",
        "一个", "最新", "突发", "视频", "详情", "回应", "发生", "报道", "消息", "相关"
    };

    private readonly AppConfig _config;
    private readonly IEventRepository _repository;

    public EventCandidateService(AppConfig config, IEventRepository repository)
    {
        _config = config;
        _repository = repository;
    }

    public async Task<IReadOnlyList<EventCandidate>> RecallAsync(ContentItem item, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var sourceText = JoinText(item.Title, item.Summary);
        var sourceTokens = Tokenize(sourceText);
        var sourceNgrams = BuildNgrams(NormalizeForNgrams(sourceText));
        var candidates = await _repository.LoadRecallCandidatesAsync(
            now,
            _config.Analysis.HistoryHours,
            _config.Analysis.Event.StaleHours,
            _config.Analysis.Event.ArchiveRecallDays,
            cancellationToken);

        return candidates
            .Select(candidate => ScoreCandidate(candidate, sourceTokens, sourceNgrams, now))
            .Where(candidate => candidate.Score > 0.08)
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Event.LastSeenAt)
            .ThenBy(candidate => candidate.Event.Id, StringComparer.Ordinal)
            .Take(Math.Max(1, _config.Analysis.Event.CandidateLimit))
            .ToList();
    }

    private static EventCandidate ScoreCandidate(
        EventAggregate eventAggregate,
        HashSet<string> sourceTokens,
        HashSet<string> sourceNgrams,
        DateTimeOffset now)
    {
        var candidateText = JoinText(
            eventAggregate.CanonicalTitle,
            eventAggregate.Summary,
            string.Join(' ', eventAggregate.Entities),
            string.Join(' ', eventAggregate.RepresentativeTitles),
            string.Join(' ', eventAggregate.KeyTerms),
            string.Join(' ', eventAggregate.Aliases));
        var candidateTokens = Tokenize(candidateText);
        var candidateNgrams = BuildNgrams(NormalizeForNgrams(candidateText));
        var tokenScore = Jaccard(sourceTokens, candidateTokens);
        var ngramScore = Jaccard(sourceNgrams, candidateNgrams);
        var representativeScore = eventAggregate.RepresentativeTitles
            .Select(title => Jaccard(sourceNgrams, BuildNgrams(NormalizeForNgrams(title))))
            .DefaultIfEmpty(0)
            .Max();
        var keyTermScore = eventAggregate.KeyTerms.Count == 0
            ? 0
            : eventAggregate.KeyTerms.Count(term => sourceTokens.Contains(NormalizeToken(term))) / (double)eventAggregate.KeyTerms.Count;
        var recencyHours = Math.Max(0, (now - eventAggregate.LastSeenAt).TotalHours);
        var recencyScore = eventAggregate.Status == EventStatus.Active
            ? Math.Clamp(1 - recencyHours / 168, 0, 1)
            : Math.Clamp(1 - recencyHours / 720, 0, 1) * 0.8;
        var score = tokenScore * 0.35 + ngramScore * 0.35 + representativeScore * 0.15 + keyTermScore * 0.10 + recencyScore * 0.05;
        var features = new List<string>();

        if (tokenScore >= 0.12) features.Add("token_overlap");
        if (ngramScore >= 0.18) features.Add("char_ngram_jaccard");
        if (representativeScore >= 0.18) features.Add("representative_title");
        if (keyTermScore > 0) features.Add("key_terms");
        if (recencyScore >= 0.5) features.Add("recent_event");

        return new EventCandidate(eventAggregate, Math.Round(score, 4), features);
    }

    internal static HashSet<string> Tokenize(string text)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var builder = new StringBuilder();

        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                builder.Append(rune.ToString().ToLower(CultureInfo.InvariantCulture));
                continue;
            }

            FlushToken(builder, tokens);
        }

        FlushToken(builder, tokens);
        return tokens;
    }

    internal static HashSet<string> BuildNgrams(string text)
    {
        var values = text.EnumerateRunes().Select(rune => rune.ToString()).ToList();
        var grams = new HashSet<string>(StringComparer.Ordinal);
        for (var size = 2; size <= 3; size++)
        {
            for (var i = 0; i <= values.Count - size; i++)
            {
                grams.Add(string.Concat(values.Skip(i).Take(size)));
            }
        }

        return grams;
    }

    private static void FlushToken(StringBuilder builder, HashSet<string> tokens)
    {
        if (builder.Length == 0)
        {
            return;
        }

        var token = NormalizeToken(builder.ToString());
        if (token.Length >= 2 && !StopWords.Contains(token))
        {
            tokens.Add(token);
        }

        builder.Clear();
    }

    private static string NormalizeToken(string value)
        => value.Trim().ToLowerInvariant();

    private static string NormalizeForNgrams(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                builder.Append(rune.ToString().ToLower(CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    private static double Jaccard(HashSet<string> left, HashSet<string> right)
    {
        if (left.Count == 0 || right.Count == 0)
        {
            return 0;
        }

        var intersection = left.Count(right.Contains);
        var union = left.Count + right.Count - intersection;
        return union == 0 ? 0 : intersection / (double)union;
    }

    private static string JoinText(params string?[] values)
        => string.Join(' ', values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!.Trim()));
}
