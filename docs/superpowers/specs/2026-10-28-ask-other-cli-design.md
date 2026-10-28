# 입력창에서 다른 CLI에게 묻기 (oh-my-claudecode ask)

2026-10-28 · 사용자 요청 ("클러드 에이전트 모드이고 oh my claudecode 가 설치되어있다면 인풋창에 설치되어있는 다른 cli 로 묻는 버튼 또는 팝다운 메뉴를 만들어줘"). 고른 것: 보낼 대상 고르기(스위치), 맥 + 윈도우 + 폰, "이대로 진행".

## 목표
oh-my-claudecode(OMC)의 `ask` 스킬은 Claude 안에서 다른 CLI에게 물어보는 기능이다. 사용법은 `/oh-my-claudecode:ask <codex|gemini|antigravity|grok|cursor|…> <질문>`이다.
- 스킬은 `omc ask`를 실행한다.
- 답은 `.omc/artifacts/ask/…md` 파일에 저장된다.
- Claude는 그 파일 경로를 받아 답을 읽고 대화에 보인다.

이 스킬을 입력창에서 바로 쓰도록, Claude 에이전트 창의 입력창에 **"보낼 대상" 스위치**를 둔다.

## 보이는 조건
- 창이 Claude 에이전트 창이어야 한다. 맥은 `session.kind == "claude" && session.provider == "claude"`, 윈도우와 폰도 같은 뜻의 조건을 쓴다.
- 그 창의 슬래시 명령 목록에 `oh-my-claudecode:ask`가 있어야 한다. 이 목록은 설치된 플러그인의 skills에서 만들어지고, 지금처럼 캐시해 쓴다.
- 아래 대상 가운데 이 컴퓨터에 설치된 것이 하나 이상 있어야 한다.

## 대상 (공통 계약 `native/contracts/fixtures/ask-targets.json`)
| id (OMC 이름) | 보이는 이름 | 확인하는 실행 파일 |
|---|---|---|
| `codex` | Codex | `codex` |
| `antigravity` | Antigravity | `agy` |
| `gemini` | Gemini CLI | `gemini` |
| `grok` | Grok | `grok` |
| `cursor` | Cursor | `cursor-agent` |

- 목록의 순서가 메뉴의 순서다.
- 다음 둘은 넣지 않는다.
  - `claude`: 기본값이라 따로 고를 필요가 없다.
  - `muse`: 설치된 전역 `omc` 5.4.0이 아직 지원하지 않는다.
- 설치 여부는 앱이 자기 CLI를 찾을 때 쓰는 PATH 탐색과 같은 방식으로 확인한다. 맥은 로그인 셸 환경, 윈도우는 `where`와 같은 규칙이다.
  - 결과는 캐시한다.
  - 앱을 시작할 때, 그리고 슬래시 목록을 새로 읽을 때 같이 다시 확인한다(맥 30초 캐시와 같은 주기).

## 입력창 스위치
- 도구 줄에 알약 단추를 둔다. 이름은 고른 대상이고, 기본은 "Claude"다.
- 메뉴 내용:
  - "Claude (기본)"
  - 구분선
  - 설치된 대상들, 고른 것에 체크 표시
- 맥 도구 줄이 좁으면 다른 단추처럼 넘침(…) 메뉴로 들어간다. `ComposerToolbarMetrics` 폭 계산에 넣는다.
- 고른 값은 **창마다** 따로 기억하고, 창 상태에 저장한다. 맥은 `RunSession` 쪽 선택 필드, 윈도우도 같은 필드를 쓴다. 앱을 다시 켜도 유지된다. 옛 저장 파일에는 이 필드가 없으며, 없으면 Claude다.
- Claude가 아닌 대상을 고르면 다음이 바뀐다.
  - 입력창 안내 문구가 "{name}에게 묻기…"가 된다.
  - 알약에 강조색을 입힌다.
  - 첨부 단추를 끈다. `ask`는 글만 넘기기 때문이다. 이미 붙인 첨부가 있으면 스위치를 바꿀 때 그대로 두되, 보낼 수 없다는 안내를 보인다.
