# MightyClaude Mobile

데스크톱 MightyClaude를 휴대폰에서 원격 조종하는 React Native(Expo) 클라이언트입니다.

> **네이티브 다시 빌드 필요** — 이번 라운드에서 `expo-image-picker`, `expo-document-picker`,
> `expo-file-system`이 의존성으로 들어왔습니다. 이미 만들어 둔 개발 빌드나 `android/`·`ios/`
> 산출물은 새 네이티브 모듈을 모르므로, 첨부 기능을 쓰려면 `npx expo prebuild` 뒤
> `npm run android` / `npm run ios`로 **다시 빌드해야 합니다**. JS만 새로고침해도 붙지 않습니다.

- 페어링된 호스트 목록 / 연결 상태 확인
- 작업 공간별 세션 목록 (상태 칩, 권한 대기 배지, 새 창 만들기)
- 세션 상세: 대화 기록, 권한 허용/거부, 질문 답변, 대기열, 메시지 전송/중지
- 호스트가 알려 주는 기능만 추가로: 전송 방식 선택, 대기열 관리, 이름 변경·창 닫기,
  이전 기록 불러오기, 설정 변경, 슬래시 명령, 상태 줄,
  **Mighty 블록 보기**, **파일 첨부** (아래 **호스트 기능(capabilities)**)
- 인증: 처음 한 번만 페어링 키를 쓰고, 이후에는 호스트가 발급한 **기기 토큰**으로 붙습니다
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
3. 앱이 실제로 릴레이에 접속해 `auth_ok`까지 확인한 뒤 비밀을
   **expo-secure-store**(iOS 키체인 / Android Keystore)에 저장합니다. 호스트가 그 자리에서
   **기기 토큰**을 발급하면 토큰만 남기고 페어링 키는 저장하지 않습니다(아래 **기기 토큰**).

호스트를 지우려면 목록에서 **길게 누르기** → 삭제.

> 예전(v1) QR — `host`/`port`/`key` 형식 — 은 더 이상 지원하지 않습니다.
> 스캔하면 `이 QR은 이전 방식입니다. Mac 앱을 업데이트하세요.` 가 뜹니다.
> 저장돼 있던 v1 호스트도 앱 업데이트 후 첫 실행 때 정리되므로 다시 페어링해야 합니다.

### 호스트 목록의 상태 표시

| 표시 | 뜻 |
|---|---|
| `연결됨` | 릴레이·호스트 모두 정상, 인증 완료 |
| `호스트 오프라인` | 릴레이는 붙었지만 Mac이 접속해 있지 않음 (close 4404 / 4410 / 4504) |
| `재페어링 필요` | 페어링 키가 거부됨(`auth_error` `pairing-key`, close 4401), 또는 Mac에서 이 기기를 해제함(`device-revoked`) |
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
| `mighty` | 헤더의 `대화`/`블록` 전환, 요청별 블록 목록, Ouroboros·Paperthin 패널 |
| `attachments` | 입력창의 `＋` 버튼(사진·파일), 첨부 칩, 청크 업로드 |

- 이전 기록은 호스트가 `hasOlder`라고 말할 때만 더 불러오고, `hasMore: false`이거나
  빈 배열(= `before`가 밀려남)이 오면 멈춥니다. 불러오는 동안 롱폴로 들어온 항목과
  **겹치거나 순서가 바뀌지 않습니다**.
- 설정은 실행 중이면 호스트가 `editable: false`로 알려 주고, 앱은 칩을 잠그고 이유를 적어 둡니다.
  그래도 보낸 경우의 409·400은 토스트로 그대로 보여 줍니다.
- 슬래시 명령 중 `action`이 있는 것은 앱이 처리합니다: `model`·`permission`은 설정 선택,
  `rename`은 이름 변경, `clear`·`usage`·`help`는 `/command` 호출(본문이 오면 시트로 표시).
  `action`이 없으면 입력창에 `/이름 `만 넣습니다.
- 프로바이더 표시는 Mac과 **같은 아웃라인**을 브랜드 색으로 그립니다(Claude `#D97757`,
  Codex `#10A37F`, Gemini는 `#4285F4`→`#9B72CB`→`#D96570` 그라디언트를 왼쪽 아래에서
  오른쪽 위로). 아웃라인 데이터는 Mac의 `MightyCore/ProviderMark.swift`와 같은 24×24 경로로
  `src/lib/provider-marks.ts`에 있고, `react-native-svg`로 그립니다.
  **모르는 프로바이더는 아웃라인이 없으므로 중립 칩**으로 둡니다.

