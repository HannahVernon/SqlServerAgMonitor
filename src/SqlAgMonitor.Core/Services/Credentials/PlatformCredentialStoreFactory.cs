using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace SqlAgMonitor.Core.Services.Credentials;

public static class PlatformCredentialStoreFactory
{
    public static ICredentialStore Create(
        ILoggerFactory loggerFactory,
        IPasswordStrengthValidator passwordValidator,
        string? storeDirectory = null)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                return new DpapiCredentialStore(
                    loggerFactory.CreateLogger<DpapiCredentialStore>(),
                    storeDirectory);
            }
            catch (Exception)
            {
                // Fall through to AES store
            }
        }

        return new AesCredentialStore(
            loggerFactory.CreateLogger<AesCredentialStore>(),
            passwordValidator,
            storeDirectory);
    }
}
