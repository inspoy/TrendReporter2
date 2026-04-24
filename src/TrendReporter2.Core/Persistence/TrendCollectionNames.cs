namespace TrendReporter2.Core.Persistence;

public static class TrendCollectionNames
{
    public const string ContentItem = "content_item";
    public const string ContentSnapshot = "content_snapshot";
    public const string Event = "event";
    public const string EventItem = "event_item";
    public const string EventScoreSnapshot = "event_score_snapshot";
    public const string PushLog = "push_log";
    public const string FetchRun = "fetch_run";
    public const string AppState = "app_state";

    public static readonly IReadOnlyList<string> All = new[]
    {
        ContentItem,
        ContentSnapshot,
        Event,
        EventItem,
        EventScoreSnapshot,
        PushLog,
        FetchRun,
        AppState
    };
}