### Mighty 블록 보기 (`mighty`)

호스트가 `detail.mighty`를 보내면 세션 헤더에 `대화`/`블록` 칩이 생깁니다.
`블록`은 Mac의 그래프를 **목록**으로 옮긴 것입니다: 요청(run)을 시간순으로 놓고, 그 안의
블록을 종류별 색·표식(`main ●` `agent ◆` `task ▣` `steer ↳` `compact ⤡` `question ?`)과
한국어 이름, 상태 칩, 요약, 소요 시간으로 그립니다. `output`이 있는 블록은 눌러서 펼칩니다.
**계약에 없는 종류·상태는 중립 색으로, 호스트가 보낸 단어 그대로** 그립니다.

보기 방식(`plain`/`mighty`)과 스타일(`cli`/`ouroboros`/`paperthin`)은 헤더의 설정 칩에서
고릅니다. 스타일이 두 가지 이상일 때는 보기 방식을 먼저 고른 뒤 스타일을 고르고, 둘은
**한 번의 `POST …/settings`** 로 함께 나갑니다.

- **Ouroboros 패널**: 현재 단계, 호스트가 알려 준 `next` 스킬 버튼, 나머지 `all`은 `더 보기`.
  `takesText`에 있는 스킬은 입력창의 내용을 함께 보냅니다(그 외에는 스킬만).
  `ready: false`면 무엇이 빠졌는지 적고 버튼을 잠급니다.
- **Paperthin 패널**: 네 도메인을 2×2로, 고른 도메인의 질문과 스킬(이모지, 이름,
  `userInvoked` 👤, `readOnly` 👁)을 보여 줍니다. 추천 스킬은 테두리로 강조하고, 길게 누르면
  설명이 뜹니다. 케이스북 줄(이름·비중·파일)은 **보여 주기만** 합니다(파일 열기는 Mac의 일).
  `installed: false`면 스킬 설치는 Mac에서 한다고 적습니다.
- 두 패널의 버튼은 `POST …/guided {style, skill, text?}`를 부르고, 토스트는 호스트가 돌려준
  `accepted`를 그대로 옮깁니다. **질문(AskUserQuestion) 카드가 떠 있는 동안에는 패널이 비켜
  나고** 질문 카드만 남습니다.

### 파일 첨부 (`attachments`)

입력창 왼쪽 `＋` → `사진 선택`(expo-image-picker) / `파일 선택`(expo-document-picker).
고른 파일은 입력창 위에 칩으로 쌓이고, 칩의 `✕`로 뺍니다.

- **사진 권한은 묻지 않습니다.** 시스템 사진 선택기(iOS PHPicker, Android Photo Picker)는
  고른 항목만 앱에 넘겨 주므로 보관함 접근 권한이 필요 없습니다. 그래서 `app.json`에도
  `NSPhotoLibraryUsageDescription`이 없습니다.
- 한도는 Mac과 같습니다: **파일 8개, 개당 5 MB, 합계 8 MB**. 한도는 **보내기 전에** 확인하고,
  들어가지 못한 파일은 이름과 함께 한국어로 알립니다.
- 크기는 **디스크의 실제 크기**를 씁니다(선택기가 알려 준 값은 디스크에서 읽을 수 없을 때만).
  크기가 0이거나 읽을 수 없는 파일은 보내기 전에 이름과 함께 거절합니다.
- 보내기: `POST …/uploads`(호스트가 `chunkSize`를 알려 줌) → `chunks/{index}`를 0부터 순서대로
  (base64) → `complete`. 칩에 진행률이 뜨고, `취소`를 누르면 열어 둔 업로드를 `cancel` 합니다.
- 한 청크가 실패하면 **최대 3번까지** 간격을 늘려 다시 보냅니다. 다만 413·400·404처럼 다시
  보내도 답이 같을 오류는 바로 멈추고, 호스트의 문장을 파일 이름과 함께 토스트로 보여 줍니다.
- 파일은 **한 번에 한 청크씩** 읽습니다(`expo-file-system`의 `File.open()` + `offset`/`readBytes`).
  5 MB 파일이 통째로 JS 문자열이 되는 일은 없습니다.
- `submit`에는 `complete`가 돌려준 `attachment.id`가 갑니다. `submit`이 실패하면 그 시도에서
  올린 업로드를 **모두 `cancel`** 합니다. 그러지 않으면 실패를 몇 번 되풀이하는 동안 창의
  업로드 자리(창당 16개)가 차서 다시 시도조차 못 하게 됩니다.
