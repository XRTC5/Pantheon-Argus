<#
.SYNOPSIS
    Stops scheduled tasks from flashing an empty console window on screen.

.DESCRIPTION
    A scheduled task set to "Run only when the user is logged on" starts its
    program inside your interactive session. For a console program - powershell,
    python, cmd - Windows creates the console host window BEFORE the program
    gets to run. So `-WindowStyle Hidden` is applied too late: the window is
    already on screen, and you see an empty console appear and vanish.

    Nothing is written in it because the program has not started yet. That is
    why it looks like a ghost with no output.

    Switching the task to S4U ("Run whether the user is logged on or not",
    without storing a password) runs it without an interactive session. No
    session means no console host, which means no window. The task still runs
    as you, still reaches the network, still writes to your files.

    Run with no arguments to see which tasks on this machine can do this.
    Pass -TaskName to fix specific ones. Every task definition is exported to
    ..\backup\ before it is touched, and -Revert puts them back.

    Requires elevation; it will ask.

.PARAMETER TaskName
    One or more task names to fix. Omit to list candidates without changing
    anything.

.PARAMETER All
    Fix every candidate found, without naming them individually.

.PARAMETER Revert
    Put the named tasks back to an Interactive logon type.

.EXAMPLE
    ./fix-console-flash.ps1
    ./fix-console-flash.ps1 -TaskName 'My nightly job'
    ./fix-console-flash.ps1 -All
    ./fix-console-flash.ps1 -TaskName 'My nightly job' -Revert
#>
[CmdletBinding()]
param(
    [string[]]$TaskName,
    [switch]$All,
    [switch]$Revert
)

$ErrorActionPreference = 'Stop'
$backupDir = Join-Path (Split-Path $PSScriptRoot -Parent) 'backup'

# Console hosts. These are the programs whose windows actually appear.
$consolePrograms = @('powershell.exe', 'pwsh.exe', 'cmd.exe', 'python.exe',
                     'wscript.exe', 'cscript.exe', 'node.exe')

function Get-ExeName {
    param([string]$Path)
    # Deliberately not Path.GetFileName: task actions legitimately contain
    # things it rejects as illegal - unexpanded %windir% variables, embedded
    # quotes, wildcards - and it throws rather than returning null.
    if (-not $Path) { return '' }
    ($Path.Trim('"', " ") -split '[\/]')[-1].ToLowerInvariant()
}

function Get-Candidates {
    Get-ScheduledTask | Where-Object {
        $_.Principal.LogonType -eq 'Interactive' -and
        $_.State -ne 'Disabled' -and
        $_.TaskPath -notlike '*Microsoft*' -and
        @($_.Actions | Where-Object {
            $_.Execute -and ($consolePrograms -contains (Get-ExeName $_.Execute))
        }).Count -gt 0
    }
}

# ---------------------------------------------------------------- list mode
if (-not $TaskName -and -not $All) {
    $found = @(Get-Candidates)
    if ($found.Count -eq 0) {
        Write-Host 'No tasks on this machine can flash a console window.' -ForegroundColor Green
        return
    }

    Write-Host "Tasks that can flash a console window ($($found.Count)):" -ForegroundColor Yellow
    Write-Host ''
    foreach ($t in $found) {
        $info = Get-ScheduledTaskInfo -TaskName $t.TaskName -TaskPath $t.TaskPath
        $failed = $info.LastTaskResult -ne 0 -and $info.LastTaskResult -ne 0x41301
        Write-Host ("  {0}" -f $t.TaskName) -ForegroundColor Cyan
        foreach ($a in $t.Actions) {
            Write-Host ("      runs: {0} {1}" -f $a.Execute, $a.Arguments)
        }
        $msg = "      last result: 0x{0:X8}" -f $info.LastTaskResult
        if ($info.LastTaskResult -eq 0xC000013A) {
            $msg += '  <- killed by its console closing, not completing'
        }
        Write-Host $msg -ForegroundColor $(if ($failed) { 'Red' } else { 'DarkGray' })
    }
    Write-Host ''
    Write-Host 'To fix them:' -ForegroundColor Yellow
    Write-Host ("  ./fix-console-flash.ps1 -TaskName '{0}'" -f $found[0].TaskName) -ForegroundColor Cyan
    Write-Host '  ./fix-console-flash.ps1 -All' -ForegroundColor Cyan
    return
}

