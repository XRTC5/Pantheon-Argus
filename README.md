# Argus

A watchman panel pinned to the edge of the desktop. Shows what is running, what
is scheduled, what just flashed a window on screen, and what Claude Code is
doing, without being a second thing to manage.

Named for the hundred-eyed giant who was set to watch and never slept.

---

## What it shows

| Section | Answers |
|---|---|
| **Services** | Is each service actually up, and is it *answering* or just holding a port open |
| **Schedule** | When the next pull runs, and whether the last one really succeeded |
| **Claude Code** | Which project has a live session writing right now |
| **Caught consoles** | Every console that started, who started it, and whether it was ever on screen |
| **Processes** | Grouped by image, ranked by memory, foreground first |

The header carries machine CPU and RAM, the process count, and Argus's own
working set — a watchman that will not be watched is not one to trust.

## What a caught console actually means

Argus does not watch windows here. It watches the process table for a console
host appearing - `conhost`, `cmd`, `powershell`, `wscript` - and treats that as
a console. Useful, and not the same thing, because two of those events are not
what they look like:

- **A console in session 0 was never on screen.** Scheduled tasks configured to
  run whether you are logged in or not run in the service session, which has no
  interactive desktop at all. A window there is not hidden, it is impossible.
  Those rows are grey and say so, rather than being flagged amber as something
  that happened to you.
- **One console event is several processes.** Running a single command through a
  shell produces the shell, whatever it calls, and the `conhost` Windows
  attaches to the console - three rows, three timestamps, one event. Only the
  outermost is listed; the rest are under it in the breakdown.

Anything the Task Scheduler starts reports a parent of `svchost.exe`, which
names the mechanism and never the job, so Argus asks Task Scheduler which task
owns the process and shows that name instead.

## What is under a row

Click a row to read what it is, then **WHAT IS UNDER THIS** to see what it is
made of: the process tree beneath it, the consoles it opened, the port it
holds, the scheduled task that owns it.

It opens as its own small window - drag it by its header, resize it from the
corner, close it with the button or Esc, and it reopens where you left it. A
window rather than a panel takeover, because it is a thing you read *while*
looking at the panel; and a real window rather than a popup, because a popup is
owned by its opener and would inherit the panel's place at the bottom of the
window stack. It stays live while it is open.

The sections on the panel group by kind, which is right for a glance and wrong
for a question - one piece of work appears in three of them at once and reads as
three unrelated things. A task, the shell it runs, the console that shell drags
in with it and the port it ends up holding are one thing, and this is the view
that says so.

## Starting and stopping

Service rows carry a **START** or **STOP** button when they are configured for
it. Give a target a `StartCommand` in `settings.json` and Argus will run it -
a `.cmd` or `.bat` goes through `cmd.exe` with no window, so starting a service
never flashes the kind of console this tool exists to hunt. With no
`StartCommand` but a `ScheduledTask`, Start runs the task instead. With neither,
there is no button: Argus never guesses how to start something.

**Stop kills whatever owns the port**, not whatever matched a name. That
distinction was learned by getting it wrong. Launcher scripts commonly use
`start` to detach the real server, so the server is not a child of the script -
killing the matched process tree left the port held and the service merrily
running. And a command-line substring match will happily catch an unrelated
shell that merely mentions the path. Whoever holds the socket is the service, by
definition; nothing else is. After killing, Argus waits for the port to actually
release and says so if it does not, rather than claiming success.

**A target can ask before it kills.** Give it a `StopCommand` (and `StopArgs`)
and Stop runs that first, for an app that should save on its way out. Argus
falls back to the kill only if the process is still running five seconds
later.

### With Clio

