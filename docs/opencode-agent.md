# opencode 에이전트 창 (Mac 1단계)

[opencode](https://opencode.ai) 코딩 에이전트 CLI(npm `opencode-ai`, 명령 `opencode`, 소스 [anomalyco/opencode](https://github.com/anomalyco/opencode), 예전 sst/opencode)를 Claude·Codex·Antigravity·pi와 나란히 쓰는 다섯 번째 에이전트로 붙인다. 아무것도 설치되지 않은 Mac에서 앱의 구성 요소 화면만으로 설치·로그인·사용까지 된다. 1단계는 Mac만 한다(Windows·폰은 다음 단계).

이 문서와 fixture는 opencode를 설치하거나 실행하지 않고 **opencode 1.19.5 소스(tag `v1.19.5`, 커밋 810c0eba)를 읽고** 썼다. 근거가 된 파일: `packages/opencode/src/acp/*`(agent.ts, service.ts, permission.ts, tool.ts, usage.ts, config-option.ts), `cli/cmd/acp.ts`, `cli/cmd/run.ts`, `auth/index.ts`, `core/src/session/sql.ts`, `core/src/database/database.ts`.

## 식별자

- provider id `opencode`, 화면 이름 `opencode`, 실행 파일 `opencode`. 베타 배지, 회색(0x8A8F98) 블록 O 마크, SF Symbol `chevron.left.forwardslash.chevron.right`.
- `ProviderOptions.ids`의 다섯 번째. pi처럼 원격 작업 공간에서는 고를 수 없다(`ProviderOptions.isLocalOnly`, `run.blocked.opencodeRemote`). 마이티 오케스트레이션(위임)에 참여하지 않는다. 폰 capability(`MobileRemoteModels`)에는 아직 넣지 않았다.

## OpenCode 1과 2

npm `opencode-ai`는 **OpenCode 1만** 담는다(1.19.5가 최신). OpenCode 2는 `@opencode/cli`로 따로 나오는 다시 쓴 판이다. 앱은 1을 설치하고 1의 ACP를 쓴다. 1의 `opencode upgrade`는 2가 있다고 알려 주지만 `--major` 없이는 1 안에서만 올린다(앱은 `--major`를 쓰지 않는다). 2로 넘어간 사용자는 나중 단계에서 따로 본다.

## 설치와 로그인

- 구성 요소 항목 `opencode`: group `agent`, need `optional`, macOS만: probe executable `opencode`, install `npmGlobal` `opencode-ai`, prerequisites `["node"]`(Homebrew → Node.js → opencode), source `https://github.com/anomalyco/opencode`, afterInstall `signIn` provider `opencode`.
- **이 항목은 공유 계약 `native/contracts/fixtures/setup-catalog.json`이 아니라 Mac 전용 파일 `native/macos/Sources/MightyCore/Resources/Setup/setup-catalog-macos.json`에 있다.** Windows는 계약 파일을 그대로 넣어 쓰고 모르는 라벨 키·provider가 있으면 디코더가 멈추기 때문이다. `SetupCatalogDecoder.decode(_:macOnly:)`가 둘을 하나로 합쳐 검사하고(ID, 선행 항목, 순환), Mac 전용 항목은 macOS 항목만 가질 수 있으며 같은 group의 끝(pi 뒤)에 놓인다.
- 업데이트 `opencode upgrade`(설치 방식을 스스로 찾는다), 버전 `opencode --version`(예 `1.19.5`).
- 로그인 판정(`CLIAccountSupport.opencodeStatus`): `opencode auth list`가 끝나는지(종료 코드)만 생존 확인으로 보고, 실제 판단은 `$XDG_DATA_HOME/opencode/auth.json`(기본 `~/.local/share/opencode/auth.json`, `$OPENCODE_AUTH_CONTENT`가 있으면 그것)의 항목(`type`이 `oauth`/`api`/`wellknown`인 제공자 이름만, 값은 읽어 보이지 않는다)과 환경 변수의 제공자 API 키다. 둘 다 없으면 **로그인 안 됨** + "opencode Zen의 무료 모델만" 안내(opencode Zen은 키 없이 무료 모델을 돌린다). `auth list` 출력은 clack UI라 파싱하지 않는다.
- 로그인: 터미널 창에서 `opencode auth login`(제공자 고르기 → 그 제공자의 OAuth 또는 API 키). `CLITerminalSignIn.table`의 `opencode` 항목(`loginRecovery.opencodeTerminal`, 제목 `loginRecovery.opencodeTerminalTitle`)이 로그인 카드·자동 재로그인에 쓰이고, 상태가 로그인됨으로 바뀌면 실패한 요청을 다시 보낸다. 로그아웃은 터미널의 `opencode auth logout`(앱은 하지 않는다).
- 로그인 실패 신호(`CLIAuthFailure.opencode`): ACP 오류 코드 -32000(opencode가 `ProviderAuthError`를 `AuthRequiredError`로 바꾼다), "Authentication required", "API key is missing", "invalid api key", 401+unauthorized, "opencode auth login". "407 Proxy Authentication Required"는 아니다. 신호일 뿐이고 카드는 위 상태가 로그인 안 됨일 때만 뜬다.

## 실행 방식: ACP를 골랐다

두 길을 비교했다.

| | `opencode acp` (ACP) | `opencode run --format json` |
|---|---|---|
| 승인 | `session/request_permission`(allow_once / allow_always / reject_once) → **앱의 승인 카드** | `--auto`(모두 허용)나 자동 거절뿐 |
| 스트리밍 | 답 텍스트 delta, 도구 pending→in_progress→completed | 끝난 text part와 끝난 도구만 |
| 사용량 | 프롬프트 응답의 마지막 모델 호출 토큰 + `usage_update`의 세션 누적 비용·컨텍스트 | step마다 정확한 tokens·cost |
| 이어하기 | `session/resume`(재생 없음), `session/load` | `-s ses_…` |
| 재사용 | Hermes(`hermes acp`)도 같은 클라이언트 | opencode 전용 |

**ACP를 골랐다.** 승인 카드(Claude·Codex가 쓰는 `ToolPermissionRequest` → `ToolPermissionBar`)를 그대로 쓸 수 있고, 같은 `ACPChannel`을 Hermes가 다음에 쓴다. 잃는 것은 토큰의 정확도 하나다(아래 사용량).

## argv / stdin / 환경

- 요청마다 프로세스 하나: `opencode acp`(cwd = 창의 작업 폴더). 상주 프로세스는 두지 않는다(실행 단위가 요청이라 상태·중지·대기열이 다른 에이전트와 같은 길).
- 환경: `OPENCODE_PERMISSION=<JSON>`(아래 권한 표). opencode가 사용자 설정 위에 마지막으로 합친다. 사용자의 설정 파일은 건드리지 않는다.
- stdio는 JSON-RPC 2.0, 한 줄에 한 객체(`ACPChannel`, `MightyCore/ACPChannel.swift`). 앱이 보내는 순서:
  1. `initialize` `{protocolVersion: 1, clientCapabilities: {fs: {readTextFile: false, writeTextFile: false}, terminal: false}, clientInfo}`
  2. 이어하기 id가 없으면 `session/new` `{cwd, mcpServers: []}`. 있으면 `agentCapabilities.sessionCapabilities.resume`이 있을 때 `session/resume`, 없고 `loadSession`이면 `session/load`(재생되는 update는 무시). 실패하면 한 줄 알리고 `session/new`.
  3. 설정: `session/set_config_option` `{sessionId, configId, value}`를 하나씩 — 모델(`model`, `provider/model`), 사고 강도(`effort`), plan 모드(`mode` = `plan`). 세션의 `configOptions`에 없는 값은 한 줄 알리고 빼며, 거절되어도 요청은 간다. build(기본)는 보내지 않는다.
  4. `session/prompt` `{sessionId, prompt: [{type: text, text}, {type: image, data, mimeType}…]}` — 그림은 base64로, 다른 첨부는 복사본 경로를 텍스트에 적는다.
- 응답 뒤 stdin을 닫고, 2초 안에 끝나지 않으면 앱이 종료한다(`opencode acp`는 stdin EOF에서 연결을 닫지만 서버가 남을 수 있다). 이렇게 끝낸 것은 실패가 아니다(`acpEnding`). 중지는 프로세스 종료.
- 초기화 제한 시간 45초(서버 부팅과 models.dev 갱신).
- 에이전트가 보내는 `fs/*`·`terminal/*` 요청은 -32601로 거절한다. opencode는 허용된 edit 뒤 `fs/write_text_file`을 시도하지만 오류를 무시하고 자기 도구로 고친다.

## 이벤트 → 기록

`ACPChannel`이 `acp.*` 객체로 바꿔 `CLIStreamParser.acp`, `SessionUsageTracker.consumeACP`, `ExecutionGraphTracker.consumeACP`에 넘긴다.

| ACP | 기록 |
|---|---|
| `session/new`·`resume` 결과 | `acp.session` → 창의 이어하기 id(`ses_…`), 사용량의 세션 id와 모델(`configOptions.model.currentValue`) |
| `agent_message_chunk` | 모아 두었다가 messageId가 바뀌거나 도구 호출·프롬프트 응답에서 답 한 개로 기록, 그래프 블록의 답 |
| `agent_thought_chunk` | 옮기지 않는다 |
| `tool_call` / `tool_call_update` | 도구 행(kind: execute→Bash, edit→Edit, read→Read, search→Grep, fetch→WebFetch, 그 밖은 title). `rawInput`의 camelCase 키는 snake_case로(`filePath`→`file_path`). 결과는 content의 텍스트, diff 경로, 또는 `rawOutput.output`/`error` |
| `plan` | 체크리스트 줄(opencode 1.19.5는 보내지 않는다) |
| `usage_update` | 컨텍스트 사용량·크기, 세션 누적 비용 |
| `session/request_permission` | 승인 카드. 허용 → `{outcome: {outcome: selected, optionId: "once"}}`, 거부 → `"reject"`, 같은 도구 호출이 끝나면 카드는 취소. "항상 허용"은 내보이지 않는다 |
| 프롬프트 응답 | `acp.prompt_result`: stopReason(end_turn, max_tokens 알림, refusal 알림, cancelled), 토큰 → 요청 완료 |
| JSON-RPC 오류 | `acp.error`: 실패, 오류 문구, 로그인 실패 판정 |
| JSON이 아닌 줄 | 출력 줄로 보인다 |

## 권한

opencode의 기본 규칙은 거의 다 `allow`라서, 앱이 요청마다 `OPENCODE_PERMISSION`으로 규칙을 준다. `ask`로 남은 일은 ACP로 물어 오고 창의 승인 카드가 된다(`ProviderOptions.asksInPane`).

| 모드 | `OPENCODE_PERMISSION` (+ ACP `mode`) | 뜻 |
|---|---|---|
| 기본 권한(manual) | edit·bash·webfetch·websearch·task·external_directory·doom_loop = ask, question = deny | 고치기·명령·웹·프로젝트 밖 전에 카드로 묻는다. 읽기·검색은 묻지 않는다 |
| 수정 허용(acceptEdits) | edit = allow, 나머지는 위와 같음 | 파일 수정만 묻지 않는다 |
| 계획(plan) | bash·webfetch·websearch·external_directory·doom_loop = ask, question = deny, `mode` = plan | opencode의 plan 에이전트(edit 거부)를 덮지 않으려고 edit 규칙은 주지 않는다 |
| 전체 접근(fullAccess) | `*`·external_directory·doom_loop = allow, question = deny | 묻지 않는다 |

`question` 도구는 창에서 답할 수 없어 늘 deny다. 기본값은 기본 권한.

## 모델과 사고 강도

- `opencode models`: 한 줄에 `provider/model`(opencode Zen의 `opencode/…`가 먼저). `provider/model` 모양이 아닌 줄(`--verbose` JSON, "Models cache refreshed")은 뺀다. 첫 항목 "opencode 설정 따름"(`default`, 설정을 보내지 않는다).
- 사고 강도: 모델의 variant(ACP `effort` 옵션). 모델마다 다르므로 Low, Medium, High, Max를 보이고, 그 모델에 없는 값은 실행 때 한 줄 알리고 뺀다. `default` 모델에서는 보이지 않는다.

## 사용량

- `usage_update.cost.amount`는 **세션 누적 비용**(`costScope: session`) → `UsageLedger`가 같은 창·같은 세션의 이전 값과의 차이만 센다. 이어하기 때 opencode가 load/resume 직후 보내는 값이 기준이 된다.
- 프롬프트 응답의 `usage`는 **그 요청의 마지막 모델 호출** 토큰(`tokenScope: response`). opencode의 `inputTokens`는 캐시를 빼므로 앱은 input = inputTokens + cachedRead + cachedWrite로 센다(다른 에이전트와 같게). 여러 단계가 있는 요청에서는 앞 단계 토큰이 빠진다 — 비용은 정확하고 토큰은 덜 센다.
- `used`/`size` → 컨텍스트 사용률. 계정 사용량은 없다(`accountUsage.detailOpencodeUnavailable`).

## 이어하기와 세션 목록

- 세션 id는 `ses_` + 26자(base62). `CoreValidation.identifier`는 `_`를 받지 않아 `CoreValidation.sessionIdentifier`(identifier 또는 `^ses_[A-Za-z0-9]+$`)를 이어하기 id 자리에 쓴다.
- "이전 세션 이어가기" 목록: `opencode.db`(`$OPENCODE_DB`, 기본 `~/.local/share/opencode/opencode.db`, SQLite)를 읽기 전용(`mode=ro`, 안 되면 `immutable=1`)으로 연다. `session`에서 `parent_id IS NULL`, `time_archived IS NULL`, `directory`가 작업 폴더, 60일 안. 제목은 첫 사용자 메시지(`message.data.role = user`의 `part.data.type = text`), 없으면 opencode 제목("New session - …"은 자리표시로 본다). 요청 수는 사용자 메시지 수, 모델은 마지막 assistant 메시지의 `providerID/modelID`.
- 마이티 흐름 보기로 이전 요청을 불러오는 `SessionHistory`는 아직 opencode를 읽지 않는다.

## 실물 CLI 없이 확인하지 못한 것

- fixture(`native/contracts/fixtures/opencode-acp-{answer,tools,auth,resume-missing}.jsonl`)는 녹화가 아니라 **소스에서 옮겨 쓴 것**이다. 실제 줄 순서, `messageId` 유무, `tool_call` 첫 이벤트의 `rawInput`(빈 객체로 둠), 권한 요청의 `title` 문구는 실물로 다시 확인해야 한다.
- `session/resume`이 없는 세션에 주는 오류 모양, `set_config_option`의 model 값이 `provider/model`인지, `effort` 값 이름(모델별 variant)이 Low/Medium/High/Max와 맞는지.
- stdin을 닫은 뒤 `opencode acp`가 스스로 끝나는지(안 끝나면 2초 뒤 앱이 끝낸다).
- `opencode.db`의 열 이름(`time_updated`, `time_archived`, `parent_id`, `directory`)과 `message.data`/`part.data` JSON 모양, WAL 중 읽기.
- `opencode auth list`가 로그인 없이도 0으로 끝나는지, 첫 실행의 부팅 시간(45초 안).

## 나중 단계

- Windows(Core·WinUI)와 폰, 원격 작업 공간의 opencode 창, 위임 자식으로 opencode 고르기.
- "항상 허용"(allow_always), 단계별 토큰(`run --format json`의 step_finish처럼 정확한 합계), `SessionHistory`로 이전 요청 불러오기, opencode 스킬의 `/` 명령, OpenCode 2(`@opencode/cli`).
- Hermes(`hermes acp`)를 같은 `ACPChannel`로 붙이기: provider별로 바뀌는 것은 argv, 권한 전달 방식, 로그인 판정뿐이게 둔다.

## Windows가 맞출 것

- 위 argv(`opencode acp`)·JSON-RPC 순서·`OPENCODE_PERMISSION` 표, `opencode-acp-*.jsonl` fixture와 같은 기록·사용량·카드.
- `setup-catalog-macos.json`의 `opencode` 항목을 Windows 항목(npmGlobal `opencode-ai`, prerequisites `["node"]`)과 함께 공유 `setup-catalog.json`으로 옮기고, Windows의 `SetupCatalog.KnownKeys`, `Wire.Providers`, 허용 목록에 더한다.
- `ses_` 세션 id를 받는 검증, `opencode.db` 세션 목록(Windows의 데이터 폴더 위치 확인 필요).
