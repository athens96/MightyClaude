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

## 3. NativeSlot: 페이지 위에 네이티브 뷰 올리기

지금 Windows의 브라우저 창이 쓰는 방식(`browserBounds`)을 모든 네이티브 화면으로 넓힌다.

1. **능력 알림:** `ready` 뒤 호스트가 `{kind:'host', surfaces:{terminal:'web'|'native', browser:'native'}, platform:'windows'|'macos'}`를 보낸다. 페이지는 `terminal:'web'`이면 지금처럼 xterm.js를 그리고, `'native'`면 빈 `NativeSlot`을 그린다.
2. **자리 알림:** `NativeSlot`은 `surfaceBounds{paneId, surface, rect(기기 픽셀), visible, occluded}`를 보낸다(크기·위치·스크롤·탭 전환 때마다, 한 프레임에 한 번).
3. **올리기:** 호스트의 `SurfaceManager`가 플랫폼 구현에게 그 자리에 뷰를 만들고·옮기고·숨기게 한다(Mac: 창의 contentView에 NSView를 웹뷰 위로 추가, Windows: 자식 웹뷰나 자식 HWND).
4. **가림 처리:** 페이지의 대화 상자·메뉴·팝오버가 슬롯을 덮으면 `occluded:true` → 네이티브 뷰를 숨기고, 숨기기 직전 화면을 이미지로 받아 슬롯에 대신 그린다(깜박임 없이 대화 상자가 위에 보이게). 지금 Windows 브라우저 창이 하는 규칙을 그대로 넓힌 것.
5. **키보드:** 네이티브 뷰가 포커스를 가진 동안 앱 단축키(⌘T, ⌘K, Ctrl+Tab 등)는 호스트가 받아 페이지로 넘긴다. 포커스를 페이지로 돌려주는 신호(Esc 등 기존 규칙)도 같은 경로.
6. **메시지:** 터미널·브라우저와 주고받는 내용(입력, 제목, 작업 폴더, 복사·붙여넣기, 주소, 뒤로·앞으로)은 지금의 `terminal`/`browser` push와 메서드를 그대로 쓴다. 웹 터미널이든 Ghostty든 위쪽 규약은 같다.

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
