using System.Reflection;
using TrendReporter2.App.Scheduling;

namespace TrendReporter2.Tests;

public sealed class ProgramCliTests
{
    [Fact]
    public void Parse_DefaultsToBackgroundAndResolvesDefaultConfigPath()
    {
        var options = Parse();

        Assert.Equal(Path.GetFullPath("config.yaml"), GetStringProperty(options, "ConfigPath"));
        Assert.Equal("Background", GetEnumPropertyName(options, "Mode"));
    }

    [Theory]
    [InlineData("validate", "Validate")]
    [InlineData("fetch-once", "FetchOnce")]
    [InlineData("digest-once", "DigestOnce")]
    public void Parse_AllowsSupportedModes(string modeArgument, string expectedModeName)
    {
        var options = Parse(modeArgument, "--config", "./config.example.yaml");

        Assert.Equal(Path.GetFullPath("./config.example.yaml"), GetStringProperty(options, "ConfigPath"));
        Assert.Equal(expectedModeName, GetEnumPropertyName(options, "Mode"));
    }

    [Theory]
    [MemberData(nameof(ConflictingModePairs))]
    public void Parse_RejectsMixedSupportedModes(string[] arguments)
    {
        var exception = Assert.Throws<TargetInvocationException>(() => Parse(arguments));
        var innerException = Assert.IsType<ArgumentException>(exception.InnerException);

        Assert.Equal("请只选择一种模式: validate、fetch-once 或 digest-once。", innerException.Message);
    }

    [Theory]
    [InlineData("data-view")]
    [InlineData("invalid-command")]
    public void Parse_RejectsUnknownCommands(string unknownCommand)
    {
        var exception = Assert.Throws<TargetInvocationException>(() => Parse(unknownCommand));
        var innerException = Assert.IsType<ArgumentException>(exception.InnerException);

        Assert.Contains($"未知参数 '{unknownCommand}'", innerException.Message);
        Assert.Contains("validate | fetch-once | digest-once", innerException.Message);
        Assert.DoesNotContain("data-view <collection>", innerException.Message);
    }

    public static IEnumerable<object[]> ConflictingModePairs()
    {
        yield return [new[] { "validate", "fetch-once" }];
        yield return [new[] { "validate", "digest-once" }];
        yield return [new[] { "fetch-once", "digest-once" }];
        yield return [new[] { "digest-once", "validate" }];
    }

    private static object Parse(params string[] args)
        => ParseMethod.Invoke(null, [args])!;

    private static string GetStringProperty(object instance, string propertyName)
        => (string)instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)!.GetValue(instance)!;

    private static string GetEnumPropertyName(object instance, string propertyName)
        => GetPropertyValue(instance, propertyName).ToString()!;

    private static object GetPropertyValue(object instance, string propertyName)
        => instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)!.GetValue(instance)!;

    private static readonly MethodInfo ParseMethod = typeof(FetchJob).Assembly
        .GetType("CliOptions", throwOnError: true)!
        .GetMethod("Parse", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
}
