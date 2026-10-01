# Afterglow inventory and Windows port plan

Inventoried 2026-09-29 from `/Users/klingon/TMOGClone/build/Afterglow.app` and its adjacent source. App bundle: `org.local.afterglow`, version 1.0.0 (1), ARM64 Mach-O, minimum macOS 14. Executable SHA-256: `f6f622ecf9dd8d051e5e85f01a47b20daa1acff55007fc397123b87d71523a06`.

The source comprises 2,706 lines across the Swift UI/model files and C telemetry implementation. `Package.swift` has a native executable and a C target linked to IOKit/CoreFoundation. The initial inventory used bundle metadata and source inspection. During the subsequent 1.0.1 visual revision, native screenshots of the running Mac Summary and Processes pages were also inspected successfully. The shipped Mac binary and Swift/C sources were not modified.

## Feature inventory and current Windows coverage

| Area | Mac implementation | Windows core port |
| --- | --- | --- |
| Shell | SwiftUI/AppKit, 13 destinations, native menus | WPF/C#, same destinations, native window and dialogs, keyboard commands |
| Summary | CPU/kernel/temperature history, VFD gauges, top processes, memory and resource tiles | Same arrangement. The package-power gauge stays empty. Temperature, GPU, disk busy, cache/commit/page file, and battery rate bind only when measured |
| Performance | Per-core charts; CPU, memory, storage, network, energy, GPU and NPU views; device selectors | Per-core PDH charts, memory detail, GPU engines, per-disk and per-adapter selectors, battery-rate graph, ACPI zones. The NPU page explains that no generic counter exists |
| Processes | Search/sort, flat/app/tree modes, icons, inspector, reveal, terminate/force quit/tree, CSV, disk rates, derived energy score | Path groups, Applications, My processes, hierarchy, icons, inspector, reveal, CSV of underlying rows, energy score, and identity-checked end, end-tree, suspend, and resume |
| System Info | Host, OS, CPU, model, memory, topology, architecture | Host, OS, BIOS model, logical and physical processors, packages, cache sizes, virtualization, and the hypervisor bit. Failures stay Unavailable |
| Startup apps | LaunchAgent/LaunchDaemon plists | Run/RunOnce, Startup folders, and Task Scheduler tasks (best effort) |
| Users | Process counters grouped by owner | Same owner grouping. Hiding Afterglow affects this list and does not change the recording |
| Services | `launchctl list` | Native Service Control Manager, read-only |
| Connections | `lsof` with PID/protocol/endpoints/state, live refresh | IPv4/IPv6 TCP and UDP with owning PID, refreshed every 2 seconds |
| Power & Freq | IOPS, IOReport power rails, HID die temperatures, Apple-managed clocks | Live PDH frequency, registry nominal frequency, and battery charge/rate. Package and rail watts stay unavailable |
| Flight Recorder | JSON v1, 3,600-frame/250k-process budget, pause/seek/replay | JSON v2, same budgets plus a 128 MiB cap. Optional v2 fields. Mac v1 import keeps shared counters, cores, disks, GPU, temperature, and process parent/threads/priority. Mac CPU time is not copied into the Windows CPU-time field |
| Benchmarks | Integer CPU, 1 GiB SHA-256, 64 MiB sync-write/verified cached read | Corresponding C# workloads, serialized background execution, unique temporary file with cleanup |
| Installed Apps | `.app` bundles in standard folders | Desktop uninstall entries plus install location, and Store package family names |
| Disk Space | Mounted volumes, cancellable allocated-size folder scan; visual block map uses rank weights | Volumes plus that scan: immediate children, allocated size, reparse points skipped, hard links once, one million entry cap, rank-weighted block map |
| Presentation | Full color/green/amber/blue/mono, light/dark/system, saturation 0–11, bloom, animated 60 Hz VFD | Five phosphor modes, saturation, bloom, and Dark/Light/System. Changing appearance rebuilds the shell |
| Companion modes | Menu bar, compact window, topmost, F12 animated screensaver | Tray readout, compact gauges and CPU graph, topmost, and F12 phosphor ribbons. Closing the main window still quits |
| Alerts | Memory pressure, temperature, watts, sustained process CPU; self CPU/estimated watts footer | Available-memory pressure, a real hotspot at or above 90 °C, and a process at or above 150% of one core for 10 seconds. No watt alert. Footer shows self CPU only |

Mac source anchors: `App.swift` for navigation/settings/window modes; `Dashboard.swift`, `Performance.swift`, `Power.swift`, `Pages.swift`, `Components.swift` for UI; `Models.swift` for frame/schema/recording/rates/alerts; `Inventory.swift` for inventory/benchmarks/scanner; `Telemetry.c` and `Telemetry.h` for native counters, power and safe signals; `SelfTest.swift` for provider validation.

## Architecture decision

Use **C# / .NET 10 / WPF**, with native Win32 providers. WPF provides a Windows desktop control model, efficient virtualized tables, custom vector graphs, keyboard accessibility and ordinary portable deployment. This is a replacement for the SwiftUI frontend and Mach/IOKit providers, not a binary conversion. It requires no browser shell and no kernel driver for the core features.

