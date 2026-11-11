# Hermes 에이전트 창 (Mac 1단계)

Nous Research의 [Hermes Agent](https://hermes-agent.nousresearch.com)(명령 `hermes`, 소스 [NousResearch/hermes-agent](https://github.com/NousResearch/hermes-agent))를 Claude·Codex·Antigravity·pi·opencode와 나란히 쓰는 여섯 번째 에이전트로 붙인다. 아무것도 설치되지 않은 Mac에서 앱의 구성 요소 화면만으로 설치·로그인·사용까지 된다. 1단계는 Mac만 한다(Windows·폰은 다음 단계).

처음에는 Hermes를 실행하지 않고 **소스를 읽고** 썼고, 그 뒤 앱으로 설치한 **실물 0.23.1**(`Hermes Agent v0.23.1 (2026.9.24) · stable`)로 다시 확인했다. fixture는 그 실물의 녹화다(아래 "실물로 확인한 것"). 읽은 소스는 `main` 브랜치 a2fa0a4b(2026-11-10)이고, 최신 릴리스 v0.23.1(2026-11-10)의 `acp_adapter/`도 같은 파일 구성이다. 근거: `acp_adapter/*`(entry.py, gateway_server.py, gateway_reply.py, gateway_session_meta.py, gateway_commands.py, permissions.py, edit_approval.py, elicitation.py, modes.py, auth.py, tools.py, events.py, model_catalog.py, catalog.py, server.py), `hermes_cli/{gateway_chat.py,gateway_client.py,main.py,main_agent_cmds.py,_startup_fast.py,subcommands/*}`, `gateway/{session_policy.py,session_usage.py}`, `tools/{approval.py,approval_context.py}`, `hermes_state_common.py`, `hermes_state_local.py`, `hermes_state_ids.py`, `agent/usage_pricing.py`, `scripts/install.sh`, `pyproject.toml`, `pm/{install.py,extras.py}`.

## 식별자

- provider id `hermes`, 화면 이름 `Hermes`, 실행 파일 `hermes`. 베타 배지, 금색(0xD9A441) 날개 달린 H 마크, SF Symbol `paperplane`.
- `ProviderOptions.ids`의 여섯 번째. pi·opencode처럼 원격 작업 공간에서는 고를 수 없다(`ProviderOptions.isLocalOnly`, `run.blocked.hermesRemote`). 위임에 참여하지 않는다. 폰 capability에는 아직 넣지 않았다.

## Hermes가 opencode와 다른 점

둘 다 같은 `ACPChannel`을 쓴다. Hermes의 구조가 달라서 생기는 차이만 적는다.

| | opencode | Hermes |
|---|---|---|
| 실제 작업이 도는 곳 | `opencode acp`가 띄운 자기 서버(프로세스와 함께 끝난다) | **게이트웨이 데몬**. `hermes acp`는 그 화면일 뿐이고, 없으면 데몬을 띄운다(쓰지 않으면 스스로 끝난다) |
| 중지 | 프로세스 종료 | `session/cancel`을 먼저 보내고, 1초 뒤 프로세스 종료 |
| 권한 전달 | 요청별 규칙(`OPENCODE_CONFIG_CONTENT`) | 수정 정책은 세션 설정(`edit_approval_policy`), 위험한 명령은 세션을 만들 때의 `HERMES_YOLO_MODE` |
| 계획 모드 | 있다 | **없다**. 내보이지 않는다 |
| 로그인 판정 | `opencode auth list --format json` | `hermes acp`의 `initialize` 응답(`authMethods`) |
| 모델 목록 | `opencode models` | 명령이 없다. 세션이 알려 준 목록을 앱이 간직한다 |
| 토큰 | 요청 전체 합 | **세션 누적** |
| 비용 | 세션 누적 비용 | 알려 주지 않는다 |
| 도구의 `rawInput` | 있다 | 자기 도구에는 없다(제목·위치·내용으로 요약한다) |
| 질문 | form elicitation을 알려야 묻는다 | 알리면 form으로, 아니면 선택지가 여럿인 승인 요청으로 온다. 둘 다 질문 카드가 된다. ACP에서는 clarify 도구가 기본으로 꺼져 있다 |

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
  - 실물의 문구: 제공자를 정하기 전에 만든 세션은 "⚠️ provider 'auto' has no endpoint configured, so the request is not sent anywhere …"라고 답한다. 이것도 신호다.
  - Hermes는 실패한 이유를 **답 글**로 말하고, 뒤따르는 JSON-RPC 오류는 `admitted_turn_failed`뿐이다. 그래서 그 요청의 답 글 끝 8KB와 오류 문구를 함께 본다.

## 실행 방식: ACP

- 요청마다 프로세스 하나: `hermes acp`(cwd = 창의 작업 폴더). stdout은 JSON-RPC만, 로그는 stderr.
- `hermes acp`는 이 사용자 홈의 게이트웨이 데몬에 붙는다(`connect_gateway`, 없으면 `idle_exit`로 띄운다). 세션·실행·승인 대기는 데몬의 것이다.
- 환경: 전체 접근이면 `HERMES_YOLO_MODE=1`, 아니면 그 변수를 **지운다**(셸이 내보낸 값이 창의 선택을 덮지 않게, `HermesAgent.apply`). `~/.hermes/config.yaml`과 `.env`는 건드리지 않는다.
- stdio는 JSON-RPC 2.0, 한 줄에 한 객체(`ACPChannel`). 앱이 보내는 순서:
  1. `initialize` `{protocolVersion: 1, clientCapabilities: {fs: {readTextFile: false, writeTextFile: false}, terminal: false, elicitation: {form: {}}}, clientInfo}`. form elicitation을 알리므로 질문(clarify)이 form으로 온다.
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
- **중지**: 턴이 데몬에서 돌기 때문에 프로세스만 끝내면 Hermes가 계속 일한다. 그래서 중지 신호가 `{"method": "session/cancel", "params": {sessionId}}`를 먼저 쓰고(`ACPCancellation`, `LiveRunRegistry.register(cancel:)`), 1초(`cancelGrace`) 뒤 프로세스를 끝낸다. 실물은 cancel 0.2초 뒤 `stopReason: cancelled`로 답했다. 앱 종료와 채널 오류(초기화 시간 초과 등)도 같다. opencode에는 쓰지 않는다.
- 초기화 제한 시간 90초(데몬을 처음 띄우는 시간).
- **stderr는 창에 옮기지 않는다.** Hermes는 stderr에 로그(`2026-11-11 01:18:54 [INFO] …`)를 쓰고, 오류로 답하는 요청마다 Python traceback(`acp.exceptions.RequestError: Internal error`)을 쓴다. 창에는 ACP 오류에서 만든 문구만 보인다. 로그 줄과 traceback이 아닌 줄(시작하지 못한 이유 등)은 그대로 보인다(`HermesAgent.StderrFilter`).
- **제공자가 없으면 세션을 열지 않는다.** `initialize` 응답의 `authMethods`에 제공자가 없으면(`hermes-setup`뿐) 요청을 거기서 멈추고 로그인 실패로 알린다(`ACPChannel`의 `admit`, `run.error.hermesNoProvider`). 열면 Hermes가 `auto` 제공자로 세션을 만들고, 그 세션은 나중에 제공자를 정해도 계속 실패하기 때문이다.
- **답한 적 없는 세션은 잇지 않는다.** Hermes 세션은 만들어질 때의 제공자·모델을 간직한다. 첫 요청이 실패한 세션(`sessions.message_count = 0`)을 이으면 같은 실패가 되풀이되므로 새 세션을 연다(`HermesAgent.messageCount`).

## 이벤트 → 기록

`ACPChannel`이 `acp.*` 객체로 바꿔 `CLIStreamParser.acp`, `SessionUsageTracker.consumeACP`에 넘긴다. opencode와 같은 것은 [opencode 문서](opencode-agent.md)의 표를 따르고, 다른 것만 적는다.

| ACP | 기록 |
|---|---|
| `session/new`·`resume` 결과 | `acp.session` → 이어하기 id(`local-` + 16진수 64자), 사용량의 모델(`configOptions.model.currentValue`), **모델 메뉴에 쓸 목록 저장** |
| `agent_message_chunk` | `messageId`(UUID)가 바뀌거나 도구 호출·프롬프트 응답이 오면 답 한 개로 기록. 답 앞의 빈 줄은 뺀다. Hermes는 도구를 쓴 턴이 끝날 때 **스트리밍한 답을 통째로 한 번 더** 보낸다(훅이 붙인 안내가 뒤에 붙기도 한다). 이미 받은 글로 시작하는 조각은 그 글을 바꿔 넣어 한 번만 보인다 |
| `agent_thought_chunk` | 옮기지 않는다 |
| `tool_call` / `tool_call_update` | 도구 행. 아래 |
| `plan` | Hermes의 todo. 체크리스트 줄 |
| `usage_update` | `used`/`size`만 온다 → 컨텍스트 사용률 |
| `session/request_permission` | 승인 카드. 아래 권한 절 |
| 프롬프트 응답 | `stopReason`(end_turn, cancelled), 세션 누적 토큰 |
| JSON-RPC 오류 | Python ACP SDK는 예상 못 한 실패를 -32603 "Internal error" + `data.details`로 낸다. `details`를 문구로 쓰고, `admitted_turn_failed`는 `run.error.hermesTurnFailed`로 바꾼다 |
| `config_option_update` | 사용량의 모델과 간직하는 모델 목록을 새로 한다(세션은 `custom:모델`로 열리고 곧 `custom:호스트:모델`로 바뀐다) |
| `available_commands_update`, `session_info_update`, `current_mode_update` | 기록하지 않는다 |

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
- **질문(clarify)**: 앱의 질문 카드(Claude의 AskUserQuestion과 같은 카드, `AgentQuestions`)로 받는다. 모양은 `acp_adapter/elicitation.py`의 것이다. fixture `hermes-acp-clarify.jsonl`(질문 셋에 답함)과 `hermes-acp-clarify-declined.jsonl`(거절)은 실물 0.23.1의 녹화다. 여러 개 고르기·입력 칸·승인 요청 모양은 녹화에 없어 Hermes의 생성 함수(`build_clarify_elicitation`, `build_clarify_permission`)가 만든 프레임으로 시험한다(`HermesTests.builtClarify`).
  - 녹화에서 본 것: clarify 호출 하나가 질문 여럿을 담으면 질문마다 `elicitation/create`가 하나씩 차례로 온다(JSON-RPC id는 0부터의 숫자, 0도 보통 id다). 앞에는 `tool_call`(제목 `clarify: <질문>`, 내용은 질문 JSON)이, 뒤에는 그 호출의 `completed`("clarify result")가 온다. Hermes는 첫 선택지 글 끝에 ` (Recommended)`를 붙여 보내고(퀴즈에서도), 그 글 전체가 답이다. 앱은 받은 대로 보이고 그대로 돌려준다. 답하면 같은 턴이 이어지고, 거절하면 Hermes가 답 없음으로 읽고 이어 간다.
  - form에는 `toolCallId`가 없다. 앱은 그때 열려 있는(아직 닫히지 않은) 마지막 도구 호출의 질문으로 본다: 그 행이 "대기"가 되고, 그 호출이 닫히면 카드를 거둔다.
  - **기다리는 시간**: Hermes는 `agent.clarify_timeout`(기본 90초) 동안만 답을 기다리고, 지나면 클라이언트에 알리지 않고 호출을 닫는다(Python ACP SDK는 요청 취소를 보내지 않는다). 앱은 호출이 닫히는 것을 보고 카드를 거두며 한 줄 남긴다(`acp.channel.questionWithdrawn`). 시간을 늘리는 것은 사용자의 Hermes 설정이다.
  - 앱이 `elicitation.form`을 알리므로 `elicitation/create` `{sessionId, mode: "form", message: <질문>, requestedSchema: {type: "object", properties: {answer: …}, required: ["answer"]}}`로 온다. `answer`는 선택지가 있으면 `{type: "string", enum: […]}`, 여러 개 고르기면 `{type: "array", items: {type: "string", enum: […]}}`, 없으면 `{type: "string"}`(입력 칸)이다. `toolCallId`가 없어 활동 행은 만들지 않는다.
  - 답: `{action: "accept", content: {answer: "Yes"}}`, 여러 개는 `{answer: ["a", "b"]}`. Hermes는 `answer`의 문자열을 `enum`과 맞춰 보지 않으므로 **직접 쓴 답도 같은 `answer`로 간다**(여러 개 고르기에서는 목록의 마지막 항목). 카드의 취소는 `{action: "decline"}`이고 Hermes는 건너뛰기(빈 답)로 읽고 이어 간다.
  - form을 알리지 않은 클라이언트에는 선택지마다 `allow_once` 옵션(`choice-N`)과 `reject_once`(`skip`)를 단 승인 요청(`toolCallId: clarify-<id>`, `rawInput: {question, choices}`)으로 온다. `allow_once`가 둘 이상이면 이것도 질문 카드로 보이고, 고른 선택지의 `optionId`로 답한다. 이 모양에는 직접 쓴 답을 실을 곳이 없어 카드에서 거절한다. 취소는 `skip`이다.
  - 카드로 보일 수 없으면(같은 이름의 선택지, 보기 12개 초과) 전처럼 건너뛰기로 답하고 질문과 선택지를 한 줄 남긴다(`acp.channel.questionSkipped`). form이면 `decline`과 `acp.channel.formDeclined`.
  - **ACP의 Hermes에는 clarify 도구가 기본으로 없다**(`toolsets.py`의 `hermes-acp` = coding 도구에서 clarify를 뺀 것). 꺼져 있으면 모델이 "clarify 도구가 없다"며 글로 묻는다. 그래서 **앱이 켠다**(`HermesQuestionTool`, 아래).
  - 켜는 길은 Hermes 설정의 `platform_toolsets.acp` 하나뿐이다. `hermes tools enable --platform acp`는 "Unknown platform 'acp'"로 안 되고, 실행별 플래그·환경 변수·ACP 인자도 없다(`session/new`는 `cwd`와 `mcpServers`만 읽고, `env_launch_flags`는 yolo 같은 불 값뿐이다).
  - 앱이 하는 일(앱 실행마다 Hermes 실행 파일·`HERMES_HOME`별로 한 번, 첫 Hermes 요청 전):
    1. `hermes config get --json platform_toolsets.acp`. 종료 코드 1과 stderr의 `Config key not set: platform_toolsets.acp`만 "없음"으로 본다. 그 밖의 실패(읽을 수 없는 설정, 관리되는 설정)에는 아무것도 하지 않고 다음 요청 때 다시 본다.
    2. 없으면 `hermes config get --json toolsets`를 읽고 `hermes config set platform_toolsets.acp '["hermes-acp","clarify","x_search"]'`를 실행한 뒤 다시 읽어 확인한다. JSON 배열 모양이어야 YAML 목록으로 저장된다(`[hermes-acp, clarify]`처럼 따옴표 없는 글은 문자열로 남아 무시된다). `--force`는 필요 없다. 기록에 한 줄 남긴다(`run.notice.hermesClarifyEnabled`: 키, 값, 되돌리는 명령).
    3. 사용자가 저장한 목록이 있으면 건드리지 않는다. 거기에 clarify가 없으면 더하는 법을 한 줄 알린다(`run.notice.hermesClarifyMissing`).
  - 값이 `[hermes-acp, clarify]`가 아니라 세 항목인 이유: 목록을 저장하면 `_get_platform_tools`가 `_composite_toolsets` 대신 `_explicit_toolsets`로 가고, 저장하지 않았을 때만 붙던 두 가지가 빠진다. `x_search`(xAI 자격 증명이 있을 때만 붙는다. 없으면 도구의 `check_fn`이 여전히 뺀다)는 항상 적고, `kanban`은 설정의 옛 최상위 `toolsets`에 있을 때만 적는다. 0.23.1의 `_get_platform_tools`로 비교하면 도구 집합은 전과 같고 clarify만 는다. 켜 둔 MCP 서버도 그대로 들어간다(목록에 MCP 서버 이름이 없으면 전부 포함).
  - 앱은 이 키 말고는 Hermes 설정을 읽거나 쓰지 않고, 파일을 직접 고치지 않는다. 되돌리기: `hermes config unset platform_toolsets.acp`(앱이 다음 실행 때 다시 켠다. 끄고 싶으면 `hermes config set platform_toolsets.acp '["hermes-acp"]'`처럼 clarify 없는 목록을 저장한다).
  - 적용 시점(실물로 확인): 게이트웨이는 세션을 만들 때(`session.create`) 설정을 새로 읽어 도구 모음을 정해 얼리므로 **새 대화부터** 적용되고, 이어 가는 세션은 만들 때의 도구를 쓴다. 데몬을 다시 띄울 필요는 없다. 앱이 키를 쓴 3초 뒤에 만든 세션에도 clarify가 있었다(`state_meta`의 `gateway.local_policy.v1:<세션>` → `policy.toolsets`). 세션의 도구는 모델 호출 없이 `/tools` 명령으로도 볼 수 있다.
  - **모델이 도구를 쓰게**: clarify가 켜져 있어도 Hermes의 모델은 보기를 글로 적었다(사용자의 창에서 두 번, 시험에서 세 번 중 세 번). Hermes에는 실행별 시스템 프롬프트가 없다(`session/new`는 폴더와 MCP 서버만, 실행 플래그는 불 값뿐). 그래서 앱은 clarify가 켜진 동안 **요청 글 뒤에** 한 문단을 붙여 보낸다(`HermesQuestionTool.promptNote`: "[Note from the Mighty Code app, not from the user: … call clarify …]"). 앞이 아니라 뒤인 이유는 Hermes가 첫 메시지의 앞머리로 세션 제목을 만들기 때문이다. 창의 기록에는 사용자의 글만 남고, 세션 목록은 저장된 요청에서 이 문단을 떼고 보인다. Hermes 자신의 기록(`state.db`, 터미널의 Hermes)에는 문단이 보인다. 붙인 뒤에는 "선택하는 문제를 내"와 "선택하는 질문을 해줘"가 여덟 번 중 여덟 번 clarify로 물었다(앞에 붙인 넷, 뒤에 붙인 넷).
  - 폰: 카드가 Claude의 질문과 같은 `ToolPermissionRequest`라 폰에도 그대로 가고, 폰의 답도 같은 길로 온다.
- 승인은 Hermes의 `approvals.timeout`(기본 300초)이 지나면 Hermes가 스스로 거절한다.
- 기본값은 기본 권한.

## 모델과 사고 강도

- Hermes에는 세션 없이 모델을 나열하는 명령이 없다(`hermes model`은 대화형이다). 세션 응답의 `configOptions.model.options`가 로그인한 제공자의 모델 목록이다.
- 앱은 Hermes 세션이 열릴 때마다 그 목록을 `~/Library/Application Support/MightyCode/hermes-models.json`에 간직하고(`HermesAgent.saveModels`), 모델 메뉴가 그것을 읽는다(`HermesAgent.cachedCatalog`). `~/.hermes`에는 쓰지 않는다.
- 이 파일의 자리는 앱의 프로필 폴더다(`ProviderService.hermesModelCacheURL`, `--profile`이면 그 폴더). 저장 폴더 없이 만든 `ProviderService`(테스트)는 이 파일이 없어, 가짜 Hermes의 세션이 사용자의 목록을 덮어쓰지 않는다. 전에는 테스트가 실제 파일에 픽스처 목록(`hermes-acp-resume.jsonl`의 19개, `custom:llm.example.com:…` 포함)을 써 넣었다. 다음 실제 Hermes 세션이 그것을 다시 덮어쓴다.
- Hermes 실행이 끝나면 앱이 Hermes 모델 목록을 다시 읽는다. 첫 요청이 가져온 모델이 손으로 새로 고치지 않아도 메뉴에 나온다.
- Hermes 목록은 아래 [CLI 계정](cli-accounts.md#모델-목록을-읽지-못할-때)의 "이전 목록 간직"에서 빠진다. 제공자가 없어 목록을 지운 것(`forgetModels`)을 이전 목록으로 되살리지 않는다.
- 그래서 **첫 요청을 보내기 전에는 "Hermes 설정 따름"(`default`)뿐**이다. 그때는 `hermes model`로 정한 기본 모델로 간다. 이유를 목록 설명에 적는다(`provider.catalog.hermesEmpty`).
- 목록에 없는 모델을 고른 채 보내면 한 줄 알리고 세션의 모델로 간다.
- 값의 모양: `anthropic:claude-opus-4-6`, 사용자 정의 끝점은 `custom:모델`과 `custom:호스트:모델` 두 가지로 함께 나온다. 둘 다 고를 수 있다.
- 제공자가 없는 Hermes를 만나면 간직한 목록을 지운다. 세션이 열릴 때와 `config_option_update`가 올 때마다 목록 전체를 바꿔 쓴다.
- 모델을 바꾸면 Hermes가 보호 장치(비싼 모델 등)를 확인으로 물을 수 있다. 그 확인도 승인 카드로 온다.
- 사고 강도: `reasoning_effort` low, medium, high, xhigh, max(Hermes는 none, minimal, ultra도 받지만 앱의 단계만 보인다).

## 사용량

- 프롬프트 응답의 `usage`는 **세션 누적 값**이다(데몬이 든 에이전트의 `session_*` 카운터, `gateway/session_usage.py`). `tokenScope`는 `session`이고, `UsageLedger`가 같은 창·같은 세션의 이전 값과의 차이만 센다. 데몬이 쉬다 다시 뜨면 카운터가 0부터라 값이 줄 수 있고, 그때는 전부를 센다.
- `inputTokens`는 캐시를 빼고 `totalTokens`는 캐시(읽기·쓰기)를 넣는다. 앱은 input = totalTokens − outputTokens로 센다(다른 에이전트처럼 캐시를 input에 넣는다). `cachedReadTokens`, `thoughtTokens`도 옮긴다.
- 비용은 ACP로 오지 않는다. 계정 사용량도 없다(`accountUsage.detailHermesUnavailable`).
- 실물로 확인: 한 세션의 두 번째 요청은 34,633, 다시 이은 뒤 46,130으로 **누적**이었다. 캐시 필드는 제공자가 주지 않으면 없다. 첫 모델 호출 전에 취소한 턴은 0을 보내므로 버린다.
- 마이티 흐름 블록에는 토큰을 싣지 않는다(요청별 값이 없다).

## 이어하기와 세션 목록

- ACP 세션 id는 `local-` + 16진수 64자다(소스에서 본 `<yyyymmdd>_<hhmmss>_<hex>`는 데몬 안쪽 id였다). `CoreValidation.identifier`가 그대로 받는다.
- "이전 세션 이어가기" 목록은 `$HERMES_HOME/state.db`(기본 `~/.hermes/state.db`)를 읽기 전용(`mode=ro`, 안 되면 `immutable=1`)으로 연다(`HermesSessions`).
  - 고르는 행: `sessions`에서 `source = 'acp'` 또는 `created_source = 'acp'`(데몬은 편집기 세션을 `local` + `created_source='acp'`로 적는다), `archived`·`hidden`이 아니고, 60일 안. Hermes의 ACP `session/list`가 내보이는 것과 같은 범위다. 터미널·메신저에서 만든 세션은 넣지 않는다.
  - 폴더: `cwd` 열, 없으면 `model_config`의 `cwd`.
  - 압축된 대화는 `parent_session_id`로 이어진 여러 행이다. 첫 행의 id 아래 한 줄로 묶는다(ACP가 잇는 id가 그것이다). 시각은 묶음에서 가장 늦은 것.
  - 제목: 첫 사용자 메시지(`messages.role = 'user'`의 `content`, 첨부가 있으면 JSON 목록의 text). 없으면 Hermes 제목.
  - 요청 수는 사용자 메시지 수. 메시지가 없는 세션은 뺀다.
  - 시각: `last_activity_at`은 세션을 만든 때 근처에 머문다. 가장 늦은 메시지의 `timestamp`를 쓴다.
  - Hermes 버전마다 열이 다를 수 있어, 있는 열만 질의에 넣는다(`PRAGMA table_info`).
- yolo 여부: `state_meta`의 `gateway.local_policy.v1:<세션 id>` 값(JSON)에서 `policy.request_json`의 `yolo`.
- 마이티 흐름 보기로 이전 요청을 불러오는 `SessionHistory`는 아직 Hermes를 읽지 않는다.

## 실물로 확인한 것 (Hermes 0.23.1)

fixture(`native/contracts/fixtures/hermes-acp-{answer,tools,auth,resume,resume-missing,full-access,cancel,unconfigured}.jsonl`)는 `hermes acp`의 **녹화**다. 세션 id, 폴더 경로, 사용자의 모델 이름만 바꿨고, 생각(thought) 조각은 메시지별로 이어 붙였다(앱이 옮기지 않고, 녹화 줄의 대부분이다).

- `answer`: 새 세션에 한 마디. `tools`: read_file, write_file(수정 승인 거부), `rm -rf`(위험한 명령 승인 거부), tool_search. `resume`: 두 요청이 있던 세션 잇기 + 수정 정책 바꾸기. `resume-missing`: 없는 세션. `full-access`: `HERMES_YOLO_MODE=1`로 만든 세션. `cancel`: 4초 뒤 취소. `auth`: 제공자 없이 만들어진 세션을 이었을 때의 실패. `unconfigured`: 빈 `HERMES_HOME`의 `initialize`.
- 소스에서 짐작한 대로였던 것:
  - 문자열 JSON-RPC id를 그대로 돌려준다. Hermes가 보내는 요청의 id는 숫자(0부터)다.
  - 빈 값은 생략한다(`status` 없는 `tool_call`, `rawInput` 없는 자기 도구).
  - 예상 못 한 실패는 -32603 "Internal error" + `data.details`(`not_found`, `admitted_turn_failed`).
  - resume은 답하기 전에 이전 대화를 다시 보낸다.
  - 승인 요청: 수정은 `edit-approval-N` + `{tool, arguments}` + 선택지 allow_once/deny, 위험한 명령은 `perm-check-N` + 선택지 다섯 개.
  - `HERMES_YOLO_MODE=1`은 `state_meta`의 `policy.request_json.yolo`에 남는다.
  - `initialize`만 보내고 닫는 로그인 확인: 0.45초에 답하고, stdin을 닫으면 0.1초 안에 끝난다.
  - `session/cancel` 0.2초 뒤 `cancelled`.
  - `hermes --version` 1.2초, 첫 줄 `Hermes Agent v0.23.1 (2026.9.24) · stable · local 3b4215785882`.
  - `state.db`의 열, `source = 'local'` + `created_source = 'acp'`, `cwd`, `message_count`.
- 짐작과 달랐던 것(고쳤다):
  - 세션 id 모양(`local-…`).
  - **제공자를 정하기 전에 요청하면** Hermes가 `auto` 제공자로 세션을 만들고, 그 세션은 뒤에 제공자를 정해도 "provider 'auto' has no endpoint configured"로 실패한다. 이 문구를 로그인 실패로 보지 못했고, 실패한 세션을 계속 이었다.
  - stderr의 로그와 traceback이 창에 오류로 보였다.
  - 도구를 쓴 턴 끝에 답이 한 번 더 온다(두 번 보였을 것이다).
  - 답 앞의 빈 줄, kind만 되풀이하는 update가 행 이름을 "Tool"로 바꾸던 것.
  - `last_activity_at`이 마지막 사용 시각이 아니다.
  - 취소한 턴의 0 토큰.

## 아직 확인하지 못한 것

- 질문(clarify)을 모델이 실제로 묻는 턴: 실물 0.23.1의 ACP 도구 모음에는 clarify가 없어(사용자 설정으로만 켠다) 모델이 "clarify 도구가 없다"며 글로 물었다(form을 알린 실행과 알리지 않은 실행 둘 다). 프레임은 Hermes 자신의 생성 함수로 만들어 시험했고, form을 알려도 보통 실행이 그대로 되는 것은 실물로 확인했다. 답이 간 뒤 게이트웨이의 `clarify.respond`와 턴이 이어지는 것은 확인하지 못했다. 여러 개 고르기·입력 칸 form과 승인 요청 모양의 실물 턴, 90초가 지나 Hermes가 스스로 닫는 순간, 직접 쓴 답이 `answer`로 가는 실물 턴은 아직 못 봤다(생성 함수의 프레임과 소스로 시험한다).
- 열쇠 없이 쓰는 로컬 끝점(Ollama 등)만 설정한 Hermes가 `authMethods`에 제공자를 내는지. 내지 않으면 앱이 요청을 막는다.
- 제공자 로그인이 끊겼을 때(만료된 토큰, 틀린 키)의 실제 문구.
- 압축된 대화의 `parent_session_id` 사슬(가지·하위 에이전트 세션과 섞이지 않는지), WAL 중 읽기.
- 게이트웨이 데몬을 처음 띄우는 시간(녹화 때는 이미 떠 있어 `session/new`가 0.4초였다), 뜨지 못할 때의 문구.
- minimumVersion `0.23.0`: 0.23.1만 확인했다.
- `hermes update --yes`가 정말 묻지 않는지. 설치기는 사용자가 앱으로 한 번 돌려 끝까지 갔다.
- 모델 바꾸기의 확인 카드, `approvals.mode`가 smart·off일 때.

## 나중 단계

- Windows(Core·WinUI)와 폰, 원격 작업 공간의 Hermes 창, 위임 자식으로 Hermes 고르기.
- "세션 동안 허용"·"항상 허용".
- 세션 없이 모델 목록 받기(Hermes에 그런 명령이 생기면), 서비스 등급(`service_tier`) 설정.
- 실행 중 끼어들기(`/steer`), Hermes의 `/` 명령(`available_commands_update`).
- 요청별 토큰과 비용(데몬의 `session.stats`에는 있지만 ACP로 오지 않는다).
- `SessionHistory`로 이전 요청 불러오기, 터미널·메신저에서 만든 Hermes 세션 잇기.
- 프로필(`hermes -p`) 고르기. 지금은 `$HERMES_HOME`이나 기본 프로필만 쓴다.

## Windows가 맞출 것

- 위 argv(`hermes acp`)·JSON-RPC 순서·권한 표(`edit_approval_policy` + `HERMES_YOLO_MODE`), `hermes-acp-*.jsonl` fixture와 같은 기록·사용량·카드.
- 중지할 때 `session/cancel`을 먼저 보내는 것, 질문 카드(form과 승인 요청 두 모양), `{tool, arguments}` 풀기, `rawInput` 없는 도구의 요약, `data.details` 오류 문구.
- yolo로 만든 세션을 묻는 창이 잇지 않는 규칙(`state_meta` 읽기).
- 제공자가 없으면 `initialize`에서 멈추기, 답한 적 없는 세션(`message_count = 0`)을 잇지 않기, stderr의 로그·traceback 거르기, 한 번 더 오는 답을 한 번만 보이기.
- `setup-catalog-macos.json`의 `hermes` 항목을 Windows 항목과 함께 공유 `setup-catalog.json`으로 옮긴다.
  - Hermes의 Windows 설치기(`setup-hermes.ps1`)와 옵션, 데이터 폴더 위치는 따로 확인해야 한다.
  - Windows의 `SetupCatalog.KnownKeys`, `Wire.Providers`, 허용 호스트에도 더한다.
- `initialize`로 읽는 로그인 판정, 세션이 준 모델 목록 간직하기, `state.db` 세션 목록.
