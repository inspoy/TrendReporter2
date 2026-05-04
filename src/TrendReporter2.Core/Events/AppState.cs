namespace TrendReporter2.Core.Events;

public sealed class AppState
{
    public string Id { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; }
}
