# PocketModem — Technical Design

**Status:** Draft v1 · **Date:** 2026-09-06
**Goal:** Wireless internet sharing from an Android phone's cellular data to a Windows PC, over Wi-Fi Direct, with no root and without enabling the system hotspot.

**Stack:** Android host — native Kotlin + Jetpack Compose. Windows client — C# .NET with Wintun.

---

## 1. Problem statement

A Windows PC needs full internet access routed through an Android phone's mobile data connection, such that:

- **No root** on the phone.
- **No system hotspot / USB tethering** — the OS tethering stack is never enabled, so carrier hotspot metering and hotspot-blocking policies do not apply in the usual way.
- **All applications work** — not just a browser. No system proxy configuration, no per-app setup.
- **Fast and reliable** — the specific failure modes that make PdaNet+ unpleasant (throughput collapse, freezes, mandatory reconnects) are the things this project exists to fix.

This is the PdaNet Wi-Fi Direct architecture, rebuilt on modern APIs.

### 1.1 Why this niche is empty

Wi-Fi Direct (`WifiP2pManager`) is available to ordinary apps, but Android deliberately does not let an app NAT/forward foreign traffic onto the cellular interface. `LocalOnlyHotspot` explicitly has no internet access. So a working product must supply its *own* transport, its *own* userspace TCP/UDP forwarding, and its *own* Windows virtual network adapter. That is three hard components plus cross-vendor Wi-Fi Direct quirks — which is why dozens of proxy/hotspot apps exist and almost no true Wi-Fi-Direct-plus-desktop-client tunnels do.

The gap is real, and it is the reason PdaNet remains relevant despite its age. Its moat is accumulated compatibility work, not a clever core idea.

### 1.2 Non-goals for v1

- Bluetooth transport.
- USB transport (deferred — see §12).
- HTTP/SOCKS proxy mode.
- Dozens of legacy compatibility modes.
- Routing *Android's own* app traffic (that would be a VPN product, not this).
- Multiple simultaneous PCs.

---

## 2. Platform and stack decisions

### 2.1 Android only

Wi-Fi Direct is the defining requirement of the product, and it is precisely the capability iOS does not expose (§2.3). There is no second mobile platform, so the phone side is a single native Android app.

**Android host capabilities:**

| Capability | API | Status |
|---|---|---|
| Create a P2P group without an AP | `WifiP2pManager.createGroup()` | Supported |
| Discover / accept peers | `WifiP2pManager` + broadcasts | Supported |
| Plain sockets over the P2P link | `java.net.Socket` on the P2P interface | Supported |
| Outbound connections pinned to cellular | `ConnectivityManager` + `Network.socketFactory` | Supported |
| Forward foreign IP packets to cellular | — | **Not available** — hence userspace forwarding (§6) |

**Target range: Android 12 → 17.** Designed against the strictest modern rules from day one rather than retrofitted later.

### 2.2 Native Kotlin + Compose, not React Native

React Native was considered and dropped. Its value would have been one UI codebase across two mobile platforms; with iOS excluded, that value disappears. What remains is a bridge, a JS runtime and a second build toolchain sitting between a handful of screens and the Kotlin core that does all the real work.

The app is roughly five screens (Connect, Stats, Diagnostics, Devices, Settings) over a heavy native networking service. Compose talks to that service directly — no bridge, no serialization boundary on a path that carries live per-second stats.

### 2.3 iOS — excluded, because it cannot be a tethering host

Recorded here so the decision is not revisited. This is a hard platform limit, not an implementation gap:

- **No Wi-Fi Direct API.** Apple exposes no `WifiP2pManager` equivalent. `MultipeerConnectivity` creates a peer link (infrastructure Wi-Fi + AWDL + Bluetooth), but it is a message/stream API between *apps*, not a network interface a PC can route through, and there is no Windows-side peer implementation.
- **No third-party access to the cellular interface as a gateway.** An iOS app can open its own sockets but cannot act as a NAT for an external device.
- **`NEHotspotHelper` requires a carrier entitlement** Apple grants only to network operators. Not obtainable for a product like this.
- **`NEPacketTunnelProvider`** gives a TUN interface for routing *the device's own* traffic into a tunnel — the opposite direction from what is needed here.
- Personal Hotspot is a system feature with no third-party API surface.

