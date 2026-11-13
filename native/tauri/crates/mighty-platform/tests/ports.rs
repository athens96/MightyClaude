//! The ports' contracts, against the fakes: what every OS implementation must also do.

use std::collections::BTreeMap;
use std::path::PathBuf;
use std::sync::{Arc, Mutex};
use std::time::Duration;

use mighty_platform::fake::{self, FakeProcessHost, FakePtyHost, FakeScript, FakeSurfaces, MemoryVault, RecordingNotifier, SurfaceEvent};
use mighty_platform::*;

fn absolute(name: &str) -> PathBuf {
    if cfg!(windows) { PathBuf::from(format!("C:\\{name}")) } else { PathBuf::from(format!("/{name}")) }
}

#[tokio::test]
async fn a_capture_feeds_stdin_and_collects_both_outputs() {
    let host = FakeProcessHost::default();
    host.push(FakeScript { stdout: b"out".to_vec(), stderr: b"err".to_vec(), exit_code: 3, hang: false });
    let request = SpawnRequest::new(absolute("bin\\tool.exe"), ["--flag"], absolute("work"));
    let captured = capture(&host, request, Some(b"input".to_vec()), Duration::from_secs(5), 1024).await.unwrap();
    assert_eq!(captured, Captured { exit_code: 3, stdout: b"out".to_vec(), stderr: b"err".to_vec() });
    assert_eq!(host.input(0), b"input");
    let spawned = host.spawned();
    assert_eq!(spawned[0].env.get("NO_COLOR").map(String::as_str), Some("1"), "C# StartInfo always sets NO_COLOR");
    assert_eq!(spawned[0].args, ["--flag"]);
}

#[tokio::test]
async fn a_capture_kills_a_child_past_its_timeout_or_its_output_limit() {
    let host = FakeProcessHost::default();
    host.push(FakeScript { hang: true, ..FakeScript::default() });
    let request = SpawnRequest::new(absolute("bin\\tool.exe"), Vec::<String>::new(), absolute("work"));
    let error = capture(&host, request.clone(), None, Duration::from_millis(50), 1024).await.unwrap_err();
    assert!(matches!(error, PlatformError::TimedOut(_)), "{error}");

    host.push(FakeScript { stdout: vec![b'x'; 2048], ..FakeScript::default() });
    let error = capture(&host, request, None, Duration::from_secs(5), 1024).await.unwrap_err();
    assert!(matches!(error, PlatformError::TooLarge(1024)), "{error}");
}

#[tokio::test]
async fn a_killed_child_ends_and_relative_paths_are_refused() {
    let host = FakeProcessHost::default();
    host.push(FakeScript { hang: true, ..FakeScript::default() });
    let child = host.spawn(SpawnRequest::new(absolute("tool"), Vec::<String>::new(), absolute("work"))).await.unwrap();
    child.kill();
    assert_eq!(child.wait().await.unwrap(), fake::KILLED);
    child.close().await.unwrap();
    assert!(host.spawn(SpawnRequest::new("tool", Vec::<String>::new(), "relative")).await.is_err());
}

#[tokio::test]
async fn a_terminal_takes_input_resizes_and_closes() {
    let host = FakePtyHost::default();
    let request = PtyRequest {
        program: host.default_shell(),
        args: vec![],
        cwd: absolute("work"),
        env: BTreeMap::from([("PATH".into(), "x".into())]),
        size: PtySize { columns: 5000, rows: 0 },
    };
    let mut session = host.open(request.clone()).await.unwrap();
    assert_eq!(host.sizes(), [PtySize { columns: 1000, rows: 1 }], "sizes are clamped as C# clamps them");
    assert!(session.control.write(b"echo"));
    assert_eq!(session.output.recv().await.unwrap(), b"echo");
    assert!(!session.control.write(&vec![0; 65_537]), "more than 65,536 UTF-16 units at once is refused");
    let korean = "한".repeat(30_000);
    assert!(session.control.write(korean.as_bytes()), "the limit counts UTF-16 units as C#'s string.Length does, not the 90,000 bytes");
    assert_eq!(session.output.recv().await.unwrap(), korean.as_bytes());
    session.control.resize(PtySize { columns: 120, rows: 40 }).unwrap();
    assert!(session.control.process_ids().is_some());
    // A close does not wait for the output to be read: this is still unread when it returns, as a real console's output may be.
    assert!(session.control.write(b"unread"));
    tokio::time::timeout(Duration::from_secs(10), session.control.close()).await.expect("a close returns with its output unread").unwrap();
    // What was queued before the close can still be read, then the output ends (a real console adds its final frame first).
    let rest = tokio::time::timeout(Duration::from_secs(10), async {
        let mut rest = Vec::new();
        while let Some(bytes) = session.output.recv().await {
            rest.extend(bytes);
        }
        rest
    })
    .await
    .expect("the output ends with the terminal");
    assert_eq!(rest, b"unread");
    assert!(!session.control.write(b"late"));
    assert!(session.control.process_ids().is_none());

    let bad = PtyRequest { env: BTreeMap::from([("A=B".into(), "x".into())]), ..request };
    assert!(host.open(bad).await.is_err());
}

