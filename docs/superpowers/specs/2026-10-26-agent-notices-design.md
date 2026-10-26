# 하위 에이전트 알림을 마이티 보기에 보이기

2026-10-26 · 사용자 요청 ("하위 에이전트 알림을 보여주는 작업 진행해줘")

## 목표
백그라운드 하위 에이전트(Agent 도구, `run_in_background`)가 끝나면 Claude Code는 두 가지를 실행 중인 Claude에 넣는다.
1. `<task-notification>`: `tool-use-id`, `task-id`(에이전트 id), `status`(completed / failed / killed / stopped), `summary`가 있다. 레코드에서 사용자 메시지의 `origin.kind`는 `task-notification`이다.
2. 하위 에이전트의 최종 보고: `Another Claude session sent a message:\n<agent-message from="<에이전트 id 또는 이름>">\n[Subagent hand-back] …머리말… The report follows:\n  <두 칸 들여쓴 보고>\n</agent-message>`이다. 레코드에서 `origin.kind`는 `peer`이고, `origin.from`과 `origin.senderTaskId`가 붙는다. 다른 일이 진행 중이면 `<system-reminder>` 안에 끼어 들어오기도 한다.

지금 맥·윈도우 마이티 보기는 백그라운드 **명령**(kind `task`)의 알림만 읽는다(`ExecutionGraphTracker.settleTasks`의 `kind == "task"` 조건). 에이전트 알림은 건너뛰고, 최종 보고는 어디서도 읽지 않는다. 백그라운드 명령과 같은 수준으로 맞춘다.

## 동작 (맥 기준, 윈도우 동일)
- **에이전트 블록 상태:** 그 에이전트의 `<task-notification>`이 오면 status대로 completed / error(failed·error) / stopped(killed·stopped·그 밖)로 바꾼다. 이미 끝난 상태(terminal)면 그대로 둔다. 짝은 `tool-use-id`로 찾고, 없으면 `task-id`로 찾는다. 에이전트 id는 Agent 도구의 발사 결과 글 `agentId: <id>`에서 기억한다(명령의 `background with ID:`와 같은 방식).
- **에이전트 결과:** 최종 보고가 오면 그 에이전트 블록의 결과(`output`)를 보고 본문으로 바꾼다. 본문은 머리말 문단 뒤의 들여쓴 보고에서 두 칸 들여쓰기를 걷은 글이다. 짝은 `from`이 에이전트 id면 id로 찾고, 이름이면 Agent 호출의 `name`으로 찾는다. 보고가 오면 블록은 completed가 된다(이미 terminal이면 상태는 두고 결과만 채운다). 알림의 `summary`는 결과가 비어 있을 때만 쓴다.
- **작업 알림 블록:** 에이전트가 끝나 Claude가 다시 답하는 턴은 백그라운드 명령과 같은 "작업 알림" steer 블록으로 보인다. 글은 `<에이전트 제목> · 완료/실패/중지`이고, 그 턴의 답은 지금처럼 요청 결과에 `작업 알림 이후` 절로 붙는다. 한 에이전트에 블록은 하나만 둔다. 알림과 최종 보고가 둘 다 와도 하나다.
- **결과 턴 구분:** 최종 보고로 시작된 턴의 `result`(origin `peer`)는 요청 자신의 결과가 아니다. `ClaudeStream.isNotificationResult`가 `task-notification`과 같이 `peer`도 알림 결과로 본다. 이 결과를 요청 결과로 잘못 보면 stdin이 일찍 닫히는 버그(기존 주석)가 생긴다. 실제 stream-json에서 이 result의 origin을 확인하고, 다르면 실제 값에 맞춘다.
- **지난 대화(기록) 다시 읽기:** `SessionHistory`가 이미 `peer`와 `task-notification`을 끼어든 턴으로 거른다. 기록에서 다시 그린 마이티 보기도 위와 같은 블록 상태와 결과를 보이게 한다. 같은 tracker 경로를 타면 따로 할 일은 없다. 다른 경로가 있으면 맞춘다.
- **세션 재시작:** 이전 세션에서 끝나지 못한 에이전트의 `stopped` 알림("didn't finish before the previous session ended")은 블록을 중지로 표시한다.

## 범위
- 맥(MightyCore `ExecutionGraphTracker`, `CLIStreamParser`, 필요하면 `SessionHistory`)과 윈도우 Core(같은 이름의 C# 파일)를 같은 규칙으로 바꾼다.
- 폰은 맥·윈도우가 보내는 마이티 구조를 그대로 그리므로 코드를 바꾸지 않는다.
- 웹·WinUI 화면은 Core가 만드는 블록을 그리므로 바꾸지 않는다. 새 문구가 필요할 때만 바꾼다.
- 도움말 4개 언어: 마이티 보기의 "작업 알림" 설명에 하위 에이전트도 포함된다고 한 줄 넣는다.

## 공통 사례
`native/contracts/fixtures/agent-notices.json`에 실제 레코드 모양을 담는다. 개인 경로와 아이디는 지운다.
- Agent 호출(`run_in_background`, `name` 있음·없음)과 발사 결과(`agentId: …`)
- 에이전트 `<task-notification>` 네 가지(completed / failed / killed / stopped·재시작)
- `agent-message` 최종 보고: 단독으로 오는 것, `<system-reminder>` 안에 끼인 것, `from`이 이름인 것
- 기대값: 블록 상태, 결과 글, 작업 알림 블록 수(에이전트당 1)

맥 테스트와 윈도우 Core 검사가 같은 파일을 읽는다.

## 테스트
- 위 사례로 tracker 순수 테스트를 맥 MightyCore와 윈도우 Core에 둔다.
- `isNotificationResult`에 peer 사례를 넣는다.
- 빌드·테스트 실행은 사용자가 요청할 때만 한다(2026-10-23 규칙). 텍스트 검사는 돌린다.

## 범위 밖
포그라운드 에이전트(지금도 tool result로 끝남), Codex·Antigravity 하위 에이전트 알림(형식이 다름), 알림 소리·푸시(폰 푸시는 기존 규칙 그대로).
