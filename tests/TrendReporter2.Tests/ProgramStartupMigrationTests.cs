using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using TrendReporter2.App.Scheduling;
using TrendReporter2.Core.Configuration;
using TrendReporter2.Infrastructure.Persistence;

namespace TrendReporter2.Tests;

public sealed class ProgramStartupMigrationTests
{
    [Fact]
    public async Task ValidateCommand_ExitsWithoutStartupMigration()
    {
        using var directory = TempDirectory.Create();
        var configPath = WriteConfig(directory.Path, migrateOnStartup: true);

        var result = await RunAppAsync("validate", "--config", configPath);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("配置验证成功。", result.StandardOutput);
        Assert.DoesNotContain("PostgreSQL 启动迁移", result.CombinedOutput);
    }

    [Fact]
    public async Task ValidateCommand_RejectsLegacyNewsNowOnlyConfig()
    {
        using var directory = TempDirectory.Create();
        var configPath = WriteLegacyNewsNowOnlyConfig(directory.Path);

        var result = await RunAppAsync("validate", "--config", configPath);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("sources 必须至少包含一个启用的信源。", result.CombinedOutput);
    }

    [Fact]
    public async Task NonValidateStartup_WhenMigrateOnStartupIsTrue_FailsFastOnMigrationFailure()
    {
        using var directory = TempDirectory.Create();
        var configPath = WriteConfig(directory.Path, migrateOnStartup: true);

        var result = await RunAppAsync("fetch-once", "--config", configPath);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("PostgreSQL 启动迁移失败", result.CombinedOutput);
    }

