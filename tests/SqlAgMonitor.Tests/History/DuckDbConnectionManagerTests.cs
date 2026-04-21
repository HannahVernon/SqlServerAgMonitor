using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SqlAgMonitor.Core.Services.History;

namespace SqlAgMonitor.Tests.History;

public sealed class DuckDbConnectionManagerTests : IAsyncLifetime
{
    private readonly string _testDir;
    private readonly ILogger<DuckDbConnectionManager> _logger;
    private DuckDbConnectionManager? _db;

    public DuckDbConnectionManagerTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "DuckDbMgrTests_" + Guid.NewGuid());
        _logger = NullLoggerFactory.Instance.CreateLogger<DuckDbConnectionManager>();
    }

    public Task InitializeAsync()
    {
        _db = new DuckDbConnectionManager(_logger, _testDir);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_db != null)
            await _db.DisposeAsync();

        try
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    [Fact]
    public async Task InitializeAsync_CreatesDatabase()
    {
        await _db!.InitializeAsync();

        var dbPath = Path.Combine(_testDir, "events.duckdb");
        Assert.True(File.Exists(dbPath));
    }

    [Fact]
    public async Task IsInitialized_TrueAfterInit()
    {
        await _db!.InitializeAsync();

        Assert.True(_db.IsInitialized);
    }

    [Fact]
    public void IsInitialized_FalseBeforeInit()
    {
        Assert.False(_db!.IsInitialized);
    }

    [Fact]
    public async Task InitializeAsync_IsIdempotent()
    {
        await _db!.InitializeAsync();
        await _db.InitializeAsync();

        Assert.True(_db.IsInitialized);
    }

    [Fact]
    public async Task ExecuteAsync_Generic_ReturnsResult()
    {
        await _db!.InitializeAsync();

        var result = await _db.ExecuteAsync(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 42";
            return Convert.ToInt32(cmd.ExecuteScalar());
        });

        Assert.Equal(42, result);
    }

    [Fact]
    public async Task ExecuteAsync_Void_ExecutesAction()
    {
        await _db!.InitializeAsync();

        var executed = false;
        await _db.ExecuteAsync(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            cmd.ExecuteScalar();
            executed = true;
        });

        Assert.True(executed);
    }

    [Fact]
    public async Task DisposeAsync_SubsequentExecuteThrows()
    {
        await _db!.InitializeAsync();
        await _db.DisposeAsync();

        // After dispose the semaphore is disposed, so ExecuteAsync should throw
        await Assert.ThrowsAnyAsync<ObjectDisposedException>(async () =>
        {
            await _db.ExecuteAsync(conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT 1";
                return Convert.ToInt32(cmd.ExecuteScalar());
            });
        });
    }

    [Fact]
    public async Task SchemaTables_ExistAfterInit()
    {
        await _db!.InitializeAsync();

        var tables = await _db.ExecuteAsync(conn =>
        {
            var result = new List<string>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT table_name FROM information_schema.tables WHERE table_schema = 'main' ORDER BY table_name";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                result.Add(reader.GetString(0));
            return result;
        });

        Assert.Contains("_schema_meta", tables);
        Assert.Contains("events", tables);
        Assert.Contains("snapshots", tables);
        Assert.Contains("snapshot_hourly", tables);
        Assert.Contains("snapshot_daily", tables);
    }
}
