namespace TrendReporter2.Core.Sources;

public sealed record SourceDefinition(
    string Id,
    string Provider,
    string ExternalId,
    string Category,
    string DisplayName,
    string ContentKind,
    bool Enabled,
    double Weight,
    string Param = "");