- `Afterglow.Core`: platform-neutral records, rate/CPU math, identity policy, formatting/CSV, validated versioned trace storage and benchmarks.
- `Afterglow.Windows`: WPF shell, graph drawing, user settings, serial worker-based sampling, registry/network/SCM inventories, P/Invoke telemetry and process controls.
- `Afterglow.Tests`: package-free executable tests runnable on Mac, Linux and Windows.
- `--self-test`: Windows-native layouts/provider/termination/trace smoke checks.
- `scripts/build.ps1`, `scripts/test.ps1` and Windows CI: portable x64/ARM64 builds and validation.

Sampling runs off the UI thread with at most one sample in flight. Page and replay generation checks reject stale worker results. Live graph history retains only samples, not old process tables. Process identity is PID plus kernel creation FILETIME. Termination rechecks identity and critical-process status on the same open handle used to act. No automatic elevation or service mutation is implemented.

## Provider mapping and semantics

| Mac source | Windows source | Important behavior |
| --- | --- | --- |
| `host_processor_info` | PDH `\Processor Information(*)`, then `GetSystemTimes` | Privileged time does not include idle. The first sample is unavailable. The fallback aggregate is disabled above 64 logical processors. |
| Mach VM/swap/memorystatus | `GlobalMemoryStatusEx`, `GetPerformanceInfo`, paging-file PDH | Pressure uses available-memory thresholds (under 15% critical, under 40% warning). Commit and cache come from performance info. Page-file percent is not turned into bytes. |
| `libproc` | Toolhelp, `GetProcessTimes`, tokens, `IsWow64Process2` | One core = 100%. Missing GPU instances are 0 only when the GPU counter source produced a utilization. |
| Process disk counters | `GetProcessIoCounters` | Includes network, device, and file I/O. |
| IOKit disks | PDH `PhysicalDisk(*)` bytes and `% Idle Time` | Busy is `100 − idle`. Per-disk rates above 16 GiB/s are dropped. Aggregate busy is the maximum. `_Total` is a fallback. |
| `getifaddrs` | `NetworkInterface.GetIPStatistics` | Non-loopback, up adapters. A warming adapter does not blank totals from adapters that have a rate. |
| IOPS package power | No generic Windows counter | The Summary power gauge stays unavailable. |
| Battery rate | `CallNtPowerInformation(SystemBatteryState)` | Positive rate is discharging. Values that are missing, non-finite, or beyond 500 W are not shown. This is not package power. |
| HID temperature | ACPI `MSAcpi_ThermalZoneTemperature` | Off the sample thread, at most every 15 seconds. Readings outside −20..125 °C are dropped. |
| GPU | PDH `GPU Engine(*)` and DXGI adapter names | Utilization is the busiest adapter's highest engine. Dedicated memory from DXGI is capacity. |
| Clocks | PDH processor frequency and registry `~MHz` | A missing live clock says "Clock unavailable". |
| Launch services / app bundles | SCM, registry, Startup folders, Task Scheduler, Store package keys | COM and registry failures are skipped. Store names are package family names. |
| `lsof` | `GetExtendedTcpTable` / `GetExtendedUdpTable` | Owning PID, joined to the current process snapshot when that PID is present. |
| NPU / power rails | None | Left unavailable. No usage-based estimate. |

Trace compatibility is intentionally versioned. Windows reads Mac v1 recordings as a projection of shared counters and process metadata; it does not preserve Mac-specific sensors, cores, topology, process start identities or every original field on re-export. Mac v1 readers do not support Windows v2. Keep original Mac traces. Replay never invokes live process actions or substitutes live inventories for historical data.

## Delivery phases and acceptance

1. **Inventory and core port — implemented.** Native shell, inventories, recorder, benchmarks, and process-action guardrails. Mac-host checks passed. Windows runtime acceptance is still outstanding.
2. **Monitoring parity — implemented in source, not runtime-proven here.** Per-core group-aware PDH, per-disk and per-adapter graphs, GPU engine aggregation, process tree and inspector, PID-owned TCP/UDP, Store family names, scheduled tasks, cancellable folder scan, and tray readout. x64/ARM64 Windows, non-English Windows, hotplug, and access-denied behavior still need a Windows machine.
3. **Hardware and visual parity — partial.** Light/System appearance, phosphor ribbons, memory-pressure, temperature, and sustained-CPU alerts are implemented. Package watts, vendor rails, and NPU utilization stay unavailable rather than estimated.
4. **Release hardening — open.** Long-run profiling, DPI and screen-reader checks, sleep/resume, >64-core coverage, trace fuzzing, and signed binaries are not done. These archives are unsigned development builds.

## Primary technical references

- [WPF overview](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/) and [Windows targeting on other hosts](https://learn.microsoft.com/en-us/dotnet/core/tools/sdk-errors/netsdk1100).
- [GetSystemTimes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getsystemtimes): idle is included in kernel time and processor-group limitations apply.
- [GetProcessMemoryInfo](https://learn.microsoft.com/en-us/windows/win32/api/psapi/nf-psapi-getprocessmemoryinfo): access and memory counter semantics.
- [PdhAddEnglishCounterW](https://learn.microsoft.com/en-us/windows/win32/api/pdh/nf-pdh-pdhaddenglishcounterw): language-neutral counter registration.
- [.NET 10 downloads](https://dotnet.microsoft.com/en-us/download/dotnet/10.0): SDK/runtime distribution.
