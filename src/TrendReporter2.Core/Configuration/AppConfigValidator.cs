using System.Globalization;

namespace TrendReporter2.Core.Configuration;

public static class AppConfigValidator
{
    public static void Validate(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var errors = new List<string>();

        Require(!string.IsNullOrWhiteSpace(config.NewsNow.BaseUrl), "newsNow.baseUrl must not be empty.");
        Require(!string.IsNullOrWhiteSpace(config.Database.Path), "database.path must not be empty.");
        Require(config.Analysis.FetchInterval > 0, "analysis.fetchInterval must be greater than 0.");
        Require(config.Analysis.HistoryHours > 0, "analysis.historyHours must be greater than 0.");
        Require(config.Analysis.Push.PushCount > 0, "analysis.push.pushCount must be greater than 0.");
        Require(config.Analysis.Push.PushTime.Count > 0, "analysis.push.pushTime must contain at least one time.");

        foreach (var pushTime in config.Analysis.Push.PushTime)
        {
            Require(
                TimeOnly.TryParseExact(pushTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
                $"analysis.push.pushTime contains invalid time '{pushTime}'. Expected HH:mm.");
        }

        Require(config.Analysis.Event.SourceCount > 0, "analysis.event.sourceCount must be greater than 0.");
        Require(IsRatio(config.Analysis.Event.NormalizedRankThreshold), "analysis.event.normalizedRankThreshold must be between 0 and 1.");
        Require(config.Analysis.Event.TrendWindowHours > 0, "analysis.event.trendWindowHours must be greater than 0.");
        Require(config.Analysis.Event.StaleHours > 0, "analysis.event.staleHours must be greater than 0.");
        Require(config.Analysis.Event.ArchiveRecallDays > 0, "analysis.event.archiveRecallDays must be greater than 0.");
        Require(config.Analysis.Event.CandidateLimit > 0, "analysis.event.candidateLimit must be greater than 0.");
        Require(IsRatio(config.Analysis.Event.MergeThreshold), "analysis.event.mergeThreshold must be between 0 and 1.");
        Require(IsRatio(config.Analysis.Event.StaleMergeThreshold), "analysis.event.staleMergeThreshold must be between 0 and 1.");
        Require(config.Analysis.Event.MinTrendSamples > 0, "analysis.event.minTrendSamples must be greater than 0.");
        Require(config.Analysis.Event.MinTrendHeat >= 0, "analysis.event.minTrendHeat must not be negative.");

        Require(config.Enrichment.MaxRequestsPerRun >= 0, "enrichment.maxRequestsPerRun must not be negative.");
        Require(config.Enrichment.MinTitleLength > 0, "enrichment.minTitleLength must be greater than 0.");
        Require(IsRatio(config.Enrichment.RecallWeakScoreThreshold), "enrichment.recallWeakScoreThreshold must be between 0 and 1.");
        Require(config.Enrichment.RetryCooldownHours >= 0, "enrichment.retryCooldownHours must not be negative.");

        Require(config.System.MaxParallelFetch > 0, "system.maxParallelFetch must be greater than 0.");
        Require(config.System.MaxParallelEnrichment > 0, "system.maxParallelEnrichment must be greater than 0.");
        Require(config.System.MaxParallelLlm > 0, "system.maxParallelLlm must be greater than 0.");

        try
        {
            _ = TimeZoneResolver.Find(config.System.TimeZone);
        }
        catch (TimeZoneNotFoundException)
        {
            errors.Add($"system.timeZone '{config.System.TimeZone}' was not found on this machine.");
        }
        catch (InvalidTimeZoneException)
        {
            errors.Add($"system.timeZone '{config.System.TimeZone}' is invalid on this machine.");
        }

        if (errors.Count > 0)
        {
            throw new AppConfigValidationException(errors);
        }

        void Require(bool condition, string message)
        {
            if (!condition)
            {
                errors.Add(message);
            }
        }
    }

    private static bool IsRatio(double value) => value is >= 0 and <= 1;
}
