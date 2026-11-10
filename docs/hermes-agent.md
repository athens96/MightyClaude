# Hermes 에이전트 창 (Mac 1단계)

Nous Research의 [Hermes Agent](https://hermes-agent.nousresearch.com)(명령 `hermes`, 소스 [NousResearch/hermes-agent](https://github.com/NousResearch/hermes-agent))를 Claude·Codex·Antigravity·pi·opencode와 나란히 쓰는 여섯 번째 에이전트로 붙인다. 아무것도 설치되지 않은 Mac에서 앱의 구성 요소 화면만으로 설치·로그인·사용까지 된다. 1단계는 Mac만 한다(Windows·폰은 다음 단계).

이 문서와 fixture는 Hermes를 설치하거나 실행하지 않고 **소스를 읽고** 썼다. 읽은 것은 `main` 브랜치 a2fa0a4b(2026-11-10)이고, 최신 릴리스 v0.23.1(2026-11-10)의 `acp_adapter/`도 같은 파일 구성이다. 근거: `acp_adapter/*`(entry.py, gateway_server.py, gateway_reply.py, gateway_session_meta.py, gateway_commands.py, permissions.py, edit_approval.py, elicitation.py, modes.py, auth.py, tools.py, events.py, model_catalog.py, catalog.py, server.py), `hermes_cli/{gateway_chat.py,gateway_client.py,main.py,main_agent_cmds.py,_startup_fast.py,subcommands/*}`, `gateway/{session_policy.py,session_usage.py}`, `tools/{approval.py,approval_context.py}`, `hermes_state_common.py`, `hermes_state_local.py`, `hermes_state_ids.py`, `agent/usage_pricing.py`, `scripts/install.sh`, `pyproject.toml`, `pm/{install.py,extras.py}`.

## 식별자

- provider id `hermes`, 화면 이름 `Hermes`, 실행 파일 `hermes`. 베타 배지, 금색(0xD9A441) 날개 달린 H 마크, SF Symbol `paperplane`.
- `ProviderOptions.ids`의 여섯 번째. pi·opencode처럼 원격 작업 공간에서는 고를 수 없다(`ProviderOptions.isLocalOnly`, `run.blocked.hermesRemote`). 위임에 참여하지 않는다. 폰 capability에는 아직 넣지 않았다.

## Hermes가 opencode와 다른 점

둘 다 같은 `ACPChannel`을 쓴다. Hermes의 구조가 달라서 생기는 차이만 적는다.

| | opencode | Hermes |
|---|---|---|
| 실제 작업이 도는 곳 | `opencode acp`가 띄운 자기 서버(프로세스와 함께 끝난다) | **게이트웨이 데몬**. `hermes acp`는 그 화면일 뿐이고, 없으면 데몬을 띄운다(쓰지 않으면 스스로 끝난다) |
| 중지 | 프로세스 종료 | `session/cancel`을 먼저 보내고, 1.5초 뒤 프로세스 종료 |
| 권한 전달 | 요청별 규칙(`OPENCODE_CONFIG_CONTENT`) | 수정 정책은 세션 설정(`edit_approval_policy`), 위험한 명령은 세션을 만들 때의 `HERMES_YOLO_MODE` |
| 계획 모드 | 있다 | **없다**. 내보이지 않는다 |
| 로그인 판정 | `opencode auth list --format json` | `hermes acp`의 `initialize` 응답(`authMethods`) |
| 모델 목록 | `opencode models` | 명령이 없다. 세션이 알려 준 목록을 앱이 간직한다 |
| 토큰 | 요청 전체 합 | **세션 누적** |
| 비용 | 세션 누적 비용 | 알려 주지 않는다 |
| 도구의 `rawInput` | 있다 | 자기 도구에는 없다(제목·위치·내용으로 요약한다) |
| 질문 | elicitation을 알리지 않으면 건너뛴다 | 선택지가 여럿인 승인 요청으로 온다. 앱이 건너뛴다 |

## 설치와 로그인

- 구성 요소 항목 `hermes`: group `agent`, need `optional`, macOS만: probe executable `hermes` (minimumVersion `0.23.0`), install `officialScript` `https://hermes-agent.nousresearch.com/install.sh` (bash), prerequisites `["git"]`, source `https://github.com/NousResearch/hermes-agent`, afterInstall `signIn` provider `hermes`. opencode처럼 Mac 전용 `setup-catalog-macos.json`에 있다(공유 계약 `setup-catalog.json`은 그대로).
- 설치 명령: 스크립트를 내려받아 `bash -c "$mighty_script" mighty-install '--skip-setup'`으로 돌린다.
  - `--skip-setup`은 `SetupCatalog.scriptArguments`(스크립트 URL → 옵션)가 붙인다. 카탈로그 파일은 옵션을 줄 수 없다.
  - 이 옵션이 없으면 설치기가 끝에 자기 설정 마법사(`hermes setup`)와 `hermes gateway install`(로그인 항목으로 게이트웨이 등록)을 터미널에서 돌린다. 앱은 로그인(`hermes model`)을 따로 권하고, 게이트웨이는 `hermes acp`가 필요할 때 띄우므로 둘 다 건너뛴다.
  - 설치기가 스스로 하는 일: 고정된 uv 내려받기, uv로 Python 받기, `~/.hermes/hermes-agent`에 저장소 받기, 의존성 설치(기본 extra `all`에 ACP 패키지 `agent-client-protocol`이 들어 있다), `~/.local/bin/hermes` 런처, 셸 rc에 PATH 추가, 브라우저 도구(Chromium)와 computer-use 드라이버 내려받기. Homebrew·Node.js·uv를 앱이 먼저 깔 필요는 없다.
  - git이 없으면 설치기가 Xcode 명령줄 도구 창을 띄우고 기다린다. 앱은 `git` 항목(`xcode-select --install`)을 선행 항목으로 먼저 돌린다.
  - 허용 목록에 `hermes-agent.nousresearch.com`(호스트)과 `NousResearch/hermes-agent`(저장소)를 더했다.
- ACP 패키지가 빠진 설치라면 `hermes acp`가 "The ACP server can't start: its protocol packages are missing"을 내고 끝난다. 계정 화면이 `hermes pm install --extra acp`를 알려 준다(`settings.cliAccounts.detailHermesUnknown`). 공식 설치기로 깔았다면 생기지 않는다.
- 업데이트 `hermes update --yes`(`--yes`는 물음에 멈추지 않게 한다). 0.23.0보다 낮으면 구성 요소 화면에 "오래됨"으로 보이고 CLI 업데이트로 올린다.
- 버전 `hermes --version`은 여러 줄이다. 첫 줄 `Hermes Agent v0.23.1 (2026.11.10)`만 버전으로 쓴다(`HermesAgent.versionLine`). 뒤에는 설치 폴더, Python, 업데이트 상태가 온다.
- 로그인 판정(`CLIAccountSupport.hermesStatus`, `HermesAgent.probe`): Hermes에는 상태를 JSON으로 내는 명령이 없다. 그래서 `hermes acp`를 띄워 `initialize` 하나만 보내고 응답을 읽은 뒤 stdin을 닫는다. 세션을 열지 않으므로 게이트웨이는 뜨지 않는다.
  - `authMethods`에는 항상 터미널 설정 `hermes-setup`이 있고, 자격 증명이 풀리는 제공자가 있으면 그 제공자 이름을 id로 하는 항목이 하나 더 있다(`acp_adapter/auth.py`, `resolve_runtime_provider`). 그 항목이 있으면 **로그인됨**이고 이름만 보인다(자격 증명 값은 응답에 없다).
  - `hermes-setup`뿐이면 **로그인 안 됨**.
  - 응답이 없으면(오래된 Hermes, ACP 패키지 없음, 25초 초과) 알 수 없음.
- 로그인: 터미널 창에서 `hermes model`(제공자 고르기 → 로그인 또는 API 키 → 기본 모델). ACP의 터미널 인증(`hermes acp --setup`)이 돌리는 것과 같은 명령이다. `CLITerminalSignIn.table`의 `hermes` 항목이 로그인 카드·자동 재로그인에 쓰인다. 자격 증명을 지우는 것은 터미널의 `hermes auth`.
- 로그인 실패 신호(`CLIAuthFailure.hermes`): "Hermes is not connected to any AI provider yet", "No LLM provider configured", `hermes model`·`hermes setup`·`hermes auth add`를 돌리라는 안내, "Not logged into …", invalid api key, 401+unauthorized. "407 Proxy Authentication Required"는 아니다.
  - Hermes는 실패한 이유를 **답 글**로 말하고, 뒤따르는 JSON-RPC 오류는 `admitted_turn_failed`뿐이다. 그래서 그 요청의 답 글 끝 8KB와 오류 문구를 함께 본다.

## 실행 방식: ACP

- 요청마다 프로세스 하나: `hermes acp`(cwd = 창의 작업 폴더). stdout은 JSON-RPC만, 로그는 stderr.
- `hermes acp`는 이 사용자 홈의 게이트웨이 데몬에 붙는다(`connect_gateway`, 없으면 `idle_exit`로 띄운다). 세션·실행·승인 대기는 데몬의 것이다.
- 환경: 전체 접근이면 `HERMES_YOLO_MODE=1`, 아니면 그 변수를 **지운다**(셸이 내보낸 값이 창의 선택을 덮지 않게, `HermesAgent.apply`). `~/.hermes/config.yaml`과 `.env`는 건드리지 않는다.
- stdio는 JSON-RPC 2.0, 한 줄에 한 객체(`ACPChannel`). 앱이 보내는 순서:
  1. `initialize` `{protocolVersion: 1, clientCapabilities: {fs: {readTextFile: false, writeTextFile: false}, terminal: false}, clientInfo}`. elicitation은 알리지 않는다.
  2. 이어하기 id가 없으면 `session/new` `{cwd, mcpServers: []}`. 있으면 `session/resume`(Hermes는 `sessionCapabilities.resume`을 알린다).
     - Hermes는 resume에 답하기 **전에** 이전 대화를 `session/update`로 다시 보낸다. 세션이 열리기 전에 온 update는 이 요청의 것이 아니므로 버린다.
     - 세션이 없으면 `not_found` 오류가 오고, 한 줄 알리고 `session/new`.
     - 폴더가 다르면 Hermes가 세션의 작업 폴더를 옮긴다(`_move_workspace`).
  3. 설정은 `session/set_config_option` `{sessionId, configId, value}`로 하나씩. 세션 응답의 `configOptions`에 있는 값만 보내고, 현재 값과 같으면 보내지 않는다.
     - `model`: Hermes의 선택 id `제공자:모델`(예 `openrouter:anthropic/claude-sonnet-4.5`). `default`면 보내지 않는다.
     - `reasoning_effort`: low, medium, high, xhigh, max. `default`면 보내지 않는다(이미 정한 세션의 값은 그대로 남는다).
     - `edit_approval_policy`: 권한 표의 값. **항상** 보낸다(이어 온 세션이 창의 권한을 따르게).
     - 세션이 바쁘면 -32603 "… changes apply when the session is idle"로 거절한다. 한 줄 알리고 요청은 간다.
  4. `session/prompt` `{sessionId, prompt: [{type: text, text}, {type: image, data, mimeType}…]}`.
- 응답 뒤 stdin을 닫는다. `acp.run_agent`가 EOF에서 끝난다. 2초 안에 끝나지 않으면 앱이 끝내고, 실패가 아니다(`acpEnding`).
- **중지**: 턴이 데몬에서 돌기 때문에 프로세스만 끝내면 Hermes가 계속 일한다. 그래서 중지 신호가 `{"method": "session/cancel", "params": {sessionId}}`를 먼저 쓰고(`ACPCancellation`, `LiveRunRegistry.register(cancel:)`), 1.5초(`cancelGrace`) 뒤 프로세스를 끝낸다. 앱 종료와 채널 오류(초기화 시간 초과 등)도 같다. opencode에는 쓰지 않는다.
- 초기화 제한 시간 90초(데몬을 처음 띄우는 시간).

## 이벤트 → 기록

`ACPChannel`이 `acp.*` 객체로 바꿔 `CLIStreamParser.acp`, `SessionUsageTracker.consumeACP`에 넘긴다. opencode와 같은 것은 [opencode 문서](opencode-agent.md)의 표를 따르고, 다른 것만 적는다.

| ACP | 기록 |
|---|---|
| `session/new`·`resume` 결과 | `acp.session` → 이어하기 id(`20261111_101502_3fa9c1d2` 모양), 사용량의 모델(`configOptions.model.currentValue`), **모델 메뉴에 쓸 목록 저장** |
| `agent_message_chunk` | `messageId`(UUID)가 바뀌거나 도구 호출·프롬프트 응답이 오면 답 한 개로 기록 |
| `agent_thought_chunk` | 옮기지 않는다 |
| `tool_call` / `tool_call_update` | 도구 행. 아래 |
| `plan` | Hermes의 todo. 체크리스트 줄 |
| `usage_update` | `used`/`size`만 온다 → 컨텍스트 사용률 |
| `session/request_permission` | 승인 카드. 아래 권한 절 |
| 프롬프트 응답 | `stopReason`(end_turn, cancelled), 세션 누적 토큰 |
| JSON-RPC 오류 | Python ACP SDK는 예상 못 한 실패를 -32603 "Internal error" + `data.details`로 낸다. `details`를 문구로 쓰고, `admitted_turn_failed`는 `run.error.hermesTurnFailed`로 바꾼다 |
| `available_commands_update`, `session_info_update`, `current_mode_update`, `config_option_update` | 기록하지 않는다 |

도구 행:
- kind → 이름은 opencode와 같다(execute→Bash, edit→Edit, read→Read, search→Grep, fetch→WebFetch). Hermes의 terminal·execute_code·delegate_task는 execute, write_file·patch는 edit, web_search·web_extract는 fetch다. 그 밖(kind other)은 제목 `도구: 요약`의 콜론 앞을 이름으로 쓴다(`ACPChannel.titledTool`).
- Hermes는 자기 도구에 `rawInput`을 보내지 않는다(모르는 도구만 보낸다). 요약은 `locations[0].path`, 내용의 `$ 명령` 줄, 제목의 콜론 뒤 순으로 고른다(`ACPChannel.describedInput`).
- 결과는 content의 텍스트나 diff 경로, `rawOutput`.
- 승인 카드의 풍선(`perm-check-N`, `edit-approval-N`)은 답이 가면 Hermes가 `completed`/`failed`로 닫는다. 카드가 만든 행이 그 상태가 된다. 알린 적 없는 호출의 닫힘(건너뛴 질문)은 행을 만들지 않는다.

## 권한

Hermes에는 승인이 둘 있다.

- **파일 수정**: 세션의 수정 정책(ACP 모드 Default / Accept Edits / Don't Ask = `ask` / `workspace_session` / `session`). 민감한 경로는 어느 정책이든 묻는다.
- **위험한 명령**: 패턴에 걸리는 명령(`rm -rf` 등)만 묻는다. 사용자의 `approvals.mode`(manual 기본 / smart / off)를 따르고, `--yolo`면 묻지 않는다. 걸리지 않는 명령과 읽기는 원래 묻지 않는다.

둘 다 ACP `session/request_permission`으로 오고 창의 승인 카드가 된다(`ProviderOptions.asksInPane`은 Hermes의 모든 모드에서 true).

| 모드 | `edit_approval_policy` | `HERMES_YOLO_MODE` | 뜻 |
|---|---|---|---|
| 기본 권한(manual) | `ask` | 없음 | 파일을 고칠 때마다 카드. 위험한 명령은 `approvals.mode`대로 카드 |
| 수정 허용(acceptEdits) | `workspace_session` | 없음 | 작업 폴더와 임시 폴더 안의 수정은 묻지 않는다. 민감한 경로와 위험한 명령은 카드 |
| 전체 접근(fullAccess) | `session` | `1` | 위험한 명령도 묻지 않고(`--yolo`), 수정도 묻지 않는다. 민감한 경로의 수정만 묻는다 |

- **계획 모드는 없다.** Hermes에는 읽기 전용이나 계획 전용 실행이 없어서 내보이지 않는다(`ProviderOptions.permissionModes`). 저장된 plan은 기본 권한으로 읽는다.
- `approvals.mode`는 요청별로 바꿀 길이 없다(환경 변수도 CLI 옵션도 없고 설정 파일뿐). 그래서 앱은 건드리지 않는다. 사용자가 `off`로 두었다면 어느 모드에서든 위험한 명령을 묻지 않고, `smart`면 보조 모델이 먼저 판단한다.
- **`--yolo`는 세션을 만들 때 정해진다.** `hermes acp`는 `HERMES_YOLO_MODE`를 `session/new` 때 세션 정책에 얼리고(`env_launch_flags`), 데몬은 그 변수를 물려받지 않으며, resume은 얼린 정책을 그대로 쓴다. 그래서:
  - 앱은 이어 갈 세션이 `--yolo`로 만들어졌는지 `state.db`에서 읽는다(`HermesAgent.startedWithFullAccess`, 아래 세션 절).
  - yolo로 만든 세션을 전체 접근이 **아닌** 창이 이으려 하면 잇지 않고 새 세션을 연다(`run.notice.hermesFullAccessSession`). 묻지 않는 세션을 "묻는다"고 보이는 창으로 돌리지 않으려는 것이다.
  - yolo 없이 만든 세션을 전체 접근 창이 이으면 그대로 잇고, 계속 묻는다고 알린다(`run.notice.hermesAskingSession`). 새 대화부터 적용된다.
  - 기록을 읽지 못하면 그대로 잇는다.
- 카드의 답: 허용 → Hermes의 `allow_once`, 거부 → `deny`(reject_once). "세션 동안 허용"(`allow_session`)과 "항상 허용"(`allow_always`)은 내보이지 않는다.
- 수정 승인의 `rawInput`은 `{tool, arguments}`다. 카드에는 `arguments`를 풀어 보인다(`ACPChannel.displayInput`). 내용이 64KB를 넘는 쓰기는 카드에 다 보일 수 없어 거부된다(수정 허용 모드에서는 묻지 않으므로 된다).
- **질문(clarify)**: 앱이 elicitation을 알리지 않으므로 Hermes는 선택지마다 `allow_once` 옵션을 단 승인 요청으로 묻는다. 카드에는 허용이 하나뿐이라 고를 수 없다. `allow_once`가 둘 이상이면 앱이 건너뛰기(reject_once)로 답하고 질문과 선택지를 한 줄 남긴다(`acp.channel.questionSkipped`). Hermes는 답 없이 이어 간다.
- 승인은 Hermes의 `approvals.timeout`(기본 300초)이 지나면 Hermes가 스스로 거절한다.
- 기본값은 기본 권한.

## 모델과 사고 강도

- Hermes에는 세션 없이 모델을 나열하는 명령이 없다(`hermes model`은 대화형이다). 세션 응답의 `configOptions.model.options`가 로그인한 제공자의 모델 목록이다.
- 앱은 Hermes 세션이 열릴 때마다 그 목록을 `~/Library/Application Support/MightyCode/hermes-models.json`에 간직하고(`HermesAgent.saveModels`), 모델 메뉴가 그것을 읽는다(`HermesAgent.cachedCatalog`). `~/.hermes`에는 쓰지 않는다.
- 그래서 **첫 요청을 보내기 전에는 "Hermes 설정 따름"(`default`)뿐**이다. 그때는 `hermes model`로 정한 기본 모델로 간다. 이유를 목록 설명에 적는다(`provider.catalog.hermesEmpty`).
- 목록에 없는 모델을 고른 채 보내면 한 줄 알리고 세션의 모델로 간다.
- 모델을 바꾸면 Hermes가 보호 장치(비싼 모델 등)를 확인으로 물을 수 있다. 그 확인도 승인 카드로 온다.
- 사고 강도: `reasoning_effort` low, medium, high, xhigh, max(Hermes는 none, minimal, ultra도 받지만 앱의 단계만 보인다).

## 사용량

- 프롬프트 응답의 `usage`는 **세션 누적 값**이다(데몬이 든 에이전트의 `session_*` 카운터, `gateway/session_usage.py`). `tokenScope`는 `session`이고, `UsageLedger`가 같은 창·같은 세션의 이전 값과의 차이만 센다. 데몬이 쉬다 다시 뜨면 카운터가 0부터라 값이 줄 수 있고, 그때는 전부를 센다.
- `inputTokens`는 캐시를 빼고 `totalTokens`는 캐시(읽기·쓰기)를 넣는다. 앱은 input = totalTokens − outputTokens로 센다(다른 에이전트처럼 캐시를 input에 넣는다). `cachedReadTokens`, `thoughtTokens`도 옮긴다.
- 비용은 ACP로 오지 않는다. 계정 사용량도 없다(`accountUsage.detailHermesUnavailable`).
- 마이티 흐름 블록에는 토큰을 싣지 않는다(요청별 값이 없다).

## 이어하기와 세션 목록

- 세션 id는 `<yyyymmdd>_<hhmmss>_<hex 8자>`. `CoreValidation.identifier`가 그대로 받는다.
- "이전 세션 이어가기" 목록은 `$HERMES_HOME/state.db`(기본 `~/.hermes/state.db`)를 읽기 전용(`mode=ro`, 안 되면 `immutable=1`)으로 연다(`HermesSessions`).
  - 고르는 행: `sessions`에서 `source = 'acp'` 또는 `created_source = 'acp'`(데몬은 편집기 세션을 `local` + `created_source='acp'`로 적는다), `archived`·`hidden`이 아니고, 60일 안. Hermes의 ACP `session/list`가 내보이는 것과 같은 범위다. 터미널·메신저에서 만든 세션은 넣지 않는다.
  - 폴더: `cwd` 열, 없으면 `model_config`의 `cwd`.
  - 압축된 대화는 `parent_session_id`로 이어진 여러 행이다. 첫 행의 id 아래 한 줄로 묶는다(ACP가 잇는 id가 그것이다). 시각은 묶음에서 가장 늦은 것.
  - 제목: 첫 사용자 메시지(`messages.role = 'user'`의 `content`, 첨부가 있으면 JSON 목록의 text). 없으면 Hermes 제목.
  - 요청 수는 사용자 메시지 수. 메시지가 없는 세션은 뺀다.
  - Hermes 버전마다 열이 다를 수 있어, 있는 열만 질의에 넣는다(`PRAGMA table_info`).
- yolo 여부: `state_meta`의 `gateway.local_policy.v1:<세션 id>` 값(JSON)에서 `policy.request_json`의 `yolo`.
- 마이티 흐름 보기로 이전 요청을 불러오는 `SessionHistory`는 아직 Hermes를 읽지 않는다.

## 실물 CLI 없이 확인하지 못한 것

- fixture(`native/contracts/fixtures/hermes-acp-{answer,tools,auth,resume,resume-missing,full-access}.jsonl`)는 녹화가 아니라 **소스에서 옮겨 쓴 것**이다. 실물로 다시 볼 것:
  - Python ACP SDK(`agent-client-protocol` 0.9.0)가 내는 필드 이름과 빈 값의 생략 여부(`status` 없는 `tool_call`, `rawInput` 없는 호출)
  - 문자열 JSON-RPC id(`mighty-1`)를 그대로 돌려주는지
  - 예상 못 한 실패의 오류 모양(-32603 "Internal error" + `data.details`로 추정)
  - 줄 순서, `usage_update`가 오는 때, 권한 요청의 선택지 구성
  - 제공자가 없을 때 `session/new`가 되는지, 턴이 어떤 글과 오류로 끝나는지
- `initialize`만 보내고 닫는 로그인 확인이 실제로 응답을 받는지, 걸리는 시간(Python 시작, 토큰 갱신).
- 설치기를 `bash -c "$script" name --skip-setup`으로 돌렸을 때 끝까지 가는지, 걸리는 시간, Chromium 내려받기, PATH 반영.
- `hermes --version`이 4초 안에 끝나는지(업데이트 상태를 함께 본다), 첫 줄의 실제 모양.
- minimumVersion `0.23.0`: 게이트웨이 방식 ACP(`configOptions`로 모델·수정 정책·사고 강도)가 들어온 릴리스를 확인하지 못했다. v0.23.1에는 있다. 더 오래된 Hermes는 `configOptions`가 없어 설정이 가지 않는다.
- 게이트웨이 데몬을 처음 띄우는 시간(90초 안), 데몬이 뜨지 못할 때의 오류 문구.
- `session/cancel` 뒤 1.5초면 데몬이 턴을 멈추는지.
- stdin을 닫으면 `hermes acp`가 곧 끝나는지(안 끝나면 2초 뒤 앱이 끝낸다).
- `HERMES_YOLO_MODE=1`이 `session/new`에서 세션 정책에 실리는지, `state_meta`의 기록 모양(`policy.request_json.yolo`).
- `state.db`의 실제 열, WAL 중 읽기, 압축된 대화의 `parent_session_id` 사슬이 가지(branch)·하위 에이전트 세션과 섞이지 않는지.
- `hermes update --yes`가 정말 묻지 않는지.

## 나중 단계

- Windows(Core·WinUI)와 폰, 원격 작업 공간의 Hermes 창, 위임 자식으로 Hermes 고르기.
- "세션 동안 허용"·"항상 허용".
- 질문(clarify)을 앱의 질문 카드로 받기(`elicitation/create`를 알리면 form으로 온다).
- 세션 없이 모델 목록 받기(Hermes에 그런 명령이 생기면), 서비스 등급(`service_tier`) 설정.
- 실행 중 끼어들기(`/steer`), Hermes의 `/` 명령(`available_commands_update`).
- 요청별 토큰과 비용(데몬의 `session.stats`에는 있지만 ACP로 오지 않는다).
- `SessionHistory`로 이전 요청 불러오기, 터미널·메신저에서 만든 Hermes 세션 잇기.
- 프로필(`hermes -p`) 고르기. 지금은 `$HERMES_HOME`이나 기본 프로필만 쓴다.

## Windows가 맞출 것

- 위 argv(`hermes acp`)·JSON-RPC 순서·권한 표(`edit_approval_policy` + `HERMES_YOLO_MODE`), `hermes-acp-*.jsonl` fixture와 같은 기록·사용량·카드.
- 중지할 때 `session/cancel`을 먼저 보내는 것, 질문 건너뛰기, `{tool, arguments}` 풀기, `rawInput` 없는 도구의 요약, `data.details` 오류 문구.
- yolo로 만든 세션을 묻는 창이 잇지 않는 규칙(`state_meta` 읽기).
- `setup-catalog-macos.json`의 `hermes` 항목을 Windows 항목과 함께 공유 `setup-catalog.json`으로 옮긴다.
  - Hermes의 Windows 설치기(`setup-hermes.ps1`)와 옵션, 데이터 폴더 위치는 따로 확인해야 한다.
  - Windows의 `SetupCatalog.KnownKeys`, `Wire.Providers`, 허용 호스트에도 더한다.
- `initialize`로 읽는 로그인 판정, 세션이 준 모델 목록 간직하기, `state.db` 세션 목록.
