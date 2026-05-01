namespace TrendReporter2.App.DataView;

public sealed record DataViewRow(IReadOnlyDictionary<string, object?> Fields);
