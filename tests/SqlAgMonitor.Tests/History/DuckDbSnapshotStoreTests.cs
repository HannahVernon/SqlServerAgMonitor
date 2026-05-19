using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SqlAgMonitor.Core.Models;
using SqlAgMonitor.Core.Services.History;
using SqlAgMonitor.Tests.Helpers;

namespace SqlAgMonitor.Tests.History;

public sealed class DuckDbSnapshotStoreTests : IAsyncLifetime
{
    private readonly string _testDir;
    private readonly ILogger<DuckDbConnectionManager> _dbLogger;
    private readonly ILogger<DuckDbSnapshotStore> _storeLogger;
    private DuckDbConnectionManager? _db;
    private DuckDbSnapshotStore? _store;

    public DuckDbSnapshotStoreTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "DuckDbSnapshotTests_" + Guid.NewGuid());
        _dbLogger = NullLoggerFactory.Instance.CreateLogger<DuckDbConnectionManager>();
        _storeLogger = NullLoggerFactory.Instance.CreateLogger<DuckDbSnapshotStore>();
    }

    public async Task InitializeAsync()
    {
        _db = new DuckDbConnectionManager(_dbLogger, _testDir);
        await _db.InitializeAsync();
        _store = new DuckDbSnapshotStore(_db, _storeLogger);
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

    private static MonitoredGroupSnapshot BuildSnapshot(
        string groupName = "TestAG",
        DateTimeOffset? timestamp = null,
        Action<SnapshotBuilder>? configure = null)
    {
        var builder = new SnapshotBuilder()
            .WithName(groupName)
            .WithTimestamp(timestamp ?? DateTimeOffset.UtcNow);

        configure?.Invoke(builder);
        return builder.Build();
    }

    [Fact]
    public async Task RecordSnapshotAsync_StoresData_ThatCanBeQueried()
    {
        var ts = DateTimeOffset.UtcNow;
        var snapshot = BuildSnapshot(timestamp: ts, configure: b => b
            .AddReplica("Server1", ReplicaRole.Primary, r => r.AddDatabase("DB1"))
            .AddReplica("Server2", ReplicaRole.Secondary, r => r.AddDatabase("DB1")));

        await _store!.RecordSnapshotAsync(snapshot);

        var data = await _store.GetSnapshotDataAsync(ts.AddMinutes(-1), ts.AddMinutes(1));
        Assert.Equal(2, data.Count);
        Assert.Contains(data, d => d.ReplicaName == "Server1");
        Assert.Contains(data, d => d.ReplicaName == "Server2");
    }

    [Fact]
    public async Task GetSnapshotDataAsync_WithGroupNameFilter_ReturnsOnlyMatching()
    {
        var ts = DateTimeOffset.UtcNow;

        var snap1 = BuildSnapshot("AG1", ts, b => b
            .AddReplica("S1", ReplicaRole.Primary, r => r.AddDatabase("DB1")));
        var snap2 = BuildSnapshot("AG2", ts, b => b
            .AddReplica("S2", ReplicaRole.Primary, r => r.AddDatabase("DB1")));

        await _store!.RecordSnapshotAsync(snap1);
        await _store.RecordSnapshotAsync(snap2);

        var data = await _store.GetSnapshotDataAsync(ts.AddMinutes(-1), ts.AddMinutes(1), groupName: "AG1");
        Assert.Single(data);
        Assert.Equal("AG1", data[0].GroupName);
    }

    [Fact]
    public async Task GetSnapshotDataAsync_WithReplicaNameFilter_ReturnsOnlyMatching()
    {
        var ts = DateTimeOffset.UtcNow;
        var snapshot = BuildSnapshot(timestamp: ts, configure: b => b
            .AddReplica("ServerA", ReplicaRole.Primary, r => r.AddDatabase("DB1"))
            .AddReplica("ServerB", ReplicaRole.Secondary, r => r.AddDatabase("DB1")));

        await _store!.RecordSnapshotAsync(snapshot);

        var data = await _store.GetSnapshotDataAsync(
            ts.AddMinutes(-1), ts.AddMinutes(1), replicaName: "ServerA");
        Assert.Single(data);
        Assert.Equal("ServerA", data[0].ReplicaName);
    }

    [Fact]
    public async Task GetSnapshotDataAsync_ReturnsEmpty_ForNonMatchingTimeRange()
    {
        var ts = DateTimeOffset.UtcNow;
        var snapshot = BuildSnapshot(timestamp: ts, configure: b => b
            .AddReplica("S1", ReplicaRole.Primary, r => r.AddDatabase("DB1")));

        await _store!.RecordSnapshotAsync(snapshot);

        var data = await _store.GetSnapshotDataAsync(
            ts.AddHours(-5), ts.AddHours(-4));
        Assert.Empty(data);
    }

    [Fact]
    public async Task ResolveTierAsync_ReturnsRaw_ForShortRange()
    {
        var ts = DateTimeOffset.UtcNow;
        var snapshot = BuildSnapshot(timestamp: ts, configure: b => b
            .AddReplica("S1", ReplicaRole.Primary, r => r.AddDatabase("DB1")));
        await _store!.RecordSnapshotAsync(snapshot);

        var tier = await _store.ResolveTierAsync(ts.AddHours(-1), ts.AddHours(1));
        Assert.Equal(SnapshotTier.Raw, tier);
    }

    [Fact]
    public async Task ResolveTierAsync_FallsBackToRaw_WhenHigherTiersEmpty()
    {
        var ts = DateTimeOffset.UtcNow;
        var snapshot = BuildSnapshot(timestamp: ts, configure: b => b
            .AddReplica("S1", ReplicaRole.Primary, r => r.AddDatabase("DB1")));
        await _store!.RecordSnapshotAsync(snapshot);

        // Request 100-day range — preferred tier is Daily, but no data there
        var tier = await _store.ResolveTierAsync(ts.AddDays(-100), ts.AddDays(1));
        Assert.Equal(SnapshotTier.Raw, tier);
    }

    [Fact]
    public async Task GetSnapshotFiltersAsync_ReturnsDistinctNames()
    {
        var ts = DateTimeOffset.UtcNow;

        var snap1 = BuildSnapshot("AG1", ts, b => b
            .AddReplica("S1", ReplicaRole.Primary, r => r.AddDatabase("DB1"))
            .AddReplica("S2", ReplicaRole.Secondary, r => r.AddDatabase("DB2")));
        var snap2 = BuildSnapshot("AG2", ts, b => b
            .AddReplica("S3", ReplicaRole.Primary, r => r.AddDatabase("DB3")));

        await _store!.RecordSnapshotAsync(snap1);
        await _store.RecordSnapshotAsync(snap2);

        var filters = await _store.GetSnapshotFiltersAsync();

        Assert.Equal(2, filters.GroupNames.Count);
        Assert.Contains("AG1", filters.GroupNames);
        Assert.Contains("AG2", filters.GroupNames);
        Assert.Equal(3, filters.ReplicaNames.Count);
        Assert.Equal(3, filters.DatabaseNames.Count);
    }

    [Fact]
    public async Task GetSnapshotFiltersAsync_FiltersByGroupName()
    {
        var ts = DateTimeOffset.UtcNow;

        var snap1 = BuildSnapshot("AG1", ts, b => b
            .AddReplica("S1", ReplicaRole.Primary, r => r.AddDatabase("DB1")));
        var snap2 = BuildSnapshot("AG2", ts, b => b
            .AddReplica("S2", ReplicaRole.Primary, r => r.AddDatabase("DB2")));

        await _store!.RecordSnapshotAsync(snap1);
        await _store.RecordSnapshotAsync(snap2);

        var filters = await _store.GetSnapshotFiltersAsync(groupName: "AG1");

        Assert.Single(filters.ReplicaNames);
        Assert.Equal("S1", filters.ReplicaNames[0]);
        Assert.Single(filters.DatabaseNames);
        Assert.Equal("DB1", filters.DatabaseNames[0]);
    }

    [Fact]
    public async Task RecordSnapshotAsync_WithNoReplicas_IsNoOp()
    {
        var snapshot = BuildSnapshot();

        await _store!.RecordSnapshotAsync(snapshot);

        var data = await _store.GetSnapshotDataAsync(
            DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1));
        Assert.Empty(data);
    }

    [Fact]
    public async Task GetSnapshotDataAsync_WhenNotInitialized_ReturnsEmpty()
    {
        var uninitDir = Path.Combine(_testDir, "uninit");
        var uninitDb = new DuckDbConnectionManager(_dbLogger, uninitDir);
        var uninitStore = new DuckDbSnapshotStore(uninitDb, _storeLogger);

        var data = await uninitStore.GetSnapshotDataAsync(
            DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1));

        Assert.Empty(data);
        await uninitDb.DisposeAsync();
    }
}
