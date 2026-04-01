using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;
using SqlAgMonitor.Core.Models;

namespace SqlAgMonitor.Core.Services.History;

/// <summary>
/// Handles alert event recording, querying, and pruning against DuckDB.
/// </summary>
internal sealed class DuckDbEventStore
{
    private readonly DuckDbConnectionManager _db;
    private readonly ILogger _logger;

    public DuckDbEventStore(DuckDbConnectionManager db, ILogger<DuckDbEventStore> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task RecordEventAsync(AlertEvent alertEvent, CancellationToken cancellationToken = default)
    {
        if (!_db.IsInitialized) return;
        try
        {
            await _db.ExecuteAsync(conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO events (id, timestamp, alert_type, group_name, replica_name, database_name, message, severity, email_sent, syslog_sent)
                    VALUES (nextval('event_seq'), $ts, $alert_type, $group_name, $replica_name, $database_name, $message, $severity, $email_sent, $syslog_sent)
                ";
                var utcDt = alertEvent.Timestamp.UtcDateTime;
                _logger.LogDebug("Recording event: type={Type}, timestamp={Timestamp:O}", alertEvent.AlertType, utcDt);
                cmd.Parameters.Add(new DuckDBParameter("ts", utcDt));
                cmd.Parameters.Add(new DuckDBParameter("alert_type", alertEvent.AlertType.ToString()));
                cmd.Parameters.Add(new DuckDBParameter("group_name", alertEvent.GroupName));
                cmd.Parameters.Add(new DuckDBParameter("replica_name", (object?)alertEvent.ReplicaName ?? DBNull.Value));
                cmd.Parameters.Add(new DuckDBParameter("database_name", (object?)alertEvent.DatabaseName ?? DBNull.Value));
                cmd.Parameters.Add(new DuckDBParameter("message", alertEvent.Message));
                cmd.Parameters.Add(new DuckDBParameter("severity", alertEvent.Severity.ToString()));
                cmd.Parameters.Add(new DuckDBParameter("email_sent", alertEvent.EmailSent));
                cmd.Parameters.Add(new DuckDBParameter("syslog_sent", alertEvent.SyslogSent));
                cmd.ExecuteNonQuery();
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record event for group {Group}.", alertEvent.GroupName);
        }
    }

    public async Task<IReadOnlyList<AlertEvent>> GetEventsAsync(
        string? groupName = null, DateTimeOffset? since = null, int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (!_db.IsInitialized) return Array.Empty<AlertEvent>();
        limit = Math.Max(1, limit);
        try
        {
            return await _db.ExecuteAsync(conn =>
            {
                var events = new List<AlertEvent>();
                using var cmd = conn.CreateCommand();
                var where = new List<string>();
                if (groupName != null)
                {
                    where.Add("group_name = $group_name");
                    cmd.Parameters.Add(new DuckDBParameter("group_name", groupName));
                }
                if (since != null)
                {
                    where.Add("timestamp >= $since_ts");
                    cmd.Parameters.Add(new DuckDBParameter("since_ts", since.Value.UtcDateTime));
                }

                // WHERE clause assembled from code-controlled static fragments with
                // DuckDB parameter placeholders. No user input is interpolated.
                var whereClause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
                cmd.CommandText = @"
                    SELECT id, timestamp, alert_type, group_name, replica_name, database_name, message, severity, email_sent, syslog_sent
                    FROM events
                    " + whereClause + @"
                    ORDER BY timestamp DESC
                    LIMIT $limit
                ";
                cmd.Parameters.Add(new DuckDBParameter("limit", limit));

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
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
                return (IReadOnlyList<AlertEvent>)events;
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read events from DuckDB.");
            return Array.Empty<AlertEvent>();
        }
    }

    public async Task<long> GetEventCountAsync(string? groupName = null, CancellationToken cancellationToken = default)
    {
        if (!_db.IsInitialized) return 0;
        try
        {
            return await _db.ExecuteAsync(conn =>
            {
                using var cmd = conn.CreateCommand();
                if (groupName != null)
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM events WHERE group_name = $group_name";
                    cmd.Parameters.Add(new DuckDBParameter("group_name", groupName));
                }
                else
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM events";
                }
                return Convert.ToInt64(cmd.ExecuteScalar());
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get event count from DuckDB.");
            return 0;
        }
    }

    public async Task<long> PruneEventsAsync(int? maxAgeDays, int? maxRecords, CancellationToken cancellationToken = default)
    {
        if (!_db.IsInitialized) return 0;
        try
        {
            return await _db.ExecuteAsync(conn =>
            {
                long totalDeleted = 0;
                if (maxAgeDays.HasValue)
                {
                    var clampedDays = Math.Max(1, maxAgeDays.Value);
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "DELETE FROM events WHERE timestamp < $cutoff";
                    cmd.Parameters.Add(new DuckDBParameter("cutoff", DateTime.UtcNow.AddDays(-clampedDays)));
                    totalDeleted += cmd.ExecuteNonQuery();
                }

                if (maxRecords.HasValue)
                {
                    var clampedRecords = Math.Max(1, maxRecords.Value);
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        DELETE FROM events 
                        WHERE id NOT IN (
                            SELECT id FROM events ORDER BY timestamp DESC LIMIT $max_records
                        )";
                    cmd.Parameters.Add(new DuckDBParameter("max_records", clampedRecords));
                    totalDeleted += cmd.ExecuteNonQuery();
                }

                if (totalDeleted > 0)
                    _logger.LogInformation("Pruned {Count} old events from history.", totalDeleted);

                return totalDeleted;
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to prune events from DuckDB.");
            return 0;
        }
    }
}
