# 모바일 리모트 (iOS · Android)

PC에서 실행 중인 Mighty Claude에 휴대폰으로 접속해 워크스페이스와 에이전트(실행 창)를 보고, 요청을 보내고, 권한·질문에 답하는 기능이다. 모바일 앱은 `mobile/`의 Expo(React Native) 프로젝트이고, 호스트는 데스크톱 앱 안에 있다. Mac과 휴대폰이 각각 **릴레이 서버**에 바깥으로 접속해 연결되며, 모든 내용은 종단 간 암호화된다(상세: [relay.md](relay.md)). 포트 개방이나 VPN이 필요 없다. 휴대폰과 Mac이 같은 Tailscale 네트워크에 있으면 휴대폰은 릴레이를 거치지 않고 Mac에 **직접** 붙는다(같은 암호화, 아래 "Tailscale 직접 연결"). Mac과 Windows 호스트가 직접 연결을 연다.

## 동작 개요

1. 릴레이 서버를 하나 띄운다(`relay/`, Docker 이미지 제공). 집·회사 어디서든 접속하려면 공개 주소(`wss://…`)가 있어야 하고, 같은 Wi‑Fi에서만 쓸 때는 Mac에서 `npm start`로 띄운 `ws://<Mac IP>:8787`로 충분하다.
2. Mac의 **설정 → 모바일 리모트**에 릴레이 주소를 넣고 스위치를 켠다. 앱이 릴레이에 접속하면 QR 코드가 나타난다. QR에는 호스트 ID, 호스트 공개키, 릴레이 주소, 페어링 키가 들어 있다. 스위치 상태·릴레이 주소·키는 저장되어 앱을 다시 켜도 유지되며, 릴레이가 끊기면 자동으로 재접속한다.
3. 휴대폰 앱에서 QR을 스캔하거나 페어링 링크를 붙여 넣는다. 앱은 릴레이를 통해 Mac과 X25519 키 교환을 하고, 페어링 키로 인증한 뒤 키를 보안 저장소에 남긴다.
4. 이후 앱은 암호화된 채널로 `/m1/*` 요청을 보내고, Mac은 변화가 있을 때 `notify`를 보내 즉시 갱신하게 한다.
5. 요청 전송은 데스크톱의 입력창과 같은 규칙을 따른다. 실행 중인 로컬 Claude 창이면 진행 중인 턴에 바로 전달되고, 그 밖에는 대기열에 들어간다. 중지, 권한 허용·거부, 질문 답변, 새 실행 창 만들기도 가능하다.

## Tailscale 직접 연결

릴레이 대신 휴대폰이 호스트(Mac·Windows PC)에 바로 WebSocket을 여는 길이다. 바뀌는 것은 소켓을 어디에 여는가뿐이고, 그 위의 `hello`/`ready` 핸드셰이크·ChaCha20-Poly1305 프레임·인증(페어링 키·기기 토큰)·m1 터널은 릴레이 경로와 한 글자도 다르지 않다. 와이어와 제한은 [relay.md](relay.md)의 "Tailscale 직접 연결" 절.

- **Mac**: **설정 → 모바일 리모트**의 "Tailscale로 직접 연결" 스위치(기본 켜짐)와 포트(기본 `43139`, 1024–65535). 모바일 리모트가 켜져 있고 이 스위치도 켜져 있을 때만, Mac의 Tailscale 주소에서만 연결을 받는다. Tailscale 인터페이스는 fd7a:115c:a1e0::/48 주소를 가진 `utun`이고(그래야 같은 100.64.0.0/10을 쓰는 WARP 같은 VPN이 빠진다), 그 인터페이스의 두 범위 주소에만 그 인터페이스로 고정해 연다. 0.0.0.0·LAN·공인 주소에는 열지 않고, 상대 주소가 두 범위 밖이면 받지 않는다. 주소는 네트워크가 바뀔 때와 15초마다 다시 읽는다. 스위치 아래 줄은 대기 중인 `주소:포트` 목록, "Tailscale을 찾지 못함", 또는 열지 못한 이유를 보여 준다. 릴레이가 끊겨 있어도 직접 연결이 열려 있으면 QR이 나온다. 연결 수·상대 주소별 한도와 인증 실패 제한은 relay.md에 있다.
- **Windows**: 같은 스위치(기본 켜짐)·포트(기본 `43139`)·상태 줄에 **방화벽 규칙** 줄이 더 있다. Tailscale 어댑터는 fd7a:115c:a1e0::/48 주소를 가진 어댑터이고(이름은 보지 않는다), 그 어댑터의 두 범위 주소에만 주소마다 하나씩 `TcpListener`로 연다. 한도·거절·끝점 알리기는 Mac과 같고, MagicDNS 이름은 싣지 않는다. Windows 방화벽은 줄의 "규칙 추가" 버튼을 누를 때 UAC 확인을 거쳐 규칙 하나(이 앱만, 모든 프로필, TCP, 이 포트, 상대와 닿는 로컬 주소 둘 다 Tailscale 범위)를 더하고(이미 있으면 그 자리에서 고친다), "규칙 지우기" 버튼이나 규칙이 있는 채 원격·직접 연결을 끄는 클릭이 지운다. 규칙의 상태를 읽지 못하면 버튼이 "다시 확인"이 되어 권한을 묻지 않고 다시 읽는다. 자세한 규칙은 [relay.md](relay.md)의 "Windows 리스너"와 [windows-mobile-remote.md](windows-mobile-remote.md).
- **페어링**: QR에 선택 파라미터 `direct=`(쉼표로 구분한 `host:port`, 퍼센트 인코딩)가 붙는다. Tailscale CLI가 있으면 `tailscale status --json`의 MagicDNS 이름(`….ts.net`)도 함께 싣는다. 이 파라미터를 모르는 구버전 앱은 무시하고 릴레이로 붙는다.
- **이미 페어링한 휴대폰**: 호스트는 `"direct"` 기능을 광고하고, `auth_ok`와 `GET /m1/info`에 지금의 끝점 목록 `direct: string[]`을 싣는다(직접 연결이 꺼져 있거나 Tailscale이 없으면 빈 배열). 휴대폰은 `"direct"` 기능을 광고한 호스트의 목록만 받아 저장된 목록을 바꾼다. 그래서 QR 없이도 접속할 때마다 최신 주소를 알고, Mac이 직접 연결을 끄면 시도를 멈춘다.
- **휴대폰**: 접속할 때마다 저장된 끝점 가운데 **주소만** `ws://<끝점>/ws?v=1`(서버 id·연결 id 없음)로 **모두 동시에** 열어 1.8초 안에 먼저 열린 것을 쓰고, 하나도 열리지 않거나 모두 바로 거절되면 그 즉시 릴레이로 간다. MagicDNS 이름은 저장만 하고 열지 않는다(휴대폰에 Tailscale 경로가 없으면 공개 DNS에 묻게 되므로). 열린 직접 소켓이 3초 안에 `auth_ok`까지 가지 못하면 — 다른 기기가 그 주소를 써서 공개키가 다름, 핸드셰이크 지연, Mac이 받지 않음, Mac의 일반 거절(`unauthorized`) — 페어링을 지우지 않고 곧바로 릴레이로 가서, 해제·키 교체 같은 진짜 이유는 릴레이에서 듣는다. 직접 연결이 끊기면 평소의 재접속 간격(1.5초부터 2배씩 최대 30초)을 지키며 같은 순서로 다시 붙는다. 호스트 카드에는 연결된 경로가 "Tailscale 직접" 또는 "릴레이" 배지로 보인다.
- 휴대폰은 Tailscale 범위(100.64.0.0/10, fd7a:115c:a1e0::/48, `*.ts.net`) 밖의 끝점은 QR에서든 호스트에서든 받지 않는다.

### 휴대폰의 평문 `ws://`

직접 연결은 `ws://100.x.y.z:43139/ws?v=1`처럼 TLS 없는 WebSocket이다. 내용은 종단 간 암호화되어 있고 공개키 확인으로 상대가 페어링한 Mac임을 확인하므로 TLS가 더하는 것은 없다. 플랫폼이 이 평문 소켓을 막는지는 이렇게 확인했다. `app.json`은 바꾸지 않았다.

- **Android(릴리스 빌드)**: React Native의 WebSocket은 OkHttp이고, OkHttp는 `ws://`를 평문 HTTP로 보고 `NetworkSecurityPolicy.isCleartextTrafficPermitted(host)`를 따른다. Expo 템플릿의 `main/AndroidManifest.xml`에는 `usesCleartextTraffic`이 없어(디버그 매니페스트에만 `true`) 릴리스에서는 기본값(대상 SDK 28 이상이면 거부)이 되지만, 이 앱은 `expo-build-properties`의 `android.usesCleartextTraffic: true`로 **메인** 매니페스트의 `<application>`에 `android:usesCleartextTraffic="true"`를 쓴다(플러그인 `withAndroidCleartextTraffic`, 로컬 prebuild의 `android/app/src/main/AndroidManifest.xml`에서도 확인). 어떤 플러그인도 `android:networkSecurityConfig`를 넣지 않으므로 이 값이 모든 호스트에 그대로 적용되고, 릴리스 빌드도 100.x 주소로의 `ws://`를 허용한다. 더 좁힐 수는 없다: 네트워크 보안 설정의 `domain-config`는 이름과 단일 IP만 받고 100.64.0.0/10 같은 범위는 받지 않으며, 같은 Wi‑Fi의 `ws://` 릴레이도 이 허용에 기대고 있다. 기기에서 직접 확인한 것은 아니다.
- **iOS**: `NSAllowsLocalNetworking`만 그대로 둔다. React Native의 iOS WebSocket은 SocketRocket(`SRWebSocket`, React-Core의 의존성)이고, SocketRocket은 URLSession이 아니라 CFStream/NSStream 소켓으로 연결한다. ATS는 URLSession과 그 위에 놓인 API에만 걸리므로 이 소켓은 ATS의 대상이 아닐 가능성이 높다. 하지만 기기에서 확인하지 못했으므로 확실하다고 할 수 없다. 앞서 넣었던 `NSExceptionDomains`(100.64.0.0/10, fd7a:115c:a1e0::/48, `ts.net`)는 뺐다: CIDR 키는 URLSession에만 의미가 있어 여기서는 효과가 없고, `ts.net`은 앱 전체의 평문을 넓힌다. 지금 배포하는 휴대폰 앱은 Android뿐이다. iOS 빌드를 내기 전에 기기에서 100.x 주소로 직접 연결되는지 확인해야 한다.

## 프로토콜 m1

요청은 릴레이 암호화 채널 위에서 `{id, method, path, body}` → `{id, status, body}`로 오간다(relay.md). `path`는 아래 라우트와 질의를 그대로 담는다. 성공 응답 본문에는 `"protocol": 1`이 있고, 오류는 `{ "protocol": 1, "error": "..." }`와 상태(400·404·409·413·429·503)다.

| 메서드 | 경로 | 요청 | 응답 |
|---|---|---|---|
| GET | `/m1/info` | | `{ protocol, hostId, hostName, appVersion, platform }` |
| GET | `/m1/state?since=<rev>&wait=<0..10>` | | `MobileState` — `revision`이 `since`보다 커질 때까지 최대 `wait`초 기다렸다가 응답. 시간 안에 변화가 없으면 같은 `revision`으로 현재 상태를 돌려준다 |
| GET | `/m1/sessions/{id}?since=<rev>&wait=<0..10>[&structure=<tag>]` | | `MobileSessionDetail` — 세션 단위 `revision`으로 같은 방식. `structure`는 `"structure"` 기능이 있는 호스트(Mac·Windows)에만(아래 "구조 보기" 절) |
| POST | `/m1/sessions/{id}/submit` | `{ text }` | 202 `{ protocol, accepted: "started" \| "steered" \| "queued" }` · 실행할 수 없으면 409 |
| POST | `/m1/sessions/{id}/stop` | | `{ protocol, stopped }` — 실행 중인 요청이 없어 멈출 것이 없었으면 `stopped: false` |
| POST | `/m1/sessions/{id}/permission` | `{ requestId, runId, allow, always? }` | `{ protocol, ok: true }` — `always`는 Antigravity 권한 카드에만(아래 "Antigravity 권한 카드" 절) |
| POST | `/m1/sessions/{id}/answers` | `{ requestId, runId, answers: { "<질문 문장>": { selectedOptions, customText? } } }` | `{ protocol, ok: true }` |
| POST | `/m1/sessions/{id}/plan` | `{ requestId, runId, decision: "approveAutoEdit" \| "approveConfirmEach" \| "revise" \| "cancel", feedback? }` | `{ protocol, ok: true }` — Claude 계획(ExitPlanMode) 카드의 네 답. `feedback`은 `revise`에만 쓰며 앞뒤 공백을 뺀 뒤 비어 있거나 16 KiB를 넘으면 400(`plan.error.emptyFeedback` / `plan.error.feedbackTooLong` 문구). 모르는 `decision` 400(`plan.error.invalidDecision`), 형식이 틀린 `requestId`·`runId` 400(`plan.error.invalidRequest`), 이미 끝났거나 계획이 아닌 요청 409 |
| POST | `/m1/workspaces/{id}/sessions` | `{ kind: "claude" \| "shell", provider?: "claude" \| "codex" \| "gemini" }` | 201 `{ protocol, sessionId }` |

