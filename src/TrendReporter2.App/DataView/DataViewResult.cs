namespace TrendReporter2.App.DataView;

public sealed record DataViewResult(
    string CollectionName,
    int RequestedLimit,
    int ReturnedRowCount,
    IReadOnlyList<DataViewRow> Rows);
