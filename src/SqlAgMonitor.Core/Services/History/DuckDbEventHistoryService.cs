using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;
using SqlAgMonitor.Core.Models;

namespace SqlAgMonitor.Core.Services.History;

public class DuckDbEventHistoryService : IEventHistoryService
{
    private readonly ILogger<DuckDbEventHistoryService> _logger;
    private readonly string _dbPath;
    private DuckDBConnection? _connection;
    private readonly object _lock = new();
    private bool _disposed;

    public DuckDbEventHistoryService(ILogger<DuckDbEventHistoryService> logger, string? dataDirectory = null)
    {
        _logger = logger;
        var dir = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SqlAgMonitor", "data");
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "events.duckdb");
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _connection = new DuckDBConnection($"Data Source={_dbPath}");
        await _connection.OpenAsync(cancellationToken);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            CREATE SEQUENCE IF NOT EXISTS error_log_seq START 1;
            CREATE SEQUENCE IF NOT EXISTS event_seq START 1;

            CREATE TABLE IF NOT EXISTS events (
                id BIGINT PRIMARY KEY,
                timestamp TIMESTAMPTZ NOT NULL,
                alert_type VARCHAR NOT NULL,
                group_name VARCHAR NOT NULL,
                replica_name VARCHAR,
                database_name VARCHAR,
                message VARCHAR NOT NULL,
                severity VARCHAR NOT NULL,
                email_sent BOOLEAN DEFAULT FALSE,
                syslog_sent BOOLEAN DEFAULT FALSE
            );

