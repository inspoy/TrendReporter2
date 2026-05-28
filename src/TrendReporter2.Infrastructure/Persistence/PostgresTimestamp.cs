namespace TrendReporter2.Infrastructure.Persistence;

internal static class PostgresTimestamp
{
    public static DateTimeOffset ToUtc(DateTimeOffset value)
        => value.ToUniversalTime();

    public static DateTimeOffset? ToUtc(DateTimeOffset? value)
        => value?.ToUniversalTime();
}
