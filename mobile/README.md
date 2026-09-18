# MightyClaude Mobile

데스크톱 MightyClaude를 휴대폰에서 원격 조종하는 React Native(Expo) 클라이언트입니다.
iOS / Android 모두 **Expo Go**에서 바로 실행됩니다. (커스텀 네이티브 모듈 없음)

- 페어링된 호스트 목록 / 연결 상태 확인
- 작업 공간별 세션 목록 (상태 칩, 권한 대기 배지, 새 창 만들기)
- 세션 상세: 대화 기록, 권한 허용/거부, 질문 답변, 대기열, 메시지 전송/중지
- 연결: **릴레이 + 종단 간 암호화** (자세한 계약은 [`docs/relay.md`](../docs/relay.md))
- 터널 안의 요청/응답은 기존 `m1` 프로토콜 그대로입니다 ([`docs/mobile-remote.md`](../docs/mobile-remote.md))

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
- 전송 가능한 텍스트는 최대 32KiB입니다.

---

## 5. 제한 사항

- **로컬 터미널 창**(`terminal: true`)에는 모바일에서 명령을 보낼 수 없습니다. 읽기 전용입니다.
- 전송 기록은 서버가 주는 **최근 80건**만 표시됩니다. 과거 기록 무한 스크롤은 없습니다.
- 파일 첨부, 이미지 업로드, 세션 삭제/이름 변경은 지원하지 않습니다.
- 릴레이를 공개 인터넷에 둔다면 반드시 TLS(`wss://`) 뒤에 두세요. 릴레이는 비밀을 알지 못하지만, 메타데이터는 보입니다.
- 푸시 알림은 없습니다. 앱이 백그라운드면 폴링이 멈춥니다.

---

## 6. 개발

```bash
npm run typecheck   # tsc --noEmit
npm test            # jest (암호·페어링·전송 계층 단위 테스트)
```

테스트는 네이티브 모듈 없이 돕니다. `expo-crypto`는 Node의 webcrypto로 목되고,
전송 테스트는 `ws`로 만든 **인프로세스 가짜 릴레이+호스트**를 상대로 실제 핸드셰이크를 수행합니다.

주요 구조:

```
app/                     expo-router 화면
  _layout.tsx            루트 스택 + 다크 테마 + crypto 폴리필
  index.tsx              호스트 목록
  pair.tsx               QR/수동 페어링 (v2)
  host/[hostId]/index.tsx                작업 공간 + 세션 목록
  host/[hostId]/session/[sessionId].tsx  세션 상세
src/
  api/       client.ts(m1 클라이언트 + 연결 풀), types.ts(프로토콜 타입)
  api/relay/ crypto.ts(X25519·HKDF·ChaCha20-Poly1305·프레임)
             transport.ts(RelayConnection: 핸드셰이크·인증·요청/알림·재접속)
             random.ts(expo-crypto 난수 + globalThis.crypto 폴리필)
             foreground.ts(AppState → 즉시 재접속)
  components/ UI 컴포넌트
  hooks/     use-long-poll.ts (AbortController + 포커스 + notify 연동)
  lib/       pairing.ts, merge.ts, device.ts
  store/     hosts.ts(secure store), live.ts(상태 캐시), toast.ts
  theme/     색상 / 간격 / 상태 라벨
```
