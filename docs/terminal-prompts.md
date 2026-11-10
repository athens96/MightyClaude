# 터미널 입력 카드 (Mac · Windows · 폰)

에이전트 창의 터미널에서 돌고 있는 명령(에이전트의 `run_in_terminal`, 답의 셸 블록 실행)이 사용자의 입력을 기다리면, 그 에이전트 창과 폰에 카드가 뜬다. 사용자가 카드에서 답하면 그 답이 명령의 터미널에 그대로 입력된다. 사람이 답할 때만 입력되고, 에이전트는 대신 답하지 않는다. 터미널 창에서 직접 입력하는 것도 그대로 된다.

## 알아내기 (`TerminalPromptMonitor`, `TerminalPromptDetector`)

- 감시기는 에이전트 창마다 하나다. 실행 중인 명령의 출력을 **자기 커서로** 읽고(에이전트의 읽기와 무관), 에이전트에게 주는 것과 같은 방법으로 정리하고(`AgentTerminalOutputCleaner`), 마지막 4,096자를 지닌다. 앱이 0.4초마다 묻는다.
- 출력이 **0.8초 동안 늘지 않았을 때만** 판별한다. 그래서 진행 표시가 움직이는 동안에는 카드가 뜨지 않는다.
- 판별 규칙 (먼저 맞는 것):
  1. **목록**: 마지막 14줄 안에 `?`로 시작하는 질문 줄이 있고, 그 아래 줄이 둘 이상이며, 그중 하나가 `❯ › > ● ◉ ◆ →`로 시작하면 → `choice`. `text`는 질문 줄, `options`는 그 아래 줄들(앞뒤 공백 제거, 최대 12개).
  2. 마지막 줄(마지막 줄바꿈 뒤, 끝 공백 제거)이 비었거나 400자를 넘거나 `...`·`…`로 끝나면 → 아님.
  3. `(Y/n)`·`[y/N]`·`(yes/no)` 꼴(괄호 `()`·`[]`, 대소문자 무관) → `yesNo`. `Y/n`이면 `defaultYes: true`, `y/N`이면 `false`, 그 밖은 없음.
  4. `password|passphrase|passcode|pin(단어)|token|secret|비밀번호|암호|パスワード|密码` 뒤로 `:`·`?`·`：`로 끝나면 → `secret`.
  5. `? : › > ： ？` 중 하나로 끝나면 → `text`.
  6. 그 밖은 아님.
- 카드 `id`는 `"<handle>:<정리된 출력 글자 수>"`. 답한 카드는 그 명령의 출력이 늘기 전까지 다시 뜨지 않는다. 명령이 끝나면 카드도 없다.

## 답 (`TerminalPromptAnswer.input`)

| 답 | 터미널에 입력되는 것 |
|---|---|
| `yes` / `no` | `y\r` / `n\r` |
| `text` | 첫 CR·LF 앞까지의 글 + `\r` (한 답은 한 줄) |
| `up` / `down` | `ESC [ A` / `ESC [ B` |
| `enter` / `space` | `\r` / ` ` |

비밀번호(`secret`)의 답은 화면에 보이지 않고 어디에도 남기지 않는다(터미널도 보통 에코하지 않는다).

## 화면

- 에이전트 창의 승인 카드 자리(승인 카드 아래, 입력창 위). 제목 `terminalPrompt.title`, 터미널에 보이는 질문(고정폭), 오른쪽 위 `terminalPrompt.showTerminal`.
- `yesNo`: `terminalPrompt.no` / `terminalPrompt.yes` 단추. 기본값 쪽에 `(terminalPrompt.default)`를 붙이고 강조한다(기본값이 없으면 예를 강조).
- `secret`: 가려지는 입력 칸(`terminalPrompt.secretPlaceholder`) + `terminalPrompt.send`. 보낸 뒤 칸을 비운다.
- `text`: 입력 칸(`terminalPrompt.textPlaceholder`) + `terminalPrompt.enterOnly`(기본값을 받아들이는 Enter) + `terminalPrompt.send`.
- `choice`: 선택지 목록(표시된 줄 강조) + `terminalPrompt.up` / `terminalPrompt.down` / `terminalPrompt.select`(Enter).
- 아래에 `terminalPrompt.help`.
- 문자열은 `locales/*.json`의 `terminalPrompt.*` 키만 쓴다(오류: `terminalPrompt.error.gone`, `terminalPrompt.error.badAnswer`).

## 폰 프로토콜

- `GET /m1/sessions/{id}`의 `MobileSessionDetail`에 `terminalPrompt`(없으면 빠짐): `{ id, handle, text, options: [String], kind: "yesNo"|"secret"|"text"|"choice", defaultYes?: Bool }`.
- 카드가 생기거나 바뀌거나 사라지면 그 창의 revision을 올리고 `session:<id>`를 알린다.
- 답: `POST /m1/sessions/{id}/terminal-prompt` 본문 `{ promptId, answer: "yes"|"no"|"text"|"up"|"down"|"enter"|"space", text? }`. `text`는 `answer: "text"`일 때만, 1~4,096바이트. `promptId`는 1~200자, 제어 문자 없음. 잘못된 본문은 400 `terminalPrompt.error.badAnswer`, 카드가 이미 없거나 다른 카드면 409 `terminalPrompt.error.gone`.

## 구현 위치

- Mac: `MightyCore/TerminalPrompt.swift`(모델·판별·감시), `MightyCode/AppStore+TerminalPrompts.swift`(폴링·답·폰 알림), `MightyCode/TerminalPromptCard.swift`, `MobileRemoteModels.swift`(`MobileTerminalPromptAnswer`), `MobileRemoteService.swift`(`terminal-prompt` 경로).
- Windows·폰: 같은 규칙과 같은 문자열.
