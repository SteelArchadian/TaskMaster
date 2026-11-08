# Task Manager (clone) — design

Goal: look and behave 1:1 like the Windows 11 Task Manager, but open instantly and never stall the desktop.

## Why the real one lags on launch
On open it builds every view synchronously: PDH counters for every process and every GPU engine
(hundreds of instances on a 5090 + ~570 processes), per-core CPU, disks, adapters, startup list,
icons and descriptions for every image. That work runs on/blocks the UI thread and floods DWM.

## Rules that make it light
1. **Window first, data second.** First frame shows the shell (title bar, nav, empty table)
   within ~100 ms. Data arrives on a background thread at below-normal priority.
2. **One cheap call per tick.** `NtQuerySystemInformation(SystemProcessInformation)` returns every
   process + thread with CPU times, memory, I/O counts in one syscall (no per-process handles).
3. **No PDH.** GPU via D3DKMT statistics (same as System Informer), cached per adapter/engine.
   CPU/memory totals via `NtQuerySystemInformation`/`GetSystemTimes`/`GlobalMemoryStatusEx`.
4. **Cache by image path.** Icons, file descriptions and publisher are loaded once per exe, lazily,
   only for rows on screen, at low priority.
5. **Only visible rows update.** Virtualized list; diff-based updates; no full re-sort unless
   the sort key changed order.
6. **Stop when hidden.** Minimized/hidden = sampling paused (Task Manager keeps polling).
7. **Bounded memory.** Fixed-size history ring buffers (60 samples) for graphs.

## Safety rules (non-negotiable)
- **User mode only.** No kernel driver, no DLL injection, no hooks into other processes,
  no undocumented writes. Read-only queries except explicit user actions.
- **Every action is the same Win32 call Task Manager uses** (TerminateProcess, SetPriorityClass,
  SetProcessAffinityMask, ProcessPowerThrottling for Efficiency mode, SCM for services,
  MiniDumpWriteDump for dumps). Critical-process kill shows Task Manager's warning.
- **No network access** except the opt-in update check (signed, HTTPS, GitHub Releases).
- **Crash isolation.** Each sampler wrapped; a failing source shows "—" instead of crashing.
  Unhandled exceptions are logged to `%LOCALAPPDATA%\TaskManager\logs`, never silently swallowed.
- **Replacing Ctrl+Shift+Esc** uses the same documented IFEO `Debugger` value Process Explorer
  uses, written only on the user's opt-in and removed cleanly on opt-out/uninstall.
  Real taskmgr.exe stays untouched and reachable.
- Elevation: runs as the user; "Run as administrator" relaunch (or opt-in scheduled task for
  prompt-free elevation) for actions on elevated processes/services.

## Parity checklist (Windows 11 24H2/25H2 Task Manager)
- [ ] Shell: Mica title bar, hamburger nav, search box, command bar per page, dark/light, settings
- [ ] Processes: Apps / Background / Windows groups, expand to children, heat-map columns,
      CPU, Memory, Disk, Network, GPU, GPU engine, Power usage (+trend), Status (Efficiency/Suspended),
      Publisher, PID, Process name, Type, Command line; End task, Efficiency mode, Run new task,
      context menu (switch to, restart (Explorer), open file location, search online, properties,
      create dump file, go to details, provide feedback n/a), column picker, resource values %/abs
- [ ] Performance: CPU (overall/logical), Memory, Disk N, Ethernet/Wi-Fi, GPU N, NPU; graphs + stats
- [ ] App history
- [ ] Startup apps: enable/disable, impact
- [ ] Users: per user totals, disconnect/sign off
- [ ] Details: all columns, priority, affinity, set efficiency, analyze wait chain, UAC virtualization,
      create dump, end process tree
- [ ] Services: start/stop/restart, open Services, go to details
- [ ] Settings: default page, real-time update speed, always on top, minimize on use, hide when minimized,
      theme, show full account name, history for all processes, multi-tab grouping
- [ ] Keyboard: Ctrl+Shift+Esc (opt-in), Ctrl+E/Ctrl+F search, Del end task, F5 refresh, Ctrl+Tab pages
- [ ] Updater (Obelisk shared design), GPL-3.0

## Layout
- `src/TaskManager/Engine/` — samplers (no UI types), all on one background thread.
- `src/TaskManager/Views/` — pages.
- `src/TaskManager/Native/` — P/Invoke, one file per API family.
