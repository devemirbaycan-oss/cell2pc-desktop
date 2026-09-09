# M1 / M2 — Wi-Fi Direct test procedure

You run this one; it needs the radio and physical proximity. The APK on the
phone is already current.

**What we're measuring:** the same two numbers USB produced (284 Mbps down /
245 Mbps up / 1.84 ms), but over Wi-Fi Direct. That comparison is the point —
USB gives us a known-good reference, so anything Wi-Fi Direct falls short by is
attributable to the radio rather than to the code.

---

## Before you start

**The one real risk:** Wi-Fi Direct needs the Wi-Fi radio on, and if the phone
auto-joins a nearby access point it may prefer that AP for internet — which
would change what you're measuring, and could interrupt the PC's connection if
the PC is online through this phone.

To avoid it, **turn off auto-reconnect for nearby networks** (or move out of
range) before enabling Wi-Fi. The phone should have Wi-Fi *on* but be joined to
*no network*. Creating a P2P group does not join a network.

If the PC's only internet is this phone, expect the PC to drop offline during
the test. That is fine — the probe runs entirely over the local link.

---

## Steps

**1. Phone — enable the radio**

Turn Wi-Fi on. Do not join a network. If Android auto-joins one, forget it.

**2. Phone — start the group**

Open PocketModem → tap **Wi-Fi Direct**.

The DIRECT LINK card should fill in:

```
SSID         DIRECT-WD-PocketModem
Passphrase   <12 characters>
Band         5 GHz              <-- the number that matters
Frequency    5180 MHz
Owner IP     192.168.49.1
PC joined    not yet
```

> **If Band says 2.4 GHz**, note it and continue. That alone would explain a
> weak result, and it is exactly what DESIGN §4.2 predicted we would need to
> watch for.

> **If it fails with "P2P framework busy"**, toggle Wi-Fi off and on and retry.
> A stale group is the usual cause.

**3. PC — join the group**

Windows Wi-Fi settings → connect to `DIRECT-WD-PocketModem` using the
passphrase shown on the phone.

The phone's **PC joined** field should flip to `yes (1)` within a couple of
seconds. It polls, so no need to tap anything.

**4. PC — run the probe**

```sh
cd windows/PocketModem.Probe
dotnet run
```

It auto-discovers the `192.168.49.x` interface. If discovery fails:

```sh
dotnet run -- 192.168.49.1
```

---

## What to send back

Paste the probe output. It looks like:

```
[M1] ICMP ping 192.168.49.1 ... ok (3 ms)
[M1] TCP connect 192.168.49.1:47811 ... ok
[M2] Latency (32 round trips) ... min ? ms   median ? ms   max ? ms
[M2] Download (phone -> PC, 10s) ...  ? MiB in 10.0s = ? Mbps
[M2] Upload   (PC -> phone, 10s) ...  ? MiB in 10.0s = ? Mbps
```

Plus the **Band** and **Frequency** from the phone screen — without those the
throughput number can't be interpreted.

---

## How to read the result

| Result | Meaning | Next step |
|---|---|---|
| **>150 Mbps, 5 GHz** | Healthy. Comfortably above LTE. | Proceed to M3 |
| **50–150 Mbps, 5 GHz** | Usable — still well above LTE, so it does not block the product | Proceed to M3, note the ceiling |
| **<50 Mbps on 2.4 GHz** | Band fallback, not a code problem | Investigate why 5 GHz was refused |
| **<50 Mbps on 5 GHz** | Genuine radio/interference problem | Retest elsewhere before concluding |
| **Group won't form** | Samsung P2P quirk — the vendor risk in §11 | Send the logcat below |

Remember the bar: this only has to beat **LTE**, not USB. A result far below
284 Mbps can still be a complete success for the product.

If something fails, this captures the relevant logs:

```sh
adb logcat -d -s P2pTransport:V TunnelService:V LinkProbe:V > p2p.log
```

---

## Why it might legitimately be slower than USB

Wi-Fi Direct is half-duplex over a shared radio channel, so a lower number than
the cable is expected and not a defect. What matters is whether it clears LTE
with margin. It also shares the chip with any Wi-Fi scanning the phone does,
which is a further reason to have the phone joined to no network.
