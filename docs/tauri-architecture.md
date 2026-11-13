# Tauri 앱의 기본 구조 — Mac과 Windows가 다른 모듈을 쓰는 방법

2026-11-13. 변환 계획(`docs/tauri-conversion.md`)의 바탕이 되는 구조. 원칙: **같은 것은 한 번만 쓰고, 다른 것은 같은 자리(포트)에 플랫폼마다 다른 구현을 끼운다.** Mac 터미널은 Ghostty, Windows 터미널은 xterm.js처럼 플랫폼마다 다른 모듈을 쓸 수 있어야 한다.

## 1. 층

```
┌──────────────────────────────────────────────────────────────────────┐
│ 페이지 (React, 공통)   native/web  ← 지금의 native/windows/web          │
│  ├ 웹으로 그리는 창: 흐름, 대화, 작성창, 카드, 설정 폼(NativeNode)…       │
│  └ NativeSlot: 플랫폼이 네이티브 뷰를 올릴 빈 자리(터미널, 브라우저…)     │
├──────────────── host-message / host_post (한 줄로 순서 보장) ────────────┤
│ 호스트 (Rust, 공통)    native/tauri/src-tauri                           │
│  브리지·델타·셸/작성창/카드 모델·NativeNode 폼·SurfaceManager·창 관리     │
├──────────────────────────────────────────────────────────────────────┤
│ 공통 로직 (Rust, OS 코드 없음)                                           │
│  mighty-core · mighty-run · mighty-graph · mighty-remote ·              │
│  mighty-delegation · mighty-tools          ── OS 일은 포트로만 부른다     │
├──────────────────────────── 포트 (trait) ────────────────────────────┤
│ mighty-platform: ProcessHost, PtyHost, SecretVault, PrefsStore,          │
│   Notifier, FilePicker, UpdateInstaller, Firewall, ScreenCapture, …     │
│   + Surface 종류: TerminalSurface, BrowserSurface, PetWindow, …          │
├───────────────────────────────┬──────────────────────────────────────┤
│ mighty-platform-windows (Rust) │ mighty-platform-macos (Rust)          │
│  Job Object, ConPTY, DPAPI,    │  posix_spawn/로그인 셸, Keychain,      │
│  레지스트리, WebView2 자식 뷰,   │  UserDefaults, …                       │
│  toast, netsh …                │  objc2(AppKit), Ghostty(libghostty C  │
│                                │  API), CEF·WKWebView 브라우저,          │
│                                │  ScreenCaptureKit, 한글 입력 보조 …     │
│                                │  — 모두 Rust                            │
└───────────────────────────────┴──────────────────────────────────────┘
```

