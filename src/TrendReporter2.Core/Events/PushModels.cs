namespace TrendReporter2.Core.Events;

public sealed class PushMessage
{
    public string Title { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public string Link { get; set; } = string.Empty;

    public string PushType { get; set; } = PushTypes.Instant;

    public string? EventId { get; set; }

    public string DedupKey { get; set; } = string.Empty;
}

public sealed class PushLog
{
    public string Id { get; set; } = string.Empty;

    public string? EventId { get; set; }

    public string PushType { get; set; } = string.Empty;

    public DateTimeOffset PushedAt { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public string Payload { get; set; } = string.Empty;

    public string DedupKey { get; set; } = string.Empty;

    public bool Success { get; set; }

    public string? Error { get; set; }
}

public sealed record PushResult(bool Success, string Payload, string? Error)
{
    public static PushResult Skipped(string reason, string payload = "{}") => new(false, payload, reason);
}

public static class PushTypes
{
    public const string Instant = "Instant";
    public const string Digest = "Digest";
}
