use crate::model::*;
use anyhow::Result;
use std::{
    sync::{Arc, RwLock},
    time::{Duration, Instant},
};
use sysinfo::System;

#[derive(Clone)]
pub struct CachedSnapshot {
    pub snapshot: Snapshot,
    pub captured: Instant,
}
pub type Cache = Arc<RwLock<Option<CachedSnapshot>>>;
pub fn cached(cache: &Cache) -> Option<Snapshot> {
    cache.read().ok()?.as_ref().map(|entry| {
        entry
            .snapshot
            .aged(entry.captured.elapsed().as_millis().min(u64::MAX as u128) as u64)
    })
}

pub struct Collector {
    system: System,
    boot: String,
    sequence: u64,
    node_id: String,
    #[cfg(any(target_os = "windows", target_os = "linux"))]
    nvml: Option<nvml_wrapper::Nvml>,
    #[cfg(any(target_os = "windows", target_os = "linux"))]
    last_nvml_attempt: Instant,
    #[cfg(target_os = "macos")]
    mac_gpu: Arc<RwLock<Option<(f64, Instant)>>>,
}
impl Collector {
    pub fn new(node_id: String) -> Result<Self> {
        let mut system = System::new()?;
        system.refresh_cpu_usage();
        system.refresh_memory();
        Ok(Self {
            system,
            boot: uuid::Uuid::new_v4().to_string(),
            sequence: 0,
            node_id,
            #[cfg(any(target_os = "windows", target_os = "linux"))]
            nvml: nvml_wrapper::Nvml::init().ok(),
            #[cfg(any(target_os = "windows", target_os = "linux"))]
            last_nvml_attempt: Instant::now(),
            #[cfg(target_os = "macos")]
            mac_gpu: start_mac_gpu(),
        })
    }
    pub fn sample(&mut self) -> Snapshot {
        self.system.refresh_cpu_usage();
        self.system.refresh_memory();
        self.sequence += 1;
        let cpu = CpuReading {
            reading: Reading::measured(self.system.global_cpu_usage() as f64),
            model: self
                .system
                .cpus()
                .first()
                .map(|c| c.brand().to_owned())
                .unwrap_or_default(),
            logical_cores: self.system.cpus().len(),
        };
        let total = self.system.total_memory();
        let ram = MemoryReading {
            used_bytes: Some(self.system.used_memory()),
            total_bytes: Some(total),
            status: if total > 0 { "ok" } else { "collection_failed" }.into(),
            age_ms: 0,
            source: "sysinfo OS memory accounting".into(),
        };
        let (gpus, gpu_status) = self.gpus();
        Snapshot {
            schema_version: 1,
            agent_version: env!("CARGO_PKG_VERSION").into(),
            node_id: self.node_id.clone(),
            hostname: System::host_name().unwrap_or_else(|_| "This machine".into()),
            os: std::env::consts::OS.into(),
            boot_id: self.boot.clone(),
            sequence: self.sequence,
            interval_ms: 2000,
            age_ms: 0,
            cpu,
            ram,
            gpus,
            gpu_status,
        }
    }
    #[cfg(any(target_os = "windows", target_os = "linux"))]
    fn gpus(&mut self) -> (Vec<GpuReading>, String) {
        if self.nvml.is_none() && self.last_nvml_attempt.elapsed() >= Duration::from_secs(30) {
            self.nvml = nvml_wrapper::Nvml::init().ok();
            self.last_nvml_attempt = Instant::now();
        }
        let Some(nvml) = &self.nvml else {
            return (vec![], "unsupported_or_driver_missing".into());
        };
        let count = match nvml.device_count() {
            Ok(n) => n,
            Err(_) => return (vec![], "collection_failed".into()),
        };
        let mut gpus = vec![];
        let mut partial = false;
        for index in 0..count.min(64) {
            let Ok(device) = nvml.device_by_index(index) else {
                partial = true;
                continue;
            };
            let Ok(id) = device.uuid() else {
                partial = true;
                continue;
            };
            let utilization = device
                .utilization_rates()
                .map(|u| Reading::measured(u.gpu as f64))
                .unwrap_or_else(|_| Reading::unavailable("unsupported_or_collection_failed"));
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
    #[cfg(target_os = "macos")]
    fn gpus(&mut self) -> (Vec<GpuReading>, String) {
        let reading = self
            .mac_gpu
            .read()
            .ok()
            .and_then(|v| *v)
            .map(|(ratio, time)| {
                let mut r = Reading::measured(ratio * 100.0);
                r.age_ms = time.elapsed().as_millis() as u64;
                r
            })
            .unwrap_or_else(|| Reading::unavailable("warming_up_or_unsupported"));
        let state = if reading.fresh() { "ok" } else { "unavailable" }.into();
        (
            vec![GpuReading {
                id: "apple-integrated".into(),
                name: "Apple silicon GPU".into(),
                vendor: "Apple".into(),
                utilization: reading,
                metric: "active_residency".into(),
                memory: None,
            }],
            state,
        )
    }
}

#[cfg(target_os = "macos")]
fn start_mac_gpu() -> Arc<RwLock<Option<(f64, Instant)>>> {
    let cache = Arc::new(RwLock::new(None));
    let output = cache.clone();
    std::thread::spawn(move || {
        loop {
            // Subscribe only to the GPU residency channel. macmon's all-metrics sampler
            // defaults absent counters to zero; absence here must remain unavailable.
            match macmon::sources::IOReport::new(vec![(
                "GPU Stats",
                Some("GPU Performance States"),
            )]) {
                Ok(report) => loop {
                    let mut states = vec![];
                    for item in report.get_sample(2000) {
                        if item.channel == "GPUPH" {
                            states.extend(macmon::sources::cfio_get_residencies(item.item));
                        }
                    }
                    if let Some(percent) = residency_percentage(&states) {
                        if let Ok(mut current) = output.write() {
                            *current = Some((percent / 100.0, Instant::now()));
                        }
                    } else {
                        if let Ok(mut current) = output.write() {
                            *current = None;
                        }
                        break;
                    }
                },
                Err(_) => std::thread::sleep(Duration::from_secs(30)),
            }
            std::thread::sleep(Duration::from_secs(2));
        }
    });
    cache
}
pub fn start(cache: Cache, node_id: String) {
    std::thread::spawn(move || {
        let mut collector = match Collector::new(node_id) {
            Ok(c) => c,
            Err(e) => {
                eprintln!("Collector could not start: {e}");
                return;
            }
        };
        std::thread::sleep(Duration::from_secs(2));
        loop {
            let started = Instant::now();
            let snapshot = collector.sample();
            if let Ok(mut value) = cache.write() {
                *value = Some(CachedSnapshot {
                    snapshot,
                    captured: Instant::now(),
                })
            }
            std::thread::sleep(Duration::from_secs(2).saturating_sub(started.elapsed()));
        }
    });
}
