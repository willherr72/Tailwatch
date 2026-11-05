use crate::{
    collect::{GpuCache, GpuSample},
    model::*,
};
use anyhow::Result;
use serde::{Deserialize, Serialize};
use std::{
    io::{Read, Write},
    process::{Command, Stdio},
    sync::{Arc, RwLock},
    thread::JoinHandle,
    time::{Duration, Instant},
};
use tokio::{
    io::{AsyncBufReadExt, AsyncReadExt, BufReader},
    sync::oneshot,
};

const MAX_FRAME: u64 = 64 * 1024;

#[derive(Serialize, Deserialize)]
struct Frame {
    readings: Vec<GpuReading>,
    status: String,
}

#[derive(Clone, Copy)]
pub(crate) struct Timing {
    pub deadline: Duration,
    pub initial_retry: Duration,
    pub max_retry: Duration,
    pub healthy_reset: Duration,
}
impl Default for Timing {
    fn default() -> Self {
        Self {
            deadline: Duration::from_secs(6),
            initial_retry: Duration::from_secs(1),
            max_retry: Duration::from_secs(30),
            healthy_reset: Duration::from_secs(60),
        }
    }
}

/// Owns the supervisor and its child. Dropping it interrupts reads/backoff and reaps the worker.
pub struct GpuWorker {
    pub cache: GpuCache,
    stop: Option<oneshot::Sender<()>>,
    thread: Option<JoinHandle<()>>,
}
impl GpuWorker {
    pub fn start() -> Self {
        match std::env::current_exe() {
            Ok(executable) => {
                let mut command = Command::new(executable);
                command.arg("gpu-worker");
                Self::start_command(command, Timing::default())
            }
            Err(_) => {
                let cache = Arc::new(RwLock::new(None));
                unavailable(&cache, "worker_start_failed");
                Self {
                    cache,
                    stop: None,
                    thread: None,
                }
            }
        }
    }

