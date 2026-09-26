# 모바일 온보딩 빈틈 — 정리

## 요약

v4 폰 앱은 개발용으로 쓰기에 일곱 군데가 비어 있었습니다. 아이콘과 스플래시가
Expo 기본 그림이었고, Mac을 페어링하지 않은 상태에서는 빈 화면만 나왔고, Mac
설정 화면에도 연결 순서가 없었습니다. 릴레이 기본 주소와 호스팅 방법이 없었고,
Mighty 블록 화면은 Mac 창이 Mighty 모드일 때만 열렸고, 폰에서 보낸 채팅이 연
창에 도착하는지 끝까지 확인하는 테스트도 없었습니다. 이 문서는 빈틈마다 무엇이
없었는지, 무엇을 바꿨는지, 어떤 검사가 그것을 증명하는지 적습니다. 마지막 표는
실제 폰에서 사용자가 직접 확인할 항목입니다.

---

## [icon] 앱 아이콘

### 무엇이 없었나
`mobile/assets/images/`의 `icon.png`, `android-icon-foreground.png`,
`android-icon-monochrome.png`, `favicon.png`가 Expo 템플릿 그대로였습니다.
데스크톱 너구리 그림(`assets/icons/mightyclaude.png`)과 연결된 것이 없었습니다.

### 무엇을 바꿨나
- `scripts/generate-phone-assets.py` — 원본 한 장(`assets/icons/mightyclaude.png`)에서
  모든 폰 이미지를 만드는 생성기. 배경색은 `#0d0d0f`.
  - `icon.png`: 1024×1024, 알파 없음(RGB), `#0d0d0f` 위에 너구리.
  - `android-icon-foreground.png`: 512×512 RGBA, 너구리를 66% 안전 영역 안에 배치.
  - `android-icon-monochrome.png`: 512×512 RGBA, 흰색 실루엣.
  - `favicon.png`: 48×48.
- `mobile/assets/images/asset-sources.json` — 원본 경로와 원본 sha256, 출력별 크기·모드·sha256 기록.
- 쓰이지 않던 템플릿 그림 `android-icon-background.png`는 지웠습니다. 배경은 `app.json`의 색으로 칠합니다.
- `mobile/app.json`의 `android.adaptiveIcon.backgroundColor`는 `#0d0d0f`.

### 어떤 검사가 증명하나
`cd mobile && npx jest src/__tests__/app-assets.test.ts` — PNG 헤더로 파일별 크기와
색 형식(iOS 아이콘은 불투명)을 확인하고, `asset-sources.json`의 원본 sha256이 현재
`assets/icons/mightyclaude.png`와 같은지, 디스크의 출력 파일이 생성기가 기록한 sha256과
같은지, Expo 템플릿 그림의 해시가 하나도 남지 않았는지, `app.json`이 생성된 그림과
`#0d0d0f` 배경만 가리키는지 확인합니다.

---

## [splash] 스플래시

### 무엇이 없었나
`splash-icon.png`가 Expo 템플릿의 빈 그림이라 첫 실행에 너구리가 보이지 않았습니다.

### 무엇을 바꿨나
- 같은 생성기(`scripts/generate-phone-assets.py`)가 `mobile/assets/images/splash-icon.png`를
  너구리로 다시 만듭니다.
- `mobile/app.json`의 `expo-splash-screen` 플러그인은 배경 `#0d0d0f`, 이미지
  `./assets/images/splash-icon.png`로 가운데에 너구리를 둡니다.

### 어떤 검사가 증명하나
`cd mobile && npx jest src/__tests__/app-assets.test.ts`의
출력 sha256 대조와 `app.json` 항목(스플래시 이미지·배경색). 화면에서의 모습은 아래 체크리스트 1번.

---

## [phone-guide] 폰 연결 안내

### 무엇이 없었나
페어링된 Mac이 없으면 호스트 목록의 빈 상태 문구("페어링된 호스트가 없습니다")만
나왔습니다. 무엇을 어떤 순서로 해야 하는지 알려주는 화면이 없었습니다.

### 무엇을 바꿨나
- `mobile/src/lib/onboarding.ts` — 네 단계(`CONNECT_STEPS`: Mac 설정 열기 → 릴레이
  주소 입력 → QR 확인 → 스캔 또는 링크 붙여넣기)와 `needsOnboarding(hostCount)`.
- `mobile/app/connect.tsx` — 번호 붙은 네 단계를 보여 주고 `QR 스캔 또는 링크 붙여넣기`
  버튼으로 기존 `/pair` 화면에 넘깁니다.
- `mobile/app/index.tsx` — 저장소를 읽은 뒤 호스트가 0개면 `/connect`로 바로 이동하고,
  호스트가 있으면 지금처럼 목록을 보여 주며 `연결 안내 보기` 버튼으로 안내에 돌아갈 수 있습니다.
