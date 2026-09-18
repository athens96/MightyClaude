# MightyClaude Mobile

데스크톱 MightyClaude를 휴대폰에서 원격 조종하는 React Native(Expo) 클라이언트입니다.
iOS / Android 모두 **Expo Go**에서 바로 실행됩니다. (커스텀 네이티브 모듈 없음)

- 페어링된 호스트 목록 / 연결 상태 확인
- 작업 공간별 세션 목록 (상태 칩, 권한 대기 배지, 새 창 만들기)
- 세션 상세: 대화 기록, 권한 허용/거부, 질문 답변, 대기열, 메시지 전송/중지
- 호스트가 알려 주는 기능만 추가로: 전송 방식 선택, 대기열 관리, 이름 변경·창 닫기,
  이전 기록 불러오기, 설정 변경, 슬래시 명령, 상태 줄 (아래 **호스트 기능(capabilities)**)
- 연결: **릴레이 + 종단 간 암호화** (자세한 계약은 [`docs/relay.md`](../docs/relay.md))
- 터널 안의 요청/응답은 기존 `m1` 프로토콜 그대로입니다 ([`docs/mobile-remote.md`](../docs/mobile-remote.md))
- 화면은 시스템 설정을 따라 밝게/어둡게 바뀝니다(`useColorScheme`). 어두운 화면은 예전 그대로입니다.

---

## 1. 실행 방법

```bash
cd mobile
npm install
npx expo start
```

터미널에 뜬 QR 코드를 휴대폰으로 스캔합니다.

- **iOS**: 기본 카메라 앱으로 QR 스캔 → Expo Go에서 열기
- **Android**: Expo Go 앱의 *Scan QR code*

> 개발용 PC와 휴대폰이 같은 네트워크에 있어야 Expo 번들을 받을 수 있습니다.
> 다른 네트워크라면 `npx expo start --tunnel`을 사용하세요.

---

## 2. 연결 방식 (VPN 불필요)

휴대폰과 Mac이 **각자 릴레이 서버에 바깥으로 접속**해서 만납니다.
포트 개방도, VPN도 필요 없습니다.

```
휴대폰  ──wss──▶  릴레이  ◀──wss──  Mac
        └─────── 종단 간 암호화 ───────┘
```

- 릴레이는 `serverId`로 두 소켓을 이어주는 파이프일 뿐이고, 오가는 내용은 보지 못합니다.
- 키 교환은 X25519 + HKDF-SHA256, 프레임 암호화는 ChaCha20-Poly1305입니다.
- 앱은 페어링 때 받은 호스트 공개키와 릴레이가 전달한 `serverKey`가 **같은지 확인한 뒤에만** 인증 정보를 보냅니다.
- 유지용 `ping`/`pong`이 20초마다 오가고, 60초 무응답이면 끊고 다시 붙습니다.
  재접속 간격은 1.5초에서 2배씩 최대 30초이며, 앱이 전면으로 돌아오면 즉시 재시도합니다.

Mac 쪽 준비는 **설정 → 모바일 리모트**에서 스위치를 켜고 릴레이 주소를 지정하는 것뿐입니다.

---

## 3. 페어링

Mac의 **설정 → 모바일 리모트**가 아래 형식(v2)의 페어링 문자열을 QR로 표시합니다.

```
mightyclaude://pair?v=2&sid=<serverId>&pk=<base64url 공개키 32B>&relay=<wss://host:port>&key=<pairingKey>&name=<이름>
```

앱에서:

1. 첫 화면 하단 **+ 호스트 추가**
2. **QR 스캔** 탭에서 카메라 권한을 허용하고 Mac 화면의 QR을 비춥니다.
   - 카메라를 쓸 수 없으면 **링크 붙여넣기** 탭에 `mightyclaude://pair?v=2&…` 링크를 그대로 붙여넣습니다.
3. 앱이 실제로 릴레이에 접속해 `auth_ok`까지 확인한 뒤, 페어링 키를
   **expo-secure-store**(iOS 키체인 / Android Keystore)에 저장합니다.

호스트를 지우려면 목록에서 **길게 누르기** → 삭제.

