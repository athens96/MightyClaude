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

## m1 확장 (capabilities)

기존 라우트와 필드는 그대로다. 아래는 모두 **추가**이며, 새 필드는 전부 선택(optional)이다. 업데이트하지 않은 휴대폰 앱은 모르는 필드를 무시하고 지금처럼 동작한다. 휴대폰은 `/m1/info`의 `capabilities`에 이름이 있을 때만 해당 기능을 보여 준다(없으면 구버전 호스트).

`MobileInfo.capabilities: string[]` — 허용 값: `"submit-mode"`, `"queue"`, `"pane"`, `"history"`, `"settings"`, `"commands"`, `"mighty"`, `"status"`, `"attachments"`.

공통 오류: 알 수 없는 실행 창·항목 404, 형식 오류·허용 값 밖의 문자열·필수 필드 누락 400, 지금 상태에서 할 수 없음 409, 크기 초과 413. 허용 값 밖의 값을 조용히 기본값으로 바꾸지 않는다.

### 고정 문자열 값

| 필드 | 허용 값 |
|---|---|
| `status` | `idle` `running` `completed` `error` `stopped` |
| `LogEntry.kind` | `user` `assistant` `system` `output` `error` |
| `activity.state` | `running` `waiting` `completed` `error` `stopped` |
| `permissionMode` | 호스트가 `settings.options.permissionModes`로 알려 주는 id (프로바이더마다 다름) |
| `agentViewMode` | `plain` `mighty` |
| `mightyStyle` | `cli` `ouroboros` `paperthin` |
| 블록 `kind` | `main` `agent` `task` `steer` `compact` `question` |
| 블록 `status` | `running` `waiting` `completed` `error` `stopped` |
| `accepted` | `started` `steered` `queued` |
| `mode` | `steer` `queue` |
| 명령 `source` | `app` `builtin` `project` `user` `plugin` |
| 명령 `action` | `model` `permission` `clear` `usage` `help` `rename` |

### 라우트

| 메서드 | 경로 | 요청 | 응답 |
|---|---|---|---|
| POST | `/m1/sessions/{id}/submit` | `{ text, mode?, attachments?: [uploadId] }` | 202 `{ protocol, accepted }` — `accepted`는 **실제로 일어난 일**이다. `mode: "queue"`면 조정하지 않고 대기열에 넣는다. `mode: "steer"`(또는 생략)면 조정을 시도하고, 조정할 수 없는 창(로컬 Claude가 아님, 턴이 이미 닫힘)이면 대기열로 가며 `queued`를 돌려준다. 실행 중이 아니면 `mode`와 상관없이 바로 시작하고 `started`를 돌려준다. 대기열이 가득 차면 409 |
| POST | `/m1/sessions/{id}/queue/{itemId}/remove` | | `{ protocol, ok }` · 없는 항목 404 |
| POST | `/m1/sessions/{id}/queue/run-next` | | `{ protocol, ok }` · 실행 중이거나 대기열이 비면 409 |
| POST | `/m1/sessions/{id}/rename` | `{ title }` (앞뒤 공백 제거 후 1~80자) | `{ protocol, ok }` |
| POST | `/m1/sessions/{id}/close` | | `{ protocol, ok }` — 실행 중이면 Mac에서 닫을 때와 같이 중지 후 닫는다 |
| GET | `/m1/sessions/{id}/entries?before=<entryId>&limit=<1..100>` | | `{ protocol, entries: [LogEntry], hasMore }` — `before`보다 오래된 항목을 시간순으로. `before`가 호스트 기록에 없으면(밀려남) 가장 오래된 쪽부터가 아니라 **빈 배열과 `hasMore: false`** |
| POST | `/m1/sessions/{id}/settings` | `{ model?, permissionMode?, effort?, agentViewMode?, mightyStyle? }` (하나 이상) | `{ protocol, ok }` · 실행 중이면 409 · 옵션에 없는 값 400 |
| GET | `/m1/sessions/{id}/commands` | | `{ protocol, commands: [MobileCommand] }` |
| POST | `/m1/sessions/{id}/command` | `{ action }` (`clear` `usage` `help`만) | `{ protocol, ok, message? }` — `usage`·`help`는 `message`에 본문 |
| POST | `/m1/sessions/{id}/guided` | `{ style: "ouroboros" \| "paperthin", skill, text? }` | 202 `{ protocol, accepted }` — 호스트가 Mac과 같은 함수로 프롬프트를 만든다(`/ouroboros:seed`, `/re0 docs/spec.md`). 모르는 스킬 400, 그 스타일이 아닌 창 409 |
| POST | `/m1/sessions/{id}/uploads` | `{ name, size, mimeType? }` | 201 `{ protocol, uploadId, chunkSize }` · 크기·개수 한도 초과 413 · 열어 둔 업로드가 너무 많으면 429(실행 창당 16개, 호스트 전체 64개) |
| POST | `/m1/uploads/{uploadId}/chunks/{index}` | `{ dataBase64 }` | `{ protocol, ok, received }` — 순서대로 0부터. 이 라우트만 본문 한도 300 KiB |
| POST | `/m1/uploads/{uploadId}/complete` | | `{ protocol, attachment: { id, name, size } }` · 크기가 선언과 다르면 400 |
| POST | `/m1/uploads/{uploadId}/cancel` | | `{ protocol, ok }` |

