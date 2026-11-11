# opencode 에이전트 창 (Mac 1단계, OpenCode 2)

[opencode](https://opencode.ai) 코딩 에이전트 CLI를 Claude·Codex·Antigravity·pi와 나란히 쓰는 다섯 번째 에이전트로 붙인다. 대상은 **OpenCode 2**(npm `@opencode/cli`, 명령 `opencode`와 `opencode2`, 소스 [anomalyco/opencode](https://github.com/anomalyco/opencode) `dev` 브랜치, `packages/cli`)다. 아무것도 설치되지 않은 Mac에서 앱의 구성 요소 화면만으로 설치·로그인·사용까지 된다. 1단계는 Mac만 한다(Windows·폰은 다음 단계).

이 문서와 fixture는 opencode를 설치하거나 실행하지 않고 **OpenCode 2.2.7 소스(`packages/cli/package.json` 2.2.7, dev 783b7d64)를 읽고** 썼다. 근거: `packages/cli/src/acp/*`(service.ts, agent.ts, translate.ts, tool.ts, turn.ts, permission.ts, config-option.ts, elicitation.ts, error.ts), `packages/cli/src/commands/handlers/{auth/*,models.ts,upgrade.ts}`, `packages/cli/src/database-path.ts`, `packages/core/src/{config.ts,credential/sql.ts,session/sql.ts,session/usage.ts}`, `services/site/src/docs/content/{migrate-v1.mdx,permissions.mdx,cli/acp.mdx}`.

## 식별자

- provider id `opencode`, 화면 이름 `opencode`, 실행 파일 `opencode`. 베타 배지, 회색(0x8A8F98) 블록 O 마크, SF Symbol `chevron.left.forwardslash.chevron.right`.
- `ProviderOptions.ids`의 다섯 번째. pi처럼 원격 작업 공간에서는 고를 수 없다(`ProviderOptions.isLocalOnly`, `run.blocked.opencodeRemote`). 위임에 참여하지 않는다. 폰 capability에는 아직 넣지 않았다.

## OpenCode 2를 쓴다 (1에서 달라진 것과 1의 처리)

OpenCode 1(npm `opencode-ai`, 1.19.x)과 2는 같은 `opencode` 명령을 쓰고 나란히 설치되지 않는다(migrate-v1.mdx: "Remove a package-managed V1 installation before installing V2"). 앱이 보는 차이:

| | OpenCode 1 | OpenCode 2 (앱이 쓰는 것) |
|---|---|---|
| 패키지 | `opencode-ai` | `@opencode/cli` |
| `--version` | `1.19.5` | `opencode v2.2.7` |
| 자격 증명 | `auth.json` | DB의 `credential` 표(2가 처음 열 때 `auth.json`을 한 번 가져온다) |
| 로그인 상태 | 파일 읽기 | `opencode auth list --format json --standalone` |
| 요청별 권한 | `OPENCODE_PERMISSION` | `OPENCODE_CONFIG_CONTENT`의 `permissions` 배열 |
| 기본 권한 | 거의 allow, 프로젝트 밖은 ask | 전부 allow(프로젝트 밖도) |
| 도구 이름 | bash, task, filePath | shell, subagent, path |
| 세션 표 | `session`/`message`/`part` | `session_v2`/`session_message`/`session_part` |
| 프롬프트 응답 토큰 | 마지막 모델 호출 | 요청 전체 합 |

OpenCode 1 처리:
- 구성 요소 항목의 probe가 `minimumVersion 2.0.0`이라 1은 "오래됨"으로 보인다. 다른 에이전트 CLI의 오래됨은 CLI 업데이트로 가지만, opencode는 1의 `upgrade`가 1 안에서만 올리므로 **설치 항목으로 다시 고를 수 있다**(`SetupCatalog.reinstalledWhenOutdated`).
- 설치 명령은 `npm uninstall -g 'opencode-ai' >/dev/null 2>&1; npm install -g '@opencode/cli'`(`SetupCatalog.npmReplaces`). npm으로 깐 1을 먼저 지운다. 없으면 아무 일도 없다.
- curl(`~/.opencode/bin`)이나 Homebrew로 깐 1은 지우지 않는다. PATH에서 그쪽이 먼저면 설치 뒤에도 1이 보이고, 안내 문구가 다시 설치를 권한다. 2의 curl 설치기는 1의 바이너리를 바꿔 쓴다고 문서가 말한다.
- 1이 남아 있으면 요청은 시작하지 않고 `run.error.opencodeV1`을 보인다. 계정 화면은 `settings.cliAccounts.detailOpencodeV1`, CLI 업데이트는 `settings.cliUpdate.detailOpencodeV1`(업데이트하지 않는다).
- 1의 세션은 2가 백그라운드로 `session_v2`에 옮긴다(`migration.v1-v2`). 앱은 `session_v2`만 읽는다.

## 설치와 로그인

- 구성 요소 항목 `opencode`: group `agent`, need `optional`, macOS: probe executable `opencode` (minimumVersion `2.0.0`), install `npmGlobal` `@opencode/cli`, prerequisites `["node"]`(Homebrew → Node.js → opencode), source `https://github.com/anomalyco/opencode`, afterInstall `signIn` provider `opencode`.
- **이 항목은 공유 계약 `native/contracts/fixtures/setup-catalog.json`(Mac 사본과 바이트까지 같다)에 있다**(2026-11-11, 처음에는 Mac 전용 `setup-catalog-macos.json`에 있었고 그 파일과 디코더의 병합은 없앴다). Windows 항목: npmGlobal `@opencode/cli`, prerequisites Windows의 Node.js 항목, minimumVersion `2.0.0`, 설치 단계 `npm uninstall -g 'opencode-ai' *> $null; npm install -g '@opencode/cli'`(1을 먼저 지우고, 설치의 종료 코드가 단계를 정한다), 오래된 opencode는 Windows에서도 설치 항목으로 다시 고를 수 있다. Windows가 찾는 순서(PATH의 폴더마다): `opencode.exe`, npm 패키지 안의 진짜 바이너리 `node_modules\@opencode\cli\bin\opencode.exe`(cmd.exe를 거치지 않는다), `opencode.cmd`(npm으로 깐 OpenCode 1에는 이것만 있어 오래됨으로 보인다).
- 업데이트 `opencode upgrade`(묻지 않고, 설치 방식을 스스로 찾는다), 버전 `opencode --version` → `opencode v2.2.7`.
- 로그인 판정(`CLIAccountSupport.opencodeStatus`): `opencode auth list --format json --standalone`. `--standalone`은 2의 백그라운드 서비스를 띄우지 않으려고 붙인다. 출력은 `[{id, name, connections: [{type: "credential", method: "oauth"|"key"|…, status?} | {type: "env", name}]}]`이다.
  - `status.status == "needs_auth"`가 아닌 연결이 하나라도 있는 integration을 로그인으로 보고, 이름만 보인다(자격 증명 값은 이 출력에 없다).
  - 하나도 없으면 **로그인 안 됨** + "opencode Zen의 무료 모델만" 안내. 2도 로그인 없이 Zen 무료 모델을 돌린다.
  - JSON이 아니면 `opencode --version`으로 1인지 보고, 1이면 2를 설치하라고 한다. 그 밖에는 알 수 없음.
- 로그인: 터미널 창에서 `opencode auth login`(TTY에서 제공자 고르기 → 방법). `CLITerminalSignIn.table`의 `opencode` 항목이 로그인 카드·자동 재로그인에 쓰인다. 로그아웃은 터미널의 `opencode auth logout`.
- 로그인 실패 신호(`CLIAuthFailure.opencode`): ACP 오류 코드 -32000 "provider authentication required"(`provider.auth`), "Authorization failed", "Free usage limit reached. Connect a provider …"(로그인하면 풀린다), 401+unauthorized, invalid api key, "opencode auth login". "407 Proxy Authentication Required"는 아니다. 신호일 뿐이고, 카드는 위 상태가 로그인 안 됨일 때만 뜬다.

## 실행 방식: ACP

| | `opencode acp` (ACP) | `opencode run --format json` |
|---|---|---|
| 승인 | `session/request_permission` → **앱의 승인 카드** | 모두 허용(`--auto`)이나 자동 거절 |
| 스트리밍 | 답 텍스트 delta, 도구 pending→in_progress→completed | 끝난 part만 |
| 사용량 | 요청 전체 토큰(프롬프트 응답) + 세션 누적 비용(`usage_update`) | step마다 |
| 이어하기 | `session/resume`(재생 없음), `session/load` | `-s ses_…` |
| 재사용 | Hermes(`hermes acp`)도 같은 클라이언트([문서](hermes-agent.md)) | opencode 전용 |

ACP를 골랐다. 승인 카드(Claude·Codex가 쓰는 `ToolPermissionRequest` → `ToolPermissionBar`)를 그대로 쓰고, 같은 `ACPChannel`을 Hermes가 다음에 쓴다. 2에서는 요청 전체 토큰이 프롬프트 응답에 오므로 1에서 잃던 정확도도 돌아왔다.

## argv / stdin / 환경

- 요청마다 프로세스 하나: `opencode acp`(cwd = 창의 작업 폴더). `opencode acp`는 자기만의 서버(`opencode serve --stdio`)를 띄우고, 환경을 물려준다.
- 환경: `OPENCODE_CONFIG_CONTENT={"permissions":[…]}`(아래 권한 표). 2는 이 문서를 사용자·프로젝트 설정 뒤에 읽는다. 사용자 설정 파일은 건드리지 않는다. 사용자가 이미 `OPENCODE_CONFIG_CONTENT`를 쓰고 있었다면 그 요청에서는 앱의 것으로 바뀐다.
- stdio는 JSON-RPC 2.0, 한 줄에 한 객체(`ACPChannel`, `MightyCore/ACPChannel.swift`). 앱이 보내는 순서:
  1. `initialize` `{protocolVersion: 1, clientCapabilities: {fs: {readTextFile: false, writeTextFile: false}, terminal: false, elicitation: {form: {}}}, clientInfo}`. form elicitation을 알려야 `question` 도구가 묻는다(아래 "질문").
  2. 이어하기 id가 없으면 `session/new` `{cwd, mcpServers: []}`.
     - 있으면 `sessionCapabilities.resume`이 있을 때 `session/resume`(2는 있다), 없고 `loadSession`이면 `session/load`(재생되는 update는 무시).
     - cwd가 세션의 폴더와 다르거나 세션이 없어 실패하면, 한 줄 알리고 `session/new`.
  3. 설정은 `session/set_config_option` `{sessionId, configId, value}`로 하나씩 보낸다. build(기본)는 보내지 않는다.
     - `model`: `provider/model`.
     - `effort`: 모델의 variant id. 세션의 선택지에 없으면 한 줄 알리고 뺀다.
     - plan 모드: `mode` = `plan`.
     - -32602로 거절되어도 요청은 간다.
  4. `session/prompt` `{sessionId, prompt: [{type: text, text}, {type: image, data, mimeType}…]}`. 다른 첨부는 복사본 경로를 텍스트에 적는다.
- 응답 뒤 stdin을 닫는다. 2는 연결이 닫히면 `process.exit(0)`. 2초 안에 끝나지 않으면 앱이 끝내고, 이것은 실패가 아니다(`acpEnding`). 중지는 프로세스 종료.
- 초기화 제한 시간 45초(사설 서버 부팅).
- 2는 클라이언트에 `fs/*`·`terminal/*`를 부르지 않는다. 와도 -32601로 거절한다.

## 이벤트 → 기록

`ACPChannel`이 `acp.*` 객체로 바꿔 `CLIStreamParser.acp`, `SessionUsageTracker.consumeACP`, `ExecutionGraphTracker.consumeACP`에 넘긴다.

| ACP | 기록 |
|---|---|
| `session/new`·`resume` 결과 | `acp.session` → 창의 이어하기 id(`ses_…`), 사용량의 세션 id와 모델(`configOptions.model.currentValue`) |
| `agent_message_chunk` | 모아 두었다가 messageId가 바뀌거나, 도구 호출이나 프롬프트 응답이 오면 답 한 개로 기록. 그래프 블록의 답이 된다. `_meta["opencode/child-session"]`이 붙은 하위 에이전트의 글은 답에 넣지 않는다 |
| `agent_thought_chunk` | 옮기지 않는다 |
| `tool_call` / `tool_call_update` | 도구 행. 아래 따로 적는다 |
| `usage_update` | 컨텍스트 사용량(마지막 단계 토큰)·크기, 세션 누적 비용 |
| `session/request_permission` | 승인 카드. 허용 → `{outcome: {outcome: selected, optionId: "once"}}`, 거부 → `"reject"`, 같은 도구 호출이 끝나면 카드는 취소. "항상 허용"은 내보이지 않는다 |
| 프롬프트 응답 | `acp.prompt_result`: stopReason(end_turn, max_tokens 알림, refusal 알림, cancelled), 토큰 → 요청 완료 |
| JSON-RPC 오류 | `acp.error`: 실패, 오류 문구(`message`, `data.message`), 로그인 실패 판정. 2의 -32603은 `data.errorName`을 단다 |
| `session_info_update`, `available_commands_update`, `config_option_update` | 기록하지 않는다 |
| JSON이 아닌 줄 | 출력 줄로 보인다 |

도구 행:
- kind를 이름으로 바꾼다: execute→Bash, edit→Edit, read→Read, search→Grep, fetch→WebFetch. 그 밖에는 `name`이나 title을 쓴다(2는 `name`에 shell, read 같은 도구 이름을 싣는다).
- `rawInput`의 camelCase 키는 snake_case로 바꾼다. 2는 `path`와 shell의 `cwd`를 쓴다.
- 결과는 content의 텍스트, diff 경로, 또는 `rawOutput.output`/`error`.
- 2는 `plan` update를 보내지 않는다. 오면 체크리스트 줄로 남긴다.

## 권한

2의 기본 정책은 모든 행동 allow다(`.env` 읽기만 ask). 그래서 앱이 요청마다 `OPENCODE_CONFIG_CONTENT`의 `permissions`(순서 있는 `{action, resource, effect}` 배열, **마지막에 맞는 규칙이 이긴다**)로 규칙을 준다. ask로 남은 일은 ACP로 물어 오고 창의 승인 카드가 된다(`ProviderOptions.asksInPane`).

| 모드 | `permissions` (resource는 모두 `*`) (+ ACP `mode`) | 뜻 |
|---|---|---|
| 기본 권한(manual) | edit·shell·webfetch·websearch·subagent·external_directory = ask | 고치기·명령·웹·하위 에이전트·프로젝트 밖 전에 카드로 묻는다. 읽기·검색은 묻지 않는다 |
| 수정 허용(acceptEdits) | edit = allow, 나머지는 위와 같음 | 파일 수정만 묻지 않는다 |
| 계획(plan) | shell·webfetch·websearch·subagent·external_directory = ask, `mode` = plan | plan 에이전트(edit 거부, `~/.opencode/plan/*`만 허용)를 덮지 않으려고 edit 규칙은 주지 않는다 |
| 전체 접근(fullAccess) | `*` = allow | 묻지 않는다 |

- `external_directory`는 2에서 기본 allow가 되었으므로 묻는 모드마다 ask 규칙을 명시한다.
- `question` 도구: 2는 클라이언트가 `clientCapabilities.elicitation.form`을 알릴 때만 `elicitation/create`로 묻는다. 앱이 알리므로 질문 카드로 온다(아래 "질문"). 알리지 않던 때에는 2가 "이 클라이언트에서는 질문을 보일 수 없다"고 도구 결과를 내고 모델이 글로 선택지를 적었다.
- 기본값은 기본 권한.

## 질문

OpenCode 2.2.7 실물로 확인했고 fixture는 그 녹화다(`opencode-acp-question.jsonl`, `-question-multiple.jsonl`, `-question-dismissed.jsonl`). 근거 소스는 `packages/cli/src/acp/elicitation.ts`.

- 요청(에이전트 → 앱, JSON-RPC 요청, id는 숫자): `elicitation/create` `{mode: "form", sessionId, toolCallId, message: "Questions", requestedSchema: {type: "object", properties, required: []}}`.
  - 하나 고르기: `q0: {type: "string", title: <머리글>, description: <질문>, oneOf: [{const, title, description}]}`
  - 여러 개 고르기: `q0: {type: "array", title, description, items: {anyOf: [{const, title, description}]}}`
  - 직접 쓰기를 받는 질문에는 `q0_custom: {type: "string", title: "<머리글> (other)"}`가 함께 온다. 이것은 질문이 아니라 카드의 "직접 쓰기" 줄이다.
- 카드(`ACPElicitationForm` → `AgentQuestions`): 속성 하나가 질문 하나다. `title`이 머리글, `description`이 질문, `oneOf`/`anyOf`의 항목이 선택지(`title`이 이름, `const`가 보낼 값)다. `enum`(+`enumNames`), 보기 없는 `string`(입력 칸), `boolean`(예/아니요)도 받는다. JSON 객체에는 순서가 없어 키를 숫자 순으로 정렬한다(`q0, q1, … q10`).
- 답: `{action: "accept", content: {q0: "Red"}}`, 여러 개는 `{q0: ["Apple", "Cherry"]}`, 직접 쓴 답은 `{q0_custom: "Purple"}`(여러 개 고르기에서는 고른 것과 함께 `{q0: […], q0_custom: "…"}`). `_custom`이 없는 질문에 쓴 답은 카드에서 거절한다(카드는 남는다).
- 카드의 취소: `{action: "decline"}`. 2는 도구를 `failed`("The user dismissed this question")로 닫고 **턴을 끝낸다**(`session/prompt`의 `stopReason: "cancelled"`). 실행은 실패가 아니라 끝난 것으로 본다.
- 카드로 보일 수 없는 form(숫자·객체 속성, 질문 8개나 보기 12개 초과, 같은 질문이나 같은 이름의 보기)은 `decline`으로 답하고 한 줄 남긴다(`acp.channel.formDeclined`). 2는 위와 같이 턴을 끝낸다. 알리지 않던 때처럼 모델이 글로 이어 가게 할 방법은 form을 알린 뒤에는 없다.
- 카드가 떠 있는 동안 그 호출이 `completed`/`failed`가 되거나 에이전트가 요청을 거두면(`$/cancel_request`) 카드를 거둔다. 실행을 멈추면 답 없이 거둔다.
- 모델이 보기를 글로 적지 않게: 실행의 `OPENCODE_CONFIG_CONTENT`에 `instructions: ["<앱 번들>/Agents/opencode-questions.md"]`를 함께 싣는다. 2는 모든 설정 원본의 `instructions`(경로·URL 목록)를 합치므로 사용자의 것은 그대로다. 파일은 "보기 중에서 고르게 할 때는 `question` 도구를 부른다"는 짧은 안내다. 실물 2.2.7에서 안내 없이 "선택하는 문제를 내"는 두 번 모두 글로 퀴즈를 냈고, 안내와 함께는 두 문장 여섯 번 모두 도구로 물었다.
- 폰: 카드가 Claude의 질문과 같은 `ToolPermissionRequest`라 폰에도 그대로 가고, 폰의 답도 같은 길로 온다.

## 모델과 사고 강도

- `opencode models --standalone`: 한 줄에 `provider/model`(정렬됨). 모양이 아닌 줄은 뺀다. 첫 항목 "opencode 설정 따름"(`default`, 설정을 보내지 않는다).
- 로그인하지 않았을 때 이 목록에 Zen의 유료(꺼진) 모델도 나올 수 있다. ACP의 model 선택지는 켜진 모델만 담으므로, 그런 모델은 실행 때 한 줄 알리고 기본 모델로 간다.
- 사고 강도: 모델의 variant(ACP `effort` 옵션, 값은 variant id와 `default`). 모델마다 달라 Low, Medium, High, Max를 보이고, 없는 값은 실행 때 한 줄 알리고 뺀다. `default` 모델에서는 보이지 않는다.

## 사용량

- `usage_update.cost.amount`는 **세션 누적 비용**(`costScope: session`). `UsageLedger`가 같은 창·같은 세션의 이전 값과의 차이만 센다.
- 프롬프트 응답의 `usage`는 **그 요청의 모든 모델 호출 합**(하위 세션 제외, `tokenScope: run`). 2의 `inputTokens`는 캐시를 빼므로 앱은 input = inputTokens + cachedRead + cachedWrite로 센다(다른 에이전트와 같게).
- `used`/`size` → 컨텍스트 사용률. 계정 사용량은 없다(`accountUsage.detailOpencodeUnavailable`).

## 이어하기와 세션 목록

- 세션 id는 `ses_` + 26자(base62). `CoreValidation.identifier`는 `_`를 받지 않아 `CoreValidation.sessionIdentifier`(identifier 또는 `^ses_[A-Za-z0-9]+$`)를 이어하기 id 자리에 쓴다.
- "이전 세션 이어가기" 목록은 DB를 읽기 전용(`mode=ro`, 안 되면 `immutable=1`)으로 연다.
  - DB 위치: `$OPENCODE_DB`, 기본 `~/.local/share/opencode/opencode.db`. 릴리스가 아닌 채널이면 `opencode-<channel>.db`.
  - 고르는 행: `session_v2`에서 `parent_id IS NULL`, `time_archived IS NULL`, `directory`가 작업 폴더, 60일 안.
  - 제목: 첫 사용자 메시지(`session_message.type = 'user'`의 `data.text`). 없으면 opencode 제목("New session - …"은 자리표시로 본다).
  - 요청 수는 사용자 메시지 수, 모델은 `session_v2.model`의 `providerID/id`.
- 마이티 흐름 보기로 이전 요청을 불러오는 `SessionHistory`는 아직 opencode를 읽지 않는다.

## 실물 CLI 없이 확인하지 못한 것

질문 절의 fixture(`opencode-acp-question*.jsonl`)만 실물 2.2.7의 녹화다(앞의 두 줄, `initialize`와 `session/new`의 답은 아래 fixture의 것을 그대로 썼다).

- fixture(`native/contracts/fixtures/opencode-acp-{answer,tools,auth,resume-missing}.jsonl`)는 녹화가 아니라 **2.2.7 소스에서 옮겨 쓴 것**이다. 실물로 다시 볼 것:
  - 실제 줄 순서와 `messageId` 값
  - `tool_call` 첫 이벤트의 빈 `rawInput`
  - 권한 요청의 `title`
  - 하위 세션 `_meta`의 모양
  - 없는 세션 resume의 오류 코드·문구
- `opencode --version`이 정말 `opencode v2.2.7`인지. effect/cli 형식에서 추론했고, 숫자만 읽으므로 둘 다 통한다.
- `OPENCODE_CONFIG_CONTENT`의 전역 `permissions`가 plan 에이전트의 edit 거부 뒤에 붙는지. 그래서 plan에는 edit 규칙을 주지 않았다.
- `opencode auth list --format json --standalone`의 실제 출력과 종료 코드, 로그인 없이 `[]`인지.
- `opencode models`가 꺼진 모델을 내보내는지.
- `session_v2`/`session_message`의 열과 `data` JSON 모양, WAL 중 읽기, 1→2 마이그레이션 중의 모습.
- npm `uninstall`/`install`이 실제 Mac에서 1→2를 깨끗이 바꾸는지(curl·brew로 깐 1과 PATH 순서).
- 첫 실행의 부팅 시간(45초 안).

## 나중 단계

- Windows(Core·WinUI)와 폰, 원격 작업 공간의 opencode 창, 위임 자식으로 opencode 고르기.
- "항상 허용"(allow_always).
- `_session/steering`으로 실행 중 끼어들기.
- `SessionHistory`로 이전 요청 불러오기, opencode 스킬의 `/` 명령.
- Hermes(`hermes acp`)는 같은 `ACPChannel`로 붙였다([Hermes 에이전트 창](hermes-agent.md)). provider별로 바뀌는 것은 argv, 권한 전달 방식, 로그인 판정, 설정의 id다.

## Windows (2026-11-11 반영)

- 실행: 같은 argv(`opencode acp`)·JSON-RPC 순서·`OPENCODE_CONFIG_CONTENT` 권한 표(`OpencodeAgent`, `AcpAgents.cs`)로, 같은 클라이언트(`AcpChannel.cs`)가 `opencode-acp-*.jsonl` fixture를 Mac과 같은 기록·사용량·카드로 재생한다(`AcpVerification`). 질문 안내 파일은 복사하지 않고 Mac 번들의 `Resources/Agents/opencode-questions.md`를 앱 옆 `Agents\`에 싣는다. OpenCode 1이면 실행하지 않고 `run.error.opencodeV1`.
- 데이터 폴더(OpenCode 2.2.7 바이너리 안의 소스로 확인): Windows에도 따로 분기가 없어 `$XDG_DATA_HOME/opencode`, 없으면 `os.homedir()`(`%USERPROFILE%`)의 `.local\share\opencode`이고, DB는 `$OPENCODE_DB`(상대 경로면 데이터 폴더 기준, `:memory:`는 없음) 아니면 `opencode.db`다. 이어가기 목록은 거기서 읽는다(`OpencodeSessions`, `AcpSessions.cs`).
- 구성 요소 항목은 공유 계약의 Windows 항목이다(위 "설치와 로그인": Node.js 뒤 npm, OpenCode 1 제거, 찾는 순서).
- `ses_` 세션 id는 `Wire.SessionIdentifier`가 받는다(실행 요청, 저장된 창, resume 이벤트, 파서).
- 로그인 판정·업데이트·계정 사용량은 [CLI 계정](cli-accounts.md)의 Windows 목록, 모델 목록은 `ProviderCatalog.OpencodeCatalog`.
- Windows PC에서만 확인할 것: npm 심 `opencode.cmd`(cmd.exe를 거친다)로 실제 실행, 첫 실행의 부팅 시간(45초 안), 실제 `opencode.db` 읽기, 마이티 그래프 블록(흐름 쪽 이식 뒤).