#[test]
fn a_vault_forgets_nothing_it_kept_and_misses_nothing_it_lacks() {
    let vault = MemoryVault::default();
    assert_eq!(vault.load("c1"), None);
    vault.save("c1", "secret").unwrap();
    assert_eq!(vault.load("c1").as_deref(), Some("secret"));
    vault.delete("c1").unwrap();
    vault.delete("c1").unwrap();
    assert_eq!(vault.load("c1"), None);
    assert!(!format!("{vault:?}").contains("secret"));
}

#[test]
fn environments_and_pairing_never_print_their_secrets() {
    let environment = CliEnvironment { values: BTreeMap::from([("TOKEN".into(), "hunter2".into())]), source: EnvironmentSource::Profile };
    assert!(!format!("{environment:?}").contains("hunter2"));
    let identity = PairingIdentity {
        host_id: "h".into(),
        host_token: "t".repeat(64),
        pairing_key: "pairing-secret".into(),
        secret_key: [7; 32],
        devices: vec![],
    };
    let shown = format!("{identity:?}");
    assert!(!shown.contains("pairing-secret") && !shown.contains(&"t".repeat(64)));
    let request = SpawnRequest::new(absolute("tool"), Vec::<String>::new(), absolute("w")).env("API_KEY", "sk-123");
    assert!(!format!("{request:?}").contains("sk-123"));
}

#[tokio::test]
async fn a_notification_click_names_its_pane() {
    let notifier = RecordingNotifier::default();
    let clicked = Arc::new(Mutex::new(None));
    let seen = clicked.clone();
    notifier.on_activated(Arc::new(move |pane| *seen.lock().unwrap() = Some(pane)));
    notifier.notify(Notification { title: "W · pane".into(), body: "done".into(), pane_id: Some("p1".into()) }).await.unwrap();
    notifier.click(0);
    assert_eq!(*clicked.lock().unwrap(), Some(Some("p1".to_string())));
}

#[tokio::test]
async fn the_firewall_rule_follows_apply() {
    let firewall = fake::FakeFirewall::default();
    assert_eq!(firewall.read().await.unwrap(), FirewallRule::Missing);
    assert_eq!(firewall.apply(vec![43137]).await.unwrap(), FirewallChange::Applied);
    assert_eq!(firewall.read().await.unwrap(), FirewallRule::Present { ports: vec![43137] });
    firewall.decline_next();
    assert_eq!(firewall.apply(vec![]).await.unwrap(), FirewallChange::Declined);
    assert_eq!(firewall.apply(vec![]).await.unwrap(), FirewallChange::Applied);
    assert!(firewall.apply(vec![80]).await.is_err());
}

#[tokio::test]
async fn a_closed_surface_refuses_every_call() {
    let surfaces = FakeSurfaces::default();
    assert_eq!(surfaces.drawing(SurfaceKind::Browser), Drawing::Native);
    assert_eq!(surfaces.drawing(SurfaceKind::Terminal), Drawing::Web);
    let rect = DeviceRect { x: 1, y: 2, width: 30, height: 40 };
    let options = BrowserOptions { pane_id: "p".into(), url: "about:blank".into(), data_dir: absolute("b"), bounds: rect };
    let view = surfaces.open_browser(options).await.unwrap();
    view.set_visible(false).unwrap();
    assert_eq!(view.inspect().await.unwrap(), SurfaceState { bounds: rect, visible: false, url: Some("about:blank".into()) });
    view.close().unwrap();
    assert!(view.place(rect).is_err());
    assert_eq!(surfaces.events().last(), Some(&SurfaceEvent::Closed { pane: "p".into() }));
    assert!(surfaces.open_pet().await.err().is_some_and(|error| error.is_unsupported()));
}

#[test]
fn a_fake_platform_has_every_port() {
    let root = absolute("fake-root");
    let platform = fake::platform(&root, Os::Macos);
    assert_eq!(platform.profile, PlatformProfile::macos());
    assert_eq!(platform.paths.state_file(), root.join("data").join("workspace-state.json"));
    assert_eq!(platform.prefs.get("delegation.enabled").unwrap(), None);
    assert!(platform.pairing.load().unwrap().is_none());
    assert!(platform.updates.install_location().is_none());
}
