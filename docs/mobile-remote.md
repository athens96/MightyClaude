# 모바일 리모트 (iOS · Android)

PC에서 실행 중인 MightyClaude에 휴대폰으로 접속해 워크스페이스와 에이전트(실행 창)를 보고, 요청을 보내고, 권한·질문에 답하는 기능이다. 모바일 앱은 `mobile/`의 Expo(React Native) 프로젝트이고, 호스트는 데스크톱 앱 안에 있다. Mac과 휴대폰이 각각 **릴레이 서버**에 바깥으로 접속해 연결되며, 모든 내용은 종단 간 암호화된다(상세: [relay.md](relay.md)). 포트 개방·VPN·Tailscale이 필요 없다.

## 동작 개요

1. 릴레이 서버를 하나 띄운다(`relay/`, Docker 이미지 제공). 집·회사 어디서든 접속하려면 공개 주소(`wss://…`)가 있어야 하고, 같은 Wi‑Fi에서만 쓸 때는 Mac에서 `npm start`로 띄운 `ws://<Mac IP>:8787`로 충분하다.
2. Mac의 **설정 → 모바일 리모트**에 릴레이 주소를 넣고 스위치를 켠다. 앱이 릴레이에 접속하면 QR 코드가 나타난다. QR에는 호스트 ID, 호스트 공개키, 릴레이 주소, 페어링 키가 들어 있다. 스위치 상태·릴레이 주소·키는 저장되어 앱을 다시 켜도 유지되며, 릴레이가 끊기면 자동으로 재접속한다.
3. 휴대폰 앱에서 QR을 스캔하거나 페어링 링크를 붙여 넣는다. 앱은 릴레이를 통해 Mac과 X25519 키 교환을 하고, 페어링 키로 인증한 뒤 키를 보안 저장소에 남긴다.
4. 이후 앱은 암호화된 채널로 `/m1/*` 요청을 보내고, Mac은 변화가 있을 때 `notify`를 보내 즉시 갱신하게 한다.
5. 요청 전송은 데스크톱의 입력창과 같은 규칙을 따른다. 실행 중인 로컬 Claude 창이면 진행 중인 턴에 바로 전달되고, 그 밖에는 대기열에 들어간다. 중지, 권한 허용·거부, 질문 답변, 새 실행 창 만들기도 가능하다.

기존 **원격 워크스페이스**(데스크톱↔데스크톱, `/v1/*`, Tailscale)는 별개 기능이다.

## 프로토콜 m1

요청은 릴레이 암호화 채널 위에서 `{id, method, path, body}` → `{id, status, body}`로 오간다(relay.md). `path`는 아래 라우트와 질의를 그대로 담는다. 성공 응답 본문에는 `"protocol": 1`이 있고, 오류는 `{ "protocol": 1, "error": "..." }`와 상태(400·404·409·413·429·503)다.

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

페어링 문자열(QR 내용)은 relay.md의 v2 형식이다.

제한: 요청 본문 64 KiB, `text` 32 KiB, 세션당 대기열 16개, 롱폴 `wait` 최대 10초, 연결당 동시 요청 8개, 호스트당 휴대폰 32대.

## 데스크톱 구현

- `MightyCore/Remote/RelayChannel.swift`: X25519·HKDF·ChaCha20-Poly1305 채널, 호스트 키쌍 파일, 페어링 오퍼.
- `MightyCore/Remote/MobileRemoteService.swift`: 릴레이 제어 소켓과 재접속, 휴대폰별 데이터 소켓(핸드셰이크·인증·요청 처리), m1 라우팅, 리비전 대기·알림. 페어링 키는 `<데이터 폴더>/mobile-remote/mobile-remote.key`, 키쌍은 `relay-keypair.json`(모두 소유자만 읽기).
- `MightyClaude/AppStore+MobileRemote.swift`: 스냅샷·권한·대기열 변화를 리비전으로 바꾸고 명령을 실제 창에 적용하는 브리지.
- 설정 화면의 **모바일 리모트** 절: 스위치, 릴레이 주소, 연결 상태, QR 코드, 키 다시 만들기.

## 모바일 앱

`mobile/README.md`를 참고한다. 화면은 페어링(QR/링크 붙여넣기) → 호스트 → 워크스페이스·세션 목록 → 세션 상세(대화, 권한·질문 카드, 입력창, 중지)다.