- 고른 대상이 더 이상 설치되어 있지 않거나 OMC가 사라지면 스위치는 Claude로 돌아간다. 이때 입력창 위에 한 번만 안내한다. 문구는 "{name}을(를) 찾을 수 없어 Claude로 보냅니다"다.

## 보내기
- 호스트가 보낼 글을 만든다. 규칙은 공통 계약에 둔다.
  - 대상이 Claude이면 글을 그대로 보낸다.
  - 그 밖이면 `/oh-my-claudecode:ask <id> <글>`을 보낸다.
  - 글의 앞뒤 공백은 지금처럼 다룬다.
  - 글이 비어 있으면 보내지 않는다.
  - 글이 이미 `/`로 시작하는 슬래시 명령이면 대상과 관계없이 그대로 보낸다. 명령을 감싸지 않는다.
- 그 뒤의 흐름은 지금의 Claude 보내기와 같다. 바쁜 창이면 대기열, "전송 중", 새 요청으로 화면 이동, 실패 처리 모두 그대로다.
- 대화와 흐름 보기에는 사용자가 보낸 글로 `/oh-my-claudecode:ask codex …`가 그대로 보인다.

## 폰
- 호스트는 세션 상세에 선택 필드를 더한다.
  - `askTargets?: [{ id, label }]`: 보이는 조건을 만족하는 창에만 싣는다.
  - `askTarget?: string`: 그 창의 지금 선택이다.
- 폰은 `askTargets`가 있을 때만 같은 스위치를 그린다.
- 폰의 선택은 그 창의 선택과 같은 값이다. 폰에서 바꾸면 호스트에 새 동작 `setAskTarget`(`{ target }`)을 보내 창의 값을 바꾼다. 맥에서 바꿔도 폰에 반영된다.
- 보낼 때는 호스트가 창의 지금 선택으로 감싼다. 폰은 글만 보낸다.
- 예전 호스트는 `askTargets`를 보내지 않으므로 폰에 스위치가 보이지 않는다. 예전 폰은 이 필드를 모르므로 무시하고, 보낸 글은 그 창의 선택에 따라 호스트가 감싼다.
- `docs/mobile-remote.md`에 필드와 동작을 적는다.

## 문구
locale 키를 `locales/{ko,en,zh,ja}.json`과 사본에 넣는다. 하드코딩한 한국어는 없어야 한다.
- `composer.ask.menuLabel` "보낼 대상"
- `composer.ask.claude` "Claude (기본)"
- `composer.ask.placeholder` "{name}에게 묻기…"
- `composer.ask.attachmentsOff` "다른 CLI에게 물을 때는 첨부를 보낼 수 없습니다"
- `composer.ask.fellBack` "{name}을(를) 찾을 수 없어 Claude로 보냅니다"

## 범위
- 맥 앱과 MightyCore, 윈도우 Core와 호스트, 웹 입력창, 폰 입력창이다.
- 도움말 4개 언어: 입력창 설명에 한 절을 넣는다.
- `omc`를 앱이 직접 실행하는 방식은 쓰지 않는다. 답이 Claude 대화 밖에 생기기 때문이다.
- OMC나 대상 CLI를 설치해 주는 일은 이번 범위가 아니다.

## 테스트
- 공통 계약 `ask-targets.json`의 사례를 맥, 윈도우 Core, 폰(jest)이 같이 읽는다.
  - 대상 목록과 실행 파일 이름.
  - 감싸기 규칙. 사례: Claude, codex, 빈 글, `/`로 시작하는 글, 앞뒤 공백.
  - 보이는 조건. 사례: OMC 없음, 대상 없음, Claude 창이 아님.
- 사라진 대상은 Claude로 돌아가는 규칙을 순수 함수로 두고 테스트한다.
- 창 상태 저장: 새 필드를 읽고 쓰는지, 옛 파일은 Claude로 읽는지 확인한다.
- 폰 변환: `askTargets`와 `askTarget`이 실리고, 조건이 안 맞으면 빠지는지 확인한다.
