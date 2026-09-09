<#
.SYNOPSIS
    Install a build on the phone over the tunnel, with no cable and no ADB.

.DESCRIPTION
    The app carries its own update endpoint (UpdateServer, port 47813) for
    exactly this case: wireless debugging needs a pairing step that only the
    phone's screen can complete, and Samsung drops ADB-over-TCP on reboot, so
    the cable kept coming back. This does not depend on either - as long as the
    app is running and any link to the phone is up, a new build can be pushed.

    The phone still shows Android's own install confirmation, so a push is
    never silent. That is deliberate: this endpoint installs software, and a
    prompt is the last thing standing between a pairing token and arbitrary
    code.

.PARAMETER Apk
    Path to the APK. Defaults to the debug build.

.PARAMETER Phone
    Address of the phone. Defaults to trying the Wi-Fi Direct owner, then the
    tunnel gateway, then a hotspot gateway - whichever answers.

.PARAMETER Token
    Pairing code. Defaults to the one this PC already has saved.

.EXAMPLE
    .\push-apk.ps1
    .\push-apk.ps1 -Apk ..\android\app\build\outputs\apk\release\app-release.apk
#>

[CmdletBinding()]
param(
    [string]$Apk,
    [string]$Phone,
    [string]$Token
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

if (-not $Apk) {
    $Apk = Join-Path $root "android\app\build\outputs\apk\debug\app-debug.apk"
}
if (-not (Test-Path $Apk)) { throw "No APK at $Apk" }

# ---------------------------------------------------------------- the token --
#
# Read from the desktop client's own settings rather than asked for: this PC is
# already paired, or it would not have a tunnel to push over.

if (-not $Token) {
    $settingsPath = Join-Path $env:LOCALAPPDATA "PocketModem\settings.json"
    if (Test-Path $settingsPath) {
        $Token = (Get-Content $settingsPath -Raw | ConvertFrom-Json).Token
    }
}
if (-not $Token) { throw "No pairing code. Pass -Token, or connect once so it is saved." }

# --------------------------------------------------------------- the phone --
#
# Tried in order of directness. The Wi-Fi Direct owner address is the phone
# itself; the tunnel gateway reaches it through the link that is already up;
# a hotspot gateway covers the case where the tunnel is not running at all.

function Test-Port($address, $port) {
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $result = $client.BeginConnect($address, $port, $null, $null)
        if ($result.AsyncWaitHandle.WaitOne(1500) -and $client.Connected) { return $true }
        return $false
    } catch { return $false } finally { $client.Close() }
}

if (-not $Phone) {
    $candidates = @("192.168.49.1", "10.87.0.1")

    # Whatever is currently the default gateway, in case the phone is sharing
    # over a hotspot rather than Wi-Fi Direct.
    $gateway = (Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue |
                Sort-Object RouteMetric | Select-Object -First 1).NextHop
    if ($gateway) { $candidates += $gateway }

    foreach ($candidate in $candidates) {
        Write-Host "  trying $candidate ... " -NoNewline
        if (Test-Port $candidate 47813) { Write-Host "yes"; $Phone = $candidate; break }
        Write-Host "no"
    }
}
if (-not $Phone) { throw "Could not find the phone. Is PocketModem running on it?" }

# ----------------------------------------------------------------- the push --

$bytes = [System.IO.File]::ReadAllBytes($Apk)
Write-Host ""
Write-Host "Pushing $([math]::Round($bytes.Length / 1MB, 1)) MB to $Phone" -ForegroundColor Cyan

$client = New-Object System.Net.Sockets.TcpClient
$client.Connect($Phone, 47813)

# The socket has to outlast the transfer plus the phone staging it to disk.
$client.SendTimeout = 180000
$client.ReceiveTimeout = 180000

$stream = $client.GetStream()

try {
    $tokenBytes = [System.Text.Encoding]::ASCII.GetBytes($Token)

    # WDUP | token length (2, big-endian) | token | apk length (4, big-endian) | apk
    $stream.Write([System.Text.Encoding]::ASCII.GetBytes("WDUP"), 0, 4)

    $lengthPrefix = [BitConverter]::GetBytes([UInt16]$tokenBytes.Length)
    if ([BitConverter]::IsLittleEndian) { [Array]::Reverse($lengthPrefix) }
    $stream.Write($lengthPrefix, 0, 2)
    $stream.Write($tokenBytes, 0, $tokenBytes.Length)

    $apkPrefix = [BitConverter]::GetBytes([Int32]$bytes.Length)
    if ([BitConverter]::IsLittleEndian) { [Array]::Reverse($apkPrefix) }
    $stream.Write($apkPrefix, 0, 4)

    # Chunked with progress: a 24 MB push over Wi-Fi Direct takes long enough
    # that a silent wait looks like a hang.
    $chunk = 64 * 1024
    $sent = 0
    while ($sent -lt $bytes.Length) {
        $n = [Math]::Min($chunk, $bytes.Length - $sent)
        $stream.Write($bytes, $sent, $n)
        $sent += $n

        $percent = [math]::Round($sent * 100.0 / $bytes.Length)
        Write-Host "`r  $percent%  ($([math]::Round($sent/1MB,1)) MB)   " -NoNewline
    }
    $stream.Flush()
    Write-Host ""

    $reply = New-Object byte[] 64
    $read = $stream.Read($reply, 0, $reply.Length)
    $text = [System.Text.Encoding]::ASCII.GetString($reply, 0, [Math]::Max($read, 0))

    if ($text.StartsWith("OK")) {
        Write-Host ""
        Write-Host "  Sent. Confirm the install on the phone." -ForegroundColor Green
        exit 0
    }

    Write-Host ""
    Write-Host "  Refused: $text" -ForegroundColor Red
    exit 1
}
finally {
    $stream.Dispose()
    $client.Close()
}
