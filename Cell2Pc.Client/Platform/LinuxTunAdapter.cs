using System.Runtime.InteropServices;

namespace Cell2Pc.Client.Platform;

/// <summary>
/// Linux TUN adapter over /dev/net/tun.
///
/// The kernel side is far simpler than Wintun: open the device, issue TUNSETIFF
/// to name the interface and ask for IP packets without the 4-byte protocol
/// header, then read and write packets as ordinary file I/O. No ring buffer and
/// no driver to install - tun is in every mainstream kernel.
///
/// Requires CAP_NET_ADMIN, which in practice means running as root or granting
/// the capability to the binary. That is the same privilege the Windows side
/// needs to create its adapter.
/// </summary>
public sealed class LinuxTunAdapter : ITunAdapter
{
    private const string TunDevice = "/dev/net/tun";

    // From linux/if_tun.h
    private const short IFF_TUN = 0x0001;
    private const short IFF_NO_PI = 0x1000;   // no per-packet protocol header
    private const uint TUNSETIFF = 0x400454ca;

    private const int O_RDWR = 2;
    private const int MaxPacket = 65536;

    private readonly int _fd;
    private readonly byte[] _readBuffer = new byte[MaxPacket];

    public string Name { get; }

    [DllImport("libc", SetLastError = true)]
    private static extern int open(string pathname, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, uint request, byte[] argp);

    [DllImport("libc", SetLastError = true)]
    private static extern nint read(int fd, byte[] buf, nint count);

    [DllImport("libc", SetLastError = true)]
    private static extern nint write(int fd, byte[] buf, nint count);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int poll([In, Out] PollFd[] fds, uint nfds, int timeout);

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    private const short POLLIN = 0x001;

    private LinuxTunAdapter(int fd, string name)
    {
        _fd = fd;
        Name = name;
    }

    public static LinuxTunAdapter Create(string name = "cell2pc")
    {
        int fd = open(TunDevice, O_RDWR);
        if (fd < 0)
        {
            int err = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(
                $"Could not open {TunDevice} (errno {err}). " +
                "Run as root, or grant CAP_NET_ADMIN to this binary. " +
                "If the file is missing, load the tun module: modprobe tun");
        }

        // struct ifreq: 16 bytes of name, then flags.
        var ifreq = new byte[40];
        var nameBytes = System.Text.Encoding.ASCII.GetBytes(name);
        if (nameBytes.Length > 15)
            throw new ArgumentException("interface name must be 15 characters or fewer", nameof(name));
        nameBytes.CopyTo(ifreq, 0);
        BitConverter.GetBytes((short)(IFF_TUN | IFF_NO_PI)).CopyTo(ifreq, 16);

        if (ioctl(fd, TUNSETIFF, ifreq) < 0)
        {
            int err = Marshal.GetLastWin32Error();
            close(fd);
            throw new InvalidOperationException(
                $"TUNSETIFF failed (errno {err}). CAP_NET_ADMIN is required.");
        }

        return new LinuxTunAdapter(fd, name);
    }

    public byte[]? ReceivePacket(uint timeoutMs = 250)
    {
        // poll rather than a blocking read, so cancellation is observed
        // promptly instead of waiting for the next packet.
        var fds = new[] { new PollFd { Fd = _fd, Events = POLLIN } };
        int ready = poll(fds, 1, (int)timeoutMs);
        if (ready <= 0) return null;

        nint n = read(_fd, _readBuffer, MaxPacket);
        if (n <= 0) return null;

        var packet = new byte[n];
        Array.Copy(_readBuffer, packet, (int)n);
        return packet;
    }

    public bool SendPacket(ReadOnlySpan<byte> packet)
    {
        var buf = packet.ToArray();
        return write(_fd, buf, buf.Length) == buf.Length;
    }

    public void Dispose()
    {
        if (_fd >= 0) close(_fd);
    }
}
