using Microsoft.Extensions.Logging;

#if NET
using System.Security.AccessControl;
using System.Security.Principal;
#endif

namespace SqlAgMonitor.Core.Services;

/// <summary>
/// Restricts file access to the current user only.
/// On Windows, replaces the ACL with a single entry for the current user.
/// On Unix, sets mode 0600 (owner read/write).
/// </summary>
public static class FileAccessHelper
{
    public static void RestrictToCurrentUser(string filePath, ILogger? logger)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var fileInfo = new FileInfo(filePath);
                var security = fileInfo.GetAccessControl();

                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

                var rules = security.GetAccessRules(
                    includeExplicit: true, includeInherited: false, targetType: typeof(SecurityIdentifier));
                foreach (FileSystemAccessRule rule in rules)
                {
                    security.RemoveAccessRule(rule);
                }

                var currentUser = WindowsIdentity.GetCurrent().User!;
                security.AddAccessRule(new FileSystemAccessRule(
                    currentUser,
                    FileSystemRights.FullControl,
                    AccessControlType.Allow));

                fileInfo.SetAccessControl(security);
            }
            else
            {
                File.SetUnixFileMode(filePath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to restrict file permissions on {Path}.", filePath);
        }
    }
}
