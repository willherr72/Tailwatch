use serde::{Deserialize, Serialize};

pub const STALE_MS: u64 = 10_000;
pub fn residency_percentage(states: &[(String, i64)]) -> Option<f64> {
    let inactive = |name: &str| matches!(name, "OFF" | "IDLE" | "DOWN");
    if states.is_empty()
        || states.iter().any(|(_, v)| *v < 0)
        || !states.iter().any(|(n, _)| !inactive(n))
    {
        return None;
    }
    let total: f64 = states.iter().map(|(_, v)| *v as f64).sum();
    let active: f64 = states
        .iter()
        .filter(|(n, _)| !inactive(n))
        .map(|(_, v)| *v as f64)
        .sum();
    (total > 0.0).then_some(100.0 * active / total)
}
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Reading {
    pub value: Option<f64>,
    pub status: String,
    pub age_ms: u64,
}
impl Reading {
    pub fn measured(value: f64) -> Self {
        if !valid_percentage(value) {
            return Self::unavailable("collection_failed");
        }
        Self {
            value: Some(value),
            status: "ok".into(),
            age_ms: 0,
        }
    }
    pub fn unavailable(status: &str) -> Self {
        Self {
            value: None,
            status: status.into(),
            age_ms: 0,
        }
    }
    pub fn fresh(&self) -> bool {
        self.status == "ok" && self.age_ms <= STALE_MS && self.value.is_some_and(valid_percentage)
    }
}
pub fn valid_percentage(value: f64) -> bool {
    value.is_finite() && (0.0..=100.0).contains(&value)
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CpuReading {
    #[serde(flatten)]
    pub reading: Reading,
    pub model: String,
    pub logical_cores: usize,
}
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct MemoryReading {
    pub used_bytes: Option<u64>,
    pub total_bytes: Option<u64>,
    pub status: String,
    pub age_ms: u64,
    pub source: String,
}
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct GpuReading {
    pub id: String,
    pub name: String,
    pub vendor: String,
    pub utilization: Reading,
    pub metric: String,
    pub memory: Option<MemoryReading>,
}
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Snapshot {
    pub schema_version: u32,
    pub agent_version: String,
    pub node_id: String,
    pub hostname: String,
    pub os: String,
    pub boot_id: String,
    pub sequence: u64,
    pub interval_ms: u64,
    pub age_ms: u64,
    pub cpu: CpuReading,
    pub ram: MemoryReading,
    pub gpus: Vec<GpuReading>,
    pub gpu_status: String,
}
impl Snapshot {
    pub fn aged(&self, elapsed_ms: u64) -> Self {
        let mut value = self.clone();
        value.age_ms = value.age_ms.saturating_add(elapsed_ms);
        value.cpu.reading.age_ms = value.cpu.reading.age_ms.saturating_add(elapsed_ms);
        value.ram.age_ms = value.ram.age_ms.saturating_add(elapsed_ms);
        for gpu in &mut value.gpus {
            gpu.utilization.age_ms = gpu.utilization.age_ms.saturating_add(elapsed_ms);
            if let Some(memory) = &mut gpu.memory {
                memory.age_ms = memory.age_ms.saturating_add(elapsed_ms);
            }
        }
        value
    }
}
