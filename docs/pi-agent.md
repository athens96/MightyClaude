# pi 에이전트 창 (Mac · Windows · 폰)

[pi](https://github.com/badlogic/pi-mono) 코딩 에이전트 CLI(`@mariozechner/pi-coding-agent`, 명령 `pi`)를 Claude·Codex·Antigravity와 나란히 쓰는 네 번째 에이전트로 붙인다. 아무것도 설치되지 않은 컴퓨터에서 앱의 구성 요소 화면만으로 설치·로그인·사용까지 된다. pi 자체 문서: 설치된 패키지의 `docs/`(rpc.md, json.md, providers.md, sessions.md, models.md).

## 식별자

- provider id `pi`, 화면 이름 `pi`, 실행 파일 `pi`. 기존 에이전트 목록(`ProviderOptions` 등, Antigravity가 `gemini`로 들어간 자리들)에 같은 방식으로 더한다.

## 설치와 로그인 (구성 요소 화면, `setup-catalog.json`)

- 항목 `pi`: group `agent`, need `optional`, label/description `setup.item.pi.title`/`setup.item.pi.detail`.
- macOS: probe executable `pi`, install `npmGlobal` `@mariozechner/pi-coding-agent`, prerequisites `["node"]` (node는 Homebrew를 거친다: Homebrew → Node.js → pi). Windows: 같은 npmGlobal, prerequisites `["node"]` (winget Node.js). source `https://github.com/badlogic/pi-mono`.
- afterInstall `signIn` provider `pi`.
- 업데이트: `pi update self` (다른 CLI의 업데이트 자리와 같은 방식). 버전: `pi --version`.
- 로그인 상태: `~/.pi/agent/auth.json`에 자격 증명이 하나라도 있거나, pi가 읽는 API 키 환경 변수(`ANTHROPIC_API_KEY`, `OPENAI_API_KEY`, `GEMINI_API_KEY`, … providers.md의 목록) 중 하나가 로그인 셸에 있으면 로그인됨. 모델 목록이 비면(`pi --list-models`가 "No models available") 로그인 안 됨으로 본다.
- 로그인: 에이전트 터미널 창에서 `pi`를 실행하고 `/login`을 입력하도록 안내한다(구독: Claude Pro/Max, ChatGPT, GitHub Copilot, 또는 API 키). 로그아웃은 pi 안의 `/logout`.

## 실행 (RPC 모드)

- 창마다 상주 프로세스 하나: `pi --mode rpc` (cwd = 창의 작업 폴더), 필요한 인자: `--provider`/`--model`(사용자가 고른 경우), `--thinking <level>`, `--tools <allowlist>`(읽기 전용일 때), 이어하기는 `--session <파일 또는 id>`. 첫 요청 때 띄우고 창이 열려 있는 동안 둔다. 모델·사고 강도·권한이 바뀌면 다음 요청 전에 `set_model`/`set_thinking_level`로, 도구 목록이 바뀌면 프로세스를 다시 띄운다(같은 세션으로).
- 요청: `{"type":"prompt","message":…,"images":[…]}`. 실행 중 사용자의 끼어들기(앱의 steer)는 `{"type":"steer","message":…}`, 대기열은 앱의 대기열 그대로(끝난 뒤 다음 요청). 중지: `{"type":"abort"}`.
- 세션 id·파일은 `get_state`로 얻어 창에 저장한다(재시작 후 `--session`으로 이어하기).
- 이벤트(json.md) → 앱 기록:
  - `message_update`의 `text_delta`/`thinking_delta` → 답·생각 스트리밍, `message_end`(assistant) → 답 확정.
  - `tool_execution_start`/`_update`/`_end` → 도구 블록(이름, 인자 요약, 결과, `isError`). bash는 명령 블록으로.
  - assistant 메시지의 `usage`(input, output, cacheRead, cacheWrite, `cost.total`) → 창의 사용량과 하루 사용량 합계.
  - `agent_end` → 요청 완료. 오류·`auto_retry_*`·`compaction_*` → 기존 상태 표시 방식대로.
- 실행 중 표시, 기본 보기, 마이티 흐름 보기, 폰 화면은 다른 에이전트와 같은 모델(LogEntry, 그래프)을 쓴다.

## 권한

pi에는 승인 창이 없다. 모드 두 가지:

| 모드 | pi 인자 | 뜻 |
|---|---|---|
| 읽기 전용 | `--tools read,grep,find,ls` | 파일을 바꾸거나 명령을 실행하지 않는다 |
| 전체 허용 | (없음) | 모든 도구를 묻지 않고 쓴다 |

기본값은 읽기 전용. 전체 허용을 고르면 묻지 않고 실행된다는 안내를 보인다. 모드 이름과 설명은 기존 권한 라벨 키를 쓰고, 없으면 새 키를 4개 언어로 더한다.

## 모델

- `pi --list-models`(로그인 후)의 목록을 모델 메뉴에 쓴다(provider/id와 이름). 기본값은 "pi 설정 따름"(인자 없음).
- 사고 강도: off, minimal, low, medium, high, xhigh (`--thinking`, `set_thinking_level`).

## 이어하기

- 세션은 pi 기본 위치(`~/.pi/agent/sessions/`)에 둔다. 창을 다시 열면 저장한 세션 파일로 이어 간다.
- "이전 세션 이어가기" 목록에 그 폴더의 pi 세션도 나온다(session-format.md의 헤더와 첫 사용자 메시지로 제목).

## 1단계에 넣지 않는 것

- 워크트리로 실행의 자식 에이전트로 pi 고르기, pi 확장을 이용한 승인 카드, 원격 워크스페이스의 pi 창. (나중 단계)

## 테스트

- 이벤트 → 기록 변환은 녹화한 RPC 출력 fixture(`native/contracts/fixtures/pi-rpc-*.jsonl`)로 Mac·Windows가 같은 결과를 내는지 본다.
- 설치 목록, 로그인 판정, 모델 목록 파싱, 권한 → 인자 변환의 단위 테스트.
