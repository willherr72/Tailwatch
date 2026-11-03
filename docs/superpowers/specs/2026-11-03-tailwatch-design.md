# Tailwatch implementation design

Tailwatch gives William a quick view of how busy his Windows, Linux, and Apple silicon machines are. A Windows system tray icon opens the approved compact panel, showing CPU, GPU, and RAM utilization without opening a browser or connecting to each machine manually.

Status: proposed implementation design. The tray mockup and dedicated agent approach have been accepted; the implementation choices below are proposed defaults.

## Agreed requirements

- Windows is the viewing platform. Monitored machines run Windows, Linux, or macOS on Apple silicon.
- Show live CPU, GPU, and RAM utilization so the user can judge available capacity.
- NVIDIA and Apple silicon GPU monitoring are required. AMD is desirable but does not block the first version.
- A small dedicated background service on each monitored machine is acceptable.
- Communication uses the existing tailnet, directly between the viewer and agents, without a central monitoring server.
- Preserve the approved layout in [the interactive mockup](../../../mockups/tray-panel.html): compact rows with three meters, expandable machine details, search, sorting, and a tray toggle. The mockup's names and readings are fictional.

## Approaches and choice

We discussed reading metrics through SSH versus installing a dedicated agent. SSH could reuse existing remote access but would require ongoing command execution and platform-specific remote shell handling. The user accepted the dedicated agent approach for lightweight continuous collection.

Reusing a complete monitoring system would provide history and dashboards but introduce services and interfaces unnecessary for this live tray view. Tailwatch will reuse existing metric libraries inside its own small agent instead.

## Scope and limits

The first version includes the tray viewer, collectors, direct transport, machine discovery, initial configuration, and documented install and uninstall steps for each platform. It is read-only: no process termination, remote commands, or machine control.

Long-term history, alerts, temperature and power monitoring, mobile clients, macOS viewer UI, AMD collectors, public hosting, signed public distribution, and automatic updates are outside the first version. Small visual changes for native Windows controls and accessibility are left to the implementer.

Misleading zeros, stale readings presented as live, exposed LAN listeners, and high monitoring overhead would make this a failure even if the panel looks correct.

## Proposed implementation choices

These are the assistant's recommendations, not language preferences expressed by the user:

- A C# Windows Forms viewer on a supported .NET LTS release, using NotifyIcon for the tray and a custom compact panel. Package it self-contained so the user does not need a separate .NET runtime.
- A Rust agent shared across platforms, with platform-gated collectors. Use sysinfo for host CPU and memory, nvml-wrapper for NVIDIA, and the macmon library for Apple silicon GPU activity. Avoid subprocess launches on each sample.
- A versioned, bounded HTTP JSON response over the existing encrypted Tailscale connection. No database or central service.
- One source workspace containing viewer, agent, protocol fixtures, packaging scripts, and documentation. Pin dependencies and choose supported versions during implementation.

The current workspace contains only the mockup and is not a Git repository. Tailscale is installed; .NET and Rust tools were not found on PATH. Implementation must verify installed SDK locations and prepare the required toolchains before building. Native macOS builds and hardware verification require a Mac; Linux and Windows agents also need their own platform checks.

## Viewer behavior

Left-clicking the tray icon toggles the panel near the icon on the current monitor. Escape and clicking outside close it. Right-click offers Open, Start with Windows, and Quit. Closing the panel leaves monitoring active; Quit exits the viewer.

Show the local machine alongside remote peers when its agent is configured. Search matches names, OS, and GPU names. Sorting by CPU, GPU, or RAM places available values in descending order, with unavailable and disconnected rows last. Name is the initial sort. Preserve expanded rows and the user's search during refreshes.

Each row has CPU percent, GPU percent, and RAM percent. The GPU summary is the highest valid utilization among that machine's GPUs; expanded details show every detected GPU, its activity, and NVIDIA dedicated memory used and total. If only some GPUs have valid readings, mark the summary as partial.

CPU percent is normalized to the whole machine, with 100 percent meaning all logical CPUs busy over the interval. RAM uses the OS-aware collector's used-memory value divided by physical total; details show used and total bytes. Display units consistently as GiB. Explain that OS memory accounting differs and this measures occupied memory, not memory pressure.

Apple silicon uses unified system memory; do not manufacture a separate dedicated VRAM capacity. GPU percentage represents the collector's GPU activity or active residency, not a cross-machine performance score. Missing hardware support, sampling errors, and unknown values display a dash with a short explanation.

