# Setup, history, and distribution implementation plan

**Goal:** Deliver the next iteration requested after v0.1.0: guided agent setup, compact local history, and repeatable tested release packages.

**Architecture:** Keep the Windows WinForms viewer and Rust agent protocol. Collect a bounded 15-minute history in the viewer's existing monitor; draw three graphs in expanded rows. Generate OS-specific setup instructions using the active viewer's validated tailnet addresses. Package existing CI artifacts into tested downloadable bundles and create draft releases only on explicit dispatch.

**Authorization and execution:** User approved proceeding with the proposed scope. Implement routine details directly; no availability notifications or paid signing enrollment. Work on a feature branch in the existing checkout. Packaging/CI is independent and may be delegated under dispatching-parallel-agents; monitor and UI changes stay together. Signing availability is being clarified asynchronously.

## Constraints and review focus

- History is memory-only, at most 451 points per machine over 15 minutes, isolated to the current tailnet and cleared on account change/disconnect. No invented zero readings and no line drawn across missing samples. Hidden-panel cadence remains 15 seconds.
- History must reject repeated sequence numbers and stale metrics; restart and network gaps must not join unrelated readings. Use monotonic time for retention and displayed elapsed times.
- Setup only generates instructions and opens official release pages; it never SSH-installs or executes downloaded scripts. Shell values come from validated IP addresses and enum choices. No machine names become shell code. Recheck the active network before copying instructions.
- Setup targets published compatible v0.1.0 agents until a later agent release exists, names download architectures explicitly, and explains sign-in/admin requirements. Custom ports must be handled accurately or explicitly directed to manual setup.
- Release packages contain only explicitly allowed binaries, installers, README/LICENSE. Verify versions, archive round trips, checksums and architecture. PR builds cannot publish releases or access signing credentials.
- Physical sleep/reboot tests remain manual; do not change tailnet connections or restart remote machines as part of this work.

## Task 1 — History model and UI

Files: `viewer/Tailwatch.Core/History.cs`, `Monitor.cs`, `viewer/Tailwatch.App/HistoryGraph.cs`, `MachineCard.cs`, `tests/Tailwatch.Tests/HistoryTests.cs`, `tests/Tailwatch.UiSmoke/Program.cs`.

- [x] Write failing tests for bounded retention, duplicates, missing metrics, restart gaps, network isolation, hidden polling intervals and clock changes.
- [x] Add `HistoryPoint(TimeSpan Age, double? Cpu, double? Gpu, double? Ram)` and optional history on `MachineState`, using an internal monotonic buffer per entry. Record fresh accepted samples once; failures create gaps; disabling drops retained history.
- [x] Render CPU/GPU/RAM graphs on a fixed 0–100 scale in expanded rows, sharing the 15-minute time axis. Show unavailable/collecting states and accessible summaries. Keep existing card instances and scrolling behavior.
- [x] Run core and UI smoke checks, including expansion and ongoing refresh.

## Task 2 — Guided setup and updates

Files: `viewer/Tailwatch.Core/AgentSetup.cs`, `viewer/Tailwatch.App/AgentSetupForm.cs`, `SettingsForm.cs`, `TrayContext.cs`, new setup tests and UI smoke additions.

- [x] Test command generation for Windows x64, Linux x64 and Apple silicon, exact validated viewer IPs, unsupported architecture, disconnected/stale context and custom-port handling.
- [x] Add a selected-machine setup action to Machines and a tray setup entry usable even for an empty network. Explain download, extraction, command, verification and agent/session lifetime.
- [x] Provide official download links and copyable installation commands, separate install/update instructions, and a manual Downloads & updates action.
- [x] Run core and UI smoke checks. Document setup and history behavior.

## Task 3 — Packaging and CI

Files: new `scripts/package-release.ps1`, package tests, `.github/workflows/build.yml`, new draft-release workflow and distribution docs.

- [x] Combine the Windows agent/viewer artifacts, package Mac/Linux agents, create checksums and verify extracted bytes and architectures.
- [x] Add repeatable package tests, installer syntax checks and packaged agent executable smoke checks on native CI runners. Exercise startup/reconnect behavior with controlled fixtures rather than needing Tailscale credentials.
- [x] Add a least-privilege manual workflow that selects a successful same-repository CI run, packages its artifacts, and creates/updates a draft prerelease for that exact commit. No automatic public release and no signing claims without real credentials.
- [x] Test package failures (missing/extra files, architecture mismatch, invalid version) and run against previously downloaded CI artifacts.

## Completion

- [x] Run integrated core/UI/package tests and release build; obtain an independent review of the whole change.
- [x] Fix material findings with regression checks; update validation and document any signing or physical-test limitations.
- [x] Push the reviewable branch, deliver the updated local build where permitted, and report concrete results without publishing a new release automatically.

## Verified outcome

Code commit: 88b0352. Independent review findings fixed: stale setup commands, missing short-discovery gaps, retired Mac runner. 62 core, 13 Rust, 17 package, 20 workflow tests and native UI checks passed. GitHub run 41708908762 passed all eight jobs including packaged Windows cold startup. v0.2 is installed locally using the app's own Quit action; signing remains unsigned as explicitly requested. No public v0.2 release was created.

