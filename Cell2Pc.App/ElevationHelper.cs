using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Cell2Pc.App;

/// <summary>
/// Elevation, needed to create the adapter and edit routes (DESIGN.md 7.4).
///
/// The app relaunches itself elevated rather than demanding the user find
/// "Run as administrator" - and only when they actually press Connect, so
/// simply opening the window does not trigger a UAC prompt.
/// </summary>
internal static class ElevationHelper
{
    public static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows()) return true;  // Linux checks euid at use
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static void RestartElevated()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null) return;

            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                Verb = "runas",
            });
            Environment.Exit(0);
        }
        catch (Exception)
        {
            // The user declined the UAC prompt; leave the window as it is so
            // they can read why elevation was needed.
        }
    }
}
