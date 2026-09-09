# Measurement log

Numbers recorded against the milestones in [DESIGN.md](DESIGN.md) §10.

---

## M2 — USB transport · 2026-09-06

**Device:** Samsung Galaxy A53 5G (SM-A536E), Android 15 (API 35)
**Upstream:** Vodafone TR, LTE
**Transport:** USB cable, ADB port forward (`adb forward tcp:47811 tcp:47811`)

| Measurement | Result |
|---|---|
| Latency (32 round trips) | min 1.51 ms · **median 1.84 ms** · max 20.37 ms |
| Download (phone → PC) | 339 MiB in 10.0 s = **284 Mbps** |
| Upload (PC → phone) | 293 MiB in 10.0 s = **245 Mbps** |

Phone-side log agreed independently (285 Mbps down, 245 Mbps up), so the
measurement is not an artefact of one side's accounting.

### Verdict: **PASS — proceed to M3**

The transport ceiling is roughly an order of magnitude above LTE, so the cable
is nowhere near the bottleneck. Per §10, this is the outcome that matters: any
throughput shortfall measured from here on is attributable to our tunnel code
rather than to the link, which is exactly the position the prototype plan was
designed to establish before the NAT is written.

Latency at 1.84 ms median leaves ample headroom for interactive traffic and
gaming, which the §9 diagnostics will need to preserve end to end.

### Notes

- Asymmetry (284 down vs 245 up) is expected and not concerning: the upload
  path writes from a managed buffer through ADB's USB pipe, and the direction
  that matters most for the product is download.
- The 20.37 ms latency outlier is a single sample, consistent with scheduler
  jitter on the phone rather than the link.
- `sys.usb.state` was `rndis,adb` throughout — USB tethering happened to be
  enabled on the device. That is *not* what carried this test: the probe ran
  over the ADB-forwarded loopback port, not the rndis interface. Worth
  re-testing with tethering off to remove any doubt.

### Caveat on the transport

This used ADB port forwarding, which requires USB debugging. That is fine for
proving the ceiling but is not shippable to end users; a production build needs
a USB accessory (AOA) connection instead. The work sits behind the `Transport`
interface, so the swap does not affect anything downstream.

---

## Stability · 2026-09-07 · **stable**

Reported as the connection breaking repeatedly, cycling through
reconnecting/connected, and then losing internet entirely. Now runs with no
reconnects at all. Four bugs, found in the order they were masking each other.

### 1. The service was stopping itself (root cause)

The foreground service's notification was removed at the same millisecond all
four links closed with "Socket closed", while the process stayed alive and the
group was still `formed=true`. Nothing external killed it: the notification's
Stop action was arriving without anyone pressing it, and the service obeyed.

This is what made every other symptom look like a network problem. ACTION_STOP
now requires a flag that only the UI and the quick-settings tile set.

### 2. Traffic died at the first reconnect

`PumpOutbound` loops until cancellation and never returns, but the code started
the pump thread expecting a later iteration to pick up a replacement pump. That
iteration never came, so after a reconnect the thread sat inside the *old*
pump, reading packets from the adapter and handing them to a disposed tunnel
client.

Every indicator stayed green — the links really were up, nothing was feeding
them. The pump now lives for the session and swaps its client in place, which
is also the only way the earlier flow-reset fix could ever have worked.

### 3. No recovery when the group died

The reconnect ladder only retried the tunnel socket. When the P2P group goes,
there is nothing to reconnect *to* and the PC has fallen off the network
entirely, so retrying the same address could never succeed. §8.1 called for
rebuilding the group; only the socket rungs existed. The phone now rebuilds the
channel and group (with backoff, plus a watchdog for firmware that drops a
group with no callback), and the PC rejoins the Wi-Fi after three failures.

### 4. Wi-Fi power save

A partial wake lock keeps the CPU awake but says nothing about the radio, which
sleeps between packets and takes the P2P group with it. Added a
WIFI_MODE_FULL_HIGH_PERF lock.

### What this cost, and why

Three of these four were only visible from the phone's logcat, and each masked
the ones beneath it: the self-stop looked like a network drop, which made the
missing group recovery look like the whole problem, which hid the pump bug
until reconnects started succeeding. The user's observation that internet died
*at the first reconnect specifically* is what isolated the pump — the logs
alone showed four healthy links and no error.

---

## Full system working · 2026-09-06