    pub(crate) fn start_command(command: Command, timing: Timing) -> Self {
        let cache = Arc::new(RwLock::new(None));
        let output = cache.clone();
        let (stop, cancelled) = oneshot::channel();
        let thread = std::thread::Builder::new()
            .name("gpu-supervisor".into())
            .spawn(move || {
                match tokio::runtime::Builder::new_current_thread()
                    .enable_all()
                    .build()
                {
                    Ok(runtime) => runtime.block_on(supervise(command, output, timing, cancelled)),
                    Err(_) => unavailable(&output, "worker_start_failed"),
                }
            })
            .ok();
        if thread.is_none() {
            unavailable(&cache, "worker_start_failed");
        }
        Self {
            cache,
            stop: Some(stop),
            thread,
        }
    }
}
impl Drop for GpuWorker {
    fn drop(&mut self) {
        if let Some(stop) = self.stop.take() {
            let _ = stop.send(());
        }
        if let Some(thread) = self.thread.take() {
            let _ = thread.join();
        }
    }
}
fn unavailable(cache: &GpuCache, status: &str) {
    publish(
        cache,
        Frame {
            readings: vec![],
            status: status.into(),
        },
    );
}
fn publish(cache: &GpuCache, frame: Frame) {
    if let Ok(mut value) = cache.write() {
        *value = Some(GpuSample {
            readings: frame.readings,
            status: frame.status,
            captured: Instant::now(),
        });
    }
}
async fn read_frame(
    reader: &mut BufReader<tokio::process::ChildStdout>,
) -> std::result::Result<Frame, &'static str> {
    let mut bytes = Vec::new();
    // Limit allocation even for a worker that never emits a newline. The timeout
    // surrounds the entire frame, so trickled partial data cannot renew liveness.
    let count = reader
        .take(MAX_FRAME + 1)
        .read_until(b'\n', &mut bytes)
        .await
        .map_err(|_| "worker_exited")?;
    if count == 0 {
        return Err("worker_exited");
    }
    if count as u64 > MAX_FRAME || bytes.last() != Some(&b'\n') {
        return Err("worker_invalid_output");
    }
    let frame: Frame = serde_json::from_slice(&bytes).map_err(|_| "worker_invalid_output")?;
    if frame.readings.len() > 64
        || !matches!(
            frame.status.as_str(),
            "ok" | "partial"
                | "unsupported"
                | "unsupported_or_driver_missing"
                | "collection_failed"
        )
    {
        return Err("worker_invalid_output");
    }
    if frame.readings.iter().any(|gpu| {
        gpu.utilization
            .value
            .is_some_and(|value| !valid_percentage(value))
    }) {
        return Err("worker_invalid_output");
    }
    Ok(frame)
}
async fn supervise(
    command: Command,
    cache: GpuCache,
    timing: Timing,
    mut stop: oneshot::Receiver<()>,
) {
    let mut command = tokio::process::Command::from(command);
    command
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .kill_on_drop(true);
    #[cfg(windows)]
    command.creation_flags(0x0800_0000); // CREATE_NO_WINDOW
    let mut retry = timing.initial_retry;
    let mut last_failure = None;
    loop {
        if stop.try_recv() != Err(oneshot::error::TryRecvError::Empty) {
            break;
        }
        let failure = match command.spawn() {
            Err(_) => "worker_start_failed",
            Ok(mut child) => {
                // Child::wait closes stdin automatically. Keep our liveness pipe
                // separately owned until shutdown so exit polling cannot signal EOF.
                let _parent_pipe = child.stdin.take();
                let started = Instant::now();
                let mut reader = BufReader::new(child.stdout.take().expect("piped stdout"));
                let failure = loop {
                    tokio::select! {
                        biased;
                        _ = &mut stop => {
                            let _ = child.kill().await;
                            let _ = child.wait().await;
                            return;
                        }
                        _ = child.wait() => break "worker_exited",
                        result = tokio::time::timeout(timing.deadline, read_frame(&mut reader)) => {
                            match result {
                                Ok(Ok(frame)) => {
                                    publish(&cache, frame);
                                    if started.elapsed() >= timing.healthy_reset { retry = timing.initial_retry; }
                                    if last_failure.take().is_some() { eprintln!("GPU worker resumed output"); }
                                }
                                Ok(Err(reason)) => break reason,
                                Err(_) => break "worker_timeout",
                            }
                        }
                    }
                };
                // Invalidate before potentially slow process cleanup.
                unavailable(&cache, failure);
                let _ = child.kill().await;
                let _ = child.wait().await;
                failure
            }
        };
        unavailable(&cache, failure);
        if last_failure != Some(failure) {
            eprintln!("GPU worker unavailable: {failure}");
            last_failure = Some(failure);
        }
        tokio::select! {
            _ = &mut stop => return,
            _ = tokio::time::sleep(retry) => {},
        }
        retry = retry.saturating_mul(2).min(timing.max_retry);
    }
}

/// Internal executable entry point. Only this process may load/call NVML.
pub fn run() -> Result<()> {
    watch_parent()?;
    let mut collector = NvidiaCollector {
        nvml: nvml_wrapper::Nvml::init().ok(),
        last_nvml_attempt: Instant::now(),
        reconnect: false,
    };
    let mut output = std::io::stdout().lock();
    loop {
        let started = Instant::now();
        let (mut readings, status) = collector.gpus();
        let age = started.elapsed().as_millis().min(u64::MAX as u128) as u64;
        for gpu in &mut readings {
            gpu.utilization.age_ms = gpu.utilization.age_ms.saturating_add(age);
            if let Some(memory) = &mut gpu.memory {
                memory.age_ms = memory.age_ms.saturating_add(age);
            }
        }
        let bytes = serde_json::to_vec(&Frame { readings, status })?;
        anyhow::ensure!(
            bytes.len() < MAX_FRAME as usize,
            "GPU sample exceeded pipe limit"
        );
        output.write_all(&bytes)?;
        output.write_all(b"\n")?;
        output.flush()?;
        std::thread::sleep(Duration::from_secs(2).saturating_sub(started.elapsed()));
    }
}

