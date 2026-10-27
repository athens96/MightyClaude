# 백그라운드 작업 두 종류 구분과 실행 출력 보이기

2026-10-27 · 사용자 요청 ("두가지 경우를 따로 표기해주고 후자의 내용도 표시해줄수 있을까?" → "구분 + 실제 출력까지", 출력은 블록·카드에, 작업 줄엔 한 줄, 맥 + 윈도우 + 폰)

## 목표
Claude 창의 백그라운드 작업은 두 가지다.
- **A. 하위 에이전트 · 응답 대기:** Agent 도구를 `run_in_background`로 띄운 하위 에이전트. 요청 세션은 그 에이전트의 최종 보고(hand-back)를 기다린다.
- **B. 백그라운드 실행 · 알림으로 확인:** `run_in_background`로 띄운 명령(Bash 등). 세션은 직접 다루지 않고, 끝나면 `<task-notification>`이 온다.

지금 이 둘은 두 군데서 따로 추적된다.
- 입력창 위 작업 줄은 CLI의 `system` task 이벤트로 `kind = agent | shell | other`를 안다.
- 흐름·파이프라인 블록은 tool_use/tool_result와 `<task-notification>` 글로 만든다. 백그라운드 Agent는 `agent` 블록, 백그라운드 명령은 `task` 블록이다.

블록에는 "백그라운드 하위 에이전트"라는 표시가 없다. 명령의 출력은 어디서도 읽지 않는다. B의 블록은 명령 이름과 끝 알림의 한 줄 요약만 보인다.

이번 작업에서 할 일:
- 두 종류를 같은 이름으로 구분해 보인다.
- B에는 실행한 명령, 실행 중 출력의 마지막 줄들, 끝난 뒤의 상태와 알림 요약, 그 알림에 대한 Claude의 답을 보인다.
- 알림에 대한 답을 최종 결과에 `작업 알림 이후` 절로 붙이는 지금 동작은 그대로 둔다. 루트의 "작업 알림" steer 블록도 그대로 둔다.

## 동작 (맥 기준, 윈도우 동일)

### 블록 데이터
- **background 표시:** `MightyGraphNode`(윈도우 `MightyGraph.cs` 노드)에 선택 필드 `background: Bool`을 둔다. `run_in_background`로 띄운 agent 블록과 모든 task 블록이 `true`다. 화면은 이 값과 `kind`로 종류를 고른다.
  - `agent` + background → A
  - `task` → B
  - 그 밖 → 지금 그대로
- **B 블록이 들고 다니는 것:**
  - `input`: 실행한 명령. 지금도 명령이 들어 있다.
  - `outputFile`: 출력 파일 경로. 발사 tool_result의 `Output is being written to: <경로>`에서 읽고, `<task-notification>`의 `<output-file>`이 있으면 그 값으로 바꾼다. 경로는 화면에 보이지 않는다. 폰으로도 보내지 않는다.
  - `outputTail: [String]?`: 출력의 마지막 줄들(아래 규칙).
  - `noticeReply: String?`: 그 알림이 연 턴에서 Claude가 한 답. 그 턴을 연 알림이 이 블록 하나뿐일 때 이 블록에 붙는다. 여러 알림이 한 턴에 들어오면 루트 결과의 `작업 알림 이후` 절에만 남는다.
- **기다리는 일 세기:** `MightyGraphRun.backgroundWaiting`이 백그라운드 agent 블록도 센다. 지금은 task 블록만 센다.
- **작업 줄 잇기:** 작업 줄의 `BackgroundTask`와 흐름 블록은 둘 다 `toolUseId`를 가진다. 작업 줄은 같은 `toolUseId`의 블록에서 종류 이름과 출력의 마지막 한 줄을 가져온다. 짝이 없으면 지금처럼 `kind`(agent/shell/other)로 이름을 고르고 출력 줄은 없다.

### 실시간 출력 (호스트)
- **누가 읽나:** 맥 `AppStore`(윈도우 `RunManager` 쪽)가 1초 타이머를 돈다. 대상은 실행 중인 task 블록 가운데 `outputFile`이 있는 것이다.
- **읽기 규칙:** 순수 함수 `BackgroundOutputTail.lines(bytes:)`가 맡는다.
  - 파일 끝에서 최대 `maxBytes`(8192)를 읽는다.
  - 처음 줄이 잘렸으면(파일이 `maxBytes`보다 길면) 첫 줄을 버린다.
  - ANSI 색·커서 코드를 지운다.
  - `\r`로 덮어쓴 줄은 마지막 조각만 남긴다.
  - 끝의 빈 줄을 버리고, 마지막 `maxLines`(6)줄만 남긴다.
  - 한 줄이 `maxLineChars`(200)를 넘으면 자르고 `…`를 붙인다.
  - UTF-8로 못 읽는 바이트는 대체 문자로 바꾼다.
- **언제 보내나:** 결과가 바뀔 때만 블록의 `outputTail`을 바꾸고 그래프를 다시 보낸다. 기존 그래프 갱신 경로를 쓴다.
- **끝날 때:** 블록이 끝나면(알림 도착) 마지막으로 한 번 더 읽고 멈춘다.
- **못 읽을 때:** 파일이 없거나 읽을 수 없으면 `outputTail`을 비워 둔다. 오류 표시는 하지 않는다. 원격 워크스페이스 창이라 파일이 다른 컴퓨터에 있는 경우도 같다.
- **기록에서 다시 그릴 때:** 지난 대화로 다시 그린 블록도 출력 파일이 아직 있으면 한 번 읽어 보인다. 없으면 출력 칸이 없다.
- **공통 계약:** 규칙 숫자와 예시는 `native/contracts/fixtures/background-output.json`에 둔다. 입력 바이트(base64)와 기대 줄을 담는다. 맥·윈도우·폰이 같은 파일로 테스트한다. 폰은 호스트가 보낸 줄을 그대로 그리지만, 계약 숫자 확인용으로 읽는다.

