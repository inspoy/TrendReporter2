namespace TrendReporter2.Core.Content;

public sealed class ContentSnapshot
{
    public string Id { get; set; } = string.Empty;

    public string RunId { get; set; } = string.Empty;

    public string ContentItemId { get; set; } = string.Empty;

    public DateTimeOffset CapturedAt { get; set; }

    public string Source { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public int VisualOrder { get; set; }

    public int Rank { get; set; }

    public int SourceListSize { get; set; }

    public double NormalizedRankScore { get; set; }
}
