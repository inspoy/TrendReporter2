namespace TrendReporter2.Core.Configuration;

public interface IAppConfigLoader
{
    AppConfig Load(string path);
}