- 이미 받은 청크를 다시 보내면 호스트가 409로 답합니다. **재시도에서 받은 409는 "이미 받았다"**로
  보고 다음 청크로 넘어갑니다(첫 시도의 409는 그대로 실패).
- **첨부가 있는 요청은 조정(steer)할 수 없습니다.** 실행 중이면 `다음 요청`만 보이고,
  대기열로 들어간다고 적어 둡니다.
- 화면을 떠나면 진행 중이던 업로드는 취소됩니다.

### 기기 토큰

`docs/relay.md`의 "기기 토큰" 절 그대로입니다.

- 앱은 설치마다 `clientId`(16바이트 난수, base64url)를 한 번 만들어 보안 저장소에 둡니다.
- 처음 페어링할 때 `auth`에 페어링 키와 `clientId`를 함께 보냅니다. 호스트가 `auth_ok`로
  `deviceToken`을 한 번 돌려주면, 그 호스트의 **페어링 키는 지우고** 토큰만 보관합니다.
- 이후 접속은 `clientId` + `deviceToken`으로만 인증합니다(`pairingKey` 없음).
- 한 호스트에 대해 **페어링 키 인증은 한 번에 하나만** 나갑니다. 호스트는 페어링 키에 토큰을
  딱 한 번 돌려주므로, 목록의 도달 확인과 화면의 터널이 동시에 인증하면 토큰이 두 개 만들어지고
  하나는 버려집니다. 뒤에 선 연결은 앞의 인증이 토큰을 저장할 때까지 기다렸다가 그 토큰으로
  인증합니다. 이미 연결된 터널이 있으면 도달 확인은 소켓을 새로 열지 않고 그 터널을 씁니다.
- 토큰을 모르는 구버전 호스트는 예전처럼 페어링 키로 계속 붙습니다.
- 토큰·키는 어디에도 기록하지 않습니다.

#### `auth_error`의 `reason`

| `reason` | 최종? | 앱의 행동 |
| --- | --- | --- |
| `pairing-key` | 예 | 저장된 비밀을 지우고 **재페어링 필요**로 표시 |
| `device-revoked` | 예 | 같음. "Mac에서 이 기기를 해제했다"는 문장을 그대로 보여 줌 |
| `device-conflict` | 아니오 | 그 호스트 전용 `clientId`(16바이트 난수)를 새로 만들어 보안 저장소에 두고 페어링 키로 **한 번만** 다시 시도. 두 번째 충돌은 평범한 오류 |
| `device-limit` | 아니오 | 비밀을 그대로 두고 "Mac의 기기 목록이 가득 찼거나…"를 안내 |
| `legacy-refused` | 아니오 | 이 앱은 언제나 `clientId`를 보내므로 해당 없음. 일반 오류로 처리 |
| `malformed`·**모르는 값** | 아니오 | 비밀을 **절대 지우지 않고** 일반 오류로 알린 뒤 평소대로 재시도 |

새 QR로 다시 페어링하면 새 토큰으로 바뀌고, 호스트를 지우면 토큰도 함께 지워집니다. 호스트 전용
`clientId`는 호스트를 지울 때만 함께 지웁니다(비밀만 지울 때는 남깁니다 — 같은 Mac에 다시
페어링할 때 같은 기기로 보여야 하기 때문입니다).

---

## 6. 제한 사항

- **로컬 터미널 창**(`terminal: true`)에는 모바일에서 명령을 보낼 수 없습니다. 읽기 전용입니다.
- 롱폴이 주는 기록은 **최근 80건**입니다. 그보다 오래된 기록은 `history` 기능이 있는 호스트에서만
  위로 스크롤해 불러올 수 있습니다.
- **로컬** 작업 공간에서는 새 창 종류에 `셸`이 보이지 않습니다. 휴대폰에서 쓸 수 없는 창이라
  호스트도 409로 거절합니다.
- 릴레이를 공개 인터넷에 둔다면 반드시 TLS(`wss://`) 뒤에 두세요. 릴레이는 비밀을 알지 못하지만, 메타데이터는 보입니다.
- **iOS의 평문 `ws://`는 로컬 네트워크에서만 됩니다.** App Transport Security 설정은
  `NSAllowsLocalNetworking`뿐이고 `NSAllowsArbitraryLoads`는 없습니다. 그래서 LAN 안의
  `ws://192.168.…`·`ws://<이름>.local`은 그대로 되고, 공개 릴레이는 `wss://`여야 합니다.
  로컬이 아닌 `ws://` 주소로 페어링하려 하면 연결이 조용히 끊기는 대신 그 자리에서 한국어로
  알려 줍니다. Android는 예전대로 평문을 허용합니다(`usesCleartextTraffic`).
