# Tailwatch

A small Windows system tray app for checking CPU, GPU, and RAM usage across your Tailscale machines.

Click the tray icon to open a compact panel. Search or sort machines, expand a row for GPU and memory details, and use **Reporting only** to hide machines that are not sending fresh Tailwatch metrics. Hidden machines keep being checked and reappear when they report again.

Expanded rows also show **15-minute CPU/GPU/RAM history**. It is kept only in this viewer's memory, starts collecting when the app runs, and clears when you disconnect or change tailnets. Missing or stale readings leave gaps instead of appearing as zero. The time and utilization scales stay fixed; opening a new graph does not stretch a few samples into a full history. Background sampling remains less frequent while the panel is closed.

## First run on Windows

1. Put `Tailwatch.exe` and `tailwatch-agent.exe` in the same folder.
2. Connect Tailscale and run `Tailwatch.exe`.
3. Click the green bars icon beside the clock. Windows may initially put it in the tray overflow menu.
4. Use **Machines** to select devices and change an agent's port. Right-click the tray icon for startup and quit options.

For another machine, select it in **Machines → Set up selected machine…**, or right-click the tray icon and choose **Set up an agent…**. Select Windows x64, Linux x64, or Apple silicon; download and extract the ZIP on that machine, then copy the generated install command. It includes this viewer's current Tailscale addresses. Setup does not run remote commands automatically and invalidates its command if the active tailnet changes. Windows service installation needs an elevated PowerShell; Linux's packaged system service needs sudo; the Mac installer runs as the signed-in user.

**Update an existing agent** gives commands that replace its binary while preserving configuration. These commands apply to installations made by the packaged installers; they do not enroll a new viewer or migrate a custom per-user Linux installation. Unix packaged installers use port 48173; setup directs custom-port installations to the manual configuration instructions. Setup currently links to the published, protocol-compatible **v0.1.0 agents**, rather than an unpublished download.

**Downloads and updates…** opens official releases for manual updates. Exit the Windows tray app before replacing its files, and keep the viewer and agent together. Builds remain unsigned until real signing credentials and verification are configured.

The viewer starts a local-only agent automatically if no agent is listening on port 48173. This child exits when you quit the viewer. Installing a Windows service is needed only if this computer must report to other viewers even when the tray app is closed. Quit the viewer before installing that service.

Settings and a bounded diagnostic log live in `%LOCALAPPDATA%\Tailwatch`. Machine selections, ports, sort preference, and the reporting filter are kept separately for each Tailscale identity. The app runs without administrator access.

When Tailscale is disconnected, use **Open Tailscale** to launch its Windows app, connect there, and return to Tailwatch. Discovery resumes automatically; **Retry** checks immediately. The disconnected panel hides machine controls until a network is available.

## Multiple tailnets

Tailwatch follows the **currently active account** in the installed Tailscale client. Click the tailnet name beneath the title to choose a saved Tailscale account. This switches the connection for the whole computer. The current account is checked; **Manage accounts in Tailscale…** opens Tailscale for sign-in or account management. The dropdown is also available while disconnected.

Switching accounts clears old readings and starts monitoring the newly visible machines. In-flight responses from the old network are discarded. Saved preferences return when you switch back. If a switch fails or times out, Tailwatch reports the problem and rechecks the actual active connection.

A temporary discovery error keeps the last known machine list and expanded rows visible, pauses new requests, and lets cached readings expire. An explicitly disconnected account clears the list. Discovery runs independently of slow remote requests.

The ordinary Tailscale client connects to one account at a time. This version does not show several separate tailnets simultaneously. Simultaneous aggregation would need separate network clients or a collector in each tailnet.

An agent's remote configuration is tied to the node identity active when it was configured. If that machine switches to another tailnet, remote serving stops until the configured identity returns. Re-enrollment with a new node ID requires new configuration. The viewer's local-only helper follows its active local identity.

For build artifacts, repeatable release ZIPs, and the manual draft-prerelease workflow, see [distribution and CI](docs/distribution.md).

## Devices without an agent

Devices are discovered through Tailscale. Desktop/server peers are monitored by default; unsupported device types can be enabled manually.

A reachable Tailscale device without a usable Tailwatch endpoint displays **Agent unavailable**, with dashes for metrics. This can also mean the agent is stopped or a firewall blocks the port. It does not block other devices. Use **Machines** to disable devices you do not want to monitor.

Other states include **Offline**, **Access denied**, **Connecting**, **Stale**, and **Incompatible agent version**. Unknown or unsupported metrics are never displayed as measured zero. Failed remote peers back off for 30 seconds; **Refresh** retries immediately. This computer's loopback agent is retried on the next normal update so brief tailnet-switch interruptions recover promptly. Its explicit Tailscale transition response gets up to ten seconds of **Connecting** before an unavailable status is shown.

The agent's explicit startup response gets a ten-second grace period. Other failed responses, including unrelated services on the same port, back off normally.

## Install remote agents

Install Tailscale and connect it first. NVIDIA machines need their normal NVIDIA driver; a separate CUDA Toolkit is not needed. Copy your viewer's Tailscale address from **Machines**. The examples below use a fictional address.

Agents listen only on loopback and their own verified Tailscale addresses, default TCP port **48173**. They accept only explicitly allowed Tailscale peer addresses. No public/LAN wildcard listener, Tailscale API key, SSH password, or central server is used.

### Windows x64

Extract the Windows agent artifact. In an elevated PowerShell:

```powershell
.\install-windows.ps1 -AllowPeer '100.64.0.10'
```

