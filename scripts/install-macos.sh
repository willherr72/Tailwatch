#!/bin/bash
set -euo pipefail
# Usage: bash install-macos.sh ./tailwatch-agent VIEWER_TAILSCALE_IP [MORE_IPS...]
if [[ $# -lt 2 || ${EUID} -eq 0 ]]; then echo "Run as your normal user: bash $0 AGENT_BINARY VIEWER_TAILSCALE_IP [MORE_IPS...]" >&2; exit 1; fi
agent="$1"
shift
directory="$HOME/Library/Application Support/Tailwatch"
plist="$HOME/Library/LaunchAgents/com.tailwatch.agent.plist"
if [[ -e "$directory/agent.json" || -e "$plist" ]]; then echo "Existing install found. Stop the job and replace its binary to update." >&2; exit 1; fi
mkdir -p "$directory" "$HOME/Library/LaunchAgents"
chmod 700 "$directory"
cp "$agent" "$directory/tailwatch-agent"
chmod 755 "$directory/tailwatch-agent"
args=()
for peer in "$@"; do args+=(--allow-peer "$peer"); done
TAILSCALE_BE_CLI=1 "$directory/tailwatch-agent" configure --output "$directory/agent.json" "${args[@]}"
chmod 600 "$directory/agent.json"
/usr/bin/plutil -create xml1 "$plist"
/usr/bin/plutil -insert Label -string com.tailwatch.agent "$plist"
/usr/bin/plutil -insert ProgramArguments -array "$plist"
/usr/bin/plutil -insert ProgramArguments.0 -string "$directory/tailwatch-agent" "$plist"
/usr/bin/plutil -insert ProgramArguments.1 -string run "$plist"
/usr/bin/plutil -insert ProgramArguments.2 -string --config "$plist"
/usr/bin/plutil -insert ProgramArguments.3 -string "$directory/agent.json" "$plist"
# The bundled Tailscale executable otherwise selects GUI mode under launchd.
/usr/bin/plutil -insert EnvironmentVariables -dictionary "$plist"
/usr/bin/plutil -insert EnvironmentVariables.TAILSCALE_BE_CLI -string 1 "$plist"
/usr/bin/plutil -insert RunAtLoad -bool YES "$plist"
/usr/bin/plutil -insert KeepAlive -bool YES "$plist"
/usr/bin/plutil -insert ThrottleInterval -integer 10 "$plist"
/usr/bin/plutil -insert StandardOutPath -string /dev/null "$plist"
/usr/bin/plutil -insert StandardErrorPath -string /dev/null "$plist"
launchctl bootstrap "gui/$(id -u)" "$plist"
echo "Installed. Monitoring starts at sign-in. Keep Tailscale running."
echo "Uninstall: launchctl bootout gui/$(id -u) \"$plist\"; rm \"$plist\""
echo "Configuration and executable are retained in: $directory"
