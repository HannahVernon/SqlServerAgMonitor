using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SqlAgMonitor.Core.Models;
using SqlAgMonitor.Core.Services.History;

namespace SqlAgMonitor.Tests.History;

public sealed class DuckDbEventStoreTests : IAsyncLifetime
{
    private readonly string _testDir;
    private readonly ILogger<DuckDbConnectionManager> _dbLogger;
    private readonly ILogger<DuckDbEventStore> _storeLogger;
    private DuckDbConnectionManager? _db;
    private DuckDbEventStore? _store;

    public DuckDbEventStoreTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "DuckDbTests_" + Guid.NewGuid());
        _dbLogger = NullLoggerFactory.Instance.CreateLogger<DuckDbConnectionManager>();
        _storeLogger = NullLoggerFactory.Instance.CreateLogger<DuckDbEventStore>();
    }

    public async Task InitializeAsync()
    {
        _db = new DuckDbConnectionManager(_dbLogger, _testDir);
        await _db.InitializeAsync();
        _store = new DuckDbEventStore(_db, _storeLogger);
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

    private static AlertEvent CreateEvent(
        AlertType alertType = AlertType.ConnectionLost,
        string groupName = "AG1",
        string message = "Test event",
        AlertSeverity severity = AlertSeverity.Warning,
        DateTimeOffset? timestamp = null,
        string? replicaName = null,
        string? databaseName = null)
    {
        return new AlertEvent
        {
            Id = 0,
            Timestamp = timestamp ?? DateTimeOffset.UtcNow,
            AlertType = alertType,
            GroupName = groupName,
            Message = message,
            Severity = severity,
            ReplicaName = replicaName,
            DatabaseName = databaseName,
            EmailSent = false,
            SyslogSent = false
        };
    }

    [Fact]
    public async Task RecordEventAsync_StoresEvent()
    {
        await _store!.RecordEventAsync(CreateEvent());

        var count = await _store.GetEventCountAsync();
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task GetEventsAsync_ReturnsRecordedEvents()
    {
        await _store!.RecordEventAsync(CreateEvent(message: "First"));
        await _store.RecordEventAsync(CreateEvent(message: "Second"));

        var events = await _store.GetEventsAsync();

        Assert.Equal(2, events.Count);
    }

    [Fact]
    public async Task GetEventsAsync_WithGroupNameFilter_ReturnsOnlyMatching()
    {
        await _store!.RecordEventAsync(CreateEvent(groupName: "AG1"));
        await _store.RecordEventAsync(CreateEvent(groupName: "AG2"));
        await _store.RecordEventAsync(CreateEvent(groupName: "AG1"));

        var events = await _store.GetEventsAsync(groupName: "AG1");

        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal("AG1", e.GroupName));
    }

    [Fact]
    public async Task GetEventsAsync_WithSinceFilter_ReturnsOnlyNewerEvents()
    {
        var old = DateTimeOffset.UtcNow.AddHours(-2);
        var recent = DateTimeOffset.UtcNow.AddMinutes(-5);

        await _store!.RecordEventAsync(CreateEvent(timestamp: old, message: "old"));
        await _store.RecordEventAsync(CreateEvent(timestamp: recent, message: "recent"));

        var cutoff = DateTimeOffset.UtcNow.AddHours(-1);
        var events = await _store.GetEventsAsync(since: cutoff);

        Assert.Single(events);
        Assert.Equal("recent", events[0].Message);
    }

    [Fact]
    public async Task GetEventsAsync_RespectsLimitParameter()
    {
        for (var i = 0; i < 5; i++)
            await _store!.RecordEventAsync(CreateEvent(message: $"Event {i}"));

        var events = await _store!.GetEventsAsync(limit: 3);

        Assert.Equal(3, events.Count);
    }

    [Fact]
    public async Task GetEventsAsync_ReturnsEventsOrderedByTimestampDesc()
    {
        var t1 = DateTimeOffset.UtcNow.AddMinutes(-30);
        var t2 = DateTimeOffset.UtcNow.AddMinutes(-20);
        var t3 = DateTimeOffset.UtcNow.AddMinutes(-10);

        await _store!.RecordEventAsync(CreateEvent(timestamp: t1, message: "oldest"));
        await _store.RecordEventAsync(CreateEvent(timestamp: t3, message: "newest"));
        await _store.RecordEventAsync(CreateEvent(timestamp: t2, message: "middle"));

        var events = await _store.GetEventsAsync();

        Assert.Equal("newest", events[0].Message);
        Assert.Equal("middle", events[1].Message);
        Assert.Equal("oldest", events[2].Message);
    }

    [Fact]
    public async Task GetEventCountAsync_ReturnsCorrectCount()
    {
        await _store!.RecordEventAsync(CreateEvent());
        await _store.RecordEventAsync(CreateEvent());
        await _store.RecordEventAsync(CreateEvent());

        var count = await _store.GetEventCountAsync();

        Assert.Equal(3, count);
    }

    [Fact]
    public async Task GetEventCountAsync_WithGroupNameFilter_ReturnsFilteredCount()
    {
        await _store!.RecordEventAsync(CreateEvent(groupName: "AG1"));
        await _store.RecordEventAsync(CreateEvent(groupName: "AG2"));
        await _store.RecordEventAsync(CreateEvent(groupName: "AG1"));

        var count = await _store.GetEventCountAsync(groupName: "AG2");

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task PruneEventsAsync_ByMaxAgeDays_RemovesOldEvents()
    {
        var old = DateTimeOffset.UtcNow.AddDays(-10);
        var recent = DateTimeOffset.UtcNow.AddMinutes(-5);

        await _store!.RecordEventAsync(CreateEvent(timestamp: old, message: "old"));
        await _store.RecordEventAsync(CreateEvent(timestamp: recent, message: "recent"));

        var deleted = await _store.PruneEventsAsync(maxAgeDays: 5, maxRecords: null);

        Assert.Equal(1, deleted);
        var remaining = await _store.GetEventsAsync();
        Assert.Single(remaining);
        Assert.Equal("recent", remaining[0].Message);
    }

    [Fact]
    public async Task PruneEventsAsync_ByMaxRecords_KeepsOnlyNewestN()
    {
        var t1 = DateTimeOffset.UtcNow.AddMinutes(-30);
        var t2 = DateTimeOffset.UtcNow.AddMinutes(-20);
        var t3 = DateTimeOffset.UtcNow.AddMinutes(-10);

        await _store!.RecordEventAsync(CreateEvent(timestamp: t1, message: "oldest"));
        await _store.RecordEventAsync(CreateEvent(timestamp: t2, message: "middle"));
        await _store.RecordEventAsync(CreateEvent(timestamp: t3, message: "newest"));

        var deleted = await _store.PruneEventsAsync(maxAgeDays: null, maxRecords: 2);

        Assert.Equal(1, deleted);
        var remaining = await _store.GetEventsAsync();
        Assert.Equal(2, remaining.Count);
        Assert.Contains(remaining, e => e.Message == "newest");
        Assert.Contains(remaining, e => e.Message == "middle");
    }

    [Fact]
    public async Task RecordEventAsync_WithNullOptionalFields_Works()
    {
        var evt = CreateEvent(replicaName: null, databaseName: null);

        await _store!.RecordEventAsync(evt);

        var events = await _store.GetEventsAsync();
        Assert.Single(events);
        Assert.Null(events[0].ReplicaName);
        Assert.Null(events[0].DatabaseName);
    }

    [Fact]
    public async Task GetEventsAsync_WhenNotInitialized_ReturnsEmpty()
    {
        var uninitDb = new DuckDbConnectionManager(_dbLogger, Path.Combine(_testDir, "uninit"));
        var uninitStore = new DuckDbEventStore(uninitDb, _storeLogger);

        var events = await uninitStore.GetEventsAsync();

        Assert.Empty(events);
        await uninitDb.DisposeAsync();
    }
}
