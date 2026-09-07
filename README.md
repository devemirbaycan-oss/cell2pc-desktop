# Cell2Pc Desktop

The desktop client for [Cell2Pc](https://github.com/devemirbaycan-oss/cell2pc) — it shares an Android phone's mobile data with a Windows or Linux PC over Wi-Fi Direct.

This repository is the client half, published under MIT. It creates a virtual network adapter and changes your machine's routing, which is a lot of trust to ask for on faith — so the code that does it is here to read.

The Android app is closed source.

---

## What this half does

```
your applications
      |
virtual network adapter        <- Wintun (Windows) or /dev/net/tun (Linux)
      |
tunnel client                  <- classifies packets, multiplexes streams
      |
  Wi-Fi Direct
      |
  Android app                  <- opens its own cellular sockets
```

The PC's packets stop at the phone. The phone opens ordinary sockets of its own on the cellular interface and copies bytes between the two, so the traffic leaving it is that app's own traffic. That is the whole design, and it is why no root and no tethering stack are involved.

## Layout

```
Cell2Pc.Client/           the tunnel; platform pieces behind interfaces
  Tunnel/                 wire protocol, AES-GCM framing, reconnect ladder
  Net/                    packet parsing, routing, Wi-Fi joining
  Wintun/                 P/Invoke over wintun.dll
  Platform/               the interfaces, and the Linux implementations
Cell2Pc.App/              Avalonia desktop app (Windows and Linux)
Cell2Pc.Cli/              command line
Cell2Pc.Tests/            47 tests
packaging/                build, test and release scripts
web/                      the project website, one self-contained file
```

## Building

Requires the .NET 9 SDK.

```sh
dotnet build

# Windows
dotnet publish Cell2Pc.App -c Release -r win-x64 -o dist
# Wintun (amd64) from https://www.wintun.net must sit beside the executable.

# Linux
dotnet publish Cell2Pc.App -c Release -r linux-x64 -o dist-linux
```

Running the tunnel needs Administrator on Windows, or root/CAP_NET_ADMIN on Linux — creating a network interface and editing the route table are privileged.

## Tests

```sh
dotnet test Cell2Pc.Tests
```

They target the failures that actually happened rather than coverage. The pattern in every bug this project has hit is the same: nothing throws, the tunnel connects, the links report healthy, and no traffic moves. Those are cheap for a test to catch and expensive to find in a session.

Note that the protocol and crypto are implemented once per language — the Kotlin half lives in the private repository — so these tests pin the same bytes the phone pins independently. A change that breaks the pairing fails here.

## The protocol

Published as a consequence of open-sourcing this half, and that is fine: the pairing token is the secret, not the format.

```
+--------+--------+----------+--------+---------+
| type   | flags  | streamId | length | payload |
| 1 byte | 1 byte | 4 bytes  | 2 bytes|   ...   |
+--------+--------+----------+--------+---------+
```

Big-endian. Four parallel connections carry it, with each stream pinned to one by id — a single connection means one lost Wi-Fi frame stalls every stream behind it, which is felt as an occasional total freeze rather than steady slowness.

Every frame after HELLO is sealed with AES-GCM, keyed `SHA-256("cell2pc-v1" || pairing token)`. HELLO itself is in the clear, because it carries the token the key comes from.

## Licence

MIT. See [LICENSE](LICENSE).

The Android app is separate and proprietary.
