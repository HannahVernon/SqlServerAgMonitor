using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace SqlAgMonitor.Installer;

/// <summary>
/// Handles silent uninstall when launched with /uninstall flag from Add/Remove Programs.
/// </summary>
internal static class UninstallHandler
{
    private const string ServiceName = "SqlAgMonitorService";
    private const string RegistryKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\SqlAgMonitorService";

    public static int Run()
    {
        Console.WriteLine("SqlAgMonitor Service — Uninstall");
        Console.WriteLine();

        try
        {
            // Read install location from registry
            string? installPath = null;
            using (var key = Registry.LocalMachine.OpenSubKey(RegistryKeyPath))
            {
                installPath = key?.GetValue("InstallLocation") as string;
            }

            // Stop the service
            Console.Write("Stopping service... ");
            RunProcess("sc.exe", $"stop {ServiceName}");
            System.Threading.Thread.Sleep(3000);
            Console.WriteLine("done.");

            // Delete the service
            Console.Write("Removing service... ");
            var result = RunProcess("sc.exe", $"delete {ServiceName}");
            Console.WriteLine(result == 0 ? "done." : $"sc.exe returned {result}.");

            // Remove published files
            if (!string.IsNullOrEmpty(installPath) && Directory.Exists(installPath))
            {
                Console.Write($"Removing files from {installPath}... ");
                try
                {
                    Directory.Delete(installPath, recursive: true);
                    Console.WriteLine("done.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"partial — {ex.Message}");
                }
            }

            // Remove AppData service files
            var appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SqlAgMonitor", "service");
            if (Directory.Exists(appDataPath))
            {
                Console.Write("Removing service data files... ");
                try
                {
                    Directory.Delete(appDataPath, recursive: true);
                    Console.WriteLine("done.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"partial — {ex.Message}");
                }
            }

            // Remove registry keys
            Console.Write("Removing Add/Remove Programs entry... ");
            try
            {
                Registry.LocalMachine.DeleteSubKeyTree(RegistryKeyPath, throwOnMissingSubKey: false);
                Console.WriteLine("done.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"failed — {ex.Message}");
            }

            Console.WriteLine();
            Console.WriteLine("Uninstall complete.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Uninstall failed: {ex.Message}");
            return 1;
        }
    }

    private static int RunProcess(string fileName, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            process?.WaitForExit(30000);
            return process?.ExitCode ?? -1;
        }
        catch
        {
            return -1;
        }
    }
}
