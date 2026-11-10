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
| 읽기 전용 | `--tools read,grep,find,ls,ask_user` | 파일을 바꾸거나 명령을 실행하지 않는다(`ask_user`는 앱의 질문 도구, 아래 "질문") |
| 전체 허용 | (없음) | 모든 도구를 묻지 않고 쓴다 |

기본값은 읽기 전용. 전체 허용을 고르면 묻지 않고 실행된다는 안내를 보인다. 모드 이름과 설명은 기존 권한 라벨 키를 쓰고, 없으면 새 키를 4개 언어로 더한다.

## 모델

- `pi --list-models`(로그인 후)의 목록을 모델 메뉴에 쓴다(provider/id와 이름). 기본값은 "pi 설정 따름"(인자 없음).
- 사고 강도: off, minimal, low, medium, high, xhigh (`--thinking`, `set_thinking_level`).

## 이어하기

- 세션은 pi 기본 위치(`~/.pi/agent/sessions/`)에 둔다. 창을 다시 열면 저장한 세션 파일로 이어 간다.
- "이전 세션 이어가기" 목록에 그 폴더의 pi 세션도 나온다(session-format.md의 헤더와 첫 사용자 메시지로 제목).

## 질문 (Mac)

pi에는 질문 도구가 없다. 앱이 두 가지를 한다. pi 1.5.2 실물로 확인했고 fixture는 그 녹화다(`pi-rpc-question.jsonl`, `pi-rpc-dialogs.jsonl`).

1. **질문 도구를 준다.** 앱 번들의 확장 `Resources/Agents/pi-ask-user.js`(의존성 없는 JS 한 파일)를 실행마다 `--extension <경로>`로 싣는다. `~/.pi`는 건드리지 않는다. 확장은 `ask_user` 도구(`question`, `options[]`, `multiple`)를 등록하고, 보기가 있으면 `ctx.ui.select`, 없으면 `ctx.ui.input`으로 묻는다. 읽기 전용 모드의 허용 목록에는 도구 이름을 더한다: `--tools read,grep,find,ls,ask_user`(`--tools`는 이름을 댄 도구만 켠다). 번들에 파일이 없으면 인자를 붙이지 않고 도구 없이 실행한다.
2. **대화상자를 질문 카드로 보인다.** RPC 모드에서 `ctx.ui.select/confirm/input`은 `extension_ui_request`로 오고 `extension_ui_response`로 답한다(pi의 `docs/rpc-extension-ui.md`). `PiDialogChannel`이 Claude의 AskUserQuestion과 같은 카드(`AgentQuestions`)로 바꾼다.

| pi가 보낸 것 | 카드 | 답 |
|---|---|---|
| `{"method":"select","title","options":[…]}` | 제목이 질문, 보기마다 선택지 | `{"type":"extension_ui_response","id","value":"<고른 보기>"}` |
| `{"method":"confirm","title","message"}` | 제목이 머리글, 메시지가 질문, 예/아니요 | `{…,"confirmed":true}` 또는 `false` |
| `{"method":"input","title","placeholder"}` | 보기 없이 입력 칸 | `{…,"value":"<쓴 글>"}` |
| `{"method":"editor",…}`, 보기가 12개를 넘는 `select` | 카드 없음 | `{…,"cancelled":true}` + 기록 한 줄(전과 같다) |

- 카드의 취소는 `{"cancelled":true}`다. 도구는 "사용자가 답하지 않았다"를 모델에 돌려주고 같은 턴이 이어진다.
- `ask_user`가 연 `select`(제목이 실행 중인 `ask_user` 호출의 `question`과 같다)에서만 직접 쓴 답과 여러 개 고르기를 받는다. pi는 `value`가 보기 중 하나인지 보지 않으므로 쓴 글을 그대로, 여러 개는 `", "`로 이어 보낸다. 다른 확장의 `select`는 보기 하나만 답이 되고(그 확장이 값을 어떻게 읽을지 모른다), 쓴 답은 카드에서 거절한다.
- `timeout`이 있는 대화상자는 그 시간이 지나면 pi가 스스로 닫는다(클라이언트에 알리지 않는다). 앱도 같은 시간 뒤 카드를 거두고 한 줄 남긴다.
- 실행을 멈추면 카드는 답 없이 거둔다(프로세스가 끝난다). `ask_user` 호출이 답 전에 끝나도(`tool_execution_end`) 거둔다.
- 폰: 카드가 `ToolPermissionRequest`(`AskUserQuestion`, `canAnswerQuestions`)라 `MobilePermission.questionnaire`로 그대로 가고, 폰의 답도 같은 길(`answerUserQuestions`)로 온다.

## 1단계에 넣지 않는 것

- 워크트리로 실행의 자식 에이전트로 pi 고르기, pi 확장을 이용한 승인 카드, 원격 워크스페이스의 pi 창. (나중 단계)

## 테스트

- 이벤트 → 기록 변환은 녹화한 RPC 출력 fixture(`native/contracts/fixtures/pi-rpc-*.jsonl`)로 Mac·Windows가 같은 결과를 내는지 본다.
- 설치 목록, 로그인 판정, 모델 목록 파싱, 권한 → 인자 변환의 단위 테스트.

## Mac 구현 메모 (1단계)

- 상주 프로세스 대신 **요청마다** `pi --mode rpc`를 띄운다(앱의 실행 단위가 요청이라 상태·중지·대기열이 다른 에이전트와 같은 길을 쓴다). 이어하기는 `--session <세션 id>`가 맡는다. stdin은 `get_state`(세션 id·모델의 컨텍스트 크기) → `prompt` 순서로 쓰고, `agent_end` 뒤 1초 조용하면(압축·자동 재시도가 바로 이어질 수 있다) stdin을 닫아 pi가 끝나게 한다. 닫기 전에는 `steer` 줄을 쓸 수 있다. 중지는 프로세스 종료다.
- 이어하기 id는 세션 uuid다(헤더의 `id`, 파일 이름 `<시각>_<uuid>.jsonl`). 그 파일이 없으면(답이 한 번도 없던 세션) `--session` 없이 새 세션으로 시작한다.
- 답은 `text_end`/`message_end`에서 한 번에 기록하고, 생각 텍스트는 기록에 옮기지 않는다. 재시도가 이어질 오류(`auto_retry_start`)는 실행 실패가 아니고, 재시도가 없었던 오류는 프로세스가 끝날 때 실패가 된다.
- 답은 한 번만 기록한다. pi 1.5는 `message_update`에 `message`를 싣지 않고(`usage`와 `assistantMessageEvent`만 온다), `message_start`에는 `timestamp`만, `message_end`에는 `responseId`도 있다. 그래서 메시지의 이름은 `timestamp`이고(`message_start`에서 기억한다), `text_end`와 `message_end`가 같은 답을 두 번 남기지 않는다. 전에는 두 이벤트의 이름이 달라 답이 두 번 저장되고 두 번 보였다.
- 확장이 띄운 대화상자와 질문 도구는 위 "질문" 절을 따른다.
- 이전 요청을 마이티 흐름 보기로 불러오는 기록 읽기(`SessionHistory`)는 아직 pi를 읽지 않는다. 폰은 `provider: "pi"`를 그대로 받아 보여 주기만 한다.
- Windows가 맞출 것: `native/contracts/fixtures/pi-rpc-*.jsonl`(answer, tools, retry, error, rejected, question, dialogs)과 같은 결과, `setup-catalog.json`의 `pi` 항목, 위 argv/stdin 모양.
