using PocketModem.Client.Net;
using PocketModem.Client.Wintun;

namespace PocketModem.Client.Platform;

/// <summary>
/// Picks the implementations for the host OS.
///
/// The single place the codebase asks which platform it is on. Everything above
/// it - session, packet pump, tunnel protocol - is written against the
/// interfaces, so adding a platform means adding implementations here rather
/// than threading conditionals through the logic.
/// </summary>
public static class PlatformFactory
{
    /// <summary>
    /// The Android group owner always sits at .1 on this subnet, on every
    /// platform - it is a property of Android's P2P stack, not of the host.
    /// </summary>
    public const string PhoneAddress = "192.168.49.1";
    public const string P2pPrefix = "192.168.49.";

    public static bool IsSupported =>
        OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    public static ITunAdapter CreateAdapter(string name)
    {
        if (OperatingSystem.IsWindows())
        {
            var adapter = WintunAdapter.Create(name);
            adapter.StartSession();
            return adapter;
        }
        if (OperatingSystem.IsLinux())
            return LinuxTunAdapter.Create(name);

        throw new PlatformNotSupportedException(
            "PocketModem supports Windows and Linux. macOS would need a utun " +
            "implementation, which Apple permits but which is not written yet.");
    }

    public static IRouteManager CreateRouteManager()
    {
        if (OperatingSystem.IsWindows()) return new RouteManager();
        if (OperatingSystem.IsLinux()) return new LinuxRouteManager();
        throw new PlatformNotSupportedException("Unsupported platform.");
    }

    public static IWifiJoiner CreateWifiJoiner(string ssid = "DIRECT-WD-PocketModem")
    {
        if (OperatingSystem.IsWindows()) return new WifiJoiner(ssid);
        if (OperatingSystem.IsLinux()) return new LinuxWifiJoiner(ssid);
        throw new PlatformNotSupportedException("Unsupported platform.");
    }

    /// <summary>True when this PC already holds an address on the phone's subnet.</summary>
    public static bool IsJoinedToPhone() =>
        OperatingSystem.IsLinux() ? LinuxWifiJoiner.IsJoined() : WifiJoiner.IsJoined();

    /// <summary>
    /// Creating a network interface and editing routes is privileged on both
    /// platforms: Administrator on Windows, CAP_NET_ADMIN (in practice root)
    /// on Linux.
    /// </summary>
    public static bool IsPrivileged()
    {
        if (OperatingSystem.IsWindows())
        {
#pragma warning disable CA1416 // guarded by the check above
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
#pragma warning restore CA1416
        }
        return Environment.IsPrivilegedProcess;
    }

    public static string PrivilegeHint() => OperatingSystem.IsWindows()
        ? "Run as Administrator: creating the network adapter and changing routes is privileged."
        : "Run with sudo, or grant CAP_NET_ADMIN: creating the tun interface and changing routes is privileged.";
}
