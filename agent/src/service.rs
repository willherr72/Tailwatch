use anyhow::Result;
use std::{ffi::OsString, path::PathBuf, sync::OnceLock, time::Duration};
use windows_service::{
    define_windows_service,
    service::{
        ServiceControl, ServiceControlAccept, ServiceExitCode, ServiceState, ServiceStatus,
        ServiceType,
    },
    service_control_handler::{self, ServiceControlHandlerResult},
    service_dispatcher,
};
static CONFIG: OnceLock<PathBuf> = OnceLock::new();
define_windows_service!(service_main, run);
pub fn dispatch(config: PathBuf) -> Result<()> {
    CONFIG
        .set(config)
        .map_err(|_| anyhow::anyhow!("Service already initialized"))?;
    service_dispatcher::start("TailwatchAgent", service_main)?;
    Ok(())
}
fn run(_args: Vec<OsString>) {
    let (tx, rx) = tokio::sync::watch::channel(false);
    let Ok(handle) =
        service_control_handler::register("TailwatchAgent", move |control| match control {
            ServiceControl::Stop | ServiceControl::Shutdown => {
                let _ = tx.send(true);
                ServiceControlHandlerResult::NoError
            }
            ServiceControl::Interrogate => ServiceControlHandlerResult::NoError,
            _ => ServiceControlHandlerResult::NotImplemented,
        })
    else {
        return;
    };
    let status = |state, code| ServiceStatus {
        service_type: ServiceType::OWN_PROCESS,
        current_state: state,
        controls_accepted: if state == ServiceState::Running {
            ServiceControlAccept::STOP | ServiceControlAccept::SHUTDOWN
        } else {
            ServiceControlAccept::empty()
        },
        exit_code: ServiceExitCode::Win32(code),
        checkpoint: 0,
        wait_hint: Duration::ZERO,
        process_id: None,
    };
    let _ = handle.set_service_status(status(ServiceState::Running, 0));
    let result = (|| -> Result<()> {
        let config = super::load(CONFIG.get().unwrap().clone())?;
        tokio::runtime::Runtime::new()?.block_on(tailwatch_agent::server::run(
            Some(config),
            48173,
            rx,
        ))
    })();
    let _ = handle.set_service_status(status(
        ServiceState::Stopped,
        if result.is_ok() { 0 } else { 1 },
    ));
}
