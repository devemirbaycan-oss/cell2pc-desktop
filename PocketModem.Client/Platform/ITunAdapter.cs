namespace PocketModem.Client.Platform;

/// <summary>
/// A virtual network interface carrying raw IP packets.
///
/// Wintun on Windows, /dev/net/tun on Linux. Everything above this line - the
/// packet pump, the tunnel protocol, the session - is platform-agnostic, so a
/// port replaces these implementations rather than the logic that uses them.
/// </summary>
public interface ITunAdapter : IDisposable
{
    string Name { get; }

    /// <summary>
    /// Read one outbound IP packet, blocking up to <paramref name="timeoutMs"/>.
    /// Returns null on timeout so the caller can check for cancellation.
    /// </summary>
    byte[]? ReceivePacket(uint timeoutMs = 250);

    /// <summary>
    /// Inject one inbound IP packet into the host's network stack. Returns
    /// false when the ring or queue is full, which is a legitimate drop: TCP
    /// retransmits, and blocking here would stall every other stream.
    /// </summary>
    bool SendPacket(ReadOnlySpan<byte> packet);
}

/// <summary>
/// Address, DNS, MTU and route changes for the tunnel, with the ability to
/// undo them.
///
/// The worst failure in this design is a leftover default route pointing at a
/// tunnel that no longer exists, which leaves the machine with no internet and
/// no obvious fix. Implementations must journal their undo steps before
/// applying anything.
/// </summary>
public interface IRouteManager : IDisposable
{
    /// <summary>
    /// When set, Revert leaves the routes alone. Used when handing over to a
    /// replacement process so an update does not interrupt connectivity.
    /// </summary>
    bool HandingOver { get; set; }

    /// <summary>
    /// Adopt routes that are still working, or undo ones left by a crash.
    /// The two are indistinguishable on disk, so the caller supplies a check.
    /// </summary>
    bool RecoverOrAdopt(Func<bool> tunnelReachable);

    void RecoverIfNeeded();

    /// <summary>
    /// Keep these destinations off the tunnel by routing them via the machine's
    /// normal gateway.
    ///
    /// Excluding traffic has to happen in the route table, not in the packet
    /// pump: a packet that reaches the tunnel adapter has already left the
    /// normal path, and there is no way to hand it back.
    /// </summary>
    void ExcludeRoutes(IEnumerable<string> destinations);
    void ConfigureInterface(string interfaceName, string address, string mask, string dns, int mtu);
    void ApplyTunnelRoutes(string interfaceName, string tunnelAddress, string peerAddress, string peerGateway);
    void Revert();
}

/// <summary>Joins the phone's Wi-Fi Direct group.</summary>
public interface IWifiJoiner
{
    bool IsVisible();
    Task<bool> JoinAsync(string passphrase, CancellationToken ct = default);
    void Leave();
}
