# Tailwatch failure recovery

The user approved a reliability pass after local monitoring stopped. Windows recorded an access violation in NVIDIA's `nvml.dll` inside `tailwatch-agent.exe`; the viewer stayed up without restarting the missing helper. Windows also recorded repeated-disposal exceptions during viewer shutdown. These are observed failures, not inferred from the screenshot alone.

## Goals and acceptance

- A GPU worker crash or hang must leave CPU/RAM collection and the HTTP endpoint alive. GPU values become unavailable rather than remaining falsely live.
- A viewer-owned local agent exit must trigger bounded, delayed automatic retries. No duplicate helpers or interference with an independently installed service. Shutdown prevents further retries and releases owned child processes.
- Repeated disposal and quitting with requests in flight must not throw, resurrect the helper, or leave owned children behind.
- Preserve last-success time and a useful failure reason in the viewer. Distinguish old samples, connection failures, authorization problems, and GPU-worker failure without displaying private inventories in logs.
- Verify real child-process crash/hang recovery, repeated shutdown, stale-to-live recovery, and existing network generation isolation. Record precisely which physical sleep/wake and multi-hour checks have actually run.

## Approach

Restarting the whole agent alone restores readings after a crash but interrupts CPU/RAM on every GPU fault. Catching managed/Rust errors in the current GPU thread cannot contain a native access violation. Use an isolated NVIDIA worker process plus a local-agent supervisor. This implements the approach proposed to and accepted by the user. Exact worker transport and retry constants below are implementation choices.

Keep one packaged Rust executable: an internal GPU-worker command performs NVML calls and exchanges bounded JSON over redirected pipes. The agent supervises it outside the CPU/RAM sampler, detects missing output and process exit, kills/reaps failed workers, and retries at a capped rate. Worker lifetime is tied to its parent's pipe; use Windows hidden-process flags. macOS collection and the network protocol stay compatible.

The C# viewer gets a focused local-agent supervisor with serialized start/stop decisions, per-attempt cancellation, and delays between retries. Only terminate processes it started. Treat an occupied port as externally managed; never launch competing helpers. A stalled owned helper should recover after a bounded health deadline, without confusing Tailscale disconnection or identity transition with an agent hang. Reset retry delay after a sustained healthy interval, not immediately after process creation.

Make monitor and tray-context disposal idempotent and safe with pending async continuations. Retain the failure reason independently of snapshot availability; old readings remain unavailable, with last-success time and recovery information in machine details/tooltips. Keep diagnostics bounded and log transitions rather than every failed poll.

## Constraints

No automatic driver changes, privileged service installation, firewall changes, new telemetry, or protocol/schema changes. No false zero metrics. Keep startup, icon, history, tailnet switching, and other existing features. Do not publish a release until the user requests it and the documented reliability gates are met. Physical reboot/sleep tests that interrupt the user's machine and a multi-hour soak cannot be claimed from fast automated tests.
