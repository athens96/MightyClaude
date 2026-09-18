# 입력창 `/` 명령 완성

Claude·Codex 실행 창의 입력창에 `/`로 시작하는 글을 쓰면 그 실행기가 인식하는 스킬과 사용자 명령 목록이 입력창 위에 나타난다. Gemini 실행 창에서는 아래의 앱 기능 항목만 나온다(스킬·사용자 명령 스캔은 Claude·Codex만 지원). 계속 입력하면 이름과 설명으로 좁혀지고, ↑↓로 고른 뒤 Enter 또는 Tab을 누르거나 항목을 클릭하면 `/이름 `이 입력창에 채워지고 커서는 끝으로 간다. Esc는 현재 초안에 대해 목록을 닫는다. 이름 뒤에 공백을 넣어 인자를 쓰기 시작하면 목록은 자동으로 사라진다. 한글 조합 중에는 키를 가로채지 않는다.

## 어디에서 읽어 오나

| 실행기 | 위치 | 호출 이름 | 출처 표시 |
|---|---|---|---|
| Claude | `~/.claude/skills/<폴더>/SKILL.md` | frontmatter `name`(없으면 폴더명) | 사용자 스킬 |
| Claude | `~/.claude/commands/<이름>.md`, `<그룹>/<이름>.md` | `이름`, `그룹:이름` | 사용자 명령 |
| Claude | `~/.claude/plugins/installed_plugins.json`의 각 플러그인 `installPath` 아래 `skills/`, `commands/` | `플러그인:이름` | 플러그인 <이름> |
| Claude | 워크스페이스의 `.claude/skills`, `.claude/commands` | 위와 같음 | 프로젝트 스킬·명령 |
| Codex | `~/.codex/skills/<폴더>/SKILL.md`, 워크스페이스의 `.codex/skills` | frontmatter `name` | Codex 스킬·프로젝트 스킬 |

설명은 frontmatter `description`에서, 명령 파일에 frontmatter가 없으면 본문 첫 줄에서 가져온다. 같은 이름이 여러 곳에 있으면 프로젝트 항목이 사용자·플러그인 항목을 가린다. 이름은 `[A-Za-z0-9][A-Za-z0-9._-]{0,63}` 형식만 받아들이고, 목록은 400개까지다.

목록은 실행기·워크스페이스별로 30초 동안 캐시하며 목록이 열릴 때 다시 읽는다. CLI를 실행하지 않고 파일만 읽으므로 원격 워크스페이스에서는 이 Mac의 사용자 스킬만 나온다. 터미널 창(셸)에서는 동작하지 않는다.

## CLI 자체 명령은 앱이 대신한다

`/plugin`, `/clear`, `/model` 같은 CLI 자체 명령은 대화형 터미널 전용이라 앱이 쓰는 비대화형 실행(`claude -p`, `codex exec`, `gemini -p`)에서는 동작하지 않는다(`/plugin isn't available in this environment.`). 그래서 목록에는 **앱 기능** 항목으로 들어가고, 고르면 CLI에 보내는 대신 앱의 같은 기능을 바로 연다. 입력창은 비워진다. 각 CLI 사용자가 아는 이름을 그대로 쓴다.

| 동작 | Claude | Codex | Gemini |
|---|---|---|---|
| 플러그인 마켓플레이스 열기 | `/plugin` | `/plugins` | — (앱에 Gemini 확장 화면이 없음) |
| 모델 바꾸기 (이어서 모델을 고름) | `/model` | `/model` | `/model` |
| 작업 권한 바꾸기 (이어서 모드를 고름) | `/permissions` | `/approvals` | `/approval-mode` |
| 새 대화로 시작 | `/clear` | `/new` | `/clear` |
| 이 실행 창의 토큰·비용 (세션 정보) | `/cost`, `/usage` | `/status` | `/stats` |
| MightyClaude 설정 열기 | `/config` | `/settings` | `/settings` |
| 실행 창 이름 바꾸기 | `/rename` | `/rename` | `/rename` |
| 앱 명령 목록을 실행 기록에 표시 | `/help` | `/help` | `/help` |

스킬이나 사용자 명령이 앱 기능과 같은 이름이면 앱 기능이 우선한다. 목록을 닫고 `/clear`나 `/model opus`처럼 끝까지 직접 쳐서 Enter를 눌러도 CLI로 보내지 않고 같은 동작을 한다. 인자가 없거나 목록에 없는 값이면 목록을 다시 열거나 시스템 메시지로 알린다.

`/model`과 권한 명령은 인자를 받는다. 고르면 `/model `이 채워지고 목록이 그 실행기의 모델(또는 권한 모드)로 바뀐다. 계속 입력해 좁히고 고르면 바로 적용되며, 실행 기록에 시스템 메시지로 결과가 남는다. 실행 중에는 모델을 바꿀 수 없고, 이어갈 대화가 없거나 실행 중이면 새 대화 명령도 그 사정을 시스템 메시지로 알린다. 항목 오른쪽의 ↵ 표시는 앱에서 바로 실행되는 명령, › 표시는 이어서 고르는 명령이다.

`/compact`, `/mcp`, `/init`처럼 앱에 대응 기능이 없는 자체 명령은 목록에 넣지 않았다. 컨텍스트 정리는 CLI가 스스로 하며 그래프에 블록으로 표시된다([mighty-mode.md](mighty-mode.md)).

## 검색 순서

입력한 글자로 시작하는 이름 → `플러그인:` 뒤의 이름이 그 글자로 시작하는 항목 → 이름이나 설명에 그 글자가 포함된 항목 순서로 보여준다.
