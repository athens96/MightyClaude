#!/usr/bin/env bash
# Exercise the production Swift owner against a fake asynchronous CEF bridge.
# Requires macOS + Command Line Tools, but no CEF download or SwiftPM build.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
TEST_DIR="$(mktemp -d "${TMPDIR:-/tmp}/mighty-browser-runtime.XXXXXX")"
trap 'rm -rf "$TEST_DIR"' EXIT
cat > "$TEST_DIR/bridge.c" <<'C'
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
static int initialized;
static void* parent;
static int closing;
static void record(char event) {
  FILE* file = fopen(getenv("MIGHTY_FAKE_LOG"), "a");
  if (file) { fputc(event, file); fclose(file); }
}
int32_t mighty_cef_load(const char* path) { (void)path; record('L'); return 1; }
int32_t mighty_cef_initialize(const char* root) {
  (void)root; record('I');
  if (getenv("MIGHTY_FAKE_INIT_FAIL")) return 0;
  initialized = 1; return 1;
}
int32_t mighty_cef_show(void* view, int32_t width, int32_t height,
                        const char* cache, const char* url) {
  (void)width; (void)height; (void)cache; (void)url;
  if (!initialized) return 0;
  parent = view; closing = 0; return 1;
}
int32_t mighty_cef_is_initialized(void) { return initialized; }
int32_t mighty_cef_is_browser_closed(void* view) { return parent != view; }
int32_t mighty_cef_has_browser(void* view) { return parent == view; }
int32_t mighty_cef_is_loading(void* view) { (void)view; return 0; }
void mighty_cef_close(void* view) { if (parent == view) closing = 4; }
void mighty_cef_work(void) {
  if (!initialized) abort();
  record('W');
  if (closing && --closing == 0) { parent = NULL; record('C'); }
}
void mighty_cef_shutdown(void) { parent = NULL; initialized = 0; record('S'); }
__attribute__((destructor)) static void unloaded(void) { record('U'); }
C
cat > "$TEST_DIR/main.swift" <<'SWIFT'
import AppKit

@main
struct BrowserRuntimeRegression {
    static func main() {
        let args = ProcessInfo.processInfo.arguments
        let runtime = CefBrowserRuntime.shared
        let failInitialization = ProcessInfo.processInfo.environment["MIGHTY_FAKE_INIT_FAIL"] != nil
        let logURL = URL(fileURLWithPath: ProcessInfo.processInfo.environment["MIGHTY_FAKE_LOG"]!)
        func events() -> String { (try? String(contentsOf: logURL, encoding: .utf8)) ?? "" }
        func check(_ valid: @autoclosure () -> Bool, _ description: String) {
            guard valid() else { fatalError(description) }
        }
        func pump(_ seconds: TimeInterval) {
            let deadline = Date().addingTimeInterval(seconds)
            while Date() < deadline {
                RunLoop.main.run(until: min(deadline, Date().addingTimeInterval(0.02)))
            }
        }
        check(!runtime.isMessagePumpRunning, "pump must start only after actual initialization")
        check(runtime.load(frameworkBinary: URL(fileURLWithPath: "/fake/cef"), bridgePath: args[1]), "load bridge")
        check(runtime.load(frameworkBinary: URL(fileURLWithPath: "/unused/cef"), bridgePath: "/does-not-exist"), "reuse process owner")
        check(events() == "L", "load CEF only once")
        check(!runtime.isMessagePumpRunning, "resolving CEF symbols must not start pump")
        check(runtime.initialize(cacheRoot: URL(fileURLWithPath: "/fake")) != failInitialization,
              "initialize explicitly before the app run loop")
        check(runtime.initialize(cacheRoot: URL(fileURLWithPath: "/fake")) != failInitialization,
              "repeated initialize must reuse the initial outcome")
        check(events().filter { $0 == "I" }.count == 1, "initialize CEF only once")
        var view: NSView? = NSView(frame: NSRect(x: 0, y: 0, width: 640, height: 480))
        weak var weakView = view
        let accepted = runtime.show(in: view!, profile: URL(fileURLWithPath: "/fake/profile"), url: URL(string: "about:blank")!)
        check(accepted != failInitialization, "propagate initialization outcome")
        check(runtime.isMessagePumpRunning != failInitialization, "pump only initialized CEF")
        runtime.close(view: view!)
        view = nil
        if failInitialization {
            check(runtime.pendingCloseCount == 0 && weakView == nil, "failed create has no pending parent")
            pump(0.1)
            check(!events().contains("W"), "failed initialization must not be pumped")
        } else {
            check(runtime.pendingCloseCount == 1 && weakView != nil, "retain host while CEF close is pending")
            pump(0.2)
            check(runtime.pendingCloseCount == 0 && weakView == nil, "release host only after close acknowledgement")
            check(events().contains("C"), "asynchronous close actually completed")
            let workBefore = events().filter { $0 == "W" }.count
            pump(0.1)
            check(events().filter { $0 == "W" }.count > workBefore, "pump survives last pane destruction")
            check(!events().contains("U"), "pane destruction must not unload callback code")
            var reopened: NSView? = NSView(frame: NSRect(x: 0, y: 0, width: 640, height: 480))
            check(runtime.show(in: reopened!, profile: URL(fileURLWithPath: "/fake/profile"), url: URL(string: "about:blank")!), "reopen a pane with same process runtime")
            runtime.close(view: reopened!)
            reopened = nil
            pump(0.2)
            check(runtime.pendingCloseCount == 0, "second close drained")
            check(events().filter { $0 == "L" }.count == 1, "reopen must not reload bridge")
        }
        runtime.shutDown()
        check(!runtime.isMessagePumpRunning && !runtime.isInitializedNow, "shutdown stops pump and CEF")
        let afterShutdown = events()
        pump(0.1)
        check(events() == afterShutdown, "no callbacks after shutdown")
        check(!events().contains("U"), "bridge remains loaded through process lifetime")
        print(failInitialization ? "PASS: failed initialization never starts pump" : "PASS: process owner, asynchronous host lifetime, reopen, shutdown")
    }
}
SWIFT
xcrun clang -dynamiclib "$TEST_DIR/bridge.c" -o "$TEST_DIR/FakeCEFBridge.dylib"
xcrun swiftc -parse-as-library -target "$(uname -m)-apple-macosx14.0" \
  -module-cache-path "$TEST_DIR/module-cache" \
  "$ROOT/native/macos/Sources/MightyClaude/CefBrowserRuntime.swift" \
  "$TEST_DIR/main.swift" -o "$TEST_DIR/browser-runtime-test"
MIGHTY_FAKE_LOG="$TEST_DIR/success.log" "$TEST_DIR/browser-runtime-test" "$TEST_DIR/FakeCEFBridge.dylib"
MIGHTY_FAKE_LOG="$TEST_DIR/failure.log" MIGHTY_FAKE_INIT_FAIL=1 "$TEST_DIR/browser-runtime-test" "$TEST_DIR/FakeCEFBridge.dylib"