규칙:
1. 공통 crate에는 `#[cfg(target_os)]`가 없다. OS가 다르면 반드시 포트를 하나 만든다.
2. 포트마다 **가짜 구현**이 있다. 공통 로직의 테스트(공통 기대값 파일, C# 테스트 이전분)는 가짜로 돌아서 Windows와 Mac CI 양쪽에서 똑같이 통과해야 한다.
3. 어떤 구현을 쓸지는 빌드 대상(`cfg(windows)` / `cfg(target_os = "macos")`)으로 `src-tauri`가 한 곳에서 정한다. 같은 OS 안에서 고를 수 있는 것(예: Mac 브라우저 CEF ↔ WKWebView)은 설정으로 고른다.
4. Mac 전용 네이티브 모듈도 **Rust로 쓴다**(정함 2026-11-13). AppKit·Foundation은 `objc2` 계열 crate로, Ghostty는 vendored libghostty의 **C API**(`ghostty_app_new`, `ghostty_surface_new`에 NSView를 넘김; `native/macos/Vendor/libghostty-spm`의 라이브러리)를 Rust에서 직접 부르고, CEF는 Rust CEF 바인딩으로 쓴다. 지금 Swift 코드(`LocalTerminalSession`, `HostTerminalView`, `CefBrowserEngine`, `BrowserBridge`, `HangulComposer` 등)는 동작의 기준으로 읽고 같은 동작을 Rust로 옮긴다.

## 2. 플랫폼마다 다른 것 (포트와 구현)

| 포트 | Windows | Mac |
|---|---|---|
| **TerminalSurface** (셸 창, 에이전트 터미널) | 웹: 페이지의 xterm.js + Rust ConPTY | **네이티브: Ghostty 뷰**(libghostty, Metal). PTY는 Ghostty가 가진다 |
| **BrowserSurface** (브라우저 창) | WebView2 자식 웹뷰(작업 공간별 프로필) | **CEF와 WKWebView 둘 다**, 설정 `browser.engineEnabled`로 선택(켜면 CEF, 끄면 WKWebView) |
| HtmlFrame (HTML 미리보기) | 페이지 iframe + 가상 호스트 | 같음(커스텀 프로토콜) |
| ProcessHost | CreateProcessW 일시정지 + Job Object, PATHEXT, `.cmd` → `cmd /d /s /c` | posix_spawn, 로그인 셸 `-ilc` 환경 |
| PtyHost (에이전트 PTY) | ConPTY | forkpty |
| ShellEnvironment | PowerShell 프로필 환경 캡처 | 로그인 셸 환경 캡처 |
| SecretVault | DPAPI | Keychain (`dev.mightycode.native.remote`) |
| PrefsStore | HKCU `Software\MightyCode` + 스냅샷 필드 | UserDefaults `dev.mightycode.native` |
| PairingStore (휴대폰) | `mobile-identity.json` 하나 | Mac 파일 4개 |
| Notifier | 토스트(AppNotification) | UNUserNotificationCenter |
| PetWindow | 레이어드 HWND(활성화 안 됨, 클릭 통과) | NSPanel(활성화 안 됨) |
| ScreenCapture / RemoteInput (화면 공유) | Windows Graphics Capture + SendInput + 저수준 훅 | ScreenCaptureKit + CGEvent |
| UpdateInstaller | `--update-helper` 손넘김(0.4.x와 같은 규약) | `install.sh` 방식(ditto, 두 번 이름 바꾸기) |
| Firewall (Tailscale 직접 연결) | `netsh advfirewall` (관리자 확인) | 없음 |
| AppMenu / DeepLink | 없음 / `mightycode://` 등록 | 메뉴 막대 / `mightycode://`, `mightyclaude://` |
| InputMethod 보조 | 없음 | 한글 조합 복구(`HangulComposer` 등) |
| FilePicker, Clipboard | Tauri dialog/clipboard | 같음 |

플랫폼마다 일부러 다르게 동작하는 규칙(예: 플러그인 목록 읽는 중 Mac은 `busy`, Windows는 읽기에 합류; CLI 업데이트 Homebrew ↔ winget)은 `PlatformProfile` 값 하나로 공통 로직에 넘긴다.

### 2.1 포트 목록 (W1에 확정, `native/tauri/crates/mighty-platform`)

계약(무엇을 보장하는지)은 각 trait의 문서 주석에 있고, 가짜 구현은 `mighty_platform::fake`, 아직 없는 구현은 `mighty_platform::unsupported::Unsupported`(모든 동작이 `PlatformError::Unsupported`)다. 공통 로직은 `Platform` 하나(아래 포트 전부 + `profile`)를 받는다. 화면 포트는 창이 있어야 해서 따로(`SurfaceFactory`) 만든다.

| 포트 | 한 줄 |
|---|---|
| `Paths` | 데이터 폴더(`%APPDATA%\MightyCode` / `~/Library/Application Support/MightyCode`, `--profile`이면 그 폴더), 이름 바꾸기 전 폴더, 이 기계 전용 폴더(`<local>/<identifier>`), 그 아래 상태 파일·`.bak`·로그·이미지 캐시·웹뷰·브라우저 데이터. 구현은 공통 `AppPaths` 하나에 OS마다 뿌리만 다름 |
| `ProcessHost` | `spawn(SpawnRequest{program(절대 경로), args, cwd, env(덧씌움), shell_command(Windows `.cmd`용)})` → `Child{stdin, stdout, stderr, control}`. 창 없음, UTF-8, **앱보다 오래 못 삶**(Windows: 일시정지로 만들어 kill-on-close Job에 넣은 뒤 시작; Mac: 프로세스 그룹) |
| `ChildControl` | `kill`(자식과 그 아래 전부), `wait` → 종료 코드, `close`(kill + 3초 대기 + Job이 빌 때까지 3초) |
| `capture()` | `ProcessHost` 위의 공통 함수: stdin을 쓰고 닫고, 두 출력을 한도까지 읽고, 시간 넘으면 죽임(Swift `ProcessCapture.run`) |
| `PtyHost` | `default_shell`, `shell_arguments(shell, command, no_profile)`, `open(PtyRequest{program, args, cwd, env(전체), size})` → `PtySession{output 채널, control}` |
| `PtyControl` | `write`(UTF-16 65,536단위 이하 — C# `text.Length`처럼 세므로 한글 30,000자(90,000바이트)도 받음), `resize`(2–1000 × 1–1000), `process_ids`, `busy`(셸이 명령을 실행 중인지 — Windows: Job의 셸 아닌 프로세스가 셸의 콘솔에 붙어 있음, C# `ShellProcessProbe`; 못 읽으면 None, 기본값 None — W4-A), `wait`, `close`(Job·콘솔 닫고 마지막 출력까지 읽음; 아무도 출력을 읽지 않아도 돌아옴) |
| `Clipboard` (W4-A) | `read_text` → 글자 없으면 None, `write_text`. Windows `CF_UNICODETEXT`(다른 프로그램이 잡고 있으면 잠깐 기다린 뒤 거절), Mac은 아직 `Unsupported`(Ghostty가 스스로 복사·붙여넣기). `Platform`에는 없고 `src-tauri/src/platform.rs`의 `clipboard()`로 받음 |
| `ShellEnvironment` | CLI가 시작할 환경: `resolve(workspace, force)`(실패 없음, 4초 한도, 10초 재사용), `current`, `invalidate`. 값은 로그·`Debug`에 안 나옴 |
| `SecretVault` | `load(id)` → 없거나 못 읽으면 None, `save(id, secret)`, `delete(id)`. Windows는 C# `DpapiRemoteKeyVault`와 같은 파일·엔트로피(바이트 단위로 서로 읽힘), Mac은 Keychain `dev.mightycode.native.remote` |
| `PrefsStore` | `get/set/remove(key)` → `PrefValue{Int, Text, Bool, Other}`; Windows `HKCU\Software\MightyCode`, Mac UserDefaults `dev.mightycode.native`, 이름은 같음(`delegation.enabled`, `setup.neverShow`, `textScale` …) |
| `PairingStore` | 휴대폰 페어링 신원(`host_id`, `host_token`, `pairing_key`, 비밀 키, 기기 목록)을 `load`/`save`(한 번에 함께 저장) |
| `Notifier` | `notify(Notification{title, body, pane_id})`, `on_activated(클릭 → pane id)` |
| `Firewall` | `read` → 규칙 없음/있음(포트)/못 읽음, `apply(ports)`(빈 목록은 삭제, 관리자 확인) — Mac은 `Unsupported` |
| `UpdateInstaller` | `install_location`, `hand_off(StagedUpdate, relaunch)`(도우미를 띄우고 돌아옴; 0.4.x `--update-helper` 인자 그대로) |
| `SurfaceFactory` | 창 하나의 네이티브 뷰: `drawing(kind)` → `web`/`native`, `open_browser(BrowserOptions)`, `open_terminal(TerminalOptions)`, `open_pet()` |
| `NativeSurface` | `place(DeviceRect)`, `set_visible`, `focus`, `snapshot`(PNG `data:` URL 또는 None), `inspect`(OS가 말하는 실제 위치·보임), `close` |
| `BrowserSurface` | `NativeSurface` + `navigate(url)`, `go_back`, `go_forward`, `reload` |
| `TerminalSurface` | `NativeSurface` + `send_text`, `set_font`, `set_theme` (Mac Ghostty, PTY는 뷰가 가짐) |
| `PetWindow` | `show_at(LogicalRect)`, `hide`, `set_click_through`, `close` |
| `PlatformProfile` | 값: `os`, `path_style`, `plugin_list_while_reading`(Busy/Join), `cli_updater`(Homebrew/Winget), `settings_store`, `pairing_layout`, `agent_sign_in`, `mcp_transport`, `m1_platform`("win32"/"darwin"), `remote_decodes_request_paths`, `direct_connection_firewall`, `update_needs_public_key`, `build_update_url_wins`, `update_rechecks_sha256`, `auto_update_keys`, `saves_files_pane` |

W1 현재 실제 구현: 양쪽 `Paths`; Windows `PrefsStore` 읽기, `SecretVault`(DPAPI), `SurfaceFactory`의 브라우저(Tauri 자식 웹뷰), `ProcessHost`·`PtyHost`·`ShellEnvironment`(W1-C, C# `ChildProcess`·`PseudoTerminal`·`WindowsShellEnvironment` 이전). 나머지는 `Unsupported`. 어떤 구현을 쓸지는 `src-tauri/src/platform.rs` 한 곳에서 `cfg`로 정한다.

### 2.2 Windows 프로세스 층이 C#과 일부러 다른 점 (W1-C, `mighty-platform-windows`)

C#이 기준이다. 아래만 일부러 다르게 했고, 각각 코드 주석에 같은 이유가 있다.

| 곳 | C# | 여기 | 이유 |
|---|---|---|---|
| 셸 환경 캡처가 1 MiB(stdout 바이트)·1 Mi 문자(stderr)를 넘음 (`environment.rs` `read_bounded`·`discard_bounded`) | `InvalidDataException`을 던지고, `ResolveAsync`가 잡지 않아 `RefreshAsync`가 실패한다(WindowsShellEnvironment.cs:146, 155) | 실패한 캡처로 본다: 기준 환경(레지스트리로 새로 읽은 프로세스 환경)으로 돌아가고, 전에 저장한 캡처도 버린다. `resolve`는 실패하지 않는다 | 프로필이 많이 출력한다고 CLI가 환경 없이 남으면 안 된다. 포트 계약(`ShellEnvironment::resolve`는 실패 없음)과도 맞는다 |
| `ProcessHost`가 자식에게 넘기는 핸들 (`process.rs`) | `bInheritHandles = TRUE`와 launch gate뿐(ChildProcess.cs:152) | `PROC_THREAD_ATTRIBUTE_HANDLE_LIST`로 세 파이프 끝만 넘긴다 | 다른 스레드의 상속 가능한 핸들이 자식에게 새지 않게(포트 계약). 반대 방향(앱의 다른 spawn이 이 파이프 끝을 물려받음)은 `clippy.toml`이 막는다(README "Child processes") |
| `ChildControl::close` (`process.rs`) | Job의 활성 프로세스 수가 0이 될 때까지 3초 | 그와 함께, kill 때 Job에 있던 프로세스 각각이 실제로 끝날 때까지(같은 3초 안) | 활성 수는 종료가 끝나기 전에 0이 된다. C#의 "자손이 살아 있는데 Job 닫기가 돌아옴" 검사가 묻는 것을 지킨다 |
| PTY 출력 (`pty.rs`) | 읽는 스레드가 조각마다 `receive`를 바로 부르고 기다리지 않는다(PseudoTerminal.cs:31) | 출력은 64조각짜리 채널이다. 평소에는 채널이 차면 기다리고(메모리가 묶임), close가 시작된 뒤에는 자리가 없는 조각을 버린다 | 아무도 출력을 읽지 않아도 close가 돌아오고, 24H2 이전 `ClosePseudoConsole`이 파이프가 비기를 기다리다 멈추지 않게 |

## 3. NativeSlot: 페이지 위에 네이티브 뷰 올리기

지금 Windows의 브라우저 창이 쓰는 방식(`browserBounds`)을 모든 네이티브 화면으로 넓힌다.

1. **능력 알림:** `ready` 뒤 호스트가 `{kind:'host', surfaces:{terminal:'web'|'native', browser:'native'}, platform:'windows'|'macos'}`를 보낸다. 페이지는 `terminal:'web'`이면 지금처럼 xterm.js를 그리고, `'native'`면 빈 `NativeSlot`을 그린다.
2. **자리 알림:** `NativeSlot`은 `surfaceBounds{paneId, surface, rect(기기 픽셀), visible, occluded}`를 보낸다(크기·위치·스크롤·탭 전환 때마다, 한 프레임에 한 번).
3. **올리기:** 호스트의 `SurfaceManager`가 플랫폼 구현에게 그 자리에 뷰를 만들고·옮기고·숨기게 한다(Mac: 창의 contentView에 NSView를 웹뷰 위로 추가, Windows: 자식 웹뷰나 자식 HWND).
4. **가림 처리:** 페이지의 대화 상자·메뉴·팝오버가 슬롯을 덮으면 `occluded:true` → 네이티브 뷰를 숨기고, 숨기기 직전 화면을 이미지로 받아 슬롯에 대신 그린다(깜박임 없이 대화 상자가 위에 보이게). 지금 Windows 브라우저 창이 하는 규칙을 그대로 넓힌 것.
5. **키보드:** 네이티브 뷰가 포커스를 가진 동안 앱 단축키(⌘T, ⌘K, Ctrl+Tab 등)는 호스트가 받아 페이지로 넘긴다. 포커스를 페이지로 돌려주는 신호(Esc 등 기존 규칙)도 같은 경로.
6. **메시지:** 터미널·브라우저와 주고받는 내용(입력, 제목, 작업 폴더, 복사·붙여넣기, 주소, 뒤로·앞으로)은 지금의 `terminal`/`browser` push와 메서드를 그대로 쓴다. 웹 터미널이든 Ghostty든 위쪽 규약은 같다.

### 3.1 메시지 모양 (W1에 확정)

| 방향 | 메시지 | 모양 |
|---|---|---|
| host → page | `host` | `{kind:'host', platform:'windows'\|'macos', surfaces:{terminal:'web'\|'native', browser:'web'\|'native'}}`. 페이지가 `ready`를 부르면 **스냅샷보다 먼저** 보낸다(그래서 창이 그려질 때 이미 안다). 페이지를 다시 읽으면 다시 보낸다. 지금 값: Windows `{terminal:'web', browser:'native'}`, Mac `{terminal:'native', browser:'native'}` |
| page → host | `surfaceBounds` | `{paneId, surface:'terminal'\|'browser', rect:{x, y, width, height}, visible, occluded}` → `null`. `rect`는 페이지 왼쪽 위 기준 기기 픽셀(CSS 픽셀 × devicePixelRatio, 바깥쪽으로 정수 반올림 — `slotBounds`와 같은 규칙). 바뀐 때만, 한 프레임에 한 번. 슬롯이 사라지면 `{rect:{0,0,0,0}, visible:false, occluded:false}` |
| page → host | `surfaceAction` | `{paneId, surface, action:'focus'}` → `null`; `{paneId, surface, action:'snapshot'}` → PNG `data:` URL 또는 `null`(그림을 줄 수 없는 플랫폼, 뷰가 아직 없음) |
| host → page | `browser` | `{kind:'browser', paneId, state:{status:'starting'\|'ready'\|'disabled'\|'missing'\|'failed', url?, canGoBack, canGoForward}}` (C#과 같음) |

규칙:
- 호스트는 `visible && !occluded && rect`가 비어 있지 않을 때만 뷰를 그 자리에 보이고, 아니면 숨긴다. 같은 자리면 다시 옮기지 않는다(옮긴 뒤에 보인다 — 예전 자리에서 깜박이지 않게).
- `occluded`는 브라우저 창의 기존 규칙과 같다: 호스트 흐름의 대화 상자, 페이지가 그리는 호스트 화면(설정·시트), 창 자체의 대화 상자가 열려 있으면 모든 슬롯이 가려지고, 페이지의 메뉴·팝오버·창 안 대화 상자(`data-page-overlay`)는 겹치는 슬롯만 가린다(`slot/bounds.ts` `pageCovers`).
- 보이던 슬롯이 가려지면 페이지는 `surfaceAction snapshot`을 **먼저** 보내고 `surfaceBounds{occluded:true}`를 보낸다. 호스트는 호출을 순서대로 처리하므로 뷰가 아직 보일 때 그림을 찍고, 그다음 숨긴다. 그림이 오면 슬롯이 가려져 있는 동안만 슬롯 배경으로 그린다(Windows 웹뷰는 W1에서 `null` — W4에서 `CapturePreview`).
- 뷰는 슬롯이 처음 보일 수 있을 때 열린다(브라우저는 `browserOpen`이 주소를 준 뒤). 페이지를 다시 읽으면 모든 뷰가 숨고 슬롯이 다시 알려질 때 돌아온다. 창(pane)이 상태에서 없어지면 뷰를 닫는다.
- C# 호스트는 `host`를 보내지 않는다. `host`가 없으면 페이지는 지금 규약(`browserBounds{x,y,width,height,visible}`) 그대로다. 새 페이지 코드는 모두 `import.meta.env.MODE === 'tauri'` 뒤에 있어 C# 빌드(`dist/`)는 바이트 단위로 같다.
- 브라우저 자식 웹뷰(Windows)는 메인 페이지와 다른 WebView2 데이터 폴더(`<local>/browser/<profile>`)를 쓰고, `host_post`는 메인 웹뷰(`main`)에서 온 것만 받는다.

## 4. 폴더

```
native/
  web/                      공통 페이지 (Mac 단계 시작 때 native/windows/web에서 옮김)
  contracts/                공통 기대값 (그대로)
  tauri/
    Cargo.toml              워크스페이스
    crates/
      mighty-core/          상태·모델·Wire·로케일·토큰  (W0에 있음)
      mighty-platform/      포트(trait)와 가짜 구현, PlatformProfile
      mighty-platform-windows/
      mighty-platform-macos/
      mighty-run/  mighty-graph/  mighty-remote/  mighty-delegation/  mighty-tools/
    src-tauri/              호스트 앱
  windows/  macos/          0.4.x 앱 (0.5 출시 때까지 그대로)
```

## 5. CI
W1부터 `native/tauri`를 **Windows와 Mac 둘 다**에서 빌드·테스트한다(공통 crate 테스트는 양쪽에서 같은 결과). Mac 구현이 아직 비어 있어도 공통 로직이 Mac에서 깨지지 않게 하기 위해서다. Mac 네이티브 모듈(Ghostty, CEF)은 Mac 단계에서 채우되, 포트와 NativeSlot 규약은 W1에 확정한다.

`.github/workflows/native-tauri.yml`(`feat/tauri` push, `native/tauri`·페이지를 건드리는 PR): `windows-latest`와 `macos-latest`(arm64)에서 페이지 타입 검사·단위 테스트, `cargo fmt --check`, `cargo clippy --workspace --all-targets -D warnings`, `cargo test --workspace`, Tauri 앱 debug 빌드; Windows는 데모 프로필 smoke(브라우저 화면 단계 포함)까지. 0.4.x 워크플로와 따로 돈다.