[Clio](https://github.com/XRTC5/clio) is a sticky-notes companion app and a
separate download; each works without the other. When both are installed,
Clio leaves its path in `%APPDATA%\Clio\app.json`, and Argus picks that up and
shows a **Clio** row with Start and Stop. Its Stop is `Clio.exe --quit`, so
Clio saves before it exits. Nothing is written to `settings.json`, so the row
follows Clio if it moves and disappears if Clio is removed. A Clio target you
write yourself takes precedence.

## How it sits

Three modes, switched in Settings or from the tray:

- **Desktop** (default) — pinned to the bottom of the window stack. Windows
  open over it the way they open over desktop icons. It is always there and
  never in the way.
- **On top** — floats above everything, for when you want to read it.
- **Reserve** — registers as a Windows appbar. The desktop work area genuinely
  shrinks and maximised windows stop at its edge. Same mechanism as the
  taskbar. Costs real screen width, which is the point.

Drag the header to move it; it snaps to the nearest edge of whichever screen
you dropped it on. Double-click the header, press **SCREEN**, or use the tray
menu to hop to the next display. Drag the inner edge to resize.

## Running it

```powershell
# one-time: the compiler
winget install --id Microsoft.DotNet.SDK.9

# build and launch
./build.ps1 -Run
```

`build.ps1` publishes to `dist/` and can register start-on-login. Argus also
registers itself on first run (Settings → *Start with Windows*).

Start-on-login is a **logon-triggered scheduled task**, not the Run key. The
Run key is throttled by Explorer: ~10s after the shell is ready, then each
entry several seconds apart, in registry enumeration order. Measured on one
boot here, that put Argus on screen 123s after power-on — 89s of it queued
behind OneDrive, Medal and LGHUB, while the app's own start-up is one second.
The task runs with an **InteractiveToken** principal (S4U would give it a
non-interactive window station and the panel would never paint) and an
explicit Normal priority, because a task definition otherwise defaults to
below-normal CPU and I/O. Being a WinExe, it allocates no console, so this
costs no flash. If task registration is refused, Argus falls back to the Run
key and says so in the log.

Settings, and a capped log, live in `%APPDATA%\Argus\`.

## What it actually costs

Measured on the development machine, not estimated:

| | |
|---|---|
| Private bytes (real allocation) | **~40 MB**, flat over 4 minutes |
| Working set, steady state | **~70 MB** |
| CPU over 2 minutes idle | **1.08% of one core**, 0.07% of the machine |
| Disk | 162 KB exe, ~500 KB with the scheduler library |

The first build measured 166 MB. Three things took it down, and the biggest was
a surprise:

**Software rendering, against the usual advice.** WPF defaults to Direct3D. On
a dual-GPU laptop with no MUX that loads *both* vendors' user-mode display
drivers - `nvgpucomp64`, `nvd3dumx`, `igd9dxva64`, `igc64` - which between them
map over 250 MB to hardware-accelerate a panel that never animates. Turning
that off cut private bytes from 139 MB to 42 MB. The usual objection to
software rendering is animation cost, and there is no animation here.

**Dropping WinForms.** It was referenced for the tray icon and monitor
enumeration alone, and was the largest remaining module once the GPU drivers
were gone. Both are now direct Win32 calls.

**One working-set trim, at startup only.** A recurring trim was tried and
measured: the number drops to ~41 MB then faults straight back to ~70 MB within
thirty seconds, because those pages are genuinely in use. It bought a prettier
number in Task Manager, paid for in page faults. Only the startup pages - XAML
parsing, JIT, the first sweep - are actually dead, so they are released once.

## Why it is built this way

The stated requirement was that it must not cost anything to have running. That
shaped most of the decisions, and a few of them are not obvious:

**Toolhelp, not WMI, not `Process.GetProcesses()`.** One kernel call returns
pid, parent pid and image name for the whole machine, with no per-process
handle and no managed `Process` object to dispose. Command lines come from
`NtQueryInformationProcess(ProcessCommandLineInformation)` — microseconds, and
no admin — instead of a `Win32_Process` query that would cost 100-300ms every
sweep. That is what makes a 3-second refresh free rather than something you can
feel.

**A layered window, not `AllowsTransparency`.** `AllowsTransparency` forces WPF
through an intermediate surface for the life of the window. The same
translucency comes from `SetLayeredWindowAttributes` at no per-frame cost.

**Bottom-of-Z-order, not WorkerW reparenting.** Wallpaper tools parent
themselves into the shell's `WorkerW`. That breaks per-monitor DPI and makes
coordinates relative to a window we do not own — fatal on a machine whose two
panels run at different scale factors. Forcing `HWND_BOTTOM` on every
`WM_WINDOWPOSCHANGING` gets the same result and stays a normal, correctly
scaled, clickable window.

**No animation, no blur, no shadows.** Every one of those makes WPF render
through an intermediate surface each frame. The look is built from solid fills,
1px rules and typography, so a still panel costs nothing to leave still.

**Workstation GC, concurrent GC off.** Server GC allocates a heap and a GC
thread per core; on a 16-thread i9 that alone is tens of megabytes for an app
that barely allocates.

**One probe loop, not a timer per probe.** Each probe keeps its own interval,
but they share a single thread that sleeps until the next one is actually due.
A dozen independent timers is a dozen independent reasons for the CPU to leave
idle, which on a laptop is measured in battery.

**Reconciled rows, not cleared collections.** Rebuilding an
`ObservableCollection` every few seconds throws away every container WPF built,
forces a full re-layout, and loses the scroll position. Rows are matched by id
and only what changed is touched — on a quiet machine, nothing.

## The console window that keeps appearing

Argus was built partly to find this, and found it before it was written.

A scheduled task running every 30 minutes under an **Interactive** logon type
passed `-WindowStyle Hidden` — but under an interactive logon Windows creates
the console host *before* PowerShell can apply that flag, so an empty console
flashed on screen every half hour. A second task did the same thing twice a day
with no suppression at all.

Both were also returning `0xC000013A` (`STATUS_CONTROL_C_EXIT`) — the tasks were
being killed by their console closing rather than completing, silently, for
weeks. Argus surfaces that as `last run FAILED console closed` rather than the
raw code, which is the entire reason it exists.

The fix is to stop the tasks running interactively:

```powershell
# Lists the tasks on this machine that can flash a console, and offers to fix
# them. Switching to S4U means no interactive session, so no console host and
# no window. Backs up each task definition first, and -Revert undoes it.
./tools/fix-console-flash.ps1
```

Under the hood that is one property:

```powershell
$p = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType S4U -RunLevel Limited
Set-ScheduledTask -TaskName $name -Principal $p
```

Argus does not apply this itself. It watches; the fix is a separate script you
run deliberately.

## Layout

```
src/Argus/
  Core/       Native P/Invoke, settings, dock manager, process scanner, tray
  Probes/     One per source: process, service, schedule, Claude Code
  Views/      View models, converters
  Theme.xaml  Palette and control styles
tools/
  make_icon.py   Generates argus.ico from scratch, no imaging dependency
```