> 예전(v1) QR — `host`/`port`/`key` 형식 — 은 더 이상 지원하지 않습니다.
> 스캔하면 `이 QR은 이전 방식입니다. Mac 앱을 업데이트하세요.` 가 뜹니다.
> 저장돼 있던 v1 호스트도 앱 업데이트 후 첫 실행 때 정리되므로 다시 페어링해야 합니다.

### 호스트 목록의 상태 표시

| 표시 | 뜻 |
|---|---|
| `연결됨` | 릴레이·호스트 모두 정상, 인증 완료 |
| `호스트 오프라인` | 릴레이는 붙었지만 Mac이 접속해 있지 않음 (close 4404 / 4410 / 4504) |
| `재페어링 필요` | 페어링 키가 거부됨 (`auth_error`, 또는 close 4401) |
| `릴레이 연결 안 됨` | 릴레이 주소 자체에 닿지 못함 |

---

## 4. 사용 중 참고

- 화면이 열려 있는 동안만 롱폴링(`?since=&wait=10`)합니다. 다른 화면으로 나가면 요청이 중단되어 배터리를 아낍니다.
- 호스트가 `notify`를 보내면 대기 중인 롱폴을 기다리지 않고 **즉시 다시 요청**합니다.
  뒤늦게 도착한 예전 응답은 리비전으로 걸러집니다.
- 한 호스트당 암호화 연결은 **하나**이며, 열려 있는 화면들이 공유합니다(참조 카운트).
- 동시 요청은 최대 8개이고 나머지는 대기열에 들어갑니다.
- 메시지 전송 결과는 토스트로 표시됩니다: `전송` / `실행 중인 작업에 전달` / `대기열에 추가`.
  호스트가 **실제로 한 일**을 그대로 보여 주므로, `바로 전달`을 눌러도 조정할 수 없는 창이면 `대기열에 추가`가 뜹니다.
- 전송 가능한 텍스트는 최대 32KiB입니다.
- **질문 카드**는 한 번에 한 질문씩 보여 줍니다(`질문 2/3`, `이전`/`다음`).
  고른 답은 카드가 화면에 있는 동안에만 컴포넌트 상태로 남고, 호스트가 그 요청을 거두면
  카드와 함께 **사라집니다**. 저장하지 않으므로 다시 물어보면 처음부터 고르게 됩니다.

---

## 5. 호스트 기능(capabilities)

앱은 페어링된 호스트마다 `/m1/info`를 한 번 읽고 `capabilities` 목록을 캐시합니다.
목록에 이름이 **있을 때만** 그 기능이 화면에 나타납니다. 목록이 없는(=업데이트 전) Mac에서는
앱이 예전과 똑같이 동작합니다.

| 이름 | 앱에서 보이는 것 |
|---|---|
| `submit-mode` | 실행 중일 때 `바로 전달`(steer) / `다음 요청`(queue) 두 버튼 |
| `queue` | 대기열 항목 `삭제`, 실행 중이 아닐 때 `다음 실행` |
| `pane` | 헤더 `⋯` 메뉴의 이름 변경(1~80자) · 창 닫기(되돌릴 수 없음, 닫으면 뒤로 이동) |
| `history` | 위로 스크롤하면 `entries?before=…&limit=50`으로 이전 기록을 이어 붙임 |
| `settings` | 헤더의 모델 / 권한 모드 / 사고 강도(+보기 방식·Mighty 스타일) 선택 |
| `commands` | 입력창에서 `/`를 치면 뜨는 슬래시 명령 목록 |
| `status` | 상태 줄(최대 6줄)과 사용량 막대 |
| `mighty`, `attachments` | 아직 화면에 쓰지 않습니다 (다음 라운드) |

- 이전 기록은 호스트가 `hasOlder`라고 말할 때만 더 불러오고, `hasMore: false`이거나
  빈 배열(= `before`가 밀려남)이 오면 멈춥니다. 불러오는 동안 롱폴로 들어온 항목과
  **겹치거나 순서가 바뀌지 않습니다**.
- 설정은 실행 중이면 호스트가 `editable: false`로 알려 주고, 앱은 칩을 잠그고 이유를 적어 둡니다.
  그래도 보낸 경우의 409·400은 토스트로 그대로 보여 줍니다.
