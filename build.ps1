<#
.SYNOPSIS
    Builds Argus and optionally launches it.

.EXAMPLE
    ./build.ps1 -Run
    ./build.ps1 -Run -Clean
#>
[CmdletBinding()]
param(
    [switch]$Run,
    [switch]$Clean,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$proj = Join-Path $root 'src/Argus/Argus.csproj'
$dist = Join-Path $root 'dist'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet not found. Install it with: winget install --id Microsoft.DotNet.SDK.9'
}

$sdks = & dotnet --list-sdks
if (-not $sdks) {
    throw 'A .NET runtime is present but no SDK, so nothing here can build. Install: winget install --id Microsoft.DotNet.SDK.9'
}

# Argus writes to its own exe path via the Run key, so a running instance both
# locks the file and would be left pointing at a stale build.
$running = Get-Process Argus -ErrorAction SilentlyContinue
if ($running) {
    Write-Host 'Stopping the running Argus first...' -ForegroundColor DarkYellow
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 400
}

if ($Clean -and (Test-Path $dist)) {
    Remove-Item $dist -Recurse -Force
}

Write-Host "Building Argus ($Configuration)..." -ForegroundColor Cyan

# Framework-dependent on purpose: the WindowsDesktop runtime is already on this
# machine, and self-contained would add ~150MB to disk to save nothing at all.
& dotnet publish $proj `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained false `
    --output $dist `
    --nologo `
    -p:DebugType=none

if ($LASTEXITCODE -ne 0) { throw "build failed with exit code $LASTEXITCODE" }

$exe = Join-Path $dist 'Argus.exe'
if (-not (Test-Path $exe)) { throw "build reported success but $exe is missing" }

$size = [math]::Round((Get-Item $exe).Length / 1KB, 1)
Write-Host "Built: $exe  ($size KB)" -ForegroundColor Green

if ($Run) {
    Write-Host 'Launching...' -ForegroundColor Cyan
    Start-Process $exe
    Start-Sleep -Seconds 3

    $p = Get-Process Argus -ErrorAction SilentlyContinue
    if ($p) {
        $mb = [math]::Round($p.WorkingSet64 / 1MB, 1)
        Write-Host "Argus is up. pid $($p.Id), working set $mb MB" -ForegroundColor Green
    }
    else {
        Write-Warning "Argus exited immediately. Check $env:APPDATA\Argus\argus.log"
    }
}
