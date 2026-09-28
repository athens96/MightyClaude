# Mac 네이티브 터미널

Mac의 로컬 Shell 실행 창은 Ghostty의 Metal 렌더러와 PTY를 사용하는 대화형 터미널이다. 입력을 별도 메시지 카드로 보내지 않고 화면에 직접 입력한다. 셸 안에서 `cd`로 이동한 폴더, 환경 변수와 실행 중인 프로그램은 같은 터미널에서 계속 유지된다.

## 사용 범위

- 워크스페이스 제목줄의 `+` 메뉴에서 터미널을 선택하거나 `⌘T`로 실행 창을 추가한다.
- 워크스페이스 폴더에서 셸을 시작한다. 저장되어 있던 명령 초안을 자동 실행하지 않는다.
- Enter, Tab, 방향키, Ctrl+C/Ctrl+D, 텍스트 선택과 복사·붙여넣기, 스크롤, 전체 화면 TUI를 Ghostty가 처리한다.
- 창 크기를 바꾸면 터미널 행·열과 PTY 크기도 갱신한다.
- 레이아웃·워크스페이스를 바꿔도 세션별 화면과 셸을 유지한다. 창을 닫거나 앱을 종료하면 그 터미널도 종료한다.
- 앱 종료 후 셸 프로세스를 복구하지는 않는다. 다시 열면 새 셸을 시작한다.

이번 전환은 사용자 선택에 따라 Mac 로컬 터미널에 적용한다. AI 채팅의 모델·권한·첨부 입력은 계속 기존 실행기를 사용한다. Windows 명령은 기존 요청별 실행을 유지한다.

## 에이전트 터미널 창

- Claude·Codex 에이전트 창이 `run_in_terminal` 도구로 처음 명령을 실행하면 그 에이전트 창 오른쪽에 전용 터미널 창이 열린다. 분할할 자리가 없으면 같은 그룹의 탭으로 열린다. 에이전트 창마다 하나이며 이후 명령도 같은 창에 표시된다.
- 명령마다 별도 PTY를 만들어 사용자 셸로 워크스페이스 폴더에서 실행한다. 각 명령은 자기 세션·프로세스 그룹의 리더이므로 중지 신호와 Ctrl+C가 그룹 전체에 간다. 명령 앞에는 `$ <명령>` 머리줄을 흐리게 표시한다.
- 화면은 같은 Ghostty 렌더러를 쓴다. PTY는 앱(MightyCore `PTYAgentTerminalPane`)이 소유하고, Ghostty surface는 host-managed(in-memory) 백엔드로 출력만 그린다.
- 입력한 키(Ctrl+C, 프롬프트 응답)는 아직 실행 중인 가장 최근 명령의 PTY로 간다. 실행 중인 명령이 없으면 버린다. 입력은 tty 에코로 기록되므로 에이전트의 `read_latest_output`에도 나타난다. 에코를 끈 입력(비밀번호)은 나타나지 않는다.
- 터미널 창이나 에이전트 창을 닫아도 프로세스는 계속 실행된다. 에이전트 창 제목줄의 터미널 버튼(터미널 창 열기)으로 같은 화면을 다시 열고, 새 명령이 시작될 때도 다시 나타난다. 앱을 종료하면 프로세스 그룹을 종료한다. 이 창은 상태 파일에 저장하지 않는다.

## Orca 확인 결과

이 프로젝트에서 참고한 [Orca ADE의 공식 터미널 문서](https://www.onorca.dev/docs/terminal)는 xterm.js 기반이라고 명시한다. Ghostty는 테마·폰트·커서 설정 가져오기에 사용한다. 검색에 나타나는 동명의 다른 Orca 터미널 프로젝트와 구별해야 한다.

MightyClaude는 사용자가 요청한 Ghostty 엔진을 직접 네이티브 앱에 연결한다. Ghostty의 [공식 구조 설명](https://ghostty.org/docs/about)에서 설명하는 C ABI, 네이티브 AppKit 화면과 Metal 렌더링을 사용한다.

## 의존성과 빌드

- Swift wrapper: [`Lakr233/libghostty-spm`](https://github.com/Lakr233/libghostty-spm/tree/1.5.20260906), 정확한 버전 `1.5.20260906`, 커밋 `733ae3b29d447b6707cbfc00879027a076dfd0eb`.
- Ghostty 소스 기준: `c4e16970a803b170e352432424f44192cb59f3ac`. wrapper의 `Ghostty.ref`와 바이너리 릴리스가 이 커밋을 가리킨다.
- XCFramework SHA-256: `bd9bba3b95652900e87a6a0f190f33d82a1d8e42d1c0119c330072be361385da`. SwiftPM이 다운로드한 아티팩트를 검증한다.
- Display link: `MSDisplayLink` `2.2.0`. 전이 의존성까지 `native/macos/Package.resolved`에 고정한다.
- 앱은 `GhosttyTerminal` 제품만 사용한다. wrapper의 별도 셸 에뮬레이터 제품은 사용하지 않는다.

첫 빌드는 네트워크가 필요하다. 빌드 스크립트가 SwiftPM 리소스 bundle의 terminfo와 shell integration을 앱에 복사하고, `native/licenses`의 라이선스 고지를 함께 포함한다. 설치된 Ghostty 앱이나 개발용 checkout을 런타임 경로로 참조하지 않는다.

전체 GhosttyKit의 macOS surface가 PTY와 셸을 소유한다. Swift 쪽은 화면 수명주기와 키·마우스·IME·포커스·크기 변경을 연결한다. 숨은 터미널을 잠시 화면에서 분리하는 동작과 실제로 종료하는 동작은 구별한다. 사용자 Ghostty 전역 설정은 자동으로 불러오거나 변경하지 않는다.

## 검증 재현

빌드한 앱을 새 임시 프로필로 실행하면 실제 네이티브 화면과 셸을 사용하는 검사를 수행한다. 이 검사는 AI 요청이나 사용자 클립보드를 사용하지 않는다.

```sh
release/native-macos/MightyClaude.app/Contents/MacOS/MightyClaude \
  --terminal-smoke-test --smoke-exit --profile /tmp/mighty-terminal-smoke
```

결과는 프로필 폴더의 `terminal-smoke-result.json`에 저장된다. `passed`와 프로세스 종료 코드로 성공 여부를 확인한다. 반복 실행할 때는 새 프로필 경로를 사용한다. 실제 화면과 Metal 렌더링을 사용하므로 로그인된 macOS GUI 세션이 필요하다.