Use the mockup's restrained meters and readable numeric values. Color is a secondary cue: below 75 percent uses the normal accent, 75–89 uses amber, and 90 or above uses red. These are display thresholds, not alerts. Provide keyboard navigation, accessible names, DPI scaling, and a scrollable list.

## Collection and freshness

Collect locally every two seconds, independently of network requests. Keep only the latest snapshot. CPU needs two readings before reporting a valid value. The HTTP handler returns cached metrics without blocking on a GPU sampler. GPU failure must not stop CPU, RAM, or other GPUs.

While the panel is open, request snapshots every two seconds. While closed, request every fifteen seconds. Reopening triggers an immediate request. Bound concurrent requests to eight, give each request a two-second timeout, and avoid overlapping requests to the same host.

Transport receipt time alone is insufficient for freshness. Include a random boot identifier, incrementing snapshot sequence, monotonic sample age, and per-collector age/status. The viewer tracks elapsed time locally; wall-clock differences across machines must not make stale data fresh. Mark a collector unavailable if its sample age exceeds ten seconds, even if the agent is responding.

Connection states:
- Connecting: no successful reading yet.
- Live: agent responds with fresh measurements.
- Agent unavailable: the tailnet peer exists but no authorized agent response is available.
- Offline: Tailscale reports the peer offline.
- Stale: previously valid metrics cannot be refreshed or the collector has stopped advancing.
- Incompatible: the response schema major version is unsupported.

On a failed request, immediately stop presenting old figures as live. A last-successful timestamp may remain, but old meters are muted and marked stale. Unsupported collectors remain distinguishable from disconnected machines. Back off unavailable peers to at most one probe per thirty seconds; opening the panel permits one immediate retry.

## Discovery and initial configuration

Use the installed Tailscale CLI's JSON status, with a bounded process timeout, to discover the local node and visible peers. Refresh the directory every sixty seconds. Parse only the needed fields, tolerate unknown fields, and keep the last good directory on a transient CLI failure. Never log the full status payload.

Discovery only covers machines visible to this node under the tailnet's access policy. A device appearing in Tailscale does not imply that Tailwatch is installed. Offer a Machines setup view listing discovered peers, with a per-machine monitor checkbox and connection status. Enable the local node by default; remote nodes are selected during setup. Automatically retain selected nodes across restarts using their Tailscale node identifiers, not display names.

The viewer stores its machine selections, sort, and startup setting locally. Agent configuration contains its expected local Tailscale node identity, listening port, and allowed monitoring peer addresses. Default port: TCP 48173, configurable per machine. If a port is occupied, show a specific configuration error rather than silently selecting another.

The setup instructions provide commands for installing an agent and specifying the viewing machine's Tailscale address. No Tailscale API key, SSH credential, or new cloud login is needed.

## Transport and access control

An agent listens only on its own verified Tailscale IPv4 and/or IPv6 addresses. It never falls back to 0.0.0.0, ::, or a LAN address. It checks direct socket source addresses against an explicit allowlist of the monitoring node's Tailscale addresses; an empty allowlist denies remote access. Do not trust forwarded headers. Loopback access for the local viewer is explicitly allowed on a separate loopback binding.

Tailscale provides peer authentication and encrypted transport; application source restrictions narrow access to configured monitoring nodes. Existing tailnet policy and host firewalls must also permit the connection. Document narrow firewall rules and a sample Tailscale rule, without automatically modifying the user's tailnet policy.

Revalidate the local Tailscale interface and identity periodically. Stop serving on addresses that no longer belong to the configured node, and fail closed if the identity cannot be established. Rejoining with a new identity or replacing the monitoring machine requires updating the configuration.

The service offers only read-only health and snapshot endpoints; no remote configuration or executable commands. Limit response size, connections, and request rate. The viewer validates finite 0–100 percentages, nonnegative sizes, supported versions, and identifiers before displaying data. Error responses expose only the diagnostic information needed for setup.

## Protocol contract

GET /v1/snapshot returns:
- Schema version, agent version, configured node ID, hostname, and OS.
- Boot ID, snapshot sequence, sample interval, sample age, and optional UTC sample timestamp for diagnostics.
- CPU: optional utilization percentage, status, age, model, logical core count.
- RAM: optional used and total bytes, status, age, and accounting source.
- GPUs: stable device identifier, name, vendor, optional utilization percentage, metric meaning, status, age, and optional dedicated memory used/total.
- Per-collector short error codes such as unsupported, warming_up, permission_denied, or collection_failed.

