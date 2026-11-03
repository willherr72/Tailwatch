use crate::{
    collect::{Cache, cached},
    network::{self, Config},
};
use anyhow::{Context, Result};
use axum::{
    Json, Router,
    body::Body,
    extract::{ConnectInfo, Request, State},
    http::StatusCode,
    middleware::{self, Next},
    response::{IntoResponse, Response},
    routing::get,
};
use hyper_util::rt::TokioIo;
use std::{
    collections::HashMap,
    net::{IpAddr, SocketAddr},
    sync::{Arc, Mutex, OnceLock, RwLock},
    time::{Duration, Instant},
};
use tokio::{
    net::TcpListener,
    sync::{Semaphore, watch},
    time::{sleep, timeout},
};
use tower::ServiceExt;

#[derive(Clone)]
pub struct Gate {
    pub node_id: String,
    pub valid: bool,
    pub checked: Instant,
}
#[derive(Clone)]
pub struct AppState {
    pub cache: Cache,
    pub config: Config,
    pub gate: Arc<RwLock<Gate>>,
}
type Buckets = Mutex<HashMap<IpAddr, (Instant, u32)>>;
static RATES: OnceLock<Buckets> = OnceLock::new();

async fn authorize(
    State(state): State<AppState>,
    ConnectInfo(remote): ConnectInfo<SocketAddr>,
    request: Request,
    next: Next,
) -> Response {
    if !network::allowed(remote.ip(), &state.config.allowed_peers) {
        return StatusCode::FORBIDDEN.into_response();
    }
    let valid = state
        .gate
        .read()
        .is_ok_and(|g| g.valid && g.checked.elapsed() < Duration::from_secs(6));
    if !valid {
        return (StatusCode::SERVICE_UNAVAILABLE, "tailscale_unavailable").into_response();
    }
    if request
        .headers()
        .get("content-length")
        .is_some_and(|v| v != "0")
        || request.headers().contains_key("transfer-encoding")
    {
        return StatusCode::PAYLOAD_TOO_LARGE.into_response();
    }
    {
        let mut buckets = RATES
            .get_or_init(|| Mutex::new(HashMap::new()))
            .lock()
            .unwrap_or_else(|e| e.into_inner());
        buckets.retain(|_, (at, _)| at.elapsed() < Duration::from_secs(60));
        let (_, count) = buckets
            .entry(network::normalized(remote.ip()))
            .or_insert((Instant::now(), 0));
        if *count >= 120 {
            return StatusCode::TOO_MANY_REQUESTS.into_response();
        }
        *count += 1;
    }
    next.run(request).await
}
async fn snapshot(State(state): State<AppState>) -> Response {
    let Some(mut value) = cached(&state.cache) else {
        return (StatusCode::SERVICE_UNAVAILABLE, "warming_up").into_response();
    };
    value.node_id = state
        .gate
        .read()
        .unwrap_or_else(|e| e.into_inner())
        .node_id
        .clone();
    Json(value).into_response()
}
async fn health(State(state): State<AppState>) -> Response {
    let fresh = cached(&state.cache).is_some_and(|s| s.age_ms <= 10_000);
    if fresh {
        (StatusCode::OK, "ok").into_response()
    } else {
        (StatusCode::SERVICE_UNAVAILABLE, "sampler_unavailable").into_response()
    }
}
pub fn router(state: AppState) -> Router {
    Router::new()
        .route("/v1/snapshot", get(snapshot))
        .route("/v1/health", get(health))
        .layer(middleware::from_fn_with_state(state.clone(), authorize))
        .with_state(state)
}

pub async fn run(config: Option<Config>, port: u16, mut stop: watch::Receiver<bool>) -> Result<()> {
    if let Some(c) = &config {
        c.validate()?
    }
    let initial = loop {
        if *stop.borrow() {
            return Ok(());
        }
        match network::identity().await {
            Ok(id)
                if config
                    .as_ref()
                    .is_none_or(|c| network::matching_identity(&c.node_id, &id.node_id, true)) =>
            {
                break id;
            }
            _ => {
                tokio::select! {_=sleep(Duration::from_secs(3))=>{},_=stop.changed()=>{if *stop.borrow(){return Ok(())}}}
            }
        }
    };
    let bind_addresses = if config.is_some() {
        initial.addresses.clone()
    } else {
        vec![]
    };
    let config = config.unwrap_or(Config {
        node_id: initial.node_id.clone(),
        port,
        allowed_peers: vec![],
    });
    let gate = Arc::new(RwLock::new(Gate {
        node_id: initial.node_id.clone(),
        valid: true,
        checked: Instant::now(),
    }));
    let cache: Cache = Arc::new(RwLock::new(None));
    crate::collect::start(cache.clone(), initial.node_id);
    let state = AppState {
        cache,
        config: config.clone(),
        gate: gate.clone(),
    };
    let app = router(state);
    let slots = Arc::new(Semaphore::new(32));
    let mut listeners = vec![];
    // Bind every socket before starting any server, so a partial bind failure is not hidden.
    for ip in std::iter::once("127.0.0.1".parse::<IpAddr>()?).chain(bind_addresses.clone()) {
        let address = SocketAddr::new(ip, config.port);
        listeners.push(TcpListener::bind(address).await.with_context(|| {
            format!("Cannot bind {address}; is another agent already running?")
        })?);
    }
    let mut jobs = tokio::task::JoinSet::new();
    for listener in listeners {
        let app = app.clone();
        let slots = slots.clone();
        let mut stopping = stop.clone();
        jobs.spawn(async move{
            loop {
                let permit=tokio::select!{p=slots.clone().acquire_owned()=>p?,_=stopping.changed()=>break};
                let accepted=tokio::select!{v=listener.accept()=>v?,_=stopping.changed()=>break};
                let (stream,remote)=accepted;
                let app=app.clone();
                tokio::spawn(async move{
                    let _permit=permit;
                    let service=hyper::service::service_fn(move |mut req:hyper::Request<hyper::body::Incoming>|{
                        req.extensions_mut().insert(ConnectInfo(remote));
                        app.clone().oneshot(req.map(Body::new))
                    });
                    let _=timeout(Duration::from_secs(3),hyper::server::conn::http1::Builder::new()
                        .keep_alive(false).max_buf_size(8192).serve_connection(TokioIo::new(stream),service)).await;
                });
            }
            Ok::<(),anyhow::Error>(())
        });
    }
    let expected = config.node_id.clone();
    loop {
        tokio::select! {
            _=stop.changed()=>{if *stop.borrow(){break}},
            job=jobs.join_next()=>{if let Some(result)=job{result??;}break},
            _=sleep(Duration::from_secs(2))=>{
                let result=network::identity().await;
                let mut g=gate.write().unwrap_or_else(|e|e.into_inner());
                g.checked=Instant::now();
                match result{
                    Ok(id)=>{
                        g.valid=bind_addresses.is_empty() || (network::matching_identity(&expected,&id.node_id,true) && bind_addresses.iter().all(|ip|id.addresses.contains(ip)));
                        if g.valid {g.node_id=id.node_id;}
                    },
                    Err(_)=>g.valid=false,
                }
            }
        }
    }
    gate.write().unwrap_or_else(|e| e.into_inner()).valid = false;
    jobs.abort_all();
    Ok(())
}
