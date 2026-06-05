using TrendReporter2.Core.Configuration;
using TrendReporter2.Infrastructure.Configuration;

namespace TrendReporter2.Tests;

public sealed class YamlAppConfigLoaderTests
{
    [Fact]
    public void Load_RejectsDeprecatedSystemMaxParallelLlm()
    {
        var path = Path.Combine(Path.GetTempPath(), "TrendReporter2.Tests", $"config-{Guid.NewGuid():N}.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
        system:
          maxParallelLlm: 1
        """);

        try
        {
            var exception = Assert.Throws<AppConfigValidationException>(() => new YamlAppConfigLoader().Load(path));

            Assert.Contains(
                "system.maxParallelLlm 已废弃，请分别配置 llm.cluster.maxParallel、llm.judge.maxParallel、llm.tagging.maxParallel 和 llm.embedding.maxParallel。",
                exception.Errors);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