```
MobileState {
  protocol: 1, revision: number, hostName: string,
  workspaces: [{ id, name, path }],
  sessions: [MobileSessionSummary]
}
MobileSessionSummary {
  id, workspaceId, title, kind: "claude" | "shell" | "browser" | "agent-terminal" | "agent-browser",
  provider: "claude" | "codex" | "gemini", model,
  status: "idle" | "running" | "completed" | "error" | "stopped",
  revision: number, updatedAt: ISO-8601,
  preview?: { kind, text },            // 마지막 기록 항목 (200자)
  pendingPermissions: number, pendingQuestions: number, queued: number,
  resumeId?: string, terminal: boolean, // terminal: 앱 안 로컬 터미널 창이라 모바일에서 명령 불가
  titleMode?: "auto" | "fixed",        // "auto"=요청 따름, "fixed"=직접 지정; 없으면 "auto"로 처리
  resolvedModel?: string,              // model이 지금 가리키는 전체 id(opus → claude-opus-5-5); 모르면 없음
  canSteer?: boolean,                  // 실행 중에 보낸 글이 진행 중인 턴에 합류(조정)할 수 있는지. Claude 에이전트 창만 true
  parentSessionId?: string,            // 마이티 오케스트레이션 자식 창의 부모 창 id(Mac 호스트가 싣는다. Windows 호스트는 아직 싣지 않는다); 자식이 아니면 없음
  childState?: string                  // 그 자식의 상태(Mac 사이드바 자식 행과 같은 ChildState 값); parentSessionId와 함께만
}
```

`canSteer`(선택)는 호스트가 그 창에서 조정을 할 수 있는지를 창마다 알려 준다. 값은 호스트 자신의 데스크톱 입력창과 같은 규칙에서 나온다(Mac `DelegationProviders.canSteer` → `MobileRemoteSupport.canSteer`, Windows `DelegationProviders.CanSteer`): Claude 에이전트 창(`kind: "claude"`, `provider: "claude"`)만 `true`이고, Codex·Antigravity(`provider: "gemini"`) 창과 명령 창은 `false`다 — 이 창들에는 실행 중에 보낸 글이 언제나 대기열로 간다. 휴대폰은 `false`인 창에서 실행 중 "바로 전달" 버튼을 숨기고 대기열 추가만 보인다(Mac 입력창이 이 창들에서 조정을 내놓지 않는 것과 같다). `canSteer: true`는 약속이 아니다: 호스트는 실행이 막 시작되는 중이거나, 업데이트가 실행을 붙잡고 있거나, 계획 모드 스타일이 백그라운드 작업만 기다리는 중이면 여전히 대기열에 넣고 `queued`로 답한다. 구버전 휴대폰은 이 필드를 무시하고, 필드가 없는 구버전 호스트에서는 휴대폰이 지금처럼 조정을 내놓는다(호스트가 조정할 수 없으면 `queued`로 답한다).

마이티 오케스트레이션 자식 창은 Mac 호스트에서는 Mac의 위임 파일이 자식으로 적어 둔 창만 `parentSessionId`·`childState`를 싣는다(`MobileRemoteSupport.childLink`). `childState`는 사이드바 자식 행이 보이는 그대로다(`creating`·`running`·`waiting`·`reported`·`merged`·`ended`·`interrupted`·`failed`·`closed`·`discarded`, 사람이 답해야 하는 요청을 기다리는 실행 중 자식은 `waiting`). 휴대폰은 같은 워크스페이스 목록에 부모 창이 있고 그 부모가 자식이 아니면 자식 행을 부모 바로 아래 한 단 들여 그리고 오른쪽에 상태 낱말을 보인다(`groupSessionsByWorkspace`·`childStateLabel`). 부모가 닫혔거나 목록에 없으면 보통 창처럼 둔다. 병합·버리기·되돌리기는 휴대폰에 없고 데스크톱 앱의 자식 카드에서만 한다. 지금 이 두 필드는 Mac 호스트만 싣는다: Windows 호스트의 `MobileDesktopRouter`는 아직 `parentSessionId`·`childState`를 보내지 않아서, Windows 호스트의 자식 창은 휴대폰 목록에서 평범한 창으로 보인다. 구버전 휴대폰은 두 필드를 무시하고, 구버전 Mac에서는 필드가 없어 목록이 지금처럼 평평하다.

모델 이름은 양쪽이 같은 규칙으로 버전까지 붙여 보여 준다(`claude-opus-5-5` → `Opus 5.5`, `gpt-5.2-codex` → `GPT-5.2 Codex`, `gemini-3-pro-preview` → `Gemini 3 Pro`). 규칙은 `MightyCore/ModelLabel.swift`와 `mobile/src/lib/model-label.ts`가 함께 지키고, 둘 다 `native/contracts/fixtures/model-labels.json`으로 검사한다. `resolvedModel`(선택)은 별칭(`opus`)에 버전을 붙일 근거다: 설치된 CLI 모델 목록의 `resolvedModel`, 목록에 없으면 같은 계열 별칭에 한해, 그 별칭이 지금처럼 선택돼 있던 동안 그 창이 보고한 모델. Mac(`MobileRemoteSupport.resolvedModel`)과 Windows(`ModelLabel.Resolution`) 호스트가 같은 규칙을 쓰므로 `default`의 Antigravity 창에는 이 필드가 없어 휴대폰은 `Antigravity 설정 따름`만 보이고, 지금 선택이 아닌 모델을 위해 보고된 모델은 보내지 않는다. `default`·`opusplan` 같은 다른 별칭은 제 이름 뒤에 ` · Opus 5.5`처럼 붙인다(휴대폰의 `default` 이름은 `provider.fallback.defaultLabel`, 예: `Claude 설정 따름`). 근거가 없으면 보내지 않고, 휴대폰은 버전 없이 `Opus`로 보여 준다(버전을 지어내지 않는다). 휴대폰은 이 값을 표시에만 쓰고, 설정 변경으로 보내는 값은 언제나 `model`·옵션 `id` 그대로다. 구버전 휴대폰은 이 필드를 무시하고, 구버전 호스트에서는 필드가 없으므로 `model`만으로 라벨을 만든다. 모델 선택지(`options.models[].label`)는 Mac이 같은 규칙으로 만든 라벨이다. `provider: "gemini"`는 전송·저장 id 그대로이고, 이 창에서 Google의 Antigravity CLI(`agy`)를 돌리는 호스트(`/m1/info`의 `platform`·`appVersion`으로 Mac `darwin` 0.4.23 이상, Windows `win32` 0.4.28 이상, 버전을 모르거나 읽을 수 없으면 이쪽으로 본다)면 휴대폰도 이름을 "Antigravity"(베타 배지, Gemini 마크 유지)로, 그보다 오래된 호스트면 "Gemini"로 보인다(`hostRunsAntigravity`). Antigravity를 돌리는 호스트의 창에서 agy 모델 이름은 Mac 모델 목록과 같게 읽는다: `gemini-4-argon-medium` → `Gemini 4 Argon (Medium)`, `claude-opus-5-5-high` → `Claude Opus 5.5 (High)`, `gpt-oss-120b-medium` → `GPT-OSS 120B (Medium)`(`antigravityModelLabel`; 클라이언트마다 따로 검사하고 공유 픽스처에는 없다).

에이전트 창이 소유한 창 두 가지도 같은 목록에 실린다: 전용 터미널 창(`kind: "agent-terminal"`,
id `agent-terminal:<에이전트 창 id>`)과 에이전트가 앱 안에서 연 브라우저 창(`kind: "agent-browser"`,
id `agent-browser:<에이전트 창 id>`). 둘 다 `terminal: true`로 실려서 휴대폰은 목록에만 올리고
명령은 보내지 않는다(내용 렌더링은 범위 밖). 에이전트 창이 닫히면 두 창도 목록에서 사라진다.
Mac의 읽기 전용 파일 창(`kind: "files"`, id `files:<워크스페이스 id>`)은 목록에 싣지 않으며, 그 id로 온 요청은
찾을 수 없는 창으로 처리한다([file-pane.md](file-pane.md)).

```
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
  questionnaire?: { questions: [{ header, question, multiSelect, options: [{ label, description }] }] },
  plan?: string,        // Claude 계획 승인(ExitPlanMode)의 Markdown. 있으면 title은 "계획", fields는 같은 계획 하나({ label: "계획", value: plan } — plan을 모르는 옛 폰용); 답은 POST …/plan
  receivedAt?: string,  // 계획 승인과 Antigravity 카드에만, 호스트가 요청을 받은(턴이 끝난) ISO 8601 시각
  antigravity?: { actions: [MobileAntigravityAction] }   // Antigravity 권한 카드에만(아래 절); 답은 POST …/permission
}
```

페어링 문자열(QR 내용)은 relay.md의 v2 형식이다(선택 파라미터 `direct=` 포함).

제한: 요청 본문 64 KiB, `text` 32 KiB, 요청 경로(질의 포함) 16 KiB, 세션당 대기열 16개, 롱폴 `wait` 최대 10초, 연결당 동시 요청 8개, 호스트당 휴대폰 32대. 경로가 16 KiB를 넘는 요청은 버리지 않고 414 `{ protocol: 1, error, code: "badPath" }`로 답한다(휴대폰이 제한 시간까지 기다리지 않도록). 16 KiB는 파일 라우트의 4,096바이트 경로를 퍼센트 인코딩한 최대 길이(3배)에 라우트를 더하고도 남는다.

## m1 확장 (capabilities)

기존 라우트와 필드는 그대로다. 아래는 모두 **추가**이며, 새 필드는 전부 선택(optional)이다. 업데이트하지 않은 휴대폰 앱은 모르는 필드를 무시하고 지금처럼 동작한다. 휴대폰은 `/m1/info`의 `capabilities`에 이름이 있을 때만 해당 기능을 보여 준다(없으면 구버전 호스트).

`MobileInfo.capabilities: string[]` — 허용 값: `"submit-mode"`, `"queue"`, `"pane"`, `"history"`, `"settings"`, `"commands"`, `"mighty"`, `"status"`, `"attachments"`, `"style"`, `"files"`, `"screenShare"`, `"structure"`, `"direct"`, `"push"`.

`"direct"`는 호스트가 Tailscale 직접 연결을 아는 빌드라는 뜻이다(Mac·Windows). 이 이름이 있으면 `MobileInfo.direct`와 `auth_ok.direct`가 그 순간의 끝점 목록이다(위 "Tailscale 직접 연결").

`"push"`는 호스트가 아래 "푸시 알림"의 세 라우트(`/m1/push/*`)를 알고 휴대폰에 푸시를 보낸다는 뜻이다(Mac·Windows). 이 이름이 없으면 휴대폰은 핸들을 만들지도 주지도 않는다.

`"screenShare"`는 BETA 화면 보기·조작 기능의 문이다. 세션 요청 라우트와 E2EE 시그널링 메시지 규약은 `docs/relay.md`의 "화면 공유 시그널링"에 있다.

공통 오류: 알 수 없는 실행 창·항목 404, 형식 오류·허용 값 밖의 문자열·필수 필드 누락 400, 지금 상태에서 할 수 없음 409, 크기 초과 413. 허용 값 밖의 값을 조용히 기본값으로 바꾸지 않는다.

### 고정 문자열 값

