use std::net::IpAddr;
use tailwatch_agent::network::{allowed,is_tailnet,matching_identity};
fn ip(s:&str)->IpAddr { s.parse().unwrap() }
#[test] fn rejects_public_lan_and_wildcard_bind_addresses() {
    for s in ["0.0.0.0","::","192.168.1.1","8.8.8.8","100.63.255.255","100.128.0.1","fe80::1"] { assert!(!is_tailnet(ip(s)), "{s}"); }
    for s in ["100.64.0.1","100.127.255.254","fd7a:115c:a1e0::1"] { assert!(is_tailnet(ip(s)), "{s}"); }
}
#[test] fn only_explicit_peer_and_loopback_are_allowed() {
    let peers=[ip("100.64.0.5")];
    assert!(allowed(ip("100.64.0.5"), &peers));
    assert!(allowed(ip("::ffff:100.64.0.5"), &peers));
    assert!(allowed(ip("127.0.0.1"), &[]));
    assert!(!allowed(ip("100.64.0.6"), &peers));
    assert!(!allowed(ip("192.168.0.5"), &peers));
    assert!(!allowed(ip("100.64.0.5"), &[]));
}
#[test] fn network_identity_loss_fails_closed() {
    assert!(matching_identity("node-a","node-a",true));
    assert!(!matching_identity("node-a","node-b",true));
    assert!(!matching_identity("node-a","node-a",false));
    assert!(!matching_identity("","",true));
}

