use anyhow::{Context, Result, bail};
use serde::{Deserialize, Serialize};
use std::{net::IpAddr, path::PathBuf, time::Duration};
use tokio::{process::Command, time::timeout};

pub fn normalized(ip: IpAddr) -> IpAddr {
    match ip {
        IpAddr::V6(v) => v.to_ipv4_mapped().map(IpAddr::V4).unwrap_or(ip),
        _ => ip,
    }
}
pub fn is_tailnet(ip: IpAddr) -> bool {
    match normalized(ip) {
        IpAddr::V4(ip) => {
            let o = ip.octets();
            o[0] == 100 && (64..128).contains(&o[1])
        }
        IpAddr::V6(ip) => {
            let s = ip.segments();
            s[0] == 0xfd7a && s[1] == 0x115c && s[2] == 0xa1e0
        }
    }
}
pub fn allowed(ip: IpAddr, peers: &[IpAddr]) -> bool {
    let ip = normalized(ip);
    ip.is_loopback() || (is_tailnet(ip) && peers.iter().any(|peer| normalized(*peer) == ip))
}
pub fn matching_identity(expected: &str, actual: &str, running: bool) -> bool {
    running && !expected.is_empty() && expected == actual
}
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Config {
    pub node_id: String,
    #[serde(default = "default_port")]
    pub port: u16,
    #[serde(default)]
    pub allowed_peers: Vec<IpAddr>,
}
pub fn default_port() -> u16 {
    48173
}
impl Config {
    pub fn validate(&self) -> Result<()> {
        if self.node_id.trim().is_empty() {
            bail!("node_id must be the agent's Tailscale node ID");
        }
        if self.port == 0 {
            bail!("port must be nonzero");
        }
        if self.allowed_peers.iter().any(|ip| !is_tailnet(*ip)) {
            bail!("allowed_peers must contain only Tailscale addresses");
        }
        Ok(())
    }
}
#[derive(Debug, Clone)]
pub struct Identity {
    pub node_id: String,
    pub addresses: Vec<IpAddr>,
}
pub fn executable() -> PathBuf {
    if let Some(path) = std::env::var_os("TAILWATCH_TAILSCALE") {
        return path.into();
    }
    #[cfg(windows)]
    {
        let base = std::env::var_os("ProgramFiles").unwrap_or_else(|| "C:\\Program Files".into());
        PathBuf::from(base).join("Tailscale").join("tailscale.exe")
    }
    #[cfg(not(windows))]
    {
        for path in [
            "/usr/local/bin/tailscale",
            "/opt/homebrew/bin/tailscale",
            "/usr/bin/tailscale",
            "/Applications/Tailscale.app/Contents/MacOS/Tailscale",
        ] {
            if std::path::Path::new(path).exists() {
                return path.into();
            }
        }
        "tailscale".into()
    }
}
pub async fn identity() -> Result<Identity> {
    let output = timeout(
        Duration::from_secs(2),
        Command::new(executable())
            .args(["status", "--json"])
            .kill_on_drop(true)
            .output(),
    )
    .await
    .context("Tailscale command timed out")??;
    if !output.status.success() {
        bail!("Tailscale is unavailable");
    }
    if output.stdout.len() > 4 * 1024 * 1024 {
        bail!("Tailscale directory too large");
    }
    parse_identity(&output.stdout)
}
pub fn parse_identity(data: &[u8]) -> Result<Identity> {
    let v: serde_json::Value = serde_json::from_slice(data)?;
    if v["BackendState"] != "Running" {
        bail!("Tailscale is not connected");
    }
    let node_id = v["Self"]["ID"]
        .as_str()
        .filter(|s| !s.is_empty())
        .context("Tailscale node ID missing")?
        .to_owned();
    let addresses: Vec<IpAddr> = v["Self"]["TailscaleIPs"]
        .as_array()
        .context("Tailscale addresses missing")?
        .iter()
        .filter_map(|s| s.as_str()?.parse().ok())
        .filter(|ip| is_tailnet(*ip))
        .collect();
    if addresses.is_empty() {
        bail!("No Tailscale addresses available");
    }
    Ok(Identity { node_id, addresses })
}
