# PocketModem

Your PC on the phone's mobile data, over Wi-Fi Direct — no root, no system hotspot.

All PC traffic leaves the phone as the app's own ordinary socket traffic, because the app really does open every connection itself. There is no forwarded packet and no tethering stack involved, so the carrier sees one app on one phone. See [docs/DESIGN.md](docs/DESIGN.md) for the architecture and [docs/RESULTS.md](docs/RESULTS.md) for measurements.

---

## What it does

```
Windows / Linux                          Android
  applications                             PocketModem app
      |                                        |
  virtual network adapter                 opens its own sockets
      |                                        |
      +------ Wi-Fi Direct or USB -------------+
                                               |
                                          cellular data
                                               |
                                           Internet
```

Every application works with no configuration — browsers, Steam, Windows Update, games. There is no proxy to set and nothing per-app.

## Status

| Component | State |
|---|---|
| Wi-Fi Direct transport (forced group owner, 5 GHz preferred) | Working |
| USB transport | Working |
| Userspace TCP/UDP NAT bound to cellular | Working |
| DNS interception and cache | Working |
| Virtual adapter + routing (Wintun / tun) | Working |
| Four parallel links (head-of-line blocking) | Working |
| Automatic reconnect | Written, lightly tested |
| Pairing code + AES-GCM frame encryption | Working |
| Desktop app (Windows, Linux) | Working |
| Command line | Working |
| IPv6 end to end | Working |

Measured on a Galaxy A53 over LTE: 156 Mbps down / 101 Mbps up on the link at 1.7 ms, comfortably above the cellular connection it carries.

## Installing

**Windows** — run `PocketModem-Setup-1.0.0.exe`.

The installer is not code-signed, so Windows SmartScreen will say it does not
recognise it. That is a statement about the certificate, not the software:
choose **More info** then **Run anyway**. Everything needed is bundled,
including the Wintun driver.

**Linux** — extract `pocketmodem-1.0.0-linux-x64.tar.gz` and run `./PocketModem-Desktop`
or `sudo ./pocketmodem`. Requires root or CAP_NET_ADMIN to create the tun
interface, and NetworkManager to join the phone's Wi-Fi automatically.

**Android** — install the APK from `android/app/build/outputs/apk/`.

## Using it

**On the phone**

1. Open PocketModem and tap **Wi-Fi Direct**
2. Note the **pairing code** and the **Wi-Fi passphrase**

**On the PC**

Run `PocketModem.exe` (Windows) or `./PocketModem` (Linux), enter the pairing code, and press Connect. It joins the phone's network itself and asks for elevation only when connecting — creating a network interface is privileged.

Or from a terminal:

```sh
pocketmodem                       # prompts for the pairing code
pocketmodem -t k7m2xq4p           # with a known code
pocketmodem connect --for 2h      # disconnect automatically after two hours
pocketmodem status                # is a tunnel running?
pocketmodem doctor                # work out why it will not connect
pocketmodem recover               # restore routing after a crash
```

`pocketmodem --help` lists everything; `--json` makes any command scriptable.

### If the PC loses internet

Routing changes are journalled to disk before they are applied and replayed on the next start, so a crash should recover on its own. If it does not:

```sh
pocketmodem recover               # or: FIX-MY-INTERNET.bat / ./fix-my-internet.sh
```

Disabling and re-enabling the Wi-Fi adapter also clears everything.

## Security

The Wi-Fi group is WPA2, but a group passphrase is shared with every device that ever paired, so it is not a session secret. Two further layers:

- **Pairing code** — the phone rejects any client that does not present it, so joining the Wi-Fi does not by itself grant use of the connection.
- **AES-GCM** — every tunnel frame is sealed with a key derived from that code, so traffic is unreadable to anything else on the link.

## Testing

```powershell
packaging	est.ps1                # both suites
packaging	est.ps1 -SkipAndroid   # C# only, much faster
```

The protocol and the crypto are implemented once per language, so they can only
be kept in agreement by pinning the same bytes on both sides. Running one suite
alone is misleading: a change that breaks the pairing passes on the side it was
made and fails on the other.

## Releasing

```powershell
packaging\build-release.ps1                 # everything
packaging\build-release.ps1 -SkipAndroid    # desktop only, much faster
```

Produces the Windows installer and the Linux archive in `packaging/output/`.
Signing is a parameter away when a certificate exists:

```powershell
packaging\build-release.ps1 -CertificatePath cert.pfx -CertificatePassword (Read-Host -AsSecureString)
```

Building the installer needs [Inno Setup 6](https://jrsoftware.org/isdl.php);
without it the script builds everything else and says so.

## Building

Requires JDK 17, the Android SDK (platform 36), and the .NET 9 SDK.

```sh
# Phone
cd android
./gradlew assembleDebug
adb install -r app/build/outputs/apk/debug/app-debug.apk

# Windows
cd windows/PocketModem.App && dotnet publish -c Release -r win-x64 -o ../../dist
# Wintun (amd64) from https://www.wintun.net must sit next to the exe.

# Linux
cd windows/PocketModem.App && dotnet publish -c Release -r linux-x64 -o ../../dist-linux
```

`android/local.properties` is machine-specific and not committed:

```
sdk.dir=C\:\\Users\\<you>\\AppData\\Local\\Android\\Sdk
```

## Layout

```
docs/
  DESIGN.md                     architecture and the decisions behind it
  RESULTS.md                    measurements, and what was actually slow
android/                        Kotlin + Compose, the phone side
  app/src/main/java/dev/pocketmodem/
    tunnel/P2pTransport.kt      Wi-Fi Direct group, forced group owner
    tunnel/TunnelServer.kt      framing, parallel links, stream demux
    tunnel/StreamRelay.kt       one real cellular socket per stream
    tunnel/UdpNat.kt            UDP flows, so DNS/QUIC/games work
    tunnel/DnsCache.kt          DNS interception and caching
    tunnel/UpstreamBinder.kt    pins every socket to cellular
    tunnel/Crypto.kt            AES-GCM frame encryption
    service/                    foreground service, wake lock, tile
windows/                        C#, the desktop side (Windows and Linux)
  PocketModem.Client/               the tunnel; platform pieces behind interfaces
  PocketModem.App/                  Avalonia desktop app
  PocketModem.Cli/                  command line
dist/ dist-linux/               published binaries and launchers
```
