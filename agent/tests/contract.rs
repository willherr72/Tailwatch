use tailwatch_agent::model::{Reading, valid_percentage};

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
    for value in [0.0, 50.0, 100.0] { assert!(valid_percentage(value)); }
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