- 슬래시 명령 중 `action`이 있는 것은 앱이 처리합니다: `model`·`permission`은 설정 선택,
  `rename`은 이름 변경, `clear`·`usage`·`help`는 `/command` 호출(본문이 오면 시트로 표시).
  `action`이 없으면 입력창에 `/이름 `만 넣습니다.
- 프로바이더 표시는 브랜드 색을 씁니다(Claude `#D97757`, Codex `#10A37F`,
  Gemini `#4285F4`→`#9B72CB`→`#D96570`). 이 앱에는 벡터 렌더러(`react-native-svg`)가 없고
  네이티브 의존성을 늘리지 않기로 했으므로, Mac의 아웃라인 대신 **브랜드 색 칩**으로 그립니다.

---

## 6. 제한 사항

- **로컬 터미널 창**(`terminal: true`)에는 모바일에서 명령을 보낼 수 없습니다. 읽기 전용입니다.
- 롱폴이 주는 기록은 **최근 80건**입니다. 그보다 오래된 기록은 `history` 기능이 있는 호스트에서만
  위로 스크롤해 불러올 수 있습니다.
- 파일 첨부·이미지 업로드는 아직 없습니다(프로토콜 타입만 준비돼 있습니다).
- **로컬** 작업 공간에서는 새 창 종류에 `셸`이 보이지 않습니다. 휴대폰에서 쓸 수 없는 창이라
  호스트도 409로 거절합니다.
- 릴레이를 공개 인터넷에 둔다면 반드시 TLS(`wss://`) 뒤에 두세요. 릴레이는 비밀을 알지 못하지만, 메타데이터는 보입니다.
- 푸시 알림은 없습니다. 앱이 백그라운드면 폴링이 멈춥니다.

---

## 7. 개발

```bash
npm run typecheck   # tsc --noEmit
npm test            # jest (암호·페어링·전송 계층 단위 테스트)
```

테스트는 네이티브 모듈 없이 돕니다. `expo-crypto`는 Node의 webcrypto로 목되고,
전송 테스트는 `ws`로 만든 **인프로세스 가짜 릴레이+호스트**를 상대로 실제 핸드셰이크를 수행합니다.

주요 구조:

```
app/                     expo-router 화면
  _layout.tsx            루트 스택 + 시스템 테마 연동 + crypto 폴리필
  index.tsx              호스트 목록
  pair.tsx               QR/수동 페어링 (v2)
  host/[hostId]/index.tsx                작업 공간 + 세션 목록
  host/[hostId]/session/[sessionId].tsx  세션 상세 (기록 페이징·설정·명령·창 메뉴)
src/
  api/       client.ts(m1 클라이언트 + 연결 풀), types.ts(프로토콜 타입)
  api/relay/ crypto.ts(X25519·HKDF·ChaCha20-Poly1305·프레임)
             transport.ts(RelayConnection: 핸드셰이크·인증·요청/알림·재접속)
             random.ts(expo-crypto 난수 + globalThis.crypto 폴리필)
             foreground.ts(AppState → 즉시 재접속)
  components/ UI 컴포넌트
             composer.tsx(전송 방식 + 슬래시 명령), command-list.tsx, queued-list.tsx
             session-header.tsx(설정 칩), status-line-view.tsx, provider-mark.tsx
             sheets.tsx(선택·입력·확인·본문 모달), permission-card.tsx(한 번에 한 질문)
  hooks/     use-long-poll.ts (AbortController + 포커스 + notify 연동)
             use-capabilities.ts (호스트별 /m1/info 캐시)
  lib/       pairing.ts, merge.ts, device.ts
             capabilities.ts, history.ts, questionnaire.ts, status-line.ts, commands.ts
  store/     hosts.ts(secure store), live.ts(상태·기능·명령 캐시), toast.ts
  theme/     팔레트(밝게/어둡게) / 간격 / 상태·프로바이더 라벨
```

새 기능의 순수 로직은 모두 `src/lib`에 있고 `src/__tests__`에서 직접 테스트합니다:
기록 병합(`history`), 질문 상태 기계(`questionnaire`), 기능 게이팅(`capabilities`),
상태 줄 정리(`status-line`), 슬래시 명령 필터(`commands`), 그리고 새 라우트의 요청 모양과
오류 매핑(`client`).