Null means unavailable; zero means a valid measured zero. A successful HTTP response may contain partially unavailable metrics. Unknown optional fields are ignored; a different major schema version is rejected. Preserve stable GPU identity across enumeration order changes.

GET /v1/health distinguishes a running agent from a stopped sampler. It obeys the same access restrictions.

## GPU compatibility

NVIDIA collection uses the installed driver library, loaded once per collector lifetime and retried with backoff if unavailable. Query supported fields independently. Unsupported NVML calls must not invalidate other fields. No CUDA Toolkit installation is required merely to use the driver library.

On Apple silicon, create macmon's sampler on its own worker thread because sampling blocks. Use GPU active residency rather than the frequency-scaled ratio. Do not request unused power, process-list, or temperature features when the library permits this. macmon uses private macOS interfaces, so verify the chosen version on the user's hardware and OS before claiming Mac support. Detect unavailable sampling channels; do not treat failed or absent counters as measured zero.

Retain an internal GPU collector interface so AMD can be added later. First-version AMD hardware must retain CPU/RAM monitoring and show GPU monitoring as unsupported.

## Packaging and operation

Produce Windows x64 viewer and agent artifacts, Linux x64 agent artifacts, and macOS arm64 agent artifacts. Other CPU architectures are follow-up work.

Windows agent: Windows service, with documented install, start, stop, and uninstall commands. Linux agent: systemd service under a dedicated unprivileged user. macOS agent: per-user launchd agent starting on login for the first version, with uninstall instructions; pre-login monitoring is out of scope. Installation may need elevation, but routine collection should not require an administrator session.

The Windows viewer runs in the user's session and can opt into startup at sign-in. Logs are bounded and omit credentials, full tailnet inventories, and unnecessary device metadata. Keep configuration across binary replacement. Provide explicit manual update instructions; no auto-updater.

Unsigned local development builds are acceptable for the prototype. Public signing and notarization require a separate distribution decision and are not silently promised by this design.

## Delivery and acceptance

1. Establish the snapshot contract and a Windows collector, then render real local CPU, RAM, and NVIDIA readings in the approved tray layout.
2. Add the Apple silicon collector and verify a real Windows-to-Mac connection.
3. Verify Linux NVIDIA collection, installation paths, peer discovery, startup, and recovery across all supported platforms.

Do not describe cross-platform monitoring as complete until native hardware checks are done. If a platform is unavailable, deliver reproducible artifacts and setup instructions and identify exactly which checks remain.

Required verification:
- Protocol fixtures cover valid zero, unknown values, partial GPUs, invalid values, schema mismatch, stale collectors, and machine reboot.
- Test source-address filtering and failure to bind safely when Tailscale is absent or changes identity.
- Verify discovery timeout and malformed JSON recovery, per-peer request bounds, and offline/online transitions.
- Check idle and loaded CPU/GPU readings against native tools with aligned sampling windows; exact agreement with differently sampled OS UIs is not required.
- Exercise multi-GPU enumeration and partial support; use fixtures when a dual-GPU machine is unavailable and disclose that limit.
- Measure five minutes of normal collection. Initial budgets: under 1 percent of one logical CPU on average and under 75 MiB resident memory per agent; idle viewer under 1 percent of one logical CPU and under 150 MiB resident memory. These are targets to measure, not current guarantees.
- Verify no public or LAN listening sockets, service restart, uninstall, tray show/hide, search/sort, scrolling, keyboard use, and high DPI.

## Decisions left to implementation

Choose exact supported dependency versions, native rendering details, platform packaging mechanics within the behavior above, error-code names, and test frameworks. Confirm minimum supported OS versions against the user's actual targets during setup. These decisions should preserve the agreed behavior; changes to first-version scope or collection privileges must be surfaced.

## References

- [Windows Forms NotifyIcon](https://learn.microsoft.com/en-us/dotnet/desktop/winforms/controls/notifyicon-component-windows-forms)
- [sysinfo library](https://docs.rs/sysinfo/latest/sysinfo/)
- [NVIDIA monitoring API](https://docs.nvidia.com/deploy/nvml-api/latest/index.html)
- [nvml-wrapper dynamic loading](https://docs.rs/nvml-wrapper/latest/nvml_wrapper/)
- [macmon sampling library](https://docs.rs/macmon/latest/macmon/)
- [macmon platform limitations](https://github.com/vladkens/macmon)
- [Tailscale CLI](https://tailscale.com/docs/reference/tailscale-cli)