| 필드 | 허용 값 |
|---|---|
| `status` | `idle` `running` `completed` `error` `stopped` |
| `LogEntry.kind` | `user` `assistant` `system` `output` `error` |
| `activity.state` | `running` `waiting` `completed` `error` `stopped` |
| `permissionMode` | 호스트가 `settings.options.permissionModes`로 알려 주는 id (프로바이더마다 다름) |
| `agentViewMode` | `plain` `mighty` |
| `mightyStyle` | `cli` `ouroboros` — 둘 고정(v6까지는 `paperthin`도 있었고 v7에서 그 스타일과 함께 뺐다). `ouroboros`가 아닌 스타일(번들 클러드 플랜 포함)에는 언제나 `cli`가 실린다(진짜 값은 `styleId`, 아래 "스타일" 절) |
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
| POST | `/m1/sessions/{id}/rename` | `{ title }` (앞뒤 공백 제거 후 1~80자) 또는 `{ title: "", titleMode: "auto" }` | `{ protocol, ok }` — `titleMode: "auto"`이면 `title`을 무시하고 제목을 자동 모드로 되돌린다; 오래된 폰이 `title`만 보내면 고정 이름으로 처리 |
| POST | `/m1/sessions/{id}/close` | | `{ protocol, ok }` — 실행 중이면 Mac에서 닫을 때와 같이 중지 후 닫는다 |
| GET | `/m1/sessions/{id}/entries?before=<entryId>&limit=<1..100>` | | `{ protocol, entries: [LogEntry], hasMore }` — `before`보다 오래된 항목을 시간순으로. `before`가 호스트 기록에 없으면(밀려남) 가장 오래된 쪽부터가 아니라 **빈 배열과 `hasMore: false`** |
| POST | `/m1/sessions/{id}/settings` | `{ model?, permissionMode?, effort?, agentViewMode?, mightyStyle?, styleId? }` (하나 이상) | `{ protocol, ok }` · 실행 중이면 409 · 옵션에 없는 값 400 · `styleId`가 있으면 `mightyStyle`은 무시한다(아래 "스타일" 절) |
| GET | `/m1/sessions/{id}/commands` | | `{ protocol, commands: [MobileCommand] }` |
| POST | `/m1/sessions/{id}/command` | `{ action }` (`clear` `usage` `help`만) | `{ protocol, ok, message? }` — `usage`·`help`는 `message`에 본문 |
| POST | `/m1/sessions/{id}/guided` | 새 형식 `{ styleId, actionId, text? }` · 레거시 `{ style: "ouroboros", skill, text? }` | 202 `{ protocol, accepted }` — 호스트가 Mac과 같은 함수로 프롬프트를 만든다(`/ouroboros:seed`, `/re0 docs/spec.md`). 스타일을 모르거나 승인되지 않음 400, 이 창이 그 스타일이 아님 409, 행동(스킬)을 모름 400. 자세한 값은 아래 "스타일" 절 |
| POST | `/m1/sessions/{id}/uploads` | `{ name, size, mimeType? }` | 201 `{ protocol, uploadId, chunkSize }` · 크기·개수 한도 초과 413 · 열어 둔 업로드가 너무 많으면 429(실행 창당 16개, 호스트 전체 64개) |
| POST | `/m1/uploads/{uploadId}/chunks/{index}` | `{ dataBase64 }` | `{ protocol, ok, received }` — 순서대로 0부터. 이 라우트만 본문 한도 300 KiB |
| POST | `/m1/uploads/{uploadId}/complete` | | `{ protocol, attachment: { id, name, size } }` · 크기가 선언과 다르면 400 |
| POST | `/m1/uploads/{uploadId}/cancel` | | `{ protocol, ok }` |
| GET | `/m1/workspaces/{id}/files?path=<상대 경로>` | | `MobileFileListing` — 폴더 하나(`path` 없거나 빈 값이면 워크스페이스 루트). 아래 "파일" 절 |
| GET | `/m1/workspaces/{id}/file?path=<상대 경로>` | | `MobileFilePreview` — 파일 하나의 미리보기. 아래 "파일" 절 |
| POST | `/m1/push/register` | `{ handle, kinds: [string] }` | `{ protocol, ok }` — `handle`은 43자 base64url이 아니면 400(`remote.error.badPushHandle`), `kinds`가 없거나 배열이 아니면 400. 이 호스트가 모르는 종류 이름은 거절하지 않고 뺀다. 기기 토큰이 없는 기기(구버전 앱 묶음, 레거시 기기 줄)는 409(`remote.error.pushNeedsToken`). 기기 목록을 저장하지 못하면 503(`mobileRemote.device.saveFailed`). 기기는 본문이 아니라 인증된 연결로 정한다 — `push` 기능 |
| POST | `/m1/push/unregister` | `{}` | `{ protocol, ok }` — 그 기기의 핸들과 보고 있는 세션을 지운다. 저장하지 못하면 503 |
| POST | `/m1/push/viewing` | `{ sessionId: string \| null }` | `{ protocol, ok }` — 형식이 틀린 id는 404(`remote.error.paneNotFound`). `null`이거나 `sessionId`를 빼면 지운다(Mac·Windows 같음). 본문이 아예 없으면 Mac은 400(`remote.error.bodyRequired`), Windows는 지운다. 45초 동안 유효 |

`POST /m1/workspaces/{id}/sessions`는 **로컬** 워크스페이스에서 `kind: "shell"`을 409로 거절한다(휴대폰에서 쓸 수 없는 창이 되기 때문).

첨부 한도는 Mac과 같다: 요청당 파일 8개, 개당 5 MB, 합계 8 MB. `chunkSize`는 196 608바이트(192 KiB). 끝내지 않은 업로드는 10분 뒤 지운다. `submit`의 `attachments`는 `complete`를 마친, **같은 실행 창·같은 기기**의 `uploadId`만 받고(아니면 400), 한 번 쓰면 사라진다. 같은 업로드를 두 요청이 동시에 쓰면 하나만 성공한다. 전송이 실패하면 업로드는 그대로 남으므로 휴대폰이 `cancel`로 정리한다. 다른 기기의 `uploadId`로 `chunks`·`complete`·`cancel`을 부르면 404다. 실행 창을 닫거나 기기를 해제하면 그 업로드는 바로 지운다. 파일 이름은 경로·앞쪽 점·제어 문자·보이지 않는 방향 제어 문자를 없앤 120자 이내의 이름만 남긴다. 첨부가 있는 요청은 조정(steer)되지 않는다.

### 추가 필드

```
MobileInfo            { …, capabilities?: string[], direct?: string[] }   // direct: "direct" 기능과 함께, `host:port` 목록
MobileSessionSummary  { …, agentViewMode?: "plain" | "mighty", mightyStyle?: "cli" | "ouroboros",
                        styleId?: string }
MobileSessionDetail   { …, hasOlder?: boolean,
                        settings?: MobileSettings, mighty?: MobileMighty,
                        statusLine?: { lines: [[{ text, fg?: "#RRGGBB", bold?: boolean }]] },   // 최대 6줄
                        rateLimits?: [{ label, usedPercent, resetsAt?: ISO-8601 }] }
MobileSettings {
  editable: boolean,                         // 실행 중이면 false
  model, permissionMode, effort?, agentViewMode, mightyStyle, styleId,
  options: { models: [Option], permissionModes: [Option], efforts: [Option], mightyStyles: [Option], styles: [StyleOption] }
}                                            // Option { id, label }. mightyStyles는 예전처럼 cli·ouroboros 중 적격한 것만.
                                             // styles는 이 창이 실제로 고를 수 있는 것(cli + 승인·번들 스타일) — 아래 "스타일" 절
MobileCommand { name, description, source, argumentHint?, action? }
                                             // action이 있으면 휴대폰이 직접 처리: model·permission → 설정 선택, rename → 이름 변경,
                                             // clear·usage·help → /command. action이 없으면 입력창에 "/name "을 넣는다.
                                             // Mac 화면을 여는 명령(/plugin, /config)은 목록에 넣지 않는다.
MobileMighty {
  style: "cli" | "ouroboros",
  styleId: string,                                                       // 아래 "스타일" 절
  runs: [{ id, input, title?, status, blocks: [MobileBlock], result? }], // 최근 20개 요청, 시간순
                                             // result(선택, 추가 필드)는 그 요청의 최종 결과(run.finalOutput)를 블록 output과 같은 규칙으로
                                             //   정리해 20 000자까지 담는다. 창의 **가장 최근 요청 하나**가 끝났을 때(completed·error·stopped)만,
                                             //   결과가 비어 있지 않을 때만 실린다 — 롱 폴 페이로드를 작게 두려는 것이다. 그 밖의 요청은
                                             //   main 블록의 output(2 000자)을 쓴다. 롱 폴을 깨우는 mighty 요약값에는 이 요청이 끝난 뒤
                                             //   결과 길이도 들어가므로 상태가 바뀐 뒤 늦게 도착한 결과도 휴대폰을 깨운다.
                                             //   구버전 휴대폰은 무시하고, 신버전 휴대폰은 없으면 main 블록 output으로 "최종 결과" 카드를 그린다.
  panel?: MobileStylePanel,                                              // 아래 "스타일" 절
  ouroboros?: { phase, ready: boolean, takesText: [skill], next: [{ skill, title, help }], all: [{ skill, title, help }] },
  structure?: MobileStructure,                                           // 아래 "구조 보기" 절 — ?structure=로 물었을 때만, 바뀌었을 때만
  structureTag?: number                                                  // ?structure=로 물었을 때만. 0은 그릴 것이 없음
}
MobileBlock { id, kind, title, status, summary?, output?, durationMs?, nodeModelLabel?, activity?: [string] }
                                             // output은 2 000자까지. durationMs는 끝난 블록만(첫 기록~마지막 기록)
                                             // summary는 그 블록이 받은 지시를 한 줄로 편 것(1 000바이트까지): 하위 블록은 프롬프트, main 블록은 요청 자체
                                             // activity(선택, 추가 필드)는 running·waiting 블록에만 실리는 최근 단계 최대 8줄, 오래된 것부터.
                                             //   Mac 대화 기록과 같은 문구: 도구 호출은 그 요약(없으면 도구 이름), 답변·오류는 첫 줄. 실행 자체의
                                             //   "turn" 행과 질문의 원문(JSON)은 빠진다. 줄마다 summary처럼 한 줄로 펴고 1 000바이트에서 자른다.
                                             //   끝난 블록에는 없다. 이 필드를 모르는 구버전 휴대폰은 무시한다.
                                             // 롱 폴을 깨우는 mighty 요약값은 블록 id·상태와 함께 run.rootEntries·agent.entries의 개수와
                                             // 마지막 기록 id를 해시한다 — 새 단계가 휴대폰을 깨운다. 스트리밍 중인 글자는 여전히 해시하지 않는다.
                                             // 호스트는 요청당 블록 수를 제한하지 않는다. 휴대폰은 최근 200개만 그리고 나머지는 "이전 블록 N개 생략"으로 표시한다.
```

`activity.durationMs`와 `activity.provider`는 이미 전송되고 있다(도구 소요 시간 표시에 쓴다).

#### 구조 보기 (`mighty.structure`)

데스크톱 앱의 "구조" 보기(Mac `MightyCore/MightyStructure.swift`의 `MightyStructure.make`, Windows `MightyClaude.Core/MightyStructure.cs`의 `MightyStructure.Make`)를 **그 결과 그대로** 휴대폰에 보낸다. Windows 호스트의 전송 모양·한도·창별 캐시는 `MightyClaude.Core/Remote/MobileStructure.cs`가 Mac의 `MobileStructure.swift`와 같게 지킨다.

**묻는 휴대폰에게만, 바뀌었을 때만.** 이 값은 `capabilities`에 `"structure"`가 있는 호스트(같은 버전 이상의 Mac·Windows 앱)만 안다. 휴대폰은 구조 보기를 열어 둔 동안에만 세션 상세를 `GET /m1/sessions/{id}?since=…&wait=…&structure=<tag>`로 묻는다. `<tag>`는 휴대폰이 가진 구조의 태그(없으면 `0`, 숫자 1–12자리)이고, 다른 라우트(`/m1/state` 등)에서 이 이름은 지금처럼 400이다. 그래서 구버전 휴대폰과 대화·블록 보기는 이 값을 한 바이트도 받지 않는다. 물은 상세에는 `mighty.structureTag`(지금 구조의 태그, 그릴 것이 없으면 `0`)가 언제나 실리고, `mighty.structure`는 그 태그가 휴대폰이 보낸 것과 다를 때만 실린다. 태그는 정렬된 키로 인코딩한 값의 SHA-256 앞 8바이트를 1…999 999 999 999로 줄인 수다. 휴대폰은 구조를 상세와 따로 들고 있다가(`receiveStructure`) 태그만 같으면 그대로 쓰고, 새 값이면 바뀌지 않은 요청을 같은 객체로 남겨 그 카드만 다시 그린다. 구조 보기로 바꾸는 순간 롱 폴을 다시 시작해 곧바로 묻는다.

**릴레이 프레임 안에.** 응답 하나는 릴레이 프레임 하나(1 MiB, [relay.md](relay.md))에 실린다. 호스트는 구조가 실린 상세의 JSON이 `MobileWire.maximumReplyBytes`(1 MiB − 32 KiB, 봉투·암호 머리 몫)를 넘으면 구조부터 덜어내고 태그만 남긴다(`MobileRemoteSupport.outgoing`). 휴대폰은 태그가 다른데 값이 없으면 가진 것을 그대로 보이며 옛 태그로 다시 묻는다. 휴대폰은 요청의 단계나 요청·실행·결과 단계를 스스로 계산하지 않고 받은 것만 그린다. 파이프라인 보기의 보드(스타일 없는 창은 요청 하나, 스타일 창은 단계가 뒤로 돌아가지 않는 연속 요청 묶음 하나)는 Mac의 `MightyStructure.boards`와 같은 규칙으로 받은 값에서 묶는다(`mobile/src/lib/pipeline-board.ts`). 그래서 전송 모양은 그대로(`v: 1`)이고 Mac·Windows 어느 호스트에도 그대로 맞는다. 보내지 않은 오래된 요청(`omitted`)이 있으면 가장 오래된 보드는 Mac보다 짧을 수 있고 보드 번호는 휴대폰이 가진 것 기준이다. 입력도 Mac 창과 같다: 저장된 그래프의 요청들, 승인된 스타일의 평가기, 창의 현재 단계(Mac 단계 막대와 같은 `currentPhase(session:fileSourceStates:planStage:)`), 답한 계획 기록, 답을 기다리는 계획의 run id. Mac 창에만 있는 "새로 시작" 선택은 휴대폰에 오지 않으므로 휴대폰은 언제나 이미 있는 요청 기준으로 본다. CLI 기록에서 불러온 더 오래된 요청은 `runs`처럼 보내지 않는다.

