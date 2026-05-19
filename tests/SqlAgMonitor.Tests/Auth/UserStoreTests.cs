using Microsoft.Extensions.Logging;
using NSubstitute;
using SqlAgMonitor.Service.Auth;

namespace SqlAgMonitor.Tests.Auth;

public sealed class UserStoreTests : IDisposable
{
    private readonly string _testDir;
    private readonly ILogger<UserStore> _logger;

    public UserStoreTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "UserStoreTests_" + Guid.NewGuid());
        _logger = Substitute.For<ILogger<UserStore>>();
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

    private UserStore CreateStore(string? dir = null) =>
        new(_logger, dir ?? _testDir);

    [Fact]
    public void CreateUser_ThenValidateCredentials_Succeeds()
    {
        var store = CreateStore();

        Assert.True(store.CreateUser("admin", "P@ssw0rd!"));
        Assert.True(store.ValidateCredentials("admin", "P@ssw0rd!"));
    }

    [Fact]
    public void ValidateCredentials_WrongPassword_ReturnsFalse()
    {
        var store = CreateStore();
        store.CreateUser("admin", "P@ssw0rd!");

        Assert.False(store.ValidateCredentials("admin", "WrongPassword"));
    }

    [Fact]
    public void ValidateCredentials_NonExistentUser_ReturnsFalse()
    {
        var store = CreateStore();

        Assert.False(store.ValidateCredentials("nobody", "anything"));
    }

    [Fact]
    public void HasUsers_ReturnsFalseInitially_TrueAfterCreate()
    {
        var store = CreateStore();

        Assert.False(store.HasUsers());

        store.CreateUser("first", "password123");

        Assert.True(store.HasUsers());
    }

    [Fact]
    public void CreateUser_DuplicateUsername_ReturnsFalse()
    {
        var store = CreateStore();

        Assert.True(store.CreateUser("admin", "pass1"));
        Assert.False(store.CreateUser("admin", "pass2"));
    }

    [Fact]
    public void CreateUser_IsCaseInsensitive()
    {
        var store = CreateStore();

        Assert.True(store.CreateUser("Admin", "pass1"));
        Assert.False(store.CreateUser("admin", "pass2"));
        Assert.False(store.CreateUser("ADMIN", "pass3"));
    }

    [Fact]
    public void GetUsernames_ReturnsCreatedUsernames()
    {
        var store = CreateStore();
        store.CreateUser("alice", "pass1");
        store.CreateUser("bob", "pass2");

        var usernames = store.GetUsernames();

        Assert.Equal(2, usernames.Count);
        Assert.Contains(usernames, u => u.Equals("alice", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(usernames, u => u.Equals("bob", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ChangePassword_Succeeds_OldPasswordFails_NewWorks()
    {
        var store = CreateStore();
        store.CreateUser("admin", "OldPass1!");

        Assert.True(store.ChangePassword("admin", "NewPass2@"));
        Assert.False(store.ValidateCredentials("admin", "OldPass1!"));
        Assert.True(store.ValidateCredentials("admin", "NewPass2@"));
    }

    [Fact]
    public void ChangePassword_NonExistentUser_ReturnsFalse()
    {
        var store = CreateStore();

        Assert.False(store.ChangePassword("nobody", "NewPass"));
    }

    [Fact]
    public void DataPersists_AcrossInstances()
    {
        var sharedDir = Path.Combine(_testDir, "persist");

        var store1 = CreateStore(sharedDir);
        store1.CreateUser("persist_user", "MySecret!");

        var store2 = CreateStore(sharedDir);

        Assert.True(store2.HasUsers());
        Assert.True(store2.ValidateCredentials("persist_user", "MySecret!"));
    }

    [Fact]
    public void MultipleUsers_WorkIndependently()
    {
        var store = CreateStore();
        store.CreateUser("user1", "pass1");
        store.CreateUser("user2", "pass2");

        Assert.True(store.ValidateCredentials("user1", "pass1"));
        Assert.False(store.ValidateCredentials("user1", "pass2"));
        Assert.True(store.ValidateCredentials("user2", "pass2"));
        Assert.False(store.ValidateCredentials("user2", "pass1"));
    }

    [Fact]
    public void ValidateCredentials_IsCaseInsensitive_OnUsername()
    {
        var store = CreateStore();
        store.CreateUser("Admin", "Secret!");

        Assert.True(store.ValidateCredentials("admin", "Secret!"));
        Assert.True(store.ValidateCredentials("ADMIN", "Secret!"));
    }
}
