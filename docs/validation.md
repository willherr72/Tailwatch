# Initial prototype validation — 2026-11-03

## v0.2 setup, history, and distribution checks

- Viewer core: 62 tests passed, including monotonic history retention and point bounds, duplicate suppression, missing metrics, restart and short-discovery gaps, network isolation, platform-specific install/update commands, rejected addresses, and custom-port guidance.
- Native UI smoke passed for the setup dialog's platform selection, DPI layout, update mode, disabled stale commands, fresh active-account validation immediately before copying, and expanded history graphs. Existing filter, account-switch, Unicode, scroll/refresh and offline checks remain included.
- Package validation: 17 tests passed for exact contents, archive round-trip hashes and executable modes, each binary architecture, missing/extra files, existing output directories, and invalid/mismatched versions.
- Draft-workflow boundaries: 20 tests passed for source repository/run/version checks, expired/duplicate artifacts, refusing existing tags/releases, altered bundles and draft-only uploads. These execute the workflow's JavaScript with controlled GitHub responses; they do not publish a release.
- Rust's 13 tests and the local Windows release builds passed. A fresh independent review found stale setup addresses, missing short-discovery history gaps, and a retired macOS runner image; those were addressed with regression checks and the move to macos-15.
- [The first expanded hosted run passed all eight jobs](https://github.com/willherr72/Tailwatch/actions/runs/41708908762) at code commit `88b0352`: native builds/tests, packaging, package checks on all three platforms, and a cold start of the packaged Windows viewer with its local agent. Signing remains unconfigured by the user's choice; physical sleep/reboot testing remains as documented below.

## Executed checks

- Rust: 13 contract, collection freshness, address policy, identity lease, and HTTP authorization tests; formatting and Clippy with warnings denied.
- Viewer core: 46 tests covering parsing, nullable/zero values, freshness, network switching, settings, transient discovery recovery, malformed responses, bounded startup grace, and independent polling with a 48-peer slow fleet. Saved-account tests cover Unicode labels, distinct IDs for shared login names, malformed account lists, rejected command-like IDs, and per-tailnet reporting-filter persistence. Local-agent round-trip tests reproduce and prevent a 30-second recovery delay after a transient identity mismatch, refused connection, or `tailscale_unavailable` response; wrong-identity readings remain rejected and the Connecting grace is bounded.
- Native Windows UI: row rendering, expansion/collapse, search, and GPU sorting passed the automated smoke check. A captured panel was visually inspected.
- Reporting filter and account controls: native smoke checks cover hiding offline/unavailable/stale machines, recovery into the filtered list, restoring all rows, saved-account selection by ID, disabled switching during a pending command, offline account selection, and the management action after account-list failure. A controlled child executable exercises actual process arguments, UTF-8 account output, switch success, nonzero exit, and cancellation without changing the real network connection. The installed client's saved-account JSON was inspected; a real cross-account switch remains a manual acceptance check.
- Subsequent account-switch diagnosis: a live CLI round trip between two saved accounts returned healthy loopback snapshots with matching active node identity after each switch and restored the original account. It did not reproduce the brief transition failure; deterministic regression tests reproduced the delayed recovery from that failure and verified the fix. The live Rust-to-C# check also passed with fresh CPU/RAM and NVIDIA GPU status `ok`.
- Local integration: actual Tailscale discovery, the local Rust HTTP endpoint, and C# protocol validation produced fresh CPU/RAM data. Only this computer was used for the live integration test.
- NVIDIA hardware: an RTX 5070 Laptop GPU with driver 581.80 returned `Unknown` from a long-lived NVML session while a fresh session returned valid idle readings. Recovery now retries once with a fresh session within the sample; 20 consecutive two-second observations then returned valid measured zero and the live Rust/C# check reported GPU status `ok`. Failed NVML initialization backs off for 30 seconds. Sustained compute load and driver replacement remain untested.
- Native CI: Windows x64, Ubuntu 22.04 x64, and macOS 14 arm64 built and passed Rust tests/Clippy. Windows CI also passed viewer tests, native UI smoke, and self-contained publishing.
- Installer validation: PowerShell parsing and Bash syntax checks, plus the live Mac installation described below.
- Subsequent Mac deployment: installed the per-user launch agent on an M4 Pro Mac mini running macOS 26.5. Verified background startup, restrictive file permissions, loopback/tailnet-only listeners, and fresh CPU, RAM, and Apple GPU readings received by the Windows viewer machine. macOS Tailscale's bundled executable requires `TAILSCALE_BE_CLI=1` in the launchd environment; the installer now sets it explicitly.
- Independent source review: three recovery/scheduling findings were fixed with regression tests. Review is not evidence of physical hardware testing.

## Mac mini lifecycle and load checks

Additional checks on an Apple M4 Pro Mac mini running macOS 26.5:

- Forced agent termination: launchd restarted the agent automatically, with a new boot ID and advancing fresh readings.
- LaunchAgent unload/reload: bootout followed by bootstrap started collection again. This verifies job loading, not an actual logout/login or system reboot.
- CPU: an eight-worker, 25-second workload was reflected in CPU readings. Every snapshot in the load phase was fresh.
- RAM: a touched, nonrepeating 512 MiB allocation was held for 25 seconds. Reported memory usage rose during the allocation and returned near the preceding level after release.
- GPU: a 25-second Metal compute workload reached measured full GPU activity, and readings returned to the preceding idle range afterward. Every snapshot in the load phase was fresh.
- The first GPU sample after each agent restart was explicitly marked as warming up. Subsequent GPU samples became valid; CPU/RAM collection continued independently.
- Tailscale down/up: during an eight-second disconnect, the agent returned `tailscale_unavailable`. After reconnection it resumed fresh, advancing snapshots with the original node identity.
- No macOS thermal or performance warning was reported. Temporary workload files and the test SSH tunnel were removed, and the agent was left running.

The Windows viewer changed to a different tailnet during setup. The first direct-address test run therefore could not reach the Mac and is not counted as a pass. The completed checks used an SSH tunnel over the local network to the Mac's loopback endpoint, preserving the user's active Windows connection. This exercises collection, agent lifecycle, and the Tailscale identity gate; it is not an end-to-end test of the Windows panel over Tailscale during that disconnect.

Full sleep/wake, actual reboot/sign-in, and a multi-hour soak remain pending. An unattended timed wake could not be arranged with the available administrator access.

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
