using System.Runtime.InteropServices;

namespace SqlAgMonitor.Tests.Helpers;

/// <summary>
/// An xUnit [Fact] that automatically skips on non-Windows platforms.
/// </summary>
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Skip = "This test requires Windows (DPAPI is not available on Linux/macOS).";
        }
    }
}