`POST /m1/workspaces/{id}/sessions`는 **로컬** 워크스페이스에서 `kind: "shell"`을 409로 거절한다(휴대폰에서 쓸 수 없는 창이 되기 때문).

첨부 한도는 Mac과 같다: 요청당 파일 8개, 개당 5 MB, 합계 8 MB. `chunkSize`는 196 608바이트(192 KiB). 끝내지 않은 업로드는 10분 뒤 지운다. `submit`의 `attachments`는 `complete`를 마친, **같은 실행 창·같은 기기**의 `uploadId`만 받고(아니면 400), 한 번 쓰면 사라진다. 같은 업로드를 두 요청이 동시에 쓰면 하나만 성공한다. 전송이 실패하면 업로드는 그대로 남으므로 휴대폰이 `cancel`로 정리한다. 다른 기기의 `uploadId`로 `chunks`·`complete`·`cancel`을 부르면 404다. 실행 창을 닫거나 기기를 해제하면 그 업로드는 바로 지운다. 파일 이름은 경로·앞쪽 점·제어 문자·보이지 않는 방향 제어 문자를 없앤 120자 이내의 이름만 남긴다. 첨부가 있는 요청은 조정(steer)되지 않는다.

### 추가 필드

```
MobileInfo            { …, capabilities?: string[] }
MobileSessionSummary  { …, agentViewMode?: "plain" | "mighty", mightyStyle?: "cli" | "ouroboros" | "paperthin" }
MobileSessionDetail   { …, hasOlder?: boolean,
                        settings?: MobileSettings, mighty?: MobileMighty,
                        statusLine?: { lines: [[{ text, fg?: "#RRGGBB", bold?: boolean }]] },   // 최대 6줄
                        rateLimits?: [{ label, usedPercent, resetsAt?: ISO-8601 }] }
MobileSettings {
  editable: boolean,                         // 실행 중이면 false
  model, permissionMode, effort?, agentViewMode, mightyStyle,
  options: { models: [Option], permissionModes: [Option], efforts: [Option], mightyStyles: [Option] }   // Option { id, label }
}                                            // mightyStyles는 이 창에서 쓸 수 있는 것만(로컬 Claude가 아니면 cli 하나)
MobileCommand { name, description, source, argumentHint?, action? }
                                             // action이 있으면 휴대폰이 직접 처리: model·permission → 설정 선택, rename → 이름 변경,
                                             // clear·usage·help → /command. action이 없으면 입력창에 "/name "을 넣는다.
                                             // Mac 화면을 여는 명령(/plugin, /config)은 목록에 넣지 않는다.
MobileMighty {
  style: "cli" | "ouroboros" | "paperthin",
  runs: [{ id, input, title?, status, blocks: [MobileBlock] }],          // 최근 20개 요청, 시간순
  ouroboros?: { phase, ready: boolean, takesText: [skill], next: [{ skill, title, help }], all: [{ skill, title, help }] },
  paperthin?: { installed: boolean, recommended?: skill,
                domains: [{ id, title, axis, question, skills: [{ name, emoji, summary, scope, userInvoked, readOnly }] }],
                casebook?: { name, weight: "full" | "lightweight", files: [string] } }
}
MobileBlock { id, kind, title, status, summary?, output?, durationMs? }   // output은 2 000자까지. durationMs는 끝난 블록만(첫 기록~마지막 기록)
                                             // 호스트는 요청당 블록 수를 제한하지 않는다. 휴대폰은 최근 200개만 그리고 나머지는 "이전 블록 N개 생략"으로 표시한다.
```

