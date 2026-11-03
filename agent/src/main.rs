use anyhow::{Context, Result};
use clap::{Parser, Subcommand};
use std::{net::IpAddr, path::PathBuf, time::Duration};
use tailwatch_agent::{
    collect::Collector,
    network::{self, Config},
};
#[derive(Parser)]
#[command(
    version,
    about = "Read-only CPU, GPU and RAM monitoring over Tailscale"
)]
struct Cli {
    #[command(subcommand)]
    command: Action,
}
#[derive(Subcommand)]
enum Action {
    /// Collect one local sample and print JSON.
    Sample,
    /// Create configuration for the current Tailscale identity.
    Configure {
        #[arg(long)]
        output: PathBuf,
        #[arg(long, required = true)]
        allow_peer: Vec<IpAddr>,
        #[arg(long, default_value_t = 48173)]
        port: u16,
    },
    /// Run in foreground. Local-only mode never exposes a remote listener.
    Run {
        #[arg(
            long,
            required_unless_present = "local_only",
            conflicts_with = "local_only"
        )]
        config: Option<PathBuf>,
        #[arg(long)]
        local_only: bool,
        #[arg(long, default_value_t = 48173)]
        port: u16,
    },
    /// Entry point used by Windows Service Control Manager.
    #[cfg(windows)]
    Service {
        #[arg(long)]
        config: PathBuf,
    },
}
fn load(path: PathBuf) -> Result<Config> {
    let config: Config =
        serde_json::from_slice(&std::fs::read(path).context("Cannot read agent configuration")?)?;
    config.validate()?;
    Ok(config)
}
fn main() -> Result<()> {
    let cli = Cli::parse();
    #[cfg(windows)]
    if let Action::Service { config } = cli.command {
        return service::dispatch(config);
    }
    let runtime = tokio::runtime::Runtime::new()?;
    runtime.block_on(async move {
        match cli.command {
            Action::Sample => {
                let id = network::identity()
                    .await
                    .map(|i| i.node_id)
                    .unwrap_or_else(|_| "local".into());
                let mut collector = Collector::new(id)?;
                tokio::time::sleep(Duration::from_secs(2)).await;
                println!("{}", serde_json::to_string_pretty(&collector.sample())?);
            }
            Action::Configure {
                output,
                allow_peer,
                port,
            } => {
                let id = network::identity().await?;
                let config = Config {
                    node_id: id.node_id,
                    port,
                    allowed_peers: allow_peer,
                };
                config.validate()?;
                // Never overwrite an existing access policy implicitly.
                use std::io::Write;
                let mut file = std::fs::OpenOptions::new()
                    .write(true)
                    .create_new(true)
                    .open(&output)?;
                file.write_all(serde_json::to_string_pretty(&config)?.as_bytes())?;
                eprintln!("Configuration created. Run with: tailwatch-agent run --config <file>");
            }
            Action::Run { config, port, .. } => {
                let config = config.map(load).transpose()?;
                let (tx, rx) = tokio::sync::watch::channel(false);
                tokio::spawn(async move {
                    let _ = tokio::signal::ctrl_c().await;
                    let _ = tx.send(true);
                });
                tailwatch_agent::server::run(config, port, rx).await?;
            }
            #[cfg(windows)]
            Action::Service { .. } => unreachable!(),
        }
        Ok(())
    })
}
#[cfg(windows)]
#[path = "service.rs"]
mod service;
