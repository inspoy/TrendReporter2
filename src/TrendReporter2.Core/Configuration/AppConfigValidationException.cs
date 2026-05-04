namespace TrendReporter2.Core.Configuration;

public sealed class AppConfigValidationException : Exception
{
    public AppConfigValidationException(IReadOnlyList<string> errors)
        : base("配置验证失败: " + string.Join("; ", errors))
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }
}