- `mobile/app/_layout.tsx` — `connect` 화면을 모달로 등록.
- 문구는 공용 키 `phone.connect.*`, `phone.hosts.guide`로 `locales/ko.json`,
  `locales/en.json`과 `mobile/src/locales/`에 추가.

### 어떤 검사가 증명하나
- `cd mobile && npm run typecheck && npx jest src/__tests__/onboarding.test.ts` — 단계가
  정확히 넷이고 순서가 맞는지, 모든 단계 키가 ko·en 모두에서 빈 문자열이 아닌지,
  `needsOnboarding(0)`은 참, `needsOnboarding(1)`은 거짓인지.
- `node scripts/check-locales.js --check` — ko·en 키 누락 없음.

---

## [mac-guide] Mac 설정 화면 안내

### 무엇이 없었나
Mac의 모바일 리모트 설정에는 릴레이 입력칸과 (연결된 뒤의) QR만 있었고, 폰과
연결하는 순서는 적혀 있지 않았습니다.

### 무엇을 바꿨나
- `native/macos/Sources/MightyClaude/MobileRemoteSettingsView.swift` — 릴레이 입력칸
  바로 아래에 번호 붙은 네 단계(`connectionGuide`, 접근성 id `settings-mobile-guide`)를
  넣었습니다. 릴레이 주소가 비었거나 잘못됐을 때 QR 대신 안내 문구를 보이는 기존
  동작은 그대로입니다.
- 문구 키 `settings.mobileRemote.guide.step1`~`step4`를
  `native/macos/Sources/MightyCore/Resources/Locales/ko.json`, `en.json`에 추가.

### 어떤 검사가 증명하나
- `bash scripts/test-native-macos.sh --scratch-path /tmp/mc-mobile-onboarding --filter 'MobileRemote'`
- `node scripts/check-locales.js --check`
- 화면 표시는 아래 체크리스트 9번.

---

## [relay] 기본 릴레이 주소와 호스팅

### 무엇이 없었나
Mac의 릴레이 주소 기본값이 비어 있었고, 저장소에는 릴레이를 어디에 띄울지에 대한
방법이 없었습니다. 같은 Wi-Fi 밖, 모바일 데이터에서는 폰이 Mac에 닿을 길이 없었습니다.

### 무엇을 바꿨나
- `native/macos/Sources/MightyCore/Remote/MobileRemoteModels.swift` —
  `MobileWire.defaultRelayURL` 상수를 추가했습니다. **이번 작업에서는 빈 값으로 나갑니다.**
  사용자가 `docs/relay-oracle.md`대로 자기 릴레이를 배포한 뒤, 이 한 줄에 주소를 넣는
  것으로 기본값이 생깁니다. 사용자가 입력한 릴레이 주소가 항상 기본값보다 우선합니다.
- `MobileRemoteSettings.effectiveRelayURL`(사용자 값, 없으면 기본값, 둘 다 없으면 nil)을
  두고, `MobileRemoteService`의 연결·재연결·페어링 QR과 설정 화면의 안내 문구가 모두 이
  값을 읽도록 바꿨습니다. 그래서 상수 한 줄만 채우면 실제로 그 릴레이로 연결됩니다.
  기본값이 비어 있는 지금은 동작이 이전과 같습니다.
- `relay/deploy/oracle/` — Oracle Cloud Always Free(arm64)용 배포 묶음.
  `compose.yaml`은 기존 `relay/Dockerfile`을 linux/arm64로 빌드하고 Caddy로 앞을 막아
  DuckDNS 이름에 자동 HTTPS를 붙입니다. `Caddyfile`, `setup.sh`(Docker 설치, OS
  방화벽 80·443 개방, 스택 시작) 포함. 릴레이 코드와 프로토콜은 바꾸지 않았습니다.
- `docs/relay-oracle.md` — 가입부터 `https://<이름>.duckdns.org/healthz`가 `ok`를
  돌려줄 때까지의 한국어 단계별 안내. 계정 id, 키, 토큰, IP는 저장소에 없습니다.

### 어떤 검사가 증명하나
`cd relay/deploy/oracle && RELAY_DOMAIN=example.duckdns.org docker compose -f compose.yaml config -q`,
`cd relay && docker build --platform linux/arm64 -t mightyclaude-relay:check . && npm test`,
그리고 `MobileRemote` Swift 테스트의 `effectiveRelay` 확인(사용자 값 우선, 없으면 기본값,
둘 다 없으면 연결 안 함). 모바일 데이터 연결은 배포 뒤 체크리스트 7번.

---

## [mighty-default] Mighty 블록 화면을 기본으로

