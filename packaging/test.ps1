<#
.SYNOPSIS
    Run every test suite: C# and Kotlin.

.DESCRIPTION
    The protocol and the crypto are implemented twice, once per language, and
    can only be kept in agreement by pinning the same bytes on both sides. That
    makes running one suite alone misleading - a change that breaks the pairing
    passes on the side it was made and fails on the other.

.PARAMETER SkipAndroid
    C# only. Much faster; use it while iterating on the desktop side.
#>

[CmdletBinding()]
param([switch]$SkipAndroid)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$failed = @()

Write-Host ""
Write-Host "PocketModem tests" -ForegroundColor White

# ------------------------------------------------------------------- C# --

Write-Host ""
Write-Host "==> Desktop (C#)" -ForegroundColor Cyan
dotnet test (Join-Path $root "windows\PocketModem.Tests") --nologo -v quiet
if ($LASTEXITCODE -ne 0) { $failed += "C#" }

# --------------------------------------------------------------- Kotlin --

if (-not $SkipAndroid) {
    Write-Host ""
    Write-Host "==> Phone (Kotlin)" -ForegroundColor Cyan
    Push-Location (Join-Path $root "android")
    try {
        & .\gradlew.bat testDebugUnitTest --no-daemon -q
        if ($LASTEXITCODE -ne 0) {
            $failed += "Kotlin"
            Write-Host "  report: android\app\build\reports\tests\testDebugUnitTest\index.html" -ForegroundColor Yellow
        } else {
            Write-Host "  passed" -ForegroundColor Green
        }
    } finally {
        Pop-Location
    }
}

# ----------------------------------------------------------------- done --

Write-Host ""
if ($failed.Count -gt 0) {
    Write-Host "FAILED: $($failed -join ', ')" -ForegroundColor Red
    exit 1
}

Write-Host "All suites passed." -ForegroundColor Green
