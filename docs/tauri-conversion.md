# Mighty Code → Rust + Tauri 2 (0.5) — 변환 계획

작성 2026-11-13, 기준 0.4.59 (`2f3ca705`), 브랜치 `feat/tauri`. 근거 자료: `.omc/research/tauri/host-contract.md`, `core-catalog.md`, `mac-gaps.md`.

## 0. 목표와 완료 기준
- Windows 앱(C# Core 59k줄 + WinUI 호스트 41k줄)을 Rust + Tauri 2로 다시 만든다. 화면은 지금의 React 페이지(`native/windows/web`, 2만 줄)를 그대로 쓴다.
- 그다음 Mac 앱(SwiftUI + MightyCore 56k줄)을 같은 Tauri 앱으로 바꾼다. 휴대폰 앱은 바꾸지 않는다(통신 규약을 그대로 지킨다).
- **0.5 출시 조건:** (1) C# Core 테스트 1,170개를 Rust로 옮겨 모두 통과, (2) 공통 기대값 파일(`native/contracts/fixtures`) 전부 통과, (3) Windows 웹 smoke와 같은 항목의 Tauri smoke 통과, (4) 0.4.x에서 앱 안 업데이트로 0.5가 설치되고 데이터가 그대로 이어짐, (5) Mac도 같은 조건.

## 1. 지켜야 할 호환 (깨지면 사용자가 데이터·업데이트를 잃음)
| 항목 | 그대로 둘 것 |
|---|---|
| 상태 파일 | `%APPDATA%\MightyCode\workspace-state.json` v1, `.bak`, NUL 파일 처리, 8 MiB 제한, 같은 JSON 모양(camelCase, null 생략, 모르는 필드 보존, .NET `"O"` 시각 형식) |
| 다른 데이터 | `delegation-state.json`, `mobile-identity.json`, `remote-connections.json`(DPAPI), `toolkit.json`, `companion-settings.json`, `image-cache`, `file-originals`, HKCU `Software\MightyCode` 값 |
| 실행 파일 | 이름 `MightyCode.exe`, 설치 폴더 `%LOCALAPPDATA%\Programs\MightyCode`, Inno Setup AppId `{F2F99AEF-…}`(`.iss` 유지) |
| 업데이트 | 서명된 `latest.json`(envelope `mightyclaude-update-v1`, Ed25519, 공개키 `C02DD3q…`), 같은 R2 주소. Tauri 기본 업데이터(minisign)는 쓰지 않음 |
| **0.4.x → 0.5 업데이트 손넘김** | 0.4.x가 새 exe를 `--update-helper --staged --install --backup --package --sha256 --size --wait-pid --relaunch` 로 실행한다. Rust exe가 WebView2를 띄우기 전에 이 모드를 C#과 똑같이 처리해야 함(복구, pid 대기, sha256 재확인, 두 번 이름 바꾸기와 되돌리기, 새 환경으로 재실행, 뮤텍스, 레지스트리 DisplayVersion, install.log). **가장 큰 위험** — 안 되면 0.4.x 다리 버전을 먼저 낸다 |
| 실행 파일의 다른 모드 | `--agent-io-mcp`, `--agent-delegation-mcp`(CLI가 띄우는 MCP 서버), `--profile`, `--smoke-test`, `--smoke-exit`, `--help-capture`, `--perf-scenario`, `--fake-claude`, `--help-demo-cli` |
| 휴대폰·원격 | m1 API, 릴레이 암호(X25519→HKDF→ChaCha20-Poly1305), 푸시 봉투, 원격 작업 공간 v2(포트 43137, 상태 코드 순서), Tailscale 직접 연결 |

## 2. 구조
```
native/tauri/
  Cargo.toml                 워크스페이스
  crates/mighty-core/        상태·모델·Wire JSON·로케일·디자인 토큰 (플랫폼 무관)
  crates/mighty-platform/    프로세스(Job Object, .cmd), ConPTY/forkpty, 경로, DPAPI/Keychain, 레지스트리/UserDefaults
  crates/mighty-run/         실행 엔진, 공급자 인자, 스트림 파서(Claude/Codex/agy/pi/ACP), 권한, 계획·체크리스트
  crates/mighty-graph/       그래프 추적기, 마이티 흐름·타임라인·구조·역할 보기
  crates/mighty-remote/      휴대폰(릴레이·직접·m1·푸시), 원격 작업 공간 v2, 화면 공유 규약
  crates/mighty-delegation/  위임, 작업 트리, MCP 서버, Agent IO
  crates/mighty-tools/       플러그인, CLI 계정·업데이트, 설정 도구, 사용량, 상태 줄, 작업 지도, 스타일(동결 엔진)
  src-tauri/                 호스트: 브리지, 델타 엔진, 셸·작성창·카드 모델, native 폼(NativeNode 생성), 창들, 업데이트 도우미, smoke
  tests/fixtures → ../contracts/fixtures (공통 기대값 그대로)
```
- 페이지와 통신: `HostPort`만 Tauri용으로 바꾼다(`tauriPort.ts`). 결과와 push는 한 이벤트 채널로 순서대로.
- 지금 C# 호스트가 "보이지 않는 WinUI 컨트롤을 읽어" 만드는 것(셸 모델, 작성창 상태, 카드, 터미널 바닥, 설정·대화 상자)은 Rust에서 규칙을 직접 계산해 같은 JSON을 만든다. 설정·대화 상자는 같은 NativeNode 모양으로 Rust가 직접 만든다(페이지 수정 없음).
- 페이지 폴더는 Mac 단계 시작 때 `native/windows/web` → `native/web`으로 옮긴다(C# 빌드·CI 함께 수정).

## 3. 검증 방법 ("완벽하게 같게"를 무엇으로 확인하나)
1. **공통 기대값:** 58개 fixture 중 56개를 Core 테스트가 읽는다 → Rust 테스트가 같은 파일을 읽어 같은 결과를 내야 통과.
2. **C# 테스트 1:1 이전:** `Verification.cs`의 1,170개 검사를 이름 그대로 `cargo test`로 옮긴다. 가짜 CLI는 테스트 바이너리를 다시 실행하는 방식 그대로(`[[bin]] fake-cli`).
3. **차이 비교 도구:** 같은 입력(상태 파일, CLI 스트림 녹화)을 C#과 Rust에 넣고 출력 JSON(스냅샷, 델타, 셸 모델, 카드, 그래프)을 비교하는 도구를 P0에 만든다.
4. **smoke:** C# 웹 smoke(`MainWindow.WebShell*.cs`)의 단계를 Tauri smoke로 옮긴다. 화면 확인은 WebView2 원격 디버깅(CDP)으로 DOM 검사와 스크린샷.
5. **나란히 실행:** 개발 동안에는 별도 식별자(`dev.mightycode.tauri`)와 별도 WebView2 폴더로, 실제 상태는 읽기만. 쓰기는 P0의 저장 코드가 C#과 바이트 단위로 같다고 확인된 뒤에 켠다.

## 4. 단계 (Windows 먼저)
| 단계 | 내용 | 완료 기준 |
|---|---|---|
| W0 골격 *(완료 2026-11-13)* | Tauri 앱 + 페이지 연결 + 상태 읽기(쓰기 없음) + 미구현 함수 목록 + smoke | 실제 상태로 작업 공간·창 목록이 보임 |
| W1 상태·플랫폼 *(완료 2026-11-14)* | 상태 저장(원자적 저장, .bak, 디바운스), 정규화, 이름 바꾸기 이전, 프로세스 계층(Job Object, PATHEXT, .cmd), 로케일·토큰, 델타 엔진, 셸 모델 전부 | 상태가 C#과 바이트 단위로 같게 저장; 셸·델타 비교 도구 통과 |
| W2 Claude 요청 | 실행 엔진(Claude), 스트림 파서, 권한·질문·계획 카드, 체크리스트·백그라운드, 첨부, 작성창·대기열·슬래시 명령 | claude-stream-*, claude-plan-mode, pending, queue-run-now 통과; 실제 Claude 요청이 처음부터 끝까지 됨 |
| W3 마이티 흐름 | 그래프 추적기, 흐름·타임라인·구조·역할 보기, 진행 패널, 알림 대기 | graph-vectors, flow-vectors, agent-notices 통과 |
| W4 창 종류 | 터미널(ConPTY + xterm), 파일 창·미리보기·변경점, 브라우저 창(자식 웹뷰), HTML 프레임, 작업 지도, 세션 기록·이어하기 | file-diff, work-map 통과; 해당 smoke 통과 |
| W5 다른 에이전트 | Codex(exec + 승인 채널), Antigravity(설정 바이트 단위 편집), pi, opencode, Hermes, 모델 목록·단계별 모델 | 각 fixture와 acp-graphs 통과 |
| W6 설정·관리 | 설정 화면 16개 섹션과 대화 상자 14개(NativeNode), 플러그인, CLI 계정·업데이트, 설정 도구, 사용량, 상태 줄, 알림 | setup-catalog, toolkit-hash 통과; 설정 smoke 통과 |
| W7 위임 | Agent IO MCP, 위임 MCP 서버, 작업 트리·병합, 자식 창 | delegation-modes와 git 시나리오 통과 |
| W8 원격 | 휴대폰(릴레이·직접·m1·푸시), 원격 작업 공간 v2, 펫(겹침 창), 화면 공유(지금 꺼져 있음 — 마지막) | relay, push, mobile-*, remote-v2 통과; 실제 휴대폰 연결 |
| W9 업데이트·출시 | `--update-helper` 손넘김, 업데이트 확인·서명 검증(Ed25519 동작 일치), 설치 프로그램, CI(`native-windows.yml`), 도움말 캡처, 스타일 엔진(동결본 그대로) | 0.4.59 → 0.5 실제 업데이트 성공; 전체 테스트·smoke 통과 |

## 5. Mac (Windows 이후)
- 같은 Rust 코어에 Mac 플랫폼 계층: forkpty, 로그인 셸 환경, Keychain, UserDefaults 읽기, 휴대폰 페어링 파일 4개, 경로.
- 플랫폼마다 다른 모듈은 같은 포트에 다른 구현을 끼운다(`docs/tauri-architecture.md`). **Mac 터미널은 Ghostty를 그대로 쓴다**(네이티브 뷰를 페이지의 NativeSlot 위에 올림, 지금 Swift 코드 재사용). Mac 브라우저는 CEF(지금과 같음) 또는 WKWebView를 설정으로 고른다. 한글 입력 보조도 Swift 모듈로 유지한다. 메뉴 막대·딥 링크·알림은 Tauri API로.
- 0.4.x Mac → 0.5: 번들 id `dev.mightycode.native`, 실행 파일 이름 `MightyCode`, 같은 개발자 서명 유지. CI의 Mac smoke 10종 다시 만들기, 서명·공증.

## 6. 진행 원칙
- C# 앱은 0.5까지 0.4.x로 계속 출시한다. 그 사이 C#에 들어가는 변경은 모두 Rust 이전 목록에 한 줄씩 추가한다(차이가 벌어지지 않게).
- 단계마다 브랜치 → 테스트·smoke → main 병합. 미완성 기능은 Tauri 앱 안에서만 "미구현"으로 남고, C# 앱에는 영향 없음.
- 큰 단계는 여러 에이전트로 나눠 동시에 진행하되, 파일 범위가 겹치지 않게 crate 단위로 나눈다.

## 7. 정해야 할 것
1. ~~Mac 기능 변화~~ → 정함(2026-11-13): Mac은 Ghostty 터미널을 유지하고, 모듈은 플랫폼마다 다르게 끼운다(`docs/tauri-architecture.md`).
2. 화면 공유(지금 꺼져 있음)를 0.5에 넣을지, 0.5 이후로 미룰지.
3. 0.4.x → 0.5 손넘김을 Rust exe에 직접 넣을지(추천), 0.4.x 다리 버전을 먼저 낼지.

## 8. 진행 기록

각 단계: 구현 → 테스트(C# 테스트 이전 + 공통 기대값) → 독립 코드 리뷰 → 지적 수정 → 커밋·push → Tauri CI(Windows·Mac) 통과.
정하지 않은 것의 기본값(2026-11-14): 업데이트 손넘김은 Rust exe에 직접, 화면 공유는 W8 맨 끝, 상태 저장은 개발 중 꺼 두고 W9에서 기본으로 켬.

| 단계 | 커밋 | 리뷰 | CI |
|---|---|---|---|
| W0 골격 | 4df8f04c | — | Windows 통과 |
| W1 상태·플랫폼 | 79d0c26a, 00fd9e4c, d3115cdd | W1-C 리뷰 7건 수정 | 43450108376 Windows·Mac 통과 |