```
MobileStructure {
  v: 1,                                       // 다른 값이면 휴대폰은 그리지 않고 "휴대폰 앱 업데이트 필요"를 보인다
  shape: "phases" | "free",                   // 단계가 있는 스타일이면 phases, 아니면 free
  columns?: [{ id, title, step, items: [Item] }],                       // phases: 단계 순서대로, items는 오래된 것부터
  rows?: [{ item: Item, request: Step, execution: Step, result: Step }], // free: 요청마다 한 줄, 오래된 것부터
  omitted?: number                            // 빠진 오래된 요청 수(없으면 필드 없음)
}
Step = "done" | "current" | "pending" | "error" | "stopped"           // 모르는 값은 pending(회색)으로 읽는다
Item {
  runId, ordinal,                             // ordinal은 창 안의 요청 번호("요청 3")
  title?,                                     // 스타일이 붙인 요청 이름(MobileMightyRun.title과 같은 규칙)
  status, finished: boolean,                  // status는 블록 상태 어휘
  request?, message?,                         // 요청 전문, 메인 에이전트의 최근 답(그 답이 최종 답이면 빠짐)
  activity: [Activity],                       // 메인 에이전트의 최근 도구 호출 최대 5개, 오래된 것부터
  answer?,                                    // 끝난 요청의 최종 답(Markdown)
  tally: { agents, tools },                   // 노드를 덜어내도 요청 전체를 센다
  clusters: [{ nodes: [Node] }],              // 하위 에이전트 트리마다 하나, 뿌리부터 깊이 우선
  omittedNodes?: number
}
Activity { kind, state, text }                // kind는 AgentActivity.kind, text는 한 줄
Node { id, depth, title, status, kind: "agent" | "task", model?, prompt?, output?, activity?: Activity }
                                              // depth는 0부터 6까지(더 깊으면 6), model은 버전 붙은 짧은 이름, activity는 마지막 도구 호출
```

**한도.** 글은 글자 경계(결합 이모지도 한 글자)에서 UTF-8 바이트로 자르고 잘린 글 끝에 "…"를 붙인다: `request`·`message` 1 KB, `answer` 4 KB(열린 코드 펜스는 한도 안에서 닫는다), 노드 `prompt` 300 B·`output` 1 KB, 활동 한 줄 200 B, 제목 160 B, 모델 80 B. 최근 요청 20개까지, 요청마다 노드 60개까지(`omittedNodes`). 값 전체의 JSON(열·줄 껍데기, 단계 이름, `omitted`까지 실제로 인코딩한 크기)은 192 KiB 이하로 맞추고, 넘치면 오래된 요청부터 뺀다. 가장 최근 요청 하나는 언제나 실리고, 그것만으로 넘치면 앞쪽 노드를 들어갈 만큼만(이분 탐색) 남긴다. 같은 run id가 두 번 있으면(손상된 저장) 가장 최근 것 하나만 싣는다. 단계가 있는 스타일은 요청이 하나도 없어도 단계 막대를 위해 `columns`를 보내고, 스타일이 없는 창에 요청이 없으면 필드 자체가 없다.

**다시 만드는 때.** 호스트는 휴대폰이 물을 때만, 그리고 창마다 `MightyStructure.Signature`나 키(요청 이름을 읽는 실행 가능한 스타일들의 id·해시, 모델 목록)가 바뀔 때만(스트리밍 글자는 몇 줄씩 자랄 때만) 구조를 다시 만든다. 바뀐 요청의 카드만 다시 읽고(`ItemCache`), 전송 모양과 JSON 크기도 바뀌지 않은 요청은 지난 것을 그대로 쓴다. 그 사이의 읽기는 마지막 값을 그대로 보낸다. 롱 폴을 깨우는 mighty 요약값에는 답한 계획 기록의 개수와 마지막 결과도 들어간다(계획 답이 요청의 단계를 옮긴다).

**버전이 어긋날 때.** 구버전 휴대폰은 묻지 않으므로 받지도 않는다. 신버전 휴대폰이 구버전 데스크톱 앱(Mac·Windows)에 붙으면 `capabilities`에 `"structure"`가 없으므로 묻지 않고, 요청이 있는 창의 구조 보기는 "Mac 업데이트가 필요합니다"(`phone.structure.needsUpdate.*`)를 보인다. 이 문구(와 `phone.structure.omitted`·`phone.structure.unreadable.*`)는 아직 Mac만 말하지만, Windows 호스트도 같은 기능 이름으로 구조를 보낸다. 휴대폰이 모르는 `v`·`shape`면 "휴대폰 앱 업데이트가 필요합니다"(`phone.structure.unreadable.*`)를 보인다. 공유 픽스처 `native/contracts/fixtures/mobile-structure.json`을 Mac(`MobileRemoteStructureTests`), Windows(`MobileStructureVerification.cs`)와 휴대폰(`structure.test.ts`)이 함께 읽어 필드 이름이 어긋나지 않게 한다.

### 스타일 (style)

하드코딩되어 있던 Ouroboros·Paperthin(Paperthin은 v7에서 지웠다)이 선언적 JSON 매니페스트로 구동되는 범용 엔진으로 바뀌면서(계약: [mighty-styles.md](mighty-styles.md)) 휴대폰도 스타일 id를 닫힌 두 값으로 알던 것을 멈추고 범용 패널 하나를 그리는 렌더러가 된다. 이 절은 `mighty-style-engine-v1` 태그에서 스키마·규칙 어휘와 함께 고정된다.

**`styleId`와 `mightyStyle`이 함께 실리는 이유.** `capabilities`는 호스트가 광고하고 휴대폰은 읽기만 하므로, "휴대폰이 새 스타일 이름을 아는가"를 호스트는 알 수 없다. 그래서 두 필드를 **언제나 함께** 보낸다.

- `mightyStyle`은 고정값 `cli`·`ouroboros` 안에서만 움직인다(v6까지는 `paperthin`도 있었고 v7에서 뺐다). `ouroboros`가 아닌 스타일(번들 클러드 플랜 포함)이 실제로 돌고 있어도 이 필드에는 언제나 `cli`가 실린다. v7에서 지운 `paperthin`으로 저장된 실행 창도 `cli`로 나가고, 옛 휴대폰이 `mightyStyle: "paperthin"`을 보내면 선택지 밖이라 400이다.
- `styleId`(신규, 선택)는 열린 문자열이다. 진짜 값은 언제나 여기 있다. **이 실행 창이 실제로 그 스타일로 돌고 있을 때만** 실제 id를 싣는다 — 미승인·취소·미등록·해시 불일치(마이티 스타일 계약 §3.4)는 모두 `"cli"`다. 저장된 값을 그대로 보내지 않는다: 미승인 스타일은 세션 요약 필드 하나로도 새어 나가지 않는다.
- 구버전 휴대폰은 `styleId`를 모르고 무시하므로 그 실행 창을 그냥 일반 CLI로 본다.
- 어긋날 때의 규칙은 하나뿐이다: **`styleId`가 있으면 `mightyStyle`은 무시한다.** `styleId`가 없고 `mightyStyle`만 있으면 그 값을 쓴다. 이 값이 붙는 자리는 `MobileSessionSummary.styleId?`, `MobileSettings.styleId`, `MobileMighty.styleId`다.

`MobileSettings.options.styles: [{ id, label, source? }]`는 `cli` + 이 실행 창이 **실제로 고를 수 있는(승인되었거나 번들인)** 스타일만 담는다. `source`는 `"bundled"` `"user"` `"workspace"`이고 `cli`에는 없다. 기존 `options.mightyStyles`는 그대로 `cli`와 내장 둘 중 적격한 것만 담은 채 계속 전송된다.

#### `mighty.panel`

승인·번들 스타일이 **실제로 그 실행 창을 돌리고 있을 때만**(`styleId != "cli"`일 때만) 붙는다. 그 밖에는 필드 자체가 없다. Swift 쪽 정의는 `StylePanelProjection.swift`(`StylePanel`)이고, JSON 인코딩은 UTF-8·키 사전순·들여쓰기 2칸·`\/` 이스케이프 없음으로 고정된다(골든 직렬화 규칙, mighty-styles.md §8.2).

```
MobileStylePanel {
  style: { id: string, name: string, source: "bundled" | "user" | "workspace",
           icon?: string, tint?: PaletteName },
  phase?: { id: string, title: string, index: number, count: number },   // index는 0부터
  groups: [{ id: string, title: string, axis?: string, question?: string,
             selected: boolean, actions: [actionId] }],
  actions: [{ id: string, title: string, icon?: string, glyph?: string, help: string, scope?: string,
              takesText: boolean, requiresText: boolean,
              flags: ("userInvoked" | "readOnly")[],
              prominent: boolean }],
  next: [actionId],                       // 순서대로, 첫 번째가 prominent
  recommended?: actionId,
  attachments: [{ id: string, title: string, detail?: string, readOnly: boolean }],
  setup: { ready: boolean, missing: [string], hint?: string, installCommand?: string },
  guidance?: string,                      // {phase}가 이미 실제 단계 title로 치환된 문자열
  presentation: { headerTitle: string, source: "bundled" | "user" | "workspace",
                  icon?: string, tint?: PaletteName }
}
```

`PaletteName`은 9개 고정값(`accent`·`purple`·`teal`·`indigo`·`mint`·`orange`·`green`·`red`·`secondary`), `icon`은 33개 고정값 중 하나다(마이티 스타일 계약 §1.10). **휴대폰은 SF Symbol 렌더러가 없으므로 `icon`은 파싱 단계에서 아예 버린다** — 타입에도 두지 않는다. 모르는 팔레트 이름은 accent로 그린다.

- `style.source`·`presentation.source`가 `"bundled"`가 아니면, `name`이 나오는 자리(선택기 이름, 패널 머리글)에 이름 옆으로 출처 배지가 붙는다. 이름 위조를 막는 것은 휴대폰에서도 이 배지다.
- `actions`는 **카탈로그 전체**(≤100)다. 화면에 무엇이 뜨는지는 `groups`와 `next`가 정한다.
- `requiresText`는 **UI 힌트**다. 입력이 비어 있으면 그 칩을 비활성으로 그리되, `/guided` 라우트는 이 값을 강제하지 않는다.
- `attachments`는 v7부터 언제나 빈 배열이다(첨부를 내던 내장 기능 `paperthin.casebook`을 v7에서 지웠다). 필드는 남는다 — v7 이전의 휴대폰이 읽기 때문이다. 휴대폰에는 파일을 여는 길이 없으므로 경로는 그전에도 싣지 않았다.
- `setup.installCommand`는 보여 주기 위한 것뿐이다. 휴대폰에서 그 명령을 실행하는 라우트는 없다.

**실행 중의 규칙.** 단계로 진행하는(`NextRule.byPhase`) 스타일은 실행 중일 때 `next`가 **빈 배열**이고 `guidance`는 실행 중 안내 문구다 — 다음 단계로 넘어갈 버튼이 없기 때문이다. 그룹으로 고르는(`byGroup`) 스타일은 실행 중에도 `next`와 칩이 평소와 같다 — 누른 것이 다음 요청으로 대기열에 줄을 선다. 같은 실행 창을 Mac에서 볼 때와 같은 규칙이다.

#### 레거시 페이로드

번들 Ouroboros에 한해 `mighty.ouroboros`는 **계속 나간다**. 필드 모양은 이전과 같고, 값은 이제 매니페스트 엔진에서 만들어진다(레거시 어댑터가 맡는다). 구버전 휴대폰은 이 필드만 읽으므로 아무것도 바뀐 것을 모른다. Ouroboros가 아닌 스타일에는 이 필드가 붙지 않는다 — 새 `panel` 하나만 붙는다. v6까지는 번들 Paperthin의 `mighty.paperthin`도 나갔고 v7에서 그 스타일과 함께 지웠다.

#### `/guided`의 새·옛 형식

```
POST /m1/sessions/{id}/guided
새 형식: { styleId, actionId, text? }
레거시:  { style: "ouroboros", skill, text? }
```

둘 다 올 수 있으면 **새 형식이 이긴다**. `style` capability를 모르는 구버전 호스트에는 새 형식이 400으로 거절되므로, 그 capability가 없는 호스트를 상대할 때는 휴대폰이 처음부터 레거시 형식을 보낸다(패널을 고른 것과 같은 capability 판정이 본문도 고른다). 오류는 오늘 문자열 그대로다.