**There is no stock-iOS path to "iPhone shares cellular with a PC over a Wi-Fi-Direct-like link, bypassing Personal Hotspot."** Any product claiming otherwise is either using Personal Hotspot underneath, or is not doing what it says.

The one thing iOS could do — consume a tunnel *from* an Android host over infrastructure Wi-Fi via `NEPacketTunnelProvider`, mirroring the Windows client — is listed in §12 as a possible future direction, not a v1 target.

---

## 3. Architecture

### 3.1 End-to-end data path

```
WINDOWS
  Applications (browser, Steam, Windows Update, games)
      | ordinary sockets
      v
  Windows TCP/IP stack
      | routes 0.0.0.0/0 via PocketModem adapter
      v
  Wintun virtual NIC                       <-- raw IPv4/IPv6 packets
      |
      v
  C# tunnel client
      - reads IP packets from the Wintun ring buffer
      - classifies: TCP / UDP / DNS / ICMP
      - multiplexes into tunnel streams
      v
  === Wi-Fi Direct link (5 GHz preferred) ===
      v
ANDROID
  Kotlin tunnel server (foreground service)
      - demultiplexes streams
      - userspace NAT: maps (stream id) <-> (real Android socket)
      v
  SocketChannel / DatagramChannel
      - bound to the cellular Network object
      v
  Cellular / Wi-Fi
      v
  Internet
```

The critical property: **Android never forwards a raw packet.** The Android process opens ordinary outbound sockets on its own behalf. Traffic leaves the phone as that app's own traffic — which is exactly why no root and no tethering stack are required.

### 3.2 Approach A (chosen) vs Approach B

**Approach A — userspace NAT / tun2socks style.** Windows sends IP packets; Android parses TCP/UDP and opens native sockets. **Chosen.** No root, traffic is indistinguishable from ordinary app traffic, and the NAT table is ours to tune.

**Approach B — Android `VpnService`.** Gives the app a TUN interface and raw packet read/write. Rejected for v1: it routes *the phone's* traffic into a tunnel, which is the wrong direction, and it would force a VPN consent dialog for no benefit. Revisit only if capturing Android-side traffic ever becomes a requirement.

### 3.3 Module layout

```
pocketmodem/
  docs/
    DESIGN.md
  android/                          Kotlin, single native app
    app/
      ui/                           Jetpack Compose
        ConnectScreen.kt
        StatsScreen.kt
        DiagnosticsScreen.kt
        DevicesScreen.kt
        SettingsScreen.kt
      state/
        TunnelState.kt              state machine (§8.2), exposed as StateFlow
    tunnel/
      P2pTransport.kt               WifiP2pManager lifecycle, group owner
      TunnelServer.kt               framing, stream demux, heartbeat
      NatTable.kt                   stream id <-> socket, eviction, limits
      TcpRelay.kt                   non-blocking per-stream pumps
      UdpRelay.kt                   datagram relay + NAT timeouts
      DnsCache.kt                   DNS interception and cache
      UpstreamBinder.kt             cellular Network selection, socket binding
      Pairing.kt                    QR/code pairing, remembered devices
    service/
      TunnelForegroundService.kt
  windows/                          C# .NET
    PocketModem.Client/
      Wintun/                       P/Invoke wrapper over wintun.dll
      Tunnel/                       framing, stream mux, reconnect ladder
      Net/                          route table, DNS stub, MTU probing
      Diagnostics/                  three-tier throughput measurement
    PocketModem.Service/           elevated helper (adapter + routes)
    PocketModem.Ui/                tray app + diagnostics window
```

---

## 4. Transport: the Wi-Fi Direct link

### 4.1 Group formation

The Android device **always becomes Group Owner** — `createGroup()` with a high group-owner intent. Relying on GO negotiation is a known source of cross-vendor flakiness (Samsung/Xiaomi/Pixel firmwares differ over who wins). Forcing the role removes an entire class of bugs.

