# CLI 계정 (로그인 확인 · 로그아웃 · 계정 변경)

설정 › **CLI 계정**에서 Claude Code·Codex·Gemini CLI가 어떤 계정으로 로그인돼 있는지 보고, 로그아웃하거나 다른 계정으로 바꿀 수 있다. 세 CLI는 자격 증명을 각자 보관하며, 앱은 계정 표시에 필요한 값(이메일·플랜·로그인 방식)만 읽는다. 토큰은 읽어도 화면·로그·상태 파일에 남기지 않는다.

## 상태를 어디서 읽나

| CLI | 방법 | 표시 |
|---|---|---|
| Claude | `claude auth status --json` | 이메일(없으면 조직명) · 구독 종류 · Claude 구독 / Anthropic Console |
| Codex | `codex login status` + `~/.codex/auth.json`의 id 토큰 안 계정 정보(이메일, ChatGPT 플랜) | 이메일 · 플랜 · ChatGPT / API 키 |
| Gemini | 상태 명령이 없어 `~/.gemini/settings.json`의 `security.auth.selectedType`, `google_accounts.json`의 `active`, `oauth_creds.json` 존재 여부 | Google 계정 이메일 / Gemini API 키 / Vertex AI |

## 로그아웃

- Claude: `claude auth logout`, Codex: `codex logout`.
- Gemini: CLI의 `/auth` 화면이 하는 것과 같이 `oauth_creds.json`을 지우고 `google_accounts.json`의 `active`를 `old`로 옮긴다.
- 그 실행기로 실행 중인 요청이 있거나 CLI 업데이트 중이면 막는다. 터미널에서 직접 쓰는 CLI에도 같이 적용된다는 점을 확인 창에서 알린다.

## 로그인 · 계정 변경

로그인은 브라우저 인증이 끼는 대화형 절차라 앱 안의 **터미널 실행 창**에서 진행한다. "로그인"을 누르면 설정을 닫고 로컬 워크스페이스에 "<CLI> 로그인" 터미널 창을 추가한 뒤, 셸이 뜨면 명령을 붙여 넣고 Enter를 눌러 실행한다(붙여넣기만으로는 셸이 실행하지 않는다). 세 번 시도해도 입력하지 못하면 직접 입력할 명령을 알려준다.

| CLI | 입력하는 명령 |
|---|---|
| Claude | `claude auth login` (메뉴에서 Console 과금을 고르면 `--console`) |
| Codex | `codex login` |
| Gemini | `gemini` (로그아웃 상태로 시작하면 인증 방식을 묻고 브라우저를 연다) |

"계정 변경"은 확인 후 로그아웃하고 곧바로 로그인 터미널을 연다. 브라우저가 같은 계정으로 바로 넘어가려 하면 계정 선택 화면에서 원하는 계정을 고른다. 로그인 터미널을 연 뒤에는 앱이(설정 창이 닫혀 있어도) 5초마다 상태를 확인해 로그인이 끝나면 멈추고, 10분이 지나거나 그 터미널 창을 닫거나 설정의 "대기 취소"를 누르면 그만둔다. 바꾼 계정은 각 실행 창의 **다음 요청부터** 적용된다.

설정은 시트라서 메인 창의 오류 배너가 가려진다. 계정 관련 안내(실행 중이라 바꿀 수 없음, 로컬 워크스페이스 없음, 로그아웃 확인 실패 등)는 해당 CLI 줄 안에 표시한다.

## 한계

- 로컬 워크스페이스가 하나도 없으면 로그인 터미널을 열 수 없다.
- API 키 방식 로그인(`codex login --with-api-key`, `GEMINI_API_KEY`)은 상태 표시만 하고 앱에서 키를 입력받지 않는다. Gemini의 API 키·Vertex AI 방식은 앱에서 로그아웃할 수 없어 버튼 대신 바꾸는 방법을 안내한다.
- 실행 중인지 여부는 앱의 에이전트 실행 창만 본다. 터미널 창에서 직접 실행 중인 CLI가 있어도 로그아웃을 막지 않는다.
- Claude CLI가 `auth status --json`을 지원하지 않거나 시간 안에 응답하지 않으면 "로그인되지 않음"이 아니라 "상태를 읽지 못했습니다"로 표시한다. Codex 계정 정보는 `CODEX_HOME`이 있으면 그 폴더의 `auth.json`에서 읽는다.
- 상태 표시줄의 계정 사용량은 다음 갱신 주기에 새 계정 기준으로 바뀐다.
