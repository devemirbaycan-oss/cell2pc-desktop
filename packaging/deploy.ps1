<#
.SYNOPSIS
    Build and deploy PocketModem without losing internet access.

.DESCRIPTION
    Updating used to mean closing the app, publishing, and reconnecting by
    hand. This does the whole cycle in one command.

    A word on what it cannot do. A Wintun adapter belongs to the process that
    created it: when that process exits, the driver removes the adapter and
    Windows drops every route through it. That was measured rather than
    assumed, and it means a true zero-downtime handover is not possible - the
    incoming instance has nothing to adopt.

    What it does deliver is a gap of a second or two instead of a manual
    sequence: the outgoing instance skips its explicit teardown, and the PC
    falls back to the phone's own Wi-Fi in the interval rather than losing
    connectivity outright.

    The phone is updated over Wi-Fi Direct, so no cable is needed. That does
    briefly interrupt the tunnel while Android restarts the app, which is why
    the phone goes first: the desktop reconnects to it afterwards.

.PARAMETER SkipPhone
    Desktop only. Much faster, and correct when nothing in the Android app
    changed.

.PARAMETER SkipBuild
    Deploy what is already built.

.EXAMPLE
    .\deploy.ps1
    .\deploy.ps1 -SkipPhone
#>

[CmdletBinding()]
param(
    [switch]$SkipPhone,
    [switch]$SkipBuild,
    [string]$PhoneAddress = "192.168.49.1"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root "dist"

function Write-Step($text) {
    Write-Host ""
    Write-Host "==> $text" -ForegroundColor Cyan
}

function Test-TunnelUp {
    # A default route through our own adapter is the signature of a live
    # tunnel, and distinguishes "running" from "the app happens to be open".
    $adapter = Get-NetAdapter -Name "*PocketModem*" -ErrorAction SilentlyContinue |
        Where-Object Status -eq "Up"
    if (-not $adapter) { return $false }
    $null -ne (Get-NetRoute -DestinationPrefix "0.0.0.0/1" -ErrorAction SilentlyContinue)
}

$wasConnected = Test-TunnelUp
Write-Host "PocketModem deploy" -ForegroundColor White
Write-Host ("  tunnel is currently " + $(if ($wasConnected) { "UP - expect a brief gap" } else { "down" })) -ForegroundColor Gray

# ------------------------------------------------------------------ build --

if (-not $SkipBuild) {
    Write-Step "Building the desktop app"
    # Publish beside the live files, not over them: the running instance holds
    # its exe open, and a half-written binary is worse than a clear failure.
    $staging = Join-Path $env:TEMP "pocketmodem-staging"
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue

    dotnet publish (Join-Path $root "windows\PocketModem.App") `
        -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true `
        -o $staging --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "desktop build failed" }

    dotnet publish (Join-Path $root "windows\PocketModem.Cli") `
        -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true `
        -o $staging --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "CLI build failed" }

    if (-not $SkipPhone) {
        Write-Step "Building the phone app"
        Push-Location (Join-Path $root "android")
        try {
            & .\gradlew.bat assembleDebug --no-daemon -q
            if ($LASTEXITCODE -ne 0) { throw "Android build failed" }
        } finally {
            Pop-Location
        }
    }
}

# ------------------------------------------------------------------ phone --

if (-not $SkipPhone) {
    Write-Step "Updating the phone"

    $apk = Join-Path $root "android\app\build\outputs\apk\debug\app-debug.apk"
    if (-not (Test-Path $apk)) { throw "APK not found at $apk" }

    # Try every way of reaching the phone rather than assuming one.
    #
    # Order matters. Wi-Fi Direct is where the phone lives during normal use,
    # but that link is down exactly when a deploy is most likely - the tunnel
    # having just been stopped. The hotspot survives that, and USB survives
    # everything, so each is a fallback for the one before it.
    $targets = @()
    $targets += "${PhoneAddress}:5555"

    # The default gateway on any interface is a candidate: on a phone hotspot
    # that gateway is the phone.
    Get-NetIPConfiguration | ForEach-Object {
        $gw = $_.IPv4DefaultGateway.NextHop
        if ($gw -and $gw -ne $PhoneAddress) { $targets += "${gw}:5555" }
    }

    $connected = $null
    foreach ($target in ($targets | Select-Object -Unique)) {
        & adb connect $target 2>&1 | Out-Null
        Start-Sleep -Milliseconds 700
        if (& adb devices 2>&1 | Select-String ([regex]::Escape($target) + "\s+device")) {
            $connected = $target
            Write-Host "  reached the phone at $target" -ForegroundColor Gray
            break
        }
    }

    # A cable, if one happens to be attached.
    if (-not $connected) {
        $usb = & adb devices 2>&1 | Select-String "^\w+\s+device" |
            Where-Object { $_ -notmatch ":\d+" } | Select-Object -First 1
        if ($usb) {
            $connected = ($usb -split "\s+")[0]
            Write-Host "  reached the phone over USB" -ForegroundColor Gray
        }
    }

    if (-not $connected) {
        Write-Host "  could not reach the phone" -ForegroundColor Yellow
        Write-Host "  join its Wi-Fi Direct group or hotspot, plug in a cable," -ForegroundColor Yellow
        Write-Host "  or pass -SkipPhone" -ForegroundColor Yellow
    } else {
        & adb -s $connected install -r $apk 2>&1 | Select-Object -Last 1
        Write-Host "  phone updated" -ForegroundColor Green
    }
}

# ---------------------------------------------------------------- desktop --

Write-Step "Swapping the desktop binaries"

if (-not $SkipBuild) {
    $staging = Join-Path $env:TEMP "pocketmodem-staging"

    # Kill rather than close: a killed process skips its teardown, so the
    # incoming instance rebuilds in one step instead of waiting for the old one
    # to undo everything first. Windows removes the adapter and its routes
    # regardless, since Wintun ties them to the owning process.
    $running = Get-Process -Name "PocketModem-Desktop", "pocketmodem" -ErrorAction SilentlyContinue
    if ($running) {
        Write-Host "  stopping $($running.Count) instance(s)"
        $running | Stop-Process -Force
        Start-Sleep -Seconds 2
    }

    foreach ($file in Get-ChildItem $staging -File) {
        Copy-Item $file.FullName (Join-Path $dist $file.Name) -Force
    }
    Write-Host "  binaries replaced" -ForegroundColor Green
}

# ----------------------------------------------------------------- resume --

if ($wasConnected) {
    Write-Step "Reconnecting"
    Write-Host "  the adapter went with the old process, as Wintun requires" -ForegroundColor Gray
    Write-Host "  your PC is on the phone's Wi-Fi in the meantime, so it is not offline" -ForegroundColor Gray
    Write-Host ""
    Write-Host "  Run:  .\dist\PocketModem-Desktop.exe" -ForegroundColor White
    Write-Host "  Your pairing code is remembered, so it is one click." -ForegroundColor Gray
} else {
    Write-Step "Done"
    Write-Host "  Run:  .\dist\PocketModem-Desktop.exe" -ForegroundColor White
}

Write-Host ""
