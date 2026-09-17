# 모바일 리모트 (iOS · Android)

PC에서 실행 중인 MightyClaude에 휴대폰으로 접속해 워크스페이스와 에이전트(실행 창)를 보고, 요청을 보내고, 권한·질문에 답하는 기능이다. 모바일 앱은 `mobile/`의 Expo(React Native) 프로젝트이고, 서버는 데스크톱 앱 안에 있다. 연결은 Tailscale 네트워크 안에서만 허용한다(HTTP, 암호화는 Tailscale WireGuard가 담당).

## 동작 개요

1. Mac의 **설정 → 구성 요소**에서 Tailscale을 설치·실행·로그인한다(자세한 내용은 [components.md](components.md)). 그다음 **설정 → 모바일 리모트**에서 스위치를 켠다. 앱은 Tailscale IP에 포트(기본 43138)를 열고 QR 코드와 연결 키를 보여준다. 스위치 상태와 키는 저장되어 앱을 다시 켜도 유지된다(Tailscale이 켜져 있을 때 자동으로 다시 연다).
2. 휴대폰 앱에서 QR을 스캔하거나 주소·키를 입력해 페어링한다. 키는 휴대폰의 보안 저장소에 남는다.
3. 앱은 `/m1/state`를 롱폴링해 워크스페이스·세션 목록을 갱신하고, 세션을 열면 `/m1/sessions/{id}`를 롱폴링해 대화와 대기 중인 권한 요청을 보여준다.
4. 요청 전송은 데스크톱의 입력창과 같은 규칙을 따른다. 실행 중인 로컬 Claude 창이면 진행 중인 턴에 바로 전달되고, 그 밖에는 대기열에 들어간다. 중지, 권한 허용·거부, 질문 답변, 새 실행 창 만들기도 가능하다.

기존 **원격 워크스페이스**(데스크톱↔데스크톱, `/v1/*`, 포트 43137)와는 별개의 리스너·키를 사용한다. 그쪽은 앱을 켤 때마다 꺼지지만, 모바일 리모트는 켜 둔 상태가 저장된다.

## 프로토콜 m1

기본 주소 `http://<tailscale-ip>:<port>`. 모든 요청에 `Authorization: Bearer <key>`와 `x-mighty-mobile-version: 1`을 보낸다. POST는 `Content-Type: application/json`. 브라우저가 아니므로 `Origin` 헤더는 보내지 않는다(있으면 거부). 성공 응답 본문에는 `"protocol": 1`이 있고, 오류는 `{ "protocol": 1, "error": "..." }`와 HTTP 상태(400·401·403·404·409·413·415·426·429·503)다.

| 메서드 | 경로 | 요청 | 응답 |
|---|---|---|---|
| GET | `/m1/info` | | `{ protocol, hostId, hostName, appVersion, platform }` |
| GET | `/m1/state?since=<rev>&wait=<0..10>` | | `MobileState` — `revision`이 `since`보다 커질 때까지 최대 `wait`초 기다렸다가 응답. 시간 안에 변화가 없으면 같은 `revision`으로 현재 상태를 돌려준다 |
| GET | `/m1/sessions/{id}?since=<rev>&wait=<0..10>` | | `MobileSessionDetail` — 세션 단위 `revision`으로 같은 방식 |
| POST | `/m1/sessions/{id}/submit` | `{ text }` | 202 `{ protocol, accepted: "started" \| "steered" \| "queued" }` · 실행할 수 없으면 409 |
| POST | `/m1/sessions/{id}/stop` | | `{ protocol, stopped: true }` |
| POST | `/m1/sessions/{id}/permission` | `{ requestId, runId, allow }` | `{ protocol, ok: true }` |
| POST | `/m1/sessions/{id}/answers` | `{ requestId, runId, answers: { "<질문 문장>": { selectedOptions, customText? } } }` | `{ protocol, ok: true }` |
| POST | `/m1/workspaces/{id}/sessions` | `{ kind: "claude" \| "shell", provider?: "claude" \| "codex" \| "gemini" }` | 201 `{ protocol, sessionId }` |

```
MobileState {
  protocol: 1, revision: number, hostName: string,
  workspaces: [{ id, name, path, remote: boolean }],
  sessions: [MobileSessionSummary]
}
MobileSessionSummary {
  id, workspaceId, title, kind: "claude" | "shell", provider: "claude" | "codex" | "gemini", model,
  status: "idle" | "running" | "completed" | "error" | "stopped",
  revision: number, updatedAt: ISO-8601,
  preview?: { kind, text },            // 마지막 기록 항목 (200자)
  pendingPermissions: number, pendingQuestions: number, queued: number,
  resumeId?: string, terminal: boolean // terminal: 앱 안 로컬 터미널 창이라 모바일에서 명령 불가
}
MobileSessionDetail {
  protocol: 1, revision: number, session: MobileSessionSummary,
  entries: [LogEntry],                 // 최근 80개. id는 안정적이며 스트리밍 중 같은 id의 text가 갱신된다
  permissions: [MobilePermission],     // pending 상태만
  queued: [{ id, text }],
  usage?: { model?, contextUsedTokens?, contextWindowTokens?, contextPercent?, totalTokens?, costUSD? },
  elapsedSeconds?: number
}
LogEntry { id, kind: "user" | "assistant" | "system" | "output" | "error", text, timestamp, provider?,
           activity?: { id, kind, state, summary, toolName?, output? } }
MobilePermission {
  id, runId, toolName, title, headline?, fields: [{ label, value }], summary, canAllow,
  questionnaire?: { questions: [{ header, question, multiSelect, options: [{ label, description }] }] }
}
```

페어링 문자열(QR 내용): `mightyclaude://pair?v=1&host=<tailscale-ip>&port=<port>&key=<key>&name=<percent-encoded 호스트 이름>`.

제한: 요청 본문 64 KiB, `text` 32 KiB, 세션당 대기열 16개, 롱폴 `wait` 최대 10초, 피어당 초당 30요청, 연결 피어는 Tailscale 대역(100.64/10, fd7a:115c:a1e0::/48)만.

## 데스크톱 구현

- `MightyCore/Remote/MobileRemoteService.swift`: 리스너·키·리비전 버스·롱폴 대기. 키는 `<데이터 폴더>/mobile-remote.key`(소유자만 읽기)에 저장한다. 설정은 `AppSnapshot.mobileRemote { enabled, port }`.
- `MightyClaude/AppStore+MobileRemote.swift`: 스냅샷·권한·대기열 변화를 리비전으로 바꾸고 명령을 실제 창에 적용하는 브리지.
- 설정 화면의 **모바일 리모트** 절: 스위치, 포트, QR 코드, 키 다시 만들기.

## 모바일 앱

`mobile/README.md`를 참고한다. Expo Go에서 `npx expo start`로 실행하며, 화면은 페어링(QR/수동) → 호스트 → 워크스페이스·세션 목록 → 세션 상세(대화, 권한·질문 카드, 입력창, 중지)다.
