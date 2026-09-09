<#
.SYNOPSIS
    Builds every PocketModem artifact: Windows app, CLI, installer, Linux binaries
    and the Android APK.

.DESCRIPTION
    One script so a release is reproducible rather than a sequence of commands
    someone has to remember in the right order.

    The installer is unsigned. Windows SmartScreen will warn users until the
    binary earns reputation or a certificate is bought; -CertificatePath and
    -CertificatePassword are here so that signing becomes one step whenever
    that changes, rather than a rebuild of the packaging.

.PARAMETER SkipAndroid
    Skip the APK. Useful when only the desktop side changed, since the Gradle
    build is by far the slowest part.

.PARAMETER CertificatePath
    A .pfx to sign the binaries and installer with. Unsigned when omitted.

.EXAMPLE
    .\build-release.ps1
    .\build-release.ps1 -SkipAndroid
    .\build-release.ps1 -CertificatePath cert.pfx -CertificatePassword (Read-Host -AsSecureString)
#>

[CmdletBinding()]
param(
    [switch]$SkipAndroid,
    [string]$CertificatePath,
    [System.Security.SecureString]$CertificatePassword,
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root "dist"
$distLinux = Join-Path $root "dist-linux"

function Write-Step($text) {
    Write-Host ""
    Write-Host "==> $text" -ForegroundColor Cyan
}

function Assert-LastExitCode($what) {
    if ($LASTEXITCODE -ne 0) { throw "$what failed with exit code $LASTEXITCODE" }
}

Write-Host "PocketModem release build $Version" -ForegroundColor White

# The app cannot be replaced while it is running, and a half-written exe is a
# worse outcome than a clear failure here.
$running = Get-Process -Name "PocketModem-Desktop", "pocketmodem" -ErrorAction SilentlyContinue
if ($running) {
    throw "PocketModem is running (pid $($running.Id -join ', ')). Close it and run this again."
}

# ---------------------------------------------------------------- Windows --

Write-Step "Windows desktop app"
dotnet publish (Join-Path $root "windows\PocketModem.App") `
    -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true `
    -o $dist --nologo -v quiet
Assert-LastExitCode "Windows app publish"

Write-Step "Windows command line"
dotnet publish (Join-Path $root "windows\PocketModem.Cli") `
    -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true `
    -o $dist --nologo -v quiet
Assert-LastExitCode "CLI publish"

# Wintun is not redistributed in the repository, so a clean checkout will not
# have it. Fail loudly rather than shipping an installer that cannot work.
$wintun = Join-Path $dist "wintun.dll"
if (-not (Test-Path $wintun)) {
    throw "wintun.dll is missing from dist. Download the amd64 build from https://www.wintun.net"
}

# ------------------------------------------------------------------ Linux --

Write-Step "Linux desktop app"
dotnet publish (Join-Path $root "windows\PocketModem.App") `
    -c Release -r linux-x64 --self-contained false -p:PublishSingleFile=true `
    -o $distLinux --nologo -v quiet
Assert-LastExitCode "Linux app publish"

Write-Step "Linux command line"
dotnet publish (Join-Path $root "windows\PocketModem.Cli") `
    -c Release -r linux-x64 --self-contained false -p:PublishSingleFile=true `
    -o $distLinux --nologo -v quiet
Assert-LastExitCode "Linux CLI publish"

# ---------------------------------------------------------------- Android --

if (-not $SkipAndroid) {
    Write-Step "Android APK"
    Push-Location (Join-Path $root "android")
    try {
        & .\gradlew.bat assembleRelease --no-daemon -q
        if ($LASTEXITCODE -ne 0) {
            Write-Host "  release build failed; falling back to debug" -ForegroundColor Yellow
            & .\gradlew.bat assembleDebug --no-daemon -q
            Assert-LastExitCode "Android build"
        }
    } finally {
        Pop-Location
    }
}

# ---------------------------------------------------------------- Signing --

if ($CertificatePath) {
    Write-Step "Signing"
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match "x64" } | Select-Object -First 1

    if (-not $signtool) {
        throw "signtool.exe not found. Install the Windows SDK, or omit -CertificatePath."
    }

    $plain = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
        [Runtime.InteropServices.Marshal]::SecureStringToBSTR($CertificatePassword))

    foreach ($exe in @("PocketModem-Desktop.exe", "pocketmodem.exe")) {
        & $signtool.FullName sign /f $CertificatePath /p $plain `
            /tr http://timestamp.digicert.com /td sha256 /fd sha256 `
            (Join-Path $dist $exe)
        Assert-LastExitCode "signing $exe"
    }
} else {
    Write-Host ""
    Write-Host "  Not signed. Windows SmartScreen will warn users on first run." -ForegroundColor Yellow
    Write-Host "  Pass -CertificatePath to sign." -ForegroundColor Yellow
}

# -------------------------------------------------------------- Installer --

Write-Step "Windows installer"
$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    Write-Host "  Inno Setup 6 not found; skipping the installer." -ForegroundColor Yellow
    Write-Host "  Install it from https://jrsoftware.org/isdl.php to build one." -ForegroundColor Yellow
} else {
    & $iscc "/DAppVersion=$Version" (Join-Path $PSScriptRoot "pocketmodem.iss")
    Assert-LastExitCode "installer build"

    if ($CertificatePath) {
        $setup = Join-Path $PSScriptRoot "output\PocketModem-Setup-$Version.exe"
        $plain = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
            [Runtime.InteropServices.Marshal]::SecureStringToBSTR($CertificatePassword))
        & $signtool.FullName sign /f $CertificatePath /p $plain `
            /tr http://timestamp.digicert.com /td sha256 /fd sha256 $setup
        Assert-LastExitCode "signing the installer"
    }
}

# ------------------------------------------------------------- Linux tar --

Write-Step "Linux archive"
$tarball = Join-Path $PSScriptRoot "output\pocketmodem-$Version-linux-x64.tar.gz"
New-Item -ItemType Directory -Force -Path (Split-Path $tarball) | Out-Null
tar -czf $tarball -C $distLinux .
Assert-LastExitCode "tar"

# ------------------------------------------------------------------ Done --

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Get-ChildItem (Join-Path $PSScriptRoot "output") -ErrorAction SilentlyContinue |
    Select-Object Name, @{n = "Size"; e = { "{0:N1} MB" -f ($_.Length / 1MB) } } |
    Format-Table -AutoSize
