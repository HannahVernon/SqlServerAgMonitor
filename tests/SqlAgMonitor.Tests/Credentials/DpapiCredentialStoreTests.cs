using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using NSubstitute;
using SqlAgMonitor.Core.Services.Credentials;

namespace SqlAgMonitor.Tests.Credentials;

[Trait("Category", "Windows")]
[SupportedOSPlatform("windows")]
public sealed class DpapiCredentialStoreTests : IDisposable
{
    private readonly string _testDir;
    private readonly ILogger<DpapiCredentialStore> _logger;

    public DpapiCredentialStoreTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "DpapiTests_" + Guid.NewGuid());
        _logger = Substitute.For<ILogger<DpapiCredentialStore>>();
    }

    public void Dispose()
    {
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

    private DpapiCredentialStore CreateStore(string? dir = null) =>
        new(_logger, dir ?? _testDir);

    [Fact]
    public async Task StorePassword_ThenGetPassword_Roundtrips()
    {
        using var store = CreateStore();

        await store.StorePasswordAsync("key1", "password1");
        var result = await store.GetPasswordAsync("key1");

        Assert.Equal("password1", result);
    }

    [Fact]
    public async Task GetPassword_ForMissingKey_ReturnsNull()
    {
        using var store = CreateStore();

        var result = await store.GetPasswordAsync("nonexistent");

        Assert.Null(result);
    }

    [Fact]
    public async Task HasPassword_ReturnsTrue_ForStoredKey()
    {
        using var store = CreateStore();
        await store.StorePasswordAsync("exists", "val");

        Assert.True(await store.HasPasswordAsync("exists"));
    }

    [Fact]
    public async Task HasPassword_ReturnsFalse_ForMissingKey()
    {
        using var store = CreateStore();

        Assert.False(await store.HasPasswordAsync("nope"));
    }

    [Fact]
    public async Task DeletePassword_RemovesCredential()
    {
        using var store = CreateStore();
        await store.StorePasswordAsync("to-delete", "secret");

        await store.DeletePasswordAsync("to-delete");

        Assert.False(await store.HasPasswordAsync("to-delete"));
        Assert.Null(await store.GetPasswordAsync("to-delete"));
    }

    [Fact]
    public async Task MultipleCredentials_StoredIndependently()
    {
        using var store = CreateStore();

        await store.StorePasswordAsync("k1", "v1");
        await store.StorePasswordAsync("k2", "v2");
        await store.StorePasswordAsync("k3", "v3");

        Assert.Equal("v1", await store.GetPasswordAsync("k1"));
        Assert.Equal("v2", await store.GetPasswordAsync("k2"));
        Assert.Equal("v3", await store.GetPasswordAsync("k3"));
    }

    [Fact]
    public async Task Store_PersistsAcrossInstances()
    {
        var sharedDir = Path.Combine(_testDir, "persist");

        using (var store1 = CreateStore(sharedDir))
        {
            await store1.StorePasswordAsync("saved-key", "saved-value");
        }

        using var store2 = CreateStore(sharedDir);
        var result = await store2.GetPasswordAsync("saved-key");

        Assert.Equal("saved-value", result);
    }

    [Fact]
    public async Task CorruptStoreFile_ReturnsNull()
    {
        Directory.CreateDirectory(_testDir);
        var storePath = Path.Combine(_testDir, "credentials.dat");
        await File.WriteAllTextAsync(storePath, "!!!not-json{{{corrupt");

        using var store = CreateStore();
        var result = await store.GetPasswordAsync("anything");

        Assert.Null(result);
    }

    [Fact]
    public async Task EmptyStoreFile_ReturnsNull()
    {
        Directory.CreateDirectory(_testDir);
        var storePath = Path.Combine(_testDir, "credentials.dat");
        await File.WriteAllTextAsync(storePath, "");

        using var store = CreateStore();
        var result = await store.GetPasswordAsync("anything");

        Assert.Null(result);
    }
}