- 첨부는 에이전트 창에만 붙일 수 있습니다. 셸 창에는 `＋` 버튼이 없습니다.

### 휴대폰에 두지 않기로 한 Mac 기능

`docs/mobile-remote.md`의 "휴대폰에서 의도적으로 제외한 Mac 기능" 그대로이며, 앞으로도
넣지 않습니다:

- 알림(푸시·로컬) — 앱이 백그라운드면 폴링도 멈춥니다
- 터미널 실행 창 조작
- 작업 공간 추가·이름 변경·제거
- 그래프 배치·블록 크기 조절·참조 말풍선 (블록은 **목록**으로만 봅니다)
- CLI 계정 전환·CLI 업데이트·앱 자체 업데이트·앱 설정
- 펫, 다국어 (화면 문구는 한국어 하나입니다)

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
             composer.tsx(전송 방식 + 슬래시 명령 + 첨부 칩), command-list.tsx, queued-list.tsx
             session-header.tsx(설정 칩), status-line-view.tsx, provider-mark.tsx
             sheets.tsx(선택·입력·확인·본문·목록 모달), permission-card.tsx(한 번에 한 질문)
             mighty-blocks.tsx(요청·블록 목록), ouroboros-panel.tsx, paperthin-panel.tsx
  hooks/     use-long-poll.ts (AbortController + 포커스 + notify 연동)
             use-capabilities.ts (호스트별 /m1/info 캐시)
             use-attachments.ts (picker → 한도 → 청크 업로드 → uploadId)
  lib/       pairing.ts, merge.ts, device.ts
             capabilities.ts, history.ts, questionnaire.ts, status-line.ts, commands.ts
             mighty.ts(블록·패널 파싱과 라벨), uploads.ts(한도·청크 계획·업로드 진행)
             file-slices.ts(expo-file-system 범위 읽기), device-token.ts, host-secrets.ts
             provider-marks.ts(프로바이더 마크 24×24 아웃라인)
  store/     hosts.ts(secure store), live.ts(상태·기능·명령 캐시), toast.ts
  theme/     팔레트(밝게/어둡게) / 간격 / 상태·프로바이더·블록 라벨과 색
```

새 기능의 순수 로직은 모두 `src/lib`에 있고 `src/__tests__`에서 직접 테스트합니다:
기록 병합(`history`), 질문 상태 기계(`questionnaire`), 기능 게이팅(`capabilities`),
상태 줄 정리(`status-line`), 슬래시 명령 필터(`commands`), Mighty 페이로드 파싱(`mighty`),
첨부 한도·청크·재시도(`uploads`), 기기 토큰 상태 기계와 보관 규칙(`device-token`),
그리고 새 라우트의 요청 모양과 오류 매핑(`client`).

`src/lib`의 모듈은 파일 시스템도 소켓도 직접 건드리지 않습니다. 업로드는 전송·읽기 함수를
주입받고, 보안 저장소는 `SecretStore` 인터페이스로만 다루므로 테스트가 메모리 구현으로
같은 규칙을 검사합니다.

---

## 8. iOS 빌드

- `app.json`: `ios.bundleIdentifier`는 Android `package`와 같은 `dev.mightyclaude.mobile`,
  `ios.supportsTablet: true`, 사용 설명은 카메라(QR) 하나뿐입니다. 사진은 시스템 선택기로만
  고르므로 `NSPhotoLibraryUsageDescription`은 넣지 않습니다(`expo-image-picker` 플러그인의
  `photosPermission: false`).
- `eas.json`에 `development` / `preview` / `production` 프로필이 있습니다.

```bash
npx expo config --type public   # ios.bundleIdentifier 확인
npx eas build --profile development --platform ios
```

> **자격 증명은 넣지 않았습니다.** Apple Team ID, 인증서·프로비저닝 프로파일, App Store Connect
> 앱 ID, `submit` 설정은 계정 소유자만 만들 수 있으므로 비워 두었습니다. 처음 빌드할 때
> EAS가 물어보는 대로 채우거나, `eas.json`의 `build.*.ios.credentialsSource`와
> `submit.production`에 소유자가 직접 적어 넣으세요.
