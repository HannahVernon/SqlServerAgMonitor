using System.Reflection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SqlAgMonitor.Service.Hubs;

namespace SqlAgMonitor.Tests.Hubs;

public sealed class HubExceptionFilterTests
{
    private readonly HubExceptionFilter _filter;

    public HubExceptionFilterTests()
    {
        var logger = new NullLogger<HubExceptionFilter>();
        _filter = new HubExceptionFilter(logger);
    }

    [Fact]
    public async Task InvokeMethodAsync_Success_ReturnsResult()
    {
        var context = CreateInvocationContext();
        var result = await _filter.InvokeMethodAsync(
            context,
            _ => new ValueTask<object?>("ok"));

        Assert.Equal("ok", result);
    }

    [Fact]
    public async Task InvokeMethodAsync_HubException_RethrowsAsIs()
    {
        var context = CreateInvocationContext();
        var original = new HubException("client-safe message");

        var thrown = await Assert.ThrowsAsync<HubException>(async () =>
            await _filter.InvokeMethodAsync(
                context,
                _ => throw original));

        Assert.Same(original, thrown);
        Assert.Equal("client-safe message", thrown.Message);
    }

    [Fact]
    public async Task InvokeMethodAsync_GenericException_ThrowsGenericHubException()
    {
        var context = CreateInvocationContext();

        var thrown = await Assert.ThrowsAsync<HubException>(async () =>
            await _filter.InvokeMethodAsync(
                context,
                _ => throw new InvalidOperationException("secret internal detail")));

        Assert.DoesNotContain("secret", thrown.Message);
        Assert.Contains("internal error", thrown.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvokeMethodAsync_ExceptionDoesNotLeakStackTrace()
    {
        var context = CreateInvocationContext();

        var thrown = await Assert.ThrowsAsync<HubException>(async () =>
            await _filter.InvokeMethodAsync(
                context,
                _ => throw new Exception("C:\\secrets\\config.json line 42")));

        Assert.DoesNotContain("C:\\", thrown.Message);
        Assert.DoesNotContain("config.json", thrown.Message);
    }

    private static HubInvocationContext CreateInvocationContext()
    {
        var callerContext = new FakeHubCallerContext();
        var serviceProvider = Substitute.For<IServiceProvider>();
        var hub = Substitute.For<Hub>();
        var methodInfo = typeof(Hub).GetMethod("ToString") ?? typeof(object).GetMethod("ToString")!;

        return new HubInvocationContext(
            callerContext,
            serviceProvider,
            hub,
            methodInfo,
            Array.Empty<object?>());
    }

    private sealed class FakeHubCallerContext : HubCallerContext
    {
        public override string ConnectionId => "test-conn-1";
        public override string? UserIdentifier => null;
        public override System.Security.Claims.ClaimsPrincipal? User => null;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }
}