- 스타일을 모르거나 **승인되지 않았다** → **400** `"알 수 없는 스타일입니다."` — 미등록과 미승인은 같은 문자열, 같은 즉시 응답이다. 어느 쪽도 디스크를 읽지 않으므로 타이밍으로도 구별되지 않는다. `POST …/settings`의 `styleId` 거절도 같은 문자열이다.
- 이 실행 창이 그 스타일이 아니다 → **409** `"이 실행 창은 <스타일 id> 스타일이 아닙니다."`(id를 끼워 넣는다. 이름이 아니다.)
- 행동(스킬)을 모른다 → **400** `"이 스타일에 없는 스킬입니다."`
- `actionId`(레거시의 `skill`)는 `^[A-Za-z0-9][A-Za-z0-9_.:-]{0,63}$`, 64바이트 이하.
- `text`는 지금처럼 32 KiB까지이고, 줄바꿈은 매니페스트의 `foldText` 규칙대로 접는다(`oneLine`은 공백으로 접어 한 줄).
- `requiresText`는 **검사하지 않는다** — UI 힌트일 뿐이다.

#### 구버전 휴대폰 ↔ 신버전 호스트, 신버전 휴대폰 ↔ 구버전 호스트

- **구버전 휴대폰이 신버전 호스트를 볼 때**: `capabilities`에서 `"style"`을 모르므로 `styleId`·`panel`·`options.styles`를 전부 무시한다. `mightyStyle`은 여전히 `cli`·`ouroboros` 중 하나이므로 Ouroboros는 그대로 보이고, 그 밖의 스타일이 도는 실행 창은 `cli`로 보인다(일반 CLI 화면). Ouroboros의 레거시 페이로드는 그대로 나가므로 오늘과 똑같이 동작한다.
- **신버전 휴대폰이 구버전 호스트를 볼 때**: `capabilities`에 `"style"`이 없으므로 `mighty.panel`도 `options.styles`도 오지 않고, 새 렌더러는 `mighty.ouroboros`(있는 경우)만으로 패널을 그린다. `/guided`는 레거시 본문 `{style, skill}`을 보내고, `/settings`는 `mightyStyle`만 보낸다.

**휴대폰은 스타일을 등록·승인·취소·재스캔할 수 없다.** 그 넷은 데스크톱 앱(Mac·Windows)에서만 한다(마이티 스타일 계약 §4.5). 휴대폰의 승인 여부는 오직 `styleId`·`options.styles`의 결과로만 드러난다.

### 파일 (files)

Mac 파일 창([file-pane.md](file-pane.md))을 휴대폰에서 **읽기 전용**으로 보는 두 라우트다. 아무것도 쓰지 않고, 편집·첨부·이름 바꾸기 라우트는 없다.

```
GET /m1/workspaces/{id}/files?path=<상대 경로>
MobileFileListing {
  protocol: 1, workspaceId, path,                      // path: 요청한 폴더("" = 루트)
  entries: [{ name, relativePath,                      // relativePath: 루트 아래 "/"로 이은 경로. 그대로 path=에 되돌려 보낸다
              kind: "folder" | "file" | "symlink-folder" | "symlink-file",
              size?: number,                           // 바이트, 파일만(링크는 대상 파일의 크기)
              modified?: ISO-8601,
              noise: boolean }],                       // .git·node_modules 같은 잡음 폴더(Mac처럼 흐리게)
  truncated: boolean                                   // 항목이 더 있었다
}

GET /m1/workspaces/{id}/file?path=<상대 경로>
MobileFilePreview {
  protocol: 1, workspaceId, path, name, size, modified?,
  type: "source" | "markdown" | "image" | "unsupported",
  // source·markdown
  language?: string,        // source만: Mac 강조기의 언어(swift, javascript, …, 강조 없는 텍스트는 plain)
  encoding?: string,        // "UTF-8" "UTF-8 BOM" "UTF-16 LE" "UTF-16 BE" "UTF-32 LE" "UTF-32 BE" "CP949 (EUC-KR)"
  text?: string, truncated?: boolean, lineCount?: number,
  // image
  mime?: "image/jpeg" | "image/png",                   // 썸네일 자체의 형식
  width?, height?,                                     // 원본 크기: 비트맵은 픽셀(회전 반영), svg·pdf는 포인트
  thumbnailWidth?, thumbnailHeight?, data?: base64,
  // unsupported
  reason?: "binary" | "notRegularFile" | "tooLarge" | "undecodable"
}
```

- 정렬·잡음 폴더·분류·인코딩은 Mac 파일 창과 같은 함수(`WorkspaceFiles`, `FilePreviewClassifier`)가 정한다: 폴더 먼저, 대소문자 무시·자연 정렬, 숨김 파일 포함. 텍스트는 BOM → UTF-8 → CP949 순서로 풀어 **UTF-8 문자열**로 보내고 `encoding`에 무엇으로 풀었는지 적는다. Markdown은 원문 그대로 보내고 휴대폰이 렌더링한다(131,072바이트가 넘으면 원본으로).
- 이미지: 비트맵은 ImageIO로 긴 변 2,048픽셀 이하 썸네일을 만든다. 투명한 이미지는 PNG, 아니면(또는 PNG가 크면 흰 배경에 얹어) JPEG(품질 0.8)이고, 인코딩한 크기가 512 KiB를 넘으면 긴 변을 1,600 → 1,280 → 1,024 → 768 → 512 → 256으로 줄여 가며 다시 만든다. pdf는 첫 쪽을 흰 배경에, svg는 앱이 AppKit으로 그린 그림을(포인트 크기의 2배, 최대 2,048픽셀) 같은 규칙으로 보낸다. 50 MB보다 큰 이미지는 읽지 않고, 헤더의 가로×세로가 1억 픽셀(`FilePreviewClassifier.maximumFullPixels`)을 넘는 비트맵은 썸네일로도 풀지 않는다(둘 다 `unsupported`·`tooLarge`). 풀 수 없으면 `undecodable`. svg·pdf의 포인트 크기가 유한하지 않거나 0 이하이거나 한 변이 10,000,000포인트를 넘으면 그리지 않고 `undecodable`이다 — 정수로 바꾸기 전에 거른다.
- **svg 규칙.** CoreSVG(`NSImage`)는 `<image href>`의 `file:` URL·절대 경로·맨 상대 경로(앱 프로세스의 작업 폴더 기준)를 따라가 읽으므로, 그리기 전에 파일 바이트를 보고 조금이라도 바깥을 부를 수 있으면 그리지 않고 `undecodable`로 답한다(`FilePreviewClassifier.svgLoadsExternalContent`). 텍스트로 풀고(BOM → UTF-8 → CP949, 풀리지 않으면 거부) 숫자 문자 참조(`&#…;`)와 기본 엔티티를 풀고 대소문자를 무시한 뒤, 다음 중 하나라도 있으면 거부한다: `#`으로 시작하지 않고 svg·xml·html이 아닌 `data:`도 아닌 `href`·`xlink:href`(접두어 무관)·`src`·`srcset` 값(따옴표 유무·`=` 앞뒤 공백 무관, 빈 값 포함), 같은 조건의 CSS `url(`, `@import`, `image-set(`, `xml:base`, `<!ENTITY`, 식별자(`SYSTEM`/`PUBLIC`)나 내부 부분집합(`[`)이 있는 DOCTYPE, CSS 역슬래시 이스케이프, ASCII로 읽을 수 없는 XML 선언 인코딩(UTF-7 등). 링크(`<a href="https://…">`)나 W3C DTD를 적은 svg도 거부되는 보수적 규칙이다.
- 폴더·FIFO·소켓·장치 파일은 열어도 읽지 않고 `unsupported`·`notRegularFile`(크기·수정일만), 바이너리는 `binary`.

**한도.** 폴더당 항목 2,000개(넘으면 `truncated: true`), 텍스트는 파일 앞 512 KiB. 응답 본문(JSON) 전체를 768 KiB 이하로 맞춘다 — 릴레이는 1 MiB가 넘는 프레임에서 연결을 끊고([relay.md](relay.md)) 내려받기에는 청크 방식이 없기 때문이다. 이스케이프로 커지는 텍스트(제어 문자 등)는 4분의 1씩 더 잘라 `truncated: true`로, 긴 경로로 커지는 목록은 뒤 항목을 덜어 `truncated: true`로 보낸다. 썸네일은 인코딩 전 512 KiB(base64 약 683 KiB). 폴더는 디스크에서 이름을 20,000개(`WorkspaceFiles.maximumEnumeratedNames`)까지만 읽고 그 너머는 읽지 않은 채 `truncated: true`로 답하므로, 수십만 개짜리 폴더를 거듭 요청해도 Mac이 전부 확인·정렬하지 않는다. **미리보기 자리.** 이미지(비트맵·pdf·svg)를 풀 때만 미리보기 전용 자리 하나(`concurrentImagePreviews`)를 잡는다. 텍스트 미리보기와 목록은 자리를 잡지 않고, 첨부가 있는 `submit`은 자기 자리 둘을 따로 쓰므로 업로드·첨부 보내기가 미리보기 뒤에서 기다리는 일은 없다. 자리를 기다리는 동안 같은 기기의 새 미리보기가 오면 먼저 기다리던 것은 풀지 않고 409 `superseded`로 끝낸다(휴대폰은 이미 다른 파일로 넘어갔다). 자리를 잡은 뒤와 썸네일 단계(읽기, 크기 사다리의 각 단계) 사이마다 요청이 취소됐는지 보고, 취소됐으면 더 풀지 않는다. 읽기는 모두 메인 스레드와 라우팅 액터 밖에서 한다.

**보안 규칙.** 루트는 Mac의 파일 창과 같은 워크스페이스 폴더(`MobileState.workspaces[].path`)이고, Mac에 있는 워크스페이스만 된다. 경로 검사는 새로 만들지 않고 파일 창의 것을 그대로 쓴다: 목록은 `WorkspaceFiles.list`(심볼릭 링크를 풀어 실제 경로가 루트 밖이면 목록에서 뺀다), 파일은 `WorkspaceFiles.openFile`(`O_NOFOLLOW|O_NONBLOCK`으로 열고, 일반 파일인지 `fstat`로, 열린 파일의 실제 경로를 `F_GETPATH`로 다시 확인한 뒤 그 디스크립터로만 읽음). 그 앞에서 휴대폰의 `path`를 먼저 거른다: 절대 경로와 `..` 요소는 `outsideWorkspace`, NUL·4,096바이트 초과·빈 요소(`a//b`)·`.` 요소는 `badPath`. `path` 말고 다른 질의나 `path`가 두 번 오면 400.

**오류.** 본문은 `{ protocol: 1, error, code }`다. `code`는 휴대폰이 자기 말로 바꿔 보여 주는 값이고 `error`는 한국어 문장이다.

| 상태 | `code` | 뜻 |
|---|---|---|
| 404 | `workspaceNotFound` | Mac에 그런 워크스페이스가 없다 |
| 404 | `notFound` | 그 경로에 아무것도 없다 |
| 403 | `outsideWorkspace` | 절대 경로·`..`, 또는 루트 안 폴더에 바로 있는 링크인데 실제 경로가 루트 밖(끊어진 링크 포함) |
| 403 | `notReadable` | 권한이 없는 등 읽을 수 없다 |
| 400 | `notDirectory` | `files`에 파일 경로를 줬다 |
| 400 | `badPath` | 형식이 틀린 경로, 또는 `file`에 빈 경로 |
| 414 | `badPath` | 요청 경로(질의 포함)가 16 KiB를 넘는다(터널이 답한다) |
| 409 | `superseded` | 같은 기기의 새 미리보기가 와서, 자리를 기다리던 이 요청은 건너뛰었다 |

**존재 여부를 흘리지 않는다.** 루트 밖으로 나가는 링크 **아래**의 경로(`outdir/secret.txt`처럼 부모 폴더가 루트 안으로 풀리지 않는 경로)는 거기에 무엇이 있든 없든 똑같이 404 `notFound`다. `outsideWorkspace`는 루트 안으로 풀리는 폴더에 바로 들어 있는 항목(그 링크 자체)에만 쓴다 — 그 폴더의 목록으로 이미 보이는 것이다.

`files` capability가 없는 호스트에는 두 라우트가 없으므로(404) 휴대폰은 `파일` 버튼을 보이지 않는다.

### Antigravity 권한 카드

Antigravity 창(`provider: "gemini"`)은 agy를 헤드리스(`agy -p`)로 돌리므로 실행 도중 권한을 물을 수 없다. agy는 자기 검토 모드가 물었을 작업(명령 실행, 파일 쓰기 등)을 거절하고 턴을 이어 간 뒤, 끝에 무엇을 거절했는지 알린다. 호스트는 그 턴이 끝나면 Claude 권한 카드와 같은 경로로 묻는다: 권한 모드와 상관없이(기본 '수동'도) 거절된 작업이 하나라도 있으면 카드 하나가 생긴다. **Claude와 다른 점**: Claude는 실행 전에 멈추고 답을 기다리지만, Antigravity는 그 턴을 끝낸 뒤 묻고, 허용하면 같은 대화에서 이어서 진행한다.

**카드 (`MobileSessionDetail.permissions`의 한 항목)** — Mac과 Windows 호스트가 모두 같은 계약으로 싣는다(Windows `AntigravityPermissions.cs`·`DesktopService.Antigravity.cs`).

