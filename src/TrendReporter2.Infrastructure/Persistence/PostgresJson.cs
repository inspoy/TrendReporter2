using Newtonsoft.Json;

namespace TrendReporter2.Infrastructure.Persistence;

internal static class PostgresJson
{
    public static string Serialize<T>(T value) => JsonConvert.SerializeObject(value);

    public static List<T> DeserializeList<T>(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : JsonConvert.DeserializeObject<List<T>>(value) ?? [];

    public static string EmptyObjectIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? "{}" : value;
}
