//! No child's job handle outlives its close or a failed launch (C# closes the job in `finally`, ChildProcess.cs:81 and 165). Each
//! check counts this process's open handles, so they run in a test binary of their own, one at a time.
#![cfg(windows)]

mod common;

use std::sync::{Mutex, MutexGuard};
use std::task::Poll;
use std::time::Duration;

use common::*;
use mighty_platform::ProcessHost;
use mighty_platform_windows::WindowsProcessHost;
use windows_sys::Win32::System::Threading::{GetCurrentProcess, GetProcessHandleCount};

static SERIAL: Mutex<()> = Mutex::new(());

fn serial() -> MutexGuard<'static, ()> {
    SERIAL.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
}

fn handles() -> u32 {
    let mut count = 0;
    // SAFETY: the current process's pseudo handle and an out-pointer.
    assert_ne!(unsafe { GetProcessHandleCount(GetCurrentProcess(), &mut count) }, 0);
    count
}

fn runtime() -> tokio::runtime::Runtime {
    tokio::runtime::Builder::new_multi_thread().enable_all().build().unwrap()
}

/// A child started, killed and closed, so that what the first launch makes once (the hidden console, the runtime's I/O) is there
/// before the handles are counted.
async fn warm_up(directory: &std::path::Path) {
    let pid = directory.join("warm.pid");
    let child = WindowsProcessHost::new().spawn(fake_request(&["--long-child", &pid.to_string_lossy()], directory)).await.unwrap();
    pid_from(&pid).await;
    child.close().await.unwrap();
}

/// Waits until this process holds no more handles than `before`: whatever a child's end leaves for a moment (its exit wait's
/// process handle) goes, and a job handle kept open would not.
async fn back_to(before: u32, what: &str) {
    until(|| handles() <= before, 10_000, what).await;
}

/// A close dropped half-way (its task cancelled) has still closed the job, and the next close waits for the child and finishes:
/// the child is dead when it returns and no handle of it is left.
#[test]
fn a_cancelled_close_closes_the_job_and_the_next_close_finishes() {
    let _serial = serial();
    let runtime = runtime();
    runtime.block_on(async {
        let directory = temp("cancelled-close");
        warm_up(&directory).await;
        let before = handles();
        let pid = directory.join("long.pid");
        let child = WindowsProcessHost::new().spawn(fake_request(&["--long-child", &pid.to_string_lossy()], &directory)).await.unwrap();
        let id = pid_from(&pid).await;
        let control = child.control();
        drop(child);
        // Polled once (the kill, then the wait for the exit, which a terminated process takes longer than that to reach) and
        // dropped, as a cancelled task drops it.
        let mut closing = control.close();
        let first = std::future::poll_fn(|context| Poll::Ready(closing.as_mut().poll(context))).await;
        assert!(first.is_pending(), "the close is dropped while it waits for the exit");
        drop(closing);
        tokio::time::timeout(Duration::from_secs(10), control.close()).await.expect("the second close in 10 s").unwrap();
        assert!(!alive(id), "the second close returned while the child was alive");
        drop(control);
        back_to(before, "the child's job handle closed").await;
        remove_when_free(&directory).await;
    });
}

/// A launch whose pipes cannot join the runtime's I/O (here: a runtime without an I/O driver, where tokio panics) fails before
/// any child or job exists, so it leaves no child running and no job handle open.
#[test]
fn a_failed_launch_leaves_no_child_and_no_job() {
    let _serial = serial();
    let runtime = runtime();
    let without_io = tokio::runtime::Builder::new_current_thread().build().unwrap();
    let directory = temp("failed-launch");
    runtime.block_on(warm_up(&directory));
    let before = handles();
    let pid = directory.join("long.pid");
    let request = fake_request(&["--long-child", &pid.to_string_lossy()], &directory);
    let launched = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| without_io.block_on(WindowsProcessHost::new().spawn(request))));
    assert!(!matches!(launched, Ok(Ok(_))), "a pipe without an I/O driver fails the launch");
    drop(without_io);
    runtime.block_on(async {
        back_to(before, "the failed launch's handles closed").await;
        tokio::time::sleep(Duration::from_secs(2)).await;
        assert!(!pid.exists(), "the failed launch started its child");
        remove_when_free(&directory).await;
    });
}
