// This harness is also its own child-process fixture. No fixture modes are compiled
// into the shipping executable, and the supervisor below is the production source.
#[cfg(any(target_os = "windows", target_os = "linux"))]
use tailwatch_agent::{collect, model};
#[cfg(any(target_os = "windows", target_os = "linux"))]
#[path = "../src/gpu_worker.rs"]
#[allow(dead_code)]
mod gpu_worker;

fn main() {
    #[cfg(any(target_os = "windows", target_os = "linux"))]
    cases::run();
}

#[cfg(any(target_os = "windows", target_os = "linux"))]
mod cases {
    use super::*;
    use std::{
        io::Write,
        path::{Path, PathBuf},
        process::{Child, Command, Stdio},
        time::{Duration, Instant},
    };

    const SAMPLE: &str = r#"{"readings":[{"id":"test","name":"fixture GPU","vendor":"fixture","utilization":{"value":42,"status":"ok","age_ms":0},"metric":"active_time","memory":null}],"status":"ok"}"#;

    struct Scratch(PathBuf);
    impl Scratch {
        fn new() -> Self {
            let path = std::env::temp_dir().join(format!("tailwatch-gpu-{}", uuid::Uuid::new_v4()));
            std::fs::create_dir(&path).unwrap();
            Self(path)
        }
        fn marker(&self) -> PathBuf {
            self.0.join("children")
        }
        fn pids(&self) -> Vec<u32> {
            std::fs::read_to_string(self.marker())
                .unwrap_or_default()
                .lines()
                .filter_map(|s| s.parse().ok())
                .collect()
        }
    }
    impl Drop for Scratch {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }
    fn await_condition(message: &str, check: impl Fn() -> bool) {
        let started = Instant::now();
        while !check() {
            assert!(started.elapsed() < Duration::from_secs(5), "{message}");
            std::thread::sleep(Duration::from_millis(10));
        }
    }
    fn timing() -> gpu_worker::Timing {
        gpu_worker::Timing {
            deadline: Duration::from_millis(500),
            initial_retry: Duration::from_millis(300),
            max_retry: Duration::from_millis(900),
            healthy_reset: Duration::from_secs(10),
        }
    }
    fn command(mode: &str, scratch: &Scratch) -> Command {
        let mut command = Command::new(std::env::current_exe().unwrap());
        command.arg("fixture").arg(mode).arg(scratch.marker());
        command
    }
    fn fixture(mode: &str, marker: &Path) {
        if mode == "blocked-output" {
            gpu_worker::watch_parent().unwrap();
            let mut output = std::io::stdout().lock();
            std::fs::write(marker, std::process::id().to_string()).unwrap();
            output.write_all(&vec![b' '; 1024 * 1024]).unwrap();
            panic!("fixture output pipe unexpectedly accepted a megabyte");
        }
        if mode == "parent" {
            let mut child = Command::new(env!("CARGO_BIN_EXE_tailwatch-agent"))
                .arg("gpu-worker")
                .stdin(Stdio::piped())
                .stdout(Stdio::null())
                .stderr(Stdio::null())
                .spawn()
                .unwrap();
            std::fs::write(marker, child.id().to_string()).unwrap();
            std::thread::sleep(Duration::from_secs(60));
            let _ = child.kill();
            let _ = child.wait();
            return;
        }
        // Fixtures obey the same parent-pipe contract as the shipping worker.
        // Polling Child::wait must never implicitly close this liveness pipe.
        gpu_worker::watch_parent().unwrap();
        let mut file = std::fs::OpenOptions::new()
            .create(true)
            .append(true)
            .open(marker)
            .unwrap();
        writeln!(file, "{}", std::process::id()).unwrap();
        drop(file);
        let mode =
            if mode == "recover" && std::fs::read_to_string(marker).unwrap().lines().count() > 1 {
                "healthy"
            } else {
                mode
            };
        let mut stdout = std::io::stdout().lock();
        if mode == "silent" {
            std::thread::sleep(Duration::from_secs(60));
            return;
        }
        if mode == "early-exit" {
            std::process::exit(73);
        }
        writeln!(stdout, "{SAMPLE}").unwrap();
        stdout.flush().unwrap();
        std::thread::sleep(Duration::from_millis(150));
        match mode {
            "crash" | "recover" => std::process::abort(),
            "hang" => std::thread::sleep(Duration::from_secs(60)),
            "malformed" => {
                writeln!(stdout, "not JSON").unwrap();
                stdout.flush().unwrap();
                std::thread::sleep(Duration::from_secs(60));
            }
            "oversized" => {
                stdout.write_all(&vec![b' '; 65537]).unwrap();
                stdout.flush().unwrap();
                std::thread::sleep(Duration::from_secs(60));
            }
            "partial" => {
                stdout.write_all(b"{\"readings\":").unwrap();
                stdout.flush().unwrap();
                std::thread::sleep(Duration::from_secs(60));
            }
            "healthy" => loop {
                writeln!(stdout, "{SAMPLE}").unwrap();
                stdout.flush().unwrap();
                std::thread::sleep(Duration::from_millis(100));
            },
            _ => panic!("unknown fixture"),
        }
    }