# ------------------------------------------------------------------ elevation
$isAdmin = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()
).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    # Relaunch elevated rather than just complaining. Changing a task principal
    # is an administrative action, so a UAC prompt is unavoidable - but there is
    # no reason to make the caller construct the elevated command themselves.
    Write-Host 'Needs admin - asking for elevation (approve the UAC prompt)...' -ForegroundColor Yellow

    # Order matters: everything after -File is handed to the SCRIPT, so any
    # PowerShell switch such as -NoExit has to come before it. -NoExit keeps the
    # elevated window open, since it is a separate console from this one and
    # would otherwise close before its output could be read.
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-NoExit',
                 '-File', ('"{0}"' -f $PSCommandPath))
    if ($TaskName) { $argList += @('-TaskName') + ($TaskName | ForEach-Object { '"{0}"' -f $_ }) }
    if ($All)      { $argList += '-All' }
    if ($Revert)   { $argList += '-Revert' }

    try {
        Start-Process powershell -ArgumentList $argList -Verb RunAs -Wait -ErrorAction Stop
        Write-Host 'Elevated run finished.' -ForegroundColor Green
    }
    catch {
        Write-Warning "Elevation was refused or failed: $($_.Exception.Message)"
        Write-Host 'Run this from an Administrator PowerShell instead:' -ForegroundColor Yellow
        Write-Host "  & '$PSCommandPath'" -ForegroundColor Cyan
    }
    return
}

# --------------------------------------------------------------------- apply
if ($All) { $TaskName = @(Get-Candidates | ForEach-Object { $_.TaskName }) }

if (-not $TaskName) {
    Write-Host 'Nothing to do.' -ForegroundColor Green
    return
}

New-Item -ItemType Directory -Force -Path $backupDir | Out-Null

foreach ($name in $TaskName) {
    $task = Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
    if (-not $task) {
        Write-Warning "no task named '$name' - skipping"
        continue
    }

    $safe = ($name -replace '[\\/:*?"<>|]', '_') -replace ' ', '_'
    $backup = Join-Path $backupDir "$safe.xml"

    if ($Revert) {
        $principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME `
            -LogonType Interactive -RunLevel $task.Principal.RunLevel
        Set-ScheduledTask -TaskName $name -Principal $principal | Out-Null
        Write-Host "reverted: $name -> Interactive" -ForegroundColor Yellow
        continue
    }

    # Keep a copy of the original before touching anything.
    if (-not (Test-Path $backup)) {
        Export-ScheduledTask -TaskName $name | Out-File -FilePath $backup -Encoding utf8
    }

    if ($task.Principal.LogonType -eq 'S4U') {
        Write-Host "already fixed: $name" -ForegroundColor DarkGray
        continue
    }

    $principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME `
        -LogonType S4U -RunLevel $task.Principal.RunLevel
    Set-ScheduledTask -TaskName $name -Principal $principal | Out-Null

    $after = Get-ScheduledTask -TaskName $name
    Write-Host "fixed: $name -> $($after.Principal.LogonType)" -ForegroundColor Green
}

Write-Host ''
Write-Host 'Current state:' -ForegroundColor Cyan
foreach ($name in $TaskName) {
    $t = Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
    if ($t) {
        $i = Get-ScheduledTaskInfo -TaskName $name
        '  {0,-30} logon={1,-12} next={2}' -f $t.TaskName, $t.Principal.LogonType, $i.NextRunTime
    }
}
