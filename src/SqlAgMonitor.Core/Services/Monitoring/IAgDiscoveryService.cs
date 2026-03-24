using SqlAgMonitor.Core.Models;

namespace SqlAgMonitor.Core.Services.Monitoring;

public interface IAgDiscoveryService
{
    Task<IReadOnlyList<DiscoveredGroup>> DiscoverGroupsAsync(string server, string? username, string? credentialKey, string authType, CancellationToken cancellationToken = default);
}
