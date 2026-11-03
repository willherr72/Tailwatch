# Initial prototype validation — 2026-11-03

## Executed checks

- Rust: 13 contract, collection freshness, address policy, identity lease, and HTTP authorization tests; formatting and Clippy with warnings denied.
- Viewer core: 42 tests covering parsing, nullable/zero values, freshness, network switching, settings, transient discovery recovery, malformed responses, bounded startup grace, and independent polling with a 48-peer slow fleet. Saved-account tests cover Unicode labels, distinct IDs for shared login names, malformed account lists, rejected command-like IDs, and per-tailnet reporting-filter persistence.
- Native Windows UI: row rendering, expansion/collapse, search, and GPU sorting passed the automated smoke check. A captured panel was visually inspected.
- Reporting filter and account controls: native smoke checks cover hiding offline/unavailable/stale machines, recovery into the filtered list, restoring all rows, saved-account selection by ID, disabled switching during a pending command, offline account selection, and the management action after account-list failure. A controlled child executable exercises actual process arguments, UTF-8 account output, switch success, nonzero exit, and cancellation without changing the real network connection. The installed client's saved-account JSON was inspected; a real cross-account switch remains a manual acceptance check.
- Local integration: actual Tailscale discovery, the local Rust HTTP endpoint, and C# protocol validation produced fresh CPU/RAM data. Only this computer was used for the live integration test.
- NVIDIA hardware: an RTX 5070 Laptop GPU with driver 581.80 returned `Unknown` from a long-lived NVML session while a fresh session returned valid idle readings. Recovery now retries once with a fresh session within the sample; 20 consecutive two-second observations then returned valid measured zero and the live Rust/C# check reported GPU status `ok`. Failed NVML initialization backs off for 30 seconds. Sustained compute load and driver replacement remain untested.
- Native CI: Windows x64, Ubuntu 22.04 x64, and macOS 14 arm64 built and passed Rust tests/Clippy. Windows CI also passed viewer tests, native UI smoke, and self-contained publishing.
- Installer validation: PowerShell parsing and Bash syntax checks, plus the live Mac installation described below.
- Subsequent Mac deployment: installed the per-user launch agent on an M4 Pro Mac mini running macOS 26.5. Verified background startup, restrictive file permissions, loopback/tailnet-only listeners, and fresh CPU, RAM, and Apple GPU readings received by the Windows viewer machine. macOS Tailscale's bundled executable requires `TAILSCALE_BE_CLI=1` in the launchd environment; the installer now sets it explicitly.
- Independent source review: three recovery/scheduling findings were fixed with regression tests. Review is not evidence of physical hardware testing.

## Local overhead

A 302-second run with the tray panel closed measured the pre-review Windows build:

| Process | Average CPU, one core | Average working set | Peak working set |
| --- | ---: | ---: | ---: |
| Viewer | 0.71% | 160.5 MiB | 166.8 MiB |
| Agent | 0.75% | 65.8 MiB | 65.8 MiB |

These are observed process working sets, not private allocations or fleet-wide guarantees. The viewer exceeded the design's 150 MiB target. The measurement excludes separate Tailscale CLI processes and was taken while other development work was running. It is not a benchmark of the final scheduling fix.

## Remaining acceptance checks

- Install/start/stop/uninstall services under actual Windows and Linux accounts; test Mac sign-out/sign-in and uninstallation. Mac installation and launchd job reload have been verified.
- Verify Apple GPU counters on physical Apple silicon and each supported macOS version; private IOReport interfaces can change.
- Exercise NVIDIA under sustained compute load, driver restart, sleep/wake, and multiple GPUs.
- Exercise actual tailnet switching and interface disappearance on the deployed machines; automated tests cover response isolation and identity expiry.
- Assess high-DPI placement, screen readers, long device names, and large real fleets.
- Sign/notarize binaries before broad distribution. This prototype is unsigned.

AMD GPU collection and simultaneous aggregation of separate tailnets are outside this version. CPU/RAM collection still works on AMD systems.
