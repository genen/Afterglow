# Afterglow for Windows

Native WPF shell and core monitoring implementation, targeting .NET 10 and Windows 10/11 x64 or ARM64. No browser runtime, telemetry uploads, account, background service, or administrator requirement. This is the first Windows port; see the [feature inventory and roadmap](../docs/WINDOWS-PORT.md).

## Run the portable app

Extract **all** files from `Afterglow-win-x64.zip` (Intel/AMD) or `Afterglow-win-arm64.zip` (Windows on ARM) into a folder, then run `Afterglow.exe`. The default packages include .NET. The builds are unsigned. They cannot run directly on macOS.

Version 1.3 stable. Each run of `build.ps1` advances the minor number and labels that build beta. Settings shows the label. Beta is removed only from a version reported as stable, by rebuilding with `build.ps1 -Stable`. Implemented:

- Mac-style WPF shell with 13 icon destinations, waveform wordmark, footer controls, resizable window, keyboard navigation, and virtualized tables. Dark, Light, and System appearance. Full color plus green, amber, blue, and mono, saturation 0–11, and phosphor bloom.
- Summary: CPU, package-power, temperature, and GPU gauges. The power gauge stays unavailable unless a real package-watt source exists. CPU/kernel history can show temperature on a second scale. Top processes, memory pressure, cache, commit, and page-file percent. Disk, network, battery-rate, GPU, NPU, and thermal cards. NPU stays unavailable. Battery rate is labelled as battery rate, not package power.
- Performance sections for CPU (per logical processor), memory, GPU engines, an honest empty NPU page, per-disk and per-adapter graphs, battery rate, and ACPI thermal zones.
- Processes: search, custom sort, path groups, Applications (windowed only), My processes, parent hierarchy, icons, inspector, CSV of the underlying rows, reveal, and identity-checked End, End tree, Suspend, and Resume. Critical processes and Afterglow itself are protected. Replay cannot control live processes. Hiding Afterglow only affects the display; recordings still include it.
- Startup Run/RunOnce, Startup folders, and scheduled tasks. Desktop uninstall entries with install location, plus Store package family names. Services stay read-only. Connections include the owning process and refresh every 2 seconds. Power inventory refreshes every 5 seconds.
- Disk Space volumes plus a cancellable folder scan of immediate children. Nested size is allocated size, reparse points are skipped, and each hard link is counted once. The block map uses rank weights.
- Flight Recorder v2 with the same 3,600-frame, 250,000-process, and 128 MiB limits. New trace fields are optional. A Mac v1 import keeps useful shared fields and does not copy Mac CPU time into the Windows CPU-time field. Imported processes are not actionable. Keep the original Mac file.
- Menu-bar tray (Open, Pause, Quit) and a compact window with gauges, a CPU graph, and the pressure/top line. Closing the main window still quits. F12 opens animated phosphor ribbons; Esc, F12, or a click returns. This is an app mode, not an installed `.scr` screensaver.
- Alerts for memory pressure (available memory), a real hotspot at or above 90 °C, and a process at or above 150% of one core for 10 seconds. Off unless enabled in Settings. Dismiss starts a 60-second cooldown. At most four alerts. Replay hides them.

CPU in process tables is a percent of all logical processors counted at launch, on the same scale as Task Manager. Aggregate CPU uses 0–100%. The first sample stays unavailable while counters warm up. Per-core PDH `Processor Information` counters are group-aware; if they are missing, the GetSystemTimes fallback does not publish an aggregate above 64 logical processors. Process I/O includes file, network, and device I/O. Disk busy is `100 − % Idle Time`, and the aggregate is the busiest disk. GPU percent is the busiest adapter's highest engine, with each engine clamped to 0–100%. Dedicated GPU memory is capacity. Unknown readings display **Unavailable**; a measured zero stays zero. Total watts add the CPU package, memory, GPU package or board, and any motherboard, storage, or PSU output sensor. Core, uncore, and per-pin readings are not added on top of a package. Battery rate is not that total. NPU utilization is not invented.

## Build and test on Windows