`activity.durationMs`와 `activity.provider`는 이미 전송되고 있다(도구 소요 시간 표시에 쓴다).

### 휴대폰에서 의도적으로 제외한 Mac 기능

알림(푸시·로컬), 터미널 실행 창 조작, 워크스페이스 추가·이름 변경·제거, 그래프 배치·블록 크기 조절·참조 말풍선, CLI 계정 전환·CLI 업데이트·앱 자체 업데이트·앱 설정, 펫, 다국어.

### 기기 관리

휴대폰은 처음 페어링할 때 페어링 키로 인증하고, 호스트가 발급한 **기기 토큰**을 보안 저장소에 넣은 뒤부터는 토큰으로 인증한다. 프레임·거절 사유·등록 제한은 [relay.md](relay.md)의 "기기 토큰" 절을 따른다. Mac 설정의 **모바일 리모트**에 기기 목록이 나오고, 한 대를 해제하면 페어링 키가 먼저 새로 만들어진 뒤 그 기기의 토큰이 무효가 되어 즉시 끊긴다(해제된 휴대폰이 옛 QR로 다시 페어링하지 못하게). 토큰을 받은 다른 기기는 그대로 접속한다. 토큰을 모르는 구버전 앱은 페어링 키로만 인증하므로 "구버전 앱"으로 묶여 보이고, 키가 바뀌면 다시 페어링해야 한다. 기기 토큰은 관리 기능이지 QR 유출에 대한 방어가 아니다.

`guided`의 `text`는 두 스타일 모두 줄바꿈을 공백으로 접어 한 줄로 보낸다.

## 데스크톱 구현

- `MightyCore/Remote/RelayChannel.swift`: X25519·HKDF·ChaCha20-Poly1305 채널, 호스트 키쌍 파일, 페어링 오퍼.
- `MightyCore/Remote/MobileRemoteService.swift`: 릴레이 제어 소켓과 재접속, 휴대폰별 데이터 소켓(핸드셰이크·인증·요청 처리), m1 라우팅, 리비전 대기·알림. 페어링 키는 `<데이터 폴더>/mobile-remote/mobile-remote.key`, 키쌍은 `relay-keypair.json`(모두 소유자만 읽기).
- `MightyClaude/AppStore+MobileRemote.swift`: 스냅샷·권한·대기열 변화를 리비전으로 바꾸고 명령을 실제 창에 적용하는 브리지.
- 설정 화면의 **모바일 리모트** 절: 스위치, 릴레이 주소, 연결 상태, QR 코드, 키 다시 만들기, 구버전 앱 허용 스위치, 페어링된 기기 목록(이름·처음/마지막 접속·연결 중·새 기기 표시)과 기기별 해제.
- `MightyCore/Remote/MobileRemoteSupport.swift`: 확장의 순수 규칙(이름·페이지·설정 검증, 상태줄·사용량 변환, 명령 매핑, 마이티 블록 투영과 리비전 요약, 안내형 프롬프트).
- `MightyCore/Remote/MobileUploadStore.swift`: 첨부 업로드 저장소(0700 폴더·0600 파일, 순서·크기 검증, 기기·실행 창 범위, 단일 사용, 10분 만료).
- `MightyCore/Remote/MobileDeviceRegistry.swift`: 기기 토큰 등록부와 인증 판정(`devices.json`).

## 모바일 앱

`mobile/README.md`를 참고한다. 화면은 페어링(QR/링크 붙여넣기) → 호스트 → 워크스페이스·세션 목록 → 세션 상세(대화, 권한·질문 카드, 입력창, 중지)다.