```
MobilePermission {
  id: "agy-<uuid>", runId,              // runId는 거절된 실행의 id
  toolName: "AntigravityPermission",
  title,                                // antigravity.permission.title ("Antigravity가 이 작업을 허용해 달라고 합니다")
  fields: [{ label, value }],           // 작업마다 하나: label은 종류 이름(명령·주소·파일·작업), value는 target 또는 displayName
  summary,                              // target(없으면 displayName)들을 ", "로 이은 것
  canAllow,                             // rule이 있는 작업이 하나라도 있으면 true
  receivedAt,
  antigravity: { actions: [MobileAntigravityAction] }
}
MobileAntigravityAction {
  kind: "command" | "url" | "file" | "other",   // 모르는 값은 "other"로 읽는다
  action,                               // agy의 권한 이름: "command", "write_file", "read_file", "read_url", "execute_url", "mcp", … (규칙이 없으면 도구 이름)
  displayName,                          // agy의 이름("RunCommand", "WriteFile"); 모르면 도구 이름
  target?,                              // 명령 전체, 도메인, 경로. agy가 말하지 않았으면 없음
  rule?,                                // 이 작업을 허용하는 permissions.allow 규칙(agy에서는 접두어). 없으면 규칙으로 허용할 수 없는 작업 — 카드는 "권한 모드를 바꾸세요"(antigravity.permission.noRule)를 보인다
  onceOnly?                             // true: 이 규칙은 이번만 허용할 수 있다(아래). 카드는 항상 허용을 보이지 않고 그 이유(antigravity.permission.onceOnly)를 보인다. 아니면 없음
}
```

