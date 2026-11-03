#!/usr/bin/env bash
set -euo pipefail
# Usage: sudo bash install-linux.sh ./tailwatch-agent VIEWER_TAILSCALE_IP [MORE_IPS...]
if [[ ${EUID} -ne 0 || $# -lt 2 ]]; then echo "Usage: sudo bash $0 AGENT_BINARY VIEWER_TAILSCALE_IP [MORE_IPS...]" >&2; exit 1; fi
agent=$(realpath "$1")
shift
if systemctl cat tailwatch-agent.service >/dev/null 2>&1 || [[ -e /etc/tailwatch/agent.json ]]; then
  echo "Existing installation or config found. Stop the service and replace its binary to update." >&2; exit 1
fi
args=()
for peer in "$@"; do args+=(--allow-peer "$peer"); done
install -d -m 0755 /usr/local/lib/tailwatch
install -m 0755 "$agent" /usr/local/lib/tailwatch/tailwatch-agent
if ! id tailwatch >/dev/null 2>&1; then useradd --system --no-create-home --shell /usr/sbin/nologin tailwatch; fi
install -d -m 0750 -o root -g tailwatch /etc/tailwatch
/usr/local/lib/tailwatch/tailwatch-agent configure --output /etc/tailwatch/agent.json "${args[@]}"
chown root:tailwatch /etc/tailwatch/agent.json
chmod 0640 /etc/tailwatch/agent.json
cat > /etc/systemd/system/tailwatch-agent.service <<'UNIT'
[Unit]
Description=Tailwatch host metrics
After=network-online.target tailscaled.service
Wants=network-online.target
[Service]
Type=simple
User=tailwatch
Group=tailwatch
ExecStart=/usr/local/lib/tailwatch/tailwatch-agent run --config /etc/tailwatch/agent.json
Restart=on-failure
RestartSec=5
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
PrivateTmp=true
StandardOutput=null
StandardError=journal
[Install]
WantedBy=multi-user.target
UNIT
systemctl daemon-reload
systemctl enable --now tailwatch-agent.service
echo "Installed. Permit TCP 48173 from your viewer's Tailscale IP if a host firewall blocks it."
echo "Uninstall: sudo systemctl disable --now tailwatch-agent; sudo rm /etc/systemd/system/tailwatch-agent.service; sudo systemctl daemon-reload"
echo "Configuration and executable are retained until you explicitly remove them."