The GO runs DHCP on a `192.168.49.0/24`-style subnet (Android's conventional P2P range). Windows joins as an ordinary client and receives an address. This link carries the tunnel; it is **not** the PC's default route source — the Wintun adapter is.

### 4.2 5 GHz preference

Request a 5 GHz operating band for the group where the device supports it, with graceful fallback to 2.4 GHz. Band and negotiated PHY rate are surfaced live in the UI (§9), because a silent 2.4 GHz fallback is the single most likely explanation for disappointing throughput.

### 4.3 Permissions

- Android 13+ (API 33): `NEARBY_WIFI_DEVICES` with `neverForLocation`, replacing the old location-permission requirement for P2P.
- Android 12 and below: `ACCESS_FINE_LOCATION` (P2P discovery is location-gated there).
- `FOREGROUND_SERVICE` plus the connected-device foreground service type.
- Android 14+: foreground service type declared in the manifest.
- Android 16+: local-network access restrictions are tightening; the P2P interface path must be validated on 16/17 previews early rather than at the end.

---

## 5. Tunnel protocol

### 5.1 v1: multiplexed TCP over a single connection

Deliberately unclever for v1. One TCP connection over the Wi-Fi Direct link, carrying framed messages.

```
+--------+--------+----------+--------+---------+
| type   | flags  | streamId | length | payload |
| 1 byte | 1 byte | 4 bytes  | 2 bytes|   ...   |
+--------+--------+----------+--------+---------+
```

| Type | Name | Direction | Purpose |
|---|---|---|---|
| 0x01 | `HELLO` | W→A | version, capabilities, pairing token |
| 0x02 | `HELLO_ACK` | A→W | session id, MTU, upstream info |
| 0x10 | `TCP_OPEN` | W→A | new stream: dst IP + port |
| 0x11 | `TCP_DATA` | both | stream payload |
| 0x12 | `TCP_CLOSE` | both | half/full close, with reason |
| 0x13 | `TCP_WINDOW` | both | flow-control credit update |
| 0x20 | `UDP_DATAGRAM` | both | src/dst IP+port + payload |
| 0x30 | `DNS_QUERY` | W→A | intercepted DNS |
| 0x31 | `DNS_REPLY` | A→W | from cache or upstream resolver |
| 0x40 / 0x41 | `PING` / `PONG` | both | 2 s heartbeat |
| 0x50 | `STATS` | A→W | upstream type, signal, byte counters |

### 5.2 Head-of-line blocking, and the v2 QUIC path

A single TCP tunnel means one lost Wi-Fi frame stalls *every* multiplexed connection behind it. This is very likely a large part of why PdaNet feels like it "freezes" — the symptom is a total stall rather than one slow download.

v1 mitigations: per-stream flow-control credits (`TCP_WINDOW`) so a bulk transfer cannot starve interactive streams, and `TCP_NODELAY` on the tunnel socket.

**v2: QUIC as the tunnel transport.** Independent streams eliminate cross-stream head-of-line blocking, and QUIC datagrams carry UDP naturally.

```
QUIC connection
  stream 0    = control
  stream N    = TCP connection N
  datagrams   = UDP
```

The v1 framing is deliberately transport-agnostic so this is a transport swap, not a rewrite.

### 5.3 Security

The Wi-Fi Direct group is WPA2-protected, but the group passphrase is not a strong per-session secret, and a nearby attacker who joins the group must not receive a free internet gateway.

- **Pairing:** QR code (or 6-digit code) shown on the phone, scanned/entered on the PC at first connect; establishes a shared secret.
- **Session:** tunnel encrypted with a key derived from that secret. In v2 this comes free with QUIC/TLS; in v1, Noise_NK or a ChaCha20-Poly1305 framing layer.
- **Authorization:** the phone keeps a remembered-devices list; unknown devices are rejected unless the user is on the pairing screen.

---

## 6. Android side: userspace NAT

### 6.1 TCP

`TCP_OPEN(streamId, dstIp, dstPort)` → open a `SocketChannel` to the destination, bound to the cellular `Network` (§6.4). All streams share a non-blocking selector; a thread-per-stream model will not survive a few hundred browser connections.

Flow control: bounded per-stream buffers with `TCP_WINDOW` credits back to Windows. A stream whose upstream socket is slow must apply backpressure, never buffer without limit.

Lifecycle: `TCP_CLOSE` propagates FIN/RST in both directions. Half-open states are honoured — some protocols depend on them.

### 6.2 UDP

`UDP_DATAGRAM` → a `DatagramChannel` per (srcPort, dstIp, dstPort) tuple, with an idle timeout (30 s default, longer for DNS-adjacent flows). This is what makes games, QUIC-based browsing and voice work — the thing simple proxy tethering apps get wrong.

### 6.3 DNS

Intercept UDP/53, answer from an in-process cache, forward misses to the phone's current resolvers via `LinkProperties`. Caching matters more than it looks: DNS latency over a stalled tunnel is what makes browsing *feel* broken even when raw throughput is fine.

### 6.4 Upstream binding

The Wi-Fi Direct interface and the cellular interface exist simultaneously, so outbound sockets must be pinned explicitly:

- `ConnectivityManager.requestNetwork()` with `TRANSPORT_CELLULAR`, then that `Network`'s socket factory / `bindSocket()` per socket.
- Do **not** rely on `bindProcessToNetwork()` alone — it is process-global and would also capture the P2P sockets.
- On upstream change (5G→LTE, cellular→Wi-Fi), bind new sockets to the new `Network`. Existing streams die naturally and Windows reopens them; the tunnel link itself stays up.

### 6.5 Staying alive

Foreground service with a persistent notification, a wake lock while the tunnel is active, and a battery-optimization exemption requested at first run. Doze or an OEM battery manager killing the service mid-session is a top-3 reliability risk.

---

## 7. Windows side: Wintun + C#

### 7.1 Why a virtual NIC

A real IP interface means every application works with zero configuration — no proxy settings, no per-app setup, and Steam/Windows Update/games behave normally. This is the single biggest product advantage over proxy-based competitors.

**Wintun** (WireGuard's Windows TUN driver) is the right layer: a small, signed, well-tested kernel driver with a simple ring-buffer userspace API, wrapped from C# via P/Invoke on `wintun.dll`.

### 7.2 Routing

On connect:
- Bring the adapter up with a private address (e.g. `10.87.0.2/24`).
- Add `0.0.0.0/0` via the adapter with a low metric.
- Add a **host route for the Android peer's P2P address via the Wi-Fi Direct interface** — without it the tunnel would try to route through itself. This is the classic split-tunnel bootstrap bug.
- Point the adapter's DNS at a local stub the client answers itself (§5.1 `DNS_QUERY`).
- On disconnect, restore prior routes and DNS. This must be crash-safe: a leftover default route with no tunnel behind it leaves the PC with no internet at all, the worst possible failure mode. Route changes go into a rollback journal, replayed on next start.

### 7.3 MTU

Wi-Fi Direct link MTU minus tunnel framing overhead. Start conservative (1400), then probe upward with DF-set packets. Fragmentation and PMTU black holes produce exactly the "some sites work, some hang forever" symptom.

### 7.4 Privilege model

Installing the Wintun adapter and editing the route table require elevation. The tray UI runs unelevated and talks to a small elevated Windows service over a local named pipe. A UAC prompt on every connect would be unacceptable.

---

## 8. Reliability: the reason this project exists

The complaints driving this build are *slow* and *unreliable*. Reliability is a first-class feature here, not polish.

### 8.1 Heartbeat and reconnect ladder

```
Windows -> PING every 2 s

no PONG for 6 s
    -> tear down tunnel socket, reopen over existing P2P link   (~1 s)

reopen fails 3x
    -> P2P link presumed dead: rejoin group                     (~5 s)

rejoin fails
    -> ask Android to recreate the group, then rejoin           (~10 s)

phone's upstream changed (5G -> LTE -> Wi-Fi)
    -> keep tunnel and Wintun adapter UP, rebind upstream sockets
       (existing streams reset; the PC never sees "no internet")
```

The last rule matters most: the Windows adapter must **never** go down for a transient upstream change, because Windows aggressively marks networks unusable and applications give up.

### 8.2 State machine

```
IDLE → PAIRING → GROUP_UP → TUNNEL_CONNECTING → ACTIVE
                                                  ↕
                                    DEGRADED ↔ RECONNECTING
```

`DEGRADED` (heartbeat late, throughput collapsed) is surfaced in the UI *before* a full disconnect. Android P2P broadcasts drive transitions rather than socket timeouts — reacting to the P2P connection-changed broadcast is far faster than noticing a hung socket.

### 8.3 Failure-mode table

| Symptom | Likely cause | Design response |
|---|---|---|
| Sudden throughput drop | 2.4 GHz fallback / band switch | Show band + PHY rate live (§9) |
| Total freeze, reconnect fixes it | Tunnel head-of-line blocking | Per-stream credits (v1), QUIC (v2) |
| Some apps work, others hang | MTU / PMTU black hole | MTU probing (§7.3) |
| Works, then dies after minutes | Doze killing the service | Foreground service + wake lock |
| PC has no internet after a crash | Leftover default route | Route rollback journal (§7.2) |
| High latency under load | Unbounded buffering (bufferbloat) | Bounded buffers + backpressure |

---

## 9. Diagnostics — the three-tier measurement

The most valuable screen in the product, because it turns "it's slow" into a specific answer. Measure the three legs **independently**:

```
DIRECT LINK      Windows <-> Android over Wi-Fi Direct
PHONE INTERNET   Android -> Internet (phone alone)
THROUGH TUNNEL   Windows -> Internet (end to end)
```

Example output that immediately localises the fault:

```
Wi-Fi Direct raw:   380 Mbps
Phone internet:     210 Mbps
PC through tunnel:   63 Mbps      <-- the tunnel is the bottleneck
```

Leg 1 low → radio/band problem. Leg 2 low → carrier/signal. Legs 1 and 2 healthy but 3 poor → our code, which is the case worth engineering against.

### 9.1 Main UI

```
DIRECT LINK          INTERNET            LATENCY      PHONE
5 GHz                184 Mbps down       22 ms        5G
866 Mbps PHY          31 Mbps up                      -87 dBm
```

Compose screens reading a `StateFlow` off the tunnel service.

---

## 10. Prototype plan — go / no-go

Three milestones decide whether the project is worth continuing.

**M1 — Android creates a Wi-Fi Direct group, Windows joins.**
Success: the PC gets a `192.168.49.x` address and pings the phone's P2P address. Record band, PHY rate, time to form the group, and behaviour across 3 reconnect cycles.

**M2 — Windows reaches an Android socket over that link.**
Success: raw TCP throughput measured in both directions. **This number is the ceiling for everything else.** If it is not comfortably above the phone's cellular speed, stop and investigate the radio before writing a line of NAT code.

**M3 — Windows TUN → Android → Internet for one TCP connection.**
Success: Wintun adapter up, one HTTP download from a real server, routed end to end, at good throughput.

**Go/no-go:** if M3 sustains a large fraction of M2's throughput, continue to full v1. If M3 collapses relative to M2, the userspace forwarding path is wrong and must be fixed before any feature work.

### 10.1 Sequence after go

1. Full TCP NAT (many concurrent streams) + flow control
2. UDP + DNS
3. Reconnect ladder + state machine
4. Diagnostics screen
5. Pairing + remembered devices + encryption
6. MTU probing, IPv6
7. QUIC transport evaluation

---

## 11. Risk register

| Risk | Severity | Mitigation |
|---|---|---|
| Wi-Fi Direct throughput is itself the bottleneck | High | M2 measures this before any real work |
| Vendor firmware differences in P2P behaviour | High | Force GO role; test Samsung/Pixel/Xiaomi early |
| Android 16/17 local-network restrictions break the P2P path | High | Validate on previews from day one |
| Doze / OEM battery managers kill the service | Medium | Foreground service, wake lock, exemption prompt |
| Wintun driver signing / install friction | Medium | Ship the signed upstream driver; elevated helper service |
| Carrier meters the traffic anyway | Medium | Traffic genuinely is app traffic; no TTL/fingerprint tricks needed |
| Userspace NAT cannot keep up at high throughput | Medium | Non-blocking selector, minimal-copy buffers; measure at M3 |
| Managed C# packet loop too slow | Low-Med | Decide from M3 data; move the hot loop native if needed (§13) |

---

## 12. Deferred

- USB transport as a fallback path (higher throughput ceiling, useful when Wi-Fi is congested).
- IPv6 end to end.
- Multiple simultaneous PCs.
- iOS as a *tunnel client* via `NEPacketTunnelProvider` over infrastructure Wi-Fi (§2.3) — never as a host.
- Licensing/tiering — build it to solve the actual problem first.

---

## 13. Open decisions

1. **C# vs native for the Windows packet path.** C# with pinned buffers and `Span<T>` should handle a few hundred Mbps, but if M3 shows the managed path is the bottleneck, the hot loop moves to a native library. Decide with M3 data, not in advance.
2. **v1 encryption:** Noise_NK now, or accept WPA2-only for the prototype and add crypto before any release.