The installer creates the `TailwatchAgent` service running as LocalService, with an inbound firewall rule limited to the specified peers. It preserves existing policies rather than overwriting them. To uninstall, run `uninstall-windows.ps1` elevated; configuration and binaries are retained.

### Linux x64

On a systemd host, extract the Linux artifact and run:

```sh
sudo bash install-linux.sh ./tailwatch-agent 100.64.0.10
```

The service uses an unprivileged `tailwatch` account. Permit TCP 48173 from the viewer's Tailscale address if a host firewall blocks it. Build artifacts use Ubuntu 22.04's glibc baseline.

To uninstall the service:

```sh
sudo systemctl disable --now tailwatch-agent
sudo rm /etc/systemd/system/tailwatch-agent.service
sudo systemctl daemon-reload
```

The binary, service account, and configuration remain for manual removal or later reuse.

### Apple silicon macOS

Extract the Mac artifact. Run as your regular user:

```sh
bash install-macos.sh ./tailwatch-agent 100.64.0.10
```

Monitoring starts at sign-in and stops when you sign out. It runs without sudo. macOS may require approval for an unsigned downloaded binary; public signing/notarization is not included in this initial version.

Uninstall with:

```sh
launchctl bootout "gui/$(id -u)" "$HOME/Library/LaunchAgents/com.tailwatch.agent.plist"
rm "$HOME/Library/LaunchAgents/com.tailwatch.agent.plist"
```

Configuration and the executable remain under `~/Library/Application Support/Tailwatch`.

### Manual configuration and updates

```sh
tailwatch-agent configure --output agent.json --allow-peer 100.64.0.10
tailwatch-agent run --config agent.json
```

Repeat `--allow-peer` for additional viewing machines or IPv6 addresses. Use `--port` when configuring a nondefault port, and set that same port in the viewer. `configure` refuses to overwrite existing files.

To update, stop the service/job, replace only the executable, and restart it. Keep the configuration. Restart the Windows viewer after replacing its files. There is no auto-updater.

If your tailnet policy blocks the connection, a narrow grant can permit it. Adapt this example to your existing policy; do not replace your policy with it:

```json
{"src":["100.64.0.10"],"dst":["100.64.0.20"],"ip":["tcp:48173"]}
```

## What the readings mean

- **CPU:** total machine utilization normalized to 0–100%, not a single process's percentage.
- **RAM:** OS-aware used physical memory divided by total memory. OS accounting differs; this is not memory pressure.
- **GPU:** GPU active time. For multiple GPUs the row shows the busiest available GPU; expanded details list each device. An asterisk marks partial readings.
- **NVIDIA memory:** dedicated video memory used/total, separate from GPU activity.
- **NVIDIA recovery:** transient NVML session failures trigger one fresh-session retry per sample. Failed driver initialization backs off for 30 seconds; unreadable counters remain unavailable.
- **Apple silicon:** GPU active residency from macmon's IOReport access. CPU and GPU share system memory, so no separate VRAM capacity is invented.
- **AMD:** CPU/RAM monitoring works; GPU collection is not implemented in this version.

Collectors sample every two seconds. The viewer polls every two seconds while open and every fifteen seconds while closed. Data older than ten seconds is marked unavailable/stale. No history or telemetry is uploaded to an external service.

Apple GPU monitoring uses private macOS interfaces and needs verification on each supported OS/chip combination. Native compilation and protocol tests are not proof of real GPU readings on another machine.

## Build and test

Windows requires the .NET 10 SDK, Rust 1.97.1, and Visual Studio C++ Build Tools. `scripts/build.ps1` tests and packages the Windows viewer and agent into `dist/windows`.

```powershell
.\scripts\build.ps1
```

If space is limited, set `CARGO_TARGET_DIR` to a build directory on another drive. The source can remain where it is.

On macOS/Linux, build the native agent with Rust:

```sh
cargo test --manifest-path agent/Cargo.toml --locked
cargo build --manifest-path agent/Cargo.toml --release --locked
```

Additional checks:

```sh
cargo fmt --manifest-path agent/Cargo.toml -- --check
cargo clippy --manifest-path agent/Cargo.toml --all-targets --locked -- -D warnings
dotnet test tests/Tailwatch.Tests
dotnet run --project tests/Tailwatch.UiSmoke
```

The last command is Windows-only and checks native row rendering, expanding, searching, and sorting. GitHub Actions builds Windows x64, Linux x64, and macOS arm64 agents plus the Windows viewer. Download matching artifacts from a successful workflow run and put the Windows agent next to the viewer.

With Tailscale connected and a local agent listening, `dotnet run --project tests/Tailwatch.LocalSmoke` verifies real discovery and CPU/RAM readings across the Rust/C# boundary. See [validation notes](docs/validation.md) for executed checks and hardware limitations.

The protocol example in `protocol/snapshot.example.json` contains fictional values. Private configuration, machine inventories, build outputs, and local test captures are excluded from Git.

## Project

- `agent/`: Rust collection, restricted HTTP transport, and Windows service entry point.
- `viewer/Tailwatch.Core/`: discovery, polling, validation, freshness, and per-tailnet settings.
- `viewer/Tailwatch.App/`: native Windows tray interface.
- `scripts/`: packaging and install/uninstall instructions.
- `tests/`: viewer tests and native UI smoke checks.

MIT licensed. Built using [sysinfo](https://github.com/GuillaumeGomez/sysinfo), [nvml-wrapper](https://github.com/Cldfire/nvml-wrapper), [macmon](https://github.com/vladkens/macmon), and .NET Windows Forms.
