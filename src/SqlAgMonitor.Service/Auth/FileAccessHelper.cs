using Microsoft.Extensions.Logging;
using CoreHelper = SqlAgMonitor.Core.Services.FileAccessHelper;

namespace SqlAgMonitor.Service.Auth;

/// <summary>
/// Thin wrapper that delegates to <see cref="CoreHelper"/> in SqlAgMonitor.Core.
/// Kept for backward compatibility with existing callers in the Service project.
/// </summary>
internal static class FileAccessHelper
{
    public static void RestrictToCurrentUser(string filePath, ILogger logger)
        => CoreHelper.RestrictToCurrentUser(filePath, logger);
}
