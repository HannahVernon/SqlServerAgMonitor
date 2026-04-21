using Microsoft.Extensions.Logging;
using NSubstitute;
using SqlAgMonitor.Core.Services.Credentials;

namespace SqlAgMonitor.Tests.Credentials;

public sealed class AesCredentialStoreTests : IDisposable
{
    private readonly string _testDir;
    private readonly ILogger<AesCredentialStore> _logger;
    private readonly IPasswordStrengthValidator _validator;

    private const string MasterPassword = "T3st!Master#Password_2026_xK9mQ";

    public AesCredentialStoreTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AesTests_" + Guid.NewGuid());
        _logger = Substitute.For<ILogger<AesCredentialStore>>();
        _validator = Substitute.For<IPasswordStrengthValidator>();

        _validator.Validate(Arg.Any<string>())
            .Returns(new PasswordStrengthResult(true, 150.0, "OK"));
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

    private AesCredentialStore CreateStore(string? dir = null) =>
        new(_logger, _validator, dir ?? _testDir);

    [Fact]
    public void Unlock_WithValidPassword_SetsIsUnlockedTrue()
    {
        using var store = CreateStore();

        store.Unlock(MasterPassword);

        Assert.True(store.IsUnlocked);
    }

    [Fact]
    public void Unlock_WithWeakPassword_ThrowsArgumentException()
    {
        _validator.Validate("weak")
            .Returns(new PasswordStrengthResult(false, 20.0, "Too weak"));

        using var store = CreateStore();

        var ex = Assert.Throws<ArgumentException>(() => store.Unlock("weak"));
        Assert.Contains("strength requirements", ex.Message);
    }

    [Fact]
    public void Lock_ZerosKeyAndSetsIsUnlockedFalse()
    {
        using var store = CreateStore();
        store.Unlock(MasterPassword);

        store.Lock();

        Assert.False(store.IsUnlocked);
    }

    [Fact]
    public async Task StorePassword_ThenGetPassword_RoundtripsCorrectly()
    {
        using var store = CreateStore();
        store.Unlock(MasterPassword);

        await store.StorePasswordAsync("server1", "s3cret!");
        var retrieved = await store.GetPasswordAsync("server1");

        Assert.Equal("s3cret!", retrieved);
    }

    [Fact]
    public async Task GetPassword_ForNonExistentKey_ReturnsNull()
    {
        using var store = CreateStore();
        store.Unlock(MasterPassword);

        var result = await store.GetPasswordAsync("missing-key");

        Assert.Null(result);
    }

    [Fact]
    public async Task HasPassword_ReturnsTrue_ForStoredKey()
    {
        using var store = CreateStore();
        store.Unlock(MasterPassword);
        await store.StorePasswordAsync("mykey", "myvalue");

        Assert.True(await store.HasPasswordAsync("mykey"));
    }

    [Fact]
    public async Task HasPassword_ReturnsFalse_ForMissingKey()
    {
        using var store = CreateStore();
        store.Unlock(MasterPassword);

        Assert.False(await store.HasPasswordAsync("nope"));
    }

    [Fact]
    public async Task DeletePassword_RemovesCredential()
    {
        using var store = CreateStore();
        store.Unlock(MasterPassword);
        await store.StorePasswordAsync("deleteMe", "val");

        await store.DeletePasswordAsync("deleteMe");

        Assert.False(await store.HasPasswordAsync("deleteMe"));
        Assert.Null(await store.GetPasswordAsync("deleteMe"));
    }

    [Fact]
    public async Task Operations_WhileLocked_ThrowInvalidOperationException()
    {
        using var store = CreateStore();

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.GetPasswordAsync("k"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.StorePasswordAsync("k", "v"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeletePasswordAsync("k"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.HasPasswordAsync("k"));
    }

    [Fact]
    public async Task Operations_AfterDispose_ThrowObjectDisposedException()
    {
        var store = CreateStore();
        store.Unlock(MasterPassword);
        store.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.GetPasswordAsync("k"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.StorePasswordAsync("k", "v"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.DeletePasswordAsync("k"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.HasPasswordAsync("k"));
    }

    [Fact]
    public void SetMasterPassword_CreatesNewSaltFile()
    {
        using var store = CreateStore();

        store.SetMasterPassword(MasterPassword);

        var saltFile = Path.Combine(_testDir, "credentials.salt");
        Assert.True(File.Exists(saltFile));
        Assert.True(store.IsUnlocked);
    }

    [Fact]
    public async Task WrongMasterPassword_FailsToDecrypt()
    {
        using var store = CreateStore();
        store.Unlock(MasterPassword);
        await store.StorePasswordAsync("key1", "secret");
        store.Lock();

        // Unlock with a different password — same salt, different derived key
        store.Unlock("An0ther!Strong#Password_2026_zZ");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.GetPasswordAsync("key1"));
        Assert.Contains("master password", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MultipleCredentials_StoredAndRetrievedIndependently()
    {
        using var store = CreateStore();
        store.Unlock(MasterPassword);

        await store.StorePasswordAsync("sql-prod", "ProdPass1!");
        await store.StorePasswordAsync("sql-dev", "DevPass2@");
        await store.StorePasswordAsync("sql-staging", "StagePass3#");

        Assert.Equal("ProdPass1!", await store.GetPasswordAsync("sql-prod"));
        Assert.Equal("DevPass2@", await store.GetPasswordAsync("sql-dev"));
        Assert.Equal("StagePass3#", await store.GetPasswordAsync("sql-staging"));
    }

    [Fact]
    public async Task Store_PersistsAcrossNewInstances()
    {
        var sharedDir = Path.Combine(_testDir, "persist");

        using (var store1 = CreateStore(sharedDir))
        {
            store1.Unlock(MasterPassword);
            await store1.StorePasswordAsync("persisted-key", "persisted-value");
        }

        using var store2 = CreateStore(sharedDir);
        store2.Unlock(MasterPassword);

        var result = await store2.GetPasswordAsync("persisted-key");
        Assert.Equal("persisted-value", result);
    }
}