Install the .NET 10 SDK, then from the project root:

```powershell
./Windows/scripts/build.ps1                  # self-contained win-x64 ZIP
./Windows/scripts/build.ps1 -Runtime win-arm64
./Windows/scripts/build.ps1 -Stable
./Windows/scripts/installer.ps1              # Afterglow-Setup-win-x64.msi
./Windows/scripts/installer.ps1 -Runtime win-arm64
./Windows/scripts/test.ps1                   # core + native checks on x64
# ARM64 native host:
./Windows/scripts/test.ps1 -AppPath ./Windows/artifacts/win-arm64/Afterglow.exe
```

The setup installs for all users into `C:\Program Files\Afterglow`, adds Start menu and desktop shortcuts, and appears in Settings under Apps. It asks for administrator because it installs into Program Files and installs the PawnIO sensor driver. The MSI is unsigned. A later build with the same architecture replaces the installed copy.

Afterglow runs as the signed-in user. CPU temperature and package power come from LibreHardwareMonitor, which needs the PawnIO driver and administrator access. The MSI installs PawnIO 2.2.0 (signed by namazso, pinned by SHA-256 in `installer.ps1`) in the same setup, and leaves it installed on uninstall because other monitoring tools share it. The app itself never downloads anything. With the portable ZIP, install PawnIO from pawnio.eu. Either way, CPU temperature and package power appear after **Settings › Restart as administrator**; until then they show **Unavailable**.

Pushes to `main` and pull requests build, test, and keep `Afterglow-Setup-<version>-win-x64.msi` as a workflow artifact. To publish, push a tag that matches `<Version>`, such as `v1.4.0`. The workflow then puts that one file on the release for the tag, creating the release if needed. It does not overwrite an MSI already on a release unless you run it by hand with Replace checked. The source zip and tar.gz are added by GitHub and are not the installer.

Development: `dotnet run --project Windows/Afterglow.Windows`. Open `Windows/Afterglow.slnx` in a .NET 10-compatible IDE. `-FrameworkDependent` creates a smaller build requiring the matching .NET 10 Desktop Runtime. Use a clean output folder when changing publishing modes.

The smoke test writes `afterglow-smoke-test.json` in the app working directory, collects real telemetry, enumerates inventories, and terminates only a disposable child it creates. Exit code 0 means pass. CI builds the x64 app and MSI and runs the core and native checks on the x64 Windows runner. ARM64 builds are made locally.

Core checks can also run on macOS/Linux:

```sh
dotnet run --project Windows/Afterglow.Tests -c Release
# Optional: verify import of an existing Mac recording
dotnet run --project Windows/Afterglow.Tests -c Release -- build/Validation.pulsetrace
```

WPF can be cross-compiled with `EnableWindowsTargeting`, but UI rendering and Win32 providers must be tested on Windows. See [validation](VALIDATION.md) for what was actually run and the remaining Windows acceptance checks.

## Controls and files

| Control | Action |
| --- | --- |
| Header ⋯ menu | Open/save recording, monitor commands, compact and ambient modes |
| Footer record / pause icons | Start/stop recording and pause/resume |
| Sidebar Settings / Colors | Interval, phosphor, bloom, appearance, alerts, and whether Afterglow is listed |
| Ctrl+P | Pause/resume live sampling or replay |
| Ctrl+R | Sample now |
| Ctrl+O / Ctrl+S | Open / save recording |
| Ctrl+Shift+K | Compact summary |
| F12 / Escape / click | Open / exit ambient ribbons |
| Graph hover | Sample time and the series under the pointer |
| Column header | Sort the process table, including groups |
| Tray icon | Open, pause, or quit. The tooltip shows CPU, and GPU or temperature when a real reading exists |

Settings: `%LOCALAPPDATA%\Afterglow\settings.json`. Adding a setting does not reset the ones already saved. Recordings and CSV files are saved only to a location you choose. Closing the main window quits the app; the tray is only a readout and does not keep the process alive. Trace files include process names, owners, and paths. Only share recordings you intend to disclose.

No affiliation with TMOG or Dave Plummer.
