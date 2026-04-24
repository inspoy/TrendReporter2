namespace TrendReporter2.Core.Configuration;

public sealed class AppConfigValidationException : Exception
{
    public AppConfigValidationException(IReadOnlyList<string> errors)
        : base("Configuration validation failed: " + string.Join("; ", errors))
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }
}