    [Fact]
    public async Task FetchOnce_WhenMigrateOnStartupIsFalse_ResolvesRepositoriesAndAttemptsPostgresConnection()
    {
        using var directory = TempDirectory.Create();
        var configPath = WriteConfig(directory.Path, migrateOnStartup: false);

        var result = await RunAppAsyncWithEnvironment("Development", "fetch-once", "--config", configPath);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("单次抓取模式已启动", result.CombinedOutput);
        Assert.Contains("Failed to connect", result.CombinedOutput);
        Assert.DoesNotContain("AggregateException", result.CombinedOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Unable to resolve", result.CombinedOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DigestOnce_WhenMigrateOnStartupIsFalse_ResolvesRepositoriesAndAttemptsPostgresConnection()
    {
        using var directory = TempDirectory.Create();
        var configPath = WriteConfig(directory.Path, migrateOnStartup: false);

        var result = await RunAppAsync("digest-once", "--config", configPath);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("单次摘要推送模式已启动", result.CombinedOutput);
        Assert.Contains("Failed to connect", result.CombinedOutput);
        Assert.DoesNotContain("Unable to resolve", result.CombinedOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartupMigration_SkipsRunnerWhenMigrateOnStartupIsFalse()
    {
        var invocationCount = 0;
        SetMigrationRunner((_, _) =>
        {
            invocationCount++;
            return Task.FromResult(new SqlMigrationRunResult(1, 0));
        });

        try
        {
            await InvokeStartupMigrationAsync(new AppConfig
            {
                Database = new DatabaseConfig
                {
                    Provider = "postgres",
                    ConnectionString = "Host=localhost;Database=trend;Username=trend;Password=secret",
                    MigrateOnStartup = false
                }
            });

            Assert.Equal(0, invocationCount);
        }
        finally
        {
            ResetMigrationRunner();
        }
    }

    [Fact]
    public async Task StartupMigration_InvokesRunnerWhenMigrateOnStartupIsTrue()
    {
        var invocationCount = 0;
        SetMigrationRunner((_, _) =>
        {
            invocationCount++;
            return Task.FromResult(new SqlMigrationRunResult(2, 3));
        });

        try
        {
            await InvokeStartupMigrationAsync(new AppConfig
            {
                Database = new DatabaseConfig
                {
                    Provider = "postgres",
                    ConnectionString = "Host=localhost;Database=trend;Username=trend;Password=secret"
                }
            });

            Assert.Equal(1, invocationCount);
        }
        finally
        {
            ResetMigrationRunner();
        }
    }

    private static async Task InvokeStartupMigrationAsync(AppConfig config)
    {
        var task = (Task)RunIfEnabledMethod.Invoke(null, [new ServiceProviderStub(), config, NullLogger.Instance, CancellationToken.None])!;
        await task;
    }

    private static void SetMigrationRunner(Func<IServiceProvider, CancellationToken, Task<SqlMigrationRunResult>> runner)
        => RunMigrationAsyncProperty.SetValue(null, runner);

    private static void ResetMigrationRunner()
        => ResetForTestsMethod.Invoke(null, []);

    private static async Task<ProcessResult> RunAppAsync(params string[] arguments)
        => await RunAppAsyncWithEnvironment(null, arguments);

    private static async Task<ProcessResult> RunAppAsyncWithEnvironment(string? environment, params string[] arguments)
    {
        var processStartInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        if (environment is not null)
        {
            processStartInfo.Environment["DOTNET_ENVIRONMENT"] = environment;
        }

        processStartInfo.ArgumentList.Add(AppAssemblyPath);
        foreach (var argument in arguments)
        {
            processStartInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(processStartInfo)!;
        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();
        var waitForExitTask = process.WaitForExitAsync();
        var completed = await Task.WhenAny(waitForExitTask, Task.Delay(TimeSpan.FromSeconds(10)));
        if (completed != waitForExitTask)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("TrendReporter2.App process did not exit within 10 seconds.");
        }

        var standardOutput = await standardOutputTask;
        var standardError = await standardErrorTask;
        return new ProcessResult(process.ExitCode, standardOutput, standardError);
    }

    private static string WriteConfig(string directory, bool migrateOnStartup)
    {
        var configPath = Path.Combine(directory, "config.yaml");
        File.WriteAllText(configPath, $$"""
        sources:
          newsNow:
            baseUrl: "https://news.local"
            items:
              - externalId: "ifeng"
                category: "china"
                displayName: "凤凰网"
                contentKind: "ranked_news"
                enabled: true
                weight: 1.0
        database:
          provider: "postgres"
          connectionString: "Host=127.0.0.1;Port=1;Database=trend;Username=trend;Password=secret;Timeout=1;Command Timeout=1"
          migrateOnStartup: {{migrateOnStartup.ToString().ToLowerInvariant()}}
        """);
        return configPath;
    }

    private static string WriteLegacyNewsNowOnlyConfig(string directory)
    {
        var configPath = Path.Combine(directory, "legacy-config.yaml");
        File.WriteAllText(configPath, """
        newsNow:
          baseUrl: "https://news.local"
        database:
          provider: "postgres"
          connectionString: "Host=127.0.0.1;Port=1;Database=trend;Username=trend;Password=secret;Timeout=1;Command Timeout=1"
          migrateOnStartup: false
        """);
        return configPath;
    }

    private static readonly Assembly AppAssembly = typeof(FetchJob).Assembly;
    private static readonly string AppAssemblyPath = AppAssembly.Location;
    private static readonly Type StartupMigrationType = AppAssembly.GetType("StartupMigration", throwOnError: true)!;
    private static readonly PropertyInfo RunMigrationAsyncProperty = StartupMigrationType.GetProperty("RunMigrationAsync", BindingFlags.Public | BindingFlags.Static)!;
    private static readonly MethodInfo RunIfEnabledMethod = StartupMigrationType.GetMethod("RunIfEnabledAsync", BindingFlags.Public | BindingFlags.Static)!;
    private static readonly MethodInfo ResetForTestsMethod = StartupMigrationType.GetMethod("ResetForTests", BindingFlags.Public | BindingFlags.Static)!;

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
    {
        public string CombinedOutput => StandardOutput + StandardError;
    }

    private sealed class ServiceProviderStub : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TrendReporter2.Tests", Guid.NewGuid().ToString("N"));

        private TempDirectory()
        {
            Directory.CreateDirectory(Path);
        }

        public static TempDirectory Create()
            => new();

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
