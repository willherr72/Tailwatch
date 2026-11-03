use tailwatch_agent::model::{Reading, valid_percentage};
#[test]
fn stalled_gpu_worker_does_not_renew_old_readings() {
    use std::{
        sync::{Arc, RwLock},
        time::{Duration, Instant},
    };
    use tailwatch_agent::collect::{GpuSample, cached_gpus};
    let s: tailwatch_agent::model::Snapshot =
        serde_json::from_str(include_str!("../../protocol/snapshot.example.json")).unwrap();
    let cache = Arc::new(RwLock::new(Some(GpuSample {
        readings: s.gpus,
        status: "ok".into(),
        captured: Instant::now() - Duration::from_secs(12),
    })));
    let (gpus, _) = cached_gpus(&cache);
    assert!(!gpus[0].utilization.fresh());
    assert!(gpus[0].memory.as_ref().unwrap().age_ms >= 12000);
}
#[test]
fn snapshot_ages_when_sampler_stops() {
    let s: tailwatch_agent::model::Snapshot =
        serde_json::from_str(include_str!("../../protocol/snapshot.example.json")).unwrap();
    let old = s.aged(10001);
    assert_eq!(old.age_ms, 10001);
    assert_eq!(old.ram.age_ms, 10001);
    assert!(!old.cpu.reading.fresh());
    assert!(!old.gpus[0].utilization.fresh());
    assert_eq!(old.gpus[0].memory.as_ref().unwrap().age_ms, 10001);
}
#[test]
fn apple_residency_distinguishes_idle_from_missing_counter() {
    use tailwatch_agent::model::residency_percentage;
    assert_eq!(
        residency_percentage(&[("OFF".into(), 80), ("P1".into(), 20)]),
        Some(20.0)
    );
    assert_eq!(
        residency_percentage(&[("OFF".into(), 100), ("P1".into(), 0)]),
        Some(0.0)
    );
    assert_eq!(residency_percentage(&[]), None);
    assert_eq!(
        residency_percentage(&[("OFF".into(), 0), ("P1".into(), 0)]),
        None
    );
    assert_eq!(
        residency_percentage(&[("OFF".into(), 10), ("P1".into(), -1)]),
        None
    );
    assert_eq!(residency_percentage(&[("OFF".into(), 100)]), None);
}

#[test]
fn measured_zero_is_fresh_and_distinct_from_unavailable() {
    assert!(Reading::measured(0.0).fresh());
    assert!(!Reading::unavailable("unsupported").fresh());
}
#[test]
fn percentages_reject_invalid_numbers() {
    for value in [-0.1, 100.1, f64::NAN, f64::INFINITY] {
        assert!(!valid_percentage(value), "accepted {value}");
    }
    for value in [0.0, 50.0, 100.0] {
        assert!(valid_percentage(value));
    }
}
#[test]
fn old_or_failed_samples_never_look_live() {
    let mut reading = Reading::measured(20.0);
    reading.age_ms = 10_001;
    assert!(!reading.fresh());
    reading.age_ms = 0;
    reading.status = "collection_failed".into();
    assert!(!reading.fresh());
}
