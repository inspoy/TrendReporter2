using LiteDB;
using TrendReporter2.Core.Events;
using TrendReporter2.Core.Persistence;

namespace TrendReporter2.Infrastructure.Persistence;

public sealed class LiteDbAppStateRepository : IAppStateRepository
{
    private readonly LiteDbConnectionFactory _connectionFactory;

    public LiteDbAppStateRepository(LiteDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public Task<AppState?> GetAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var database = _connectionFactory.Open();
        var state = database.GetCollection<AppState>(TrendCollectionNames.AppState)
            .FindOne(item => item.Key == key);
        return Task.FromResult<AppState?>(state);
    }

    public Task UpsertAsync(AppState state, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var database = _connectionFactory.Open();
        var collection = database.GetCollection<AppState>(TrendCollectionNames.AppState);
        var existing = collection.FindOne(item => item.Key == state.Key);
        state.Id = existing?.Id ?? BuildId(state.Key);
        try
        {
            collection.Upsert(state);
        }
        catch (LiteException ex) when (ex.ErrorCode == LiteException.INDEX_DUPLICATE_KEY)
        {
            existing = collection.FindOne(item => item.Key == state.Key);
            if (existing is not null)
            {
                state.Id = existing.Id;
                collection.Update(state);
            }
        }

        return Task.CompletedTask;
    }

    private static string BuildId(string key)
        => "as:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..24].ToLowerInvariant();
}