    #[cfg(windows)]
    fn exited(pid: u32) -> bool {
        use std::ffi::c_void;
        #[link(name = "kernel32")]
        unsafe extern "system" {
            fn OpenProcess(access: u32, inherit: i32, pid: u32) -> *mut c_void;
            fn WaitForSingleObject(handle: *mut c_void, timeout: u32) -> u32;
            fn CloseHandle(handle: *mut c_void) -> i32;
        }
        unsafe {
            let handle = OpenProcess(0x0010_0000, 0, pid); // SYNCHRONIZE
            if handle.is_null() {
                return true;
            }
            let done = WaitForSingleObject(handle, 0) == 0;
            CloseHandle(handle);
            done
        }
    }
    #[cfg(target_os = "linux")]
    fn exited(pid: u32) -> bool {
        !Path::new(&format!("/proc/{pid}")).exists()
    }

    fn assert_reaped(scratch: &Scratch) {
        let pids = scratch.pids();
        assert!(!pids.is_empty(), "fixture never started");
        for pid in pids {
            await_condition("owned worker was not reaped", || exited(pid));
        }
    }
    fn failed_workers_clear_readings_and_reap() {
        for (mode, expected) in [
            ("crash", "worker_exited"),
            ("hang", "worker_timeout"),
            ("partial", "worker_timeout"),
            ("malformed", "worker_invalid_output"),
            ("oversized", "worker_invalid_output"),
        ] {
            let scratch = Scratch::new();
            let worker = gpu_worker::GpuWorker::start_command(command(mode, &scratch), timing());
            await_condition("valid worker sample missing", || {
                collect::cached_gpus(&worker.cache)
                    .0
                    .first()
                    .is_some_and(|g| g.utilization.value == Some(42.0))
            });
            await_condition("worker failure not reported", || {
                collect::cached_gpus(&worker.cache).1 == expected
            });
            assert!(
                collect::cached_gpus(&worker.cache).0.is_empty(),
                "failed worker retained live GPU metrics: {mode}"
            );
            let attempts = scratch.pids().len();
            std::thread::sleep(Duration::from_millis(120));
            assert_eq!(
                scratch.pids().len(),
                attempts,
                "retry did not back off: {mode}"
            );
            let before = Instant::now();
            drop(worker);
            assert!(
                before.elapsed() < Duration::from_secs(2),
                "disposal blocked during {mode}"
            );
            assert_reaped(&scratch);
            println!("PASS: {mode} invalidates GPU cache and reaps child");
        }
    }
    fn persistent_success_and_shutdown() {
        let scratch = Scratch::new();
        let worker = gpu_worker::GpuWorker::start_command(command("healthy", &scratch), timing());
        await_condition("valid frame missing", || {
            collect::cached_gpus(&worker.cache).1 == "ok"
        });
        std::thread::sleep(Duration::from_millis(750));
        assert_eq!(
            scratch.pids().len(),
            1,
            "healthy worker was unnecessarily restarted"
        );
        assert!(collect::cached_gpus(&worker.cache).0[0].utilization.fresh());
        drop(worker);
        assert_reaped(&scratch);
        println!("PASS: persistent healthy worker and explicit disposal");
    }
    fn production_worker_remains_live_with_parent() {
        let mut command = Command::new(env!("CARGO_BIN_EXE_tailwatch-agent"));
        command.arg("gpu-worker");
        let worker = gpu_worker::GpuWorker::start_command(command, gpu_worker::Timing::default());
        await_condition(
            "production worker failed to publish a sample while parent is alive",
            || {
                worker
                    .cache
                    .read()
                    .unwrap()
                    .as_ref()
                    .is_some_and(|s| !s.status.starts_with("worker_"))
            },
        );
        let captured = worker.cache.read().unwrap().as_ref().unwrap().captured;
        await_condition(
            "production worker did not persist for a second sample",
            || {
                worker
                    .cache
                    .read()
                    .unwrap()
                    .as_ref()
                    .is_some_and(|s| s.captured > captured && !s.status.starts_with("worker_"))
            },
        );
        drop(worker);
        println!("PASS: production worker publishes repeated samples while parent is alive");
    }
    fn crash_recovery() {
        let scratch = Scratch::new();
        let worker = gpu_worker::GpuWorker::start_command(command("recover", &scratch), timing());
        await_condition("crash not reported", || {
            collect::cached_gpus(&worker.cache).1 == "worker_exited"
        });
        await_condition("valid readings did not recover", || {
            scratch.pids().len() >= 2 && collect::cached_gpus(&worker.cache).1 == "ok"
        });
        assert_eq!(
            collect::cached_gpus(&worker.cache).0[0].utilization.value,
            Some(42.0)
        );
        drop(worker);
        assert_reaped(&scratch);
        println!("PASS: crashed worker restarts and resumes valid data");
    }
    fn repeated_failures_have_capped_backoff() {
        let scratch = Scratch::new();
        let worker = gpu_worker::GpuWorker::start_command(command("crash", &scratch), timing());
        await_condition("first worker never launched", || !scratch.pids().is_empty());
        std::thread::sleep(Duration::from_millis(1900));
        assert!(
            scratch.pids().len() <= 3,
            "briefly valid output reset backoff before sustained health"
        );
        await_condition("capped retry stopped making progress", || {
            scratch.pids().len() >= 5
        });
        await_condition("fifth crash not detected", || {
            collect::cached_gpus(&worker.cache).1 == "worker_exited"
        });
        drop(worker);
        assert_reaped(&scratch);
        println!("PASS: repeated failures grow retry delay and continue at cap");
    }
    fn startup_hang_and_launch_failure() {
        let scratch = Scratch::new();
        let worker = gpu_worker::GpuWorker::start_command(command("silent", &scratch), timing());
        await_condition("startup hang deadline missing", || {
            collect::cached_gpus(&worker.cache).1 == "worker_timeout"
        });
        drop(worker);
        assert_reaped(&scratch);
        let worker = gpu_worker::GpuWorker::start_command(
            Command::new(scratch.0.join("missing-executable")),
            timing(),
        );
        await_condition("launch failure missing", || {
            collect::cached_gpus(&worker.cache).1 == "worker_start_failed"
        });
        assert!(collect::cached_gpus(&worker.cache).0.is_empty());
        drop(worker);
        println!("PASS: startup hang and launch failure remain unavailable");
    }
    fn wait_for_exit(child: &mut Child) {
        let started = Instant::now();
        loop {
            if let Some(status) = child.try_wait().unwrap() {
                assert!(status.success(), "worker command failed: {status}");
                break;
            }
            if started.elapsed() > Duration::from_secs(5) {
                child.kill().unwrap();
                child.wait().unwrap();
                panic!("worker survived parent pipe closure");
            }
            std::thread::sleep(Duration::from_millis(20));
        }
    }
    fn parent_lifetime() {
        let mut child = Command::new(env!("CARGO_BIN_EXE_tailwatch-agent"))
            .arg("gpu-worker")
            .stdin(Stdio::piped())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .spawn()
            .unwrap();
        drop(child.stdin.take());
        wait_for_exit(&mut child);
        let scratch = Scratch::new();
        let mut parent = command("parent", &scratch)
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .spawn()
            .unwrap();
        await_condition("fixture parent failed to launch worker", || {
            !scratch.pids().is_empty()
        });
        let pid = scratch.pids()[0];
        assert!(!exited(pid));
        parent.kill().unwrap();
        parent.wait().unwrap();
        await_condition("worker survived abrupt parent termination", || exited(pid));
        println!("PASS: production worker exits on pipe EOF and abrupt parent death");
    }
    fn blocked_output_and_active_hang_disposal() {
        let scratch = Scratch::new();
        let mut child = command("blocked-output", &scratch)
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::null())
            .spawn()
            .unwrap();
        await_condition("blocked writer fixture did not start", || {
            !scratch.pids().is_empty()
        });
        std::thread::sleep(Duration::from_millis(100));
        assert!(child.try_wait().unwrap().is_none());
        drop(child.stdin.take());
        wait_for_exit(&mut child);

        let scratch = Scratch::new();
        let mut slow = timing();
        slow.deadline = Duration::from_secs(30);
        let worker = gpu_worker::GpuWorker::start_command(command("silent", &scratch), slow);
        await_condition("silent fixture did not start", || {
            !scratch.pids().is_empty()
        });
        let started = Instant::now();
        drop(worker);
        assert!(
            started.elapsed() < Duration::from_secs(2),
            "disposal waited for output deadline"
        );
        assert_reaped(&scratch);
        println!(
            "PASS: blocked writer obeys parent EOF; active hang disposal interrupts read deadline"
        );
    }
    pub fn run() {
        let args: Vec<_> = std::env::args().collect();
        if args.get(1).is_some_and(|a| a == "fixture") {
            fixture(&args[2], Path::new(&args[3]));
            return;
        }
        parent_lifetime();
        production_worker_remains_live_with_parent();
        blocked_output_and_active_hang_disposal();
        persistent_success_and_shutdown();
        failed_workers_clear_readings_and_reap();
        crash_recovery();
        repeated_failures_have_capped_backoff();
        startup_hang_and_launch_failure();
    }
}
