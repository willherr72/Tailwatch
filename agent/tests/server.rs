use axum::{
    body::Body,
    extract::ConnectInfo,
    http::{Request, StatusCode},
};
use std::{
    net::SocketAddr,
    sync::{Arc, RwLock},
    time::{Duration, Instant},
};
use tailwatch_agent::{
    network::Config,
    server::{AppState, Gate, router},
};
use tower::ServiceExt;

fn state() -> AppState {
    AppState {
        cache: Arc::new(RwLock::new(None)),
        config: Config {
            node_id: "node-a".into(),
            port: 48173,
            allowed_peers: vec!["100.64.0.2".parse().unwrap()],
        },
        gate: Arc::new(RwLock::new(Gate {
            node_id: "node-a".into(),
            valid: true,
            checked: Instant::now(),
        })),
    }
}
async fn request(s: AppState, from: &str, path: &str) -> StatusCode {
    router(s)
        .oneshot(
            Request::builder()
                .uri(path)
                .extension(ConnectInfo(from.parse::<SocketAddr>().unwrap()))
                .body(Body::empty())
                .unwrap(),
        )
        .await
        .unwrap()
        .status()
}
#[tokio::test]
async fn unauthorized_peer_is_forbidden() {
    assert_eq!(
        request(state(), "100.64.0.3:42", "/v1/snapshot").await,
        StatusCode::FORBIDDEN
    );
}
#[tokio::test]
async fn authorized_peer_gets_warming_up_not_fake_metrics() {
    assert_eq!(
        request(state(), "100.64.0.2:42", "/v1/snapshot").await,
        StatusCode::SERVICE_UNAVAILABLE
    );
}
#[tokio::test]
async fn expired_identity_lease_disables_health_too() {
    let s = state();
    s.gate.write().unwrap().checked = Instant::now() - Duration::from_secs(7);
    assert_eq!(
        request(s, "100.64.0.2:42", "/v1/health").await,
        StatusCode::SERVICE_UNAVAILABLE
    );
}
#[tokio::test]
async fn lost_identity_disables_loopback() {
    let s = state();
    s.gate.write().unwrap().valid = false;
    assert_eq!(
        request(s, "127.0.0.1:42", "/v1/snapshot").await,
        StatusCode::SERVICE_UNAVAILABLE
    );
}
