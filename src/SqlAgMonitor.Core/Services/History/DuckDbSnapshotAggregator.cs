using System.Globalization;
using Microsoft.Extensions.Logging;

namespace SqlAgMonitor.Core.Services.History;

/// <summary>
/// Handles snapshot roll-up (raw → hourly → daily) and aged-data pruning.
/// Each step acquires/releases the connection lock independently so that
/// high-frequency RecordSnapshotAsync calls can interleave between steps.
/// </summary>
internal sealed class DuckDbSnapshotAggregator
{
    private readonly DuckDbConnectionManager _db;
    private readonly ILogger _logger;

    public DuckDbSnapshotAggregator(DuckDbConnectionManager db, ILogger<DuckDbSnapshotAggregator> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task SummarizeSnapshotsAsync(
        int rawRetentionHours = 48, int hourlyRetentionDays = 90, int dailyRetentionDays = 730,
        CancellationToken cancellationToken = default)
    {
        if (!_db.IsInitialized) return;

        // Clamp to sane minimums to prevent misconfigured config from deleting all data.
        rawRetentionHours = Math.Max(1, rawRetentionHours);
        hourlyRetentionDays = Math.Max(1, hourlyRetentionDays);
        dailyRetentionDays = Math.Max(1, dailyRetentionDays);

        // Step 1 — Generate hourly summaries from raw snapshots
        try
        {
            await _db.ExecuteAsync(conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO snapshot_hourly
                    SELECT 
                        date_trunc('hour', timestamp) AS bucket,
                        group_name, replica_name, database_name,
                        COUNT(*)::INTEGER AS sample_count,
                        MIN(log_send_queue_kb), MAX(log_send_queue_kb), AVG(log_send_queue_kb)::DOUBLE,
                        MIN(redo_queue_kb), MAX(redo_queue_kb), AVG(redo_queue_kb)::DOUBLE,
                        MIN(log_send_rate_kb_per_sec), MAX(log_send_rate_kb_per_sec), AVG(log_send_rate_kb_per_sec)::DOUBLE,
                        MIN(redo_rate_kb_per_sec), MAX(redo_rate_kb_per_sec), AVG(redo_rate_kb_per_sec)::DOUBLE,
                        MIN(log_block_difference), MAX(log_block_difference), AVG(log_block_difference)::DOUBLE,
                        MIN(secondary_lag_seconds), MAX(secondary_lag_seconds), AVG(secondary_lag_seconds)::DOUBLE,
                        LAST(role ORDER BY timestamp),
                        LAST(sync_state ORDER BY timestamp),
                        BOOL_OR(is_suspended),
                        LAST(last_hardened_lsn ORDER BY timestamp),
                        LAST(last_commit_lsn ORDER BY timestamp)
                    FROM snapshots
                    WHERE date_trunc('hour', timestamp) < date_trunc('hour', current_timestamp)
                      AND date_trunc('hour', timestamp) NOT IN (SELECT DISTINCT bucket FROM snapshot_hourly)
                    GROUP BY date_trunc('hour', timestamp), group_name, replica_name, database_name
                ";
                var inserted = cmd.ExecuteNonQuery();
                if (inserted > 0)
                    _logger.LogInformation("Inserted {Count} hourly snapshot summaries.", inserted);
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate hourly snapshot summaries.");
        }

        // Step 2 — Generate daily summaries from hourly data
        try
        {
            await _db.ExecuteAsync(conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO snapshot_daily
                    SELECT
                        date_trunc('day', bucket) AS bucket,
                        group_name, replica_name, database_name,
                        SUM(sample_count)::INTEGER,
                        MIN(log_send_queue_kb_min), MAX(log_send_queue_kb_max), 
                        SUM(log_send_queue_kb_avg * sample_count) / SUM(sample_count),
                        MIN(redo_queue_kb_min), MAX(redo_queue_kb_max),
                        SUM(redo_queue_kb_avg * sample_count) / SUM(sample_count),
                        MIN(log_send_rate_min), MAX(log_send_rate_max),
                        SUM(log_send_rate_avg * sample_count) / SUM(sample_count),
                        MIN(redo_rate_min), MAX(redo_rate_max),
                        SUM(redo_rate_avg * sample_count) / SUM(sample_count),
                        MIN(log_block_diff_min), MAX(log_block_diff_max),
                        SUM(log_block_diff_avg * sample_count) / SUM(sample_count),
                        MIN(secondary_lag_min), MAX(secondary_lag_max),
                        SUM(secondary_lag_avg * sample_count) / SUM(sample_count),
                        LAST(last_role ORDER BY bucket),
                        LAST(last_sync_state ORDER BY bucket),
                        BOOL_OR(any_suspended),
                        LAST(last_hardened_lsn ORDER BY bucket),
                        LAST(last_commit_lsn ORDER BY bucket)
                    FROM snapshot_hourly
                    WHERE date_trunc('day', bucket) < date_trunc('day', current_timestamp)
                      AND date_trunc('day', bucket) NOT IN (SELECT DISTINCT bucket FROM snapshot_daily)
                    GROUP BY date_trunc('day', bucket), group_name, replica_name, database_name
                ";
                var inserted = cmd.ExecuteNonQuery();
                if (inserted > 0)
                    _logger.LogInformation("Inserted {Count} daily snapshot summaries.", inserted);
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate daily snapshot summaries.");
        }

        // Step 3 — Prune old data
        try
        {
            await _db.ExecuteAsync(conn =>
            {
                // Retention values are integers from application config — safe to format into
                // DuckDB INTERVAL literals. DuckDB does not support parameterized INTERVAL syntax.
                using var cmd = conn.CreateCommand();
                cmd.CommandText = string.Format(CultureInfo.InvariantCulture, @"
                    DELETE FROM snapshots WHERE timestamp < current_timestamp - INTERVAL '{0} hours';
                    DELETE FROM snapshot_hourly WHERE bucket < current_timestamp - INTERVAL '{1} days';
                    DELETE FROM snapshot_daily WHERE bucket < current_timestamp - INTERVAL '{2} days';
                ", rawRetentionHours, hourlyRetentionDays, dailyRetentionDays);
                cmd.ExecuteNonQuery();
            }, cancellationToken);

            _logger.LogDebug("Snapshot summarization and pruning complete.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to prune old snapshots.");
        }
    }
}
