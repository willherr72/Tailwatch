# Tailwatch Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax for tracking.

**Goal:** Ship a Windows tray monitor and native agents for CPU, GPU, and RAM over the active Tailscale network.
**Architecture:** A Rust process samples into a cached snapshot and serves it only on validated addresses to allowed peers. A C# WinForms viewer discovers peers and polls snapshots, keeping settings and in-flight work isolated per tailnet.
**Tech Stack:** Rust, sysinfo, nvml-wrapper, macmon, Tokio/Axum, C#/.NET 10 Windows Forms.
**Spec:** ../specs/2026-11-03-tailwatch-design.md

## Global Constraints
- TCP port 48173 by default; no LAN or wildcard listeners.
- Two-second sampling/open polling, fifteen-second closed polling, two-second request timeouts, maximum eight parallel requests.
- NVIDIA and Apple silicon required; AMD optional and shown as unsupported initially.
- No fabricated zero values or stale readings presented as live.
- Active tailnet only; clear prior snapshots and cancel prior requests on an identity change.
- Missing agents do not block other machines.
- Local project goes in Desktop/code/Tailwatch. Public GitHub repository creation is authorized.

## Review Focus
- A response from the previous tailnet arriving after a switch must be discarded (Task 3).
- A dead sampler behind a live HTTP endpoint must become stale (Tasks 1 and 3).
- A peer with no agent, HTML on the port, redirects, or a huge response must not freeze or fool the viewer (Task 3).
- Allowlist parsing, mapped IPv6, and Tailscale disappearance must fail closed (Task 2).
- macOS private counters and unsupported NVIDIA calls must not produce fabricated zero readings (Task 2).

### Task 1: Snapshot contract and invariants
Files: agent/Cargo.toml, agent/src/model.rs, agent/src/lib.rs, agent/tests/contract.rs, protocol/snapshot.example.json.
Produces: Snapshot, Reading, MemoryReading, GpuReading serializable records; validate_snapshot; Reading::fresh.
- [ ] Write tests for valid zero, missing values, out-of-range percentages, monotonic age, and partial GPUs.
- [ ] Run cargo test; observe failing assertions against initial stubs.
- [ ] Implement nullable metric records and snapshot validation with per-field status/age.
- [ ] Run cargo test and commit the protocol.

### Task 2: Agent collection and restricted serving
Files: agent/src/collect.rs, agent/src/network.rs, agent/src/server.rs, agent/src/main.rs, agent/src/service.rs, agent/tests/security.rs.
Consumes: Task 1 model.
Produces: agent sample (one JSON snapshot), agent run --config path, agent service --config path (Windows SCM).
- [ ] Write tests for exact-address policy, mapped IPv6, unauthorized peers, invalid bind addresses, identity changes, age growth, and partial collection.
- [ ] Run cargo test and confirm expected failures.
- [ ] Implement CPU/RAM sampling and platform GPU providers; keep the sampler independent of request handling.
- [ ] Implement cached endpoints, bounded requests, CLI timeout, safe bindings, explicit peer allowlist, and periodic identity revalidation.
- [ ] Run cargo test, fmt/check/clippy, real local sample and HTTP authorization/freshness smoke checks; commit.

### Task 3: Viewer core and network transitions
Files: viewer/Tailwatch.Core/{Models,Tailscale,Monitor,Settings}.cs and csproj; tests/Tailwatch.Tests/{ContractTests,MonitorTests,DiscoveryTests}.cs and csproj.
Consumes: Task 1 JSON contract.
Produces: DirectorySnapshot, Peer, TailwatchMonitor, MachineState, AppSettings; methods RefreshDirectoryAsync, PollAsync, SetMonitored.
- [ ] Test real parsing, zero/null, malformed/oversized replies, partial GPU status, network switching with an old response pending, absent agent, reboot/sequence tracking, stale collector, and persistence per tailnet.
- [ ] Run dotnet test and observe expected failures.
- [ ] Implement discovery CLI timeout, validation, per-profile storage, bounded direct HTTP with no redirects/proxy, and polling generation cancellation.
- [ ] Run dotnet test and commit.

### Task 4: Native tray viewer
Files: viewer/Tailwatch.App/{Program,TrayContext,TrayPanel,MachineCard,SettingsForm,Theme}.cs and csproj.
Consumes: Task 3 monitor state.
Produces: native tray executable with approved panel and initial machine setup.
- [ ] Implement UI around tested core; use native accessible controls and custom meter painting.
- [ ] Support icon toggle, escape/outside dismissal, right-click actions, startup preference, searching, sorting, expanded GPU/memory details, scrolling, and offline/error states.
- [ ] Build and run a bounded native UI smoke check; inspect accessible controls and capture locally if needed (no browser preview).
- [ ] Run core tests and commit.

### Task 5: Packaging, installation and documentation
Files: scripts/{build.ps1,install-windows.ps1,uninstall-windows.ps1,install-linux.sh,install-macos.sh}, .github/workflows/build.yml, README.md, LICENSE, examples/agent.json.
- [ ] Package a self-contained Windows app and native agent, with a tray shortcut and opt-in startup.
- [ ] Supply non-destructive install/uninstall scripts and sample tailnet rules; native build matrix for Windows/Linux/macOS.
- [ ] Validate scripts and package outputs; describe public release prerequisites and unverified native platforms precisely.
- [ ] Run all available tests and build checks; measure local monitoring overhead; commit.

### Task 6: Review and publish
- [ ] Get one independent whole-branch code review, fix important findings with regression tests, and run final suites.
- [ ] Ensure tracked files contain no machine inventory, local credentials, or build artifacts.
- [ ] Create the authorized public GitHub repository and push the reviewed project.
- [ ] Report executable paths, repository link, multi-tailnet behavior, missing-agent behavior, and hardware verification limits.

