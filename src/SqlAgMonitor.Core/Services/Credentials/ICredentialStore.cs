namespace SqlAgMonitor.Core.Services.Credentials;

public interface ICredentialStore : IDisposable
{
    Task<string?> GetPasswordAsync(string credentialKey, CancellationToken cancellationToken = default);
    Task StorePasswordAsync(string credentialKey, string password, CancellationToken cancellationToken = default);
    Task DeletePasswordAsync(string credentialKey, CancellationToken cancellationToken = default);
    Task<bool> HasPasswordAsync(string credentialKey, CancellationToken cancellationToken = default);
}