### 무엇이 없었나
`MobileRemoteSupport.sendsMighty`가 `kind != "shell" && viewMode == "mighty"`여서,
Mac 창이 Mighty 모드일 때만 폰에 블록 데이터가 갔습니다. 폰 에이전트 화면도
`session.agentViewMode === 'mighty'`일 때만 블록을 열었습니다.

### 무엇을 바꿨나
- `native/macos/Sources/MightyCore/Remote/MobileRemoteSupport.swift` — `sendsMighty`를
  `kind != "shell"`로. 셸이 아닌 모든 창이 Mighty 데이터를 보냅니다. 폰이 Mac 창의
  보기 모드를 바꾸지는 않으며, 최신 20개 실행 제한(`MobileWire.mightyRuns = 20`)과
  블록 출력·요약 제한은 그대로입니다.
- `mobile/src/lib/mighty.ts` — `defaultView(mighty)`: 데이터가 있으면 `blocks`, 없으면 `log`.
- `mobile/app/host/[hostId]/session/[sessionId].tsx` — 기본 보기를 `defaultView`로.
  대화/블록 전환 칩과 입력창은 그대로 있습니다.

### 어떤 검사가 증명하나
- `bash scripts/test-native-macos.sh --scratch-path /tmp/mc-mobile-onboarding --filter 'MobileRemote'` —
  `MobileRemoteTests.sendsMightyIsTrueForEveryNonShellKindRegardlessOfViewMode`,
  `MobileRemoteTests.sendsMightyIsFalseForShellRegardlessOfViewMode`,
  `MobileRemoteExtensionTests.runsCarryTheNewestTwentyWithTheirGuidedTitles`.
- `cd mobile && npx jest src/__tests__/mighty.test.ts` — `default session view` 묶음
  (데이터가 있으면 blocks, 셸이면 log, Mac 창이 plain이어도 blocks).

---

## [chat] 폰 채팅이 연 창에 도착하는지

### 무엇이 없었나
실제 릴레이를 거치는 기존 테스트는 연결·암호화·기기 해제만 확인했습니다. 폰에서
보낸 요청이 여러 창 가운데 폰이 연 창에만 들어가는지 끝까지 확인하는 테스트는
없었습니다.

### 무엇을 바꿨나
- `native/macos/Tests/MightyCoreTests/RelayIntegrationTests.swift` —
  - `RecordingHost`: 창 두 개(`pane-a`, `pane-b`)를 가진 가짜 Mac. 폰이 보낸
    `mobileSubmit`을 모두 기록하고 `"started"`를 돌려줍니다. 기존 `StaticHost`의
    동작은 바꾸지 않고 상속해서 두 메서드만 덮어씁니다.
  - `phoneChatRequestReachesOnlyThePaneItHasOpen`: 테스트 안에서 띄운 진짜 Node
    릴레이를 통해 폰이 페어링하고, 상태에서 창 두 개를 확인한 뒤 `pane-b`에
    `"hello"`를 보냅니다. 폰은 202와 `accepted: "started"`를 받아야 하고, Mac은
    `pane-b`/`"hello"` 한 건만 기록해야 하며 `pane-a`에는 아무것도 없어야 합니다.
- 제품 코드는 바꾸지 않았습니다. 요청 경로는 기존 `MobileRemoteService`의 `submit` 라우트입니다.

### 어떤 검사가 증명하나
`bash scripts/test-native-macos.sh --scratch-path /tmp/mc-mobile-onboarding --filter 'RelayIntegration'`
(`relay/dist/server.js`가 있어야 합니다 — `cd relay && npm run build`. 없으면 이 묶음은
통과가 아니라 건너뜀으로 표시됩니다). 요청을 `pane-a`로 보내도록 바꾸면 이 테스트가
실패하는 것을 확인했습니다.

---

## 실제 폰 체크리스트

아래는 자동 검사로 대신할 수 없는 항목입니다. 사용자가 실제 폰에서 확인한 뒤
확인 칸을 채웁니다.

| 번호 | 항목 | 관련 빈틈 | 확인 |
|---|---|---|---|
| 1 | 첫 실행 너구리 스플래시 (`#0d0d0f` 배경, 가운데 너구리) | splash | |
| 2 | 홈 화면 앱 아이콘이 너구리 | icon | |
| 3 | 페어링 없을 때 연결 안내 4단계가 바로 나옴 | phone-guide | |
| 4 | QR 페어링 성공 | phone-guide | |
| 5 | 워크스페이스 목록 | phone-guide | |
| 6 | 에이전트 탭 → Mighty 블록 화면 전체 (대화/블록 전환, 입력창 포함) | mighty-default | |
| 7 | 채팅 요청 전송·응답 수신 | chat | |
| 8 | (릴레이 배포 후) 모바일 데이터로 연결 | relay | |
| 9 | Mac 설정 화면 4단계 표시 | mac-guide | |