            CREATE TABLE IF NOT EXISTS error_log (
                id BIGINT DEFAULT nextval('error_log_seq'),
                timestamp TIMESTAMPTZ DEFAULT current_timestamp,
                error_type VARCHAR NOT NULL,
                message VARCHAR NOT NULL,
                stack_trace VARCHAR,
                context VARCHAR
            );
        ";
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        _logger.LogInformation("DuckDB event history initialized at {Path}.", _dbPath);
    }

    public async Task RecordEventAsync(AlertEvent alertEvent, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        try
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO events (id, timestamp, alert_type, group_name, replica_name, database_name, message, severity, email_sent, syslog_sent)
                VALUES (nextval('event_seq'), $timestamp, $alert_type, $group_name, $replica_name, $database_name, $message, $severity, $email_sent, $syslog_sent)
            ";
            cmd.Parameters.Add(new DuckDBParameter("timestamp", alertEvent.Timestamp.UtcDateTime));
            cmd.Parameters.Add(new DuckDBParameter("alert_type", alertEvent.AlertType.ToString()));
            cmd.Parameters.Add(new DuckDBParameter("group_name", alertEvent.GroupName));
            cmd.Parameters.Add(new DuckDBParameter("replica_name", (object?)alertEvent.ReplicaName ?? DBNull.Value));
            cmd.Parameters.Add(new DuckDBParameter("database_name", (object?)alertEvent.DatabaseName ?? DBNull.Value));
            cmd.Parameters.Add(new DuckDBParameter("message", alertEvent.Message));
            cmd.Parameters.Add(new DuckDBParameter("severity", alertEvent.Severity.ToString()));
            cmd.Parameters.Add(new DuckDBParameter("email_sent", alertEvent.EmailSent));
            cmd.Parameters.Add(new DuckDBParameter("syslog_sent", alertEvent.SyslogSent));

            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record event for group {Group}.", alertEvent.GroupName);
        }
    }

    public async Task RecordErrorAsync(string errorType, string message, string? stackTrace, string? context, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        try
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO error_log (timestamp, error_type, message, stack_trace, context)
                VALUES (current_timestamp, $error_type, $message, $stack_trace, $context)
            ";
            cmd.Parameters.Add(new DuckDBParameter("error_type", errorType));
            cmd.Parameters.Add(new DuckDBParameter("message", message));
            cmd.Parameters.Add(new DuckDBParameter("stack_trace", (object?)stackTrace ?? DBNull.Value));
            cmd.Parameters.Add(new DuckDBParameter("context", (object?)context ?? DBNull.Value));

            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record error to DuckDB.");
        }
    }

    public async Task<IReadOnlyList<AlertEvent>> GetEventsAsync(string? groupName = null, DateTimeOffset? since = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var events = new List<AlertEvent>();

        using var cmd = _connection!.CreateCommand();
        var where = new List<string>();
        if (groupName != null)
        {
            where.Add("group_name = $group_name");
            cmd.Parameters.Add(new DuckDBParameter("group_name", groupName));
        }
        if (since != null)
        {
            where.Add("timestamp >= $since");
            cmd.Parameters.Add(new DuckDBParameter("since", since.Value.UtcDateTime));
        }

        var whereClause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
        cmd.CommandText = $@"
            SELECT id, timestamp, alert_type, group_name, replica_name, database_name, message, severity, email_sent, syslog_sent
            FROM events
            {whereClause}
            ORDER BY timestamp DESC
            LIMIT {limit}
        ";

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(new AlertEvent
            {
                Id = reader.GetInt64(0),
                Timestamp = new DateTimeOffset(reader.GetDateTime(1), TimeSpan.Zero),
                AlertType = Enum.TryParse<AlertType>(reader.GetString(2), out var at) ? at : AlertType.Unknown,
                GroupName = reader.GetString(3),
                ReplicaName = reader.IsDBNull(4) ? null : reader.GetString(4),
                DatabaseName = reader.IsDBNull(5) ? null : reader.GetString(5),
                Message = reader.GetString(6),
                Severity = Enum.TryParse<AlertSeverity>(reader.GetString(7), out var sev) ? sev : AlertSeverity.Information,
                EmailSent = reader.GetBoolean(8),
                SyslogSent = reader.GetBoolean(9)
            });
        }

        return events;
    }

    public async Task<long> GetEventCountAsync(string? groupName = null, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        using var cmd = _connection!.CreateCommand();
        if (groupName != null)
        {
            cmd.CommandText = "SELECT COUNT(*) FROM events WHERE group_name = $group_name";
            cmd.Parameters.Add(new DuckDBParameter("group_name", groupName));
        }
        else
        {
            cmd.CommandText = "SELECT COUNT(*) FROM events";
        }

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result);
    }

    public async Task<long> PruneEventsAsync(int? maxAgeDays, int? maxRecords, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        long totalDeleted = 0;

        if (maxAgeDays.HasValue)
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "DELETE FROM events WHERE timestamp < $cutoff";
            cmd.Parameters.Add(new DuckDBParameter("cutoff", DateTime.UtcNow.AddDays(-maxAgeDays.Value)));
            totalDeleted += await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        if (maxRecords.HasValue)
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = @"
                DELETE FROM events 
                WHERE id NOT IN (
                    SELECT id FROM events ORDER BY timestamp DESC LIMIT $max_records
                )";
            cmd.Parameters.Add(new DuckDBParameter("max_records", maxRecords.Value));
            totalDeleted += await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        if (totalDeleted > 0)
        {
            _logger.LogInformation("Pruned {Count} old events from history.", totalDeleted);
        }

        return totalDeleted;
    }

    private void EnsureInitialized()
    {
        if (_connection == null)
            throw new InvalidOperationException("Event history service not initialized. Call InitializeAsync first.");

        if (_connection.State != System.Data.ConnectionState.Open)
        {
            try
            {
                _connection.Open();
                _logger.LogInformation("DuckDB connection reopened.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reopen DuckDB connection. Reinitializing.");
                try
                {
                    _connection.Dispose();
                    _connection = new DuckDBConnection($"Data Source={_dbPath}");
                    _connection.Open();
                    _logger.LogInformation("DuckDB connection reinitialized.");
                }
                catch (Exception ex2)
                {
                    _logger.LogError(ex2, "Failed to reinitialize DuckDB connection.");
                    throw;
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed && _connection != null)
        {
            await _connection.CloseAsync();
            _connection.Dispose();
            _disposed = true;
        }
    }
}
