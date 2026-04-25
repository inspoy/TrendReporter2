namespace TrendReporter2.Core.Content;

public sealed record ContentIngestResult(
    int TotalCount,
    int InsertedCount,
    int UpdatedCount,
    int SnapshotCount);