All PC traffic routed through the phone over Wi-Fi Direct via the Wintun
adapter. Real browsing, 76 concurrent streams, reported as "way much faster"
after the fixes below — a usable connection rather than a demo.

### What was actually slow, in order of impact

**1. No TCP window scaling (dominant).** The synthesised SYN-ACK carried no TCP
options at all, so every connection's receive window was capped at 65535 bytes
for its whole life. Windows sent 64 KB then waited for an ACK to cross the
tunnel and return — about 64KB/RTT, roughly 10 Mbps per connection at 50 ms,
regardless of a 156 Mbps link. Advertising a window scale of 7 (8 MB window)
removed the ceiling and produced the step change.

**2. No DNS cache.** §6.3 was specified but never built, so every lookup was a
full round trip and a page needs 10–30 before its first byte of content. That
was the "takes ages to start loading" symptom specifically, distinct from
throughput.

**3. Per-frame syscalls and MTU.** Both sides wrote each frame as separate
header/payload/flush calls, putting two or three small segments on the wire per
frame with TCP_NODELAY set; the adapter MTU was never configured, so Windows
emitted oversized segments that then had to be split. Symptom: 62k packets
carrying only 22 MB, about 370 bytes per packet.

The lesson worth keeping: every one of these was a correctness bug in the
userspace TCP bridge, not a limit of Wi-Fi Direct or of the architecture. The
link had measured 156 Mbps from the start.

---

## M2 + M3 — Wi-Fi Direct · 2026-09-06 · **PASS**

Run end to end with no cable attached: standalone exes, no adb, no USB
debugging. Phone as Group Owner, PC joined the group from Windows Wi-Fi
settings.

### M2 — raw link

| Measurement | Wi-Fi Direct | USB reference |
|---|---|---|
| Latency (median) | **1.72 ms** | 1.84 ms |
| Download (phone -> PC) | **156.0 Mbps** | 284.2 Mbps |
| Upload (PC -> phone) | **100.7 Mbps** | 245.2 Mbps |

Roughly half the cable's throughput, which is expected: Wi-Fi Direct is
half-duplex on a shared channel. The bar was never USB, it was LTE, and 156
Mbps clears that with room to spare. Latency is effectively identical to the
cable, which matters more than raw throughput for interactive traffic.

### M3 — tunnel end to end

```
[M3] Opening stream to 1.1.1.1:80 ...
[M3] Received 389 bytes in 122 ms
[M3] Server said: HTTP/1.1 301 Moved Permanently
```

**A real HTTP response, fetched over the phone's cellular data, delivered to
the PC through the tunnel.** The PC's connection stopped at the phone; the app
opened its own socket bound to the cellular Network and relayed the bytes back.
On the carrier's side this was ordinary app traffic from the phone's own IP —
which is the entire premise of the product (§3.1), now demonstrated rather than
asserted.

### Verdict: **go**

All three milestones pass. Per §10 the project continues to full v1, and the
next piece is the Wintun virtual adapter (§7) so that *all* PC traffic routes
this way rather than one connection at a time.

Note the earlier stall is fixed and stayed fixed: StreamRelay used to drop the
first payload if it arrived while connect() was still in flight, which it
reliably does. First attempt after the fix succeeded.

---

## M1 — Wi-Fi Direct · 2026-09-06 · **PASS**

Group formed with the phone as Group Owner, and the Windows PC joined it. The
phone's "PC joined" field reported `yes (1)`, so association was confirmed from
the Android side rather than inferred from the PC.

This clears M1: the wireless transport works on a Samsung device, which was the
vendor flagged as the main firmware risk in §11.

Throughput over Wi-Fi Direct was not captured in this run and is still worth
measuring against the USB reference (284/245 Mbps) when convenient. It is not a
blocker: M2 has already established that a transport comfortably exceeding LTE
exists, so M3 can proceed on the USB figure alone.

### Note on what "failed" meant in this run

The PC had no internet after joining the group, which read as a Wi-Fi Direct
failure but is not one. Nothing routes traffic yet — the userspace NAT (§6) and
the Wintun adapter (§7) are M3 and were not built at the time of the test. A
joined P2P group with no tunnel behind it is a bare link between two devices,
exactly as the USB link was.

Worth stating plainly because it is the natural misreading: **Wi-Fi Direct does
not carry the internet.** It is only the pipe between PC and phone, the same
role the cable played. Cellular remains the internet path, and the app bridges
the two by opening its own sockets on the cellular network (§3.1).
