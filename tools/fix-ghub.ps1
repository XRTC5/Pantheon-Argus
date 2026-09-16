<#
.SYNOPSIS
    Un-wedges Logitech G HUB when it hangs on the loading screen.

.DESCRIPTION
    G HUB's UI talks to lghub_agent.exe over a WebSocket on 127.0.0.1:9010. When
    the agent cannot open that socket the UI sits on the loading screen forever,
    and reinstalling does not help because nothing is wrong with G HUB.

    The cause on this machine (diagnosed 2026-09-07, recurred 2026-09-15) is
    stale api-ms-win-*.dll stubs shipped inside a JDK on PATH. Those stubs are
    v10.0.10240.16384 - Windows 10 RTM, 2015. The names do NOT exist as real
    files in System32 on modern Windows; the loader resolves them as virtual API
    sets. So a PATH-based file search finds nothing in System32, falls through to
    PATH, and loads the 2015 stub. lghub_agent ends up with a decade-old
    *synchronization* forwarder, every thread parks in Wait, and it never binds
    9010.

    This retires any such stub found in a non-Windows directory on PATH - the
    general shape of the trap, not just the JDK - then restarts G HUB and
    verifies the agent is listening.

    Idempotent. With no stubs to retire it just restarts G HUB, which is a
    reasonable repair in its own right.

    Needs Administrator to rename files under Program Files.

.NOTES
    Every rename is logged and a rollback script is written beside the log.
#>

[CmdletBinding()]
param(
    # Report what would change without touching anything.
    [switch]$WhatIfOnly
)

$ErrorActionPreference = 'Stop'

$logDir  = Join-Path $env:LOCALAPPDATA 'Argus'
$logFile = Join-Path $logDir 'fix-ghub.log'
$stamp   = Get-Date -Format 'yyyyMMdd-HHmmss'
$rollbackFile = Join-Path $logDir "fix-ghub-rollback-$stamp.ps1"

New-Item -ItemType Directory -Force -Path $logDir | Out-Null

function Say($msg) {
    $line = "{0}  {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $msg
    Write-Host $line
    Add-Content -LiteralPath $logFile -Value $line -Encoding utf8
}

Say "=== fix-ghub starting ==="

$isAdmin = (New-Object Security.Principal.WindowsPrincipal(
    [Security.Principal.WindowsIdentity]::GetCurrent())
).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin -and -not $WhatIfOnly) {
    Say "ERROR: needs Administrator to rename files under Program Files."
    exit 1
}

# ---------------------------------------------------------------- find stubs
#
# Only directories that are on PATH and are NOT under C:\Windows. A stub inside
# the Windows directory is the real thing; one on PATH in an application folder
# is the trap.

$windir = [Environment]::GetFolderPath('Windows')

$pathDirs = ($env:PATH -split ';') |
    Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Container) } |
    Where-Object { -not $_.TrimEnd('\').StartsWith($windir, [StringComparison]::OrdinalIgnoreCase) } |
    Select-Object -Unique

$stubs = @()
foreach ($dir in $pathDirs) {
    try {
        $stubs += Get-ChildItem -LiteralPath $dir -Filter 'api-ms-win-*.dll' -File -ErrorAction SilentlyContinue
    } catch { }
}

Say "PATH directories outside Windows: $($pathDirs.Count)"
Say "stale api-ms-win stubs found: $($stubs.Count)"

if ($WhatIfOnly) {
    foreach ($s in $stubs) { Say "  would retire: $($s.FullName)" }
    Say "=== dry run, nothing changed ==="
    exit 0
}

# -------------------------------------------------------------- retire stubs

$renamed = 0
if ($stubs.Count -gt 0) {
    "# Rollback for api-ms-win stubs retired $stamp" | Set-Content -LiteralPath $rollbackFile -Encoding utf8

    foreach ($s in $stubs) {
        $new = "$($s.Name).disabled-$stamp"
        try {
            Rename-Item -LiteralPath $s.FullName -NewName $new -ErrorAction Stop
            "Rename-Item -LiteralPath `"$(Join-Path $s.DirectoryName $new)`" -NewName `"$($s.Name)`"" |
                Add-Content -LiteralPath $rollbackFile -Encoding utf8
            $renamed++
        } catch {
            Say "  FAILED $($s.FullName): $($_.Exception.Message)"
        }
    }
    Say "retired $renamed stubs; rollback: $rollbackFile"
} else {
    Say "no stubs to retire - restarting G HUB anyway"
}

# ------------------------------------------------------------ restart G HUB

$tray = 'C:\Program Files\LGHUB\system_tray\lghub_system_tray.exe'

Say "restarting G HUB"
Stop-Process -Name lghub, lghub_agent, lghub_system_tray, lghub_gl, lghub_sso_handler `
             -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 4

if (Test-Path -LiteralPath $tray) {
    Start-Process -FilePath $tray -ArgumentList '--minimized'
} else {
    Say "WARNING: tray not found at $tray - G HUB may not be installed"
}

# -------------------------------------------------------------------- verify
#
# The agent binding 9010 is the whole point. Confirm it rather than assume it:
# reporting success while the socket is still dead would make the panel lie.

$listening = $false
for ($i = 0; $i -lt 12; $i++) {
    Start-Sleep -Seconds 5
    $conn = Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
            Where-Object { $_.LocalPort -eq 9010 }
    if ($conn) { $listening = $true; break }
}

$agent = Get-Process lghub_agent -ErrorAction SilentlyContinue | Select-Object -First 1

if ($listening) {
    Say "SUCCESS: agent listening on 127.0.0.1:9010 (threads $($agent.Threads.Count))"
    Say "=== fix-ghub done ==="
    exit 0
}

Say "FAILED: nothing listening on 9010. agent threads $($agent.Threads.Count)"
Say "=== fix-ghub done ==="
exit 2
