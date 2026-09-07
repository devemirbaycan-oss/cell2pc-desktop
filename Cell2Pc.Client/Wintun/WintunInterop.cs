using System.Runtime.InteropServices;

namespace Cell2Pc.Client.Wintun;

/// <summary>
/// P/Invoke surface for wintun.dll (DESIGN.md §7.1).
///
/// Wintun is WireGuard's Windows TUN driver: a small signed kernel driver with
/// a ring-buffer userspace API. Using a real virtual NIC rather than a proxy is
/// what makes every application work with no configuration — browsers, Steam,
/// Windows Update and games alike.
///
/// The DLL is not redistributed here. Download wintun.dll (amd64) from
/// https://www.wintun.net and place it next to the executable.
/// </summary>
internal static class WintunInterop
{
    private const string Dll = "wintun.dll";

    /// <summary>Ring capacity. Must be a power of two between 128 KiB and 64 MiB.</summary>
    public const uint RingCapacity = 0x400000; // 4 MiB

    [DllImport(Dll, EntryPoint = "WintunCreateAdapter", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateAdapter(
        [MarshalAs(UnmanagedType.LPWStr)] string name,
        [MarshalAs(UnmanagedType.LPWStr)] string tunnelType,
        ref Guid requestedGuid);

    [DllImport(Dll, EntryPoint = "WintunOpenAdapter", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr OpenAdapter([MarshalAs(UnmanagedType.LPWStr)] string name);

    [DllImport(Dll, EntryPoint = "WintunCloseAdapter", SetLastError = true)]
    public static extern void CloseAdapter(IntPtr adapter);

    [DllImport(Dll, EntryPoint = "WintunGetAdapterLUID")]
    public static extern void GetAdapterLuid(IntPtr adapter, out ulong luid);

    [DllImport(Dll, EntryPoint = "WintunStartSession", SetLastError = true)]
    public static extern IntPtr StartSession(IntPtr adapter, uint capacity);

    [DllImport(Dll, EntryPoint = "WintunEndSession")]
    public static extern void EndSession(IntPtr session);

    [DllImport(Dll, EntryPoint = "WintunGetReadWaitEvent")]
    public static extern IntPtr GetReadWaitEvent(IntPtr session);

    /// <summary>Returns a pointer to a received packet, or NULL with ERROR_NO_MORE_ITEMS.</summary>
    [DllImport(Dll, EntryPoint = "WintunReceivePacket", SetLastError = true)]
    public static extern IntPtr ReceivePacket(IntPtr session, out uint packetSize);

    [DllImport(Dll, EntryPoint = "WintunReleaseReceivePacket")]
    public static extern void ReleaseReceivePacket(IntPtr session, IntPtr packet);

    /// <summary>Allocates space in the send ring, or NULL with ERROR_BUFFER_OVERFLOW.</summary>
    [DllImport(Dll, EntryPoint = "WintunAllocateSendPacket", SetLastError = true)]
    public static extern IntPtr AllocateSendPacket(IntPtr session, uint packetSize);

    [DllImport(Dll, EntryPoint = "WintunSendPacket")]
    public static extern void SendPacket(IntPtr session, IntPtr packet);

    public const int ErrorNoMoreItems = 259;
    public const int ErrorBufferOverflow = 111;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
}
