using System.Globalization;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;
using SqlAgMonitor.Core.Services;

namespace SqlAgMonitor.Core.Services.History;

/// <summary>
/// Owns the DuckDB embedded connection, serialization lock, schema initialization,
/// and disposal. All database operations go through <see cref="ExecuteAsync{T}"/>
/// which acquires <c>_opLock</c> and offloads work to the thread pool (DuckDB.NET
/// is synchronous).
/// </summary>
internal sealed class DuckDbConnectionManager : IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _opLock = new(1, 1);
    private DuckDBConnection? _connection;
    private bool _initialized;
    private bool _disposed;

    public bool IsInitialized => _initialized;

    public DuckDbConnectionManager(ILogger<DuckDbConnectionManager> logger, string? dataDirectory = null)
    {
        _logger = logger;
        var dir = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SqlAgMonitor", "data");
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "events.duckdb");
        _connectionString = $"Data Source={_dbPath}";
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
                return;

            // DuckDB Open() is synchronous — run on thread pool
            await Task.Run(() =>
            {
                _connection = new DuckDBConnection(_connectionString);
                _connection.Open();

                // Restrict the database file to the current user
                if (File.Exists(_dbPath))
                    FileAccessHelper.RestrictToCurrentUser(_dbPath, _logger);

                // Force UTC timezone to prevent TIMESTAMPTZ double-offset conversion.
                using var tzCmd = _connection.CreateCommand();
                tzCmd.CommandText = "SET TimeZone = 'UTC'";
                tzCmd.ExecuteNonQuery();

                // One-time schema migration: drop tables that used TIMESTAMPTZ
                // (which stored timestamps with a double timezone offset).
                using var migCmd = _connection.CreateCommand();
                migCmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS _schema_meta (key VARCHAR PRIMARY KEY, value VARCHAR);
                ";
                migCmd.ExecuteNonQuery();

                using var verCmd = _connection.CreateCommand();
                verCmd.CommandText = "SELECT value FROM _schema_meta WHERE key = 'schema_version'";
                var versionObj = verCmd.ExecuteScalar();
                var version = versionObj != null && versionObj != DBNull.Value ? int.Parse((string)versionObj, CultureInfo.InvariantCulture) : 0;

                if (version < 2)
                {
                    _logger.LogInformation("Migrating DuckDB schema from version {Old} to 2 (TIMESTAMPTZ → TIMESTAMP).", version);
                    using var dropCmd = _connection.CreateCommand();
                    dropCmd.CommandText = @"
                        DROP TABLE IF EXISTS events;
                        DROP TABLE IF EXISTS snapshots;
                        DROP TABLE IF EXISTS snapshot_hourly;
                        DROP TABLE IF EXISTS snapshot_daily;
                        DROP SEQUENCE IF EXISTS event_seq;
                    ";
                    dropCmd.ExecuteNonQuery();

                    using var setVerCmd = _connection.CreateCommand();
                    setVerCmd.CommandText = @"
                        INSERT INTO _schema_meta (key, value) VALUES ('schema_version', '2')
                        ON CONFLICT (key) DO UPDATE SET value = '2';
                    ";
                    setVerCmd.ExecuteNonQuery();
                }

                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    CREATE SEQUENCE IF NOT EXISTS event_seq START 1;

                    CREATE TABLE IF NOT EXISTS events (
                        id BIGINT PRIMARY KEY,
                        timestamp TIMESTAMP NOT NULL,
                        alert_type VARCHAR NOT NULL,
                        group_name VARCHAR NOT NULL,
                        replica_name VARCHAR,
                        database_name VARCHAR,
                        message VARCHAR NOT NULL,
                        severity VARCHAR NOT NULL,
                        email_sent BOOLEAN DEFAULT FALSE,
                        syslog_sent BOOLEAN DEFAULT FALSE
                    );

                    CREATE TABLE IF NOT EXISTS snapshots (
                        timestamp TIMESTAMP NOT NULL,
                        group_name VARCHAR NOT NULL,
                        group_type VARCHAR NOT NULL,
                        replica_name VARCHAR NOT NULL,
                        database_name VARCHAR NOT NULL,
                        role VARCHAR NOT NULL,
                        sync_state VARCHAR NOT NULL,
                        connected_state VARCHAR NOT NULL,
                        availability_mode VARCHAR NOT NULL,
                        last_hardened_lsn DECIMAL(25, 0),
                        last_commit_lsn DECIMAL(25, 0),
                        log_send_queue_kb BIGINT,
                        redo_queue_kb BIGINT,
                        log_send_rate_kb_per_sec BIGINT,
                        redo_rate_kb_per_sec BIGINT,
                        log_block_difference DECIMAL(25, 0),
                        secondary_lag_seconds BIGINT,
                        is_suspended BOOLEAN DEFAULT FALSE
                    );

                    CREATE TABLE IF NOT EXISTS snapshot_hourly (
                        bucket TIMESTAMP NOT NULL,
                        group_name VARCHAR NOT NULL,
                        replica_name VARCHAR NOT NULL,
                        database_name VARCHAR NOT NULL,
                        sample_count INTEGER NOT NULL,
                        log_send_queue_kb_min BIGINT,
                        log_send_queue_kb_max BIGINT,
                        log_send_queue_kb_avg DOUBLE,
                        redo_queue_kb_min BIGINT,
                        redo_queue_kb_max BIGINT,
                        redo_queue_kb_avg DOUBLE,
                        log_send_rate_min BIGINT,
                        log_send_rate_max BIGINT,
                        log_send_rate_avg DOUBLE,
                        redo_rate_min BIGINT,
                        redo_rate_max BIGINT,
                        redo_rate_avg DOUBLE,
                        log_block_diff_min DECIMAL(25, 0),
                        log_block_diff_max DECIMAL(25, 0),
                        log_block_diff_avg DOUBLE,
                        secondary_lag_min BIGINT,
                        secondary_lag_max BIGINT,
                        secondary_lag_avg DOUBLE,
                        last_role VARCHAR,
                        last_sync_state VARCHAR,
                        any_suspended BOOLEAN,
                        last_hardened_lsn DECIMAL(25, 0),
                        last_commit_lsn DECIMAL(25, 0),
                        PRIMARY KEY (bucket, group_name, replica_name, database_name)
                    );

                    CREATE TABLE IF NOT EXISTS snapshot_daily (
                        bucket TIMESTAMP NOT NULL,
                        group_name VARCHAR NOT NULL,
                        replica_name VARCHAR NOT NULL,
                        database_name VARCHAR NOT NULL,
                        sample_count INTEGER NOT NULL,
                        log_send_queue_kb_min BIGINT,
                        log_send_queue_kb_max BIGINT,
                        log_send_queue_kb_avg DOUBLE,
                        redo_queue_kb_min BIGINT,
                        redo_queue_kb_max BIGINT,
                        redo_queue_kb_avg DOUBLE,
                        log_send_rate_min BIGINT,
                        log_send_rate_max BIGINT,
                        log_send_rate_avg DOUBLE,
                        redo_rate_min BIGINT,
                        redo_rate_max BIGINT,
                        redo_rate_avg DOUBLE,
                        log_block_diff_min DECIMAL(25, 0),
                        log_block_diff_max DECIMAL(25, 0),
                        log_block_diff_avg DOUBLE,
                        secondary_lag_min BIGINT,
                        secondary_lag_max BIGINT,
                        secondary_lag_avg DOUBLE,
                        last_role VARCHAR,
                        last_sync_state VARCHAR,
                        any_suspended BOOLEAN,
                        last_hardened_lsn DECIMAL(25, 0),
                        last_commit_lsn DECIMAL(25, 0),
                        PRIMARY KEY (bucket, group_name, replica_name, database_name)
                    );
                ";
                cmd.ExecuteNonQuery();
            }, cancellationToken).ConfigureAwait(false);

            _initialized = true;
            _logger.LogInformation("DuckDB event history initialized at {Path}.", _dbPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize DuckDB at {Path}.", _dbPath);
            throw;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Acquires the operation lock, runs <paramref name="operation"/> on the thread pool,
    /// and releases the lock. Callers should check <see cref="IsInitialized"/> before calling.
    /// </summary>
    public async Task<T> ExecuteAsync<T>(Func<DuckDBConnection, T> operation, CancellationToken cancellationToken = default)
    {
        await _opLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => operation(_connection!), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _opLock.Release();
        }
    }

    /// <summary>
    /// Acquires the operation lock, runs <paramref name="operation"/> on the thread pool,
    /// and releases the lock. Callers should check <see cref="IsInitialized"/> before calling.
    /// </summary>
    public async Task ExecuteAsync(Action<DuckDBConnection> operation, CancellationToken cancellationToken = default)
    {
        await _opLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() => operation(_connection!), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _opLock.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_connection != null)
            {
                try { _connection.Dispose(); }
                catch (Exception ex) { _logger.LogDebug(ex, "Error disposing DuckDB connection."); }
            }
            _initLock.Dispose();
            _opLock.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}
