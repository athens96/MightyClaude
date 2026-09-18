# Claude 상태 줄(statusLine) 표시

Claude Code 터미널에서 입력창 아래에 보이는 정보 줄(oh-my-claudecode의 HUD 등)은 플러그인이나 Mods가 그리는 것이 아니라 `settings.json`의 `statusLine` 설정이 만든다. CLI는 화면을 그릴 때마다 그 명령을 실행하고, 세션 정보를 JSON으로 stdin에 넣어 준 뒤, 명령이 출력한 줄을 그대로 보여준다. Mods(function hooks)에는 이 정보가 흐르지 않고, 앱이 쓰는 비대화형 실행(`claude -p`)에서는 상태 줄 자체가 그려지지 않는다.

그래서 MightyClaude는 **같은 명령을 같은 JSON으로 직접 실행**해 결과를 입력창 아래에 그린다. 특정 플러그인을 지원하는 것이 아니라 `statusLine`을 쓰는 모든 도구(oh-my-claudecode HUD, ccusage, 직접 만든 스크립트)가 그대로 동작한다.

## 어디서 읽나

Claude와 같은 우선순위로 첫 번째로 발견되는 설정을 쓴다.

1. 워크스페이스의 `.claude/settings.local.json`
2. 워크스페이스의 `.claude/settings.json`
3. `~/.claude/settings.json` (`CLAUDE_CONFIG_DIR`가 있으면 그 폴더)

`{"statusLine": {"type": "command", "command": "...", "padding": 0}}` 형식만 받는다. 이기는 위치의 항목이 `command` 형식이 아니면 상태 줄을 끈다.

워크스페이스(1·2)의 명령은 저장소가 가져온 파일에서 오므로 바로 실행하지 않는다. 처음 발견되면 입력창 아래에 명령 전문과 "이 워크스페이스에서 허용" 버튼을 보여주고, 허용하기 전까지는 사용자 설정(3)의 명령만 실행한다. 허용은 워크스페이스와 명령 내용(해시)에 묶여 저장되므로 저장소가 명령을 바꾸면 다시 묻는다. 같은 파일의 `outputStyle`과 `alwaysThinkingEnabled`가 있으면 페이로드의 `output_style`·`thinking`으로 전달하고, 없으면 그 구역을 넣지 않는다.

## 명령에 주는 JSON

CLI 2.1.x의 `Status` 페이로드와 같은 이름을 쓴다. 앱이 잴 수 없는 값은 0 또는 null이고, 없는 구역은 넣지 않는다.

| 필드 | 값 |
|---|---|
| `hook_event_name` | `Status` |
| `session_id`, `transcript_path` | 실행 창의 Claude 세션 ID와 `<config>/projects/<cwd 슬러그>/<id>.jsonl` (슬러그는 영숫자 외 글자를 `-`로) |
| `cwd`, `workspace.current_dir`, `workspace.project_dir` | 워크스페이스 경로 |
| `model.id`, `model.display_name` | 실행 창의 모델 (기본값이면 사용량에 기록된 실제 모델) |
| `version` | 설치된 Claude CLI 버전 |
| `cost.total_cost_usd`, `cost.total_duration_ms` | 세션 사용량의 비용, 실행 경과 시간. `total_api_duration_ms`·줄 수는 0 |
| `context_window.*` | 입력·출력·캐시 토큰, 컨텍스트 크기, `used_percentage`/`remaining_percentage` (측정 전에는 null) |
| `context_window.current_usage` | 측정한 컨텍스트 크기를 `input_tokens`로, 나머지는 0 (앱의 캐시 계수는 누적값이라 그대로 넣지 않음) |
| `exceeds_200k_tokens`, `fast_mode`, `effort.level` | 실행 설정에서 |
| `output_style.name`, `thinking.enabled` | 설정 파일에 `outputStyle`·`alwaysThinkingEnabled`가 있을 때만 |
| `rate_limits.five_hour`/`seven_day` | `rate_limit_event`로 받은 사용률과 초기화 시각(epoch 초). 이미 지난 창은 뺀다 |

## 실행과 표시

- `/bin/sh -c <command>`를 워크스페이스 폴더에서 실행한다. PATH는 앱이 CLI를 찾을 때 쓰는 것과 같다(`~/.local/bin`, `~/.npm-global/bin`, Homebrew 포함). CLI 실행과 같은 `posix_spawn` 경로를 써서 별도 프로세스 그룹으로 띄우고, 제한 시간에는 그룹 전체를 끝내며, 백그라운드로 남긴 자식이 파이프를 잡고 있어도 종료 후 1초 안에 결과를 확정한다.
- 한 실행 창에 한 번에 하나만 돌리고, 2초 안에 다시 요청되면 한 번으로 묶는다. 8초가 지나면 명령을 종료한다. 출력은 16 KiB, 6줄까지만 쓴다.
- 다시 실행하는 시점: 실행 창이 보일 때, 상태·세션 사용량·세션 ID·모델이 바뀔 때, 실행 중에는 10초마다, 쉬는 중에는 60초마다.
- 출력의 ANSI SGR(굵게·흐리게·기울임·밑줄, 16색·256색·RGB)을 앱 색상으로 옮겨 그린다. 커서 이동이나 OSC 같은 다른 이스케이프는 지운다. 줄은 한 줄씩 잘라 보여주고 드래그로 선택할 수 있다.
- 명령이 아무것도 출력하지 않고 실패하면 stderr 첫 줄이나 종료 코드를 대신 보여준다.
- 로컬 워크스페이스의 Claude 실행 창에서만 동작한다. Codex·Gemini·원격 워크스페이스·셸 창에는 없다.
- 설정 › 화면 › "Claude 상태 줄 표시"로 끌 수 있다.

## 한계

- 상태 줄 명령이 transcript 파일을 읽어 만드는 정보(최근 API 오류, 도구 실행 횟수 등)는 앱이 `-p`로 실행한 세션의 transcript에 기록된 만큼만 보인다.
- 줄 수 변화, 실제 API 시간은 앱이 재지 않아 0이다.
