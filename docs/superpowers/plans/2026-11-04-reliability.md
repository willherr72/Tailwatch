# Reliability implementation plan

> **For agentic workers:** Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Recover automatically from helper failures and contain NVIDIA native crashes while retaining honest metrics and useful diagnostics.

**Architecture:** Isolate NVIDIA calls in a supervised subprocess of the Rust agent. Supervise the viewer-owned agent separately in C#, with idempotent cancellation/disposal and failure details that survive cached snapshots.

**Tech stack:** Rust/Tokio, .NET 10 WinForms, existing native UI and xUnit tests.

**Spec:** `docs/superpowers/specs/2026-11-04-reliability-design.md`

## Global constraints

- Preserve protocol schema 1, platform package layout, access policy, and user settings.
- Never restart or kill an externally managed service or a process not owned by the supervisor.
- Bound subprocess output, deadlines, and retry frequency; no unbounded worker/thread/process accumulation.
- Every native fault must become unavailable metrics, not measured zero.
- Independent Rust and viewer tasks may run in parallel; they share only the existing HTTP contract.

## Review focus

- Worker emits no output, partial/oversized JSON, or crashes in a native call: CPU/RAM remains live, worker is reaped, retries are bounded (task 1).
- Startup races an occupied loopback port or shutdown during launch: no duplicate/unowned helper termination (task 2).
- Repeated disposal while requests complete: no exception, restart, or leaked handles (task 2).
- Old snapshot followed by denied/refused/stalled response: preserve failure reason and last-success, then clear error on recovery (task 3).
- Real application lifetime differs from standalone control tests: test actual owned child processes and quit path, retaining existing first-show test (task 4).

## Task 1: NVIDIA worker containment

Files: `agent/src/collect.rs`, new `agent/src/gpu_worker.rs`, `agent/src/main.rs`, `agent/src/lib.rs`, Rust worker tests/fixtures.

- [x] Add process-boundary tests for successful output, worker exit, hang, malformed/oversized output, and cleanup; demonstrate failure before implementation.
- [x] Move NVML calls into a hidden internal worker command. Implement bounded pipe transport, output deadlines, exit handling, backoff, and parent-lifetime cleanup.
- [x] Feed existing `GpuCache` so CPU/RAM and HTTP protocol remain independent of GPU failures. Clear invalid GPU data immediately.
- [x] Run Rust tests, fmt, Clippy, and real Windows worker failure checks.

## Task 2: Local helper supervision and safe shutdown

Files: new viewer supervisor in `viewer/Tailwatch.Core/`, `viewer/Tailwatch.App/TrayContext.cs`, `viewer/Tailwatch.Core/Monitor.cs`, new tests/fixture as needed.

- [x] Reproduce repeated disposal and dead-helper non-recovery in tests before fixing.
- [x] Add cancellation-aware serialized process ownership and health supervision, capped retries, occupied-port handling, transition logs, and a useful status property/event.
- [x] Wire supervisor into the actual tray lifetime; dispose once and suppress UI updates/restarts after shutdown. Terminate/reap only owned helpers.
- [x] Test real fake-child exit/restart, hang, rapid repeated failure, shutdown during startup, external listener preservation, and repeated disposal.

## Task 3: Actionable failure details

Files: `viewer/Tailwatch.Core/Monitor.cs`, `viewer/Tailwatch.App/MachineCard.cs`, monitor recovery tests, native UI tests.

- [x] Preserve underlying failure reason independently of stale cached samples, plus existing last-success time.
- [x] Show failure/recovery details in expanded rows and accessible tooltip text; do not stretch stale metrics or display zero.
- [x] Test refusal, timeout, authorization denial, unchanged sequence, and recovery clearing the reason, including tailnet isolation.

## Task 4: Integration and acceptance

Files: focused lifecycle harness or additions to `tests/Tailwatch.UiSmoke/`, `docs/validation.md`, README, this ledger.

- [ ] Run all viewer, native UI, Rust, packaging and workflow checks. Build both executable files together.
- [x] Review the whole change independently and address findings.
- [x] Verify a real worker crash/hang preserves CPU/RAM, a local agent exit recovers, and repeated quit produces no new crash events.
- [x] Install the verified viewer and agent together, restore monitoring, and verify live readings and startup configuration.
- [x] Record actual results and pending multi-hour soak/physical sleep-wake checks. Do not claim release readiness from unit tests alone.

## Execution notes

User approval: the user accepted the five-part reliability proposal with “Lets do it.” Work continues within that scope without another permission gate. Existing T3 worktree is retained; branch `fix/reliability` starts from current `origin/main`.

Implemented in `f1a1039`; all local checks passed. Installed local-agent crash recovery measured 5.33 seconds, clean Quit exit code 0, owned children reaped, startup unchanged. Cross-platform CI and the two-hour observation started at 20:18 UTC remain pending. Physical sleep/wake and reboot checks are not claimed.