pub(crate) fn watch_parent() -> Result<()> {
    // This thread never calls the driver. EOF still terminates the process if
    // the sampling thread is stuck inside NVML or blocked writing stdout.
    std::thread::Builder::new()
        .name("gpu-parent-watch".into())
        .spawn(|| {
            let mut byte = [0];
            let _ = std::io::stdin().lock().read(&mut byte);
            std::process::exit(0);
        })?;
    Ok(())
}
#[cfg(any(target_os = "windows", target_os = "linux"))]
struct NvidiaCollector {
    nvml: Option<nvml_wrapper::Nvml>,
    last_nvml_attempt: Instant,
    reconnect: bool,
}
#[cfg(any(target_os = "windows", target_os = "linux"))]
fn transient_nvml_error(error: &nvml_wrapper::error::NvmlError) -> bool {
    use nvml_wrapper::error::NvmlError;
    matches!(
        error,
        NvmlError::Unknown
            | NvmlError::Uninitialized
            | NvmlError::GpuLost
            | NvmlError::DriverNotLoaded
            | NvmlError::ResetRequired
    )
}
#[cfg(any(target_os = "windows", target_os = "linux"))]
impl NvidiaCollector {
    fn gpus(&mut self) -> (Vec<GpuReading>, String) {
        self.reconnect = false;
        let result = self.collect_gpus();
        if self.reconnect && self.nvml.is_some() {
            // Drop/shutdown the old NVML session before opening a fresh one. Some
            // laptop drivers leave a session returning Unknown after a power transition.
            // Retry at most once per two-second sample; failed initialization backs off.
            self.nvml = None;
            self.nvml = nvml_wrapper::Nvml::init().ok();
            self.last_nvml_attempt = Instant::now();
            self.reconnect = false;
            return self.collect_gpus();
        }
        result
    }
    fn collect_gpus(&mut self) -> (Vec<GpuReading>, String) {
        if self.nvml.is_none() && self.last_nvml_attempt.elapsed() >= Duration::from_secs(30) {
            self.nvml = nvml_wrapper::Nvml::init().ok();
            self.last_nvml_attempt = Instant::now();
        }
        let Some(nvml) = &self.nvml else {
            return (vec![], "unsupported_or_driver_missing".into());
        };
        let count = match nvml.device_count() {
            Ok(n) => n,
            Err(e) => {
                self.reconnect |= transient_nvml_error(&e);
                return (vec![], "collection_failed".into());
            }
        };
        let mut gpus = vec![];
        let mut partial = false;
        for index in 0..count.min(64) {
            let device = match nvml.device_by_index(index) {
                Ok(device) => device,
                Err(e) => {
                    self.reconnect |= transient_nvml_error(&e);
                    partial = true;
                    continue;
                }
            };
            let id = match device.uuid() {
                Ok(id) => id,
                Err(e) => {
                    self.reconnect |= transient_nvml_error(&e);
                    partial = true;
                    continue;
                }
            };
            let utilization = device
                .utilization_rates()
                .map(|u| Reading::measured(u.gpu as f64))
                .unwrap_or_else(|e| {
                    self.reconnect |= transient_nvml_error(&e);
                    Reading::unavailable(&format!("nvml_{e:?}"))
                });
            let memory = device.memory_info().ok().map(|m| MemoryReading {
                used_bytes: Some(m.used),
                total_bytes: Some(m.total),
                status: "ok".into(),
                age_ms: 0,
                source: "NVML dedicated memory".into(),
            });
            if !utilization.fresh() {
                partial = true
            }
            gpus.push(GpuReading {
                id,
                name: device.name().unwrap_or_else(|_| "NVIDIA GPU".into()),
                vendor: "NVIDIA".into(),
                utilization,
                metric: "active_time".into(),
                memory,
            });
        }
        (
            gpus,
            if partial {
                "partial"
            } else if count == 0 {
                "unsupported"
            } else {
                "ok"
            }
            .into(),
        )
    }
}