### 화면
**흐름 블록 (맥 `MightyGraphView`, 윈도우 웹 `Block.tsx`, 폰 마이티 블록)**
- **A:** 머리 아래에 "하위 에이전트 · 응답 대기"를 붙인다. 끝나면 "하위 에이전트 · 완료/실패/중지"로 바꾼다. 결과는 지금처럼 최종 보고다.
- **B:** 머리 아래에 "백그라운드 실행 · 알림으로 확인"을 붙인다. 끝나면 "백그라운드 실행 · 완료/실패/중지"로 바꾼다. 몸은 순서대로:
  1. 명령 한 줄(고정폭, 길면 줄임)
  2. 실행 중일 때 `outputTail`(고정폭, 최대 6줄, 흐린 바탕)
  3. 끝나면 알림 요약, 그리고 `noticeReply`가 있으면 "Claude의 답" 절

**파이프라인 카드 (맥 `MightyStructureView`, 윈도우 웹 `Structure.tsx`, 폰 `structure-view`)**
- 흐름 블록과 같은 종류 표시를 단다.
- B 카드는 출력 마지막 2줄을 보인다. 끝난 카드는 지금처럼 접히고, 펼치면 위 1~3이 보인다.

**입력창 위 작업 줄 (맥 `BackgroundWorkStrip`, 윈도우 WinUI·웹 카드)**
- 각 행의 종류 글을 "하위 에이전트 · 응답 대기"와 "백그라운드 실행 · 알림으로 확인"으로 바꾼다.
- B 행에는 출력의 마지막 한 줄을 흐리게 붙인다(고정폭, 한 줄, 길면 줄임).
- `other` 종류는 지금 이름("작업")을 그대로 쓴다.

**문구:** locale 키를 `locales/{ko,en,zh,ja}.json`과 사본에 넣는다. 하드코딩한 한국어는 없어야 한다.
- `graph.background.agentWaiting` "하위 에이전트 · 응답 대기"
- `graph.background.runWaiting` "백그라운드 실행 · 알림으로 확인"
- `graph.background.agentEnded` "하위 에이전트 · {status}"
- `graph.background.runEnded` "백그라운드 실행 · {status}"
- `graph.background.reply` "Claude의 답"

### 폰으로 보내기 (`docs/mobile-remote.md`)
- `MobileBlock`과 구조 `Node`에 선택 필드를 더한다.
  - `background?: true`
  - `outputTail?: [string]`(최대 6줄)
  - `noticeReply?: string`(블록 `output`과 같은 길이 제한)
- `outputFile`은 보내지 않는다.
- 예전 폰은 모르는 필드를 무시한다. 새 폰이 예전 호스트에 붙으면 이 필드가 없으니 지금 모습 그대로다.
- 폰에는 입력창 위 작업 줄이 없으므로, 블록과 카드만 바뀐다.

## 범위
- Claude 창만 해당한다. Codex·Antigravity에는 이런 백그라운드 명령이 없어 바꾸지 않는다.
- 맥: MightyCore(`ExecutionGraphTracker`, `MightyGraph`, 새 `BackgroundOutputTail`), 앱(타이머, 흐름·파이프라인·작업 줄 화면, 폰 블록 변환).
- 윈도우: Core(같은 이름의 C#), 호스트 타이머, 웹 화면(흐름·파이프라인·작업 줄 카드). WinUI 작업 줄도 종류 글을 바꾼다.
- 폰: 타입, 마이티 블록, 구조 카드.
- 도움말 4개 언어: 마이티 보기의 백그라운드 작업 설명에 두 종류와 출력 표시를 넣는다.
- 고정된 스타일 엔진(`MightyCore/Styles/**`)은 건드리지 않는다. 스타일 패널의 `taskList` 위젯은 이번 범위가 아니다.

## 테스트
- `background-output.json`으로 출력 읽기 순수 테스트를 둔다(맥 MightyCore, 윈도우 Core, 폰 jest 계약 숫자). 사례:
  - 짧은 파일
  - 8 KB 넘는 파일(첫 줄 버림)
  - ANSI 색
  - `\r` 진행 막대
  - 끝 빈 줄
  - 긴 줄 자르기
  - 깨진 UTF-8
  - 빈 파일
- tracker 테스트(맥·윈도우, `agent-notices.json`에 사례 추가):
  - 발사 결과에서 `outputFile`을 읽는다.
  - 알림의 `<output-file>`로 바꾼다.
  - 백그라운드 agent는 `background`이고 foreground agent는 아니다.
  - 알림 하나가 연 턴의 답이 그 task 블록의 `noticeReply`가 된다.
  - 한 턴에 두 알림이면 `noticeReply`가 없다.
  - `backgroundWaiting`이 백그라운드 agent를 센다.
- 폰 변환 테스트: `MobileBlock`에 `background`/`outputTail`/`noticeReply`가 실리고 `outputFile`은 안 실린다.
- 빌드와 테스트 실행은 이 작업이 끝난 뒤 사용자 요청대로(빌드 → 테스트 → 릴리즈) 한다.