- 규칙: 명령은 `command(<명령 전체>)`, 그 밖에는 agy가 거절 문구에 쓴 그대로(`user denied permission for write_file(/private/tmp/a.txt)` → `write_file(/private/tmp/a.txt)`)이되 그 도구의 권한 이름일 때만이다(닫힌 대응: `run_command`→`command`, `write_file`·`write_to_file`·`replace_file_content`·`multi_replace_file_content`·`sed_file`·`notebook_edit`→`write_file`, `view_file`·`read_file`·`list_dir`→`read_file`, `read_url_content`→`read_url`; 그 밖의 도구는 규칙 없음). 규칙은 `이름(…)` 꼴, 4 KiB 이하, 줄바꿈·NUL 없음(`AntigravityAllowGrant.validRule`).
- **규칙은 접두어다**(agy 1.4.5 조사): `command(echo hi)`는 `echo hi there`처럼 **그 글자로 시작하는 모든 명령**을 허용하고(`echo hithere`는 아님), `write_file(<폴더>)`는 그 폴더 안의 모든 파일을, `command(*)`는 모든 명령을 허용한다. 카드는 명령 규칙 아래에 "이 글자로 시작하는 모든 명령을 허용"(`antigravity.permission.ruleCommand`)이라고 적는다.
- **이번만 허용도 전역이다**: agy에는 워크스페이스별 규칙 파일이 없어(조사 결과) 임시 규칙도 전역 설정에 들어간다. 그 규칙이 있는 동안에는 **이 컴퓨터의 모든 Antigravity 실행**(다른 창, 다른 워크스페이스, 터미널의 agy)에 적용된다. 카드의 안내(`antigravity.permission.scopeNote`)가 이를 말한다.
- **규칙이 없는 작업**(카드는 거절과 권한 모드 안내만): 명령에 `(`·`)`·`*`·줄바꿈·NUL이 있으면, 인수가 비었거나 `/`·`~`·`*`·드라이브 루트(`C:\`)이면, 닫힌 대응 밖의 도구이거나 거절 문구의 규칙 이름이 도구의 권한과 다르면, 규칙을 말하지 않은 거절이면. 파일 규칙(`write_file`·`read_file`)은 절대 경로여야 하고, 그 경로가 agy 자신의 설정 폴더(`~/.gemini`, Windows `%USERPROFILE%\.gemini`)나 앱 데이터 폴더(장부가 있는 곳: Mac `~/Library/Application Support/MightyClaude Native`, Windows 앱 데이터 폴더) 안이거나 같거나 그 위(홈 폴더, `/Users`, 드라이브 루트 등)이면 규칙이 없다(대소문자 무시, `/`·`\` 같게, `.`·`..` 정리, 심볼릭 링크를 푼 경로도 본다). 거기에 항상 허용 규칙이 있으면 agy가 자기 권한이나 앱의 장부를 고칠 수 있기 때문이다. 설정 파일을 고치는 쪽(규칙 보관)도 자기 설정 폴더와 장부 폴더로 다시 막는다.
- **이번만 허용할 수 있는 명령**(`onceOnly: true`, 항상 허용 없음): `;` `&` `|` `>` `<` 백틱 `$(`가 있거나, 닫힌 파괴적 명령 목록(`rm`, `rmdir`, `unlink`, `shred`, `dd`, `mkfs…`, `sudo`, `su`, `doas`, `kill`, `killall`, `pkill`, `shutdown`, `reboot`, `halt`, `poweroff`, `eval`, `del`, `erase`, `rd`, `format`, `Remove-Item`, `taskkill`, `Stop-Process`, `Stop-Computer`, `Restart-Computer`, 재귀 `chmod`/`chown`, `git push --force|-f|+refspec`, `git reset --hard`, `git clean`, `find … -delete`)에 드는 것, 셸·인터프리터(`bash`, `sh`, `zsh`, `pwsh`, `powershell`, `cmd`, `python`, `node`, `ruby`, `perl`, `php`, `lua`, `osascript`, `py`, `awk`, `gawk` 등)를 홀로, 스크립트 없이 옵션만으로(`bash -l`, `python3 -u`; 버전·도움말 옵션만이면 제외), 또는 코드를 바로 넘기는 플래그(`-c`, `-e`, `-p`, `-r`, `--eval`, `-Command`, `-EncodedCommand`, `/c` 등)로 실행하는 명령과 다른 명령을 실행하는 래퍼(`env`, `xargs`, `nohup`, `time`, `timeout`, `nice`, `watch`, `exec`, `source`, `.`, `npx`, `bunx`, `uvx`, `pipx`, `command`, `builtin`, `caffeinate`, `arch`, `stdbuf`, `wsl`, `setsid`, `script`; `awk`·`gawk`는 첫 인수가 프로그램이라 무엇이 오든; 앞의 `VAR=x`·`env VAR=x`와 빈 따옴표 낱말은 건너뛰고 첫 낱말로 보며, `VAR=x`만 있는 명령도 여기 든다), 그리고 `.gemini`를 언급하는 명령(읽기만 해도). 각 항목의 `onceOnlyReason`이 그 이유(`risky`·`interpreter`·`agyFolder`, 이 순서로 판정)를 알리고 카드의 문구(`antigravity.permission.onceOnly`·`onceOnlyInterpreter`·`onceOnlyAgyFolder`)를 고른다. 이유를 모르는 휴대폰·이유를 보내지 않는 호스트는 `risky` 문구를 쓴다. 목록은 `AntigravityRuleSafety`(Mac `MightyCore`, Windows `AntigravityPermissions.cs`)와 공유 고정 파일의 `rules`가 같다.
- 단계의 거절은 턴 결과의 `denied_actions`에 그 권한이 실렸을 때만 센다. `denied_actions`에만 있고 어느 단계에도 없던 것은 규칙 없는 항목으로 남는다.
- `MobileSessionSummary.pendingPermissions`는 카드를 하나로 센다. 카드가 있는 창은 보통 실행 중이 아니다(`status`가 `completed` 등).
- 카드는 답할 때까지, 또는 그 창의 **다음** 실행이 끝날 때까지 남는다. 다음 실행이 거절을 남기면 그 카드가 자리를 잇고, 남기지 않으면 카드가 사라진다.
- 같은 턴에 대화에 짧은 안내 줄 하나(`run.notice.antigravityAsked`, `kind: "system"`)가 남는다. 이 줄은 카드를 모르는 휴대폰에도 보인다.

**답 — `POST /m1/sessions/{id}/permission`** (`{ requestId, runId, allow, always? }`, 응답은 Claude 카드와 같다. 호스트가 처리한다)

| 본문 | 버튼 | 호스트가 하는 일 |
|---|---|---|
| `allow: false` | 거절 | 카드를 닫는다. 아무것도 실행하지 않는다 |
| `allow: true` | 이번만 허용 | 카드의 규칙을 그 호스트의 agy 설정 `permissions.allow`에 넣고, 같은 대화(창의 `resumeId`)로 이어 보내는 글(`antigravity.permission.continue`: 허용한 작업을 적은 짧은 문장)을 보낸다. 그 실행이 끝나면(성공·실패·중지·앱 종료 모두) 앱이 넣은 규칙만 지운다 |
| `allow: true, always: true` | 항상 허용 | 같은 규칙을 넣고 지우지 않는다. 이어 보내기는 같다. `onceOnly` 규칙이 있는 카드에는 409(`remote.error.antigravityOnceOnly`) |

- 이어 보내는 글은 데스크톱 입력창의 규칙을 따른다: 창이 실행 중이거나 업데이트가 붙잡고 있으면 대기열에 들어가고(규칙은 그 실행이 시작될 때 들어간다), 아니면 바로 시작한다. 휴대폰은 결과를 대기열·대화로 본다.
- 오류: `requestId`·`runId`가 식별자 꼴이 아니면 400(`remote.error.badPermissionId`, Mac과 Windows 호스트 모두). `always: true`에 `allow: false`는 400(`remote.error.alwaysNeedsAllow`). `always: true`를 Antigravity 카드가 아닌 요청에 보내면 409(`remote.error.notAntigravityPermission`). 카드가 이미 없거나 `requestId`·`runId`가 맞지 않으면 409(`remote.error.notPendingPermission`). `canAllow: false`인 카드의 허용은 409(`antigravity.permission.noRule`). 이번만 허용할 수 있는 규칙이 있는 카드의 항상 허용은 409(`remote.error.antigravityOnceOnly`). 설정 파일을 고칠 수 없으면(없는 폴더, 읽을 수 없음, JSON이 아님, `permissions`가 객체가 아님, `permissions.allow`가 배열이 아님, 저장 실패) 409에 그 이유(`antigravity.settings.error.*`)를 싣고, 카드는 남아 같은 이유를 보이며 아무것도 실행하지 않는다.
- **설정 파일**: `~/.gemini/antigravity-cli/settings.json`(Windows는 사용자 폴더의 `.gemini\antigravity-cli\settings.json`). `permissions.allow` 배열만 바꾸고 다른 키·순서·값·공백은 한 바이트도 바꾸지 않는다(규칙은 목록 끝에 그 목록의 모양대로 붙고, 지울 때는 붙인 것만 그대로 뺀다). 이미 있던 규칙은 더하지 않고, 지우지도 않는다. 옆에 임시 파일을 써서 이름을 바꿔 덮는다(권한·심볼릭 링크 유지). 파일이 없고 폴더가 있으면 그 규칙만 담은 파일을 만든다. 같은 규칙을 여러 창이 잠깐 쓰면 마지막 사용이 끝날 때 지운다. 앱이 잠깐 넣은 규칙은 앱 데이터 폴더의 `antigravity-temporary-rules.json`(장부)에 먼저 적어 두고, 지운 것이 확인될 때까지 장부에 둔다: 지우기가 실패하면(Windows에서 agy가 파일을 열고 있어 바꾸지 못할 때 등) 잠깐씩 늘려 가며 다시 하고, 그래도 남으면 다음 허용·해제와 다음 실행 때 다시 지운다. 지운 뒤 몇 초 있다 한 번 더 보고, 실행 중인 agy가 예전 내용을 다시 써 규칙이 돌아왔으면 또 지운다. 앱이 만든 `"permissions": {"allow": []}`나 `"allow": []`는 비면 지우고, 앱이 만든 파일이 비면(`{}`) 파일도 지운다. 줄바꿈이 CRLF인 파일에는 CRLF로 넣는다. 끊긴 심볼릭 링크는 새 파일로 바꾸지 않고 거절하고(`antigravity.settings.error.unreadable`), UTF-8 BOM으로 시작하는 파일은 JSON이 아닌 것으로 거절한다(`notJSON`). 앱을 끝내는 중에는 규칙을 더하지 않는다.
- **구버전 휴대폰**은 `antigravity`를 모르고 이 카드를 보통 권한 카드(제목·필드·허용/거부)로 보인다. 허용은 이번만 허용, 거부는 거절로 처리된다. **이 카드를 모르는 구버전 호스트**(카드 이전의 Mac·Windows 앱)는 이 카드를 싣지 않고, 대화에 거절 안내 줄(`run.notice.antigravityDenied`)만 남긴다.
- **원격 워크스페이스 창**(데스크톱이 다른 컴퓨터에 붙여 쓰는 창, 휴대폰 목록에는 없다)은 그 창을 실행하는 호스트가 규칙을 처리한다: 호스트 실행의 이벤트 `antigravityPermission`(`RunEvent.antigravityPermission`, 위 카드와 같은 actions)이 클라이언트 창에 카드를 띄우고, 허용하면 클라이언트가 이어 보내는 `POST /v2/runs`에 `antigravityGrant: { requestId, rules, temporary }`를 실어 보낸다. 호스트는 자기가 알린 카드(하루 안, 최근 128개)의 규칙만 받고 그 밖은 403(`remote.host.error.antigravityRules`)이다. 카드의 규칙은 **한 번만** 쓸 수 있다: 처음 받아들인 허용이 호스트의 목록에서 그 카드를 지우므로 같은 카드로 다시 보내면 403이다(규칙을 넣지 못해 실패한 허용은 쓰지 않은 것으로 친다). `temporary: false`(항상 허용)는 호스트의 '원격에서 전체 권한·명령 실행 허용'이 켜져 있을 때만 받고, 아니면 403(`remote.host.error.antigravityAlways`)이다. 켜져 있어도 이번만 허용할 수 있는 규칙이면 409(`remote.error.antigravityOnceOnly`). 클라이언트는 호스트의 카드에 규칙 안전 검사가 거절하는 규칙(`command(*)` 등)이 있으면 그 이벤트를 받지 않는다. 규칙을 넣지 못하면 409로 그 이유를 보낸다. 이 이벤트를 모르는 구버전 클라이언트는 그 이벤트만 빼고 `remote.run.notice.skipped` 줄을 남긴다.
- 와이어 표본과 agy 스트림(agy 1.4.4·1.4.5에서 녹화)·규칙 판정(`rules`)·도구 대응(`tools`)·설정 편집의 바이트 단위 기대값: `native/contracts/fixtures/antigravity-permissions.json`. Mac 구현은 `MightyCore/AntigravityPermissions.swift`(파서·카드 수명·설정 편집·규칙 보관), `MightyClaude/AppStore+AntigravityPermissions.swift`(버튼·이어 보내기), `MightyClaude/ToolPermissionBar.swift`(카드).

### 푸시 알림 (push)

호스트(Mac이 기준, Windows는 같은 규칙)가 일이 끝났거나 사람이 필요할 때 안드로이드 휴대폰에 알린다. 릴레이 쪽 HTTP 라우트(핸들 등록·전송, 상태 코드, 한도)는 [relay.md](relay.md)의 "푸시 알림"이고, 이 절은 호스트와 휴대폰의 약속이다. 호스트는 `"push"` 기능을 알릴 때만 이 절을 따른다.

**핸들.** 휴대폰은 FCM 토큰을 **페어링마다** 그 페어링의 릴레이(`POST /push/v1/handles`)에 등록해 핸들과 비밀을 받고, 핸들만 그 호스트에게 `POST /m1/push/register`로 준다(비밀은 휴대폰만 갖는다). 호스트마다 다른 핸들이라 두 호스트가 서로의 휴대폰을 알아볼 수 없다. 함께 주는 `kinds`는 휴대폰이 켠 종류(`completed` `failed` `permission` `question` `plan` `signin`)이고, 모두 끈 휴대폰은 빈 배열을 준다. 호스트는 기기 목록의 그 기기 줄에 `push: { handle, kinds, updatedAt }`로 둔다(Mac `devices.json`, Windows `mobile-identity.json`). 기기를 해제하거나 키를 다시 만들면 기기 줄과 함께 지워지고, 그 휴대폰이 보고 있던 세션 기록(아래 `viewing`)도 지워진다. 호스트는 핸들만 알고 FCM 토큰은 모른다. 휴대폰은 FCM 토큰이 바뀌면 릴레이의 핸들을 갱신하고(`PUT`), 토큰이 그대로여도 호스트에 다시 줄 때 하루에 한 번은 같은 `PUT`으로 릴레이가 그 핸들을 아직 아는지 확인한다(204면 그대로 두고 릴레이의 사용 시각도 새로워진다). 릴레이가 핸들을 잊었으면(404: 저장소 초기화, 120일 미사용 정리, 자리 비우기) 새로 만들어 호스트에 다시 준다. 릴레이에 닿지 못하면 가진 핸들을 그대로 주고 다음 동기화에서 다시 묻는다. 페어링을 지우면 릴레이의 핸들도 지운다(`DELETE`, 기다리지 않는다). 호스트는 다음 전송에서 404를 받고 자기 기록을 지운다. `/m1/push/unregister`는 휴대폰이 호스트에게 핸들을 거두게 하는 라우트이고 지금 앱은 쓰지 않는다. 기기 토큰이 없는 기기(구버전 앱 묶음)는 봉인 키를 만들 수 없어 409이고, 휴대폰은 그 호스트를 푸시 없음으로 둔다.

**봉인.** 푸시는 호스트가 그 휴대폰 하나만 열 수 있게 봉인한 뒤 릴레이에 맡긴다. 릴레이와 Google은 내용을 읽을 수 없다.
- `key = HKDF-SHA256(ikm = SHA-256(UTF-8 기기 토큰)의 32바이트, salt = UTF-8 clientId, info = "mighty-push-v1", 길이 32)`. ikm은 다이제스트의 **원시 32바이트**이지 64자 16진수 글자가 아니다. 호스트는 기기 줄의 `tokenHash`(16진수)를 풀어 같은 32바이트를 얻고 `clientId`는 기기 id다. 릴레이 연결과 Tailscale 직접 연결이 같은 기기 토큰으로 인증하므로 키도 같다.
- 봉투는 `base64url(0x01 ‖ nonce 12바이트 ‖ ChaCha20-Poly1305 암호문 ‖ 태그 16바이트)`(패딩 없음)이다. AAD는 `"mighty-push-v1:" + handle`이라 다른 핸들 아래에서는 열리지 않는다. 평문은 2048바이트 이하, 봉투는 3072자 이하다.
- 고정 파일 `native/contracts/fixtures/push.json`(키, 봉투, 변조한 봉투, 계획 단계)을 세 구현(Mac `MobilePush.swift`, Windows `MobilePush.cs`, 휴대폰 `mobile/src/lib/push/seal.ts`)이 함께 지킨다. 자세한 칸은 `native/contracts/README.md`의 "Push notifications".
- 호스트의 기기 목록 파일(소유자만 읽는다)을 읽을 수 있는 사람은 그 기기의 푸시를 열 수 있다.

**평문.**

```
MobilePushMessage {
  v: 1, id,                       // "request:<runId>:<requestId>" | "run:<sessionId>:<n>:completed|failed" | "signin:<sessionId>:<n>"
  kind: "completed" | "failed" | "permission" | "question" | "plan" | "signin",
  hostId, hostName, workspace, title, summary, sessionId,   // hostName·workspace·title 80자, summary 200자(유니코드 스칼라), summary는 한 줄
  permission?: { requestId, runId, buttons: "claude" | "antigravity" | "antigravityOnce" | "none" },  // kind가 permission일 때만
  at                              // ISO 8601 UTC, 초 단위
}
```

인코딩한 평문이 2048바이트를 넘으면(제어 문자는 6바이트로 이스케이프된다) `summary`를 비워 보내고, 비워도 넘으면 그 푸시를 건너뛴다(로그에는 id만).

**무엇을 언제 보내는가** (`MobilePushPlanner`; 공유 고정 파일의 `planner` 단계가 같은 규칙을 모든 구현에 건다). 호스트는 보이는 실행 창(휴대폰이 볼 수 있는 것만: 파일 창과 다른 컴퓨터의 워크스페이스는 뺀다)을 그때그때 플래너에게 준다.
1. 처음 보는 상태는 기준선이다. 앱이 켜질 때, 그리고 **휴대폰 리모트를 껐다가 다시 켤 때** 이미 있던 요청과 끝난 실행은 알리지 않는다. 껐다 켜기 전에 보낸 id는 다시 쓰지 않는다. 휴대폰 리모트가 꺼져 있는 동안과 앱을 끝내는 중에는 아무것도 보내지 않는다.
2. 새 요청은 한 번 알린다: 권한(`permission`), 질문(`question`), 계획(`plan`), Antigravity 카드(`permission`). id는 `request:<runId>:<requestId>`. 스타일이 자동 허용하는 요청은 알리지 않는다.
3. 실행 창이 `running`에서 `completed`나 `error`로 바뀌고 기다리는 요청이 하나도 없을 때만 완료(`completed`)·실패(`failed`)를 알린다. 요청이 남아 있으면 요청만 알린다. `stopped`로 끝난 것은 알리지 않는다. id는 `run:<sessionId>:<n>:<kind>`로, `n`은 그 실행 창의 끝난 실행 번호다.
4. 로그인 만료(`signin`)는 호스트가 확인한 뒤에만(Mac `loginRequired`, Windows는 로그인 복구가 기억한 요청) 한 번 알린다. id는 `signin:<sessionId>:<n>`.
5. `n`(실행 번호와 로그인 번호)은 실행 창이 잠깐 목록에서 빠졌다 돌아와도 이어서 센다. 기준선과 마지막 상태는 빠진 동안 잊지만 번호와 이미 보낸 id(최근 512개)는 기억한다. 그래서 돌아온 실행 창의 다음 완료·로그인 만료도 알리고, 같은 id를 두 번 보내지 않는다.
6. 실행 창 하나는 휴대폰에서 상태 알림 한 자리를 쓴다(아래 "폰"). 그래서 한 실행 창의 완료·실패·로그인 만료는 같은 알림을 바꾸고, 요청 알림은 요청마다 따로 쌓인다.
7. 받는 휴대폰: `kinds`에 그 종류가 없으면 보내지 않는다. 그 휴대폰이 `POST /m1/push/viewing`으로 **그 실행 창**을 보고 있다고 한 지 45초가 안 됐으면 그 휴대폰에는 보내지 않는다(다른 휴대폰에는 간다). 휴대폰은 세션 화면이 앞에 있는 동안 30초마다 다시 알리고, 화면을 떠나거나 앱이 뒤로 가면 `sessionId: null`을 보낸다. 키를 다시 만들거나 기기를 해제하면 호스트가 그 기록을 바로 지운다.
8. 버튼(`permission.buttons`): Claude·Codex 권한 요청은 허용할 수 있으면 `claude`, 아니면 `none`. Antigravity 카드는 규칙이 붙은 작업이 없거나 `canAllow`가 거짓이면 `none`, 규칙 중 `onceOnly`가 하나라도 있으면 `antigravityOnce`, 아니면 `antigravity`. 질문·계획은 `none`.

**전송.** 호스트는 자기 릴레이 주소(`wss://…` → `https://…/push/v1/send`)로 휴대폰마다 따로 `Authorization: Bearer <hostToken>`과 `{ serverId, handle, payload }`를 보낸다. 요청 시간 제한은 15초이고 리다이렉트는 따르지 않는다(토큰과 봉투가 호스트가 정한 릴레이 밖으로 나가지 않게). 접속 실패와 429·500·502·504는 2초, 8초 뒤 두 번 더 시도하고 `Retry-After`가 더 길면 그것을 쓴다(30초 이하로 자른다). 403·404·410은 그 기기의 핸들을 지우고 더 시도하지 않는다. 그 밖의 거절(예: 릴레이에 FCM 키가 없어 503)은 시도를 멈춘다. 실패는 진단 기록에만 남는다(Mac `os.Logger` 범주 `mobile-remote`, Windows `Trace` `mobile push:`). 핸들·토큰 해시·알림 내용은 기록에 쓰지 않는다. Tailscale 직접 연결로 붙은 휴대폰에도 알림은 릴레이로 간다(호스트가 인터넷의 릴레이에 닿아야 한다).

**폰.**
- 안드로이드 앱이 푸시를 처음 쓰는 것은 푸시를 보내는 호스트와 페어링했을 때다. FCM 자동 초기화는 꺼 둔 채(`firebase.json`) 그때 켠다. 페어링이 바뀌거나 켠 종류를 바꾸거나 토큰이 바뀌면 1초 모아서 한 번 모든 호스트에 다시 준다. 호스트나 릴레이에 닿지 못하면 30초부터 두 배씩 최대 5번 다시 한다.
- FCM 데이터 메시지 `{ v: "1", h, p }`를 앱이 닫혀 있어도 백그라운드 처리기가 받는다. `h`가 어느 페어링의 핸들인지로 페어링을 찾고(봉투 안의 `hostId`는 고르는 데 쓰지 않는다), 그 페어링의 키로 열고, 열 수 없거나 모양이 틀렸거나 그 종류를 껐으면 말없이 버린다.
- 알림 채널: `attention`(권한·질문·계획, 높은 중요도), `work`(완료·실패·로그인 만료). 잠금 화면에서는 **비공개**(`PRIVATE`)라서 제목과 내용은 사용자가 잠금 화면에서 민감한 내용을 가리게 해 둔 경우 보이지 않는다. 알림 문구는 휴대폰 앱의 언어를 따르고, `<`·`&` 같은 글자는 쓴 그대로 보인다.
- 요청 알림의 id는 `<handle>:<id>`, 상태 알림은 `<handle>:<sessionId>:status`다. 실패 뒤의 로그인 만료가 같은 자리를 바꾼다.
- 권한 알림의 버튼은 카드가 정한다: `claude`는 허용·거부, `antigravity`는 이번만 허용·항상 허용·거절, `antigravityOnce`(이번만 허용할 수 있는 명령)는 이번만 허용·거절. 질문·계획·완료 알림에는 버튼이 없다. 버튼은 기존 `POST /m1/sessions/{id}/permission`(Antigravity `always` 포함)으로 앱이 닫혀 있어도 답하고, 호스트가 앱의 카드와 같은 검사를 한다.
  - **잠금 확인**: 휴대폰이 잠겨 있으면 — 비밀번호·PIN·패턴이 걸렸거나 밀어서 푸는 잠금 화면이 떠 있는 것까지 — 호스트에 보내지 않고 알림을 "잠금을 푼 뒤 다시 누르세요"로 바꾸되 버튼은 남긴다. 확인 모듈(`MightyDeviceLock`)이 없거나 모르면 잠긴 것으로 친다.
  - 성공하면 "허용함"/"거절함"이다.
  - 409를 받으면 그 세션을 다시 읽는다. 그 요청이 이미 없으면 "이미 처리됨"(컴퓨터에서 먼저 답함)이다. 아직 있으면(예: Antigravity 설정 파일 오류로 규칙을 넣지 못함), 다른 실패이거나 15초를 넘기면 "응답하지 못했습니다. 눌러서 여세요."다. 이때는 알림을 누르면 앱이 열린다.
- 알림을 누르면 앱이 `/host/<storageId>/session/<sessionId>`(그 호스트의 그 세션 화면)로 간다. 앱이 닫혀 있었으면 뜬 뒤에 간다.
- 안드로이드 13 이상은 처음 한 번만 알림 권한을 묻는다. 설정 화면에 종류별 스위치(기본 모두 켬)와 충돌 보고 스위치(기본 켬)가 있고, 앱의 알림이 시스템에서 꺼져 있으면 그렇다고 알린다. 종류를 바꾸면 호스트에 다시 준다.

**Crashlytics.** 폰 앱의 네이티브 충돌과 JS 오류의 **이름과 스택 프레임 위치만** 보낸다. 오류 메시지는 대화·명령·경로를 인용할 수 있어 버리고(`scrubError`), 사용자 id·`log`·속성은 쓰지 않는다. 치명적이지 않은 JS 오류와 처리되지 않은 Promise 거부만 직접 기록하고, 치명적인 JS 오류는 같은 걸러낸 오류를 앱의 기본 처리기에 넘겨 네이티브 충돌 보고가 대신하게 한다. 수집은 설정(충돌 보고)을 읽은 뒤에만 켠다(`firebase.json`의 자동 수집은 꺼 둔다). 설정을 끄면 아직 보내지 않은 보고도 지운다.

**빌드.** `google-services.json`은 저장소에 없다. `mobile/app.config.js`가 환경 변수 `GOOGLE_SERVICES_JSON`이 가리키는 파일을 넣고, 변수가 없을 때 EAS 빌드는 멈춘다(푸시와 충돌 보고가 조용히 안 되는 앱을 내보내지 않으려고). 릴리즈 도구가 그 변수를 채운다. Firebase 설정이 없는 빌드(테스트·타입 검사)는 푸시 없이 돈다.

### 휴대폰에서 의도적으로 제외한 Mac 기능

로컬 알림(휴대폰의 푸시 알림은 위 "푸시 알림"), 터미널 실행 창 조작, 워크스페이스 추가·이름 변경·제거, 그래프 배치·블록 크기 조절·참조 말풍선, CLI 계정 전환·CLI 업데이트·앱 자체 업데이트·데스크톱 앱 설정, 펫, 다국어, **스타일 등록·승인·취소·재스캔과 마이티 스타일 설정 화면**(데스크톱 앱 전용).

**리셋권(한도 리셋 자격):** 휴대폰은 CLI가 보고하는 `rateLimits` 상태 줄(`resetsAt` 포함)을 그대로 표시한다. 리셋권 자격 조회(cedar_ember·juniper_tide 두 프로그램의 일곱 상태)는 데스크톱 앱 전용이며, 휴대폰에 릴레이 계약 확장으로 전달하는 것은 이후 작업으로 미룬다.

### 기기 관리

휴대폰은 처음 페어링할 때 페어링 키로 인증하고, 호스트가 발급한 **기기 토큰**을 보안 저장소에 넣은 뒤부터는 토큰으로 인증한다. 프레임·거절 사유·등록 제한은 [relay.md](relay.md)의 "기기 토큰" 절을 따른다. Mac 설정의 **모바일 리모트**에 기기 목록이 나오고, 한 대를 해제하거나 **키 다시 만들기**를 누르면 페어링 키가 새로 만들어지고, 기기 목록 전체가 지워진다. 새 키 아래에서는 기존 토큰이 모두 무효이므로 **모든 휴대폰이 QR로 다시 페어링해야 한다**. 토큰을 모르는 구버전 앱도 마찬가지다. 기기 토큰은 관리 기능이지 QR 유출에 대한 방어가 아니다.

## 데스크톱 구현

- `MightyCore/Remote/RelayChannel.swift`: X25519·HKDF·ChaCha20-Poly1305 채널, 호스트 키쌍 파일, 페어링 오퍼.
- `MightyCore/Remote/RelayLink.swift`: 제어 소켓을 새로 여는 규칙(ping 간격, 네트워크 경로 변화 판단). 동작은 [relay.md](relay.md) "재접속".
- `MightyCore/Remote/MobileRemoteService.swift`: 릴레이 제어 소켓과 재접속(ping·네트워크 변화·깨어남·수동), 휴대폰별 데이터 소켓(핸드셰이크·인증·요청 처리 — 릴레이로 온 것이든 직접 온 것이든 같은 `RelayClientConnection`), 직접 연결의 켜고 끄기와 주소 재확인, m1 라우팅, 리비전 대기·알림. 페어링 키는 `<데이터 폴더>/mobile-remote/mobile-remote.key`, 키쌍은 `relay-keypair.json`(모두 소유자만 읽기).
- `MightyCore/Remote/MobileDirect.swift`: Tailscale 직접 연결 — Tailscale 인터페이스 고르기(`select(interfaces:)`: fd7a:115c:a1e0::/48 주소를 가진 `utun`만, 그 인터페이스의 두 범위 주소만), MagicDNS 이름, 끝점 쓰기·읽기, 주소마다 하나씩 그 `utun`에 고정해 여는 WebSocket 리스너(`MobileDirectListener`, 프레임 1 MiB, 동시 연결 36개, 한도 밖 연결은 `forceCancel`), 데이터 소켓 추상화(`RelayFrameSocket`: 릴레이의 `URLSessionWebSocketTask`와 직접 연결의 `NWConnection`).
- Windows: `MightyClaude.Core/Remote/TailscaleAddresses.cs`(같은 규칙, 어댑터 이름 없이), `MobileDirect.cs`(한도·끝점·업그레이드·입장·상태 줄), `MobileDirectListener.cs`(주소마다 `TcpListener`, 함께 36개), `MobileRelayHost.cs`(같은 `Connection`, `direct-` id, 일반 거절, 재확인), `MightyClaude.Core/WindowsFirewallRule.cs`(방화벽 규칙 하나, UAC), WinUI `MainWindow.MobileRemote.cs`·`MainWindow.MobileDirect.cs`.
- `MightyClaude/AppStore+MobileRemote.swift`: 스냅샷·권한·대기열 변화를 리비전으로 바꾸고 명령을 실제 창에 적용하는 브리지.
- 설정 화면의 **모바일 리모트** 절: 스위치, 릴레이 주소, 연결 상태, Tailscale 직접 연결 스위치·포트·상태 줄, QR 코드, 키 다시 만들기, 구버전 앱 허용 스위치, 페어링된 기기 목록(이름·처음/마지막 접속·연결 중·새 기기 표시)과 기기별 해제.
- `MightyCore/Remote/MobileRemoteSupport.swift`: 확장의 순수 규칙(이름·페이지·설정 검증, 상태줄·사용량 변환, 명령 매핑, 마이티 블록 투영과 리비전 요약, 안내형 프롬프트, `/guided`의 관문 판정 `guidedDecision`).
- `MightyCore/Remote/MobileUploadStore.swift`: 첨부 업로드 저장소(0700 폴더·0600 파일, 순서·크기 검증, 기기·실행 창 범위, 단일 사용, 10분 만료).
- `MightyCore/Remote/MobileDeviceRegistry.swift`: 기기 토큰 등록부와 인증 판정(`devices.json`).
- `MightyCore/Remote/MobilePush.swift`: 푸시 알림의 계약과 규칙 — 봉인(`MobilePushSeal`), 메시지·버튼 모양, 창 목록 투영(`MobilePushPane`)과 계획기(`MobilePushPlanner`), 받을 휴대폰 고르기(`MobilePushRecipients`), 릴레이 전송(`MobilePushHTTPTransport`). 보내고 다시 시도하는 일은 `MobileRemoteService.swift`의 `pushObserve`가, 창 목록을 넘기는 일은 앱의 `MightyClaude/AppStore+MobilePush.swift`가 한다. Windows는 `MightyClaude.Core/Remote/MobilePush.cs`(같은 규칙)와 `MobilePushHub.cs`(창 변화를 모아 계획기에 넣고 전송·재시도).
- `MightyCore/Remote/MobileWorkspaceFiles.swift`: 파일 라우트의 경로 거르기, 목록·미리보기 모양, 응답 크기 맞추기, 썸네일(svg는 `MobileHostDelegate.mobileSVGRasterizer`로 앱이 그림).
- `MightyCore/Styles/StylePanelProjection.swift`: `MobileMighty.panel`이 되는 `StylePanel`의 모양과 골든 직렬화. Mac 패널과 같은 함수가 만든다(스타일 엔진 계약: [mighty-styles.md](mighty-styles.md) §5.6·§7.3).
- `MightyCore/Remote/MobileLegacyStyleAdapter.swift`: 번들 Ouroboros의 `mighty.ouroboros`를 같은 투영에서 만드는 레거시 어댑터.
- `MightyCore/Remote/MobileStructure.swift`: `mighty.structure`의 모양·한도와 창별 캐시(`MobileStructureCache`). `MightyStructure.make`의 결과를 그대로 줄인다.

## 모바일 앱

`mobile/README.md`를 참고한다. 화면은 페어링(QR/링크 붙여넣기) → 호스트 → 워크스페이스·세션 목록 → 세션 상세(대화, 권한·질문 카드, 입력창, 중지)이고, 워크스페이스마다 읽기 전용 파일 목록 → 파일 미리보기가 있다. 마이티 보기의 범용 스타일 패널은 `mobile/src/lib/styles.ts`(정규화·뷰 모델)와 `mobile/src/components/guided-panel.tsx`·`guided-action-chip.tsx`가 그린다. 세션 화면의 보기 전환은 대화·블록·구조 셋이고, 파이프라인(구조) 보기는 `mobile/src/lib/structure.ts`(방어적 정규화와 단계 모양·접기 규칙), `mobile/src/lib/pipeline-board.ts`(보드 묶기와 보드·열 이동, Mac `MightyStructure.boards`·`Navigation`과 같은 규칙)와 `mobile/src/components/structure-view.tsx`가 그린다: 보드 하나씩(스타일 없는 창은 요청 하나, 스타일 창은 파이프라인 하나), 맨 위에 ◀ ▶와 목록을 여는 "요청 3 / 7"·"파이프라인 2 / 3", 오래된 보드를 보는 동안 새 보드가 오면 "새 요청"·"새 파이프라인" 배지(누르면 가장 최근 보드로). 그 아래 보드 상태(파이프라인은 이름도), 단계 칩 줄(상태 색, 카드 수, 이어지는 선; 누르면 그 열로), 그리고 열마다 화면 너비 한 쪽씩 옆으로 넘기는 카드 목록. 처음에는 보드의 지금 단계 열이 열리고, 사용자가 다른 열로 옮기기 전까지 지금 단계를 따라간다. 카드는 Mac처럼 겹치지 않게 말한다: 요청 카드는 요청 내용만, 실행 카드는 상태부터, 결과 카드는 실패·중지일 때만 제목, 하위 에이전트 카드는 중첩이면 "… 아래". 실행 중인 카드·하위 에이전트는 실행 색 테두리가 숨 쉬듯 깜박인다(네이티브 애니메이션). 스타일 창의 끝난 요청 카드는 한 장으로 접힌다. 맨 아래에 있을 때만 새 내용을 따라가고, 사용자가 위로 올리면 그대로 둔다. 따라가기는 보드·열마다 따로 기억한다(`FollowMemory`). 고른 보드는 id와 그 보드의 요청들로 들고(새 요청이 와도 그대로), 가장 최근 보드로 돌아오면 놓아서 다시 새 보드를 따라간다.

Tailscale 직접 연결은 `mobile/src/lib/direct.ts`(끝점 거르기·QR 값 읽기)와 `mobile/src/api/relay/transport.ts`(직접 끝점 경주, 릴레이로 넘어가기, `route`)가 맡고, 호스트별 끝점은 `mobile/src/store/hosts.ts`의 `PairedHost.directEndpoints`(이 필드가 없는 예전 항목은 그대로 읽히고, `auth_ok`가 목록을 주면 채워진다)에 저장된다.
