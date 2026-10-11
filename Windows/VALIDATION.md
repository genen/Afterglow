# Windows port validation

2026-09-29, macOS ARM64 host, .NET SDK 10.0.401.

## Monitoring and presentation revision (1.1.0)

The Windows project now reads per-core PDH counters, per-disk busy and byte rates, GPU engines, DXGI adapter identity, performance-info cache and commit, ACPI thermal zones, battery discharge rate, and processor frequency. The shell adds the performance sections, process grouping and tree actions, PID-owned connections, scheduled tasks, Store family names, folder scan, light/system appearance, tray, compact gauges, phosphor ribbons, and the pressure/temperature/sustained-CPU alerts. Package watts and NPU utilization are still absent on purpose.

Release compilation succeeded with warnings treated as errors. The self-contained win-x64 and win-arm64 archives were rebuilt afterward and were not launched. Core tests: **49 checks passed, 0 failed**, including `build/Validation.pulsetrace`. On this Mac, `Marshal.SizeOf` for the 64-bit layouts matched the Windows sizes used by the providers: battery state 32, performance information 104, file identity 52, DXGI description 312, and the classic tray structure 296 (icon at offset 32, tip at offset 40). That is a marshalling check, not a call into those Windows APIs.

WPF was not executed. PDH, DXGI, WMI, IP Helper, Toolhelp, Service Control Manager, Task Scheduler, the tray, and process termination were not executed. No Windows screenshot or pixel-level comparison is claimed. The new `--self-test` checks (battery-structure size, `GetPerformanceInfo`, core range, a process parent, and GPU null-or-in-range) run only on Windows.

## Mac-style interface revision (1.0.1)

Inspected the running Mac Summary and Processes windows via native screenshots and compared them with their SwiftUI implementation. Reworked the Windows shell and Summary to follow the same layout, spacing, charcoal palette, icon sidebar, bottom wordmark, footer controls, VFD meters, phosphor colors/bloom, CPU/kernel overlays, memory fill, six resource cards and compact top-process table. The process list now includes CPU bars. Existing color choices persist; new installations default to Full color. The native Windows title bar is retained.

Release compilation and both architectures were rebuilt after this change. WPF rendering still requires Windows: no Windows screenshot or pixel-level equivalence is claimed. On Windows, specifically verify the Summary at 1100×720 and 1440×920, high DPI, all five color modes, saturation endpoints, bloom on/off, missing sensors, meter animation and graph hovering after pauses. Use the header ⋯ menu for open/save and companion windows; record/pause controls are now in the footer.

## Executed here

- Release WPF compilation: zero warnings, zero errors; warnings are treated as errors.
- Self-contained Windows x64 and ARM64 publish: succeeded. These are Windows PE executables, not Mac launchers.
- Core test executable, as of the 1.0.1 archive: **40 checks passed, 0 failed**, including the existing `build/Validation.pulsetrace` Mac recording (approximately 3.9 MiB). The 1.1.0 run is 49 checks; see the section above.
- Checked rate/reset/zero/warm-up semantics, CPU idle subtraction, live/replay/PID-reuse/self/system identity policy, locale-independent and formula-safe CSV, JSON round-trip and source platform retention, Mac epoch and process CPU conversion, malformed traces, timestamp ordering, missing vs zero fields, and frame/process recording limits.
- Packaged portable ZIPs and checked archive integrity and required executable/runtime files.

No changes were made to the Mac executable or its Swift/C source. No existing user process was terminated.

## Not executed on this Mac

WPF cannot run on macOS. Win32 telemetry, process token access, PDH availability, service enumeration, process termination and native window appearance have **not** been runtime-validated here. The PowerShell scripts and GitHub Actions workflow also await execution on Windows. No Windows device/VM was connected for this task. Successful cross-compilation does not establish runtime acceptance.

## Windows acceptance checklist

1. Install `Afterglow-Setup-<version>-win-x64.msi` and launch Afterglow from the Start menu. Verify window scaling at 100%, 150%, 200%, keyboard focus and text contrast, resizing, table scrolling and all 13 destinations. Confirm the administrator prompt appears for admin accounts.
2. Run `Windows/scripts/test.ps1`, or launch `Afterglow.exe --self-test` with a writable current directory. Inspect `afterglow-smoke-test.json` and exit code. It checks native structure layouts, real samples, inventories, trace round-trip, stale/replay process rejection and termination of a disposable child created by the test.
3. Compare CPU and physical memory against Task Manager after two warm-up samples and under a short workload. Task Manager commonly normalizes process CPU across processors; this app uses one core = 100%. Inspect network and disk during real transfer activity. Validate on non-English Windows, battery and desktop devices, disconnected networking, inaccessible processes and >64-core systems.
4. Start/stop/save a recording, reopen it, play/pause/seek, return to live. Check timestamp pacing, numeric sorting, unchanged selected PID identity, unavailable gaps, and disabled process actions/inventories in replay. Import a Mac v1 trace, preserve the original, re-export supported fields and reopen.
5. Exercise process filtering/CSV; inspect quoting and numeric values with a non-English locale. Confirm End process only on a disposable process and verify that Afterglow rejects its own process and stale identities.
6. Change interval, phosphor, bloom, Dark/Light/System, topmost, alerts, and Show Afterglow. Restart and confirm existing choices survive. Open the compact window, F12 ribbons (Esc, F12, or click exits), and the tray menu. Quit still follows the main window. Confirm NPU and package watts stay unavailable, a missing sensor stays Unavailable, and a measured zero stays zero. Battery watts, when shown, are labelled as battery rate.
7. Run all three benchmarks once. Confirm disk read verification and temporary file cleanup. Navigate away/back during a benchmark; repeated runs must be refused until completion.
8. Run for an hour, including sleep/resume and adapter changes; inspect UI responsiveness, CPU cost, memory/handle stability, recorder limits and incomplete/access-denied providers. Repeat native checks on actual ARM64 Windows before release.

Release signing, installer testing, Windows UI acceptance and hardware-provider parity remain open.
