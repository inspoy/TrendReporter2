using Dapper;
using Npgsql;
using TrendReporter2.Core.Observability;

namespace TrendReporter2.Infrastructure.Persistence;

public sealed class PostgresRunTelemetryRecorder : IRunTelemetryRecorder
{
    private readonly NpgsqlDataSource _dataSource;

    static PostgresRunTelemetryRecorder()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public PostgresRunTelemetryRecorder(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task RecordSourceAsync(RunSourceTelemetry telemetry, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("""
        insert into fetch_run_source (run_id, source_id, category, source, status, duration_ms, item_count, error, created_at)
        values (@RunId, @SourceId, @Category, @Source, @Status, @DurationMs, @ItemCount, @Error, @CreatedAt)
        on conflict (run_id, source_id) do update
        set status = excluded.status,
            duration_ms = excluded.duration_ms,
            item_count = excluded.item_count,
            error = excluded.error,
            created_at = excluded.created_at;
        """, ToParameters(telemetry), cancellationToken: cancellationToken));
    }

    public async Task RecordStageAsync(RunStageTelemetry telemetry, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("""
        insert into fetch_run_stage (id, run_id, stage, started_at, finished_at, duration_ms, status, error)
        values (@Id, @RunId, @Stage, @StartedAt, @FinishedAt, @DurationMs, @Status, @Error)
        on conflict (id) do update
        set finished_at = excluded.finished_at,
            duration_ms = excluded.duration_ms,
            status = excluded.status,
            error = excluded.error;
        """, ToParameters(telemetry), cancellationToken: cancellationToken));
    }

    public async Task RecordLlmUsageAsync(LlmUsageRecord usage, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("""
        insert into llm_usage (id, run_id, stage, model, request_id, content_item_id, event_id, input_tokens, output_tokens,
            cache_read_tokens, estimated_cost, duration_ms, success, retry_count, error, created_at)
        values (@Id, @RunId, @Stage, @Model, @RequestId, @ContentItemId, @EventId, @InputTokens, @OutputTokens,
            @CacheReadTokens, @EstimatedCost, @DurationMs, @Success, @RetryCount, @Error, @CreatedAt);
        """, ToParameters(usage), cancellationToken: cancellationToken));
    }

    public async Task<LlmUsageSummary> GetLlmUsageSummaryAsync(string runId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleAsync<LlmUsageSummaryRow>(new CommandDefinition("""
        select count(*)::integer as CallCount,
               coalesce(sum(estimated_cost), 0)::numeric as EstimatedCost
        from llm_usage
        where run_id = @RunId;
        """, new { RunId = runId }, cancellationToken: cancellationToken));

        return new LlmUsageSummary(row.CallCount, row.EstimatedCost);
    }

    private static object ToParameters(RunSourceTelemetry telemetry)
        => new
        {
            telemetry.RunId,
            telemetry.SourceId,
            telemetry.Category,
            telemetry.Source,
            telemetry.Status,
            telemetry.DurationMs,
            telemetry.ItemCount,
            telemetry.Error,
            CreatedAt = PostgresTimestamp.ToUtc(telemetry.CreatedAt)
        };

    private static object ToParameters(RunStageTelemetry telemetry)
        => new
        {
            telemetry.Id,
            telemetry.RunId,
            telemetry.Stage,
            StartedAt = PostgresTimestamp.ToUtc(telemetry.StartedAt),
            FinishedAt = PostgresTimestamp.ToUtc(telemetry.FinishedAt),
            telemetry.DurationMs,
            telemetry.Status,
            telemetry.Error
        };

    private static object ToParameters(LlmUsageRecord usage)
        => new
        {
            usage.Id,
            usage.RunId,
            usage.Stage,
            usage.Model,
            usage.RequestId,
            usage.ContentItemId,
            usage.EventId,
            usage.InputTokens,
            usage.OutputTokens,
            usage.CacheReadTokens,
            usage.EstimatedCost,
            usage.DurationMs,
            usage.Success,
            usage.RetryCount,
            usage.Error,
            CreatedAt = PostgresTimestamp.ToUtc(usage.CreatedAt)
        };

    private sealed record LlmUsageSummaryRow(int CallCount, decimal EstimatedCost);
}
