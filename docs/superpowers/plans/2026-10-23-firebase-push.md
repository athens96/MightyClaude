# Firebase 푸시 · Crashlytics · App Distribution Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

> **시작 전에 반드시:** 이 계획은 `agy-permission-cards` 브랜치(Antigravity 권한 카드: `MobilePermission.toolName == "AntigravityPermission"`, `POST /m1/sessions/{id}/permission`의 `always`)가 `main`에 병합된 **뒤에** 실행한다. 실행자는 Task 0에서 `firebase-push` 브랜치를 그 병합 뒤의 `main` 위로 rebase하고 나서 시작한다. 이 계획의 코드는 그 브랜치의 이름(`MobileAntigravityCard`, `MobilePermission.FromAntigravity`, `DesktopService.AntigravityCard`, `AntigravityCardChanged`, `antigravityBoard`, `MobilePermission(antigravity:)`, 휴대폰 `antigravityOffersAlways`, 키 `antigravity.permission.allowAlways`·`antigravity.permission.deny`)을 그대로 쓴다.

**Goal:** 연결된 Mac(또는 Windows) 호스트에서 작업이 끝나거나(완료·실패), 사람이 골라야 할 때(권한 요청·질문·계획 승인·로그인 만료, Antigravity 카드 포함) 페어링된 안드로이드 폰에 알림이 온다.
- 알림 내용은 호스트가 그 폰의 페어링 비밀에서 만든 키로 봉인하고, 폰만 연다. 릴레이와 Google은 읽지 못한다.
- 권한 알림에는 버튼이 있어(Claude: 허용·거절, Antigravity: 이번만 허용·항상 허용·거절) 잠금을 푼 뒤 알림에서 바로 답한다. 이미 처리된 요청은 "이미 처리됨"으로 바뀐다.
- 폰 앱의 충돌은 Crashlytics로 모이되 사용자 내용은 남지 않는다. 릴리즈 APK는 Firebase App Distribution으로도 테스터에게 간다.

**Architecture:**
- **릴레이(`relay/`).** HTTP 라우트 넷: 폰의 푸시 핸들 등록·갱신·삭제(`/push/v1/handles`)와 호스트의 전송(`/push/v1/send`). 핸들은 처음 보낸 `serverId`에 묶인다. 핸들→FCM 토큰은 서버 볼륨의 JSON 파일(`PUSH_STORE_PATH`)에 둔다. FCM HTTP v1 전송기는 서비스 계정 파일(`FCM_SERVICE_ACCOUNT_FILE`)로 OAuth 토큰을 만든다. 테스트에서는 가짜 전송기를 쓴다. 한도는 TURN과 같은 방식(serverId·IP·전체 슬라이딩 창)이다.
- **봉인.** `key = HKDF-SHA256(ikm = SHA-256(UTF-8 deviceToken) 32바이트, salt = UTF-8 clientId, info = "mighty-push-v1", 32바이트)`. 호스트는 저장해 둔 `tokenHash`(16진수)를 풀어 같은 32바이트를 얻는다. 봉투는 `base64url(0x01 ‖ nonce 12B ‖ ChaCha20-Poly1305 암호문 ‖ 태그 16B)`, AAD는 `"mighty-push-v1:" + handle`이다. 릴레이 연결이든 Tailscale 직접 연결이든 같은 기기 토큰으로 인증하므로 키도 같다. 공유 픽스처 `native/contracts/fixtures/push.json`을 Mac·Windows·폰 테스트가 읽는다.
- **호스트(Mac 기준, Windows 동일).** 순수 계획기 `MobilePushPlanner`가 창 목록(`MobilePushPane`)의 변화에서 메시지 6종을 만든다(중복 제거, 첫 관찰은 기준선). 같은 픽스처의 `planner` 단계로 두 플랫폼이 같게 동작함을 묶는다. 기기 기록에 핸들과 받을 종류를 둔다. m1 `POST /m1/push/register|unregister|viewing`과 기능 `"push"`를 더한다. 폰이 그 세션을 보고 있으면(45초 TTL) 보내지 않는다. 전송은 `https://<릴레이>/push/v1/send`로 최대 3번 시도하고, 실패는 진단 기록에만 남긴다.
- **폰.** `@react-native-firebase/{app,messaging,crashlytics}` 26.4.x와 `react-native-notify-kit`(Notifee API의 유지되는 포크)을 쓴다. 데이터 메시지 → 백그라운드 처리기가 핸들로 페어링을 찾아 연다 → 로컬 알림. 버튼 → 헤드리스 이벤트에서 잠금 확인(로컬 모듈 `device-lock`) → 기존 클라이언트로 `POST …/permission`. 409는 "이미 처리됨", 실패는 "눌러서 열기"가 된다. 탭하면 `mightyclaude://host/<id>/session/<sid>` 화면이 열린다. 설정 화면에서 종류별 알림과 Crashlytics를 켜고 끈다. `google-services.json`은 저장소 밖 파일을 `GOOGLE_SERVICES_JSON`이 가리킨다.
- **App Distribution.** 저장소 밖 `~/Work/.mc-release/appdist.sh`가 Firebase CLI로 APK를 올린다. 테스터 목록은 그 폴더의 로컬 파일에만 둔다.

**Tech Stack:**
- Node 22 + TypeScript, `ws`, vitest(릴레이). `node:crypto`(`createSign` RS256, SHA-256, `timingSafeEqual`), 전역 `fetch`.
- Swift 5.9 / CryptoKit(`HKDF<SHA256>`, `ChaChaPoly`), Swift Testing(Mac `MightyCore`).
- C# / .NET 10, `System.Security.Cryptography.HKDF`, BouncyCastle `ChaCha20Poly1305`, `HttpClient`(Windows Core). WinUI 3는 코드로만 짓는다.
- Expo SDK 57 / React Native 0.86, `@noble/hashes`·`@noble/ciphers`, `@react-native-firebase/*` 26.4.x, `react-native-notify-kit` 10.9.x, 로컬 Expo 모듈(Kotlin), jest(`jest-expo`).
- Firebase CLI(`firebase-tools`, npx), EAS 로컬 빌드.

## Global Constraints

**공개 저장소**
- 키·토큰·서비스 계정 JSON·`google-services.json`·이메일·개인 경로(`/Users/…`)·실제 도메인 외 실제 주소를 저장소에 넣지 않는다. 테스트의 RSA 키는 테스트가 실행 중에 만든다(`generateKeyPairSync`). 픽스처의 토큰·핸들은 이 계획이 정한 가짜 값이다.
- 새 무시 항목: `mobile/google-services.json`, `relay/deploy/oracle/secrets/`, `relay/deploy/oracle/fcm-service-account.json`.
- 로그·진단에 핸들·FCM 토큰·호스트 토큰·봉투·알림 내용을 남기지 않는다. 릴레이는 지금처럼 `serverId` 앞 8자만 찍는다.

**Mac이 기준이다**
- Mac 동작이 계약이다. Windows와 폰은 Mac을 따른다. 계획기 규칙과 봉인은 공유 픽스처로 묶는다.

**개발 기계를 건드리지 않는다**
- GUI 앱을 띄우지 않는다(Mac 앱, Windows 앱, Android 에뮬레이터 모두). `--smoke-test`도 Mac에서 돌리지 않는다.
- 개발 Mac에 아무것도 설치하지 않는다. 예외는 `mobile/`과 `relay/`의 `package.json`에 적은 npm 의존성뿐이다(`npm install`은 그 폴더 안에서만). Firebase CLI는 `npx -y firebase-tools@15`로 설치 없이 쓴다.
- `firebase login`은 사용자가 터미널 창에서 직접 한다. 에이전트는 로그인 프롬프트를 몰지 않는다.

**스타일 엔진 동결**
- `bash scripts/check-style-freeze.sh`의 동결 경로(`styles/*`, `native/macos/Sources/MightyCore/Styles/*`, `Resources/Styles/*`, `mobile/src/lib/styles.ts`, `docs/mighty-styles.md`, `native/macos/Tests/MightyCoreTests/Style*.swift`, `mobile/src/__tests__/styles*.test.ts` 등)는 건드리지 않는다.

**문구와 화면 규칙**
- 사용자에게 보이는 말은 모두 `locales/{ko,en,zh,ja}.json`의 키로 둔다. 네 언어에 같은 키와 같은 `{자리}`를 넣고, 바이트 그대로 `mobile/src/locales/`와 `native/macos/Sources/MightyCore/Resources/Locales/`에 복사한다. 키는 그 키를 처음 쓰는 태스크에서 더한다(쓰이지 않는 키는 검사가 거절한다).
- 폰 코드(`mobile/app`, `mobile/src`)에 한글 리터럴을 넣지 않는다(예산 0). 간격은 `spacing.*` 토큰만 쓴다(`check-design-tokens.js`).
- WinUI: XAML 없이 코드로, 사용자 문구는 `Locale.Get`, CS4014 금지(`_ =`로 버리지 말고 `Act`/`TrackLoginTask` 같은 기존 경로), `IsLoaded` 확인.

**커밋**
- 제목은 `(<영역>) <사용자가 이제 얻는 것>` 꼴의 소문자 문장이다. 영역은 `relay`·`contracts`·`mac`·`windows`·`mobile`·`locales`·`docs`다.
- 메시지 끝에 `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`를 단다. 형식은 `git commit -m "<제목>" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"`다.
- `git add`에는 태스크가 적은 경로만 적는다(`-A`, `.` 금지). push·amend 하지 않는다.

**푸시 계약 상수 (모든 플랫폼 같음)**
- 핸들: 32 무작위 바이트 base64url(패딩 없음), `^[A-Za-z0-9_-]{43}$`. 핸들 비밀도 같은 꼴이다.
- 봉투: 최대 3072자(`^[A-Za-z0-9_-]{24,3072}$`). 평문은 2048바이트 이하다. 문자열 상한은 유니코드 스칼라 개수로 센다: `hostName`·`workspace`·`title` 80, `summary` 200.
- 종류: `completed`·`failed`·`permission`·`question`·`plan`·`signin`. 버튼: `claude`·`antigravity`·`antigravityOnce`·`none`.
- "보고 있음" 유효 시간 45초. 폰은 30초마다 다시 알린다.
- 호스트 전송: 최대 3번 시도한다. 간격은 2초, 8초이고, 릴레이가 `Retry-After`를 주면 그 값(30초 이하)을 쓴다.

## Commands used by every task

- **RELAY-RUN:** `cd /Users/young/Work/MightyClaude-firebase/relay && npm test -- <파일 이름 일부>`. 처음 한 번 `npm install`(그 폴더 안에서만).
- **MAC-RUN:** `cd /Users/young/Work/MightyClaude-firebase && DEVELOPER_DIR=/Library/Developer/CommandLineTools bash scripts/test-native-macos.sh --scratch-path /tmp/mc-fb --filter '<정규식>'`. `--filter`를 빼지 않는다(전체 스위트는 로컬에서 멈춘다).
- **MAC-BUILD:** `cd /Users/young/Work/MightyClaude-firebase && DEVELOPER_DIR=/Library/Developer/CommandLineTools swift build --package-path native/macos --scratch-path /tmp/mc-fb`. 앱 타깃(`MightyClaude`)까지 컴파일만 한다. 실행하지 않는다.
- **CORE-RUN:** `bash ~/Work/MightyClaude-wincatch/.superpowers/sdd/bin/core-checks.sh /Users/young/Work/MightyClaude-firebase '<정규식>'`. **Pass:** 새 검사가 `PASS`로 보이고 `RESULT: BASELINE`(9개의 Windows 전용 실패만)이다. **Expected failure**는 태스크가 적는다.
- **WINUI-COMPILE:** `cd /Users/young/Work/MightyClaude-firebase && bash scripts/check-winui-compile.sh`. **Pass:** `WinUI: C# 컴파일 오류 없음`.
- **PHONE-RUN:** `cd /Users/young/Work/MightyClaude-firebase/mobile && npx jest <파일> && npm run typecheck`.
- **LOCALES:** `cd /Users/young/Work/MightyClaude-firebase && node scripts/check-locales.js && node scripts/check-locales.js --check`.

---

## File Structure

| Path | Change | Responsibility |
|---|---|---|
| `relay/src/rate.ts` | Create | 슬라이딩 창 한도(키별). |
| `relay/src/push-store.ts` | Create | 핸들 → FCM 토큰 저장소: 첫 serverId에 묶기, 비밀 해시, 유휴 정리, 원자적 JSON 파일. |
| `relay/src/fcm.ts` | Create | FCM HTTP v1 전송기: 서비스 계정, RS256 JWT, 액세스 토큰 캐시, 응답 분류. |
| `relay/src/push.ts` | Create | `/push/v1/*` HTTP 라우트, 본문 한도, 인증, 한도, 응답 코드. |
| `relay/src/config.ts` | Modify | 푸시 설정 9개(환경 변수). |
| `relay/src/server.ts` | Modify | 푸시 라우트 연결, `pushSender` 옵션, 종료 시 flush. |
| `relay/test/push-store.test.ts`, `relay/test/fcm.test.ts`, `relay/test/push.test.ts` | Create | 저장소·전송기·라우트 테스트(가짜 전송기, 실행 중 만든 RSA 키). |
| `relay/Dockerfile`, `relay/deploy/oracle/compose.yaml`, `relay/deploy/oracle/setup.sh` | Modify | `/data` 볼륨, `secrets/` 읽기 전용 마운트, 키 파일 옮기기. |
| `relay/deploy/oracle/push-check.mjs` | Create | 살아 있는 릴레이의 푸시 경로 점검. |
| `.gitignore` | Modify | `google-services.json`, 서버 비밀 폴더·파일. |
| `relay/README.md`, `docs/relay.md`, `docs/relay-oracle.md` | Modify | 푸시 라우트, 환경 변수, 키 배포. |
| `native/contracts/fixtures/push.json` | Create | 봉인 벡터와 계획기 단계(Mac·Windows·폰이 읽는다). |
| `native/contracts/README.md` | Modify | 픽스처 설명. |
| `native/macos/Sources/MightyCore/Remote/MobilePush.swift` | Create | 봉인, 메시지·요청·창 형식, 계획기, 받을 기기, 릴레이 주소, 전송 결과, HTTP 전송기, m1 요청 형식. |
| `native/macos/Sources/MightyCore/Remote/MobileDeviceRegistry.swift` | Modify | 기기 줄의 `push`, `setPush`·`clearPush`·`pushDevices`. |
| `native/macos/Sources/MightyCore/Remote/MobileRemoteService.swift` | Modify | `/m1/push/*`, 보고 있음, `pushObserve`·`deliver`, 테스트 이음새. |
| `native/macos/Sources/MightyCore/Remote/MobileRemoteModels.swift` | Modify | 기능 `"push"`. |
| `native/macos/Sources/MightyClaude/AppStore+MobilePush.swift` | Create | 창·요청·카드·로그인 상태를 계획기에 넘긴다. |
| `native/macos/Sources/MightyClaude/AppStore.swift`, `AppStore+MobileRemote.swift` | Modify | `mobilePushTask`, `configureMobilePush()` 호출. |
| `native/macos/Tests/MightyCoreTests/MobilePushTests.swift` | Create | 봉인·계획기·릴레이 주소 테스트. |
| `native/macos/Tests/MightyCoreTests/MobileRemoteTests.swift` | Modify | m1 라우트와 전송 테스트, `RecordingPushTransport`. |
| `native/windows/MightyClaude.Core/Remote/MobilePush.cs` | Create | Mac `MobilePush.swift`의 C# 짝. |
| `native/windows/MightyClaude.Core/Remote/MobilePushHub.cs` | Create | 등록·보고 있음·관찰·전송(Windows 호스트). |
| `native/windows/MightyClaude.Core/Remote/MobileIdentity.cs` | Modify | 기기 줄의 `Push`, `SetPush`·`ClearPush`·`PushDevices`. |
| `native/windows/MightyClaude.Core/Remote/MobileRelayHost.cs` | Modify | `Identity`, `RelayForPush`, `Enabled`. |
| `native/windows/MightyClaude.Core/Remote/MobileDesktopRouter.cs` | Modify | `Push` 속성, `/m1/push/*`, 기능 `"push"`. |
| `native/windows/MightyClaude.WinUI/MainWindow.MobileRemote.cs` | Modify | 허브 생성·연결, 틱마다 관찰, 종료. |
| `native/windows/MightyClaude.Core.Tests/MobilePushVerification.cs` | Create | 픽스처·버튼·받을 기기·라우트·전송 검사. |
| `native/windows/MightyClaude.Core.Tests/MightyClaude.Core.Tests.csproj`, `Verification.cs` | Modify | 픽스처 포함, 등록 줄. |
| `mobile/src/lib/push/seal.ts`, `message.ts` | Create | 봉투 열기, 평문 검사. |
| `mobile/src/lib/push/registry.ts` | Create | 릴레이 핸들 등록·갱신·삭제, 호스트 동기화, 핸들 → 페어링. |
| `mobile/src/lib/push/notification.ts`, `actions.ts` | Create | 알림 모양, 버튼 → 응답, 409 판단. |
| `mobile/src/lib/push/settings.ts`, `viewing.ts` | Create | 종류별·충돌 보고 설정, "보고 있음" 보고기. |
| `mobile/src/lib/push/device-lock.ts` | Create | 잠금 확인(모르면 잠김). |
| `mobile/src/lib/push/crash.ts`, `default-error-handler.ts` | Create | 오류 다듬기, Crashlytics 설치. |
| `mobile/src/lib/push/host-lease.ts`, `runtime.ts`, `background.ts` | Create | 헤드리스 연결, 표시·버튼·탭·동기화, 진입점 등록. |
| `mobile/src/components/push-root.tsx` | Create | 앞에 있을 때의 시작·탭·재동기화. |
| `mobile/index.js` | Create | JS 진입점(기본 오류 처리기 → 백그라운드 등록 → expo-router). |
| `mobile/app/settings.tsx` | Create | 설정 화면. |
| `mobile/app/_layout.tsx`, `mobile/app/(tabs)/hosts.tsx`, `mobile/app/host/[hostId]/session/[sessionId].tsx` | Modify | 설정 화면 등록·버튼, `PushRoot`, 보고 있음. |
| `mobile/src/api/client.ts`, `mobile/src/api/types.ts`, `mobile/src/store/hosts.ts` | Modify | m1 푸시 라우트 셋, 기능 `'push'`, 페어링 삭제 시 핸들 삭제. |
| `mobile/app.config.js`, `mobile/firebase.json` | Create | 로컬 `google-services.json` 넣기, RNFB 설정. |
| `mobile/app.json`, `mobile/package.json`, `mobile/package-lock.json` | Modify | 권한, 의존성, `main`. |
| `mobile/modules/device-lock/**` | Create | 로컬 Expo 모듈(Kotlin) `MightyDeviceLock`. |
| `mobile/src/__tests__/push-*.test.ts`, `mobile/src/__tests__/routes.test.ts` | Create / Modify | 폰 테스트. |
| `locales/{ko,en,zh,ja}.json` + 사본 둘 + `docs/i18n.md` | Modify | 호스트 오류 2개, `phone.push.*` 19개, `phone.settings.*` 13개. |
| `docs/mobile-remote.md`, `docs/help/content/{ko,en,zh,ja}/phone.md` | Modify | 계약과 도움말. |
| `~/Work/.mc-release/apk-local.sh`, `~/Work/.mc-release/appdist.sh` | Modify / Create (저장소 밖) | APK 빌드에 `GOOGLE_SERVICES_JSON`, App Distribution 업로드. |

**태스크 순서.**
- 1→2→3→4: 릴레이.
- 5→6→7→8→9: Mac.
- 10→11→12: Windows. Mac의 5·6·7을 따른다.
- 13→14→15→16→17→18: 폰. 13은 5의 픽스처를 쓴다.
- 19: 문서. 20: 저장소 밖 도구. 21: 전체 검증.

Windows와 폰은 Mac Task 7(로케일 키)이 끝난 뒤 시작한다.

---

## Task 0: 병합된 main 위로 옮기고 기준선 확인

**Files:** 없음(브랜치 작업만)

**Interfaces:**
- Consumes: `main`에 병합된 `agy-permission-cards`.
- Produces: `firebase-push` 브랜치가 그 `main` 위에 있다. 기준선 결과(Windows 9 FAIL, 폰 jest 통과)를 확인해 둔다.

- [ ] **Step 1: 병합 확인**

Run: `cd /Users/young/Work/MightyClaude-firebase && git fetch origin && git log --oneline origin/main | grep -m1 "Antigravity card"`
Expected: Antigravity 카드 커밋이 한 줄 보인다. 안 보이면 멈추고 사용자에게 알린다(병합 전에는 시작하지 않는다).

- [ ] **Step 2: rebase**

Run: `git status --short --branch && git rebase origin/main`
Expected: 충돌 없이 끝난다(이 브랜치에는 설계·계획 문서 두 개만 있다).

- [ ] **Step 3: 이름 확인**

Run: `grep -n "MobileAntigravityCard" native/macos/Sources/MightyCore/Remote/MobileRemoteModels.swift | head -2 && grep -n "FromAntigravity\|AntigravityCardChanged" native/windows/MightyClaude.Core/Remote/MobileMighty.cs native/windows/MightyClaude.Core/DesktopService.Antigravity.cs | head -3 && grep -n "antigravityOffersAlways" mobile/src/lib/antigravity-permission.ts`
Expected: 세 곳 모두 이름이 보인다.

- [ ] **Step 4: 기준선**

Run: CORE-RUN with `'^$'`, then `cd mobile && npx jest --silent | tail -3`
Expected: `RESULT: BASELINE`, jest 모두 통과. 다르면 이 계획을 시작하기 전에 사용자에게 알린다.

---

## Task 1: 릴레이가 핸들과 전송 한도를 기억한다

**Files:**
- Create: `relay/src/rate.ts`
- Create: `relay/src/push-store.ts`
- Create: `relay/test/push-store.test.ts`

**Interfaces:**
- Consumes: `SERVER_ID_PATTERN` (`relay/src/protocol.ts`).
- Produces:
  - `type RateDecision = { readonly ok: true } | { readonly ok: false; readonly retryAfterSecs: number }`
  - `class SlidingWindowLimiter { constructor(windowMs: number, limit: number); check(key: string, now: number): RateDecision; record(key: string, now: number): void; sweep(now: number): void; get size(): number }`
  - `HANDLE_PATTERN: RegExp`, `FCM_TOKEN_PATTERN: RegExp`, `newPushSecret(): string`
  - `interface PushHandleRecord { readonly handle: string; readonly secretHash: string; fcmToken: string; serverId: string | null; readonly createdAt: number; usedAt: number }`
  - `type BindResult = { readonly kind: 'ok'; readonly fcmToken: string } | { readonly kind: 'unknown' } | { readonly kind: 'foreign' }`
  - `interface PushStoreOptions { readonly path: string; readonly maxHandles: number; readonly idleMs: number; readonly log: (line: string) => void }`
  - `class PushHandleStore { static open(options: PushStoreOptions, now: number): Promise<PushHandleStore>; get size(): number; register(fcmToken: string, now: number): Promise<{ handle: string; secret: string } | null>; refresh(handle: string, secret: string, fcmToken: string, now: number): Promise<boolean>; remove(handle: string, secret: string): Promise<boolean>; bind(handle: string, serverId: string, now: number): Promise<BindResult>; drop(handle: string): Promise<void>; flush(): Promise<void> }`

**저장 방식 결정.** 핸들→토큰은 서버 볼륨의 JSON 파일 하나(`/data/push-handles.json`, 0600)에 둔다. 이유는 셋이다.
1. 릴레이는 한 인스턴스다(README가 여러 인스턴스에는 스티키 라우팅이 필요하다고 이미 말한다).
2. 쓰기가 드물다. 등록·갱신·삭제·첫 묶기·하루 한 번 사용 시각 갱신 때뿐이다.
3. 재배포·재시작 뒤에도 핸들이 남아야 한다. 그래야 폰이 앱을 다시 열지 않아도 알림이 계속 온다.

새 의존성(SQLite 등)은 들이지 않는다. 쓰기는 임시 파일에 쓰고 이름을 바꾸며, 한 줄로 직렬화한다. 읽을 수 없는 파일은 옆으로 옮기고 빈 저장소로 시작한다. FCM 토큰은 사람을 가리키는 식별자이므로 파일은 소유자만 읽는다.

- [ ] **Step 1: Write the failing test**

Create `relay/test/push-store.test.ts`:

```ts
import { mkdtemp, readFile, readdir, stat, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { describe, expect, it } from 'vitest';

import { HANDLE_PATTERN, PushHandleStore, type PushStoreOptions } from '../src/push-store.ts';
import { SlidingWindowLimiter } from '../src/rate.ts';

const TOKEN_A = 'fcm-token-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
const TOKEN_B = 'fcm-token-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbb';
const SERVER_1 = 'a'.repeat(64);
const SERVER_2 = 'b'.repeat(64);
const DAY = 86_400_000;

function options(path: string, overrides: Partial<PushStoreOptions> = {}): PushStoreOptions {
  return { path, maxHandles: 100, idleMs: 120 * DAY, log: () => {}, ...overrides };
}

describe('SlidingWindowLimiter', () => {
  it('allows up to the limit inside the window and names the wait', () => {
    const limiter = new SlidingWindowLimiter(10_000, 2);
    expect(limiter.check('k', 0)).toEqual({ ok: true });
    limiter.record('k', 0);
    limiter.record('k', 1_000);
    expect(limiter.check('k', 2_000)).toEqual({ ok: false, retryAfterSecs: 8 });
    expect(limiter.check('k', 10_000)).toEqual({ ok: true });
  });

  it('forgets keys whose events aged out', () => {
    const limiter = new SlidingWindowLimiter(1_000, 1);
    limiter.record('a', 0);
    limiter.sweep(5_000);
    expect(limiter.size).toBe(0);
  });
});

describe('PushHandleStore', () => {
  it('registers an unguessable handle and binds it to the first serverId', async () => {
    const store = await PushHandleStore.open(options(''), 0);
    const made = await store.register(TOKEN_A, 0);
    expect(made).not.toBeNull();
    expect(made!.handle).toMatch(HANDLE_PATTERN);
    expect(made!.secret).toMatch(HANDLE_PATTERN);
    expect(made!.secret).not.toBe(made!.handle);
    expect(await store.bind(made!.handle, SERVER_1, 1)).toEqual({ kind: 'ok', fcmToken: TOKEN_A });
    expect(await store.bind(made!.handle, SERVER_2, 2)).toEqual({ kind: 'foreign' });
    expect(await store.bind(made!.handle, SERVER_1, 3)).toEqual({ kind: 'ok', fcmToken: TOKEN_A });
    expect(await store.bind('x'.repeat(43), SERVER_1, 4)).toEqual({ kind: 'unknown' });
  });

  it('refreshes and removes only with the handle secret', async () => {
    const store = await PushHandleStore.open(options(''), 0);
    const made = (await store.register(TOKEN_A, 0))!;
    expect(await store.refresh(made.handle, 'y'.repeat(43), TOKEN_B, 1)).toBe(false);
    expect(await store.refresh(made.handle, made.secret, TOKEN_B, 1)).toBe(true);
    expect(await store.bind(made.handle, SERVER_1, 2)).toEqual({ kind: 'ok', fcmToken: TOKEN_B });
    expect(await store.remove(made.handle, 'y'.repeat(43))).toBe(false);
    expect(await store.remove(made.handle, made.secret)).toBe(true);
    expect(await store.bind(made.handle, SERVER_1, 3)).toEqual({ kind: 'unknown' });
  });

  it('drops a handle FCM called gone', async () => {
    const store = await PushHandleStore.open(options(''), 0);
    const made = (await store.register(TOKEN_A, 0))!;
    await store.drop(made.handle);
    expect(store.size).toBe(0);
  });

  it('refuses a new handle when full, after dropping idle ones', async () => {
    const store = await PushHandleStore.open(options('', { maxHandles: 1, idleMs: DAY }), 0);
    expect(await store.register(TOKEN_A, 0)).not.toBeNull();
    expect(await store.register(TOKEN_B, 1)).toBeNull();
    expect(await store.register(TOKEN_B, 2 * DAY)).not.toBeNull();
    expect(store.size).toBe(1);
  });

  it('keeps handles across a restart in an owner-only file', async () => {
    const folder = await mkdtemp(join(tmpdir(), 'push-store-'));
    const path = join(folder, 'data', 'push-handles.json');
    const first = await PushHandleStore.open(options(path), 0);
    const made = (await first.register(TOKEN_A, 0))!;
    await first.bind(made.handle, SERVER_1, 1);
    await first.flush();
    expect((await stat(path)).mode & 0o777).toBe(0o600);
    const text = await readFile(path, 'utf8');
    expect(text).not.toContain(made.secret);
    const second = await PushHandleStore.open(options(path), 2);
    expect(await second.bind(made.handle, SERVER_2, 3)).toEqual({ kind: 'foreign' });
    expect(await second.refresh(made.handle, made.secret, TOKEN_B, 4)).toBe(true);
  });

  it('moves an unreadable file aside and starts empty', async () => {
    const folder = await mkdtemp(join(tmpdir(), 'push-store-'));
    const path = join(folder, 'push-handles.json');
    await writeFile(path, '{not json');
    const lines: string[] = [];
    const store = await PushHandleStore.open(options(path, { log: (line) => lines.push(line) }), 7);
    expect(store.size).toBe(0);
    expect(await readdir(folder)).toContain('push-handles.json.unreadable-7');
    expect(lines).toEqual(['[push] handle store unreadable; moved aside, starting empty']);
  });
});
```

- [ ] **Step 2: Run it and expect it to fail**

Run: RELAY-RUN with `push-store`
Expected: FAIL — `Failed to load url ../src/push-store.ts` (파일 없음).

- [ ] **Step 3: Implement**

Create `relay/src/rate.ts`:

```ts
/** A sliding-window counter per key: the same rule as the TURN mint limits in hub.ts. */
export type RateDecision =
  | { readonly ok: true }
  | { readonly ok: false; readonly retryAfterSecs: number };

export class SlidingWindowLimiter {
  readonly #windowMs: number;
  readonly #limit: number;
  readonly #times = new Map<string, number[]>();

  constructor(windowMs: number, limit: number) {
    this.#windowMs = windowMs;
    this.#limit = limit;
  }

  /** Whether one more event fits for `key`; nothing is recorded. */
  check(key: string, now: number): RateDecision {
    const times = this.#live(key, now);
    if (times.length < this.#limit) return { ok: true };
    const oldest = times[0] ?? now;
    return { ok: false, retryAfterSecs: Math.max(1, Math.ceil((oldest + this.#windowMs - now) / 1000)) };
  }

  record(key: string, now: number): void {
    const times = this.#live(key, now);
    times.push(now);
    this.#times.set(key, times);
  }

  /** Drops keys whose events all aged out, so the map cannot grow without bound. */
  sweep(now: number): void {
    for (const key of [...this.#times.keys()]) this.#live(key, now);
  }

  get size(): number {
    return this.#times.size;
  }

  #live(key: string, now: number): number[] {
    const times = (this.#times.get(key) ?? []).filter((t) => now - t < this.#windowMs);
    if (times.length === 0) this.#times.delete(key);
    else this.#times.set(key, times);
    return times;
  }
}
```

Create `relay/src/push-store.ts`:

```ts
import { createHash, randomBytes, timingSafeEqual } from 'node:crypto';
import { mkdir, readFile, rename, writeFile } from 'node:fs/promises';
import { dirname } from 'node:path';

import { SERVER_ID_PATTERN } from './protocol.ts';

/** 32 random bytes, base64url without padding. */
export const HANDLE_PATTERN = /^[A-Za-z0-9_-]{43}$/;
/** FCM registration tokens: printable ASCII, no spaces. */
export const FCM_TOKEN_PATTERN = /^[\x21-\x7e]{20,4096}$/;

const DAY_MS = 86_400_000;

export function newPushSecret(): string {
  return randomBytes(32).toString('base64url');
}

function sha256Hex(value: string): string {
  return createHash('sha256').update(value, 'utf8').digest('hex');
}

export interface PushHandleRecord {
  readonly handle: string;
  /** SHA-256 of the phone's handle secret; the secret itself is never stored. */
  readonly secretHash: string;
  fcmToken: string;
  /** The host the handle was first used by; null until then. */
  serverId: string | null;
  readonly createdAt: number;
  usedAt: number;
}

export type BindResult =
  | { readonly kind: 'ok'; readonly fcmToken: string }
  | { readonly kind: 'unknown' }
  | { readonly kind: 'foreign' };

export interface PushStoreOptions {
  /** File on the server volume; '' keeps handles in memory only (tests, local runs). */
  readonly path: string;
  readonly maxHandles: number;
  /** A handle neither refreshed nor used for this long is dropped. */
  readonly idleMs: number;
  readonly log: (line: string) => void;
}

function validRecord(value: unknown): value is PushHandleRecord {
  if (typeof value !== 'object' || value === null) return false;
  const r = value as Record<string, unknown>;
  return (
    typeof r['handle'] === 'string' && HANDLE_PATTERN.test(r['handle']) &&
    typeof r['secretHash'] === 'string' && /^[0-9a-f]{64}$/.test(r['secretHash']) &&
    typeof r['fcmToken'] === 'string' && FCM_TOKEN_PATTERN.test(r['fcmToken']) &&
    (r['serverId'] === null || (typeof r['serverId'] === 'string' && SERVER_ID_PATTERN.test(r['serverId']))) &&
    Number.isFinite(r['createdAt']) && Number.isFinite(r['usedAt'])
  );
}

/**
 * Push handles → FCM tokens. A handle is what a phone hands its hosts; the token
 * never leaves the relay. A handle answers only the first host that used it.
 */
export class PushHandleStore {
  readonly #records = new Map<string, PushHandleRecord>();
  readonly #options: PushStoreOptions;
  #writing: Promise<void> = Promise.resolve();

  private constructor(options: PushStoreOptions) {
    this.#options = options;
  }

  static async open(options: PushStoreOptions, now: number): Promise<PushHandleStore> {
    const store = new PushHandleStore(options);
    if (options.path !== '') await store.#load(now);
    return store;
  }

  get size(): number {
    return this.#records.size;
  }

  async register(fcmToken: string, now: number): Promise<{ handle: string; secret: string } | null> {
    this.#dropIdle(now);
    if (this.#records.size >= this.#options.maxHandles) return null;
    const handle = newPushSecret();
    const secret = newPushSecret();
    this.#records.set(handle, { handle, secretHash: sha256Hex(secret), fcmToken, serverId: null, createdAt: now, usedAt: now });
    await this.#save();
    return { handle, secret };
  }

  async refresh(handle: string, secret: string, fcmToken: string, now: number): Promise<boolean> {
    const record = this.#owned(handle, secret);
    if (record === null) return false;
    record.fcmToken = fcmToken;
    record.usedAt = now;
    await this.#save();
    return true;
  }

  async remove(handle: string, secret: string): Promise<boolean> {
    if (this.#owned(handle, secret) === null) return false;
    this.#records.delete(handle);
    await this.#save();
    return true;
  }

  async bind(handle: string, serverId: string, now: number): Promise<BindResult> {
    const record = this.#records.get(handle);
    if (record === undefined) return { kind: 'unknown' };
    if (record.serverId !== null && record.serverId !== serverId) return { kind: 'foreign' };
    const firstUse = record.serverId === null;
    const stale = now - record.usedAt > DAY_MS;
    record.serverId = serverId;
    record.usedAt = now;
    // usedAt reaches the disk at most daily per handle; it only feeds the idle sweep.
    if (firstUse || stale) await this.#save();
    return { kind: 'ok', fcmToken: record.fcmToken };
  }

  async drop(handle: string): Promise<void> {
    if (!this.#records.delete(handle)) return;
    await this.#save();
  }

  async flush(): Promise<void> {
    await this.#writing;
  }

  #owned(handle: string, secret: string): PushHandleRecord | null {
    const record = this.#records.get(handle);
    if (record === undefined || !HANDLE_PATTERN.test(secret)) return null;
    const stored = Buffer.from(record.secretHash, 'hex');
    const given = Buffer.from(sha256Hex(secret), 'hex');
    return stored.length === given.length && timingSafeEqual(stored, given) ? record : null;
  }

  #dropIdle(now: number): void {
    for (const [handle, record] of this.#records) {
      if (now - record.usedAt > this.#options.idleMs) this.#records.delete(handle);
    }
  }

  async #load(now: number): Promise<void> {
    const path = this.#options.path;
    let text: string;
    try {
      text = await readFile(path, 'utf8');
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === 'ENOENT') return;
      throw error;
    }
    try {
      const parsed = JSON.parse(text) as { v?: unknown; handles?: unknown };
      if (parsed.v !== 1 || !Array.isArray(parsed.handles)) throw new Error('shape');
      for (const record of parsed.handles) {
        if (validRecord(record)) this.#records.set(record.handle, { ...record });
      }
    } catch {
      await rename(path, `${path}.unreadable-${now}`);
      this.#options.log('[push] handle store unreadable; moved aside, starting empty');
      return;
    }
    this.#dropIdle(now);
  }

  #save(): Promise<void> {
    const path = this.#options.path;
    if (path === '') return Promise.resolve();
    const snapshot = JSON.stringify({ v: 1, handles: [...this.#records.values()] });
    this.#writing = this.#writing
      .then(async () => {
        await mkdir(dirname(path), { recursive: true, mode: 0o700 });
        const temporary = `${path}.tmp`;
        await writeFile(temporary, snapshot, { mode: 0o600 });
        await rename(temporary, path);
      })
      .catch((error: unknown) => {
        // Memory still answers; the next write tries again.
        this.#options.log(`[push] handle store write failed: ${(error as NodeJS.ErrnoException).code ?? 'error'}`);
      });
    return this.#writing;
  }
}
```

- [ ] **Step 4: Run it and expect it to pass**

Run: RELAY-RUN with `push-store`, then `cd relay && npx tsc --noEmit`
Expected: 8 tests pass; tsc prints nothing.

- [ ] **Step 5: Commit**

```bash
git add relay/src/rate.ts relay/src/push-store.ts relay/test/push-store.test.ts
git commit -m "(relay) the relay keeps phones' push handles, each bound to the first host that uses it, in an owner-only file that survives restarts" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## Task 2: 릴레이가 FCM HTTP v1로 데이터 메시지를 보낸다

**Files:**
- Create: `relay/src/fcm.ts`
- Create: `relay/test/fcm.test.ts`

**Interfaces:**
- Produces:
  - `type PushSendResult = { readonly kind: 'sent' } | { readonly kind: 'invalid-token' } | { readonly kind: 'retry'; readonly retryAfterSecs: number } | { readonly kind: 'failed'; readonly status: number }`
  - `interface PushSender { send(fcmToken: string, data: Readonly<Record<string, string>>): Promise<PushSendResult> }`
  - `interface ServiceAccount { readonly projectId: string; readonly clientEmail: string; readonly privateKey: string; readonly tokenUri: string }`
  - `parseServiceAccount(text: string): ServiceAccount` (형식이 틀리면 `Error('service account: …')`)
  - `type FetchLike = (url: string, init: { method: string; headers: Record<string, string>; body: string }) => Promise<{ status: number; headers: { get(name: string): string | null }; text(): Promise<string> }>`
  - `class FcmHttpV1Sender implements PushSender { constructor(account: ServiceAccount, fetchImpl?: FetchLike, now?: () => number); static fromFile(path: string, fetchImpl?: FetchLike): Promise<FcmHttpV1Sender> }`

**FCM 응답 처리.**
- 200 → `sent`.
- 404, 또는 `errorCode`가 `UNREGISTERED` → `invalid-token`.
- 400 `INVALID_ARGUMENT` → `invalid-token`. 이 릴레이가 보내는 본문은 고정 모양(`v`·`h`·`p` 문자열 셋, 3.2 KB 이하)이므로, 400이 나면 남는 원인은 토큰이다.
- 403 `SENDER_ID_MISMATCH` → `invalid-token`. 다른 프로젝트의 토큰이다.
- 401 → 캐시한 OAuth 토큰을 버리고 `failed`.
- 429·500·503 → `retry`. 기다릴 시간은 `Retry-After`, 없으면 60초다.

- [ ] **Step 1: Write the failing test**

Create `relay/test/fcm.test.ts`:

```ts
import { createVerify, generateKeyPairSync } from 'node:crypto';

import { describe, expect, it } from 'vitest';

import { FcmHttpV1Sender, parseServiceAccount, type FetchLike } from '../src/fcm.ts';

// A throwaway key made for this run; no real credential is ever in the repository.
const { privateKey, publicKey } = generateKeyPairSync('rsa', {
  modulusLength: 2048,
  privateKeyEncoding: { type: 'pkcs8', format: 'pem' },
  publicKeyEncoding: { type: 'spki', format: 'pem' },
});

const ACCOUNT_JSON = JSON.stringify({
  type: 'service_account',
  project_id: 'demo-project',
  client_email: 'relay@demo-project.iam.gserviceaccount.com',
  private_key: privateKey,
  token_uri: 'https://oauth2.googleapis.com/token',
});

interface Call {
  url: string;
  init: { method: string; headers: Record<string, string>; body: string };
}

function reply(status: number, body: unknown, headers: Record<string, string> = {}) {
  return {
    status,
    headers: { get: (name: string) => headers[name.toLowerCase()] ?? null },
    text: async () => JSON.stringify(body),
  };
}

function fake(answers: Array<ReturnType<typeof reply>>, calls: Call[]): FetchLike {
  return async (url, init) => {
    calls.push({ url, init });
    const next = answers.shift();
    if (next === undefined) throw new Error('unexpected fetch');
    return next;
  };
}

const TOKEN_REPLY = reply(200, { access_token: 'ya29.test', expires_in: 3600, token_type: 'Bearer' });

describe('parseServiceAccount', () => {
  it('reads the four fields it needs', () => {
    expect(parseServiceAccount(ACCOUNT_JSON)).toEqual({
      projectId: 'demo-project',
      clientEmail: 'relay@demo-project.iam.gserviceaccount.com',
      privateKey,
      tokenUri: 'https://oauth2.googleapis.com/token',
    });
  });

  it('refuses a file without a private key', () => {
    expect(() => parseServiceAccount('{"project_id":"x","client_email":"a@b"}')).toThrow(/service account/);
  });
});

describe('FcmHttpV1Sender', () => {
  it('signs an RS256 assertion, then sends a high-priority data-only message', async () => {
    const calls: Call[] = [];
    const sender = new FcmHttpV1Sender(parseServiceAccount(ACCOUNT_JSON), fake([TOKEN_REPLY, reply(200, { name: 'm/1' })], calls), () => 1_000_000);
    expect(await sender.send('device-token', { v: '1', h: 'H', p: 'P' })).toEqual({ kind: 'sent' });

    const tokenCall = calls[0]!;
    expect(tokenCall.url).toBe('https://oauth2.googleapis.com/token');
    const form = new URLSearchParams(tokenCall.init.body);
    expect(form.get('grant_type')).toBe('urn:ietf:params:oauth:grant-type:jwt-bearer');
    const [header, claims, signature] = form.get('assertion')!.split('.');
    expect(JSON.parse(Buffer.from(header!, 'base64url').toString())).toEqual({ alg: 'RS256', typ: 'JWT' });
    expect(JSON.parse(Buffer.from(claims!, 'base64url').toString())).toEqual({
      iss: 'relay@demo-project.iam.gserviceaccount.com',
      scope: 'https://www.googleapis.com/auth/firebase.messaging',
      aud: 'https://oauth2.googleapis.com/token',
      iat: 1000,
      exp: 4600,
    });
    const verify = createVerify('RSA-SHA256');
    verify.update(`${header}.${claims}`);
    expect(verify.verify(publicKey, Buffer.from(signature!, 'base64url'))).toBe(true);

    const sendCall = calls[1]!;
    expect(sendCall.url).toBe('https://fcm.googleapis.com/v1/projects/demo-project/messages:send');
    expect(sendCall.init.headers['authorization']).toBe('Bearer ya29.test');
    expect(JSON.parse(sendCall.init.body)).toEqual({
      message: { token: 'device-token', data: { v: '1', h: 'H', p: 'P' }, android: { priority: 'HIGH', ttl: '3600s' } },
    });
  });

  it('reuses the access token until a minute before it expires', async () => {
    const calls: Call[] = [];
    let now = 0;
    const sender = new FcmHttpV1Sender(
      parseServiceAccount(ACCOUNT_JSON),
      fake([TOKEN_REPLY, reply(200, {}), reply(200, {}), TOKEN_REPLY, reply(200, {})], calls),
      () => now,
    );
    await sender.send('t', { v: '1' });
    now = 3_500_000;
    await sender.send('t', { v: '1' });
    now = 3_550_000;
    await sender.send('t', { v: '1' });
    expect(calls.map((c) => c.url.includes('oauth2'))).toEqual([true, false, false, true, false]);
  });

  it('maps a gone token, a bad token and another project to invalid-token', async () => {
    for (const answer of [
      reply(404, { error: { status: 'NOT_FOUND', details: [{ errorCode: 'UNREGISTERED' }] } }),
      reply(400, { error: { status: 'INVALID_ARGUMENT' } }),
      reply(403, { error: { status: 'PERMISSION_DENIED', details: [{ errorCode: 'SENDER_ID_MISMATCH' }] } }),
    ]) {
      const sender = new FcmHttpV1Sender(parseServiceAccount(ACCOUNT_JSON), fake([TOKEN_REPLY, answer], []), () => 0);
      expect(await sender.send('t', { v: '1' })).toEqual({ kind: 'invalid-token' });
    }
  });

  it('asks to retry on 429 and 5xx, honouring Retry-After', async () => {
    const limited = new FcmHttpV1Sender(parseServiceAccount(ACCOUNT_JSON), fake([TOKEN_REPLY, reply(429, {}, { 'retry-after': '120' })], []), () => 0);
    expect(await limited.send('t', { v: '1' })).toEqual({ kind: 'retry', retryAfterSecs: 120 });
    const down = new FcmHttpV1Sender(parseServiceAccount(ACCOUNT_JSON), fake([TOKEN_REPLY, reply(503, {})], []), () => 0);
    expect(await down.send('t', { v: '1' })).toEqual({ kind: 'retry', retryAfterSecs: 60 });
  });

  it('drops the cached access token after a 401', async () => {
    const calls: Call[] = [];
    const sender = new FcmHttpV1Sender(parseServiceAccount(ACCOUNT_JSON), fake([TOKEN_REPLY, reply(401, {}), TOKEN_REPLY, reply(200, {})], calls), () => 0);
    expect(await sender.send('t', { v: '1' })).toEqual({ kind: 'failed', status: 401 });
    expect(await sender.send('t', { v: '1' })).toEqual({ kind: 'sent' });
    expect(calls.filter((c) => c.url.includes('oauth2'))).toHaveLength(2);
  });
});
```

- [ ] **Step 2: Run it and expect it to fail**

Run: RELAY-RUN with `fcm`
Expected: FAIL — `Failed to load url ../src/fcm.ts`.

- [ ] **Step 3: Implement**

Create `relay/src/fcm.ts`:

```ts
import { createSign } from 'node:crypto';
import { readFile } from 'node:fs/promises';

export type PushSendResult =
  | { readonly kind: 'sent' }
  | { readonly kind: 'invalid-token' }
  | { readonly kind: 'retry'; readonly retryAfterSecs: number }
  | { readonly kind: 'failed'; readonly status: number };

/** Delivers one data-only message to one FCM registration token. */
export interface PushSender {
  send(fcmToken: string, data: Readonly<Record<string, string>>): Promise<PushSendResult>;
}

export interface ServiceAccount {
  readonly projectId: string;
  readonly clientEmail: string;
  readonly privateKey: string;
  readonly tokenUri: string;
}

export type FetchLike = (
  url: string,
  init: { method: string; headers: Record<string, string>; body: string },
) => Promise<{ status: number; headers: { get(name: string): string | null }; text(): Promise<string> }>;

const SCOPE = 'https://www.googleapis.com/auth/firebase.messaging';
const DEFAULT_TOKEN_URI = 'https://oauth2.googleapis.com/token';
const DEFAULT_RETRY_SECS = 60;

export function parseServiceAccount(text: string): ServiceAccount {
  let parsed: Record<string, unknown>;
  try {
    parsed = JSON.parse(text) as Record<string, unknown>;
  } catch {
    throw new Error('service account: not JSON');
  }
  const projectId = parsed['project_id'];
  const clientEmail = parsed['client_email'];
  const privateKey = parsed['private_key'];
  const tokenUri = parsed['token_uri'];
  if (typeof projectId !== 'string' || !/^[a-z0-9-]{1,64}$/.test(projectId)) throw new Error('service account: bad project_id');
  if (typeof clientEmail !== 'string' || !clientEmail.includes('@')) throw new Error('service account: bad client_email');
  if (typeof privateKey !== 'string' || !privateKey.includes('PRIVATE KEY')) throw new Error('service account: no private_key');
  return { projectId, clientEmail, privateKey, tokenUri: typeof tokenUri === 'string' ? tokenUri : DEFAULT_TOKEN_URI };
}

function base64url(value: string): string {
  return Buffer.from(value, 'utf8').toString('base64url');
}

function errorCodes(body: string): string[] {
  try {
    const parsed = JSON.parse(body) as { error?: { status?: unknown; details?: Array<{ errorCode?: unknown }> } };
    const codes: string[] = [];
    if (typeof parsed.error?.status === 'string') codes.push(parsed.error.status);
    for (const detail of parsed.error?.details ?? []) {
      if (typeof detail.errorCode === 'string') codes.push(detail.errorCode);
    }
    return codes;
  } catch {
    return [];
  }
}

/** FCM HTTP v1 without firebase-admin: a self-signed RS256 assertion traded for an access token. */
export class FcmHttpV1Sender implements PushSender {
  readonly #account: ServiceAccount;
  readonly #fetch: FetchLike;
  readonly #now: () => number;
  #token: { readonly value: string; readonly expiresAt: number } | null = null;

  constructor(account: ServiceAccount, fetchImpl: FetchLike = fetch as unknown as FetchLike, now: () => number = Date.now) {
    this.#account = account;
    this.#fetch = fetchImpl;
    this.#now = now;
  }

  static async fromFile(path: string, fetchImpl?: FetchLike): Promise<FcmHttpV1Sender> {
    return new FcmHttpV1Sender(parseServiceAccount(await readFile(path, 'utf8')), fetchImpl);
  }

  async send(fcmToken: string, data: Readonly<Record<string, string>>): Promise<PushSendResult> {
    let access: string;
    try {
      access = await this.#accessToken();
    } catch {
      return { kind: 'retry', retryAfterSecs: DEFAULT_RETRY_SECS };
    }
    const response = await this.#fetch(`https://fcm.googleapis.com/v1/projects/${this.#account.projectId}/messages:send`, {
      method: 'POST',
      headers: { authorization: `Bearer ${access}`, 'content-type': 'application/json' },
      body: JSON.stringify({ message: { token: fcmToken, data, android: { priority: 'HIGH', ttl: '3600s' } } }),
    });
    if (response.status === 200) return { kind: 'sent' };
    const codes = errorCodes(await response.text());
    if (response.status === 404 || codes.includes('UNREGISTERED')) return { kind: 'invalid-token' };
    if (response.status === 400 && codes.includes('INVALID_ARGUMENT')) return { kind: 'invalid-token' };
    if (response.status === 403 && codes.includes('SENDER_ID_MISMATCH')) return { kind: 'invalid-token' };
    if (response.status === 401) {
      this.#token = null;
      return { kind: 'failed', status: 401 };
    }
    if (response.status === 429 || response.status >= 500) {
      const after = Number.parseInt(response.headers.get('retry-after') ?? '', 10);
      return { kind: 'retry', retryAfterSecs: Number.isFinite(after) && after > 0 ? after : DEFAULT_RETRY_SECS };
    }
    return { kind: 'failed', status: response.status };
  }

  async #accessToken(): Promise<string> {
    const now = this.#now();
    if (this.#token !== null && now < this.#token.expiresAt - 60_000) return this.#token.value;
    const iat = Math.floor(now / 1000);
    const header = base64url(JSON.stringify({ alg: 'RS256', typ: 'JWT' }));
    const claims = base64url(
      JSON.stringify({ iss: this.#account.clientEmail, scope: SCOPE, aud: this.#account.tokenUri, iat, exp: iat + 3600 }),
    );
    const signer = createSign('RSA-SHA256');
    signer.update(`${header}.${claims}`);
    const assertion = `${header}.${claims}.${signer.sign(this.#account.privateKey).toString('base64url')}`;
    const response = await this.#fetch(this.#account.tokenUri, {
      method: 'POST',
      headers: { 'content-type': 'application/x-www-form-urlencoded' },
      body: new URLSearchParams({ grant_type: 'urn:ietf:params:oauth:grant-type:jwt-bearer', assertion }).toString(),
    });
    if (response.status !== 200) throw new Error(`token ${response.status}`);
    const parsed = JSON.parse(await response.text()) as { access_token?: unknown; expires_in?: unknown };
    if (typeof parsed.access_token !== 'string') throw new Error('token shape');
    const lifetime = typeof parsed.expires_in === 'number' ? parsed.expires_in : 3600;
    this.#token = { value: parsed.access_token, expiresAt: now + lifetime * 1000 };
    return parsed.access_token;
  }
}
```

- [ ] **Step 4: Run it and expect it to pass**

Run: RELAY-RUN with `fcm`, then `cd relay && npx tsc --noEmit`
Expected: 7 tests pass; tsc prints nothing.

- [ ] **Step 5: Commit**

```bash
git add relay/src/fcm.ts relay/test/fcm.test.ts
git commit -m "(relay) the relay can send a data-only FCM message with a service account file and tells a gone token from a busy server" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## Task 3: 폰이 핸들을 받고 호스트가 그 핸들로 푸시를 보낸다

**Files:**
- Create: `relay/src/push.ts`
- Create: `relay/test/push.test.ts`
- Modify: `relay/src/config.ts` (`RelayConfig`·`DEFAULTS`·`loadConfig`에 푸시 항목)
- Modify: `relay/src/server.ts` (`StartRelayOptions.pushSender`, HTTP 처리기에서 푸시 라우트, `close()`에서 저장소 flush)

**Interfaces:**
- Consumes: Task 1 `PushHandleStore`, `SlidingWindowLimiter`, `HANDLE_PATTERN`, `FCM_TOKEN_PATTERN`. Task 2 `PushSender`, `FcmHttpV1Sender.fromFile`. `ownsServerId`, `HOST_TOKEN_PATTERN`, `SERVER_ID_PATTERN` (`protocol.ts`). `clientIp` (`server.ts`).
- Produces:
  - `RelayConfig` 새 필드: `pushStorePath: string`, `fcmServiceAccountFile: string`, `pushRateWindowMs: number`, `pushMaxSendPerServerId: number`, `pushMaxSendPerIp: number`, `pushMaxSendGlobal: number`, `pushMaxHandleOpsPerIp: number`, `pushMaxHandles: number`, `pushHandleIdleDays: number`
  - `PUSH_PAYLOAD_PATTERN = /^[A-Za-z0-9_-]{24,3072}$/`
  - `class PushRoutes { handle(req: IncomingMessage, res: ServerResponse, ip: string): Promise<boolean>; close(): Promise<void> }`
  - `createPushRoutes(config: RelayConfig, log: RelayLogger, sender?: PushSender | null, now?: () => number): Promise<PushRoutes>`
  - `StartRelayOptions.pushSender?: PushSender | null`. `undefined`면 설정 파일로 만들고, `null`이면 끈다.
  - HTTP 계약(아래 표). 오류 본문은 `{ "error": "<code>" }`다.

| 메서드 · 경로 | 인증 | 본문 | 성공 | 오류 |
|---|---|---|---|---|
| `POST /push/v1/handles` | 없음 | `{ fcmToken }` | 201 `{ handle, secret }` | 400 `bad-request` · 429 `rate-limited` · 503 `full` |
| `PUT /push/v1/handles/{handle}` | `Authorization: Bearer <secret>` | `{ fcmToken }` | 204 | 400 · 404 `unknown-handle` · 429 |
| `DELETE /push/v1/handles/{handle}` | `Authorization: Bearer <secret>` | | 204 | 404 · 429 |
| `POST /push/v1/send` | `Authorization: Bearer <hostToken>` | `{ serverId, handle, payload }` | 202 `{ ok: true }` | 400 · 401 `unauthorized` · 403 `foreign-handle` · 404 `unknown-handle` · 410 `token-gone` · 413 `too-large` · 429 `rate-limited`(`Retry-After`) · 502 `fcm-failed`(`Retry-After`) · 503 `push-unavailable` |

FCM 데이터는 `{ v: "1", h: <handle>, p: <payload> }`이다. 요청 본문은 8 KiB를 넘으면 413이다. 핸들 작업(등록·갱신·삭제)은 IP별 한도 하나를 같이 쓴다. 전송은 serverId·IP·전체 한도를 모두 통과해야 하고, 인증과 형식 검사를 지난 시도만 센다.

- [ ] **Step 1: Write the failing test**

Create `relay/test/push.test.ts`:

```ts
import { createHash } from 'node:crypto';

import { afterEach, describe, expect, it } from 'vitest';

import type { PushSendResult, PushSender } from '../src/fcm.ts';
import { startRelay, type RelayHandle } from '../src/server.ts';

const sha256Hex = (value: string): string => createHash('sha256').update(value, 'utf8').digest('hex');

function identity(label: string): { serverId: string; hostToken: string } {
  const hostToken = sha256Hex(`host-secret:${label}`);
  return { serverId: sha256Hex(hostToken), hostToken };
}

class FakeSender implements PushSender {
  readonly sent: Array<{ token: string; data: Readonly<Record<string, string>> }> = [];
  next: PushSendResult = { kind: 'sent' };
  async send(token: string, data: Readonly<Record<string, string>>): Promise<PushSendResult> {
    this.sent.push({ token, data });
    return this.next;
  }
}

const FCM_TOKEN = 'fcm-token-aaaaaaaaaaaaaaaaaaaaaaaaaaaa';
const PAYLOAD = 'A'.repeat(100);
const relays: RelayHandle[] = [];

afterEach(async () => {
  for (const relay of relays.splice(0)) await relay.close();
});

async function relay(overrides: Parameters<typeof startRelay>[0] = {}): Promise<{ base: string; sender: FakeSender }> {
  const sender = new FakeSender();
  const handle = await startRelay({ host: '127.0.0.1', port: 0, logger: () => {}, pushSender: sender, ...overrides });
  relays.push(handle);
  return { base: `http://127.0.0.1:${handle.port}`, sender };
}

function call(base: string, method: string, path: string, body?: unknown, bearer?: string): Promise<Response> {
  const headers: Record<string, string> = { 'content-type': 'application/json' };
  if (bearer !== undefined) headers['authorization'] = `Bearer ${bearer}`;
  return fetch(`${base}${path}`, { method, headers, ...(body === undefined ? {} : { body: JSON.stringify(body) }) });
}

async function register(base: string): Promise<{ handle: string; secret: string }> {
  const res = await call(base, 'POST', '/push/v1/handles', { fcmToken: FCM_TOKEN });
  expect(res.status).toBe(201);
  return (await res.json()) as { handle: string; secret: string };
}

function send(base: string, who: { serverId: string; hostToken: string }, handle: string, payload = PAYLOAD): Promise<Response> {
  return call(base, 'POST', '/push/v1/send', { serverId: who.serverId, handle, payload }, who.hostToken);
}

describe('push handles', () => {
  it('hands out a handle and a secret for a token', async () => {
    const { base } = await relay();
    const made = await register(base);
    expect(made.handle).toMatch(/^[A-Za-z0-9_-]{43}$/);
    expect(made.secret).toMatch(/^[A-Za-z0-9_-]{43}$/);
  });

  it('refuses a malformed token or body', async () => {
    const { base } = await relay();
    expect((await call(base, 'POST', '/push/v1/handles', { fcmToken: 'short' })).status).toBe(400);
    expect((await call(base, 'POST', '/push/v1/handles', 'nope')).status).toBe(400);
  });

  it('refreshes and deletes only with the secret', async () => {
    const { base, sender } = await relay();
    const host = identity('refresh');
    const made = await register(base);
    const NEW = 'fcm-token-bbbbbbbbbbbbbbbbbbbbbbbbbbbb';
    expect((await call(base, 'PUT', `/push/v1/handles/${made.handle}`, { fcmToken: NEW }, 'x'.repeat(43))).status).toBe(404);
    expect((await call(base, 'PUT', `/push/v1/handles/${made.handle}`, { fcmToken: NEW }, made.secret)).status).toBe(204);
    expect((await send(base, host, made.handle)).status).toBe(202);
    expect(sender.sent.at(-1)?.token).toBe(NEW);
    expect((await call(base, 'DELETE', `/push/v1/handles/${made.handle}`, undefined, made.secret)).status).toBe(204);
    expect((await send(base, host, made.handle)).status).toBe(404);
  });

  it('limits handle operations per IP', async () => {
    const { base } = await relay({ pushMaxHandleOpsPerIp: 2 });
    await register(base);
    await register(base);
    const res = await call(base, 'POST', '/push/v1/handles', { fcmToken: FCM_TOKEN });
    expect(res.status).toBe(429);
    expect(Number(res.headers.get('retry-after'))).toBeGreaterThan(0);
  });
});

describe('push send', () => {
  it('passes the sealed payload through untouched to FCM', async () => {
    const { base, sender } = await relay();
    const made = await register(base);
    const res = await send(base, identity('host-a'), made.handle);
    expect(res.status).toBe(202);
    expect(await res.json()).toEqual({ ok: true });
    expect(sender.sent).toEqual([{ token: FCM_TOKEN, data: { v: '1', h: made.handle, p: PAYLOAD } }]);
  });

  it('refuses a host token that does not hash to the serverId', async () => {
    const { base, sender } = await relay();
    const made = await register(base);
    const real = identity('host-a');
    const res = await call(base, 'POST', '/push/v1/send', { serverId: real.serverId, handle: made.handle, payload: PAYLOAD }, identity('other').hostToken);
    expect(res.status).toBe(401);
    expect((await call(base, 'POST', '/push/v1/send', { serverId: real.serverId, handle: made.handle, payload: PAYLOAD })).status).toBe(401);
    expect(sender.sent).toHaveLength(0);
  });

  it('keeps a handle to the first host that used it', async () => {
    const { base, sender } = await relay();
    const made = await register(base);
    expect((await send(base, identity('first'), made.handle)).status).toBe(202);
    const res = await send(base, identity('second'), made.handle);
    expect(res.status).toBe(403);
    expect(await res.json()).toEqual({ error: 'foreign-handle' });
    expect(sender.sent).toHaveLength(1);
  });

  it('forgets a handle whose token FCM calls gone and says 410', async () => {
    const { base, sender } = await relay();
    const made = await register(base);
    sender.next = { kind: 'invalid-token' };
    expect((await send(base, identity('host-a'), made.handle)).status).toBe(410);
    expect((await send(base, identity('host-a'), made.handle)).status).toBe(404);
  });

  it('answers 502 with Retry-After when FCM is busy', async () => {
    const { base, sender } = await relay();
    const made = await register(base);
    sender.next = { kind: 'retry', retryAfterSecs: 12 };
    const res = await send(base, identity('host-a'), made.handle);
    expect(res.status).toBe(502);
    expect(res.headers.get('retry-after')).toBe('12');
  });

  it('refuses an oversized or malformed payload', async () => {
    const { base } = await relay();
    const made = await register(base);
    expect((await send(base, identity('host-a'), made.handle, 'A'.repeat(3073))).status).toBe(413);
    expect((await send(base, identity('host-a'), made.handle, 'not base64url!')).status).toBe(400);
  });

  it('rate-limits per serverId', async () => {
    const { base } = await relay({ pushMaxSendPerServerId: 2 });
    const made = await register(base);
    const host = identity('busy');
    expect((await send(base, host, made.handle)).status).toBe(202);
    expect((await send(base, host, made.handle)).status).toBe(202);
    const res = await send(base, host, made.handle);
    expect(res.status).toBe(429);
    expect(Number(res.headers.get('retry-after'))).toBeGreaterThan(0);
  });

  it('says 503 when no FCM sender is configured', async () => {
    const { base } = await relay({ pushSender: null });
    const made = await register(base);
    expect((await send(base, identity('host-a'), made.handle)).status).toBe(503);
  });

  it('keeps answering /healthz and 404 elsewhere', async () => {
    const { base } = await relay();
    expect((await fetch(`${base}/healthz`)).status).toBe(200);
    expect((await fetch(`${base}/push/v1/nope`)).status).toBe(404);
  });
});
```

- [ ] **Step 2: Run it and expect it to fail**

Run: RELAY-RUN with `push.test`
Expected: FAIL — TypeScript error or a 404 on `/push/v1/handles`. The cause is that `pushSender` is not an option of `startRelay` yet.

- [ ] **Step 3: Implement the config**

In `relay/src/config.ts`, add these fields to `RelayConfig` just before `trustProxy`:

```ts
  /** File for push handles on the server volume; '' keeps them in memory (local runs, tests). */
  readonly pushStorePath: string;
  /** FCM service account JSON; '' disables sending (POST /push/v1/send answers 503). */
  readonly fcmServiceAccountFile: string;
  /** Sliding window for push limits (ms). */
  readonly pushRateWindowMs: number;
  /** Push sends per serverId per window. */
  readonly pushMaxSendPerServerId: number;
  /** Push sends per source IP per window. */
  readonly pushMaxSendPerIp: number;
  /** Push sends across all hosts per window: host identity is self-certified, so this bounds a flood. */
  readonly pushMaxSendGlobal: number;
  /** Handle registrations, refreshes and deletions per source IP per window. */
  readonly pushMaxHandleOpsPerIp: number;
  /** Handles kept at most; idle ones go first. */
  readonly pushMaxHandles: number;
  /** A handle neither refreshed nor used for this many days is dropped. */
  readonly pushHandleIdleDays: number;
```

Add these to `DEFAULTS` before `trustProxy: false`:

```ts
  pushStorePath: '',
  fcmServiceAccountFile: '',
  pushRateWindowMs: 600_000,
  pushMaxSendPerServerId: 120,
  pushMaxSendPerIp: 240,
  pushMaxSendGlobal: 3000,
  pushMaxHandleOpsPerIp: 30,
  pushMaxHandles: 20_000,
  pushHandleIdleDays: 120,
```

Add these to the `fromEnv` object in `loadConfig`, before `trustProxy`:

```ts
    pushStorePath: process.env['PUSH_STORE_PATH']?.trim() ?? DEFAULTS.pushStorePath,
    fcmServiceAccountFile: process.env['FCM_SERVICE_ACCOUNT_FILE']?.trim() ?? DEFAULTS.fcmServiceAccountFile,
    pushRateWindowMs: envInt('PUSH_RATE_WINDOW_MS', DEFAULTS.pushRateWindowMs),
    pushMaxSendPerServerId: envInt('PUSH_MAX_SEND_PER_SERVER_ID', DEFAULTS.pushMaxSendPerServerId),
    pushMaxSendPerIp: envInt('PUSH_MAX_SEND_PER_IP', DEFAULTS.pushMaxSendPerIp),
    pushMaxSendGlobal: envInt('PUSH_MAX_SEND_GLOBAL', DEFAULTS.pushMaxSendGlobal),
    pushMaxHandleOpsPerIp: envInt('PUSH_MAX_HANDLE_OPS_PER_IP', DEFAULTS.pushMaxHandleOpsPerIp),
    pushMaxHandles: envInt('PUSH_MAX_HANDLES', DEFAULTS.pushMaxHandles),
    pushHandleIdleDays: envInt('PUSH_HANDLE_IDLE_DAYS', DEFAULTS.pushHandleIdleDays),
```

- [ ] **Step 4: Implement the routes**

Create `relay/src/push.ts`:

```ts
import type { IncomingMessage, ServerResponse } from 'node:http';

import type { RelayConfig } from './config.ts';
import { FcmHttpV1Sender, type PushSender } from './fcm.ts';
import type { RelayLogger } from './hub.ts';
import { HOST_TOKEN_PATTERN, ownsServerId, SERVER_ID_PATTERN } from './protocol.ts';
import { FCM_TOKEN_PATTERN, HANDLE_PATTERN, PushHandleStore } from './push-store.ts';
import { SlidingWindowLimiter, type RateDecision } from './rate.ts';

/** A sealed push (docs/relay.md "푸시 알림"): base64url, at most 3072 characters. */
export const PUSH_PAYLOAD_PATTERN = /^[A-Za-z0-9_-]{24,3072}$/;
const PUSH_PAYLOAD_MAX = 3072;
const BODY_LIMIT = 8 * 1024;
const DAY_MS = 86_400_000;
const HANDLE_PATH = /^\/push\/v1\/handles\/([A-Za-z0-9_-]{43})$/;

type Body = { readonly ok: true; readonly value: Record<string, unknown> } | { readonly ok: false; readonly status: 400 | 413 };

async function readBody(req: IncomingMessage): Promise<Body> {
  const declared = Number(req.headers['content-length'] ?? '0');
  let size = 0;
  let tooLarge = declared > BODY_LIMIT;
  const chunks: Buffer[] = [];
  for await (const chunk of req as AsyncIterable<Buffer>) {
    size += chunk.length;
    if (size > BODY_LIMIT) tooLarge = true;
    // Keep draining so the socket stays usable for the reply.
    if (!tooLarge) chunks.push(chunk);
  }
  if (tooLarge) return { ok: false, status: 413 };
  try {
    const parsed: unknown = JSON.parse(Buffer.concat(chunks).toString('utf8'));
    if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) return { ok: false, status: 400 };
    return { ok: true, value: parsed as Record<string, unknown> };
  } catch {
    return { ok: false, status: 400 };
  }
}

function bearer(req: IncomingMessage): string | null {
  const header = req.headers['authorization'];
  if (typeof header !== 'string' || !header.startsWith('Bearer ')) return null;
  return header.slice('Bearer '.length).trim();
}

function reply(res: ServerResponse, status: number, body?: unknown, retryAfterSecs?: number): void {
  const headers: Record<string, string> = { 'cache-control': 'no-store' };
  if (retryAfterSecs !== undefined) headers['retry-after'] = String(retryAfterSecs);
  if (body === undefined) {
    res.writeHead(status, headers);
    res.end();
    return;
  }
  headers['content-type'] = 'application/json; charset=utf-8';
  res.writeHead(status, headers);
  res.end(JSON.stringify(body));
}

function failure(res: ServerResponse, status: number, code: string, retryAfterSecs?: number): void {
  reply(res, status, { error: code }, retryAfterSecs);
}

function shortId(serverId: string): string {
  return serverId.slice(0, 8);
}

/** Phone handle registration and host push sends (docs/relay.md "푸시 알림"). */
export class PushRoutes {
  readonly #store: PushHandleStore;
  readonly #sender: PushSender | null;
  readonly #log: RelayLogger;
  readonly #now: () => number;
  readonly #handleOpsByIp: SlidingWindowLimiter;
  readonly #sendByServerId: SlidingWindowLimiter;
  readonly #sendByIp: SlidingWindowLimiter;
  readonly #sendGlobal: SlidingWindowLimiter;
  readonly #sweeper: NodeJS.Timeout;

  constructor(config: RelayConfig, store: PushHandleStore, sender: PushSender | null, log: RelayLogger, now: () => number) {
    this.#store = store;
    this.#sender = sender;
    this.#log = log;
    this.#now = now;
    const window = config.pushRateWindowMs;
    this.#handleOpsByIp = new SlidingWindowLimiter(window, config.pushMaxHandleOpsPerIp);
    this.#sendByServerId = new SlidingWindowLimiter(window, config.pushMaxSendPerServerId);
    this.#sendByIp = new SlidingWindowLimiter(window, config.pushMaxSendPerIp);
    this.#sendGlobal = new SlidingWindowLimiter(window, config.pushMaxSendGlobal);
    this.#sweeper = setInterval(() => {
      const at = this.#now();
      for (const limiter of [this.#handleOpsByIp, this.#sendByServerId, this.#sendByIp, this.#sendGlobal]) limiter.sweep(at);
    }, window);
    this.#sweeper.unref?.();
  }

  /** Handles `/push/v1/*`; false for every other path. */
  async handle(req: IncomingMessage, res: ServerResponse, ip: string): Promise<boolean> {
    const path = new URL(req.url ?? '/', 'http://relay.invalid').pathname;
    if (!path.startsWith('/push/v1/')) return false;
    if (req.method === 'POST' && path === '/push/v1/handles') await this.#register(req, res, ip);
    else if (req.method === 'POST' && path === '/push/v1/send') await this.#send(req, res, ip);
    else if ((req.method === 'PUT' || req.method === 'DELETE') && HANDLE_PATH.test(path)) await this.#manage(req, res, ip, HANDLE_PATH.exec(path)?.[1] ?? '');
    else {
      req.resume();
      reply(res, 404, { error: 'not-found' });
    }
    return true;
  }

  async close(): Promise<void> {
    clearInterval(this.#sweeper);
    await this.#store.flush();
  }

  #take(checks: ReadonlyArray<readonly [SlidingWindowLimiter, string]>): RateDecision {
    const now = this.#now();
    for (const [limiter, key] of checks) {
      const decision = limiter.check(key, now);
      if (!decision.ok) return decision;
    }
    for (const [limiter, key] of checks) limiter.record(key, now);
    return { ok: true };
  }

  async #register(req: IncomingMessage, res: ServerResponse, ip: string): Promise<void> {
    const body = await readBody(req);
    if (!body.ok) return failure(res, body.status, body.status === 413 ? 'too-large' : 'bad-request');
    const token = body.value['fcmToken'];
    if (typeof token !== 'string' || !FCM_TOKEN_PATTERN.test(token)) return failure(res, 400, 'bad-request');
    const rate = this.#take([[this.#handleOpsByIp, ip]]);
    if (!rate.ok) return failure(res, 429, 'rate-limited', rate.retryAfterSecs);
    const made = await this.#store.register(token, this.#now());
    if (made === null) return failure(res, 503, 'full');
    reply(res, 201, made);
  }

  async #manage(req: IncomingMessage, res: ServerResponse, ip: string, handle: string): Promise<void> {
    const secret = bearer(req) ?? '';
    const body = req.method === 'PUT' ? await readBody(req) : null;
    if (req.method !== 'PUT') req.resume();
    if (body !== null && !body.ok) return failure(res, body.status, body.status === 413 ? 'too-large' : 'bad-request');
    const token = body?.ok === true ? body.value['fcmToken'] : undefined;
    if (req.method === 'PUT' && (typeof token !== 'string' || !FCM_TOKEN_PATTERN.test(token))) return failure(res, 400, 'bad-request');
    const rate = this.#take([[this.#handleOpsByIp, ip]]);
    if (!rate.ok) return failure(res, 429, 'rate-limited', rate.retryAfterSecs);
    const done = req.method === 'PUT'
      ? await this.#store.refresh(handle, secret, token as string, this.#now())
      : await this.#store.remove(handle, secret);
    if (!done) return failure(res, 404, 'unknown-handle');
    reply(res, 204);
  }

  async #send(req: IncomingMessage, res: ServerResponse, ip: string): Promise<void> {
    const body = await readBody(req);
    if (!body.ok) return failure(res, body.status, body.status === 413 ? 'too-large' : 'bad-request');
    const { serverId, handle, payload } = body.value;
    const hostToken = bearer(req);
    if (typeof serverId !== 'string' || !SERVER_ID_PATTERN.test(serverId)) return failure(res, 400, 'bad-request');
    if (hostToken === null || !HOST_TOKEN_PATTERN.test(hostToken) || !ownsServerId(serverId, hostToken)) return failure(res, 401, 'unauthorized');
    if (typeof handle !== 'string' || !HANDLE_PATTERN.test(handle)) return failure(res, 400, 'bad-request');
    if (typeof payload === 'string' && payload.length > PUSH_PAYLOAD_MAX) return failure(res, 413, 'too-large');
    if (typeof payload !== 'string' || !PUSH_PAYLOAD_PATTERN.test(payload)) return failure(res, 400, 'bad-request');
    if (this.#sender === null) return failure(res, 503, 'push-unavailable');
    const rate = this.#take([[this.#sendByServerId, serverId], [this.#sendByIp, ip], [this.#sendGlobal, '*']]);
    if (!rate.ok) return failure(res, 429, 'rate-limited', rate.retryAfterSecs);
    const bound = await this.#store.bind(handle, serverId, this.#now());
    if (bound.kind === 'unknown') return failure(res, 404, 'unknown-handle');
    if (bound.kind === 'foreign') return failure(res, 403, 'foreign-handle');
    const result = await this.#sender.send(bound.fcmToken, { v: '1', h: handle, p: payload });
    switch (result.kind) {
      case 'sent':
        this.#log(`[${shortId(serverId)}] push sent`);
        return reply(res, 202, { ok: true });
      case 'invalid-token':
        await this.#store.drop(handle);
        this.#log(`[${shortId(serverId)}] push token gone; handle dropped`);
        return failure(res, 410, 'token-gone');
      case 'retry':
        return failure(res, 502, 'fcm-failed', result.retryAfterSecs);
      case 'failed':
        this.#log(`[${shortId(serverId)}] push failed: fcm ${result.status}`);
        return failure(res, 502, 'fcm-failed', 30);
    }
  }
}

/** The store and the sender the config names; a sender that cannot be read leaves sending off, not the relay. */
export async function createPushRoutes(
  config: RelayConfig,
  log: RelayLogger,
  sender?: PushSender | null,
  now: () => number = Date.now,
): Promise<PushRoutes> {
  const store = await PushHandleStore.open(
    { path: config.pushStorePath, maxHandles: config.pushMaxHandles, idleMs: config.pushHandleIdleDays * DAY_MS, log },
    now(),
  );
  let resolved: PushSender | null = null;
  if (sender !== undefined) resolved = sender;
  else if (config.fcmServiceAccountFile !== '') {
    try {
      resolved = await FcmHttpV1Sender.fromFile(config.fcmServiceAccountFile);
      log('[push] FCM sending on');
    } catch {
      log('[push] FCM disabled: service account unreadable');
    }
  }
  return new PushRoutes(config, store, resolved, log, now);
}
```

In `relay/src/server.ts`:
- Add the import `import { createPushRoutes } from './push.ts';` and the type import `import type { PushSender } from './fcm.ts';`.
- Change `StartRelayOptions` to:

```ts
export interface StartRelayOptions extends Partial<RelayConfig> {
  readonly logger?: RelayLogger;
  /** Overrides the FCM sender the config would build; null turns sending off (tests). */
  readonly pushSender?: PushSender | null;
}
```

- In `startRelay`, change `const { logger, ...overrides } = options;` to `const { logger, pushSender, ...overrides } = options;`.
- Replace `const httpServer: Server = createServer(handleRequest);` with:

```ts
  const push = await createPushRoutes(config, log, pushSender);
  const httpServer: Server = createServer((req, res) => {
    void (async () => {
      try {
        if (await push.handle(req, res, clientIp(req, config.trustProxy))) return;
        handleRequest(req, res);
      } catch {
        if (!res.headersSent) res.writeHead(500, { 'content-type': 'text/plain; charset=utf-8' });
        res.end();
        log('[push] request failed');
      }
    })();
  });
```

- In `close`, after `hub.dispose();`, add `await push.close();`.

- [ ] **Step 5: Run it and expect it to pass**

Run: `cd relay && npm test && npx tsc --noEmit`
Expected: every suite passes (`relay.test.ts` unchanged, plus `push-store`, `fcm` and `push`); tsc prints nothing.

- [ ] **Step 6: Commit**

```bash
git add relay/src/push.ts relay/test/push.test.ts relay/src/config.ts relay/src/server.ts
git commit -m "(relay) a phone can register a push handle and a host that proves its serverId can push a sealed message to it, within per-host, per-IP and global limits" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## Task 4: 배포 키트가 핸들 볼륨과 서비스 계정 키를 싣는다

**Files:**
- Modify: `relay/Dockerfile` (runtime 단계에 `/data`)
- Modify: `relay/deploy/oracle/compose.yaml` (`relay`의 환경·볼륨, 최상위 `relay_data`)
- Modify: `relay/deploy/oracle/setup.sh` (`secrets/` 폴더와 키 파일 옮기기)
- Create: `relay/deploy/oracle/push-check.mjs`
- Modify: `.gitignore`
- Modify: `relay/README.md` (환경 변수 표, "상태를 디스크에 쓰지 않으므로" 문장)
- Modify: `docs/relay.md` (새 절 `## 푸시 알림 (릴레이 평문 봉투, 내용은 E2EE)`을 `## TURN 자격증명 발급` 절 앞에)
- Modify: `docs/relay-oracle.md` (새 절 `## 푸시 알림(FCM) 켜기`를 `## TURN(coturn) 배포와 확인` 앞에)

**Interfaces:**
- Consumes: Task 3의 환경 변수 `PUSH_STORE_PATH`, `FCM_SERVICE_ACCOUNT_FILE`, `PUSH_*`.
- Produces: 컨테이너 안 `/data/push-handles.json`(이름 있는 볼륨 `relay_data`)과 `/run/mighty-secrets/fcm-service-account.json`(읽기 전용 바인드). 실제 서버 점검 `node relay/deploy/oracle/push-check.mjs [도메인]`.

- [ ] **Step 1: Dockerfile**

In the `runtime` stage, insert this line right before `USER node`:

```dockerfile
# The push handle store (PUSH_STORE_PATH) lives on a named volume mounted here;
# a fresh volume copies this owner, so the non-root user can write it.
RUN mkdir -p /data && chown node:node /data
```

- [ ] **Step 2: compose.yaml**

Add these lines to `relay.environment` after `TURN_MAX_GLOBAL: "60"`:

```yaml
      # Push (docs/relay.md "푸시 알림"): handles survive redeploys on relay_data;
      # without the key file sending answers 503 and everything else still works.
      PUSH_STORE_PATH: "/data/push-handles.json"
      FCM_SERVICE_ACCOUNT_FILE: "/run/mighty-secrets/fcm-service-account.json"
      PUSH_RATE_WINDOW_MS: "600000"
      PUSH_MAX_SEND_PER_SERVER_ID: "120"
      PUSH_MAX_SEND_PER_IP: "240"
      PUSH_MAX_SEND_GLOBAL: "3000"
      PUSH_MAX_HANDLE_OPS_PER_IP: "30"
```

Add this under `relay:`, after the `environment` map:

```yaml
    volumes:
      - relay_data:/data
      # setup.sh makes this folder (owner uid 1000 = the image's node user, mode 500).
      - ./secrets:/run/mighty-secrets:ro
```

Add `relay_data:` to the top-level `volumes:` list, next to `caddy_data:` and `caddy_config:`.

- [ ] **Step 3: setup.sh**

Insert this block right before the line `echo "▶ 릴레이와 Caddy와 coturn을 빌드하고 시작합니다 (첫 실행은 몇 분 걸립니다)..."`:

```bash
# ── FCM 서비스 계정 키 (푸시 알림) ───────────────────────────────────────────
# 키 파일은 저장소에 넣지 않는다. 사용자가 이 폴더에 fcm-service-account.json을
# 올려 두면 secrets/로 옮겨 컨테이너의 node 사용자(uid 1000)만 읽게 한다.
# 키가 없으면 푸시 전송만 꺼진다(503). 핸들 등록과 나머지는 그대로 동작한다.
sudo install -d -o 1000 -g 1000 -m 500 secrets
if [ -f fcm-service-account.json ]; then
    sudo install -o 1000 -g 1000 -m 400 fcm-service-account.json secrets/fcm-service-account.json
    rm -f fcm-service-account.json
    echo "▶ FCM 서비스 계정 키를 secrets/로 옮겼습니다."
fi
if sudo test -f secrets/fcm-service-account.json; then
    echo "▶ 푸시 알림 전송: 켜짐"
else
    echo "▶ 푸시 알림 전송: 꺼짐 (relay/deploy/oracle/fcm-service-account.json을 올리고 다시 실행하면 켜집니다)"
fi
```

- [ ] **Step 4: push-check.mjs**

Create `relay/deploy/oracle/push-check.mjs`:

```js
// 용법: node relay/deploy/oracle/push-check.mjs [도메인]
//
// 살아 있는 릴레이의 푸시 경로를 확인한다(docs/relay.md "푸시 알림"). 임시 호스트
// 신원과 가짜 FCM 토큰으로 핸들을 하나 만들고 그 핸들로 보내 본다. 가짜 토큰이므로
// FCM은 거절해야 하고, 릴레이는 410(token-gone)으로 답하고 핸들을 지워야 한다.
// 503이면 서비스 계정 키가 없거나 읽히지 않은 것이다. 실제 기기에는 아무것도 가지 않는다.
import { createHash, randomBytes } from 'node:crypto';

const domain = process.argv[2] ?? 'mightyclaude.duckdns.org';
const base = `https://${domain}`;
const hostToken = randomBytes(32).toString('hex');
const serverId = createHash('sha256').update(hostToken, 'utf8').digest('hex');

async function post(path, body, bearer) {
  const headers = { 'content-type': 'application/json' };
  if (bearer) headers.authorization = `Bearer ${bearer}`;
  const res = await fetch(base + path, { method: 'POST', headers, body: JSON.stringify(body) });
  return { status: res.status, body: await res.text() };
}

const registered = await post('/push/v1/handles', { fcmToken: `push-check-${randomBytes(16).toString('hex')}` });
if (registered.status !== 201) {
  console.error(`✘ 핸들 등록이 ${registered.status}로 답했습니다: ${registered.body}`);
  process.exit(1);
}
const { handle } = JSON.parse(registered.body);
const forged = await post('/push/v1/send', { serverId, handle, payload: 'A'.repeat(64) }, randomBytes(32).toString('hex'));
const sent = await post('/push/v1/send', { serverId, handle, payload: 'A'.repeat(64) }, hostToken);

const problems = [];
if (forged.status !== 401) problems.push(`틀린 호스트 토큰이 401이 아니라 ${forged.status}`);
if (sent.status === 503) problems.push('FCM이 꺼져 있습니다(서비스 계정 키 없음 또는 읽기 실패)');
else if (sent.status !== 410) problems.push(`가짜 토큰 전송이 410이 아니라 ${sent.status}: ${sent.body}`);
if (problems.length > 0) {
  for (const line of problems) console.error(`✘ ${line}`);
  process.exit(1);
}
console.log('✔ 푸시 경로: 호스트 인증 401, FCM까지 가서 가짜 토큰을 410으로 지움');
```

- [ ] **Step 5: .gitignore**

Append to `/Users/young/Work/MightyClaude-firebase/.gitignore`:

```gitignore

# Firebase and FCM secrets stay outside git (docs/relay-oracle.md, docs/mobile-remote.md).
mobile/google-services.json
relay/deploy/oracle/secrets/
relay/deploy/oracle/fcm-service-account.json
```

- [ ] **Step 6: Docs**

`relay/README.md`:
- Add these rows to the 환경 변수 표 under the `RELAY_MAX_PAYLOAD` row:

```markdown
| `PUSH_STORE_PATH` | (빈 값) | 푸시 핸들 파일. 비면 메모리에만 둔다(로컬 실행) |
| `FCM_SERVICE_ACCOUNT_FILE` | (빈 값) | FCM 서비스 계정 JSON 경로. 비거나 읽을 수 없으면 `POST /push/v1/send`가 503 |
| `PUSH_RATE_WINDOW_MS` | `600000` | 푸시 한도 창 |
| `PUSH_MAX_SEND_PER_SERVER_ID` · `PUSH_MAX_SEND_PER_IP` · `PUSH_MAX_SEND_GLOBAL` | `120` · `240` · `3000` | 창마다 전송 수 |
| `PUSH_MAX_HANDLE_OPS_PER_IP` | `30` | 창마다 IP당 핸들 등록·갱신·삭제 수 |
| `PUSH_MAX_HANDLES` · `PUSH_HANDLE_IDLE_DAYS` | `20000` · `120` | 핸들 수 상한, 쓰이지 않은 핸들을 지우는 날 수 |
```

- Replace the sentence "상태를 디스크에 쓰지 않으므로 볼륨이 필요 없고," with "푸시 핸들만 `PUSH_STORE_PATH` 파일에 쓰므로(배포 키트는 이름 있는 볼륨 `relay_data`를 `/data`에 붙인다),".
- In "릴레이는 암호문만 본다", add the sentence: "푸시 봉투도 호스트가 폰 키로 봉인한 암호문이고, 릴레이는 핸들→FCM 토큰만 안다."

`docs/relay.md`: insert this section before `## TURN 자격증명 발급 (릴레이 평문, E2EE 아님)`:

````markdown
## 푸시 알림 (릴레이 평문 봉투, 내용은 E2EE)

호스트(Mac·Windows)가 폰에 알림을 보낼 때 쓰는 HTTP 라우트다. 릴레이는 **핸들 → FCM 토큰**만 알고, 내용은 호스트가 폰 키로 봉인한 봉투(`payload`)라 열 수 없다. 봉인과 평문 모양은 `docs/mobile-remote.md` "푸시 알림"이다.

| 메서드 · 경로 | 인증 | 본문 | 성공 | 오류(`{ "error": "<code>" }`) |
|---|---|---|---|---|
| `POST /push/v1/handles` | 없음 | `{ fcmToken }` | 201 `{ handle, secret }` | 400 `bad-request` · 429 `rate-limited` · 503 `full` |
| `PUT /push/v1/handles/{handle}` | `Authorization: Bearer <secret>` | `{ fcmToken }` | 204 | 400 · 404 `unknown-handle` · 429 |
| `DELETE /push/v1/handles/{handle}` | `Authorization: Bearer <secret>` | | 204 | 404 · 429 |
| `POST /push/v1/send` | `Authorization: Bearer <hostToken>` | `{ serverId, handle, payload }` | 202 `{ ok: true }` | 400 · 401 `unauthorized` · 403 `foreign-handle` · 404 `unknown-handle` · 410 `token-gone` · 413 `too-large` · 429 `rate-limited` · 502 `fcm-failed` · 503 `push-unavailable` |

- **핸들**과 **비밀**은 각각 32 무작위 바이트 base64url(43자)이다. 폰은 핸들을 페어링된 호스트에게만(m1 `POST /m1/push/register`) 주고, 비밀은 자기만 갖는다. 비밀 없이는 핸들의 토큰을 바꾸거나 지울 수 없다. 릴레이는 비밀의 SHA-256만 저장한다.
- **호스트 인증**은 제어 소켓과 같다: `SHA-256(hostToken) == serverId`. 릴레이는 핸들을 처음 보낸 `serverId`에 묶고, 다른 `serverId`의 전송은 403이다. 폰은 호스트마다 다른 핸들을 받는다.
- **FCM 메시지**: 데이터 전용 `{ v: "1", h: <handle>, p: <payload> }`, `android.priority: HIGH`, `ttl: 3600s`. `payload`는 `^[A-Za-z0-9_-]{24,3072}$`.
- **토큰 정리**: FCM이 토큰을 무효라고 하면(404 `UNREGISTERED`, 400 `INVALID_ARGUMENT`, 403 `SENDER_ID_MISMATCH`) 릴레이가 핸들을 지우고 410으로 답한다. 호스트는 410·404·403을 받으면 그 기기의 핸들을 지운다. FCM이 바쁘면(429·5xx) 502와 `Retry-After`다.
- **한도**(TURN과 같은 슬라이딩 창, 기본 10분): 전송은 serverId당 120, IP당 240, 전체 3000. 핸들 작업은 IP당 30. 넘으면 429와 `Retry-After`.
- **저장**: `PUSH_STORE_PATH`의 JSON 파일(0600). 120일 동안 쓰이지 않은 핸들은 지운다. 핸들은 최대 2만 개다. 읽을 수 없는 파일은 `.unreadable-<시각>`으로 옮기고 빈 상태로 시작한다.
- **로그**: `[serverId 앞 8자] push sent` 같은 줄만 남긴다. 핸들·토큰·봉투는 남기지 않는다.
- 서비스 계정 키(`FCM_SERVICE_ACCOUNT_FILE`)가 없거나 읽히지 않으면 전송만 503이고 나머지는 그대로다.
````

`docs/relay-oracle.md`: insert this section before `## TURN(coturn) 배포와 확인 — 원격 화면 BETA`:

````markdown
## 푸시 알림(FCM) 켜기

폰 알림은 릴레이가 FCM으로 보낸다. 키는 서버에만 둔다(저장소·앱에 넣지 않는다).

1. Firebase 콘솔 → 프로젝트 설정 → 서비스 계정 → **새 비공개 키 생성**. JSON 파일을 받는다. 이 키에는 Firebase Cloud Messaging API 권한이 있다. Google Cloud 콘솔에서 그 프로젝트의 "Firebase Cloud Messaging API"가 사용 설정되어 있는지 확인한다.
2. 받은 파일을 서버의 `relay/deploy/oracle/fcm-service-account.json`으로 올린다. 예: `scp <받은 파일> <서버>:~/<저장소>/relay/deploy/oracle/fcm-service-account.json`.
3. 서버에서 `bash relay/deploy/oracle/setup.sh`를 다시 실행한다. 키를 `secrets/`로 옮기고(읽기 전용, 컨테이너 사용자만) 릴레이를 다시 빌드한다. "푸시 알림 전송: 켜짐"이 보여야 한다.
4. Mac에서 `node relay/deploy/oracle/push-check.mjs <도메인>`을 실행한다. `✔ 푸시 경로`가 보이면 된다.

핸들 파일은 이름 있는 볼륨 `relay_data`에 있어 재배포해도 남는다. 키를 바꾸려면 새 파일을 같은 이름으로 올리고 3을 다시 한다.
````

- [ ] **Step 7: Check**

Run: `cd relay && npm test && npx tsc --noEmit && bash -n deploy/oracle/setup.sh && node --check deploy/oracle/push-check.mjs && git check-ignore -v ../mobile/google-services.json deploy/oracle/secrets/x deploy/oracle/fcm-service-account.json`
Expected: tests pass; `bash -n` and `node --check` print nothing; `git check-ignore` prints three lines, each naming the new `.gitignore` entries.

- [ ] **Step 8: Commit**

```bash
git add relay/Dockerfile relay/deploy/oracle/compose.yaml relay/deploy/oracle/setup.sh relay/deploy/oracle/push-check.mjs .gitignore relay/README.md docs/relay.md docs/relay-oracle.md
git commit -m "(relay) the deploy kit keeps push handles on a volume, takes the FCM service account key from a server-only folder, and checks the live push path; the relay docs describe the push routes" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

- [ ] **Step 9: 사용자 설정 A — 서비스 계정 키와 재배포 (사용자가 직접)**

Tell the user. It does not block the next tasks; the live check moves to Task 21 Step 8.
- Firebase 콘솔 → 프로젝트 설정 → 서비스 계정 → 새 비공개 키를 받는다. Firebase Cloud Messaging API가 켜져 있는지 확인한다.
- 이 브랜치가 `main`에 들어가 서버가 그 코드를 받은 뒤, `docs/relay-oracle.md` "푸시 알림(FCM) 켜기"의 2~4단계를 한다(키 업로드, `setup.sh` 재실행, `push-check.mjs`).

---

## Task 5: 공유 푸시 픽스처와 Mac 봉인

**Files:**
- Create: `native/contracts/fixtures/push.json`
- Create: `native/macos/Sources/MightyCore/Remote/MobilePush.swift`
- Create: `native/macos/Tests/MightyCoreTests/MobilePushTests.swift`
- Modify: `native/contracts/README.md` (새 절 `## Push notifications (\`fixtures/push.json\`)`을 파일 끝, agy가 더한 `## Antigravity permission cards` 절 뒤에)

**Interfaces:**
- Consumes: CryptoKit `HKDF<SHA256>`, `ChaChaPoly`. `MightyError(_:)`.
- Produces:
  - 픽스처 모양 `{ seal: { deviceToken, clientId, handle, tokenHash, keyB64, nonceB64, aad, plaintext, envelope, tampered: [String], otherHandle }, planner: { hostId, hostName, at, steps: [{ note, panes: [MobilePushPane], expect: [{ id, kind, sessionId, workspace, title, summary, permission? }] }] } }`
  - `public enum MobilePushSeal { static let info: String; static let version: UInt8; static let maximumPlaintext: Int; static let maximumEnvelope: Int; static func key(tokenHash: String, clientId: String) -> SymmetricKey?; static func seal(_ plaintext: Data, key: SymmetricKey, handle: String, nonce: ChaChaPoly.Nonce = ChaChaPoly.Nonce()) throws -> String; static func open(_ envelope: String, key: SymmetricKey, handle: String) -> Data? }`
  - Swift suite `MobilePushSealTests`.

**픽스처 값.** 가짜 기기 토큰·clientId·핸들로 node `crypto`가 만든 값이고, `@noble/*`로 다시 열어 확인했다. 계획기 단계의 규칙은 Task 6이 적는다.

- [ ] **Step 1: Write the fixture**

Create `native/contracts/fixtures/push.json` with exactly this content:

```json
{
  "seal": {
    "deviceToken": "AQgPFh0kKzI5QEdOVVxjanF4f4aNlJuiqbC3vsXM09o",
    "clientId": "AAECAwQFBgcICQoLDA0ODw",
    "handle": "__79_Pv6-fj39vX08_Lx8O_u7ezr6uno5-bl5OPi4eA",
    "tokenHash": "5723c1b7256eba050781d42f7d4fd96201c4b75d7673b1a82ffbcba6dc2e1c23",
    "keyB64": "JzXs+EzwuV8UDZFIKJvoYxb9N5riD9wfYDeNtmQMRYY=",
    "nonceB64": "AAECAwQFBgcICQoL",
    "aad": "mighty-push-v1:__79_Pv6-fj39vX08_Lx8O_u7ezr6uno5-bl5OPi4eA",
    "plaintext": "{\"v\":1,\"id\":\"request:run-1:req-1\",\"kind\":\"permission\",\"hostId\":\"host-1\",\"hostName\":\"Test Mac\",\"workspace\":\"demo\",\"title\":\"Claude 1\",\"summary\":\"git status\",\"sessionId\":\"pane-1\",\"permission\":{\"requestId\":\"req-1\",\"runId\":\"run-1\",\"buttons\":\"claude\"},\"at\":\"2026-10-23T01:02:03Z\"}",
    "envelope": "AQABAgMEBQYHCAkKC10m-R6zdQ5pj0SATLOGKPuGaNgTwb_MKx-ihIwRZL_sZZZXABwjmFdYp_YG5eEHDYzWBUpeDORPjZiglEjtaNTLIGiieN3u6W5va7UjojkWOwBuFmQHuom51zcUdFbVrKkXJkQJslIBeTZsmjelyejmlsVcDKH_D69ezWZoeowWQNzrVg6aBeBLSUIMfOmtkCr_6ei6MP19DqKTKjBxilR3irTCy1_I_QQ3UGu6IqRwMgBp6PP9BnXiImYr6p08WRcTEaj_qHhFhquPgwLs70dN-P1GfkVRYZSZ-oszZSmXRhp2JD6ZLJd_kQ-BmO29g26uLryt_upXRS_AT80vu93stMX0Xbp6S9wwq3ZDzvG0vbujqZv_9e1_AwVeLDOv",
    "tampered": [
      "AQABAgMEBQYHCAkKC10m-R6zdQ5oj0SATLOGKPuGaNgTwb_MKx-ihIwRZL_sZZZXABwjmFdYp_YG5eEHDYzWBUpeDORPjZiglEjtaNTLIGiieN3u6W5va7UjojkWOwBuFmQHuom51zcUdFbVrKkXJkQJslIBeTZsmjelyejmlsVcDKH_D69ezWZoeowWQNzrVg6aBeBLSUIMfOmtkCr_6ei6MP19DqKTKjBxilR3irTCy1_I_QQ3UGu6IqRwMgBp6PP9BnXiImYr6p08WRcTEaj_qHhFhquPgwLs70dN-P1GfkVRYZSZ-oszZSmXRhp2JD6ZLJd_kQ-BmO29g26uLryt_upXRS_AT80vu93stMX0Xbp6S9wwq3ZDzvG0vbujqZv_9e1_AwVeLDOv",
      "AgABAgMEBQYHCAkKC10m-R6zdQ5pj0SATLOGKPuGaNgTwb_MKx-ihIwRZL_sZZZXABwjmFdYp_YG5eEHDYzWBUpeDORPjZiglEjtaNTLIGiieN3u6W5va7UjojkWOwBuFmQHuom51zcUdFbVrKkXJkQJslIBeTZsmjelyejmlsVcDKH_D69ezWZoeowWQNzrVg6aBeBLSUIMfOmtkCr_6ei6MP19DqKTKjBxilR3irTCy1_I_QQ3UGu6IqRwMgBp6PP9BnXiImYr6p08WRcTEaj_qHhFhquPgwLs70dN-P1GfkVRYZSZ-oszZSmXRhp2JD6ZLJd_kQ-BmO29g26uLryt_upXRS_AT80vu93stMX0Xbp6S9wwq3ZDzvG0vbujqZv_9e1_AwVeLDOv"
    ],
    "otherHandle": "BwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwc"
  },
  "planner": {
    "hostId": "host-1",
    "hostName": "Test Mac",
    "at": "2026-10-23T01:02:03Z",
    "steps": [
      {
        "note": "the first observation is the baseline: nothing is sent",
        "panes": [
          {
            "sessionId": "pane-1",
            "workspace": "demo",
            "title": "Claude 1",
            "status": "running",
            "requests": [],
            "signInLost": false
          },
          {
            "sessionId": "pane-2",
            "workspace": "demo",
            "title": "Antigravity",
            "status": "completed",
            "requests": [],
            "signInLost": false
          }
        ],
        "expect": []
      },
      {
        "note": "a new request is sent once",
        "panes": [
          {
            "sessionId": "pane-1",
            "workspace": "demo",
            "title": "Claude 1",
            "status": "running",
            "requests": [
              {
                "id": "req-1",
                "runId": "run-1",
                "kind": "permission",
                "summary": "git status",
                "buttons": "claude"
              }
            ],
            "signInLost": false
          },
          {
            "sessionId": "pane-2",
            "workspace": "demo",
            "title": "Antigravity",
            "status": "completed",
            "requests": [],
            "signInLost": false
          }
        ],
        "expect": [
          {
            "id": "request:run-1:req-1",
            "kind": "permission",
            "sessionId": "pane-1",
            "workspace": "demo",
            "title": "Claude 1",
            "summary": "git status",
            "permission": {
              "requestId": "req-1",
              "runId": "run-1",
              "buttons": "claude"
            }
          }
        ]
      },
      {
        "note": "the same request seen again is not sent again",
        "panes": [
          {
            "sessionId": "pane-1",
            "workspace": "demo",
            "title": "Claude 1",
            "status": "running",
            "requests": [
              {
                "id": "req-1",
                "runId": "run-1",
                "kind": "permission",
                "summary": "git status",
                "buttons": "claude"
              }
            ],
            "signInLost": false
          },
          {
            "sessionId": "pane-2",
            "workspace": "demo",
            "title": "Antigravity",
            "status": "completed",
            "requests": [],
            "signInLost": false
          }
        ],
        "expect": []
      },
      {
        "note": "running to completed with nothing pending sends the preview on one line",
        "panes": [
          {
            "sessionId": "pane-1",
            "workspace": "demo",
            "title": "Claude 1",
            "status": "completed",
            "requests": [],
            "signInLost": false,
            "preview": "Done.\nAll tests pass"
          },
          {
            "sessionId": "pane-2",
            "workspace": "demo",
            "title": "Antigravity",
            "status": "completed",
            "requests": [],
            "signInLost": false
          }
        ],
        "expect": [
          {
            "id": "run:pane-1:1:completed",
            "kind": "completed",
            "sessionId": "pane-1",
            "workspace": "demo",
            "title": "Claude 1",
            "summary": "Done. All tests pass"
          }
        ]
      },
      {
        "note": "a new run starts; a plan request on another pane is sent without buttons",
        "panes": [
          {
            "sessionId": "pane-1",
            "workspace": "demo",
            "title": "Claude 1",
            "status": "running",
            "requests": [],
            "signInLost": false
          },
          {
            "sessionId": "pane-2",
            "workspace": "demo",
            "title": "Antigravity",
            "status": "completed",
            "requests": [
              {
                "id": "plan-1",
                "runId": "run-2",
                "kind": "plan",
                "summary": "Refactor the parser",
                "buttons": "none"
              }
            ],
            "signInLost": false
          }
        ],
        "expect": [
          {
            "id": "request:run-2:plan-1",
            "kind": "plan",
            "sessionId": "pane-2",
            "workspace": "demo",
            "title": "Antigravity",
            "summary": "Refactor the parser"
          }
        ]
      },
      {
        "note": "running to error is a failure",
        "panes": [
          {
            "sessionId": "pane-1",
            "workspace": "demo",
            "title": "Claude 1",
            "status": "error",
            "requests": [],
            "signInLost": false,
            "preview": "boom"
          },
          {
            "sessionId": "pane-2",
            "workspace": "demo",
            "title": "Antigravity",
            "status": "completed",
            "requests": [
              {
                "id": "plan-1",
                "runId": "run-2",
                "kind": "plan",
                "summary": "Refactor the parser",
                "buttons": "none"
              }
            ],
            "signInLost": false
          }
        ],
        "expect": [
          {
            "id": "run:pane-1:2:failed",
            "kind": "failed",
            "sessionId": "pane-1",
            "workspace": "demo",
            "title": "Claude 1",
            "summary": "boom"
          }
        ]
      },
      {
        "note": "a lost sign-in is sent once",
        "panes": [
          {
            "sessionId": "pane-1",
            "workspace": "demo",
            "title": "Claude 1",
            "status": "error",
            "requests": [],
            "signInLost": true,
            "preview": "boom"
          },
          {
            "sessionId": "pane-2",
            "workspace": "demo",
            "title": "Antigravity",
            "status": "completed",
            "requests": [],
            "signInLost": false
          }
        ],
        "expect": [
          {
            "id": "signin:pane-1:1",
            "kind": "signin",
            "sessionId": "pane-1",
            "workspace": "demo",
            "title": "Claude 1",
            "summary": ""
          }
        ]
      },
      {
        "note": "a pane that appears after the baseline sends its first request; a long title is clipped to 80",
        "panes": [
          {
            "sessionId": "pane-1",
            "workspace": "demo",
            "title": "Claude 1",
            "status": "error",
            "requests": [],
            "signInLost": true
          },
          {
            "sessionId": "pane-2",
            "workspace": "demo",
            "title": "Antigravity",
            "status": "completed",
            "requests": [],
            "signInLost": false
          },
          {
            "sessionId": "pane-3",
            "workspace": "other",
            "title": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "status": "running",
            "requests": [
              {
                "id": "q-1",
                "runId": "run-3",
                "kind": "question",
                "summary": "Which branch?",
                "buttons": "none"
              }
            ],
            "signInLost": false
          }
        ],
        "expect": [
          {
            "id": "request:run-3:q-1",
            "kind": "question",
            "sessionId": "pane-3",
            "workspace": "other",
            "title": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "summary": "Which branch?"
          }
        ]
      },
      {
        "note": "a stopped run sends nothing",
        "panes": [
          {
            "sessionId": "pane-1",
            "workspace": "demo",
            "title": "Claude 1",
            "status": "error",
            "requests": [],
            "signInLost": true
          },
          {
            "sessionId": "pane-2",
            "workspace": "demo",
            "title": "Antigravity",
            "status": "completed",
            "requests": [],
            "signInLost": false
          },
          {
            "sessionId": "pane-3",
            "workspace": "other",
            "title": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "status": "stopped",
            "requests": [],
            "signInLost": false
          },
          {
            "sessionId": "pane-4",
            "workspace": "demo",
            "title": "Antigravity 2",
            "status": "running",
            "requests": [],
            "signInLost": false
          }
        ],
        "expect": []
      },
      {
        "note": "a run that ends while a card waits sends only the card",
        "panes": [
          {
            "sessionId": "pane-1",
            "workspace": "demo",
            "title": "Claude 1",
            "status": "error",
            "requests": [],
            "signInLost": true
          },
          {
            "sessionId": "pane-2",
            "workspace": "demo",
            "title": "Antigravity",
            "status": "completed",
            "requests": [],
            "signInLost": false
          },
          {
            "sessionId": "pane-3",
            "workspace": "other",
            "title": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "status": "stopped",
            "requests": [],
            "signInLost": false
          },
          {
            "sessionId": "pane-4",
            "workspace": "demo",
            "title": "Antigravity 2",
            "status": "completed",
            "requests": [
              {
                "id": "agy-1",
                "runId": "run-4",
                "kind": "permission",
                "summary": "rm -rf build",
                "buttons": "antigravityOnce"
              }
            ],
            "signInLost": false
          }
        ],
        "expect": [
          {
            "id": "request:run-4:agy-1",
            "kind": "permission",
            "sessionId": "pane-4",
            "workspace": "demo",
            "title": "Antigravity 2",
            "summary": "rm -rf build",
            "permission": {
              "requestId": "agy-1",
              "runId": "run-4",
              "buttons": "antigravityOnce"
            }
          }
        ]
      }
    ]
  }
}
```

- [ ] **Step 2: Write the failing test**

Create `native/macos/Tests/MightyCoreTests/MobilePushTests.swift`:

```swift
import CryptoKit
import Foundation
import Testing
@testable import MightyCore

/// `native/contracts/fixtures/push.json`, read by the Mac, Windows (`MobilePushVerification`) and the phone (`push-seal.test.ts`).
enum PushFixture {
    static let url: URL = {
        var url = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { url.deleteLastPathComponent() }
        return url.appendingPathComponent("native/contracts/fixtures/push.json")
    }()
    struct Seal: Decodable {
        let deviceToken, clientId, handle, tokenHash, keyB64, nonceB64, aad, plaintext, envelope, otherHandle: String
        let tampered: [String]
    }
    struct Expected: Decodable {
        let id: String
        let kind: MobilePushKind
        let sessionId, workspace, title, summary: String
        let permission: MobilePushPermission?
    }
    struct Step: Decodable {
        let note: String
        let panes: [MobilePushPane]
        let expect: [Expected]
    }
    struct Planner: Decodable {
        let hostId, hostName, at: String
        let steps: [Step]
    }
    struct File: Decodable {
        let seal: Seal
        let planner: Planner
    }
    static func load() throws -> File { try JSONDecoder().decode(File.self, from: Data(contentsOf: url)) }
}

struct MobilePushSealTests {
    @Test func theKeyComesFromTheTokenHashAndTheClientId() throws {
        let seal = try PushFixture.load().seal
        let key = try #require(MobilePushSeal.key(tokenHash: seal.tokenHash, clientId: seal.clientId))
        #expect(key.withUnsafeBytes { Data($0) }.base64EncodedString() == seal.keyB64)
        #expect(MobileDeviceRegistry.hash(seal.deviceToken) == seal.tokenHash)
        #expect(MobilePushSeal.key(tokenHash: "not hex", clientId: seal.clientId) == nil)
    }

    @Test func sealingWithTheFixtureNonceGivesTheFixtureEnvelope() throws {
        let seal = try PushFixture.load().seal
        let key = try #require(MobilePushSeal.key(tokenHash: seal.tokenHash, clientId: seal.clientId))
        let nonce = try ChaChaPoly.Nonce(data: try #require(Data(base64Encoded: seal.nonceB64)))
        #expect(try MobilePushSeal.seal(Data(seal.plaintext.utf8), key: key, handle: seal.handle, nonce: nonce) == seal.envelope)
    }

    @Test func openingChecksTheTagTheVersionAndTheHandle() throws {
        let seal = try PushFixture.load().seal
        let key = try #require(MobilePushSeal.key(tokenHash: seal.tokenHash, clientId: seal.clientId))
        #expect(MobilePushSeal.open(seal.envelope, key: key, handle: seal.handle) == Data(seal.plaintext.utf8))
        for tampered in seal.tampered { #expect(MobilePushSeal.open(tampered, key: key, handle: seal.handle) == nil) }
        #expect(MobilePushSeal.open(seal.envelope, key: key, handle: seal.otherHandle) == nil)
        #expect(MobilePushSeal.open(String(repeating: "A", count: 3_073), key: key, handle: seal.handle) == nil)
    }

    @Test func aMessageOverTheLimitIsNotSealed() throws {
        let seal = try PushFixture.load().seal
        let key = try #require(MobilePushSeal.key(tokenHash: seal.tokenHash, clientId: seal.clientId))
        #expect(throws: MightyError.self) { try MobilePushSeal.seal(Data(count: 2_049), key: key, handle: seal.handle) }
        let longest = try MobilePushSeal.seal(Data(count: 2_048), key: key, handle: seal.handle)
        #expect(longest.count <= MobilePushSeal.maximumEnvelope)
    }
}
```

- [ ] **Step 3: Run it and expect it to fail**

Run: MAC-RUN with `'\.(MobilePushSealTests)/'`
Expected: build error `cannot find 'MobilePushSeal' in scope`.

- [ ] **Step 4: Implement**

Create `native/macos/Sources/MightyCore/Remote/MobilePush.swift`:

```swift
import CryptoKit
import Foundation

/// The seal on a push (docs/mobile-remote.md "푸시 알림"): only the phone that holds the
/// device token can open it. The relay and FCM carry it blind.
///
/// key = HKDF-SHA256(ikm: SHA-256(UTF-8 deviceToken), salt: UTF-8 clientId, info: "mighty-push-v1", 32 bytes).
/// The host stores exactly that SHA-256 as `MobileDevice.tokenHash`, so it needs no new secret;
/// a phone on the relay and one on the direct path authenticate with the same token, so both get the same key.
/// envelope = base64url(0x01 ‖ nonce(12) ‖ ChaCha20-Poly1305(plaintext, aad: "mighty-push-v1:" + handle) ‖ tag(16)).
public enum MobilePushSeal {
    public static let info = "mighty-push-v1"
    public static let version: UInt8 = 1
    public static let maximumPlaintext = 2_048
    public static let maximumEnvelope = 3_072

    public static func key(tokenHash: String, clientId: String) -> SymmetricKey? {
        guard tokenHash.count == 64, let ikm = hexBytes(tokenHash) else { return nil }
        return HKDF<SHA256>.deriveKey(inputKeyMaterial: SymmetricKey(data: ikm), salt: Data(clientId.utf8), info: Data(info.utf8), outputByteCount: 32)
    }

    public static func seal(_ plaintext: Data, key: SymmetricKey, handle: String, nonce: ChaChaPoly.Nonce = ChaChaPoly.Nonce()) throws -> String {
        guard plaintext.count <= maximumPlaintext else { throw MightyError("push message too long") }
        let box = try ChaChaPoly.seal(plaintext, using: key, nonce: nonce, authenticating: aad(handle))
        return base64URL(Data([version]) + box.combined)
    }

    public static func open(_ envelope: String, key: SymmetricKey, handle: String) -> Data? {
        guard envelope.count <= maximumEnvelope, let bytes = fromBase64URL(envelope), bytes.count >= 1 + 12 + 16, bytes.first == version,
              let box = try? ChaChaPoly.SealedBox(combined: Data(bytes.dropFirst())) else { return nil }
        return try? ChaChaPoly.open(box, using: key, authenticating: aad(handle))
    }

    static func aad(_ handle: String) -> Data { Data((info + ":" + handle).utf8) }

    static func hexBytes(_ text: String) -> Data? {
        var bytes = Data(capacity: text.count / 2)
        var index = text.startIndex
        while index < text.endIndex {
            let next = text.index(index, offsetBy: 2, limitedBy: text.endIndex) ?? text.endIndex
            guard let byte = UInt8(text[index..<next], radix: 16), text.distance(from: index, to: next) == 2 else { return nil }
            bytes.append(byte)
            index = next
        }
        return bytes
    }

    static func base64URL(_ data: Data) -> String {
        data.base64EncodedString().replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "=", with: "")
    }

    static func fromBase64URL(_ text: String) -> Data? {
        guard text.range(of: "^[A-Za-z0-9_-]*$", options: .regularExpression) != nil else { return nil }
        var standard = text.replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        while standard.count % 4 != 0 { standard += "=" }
        return Data(base64Encoded: standard)
    }
}
```

- [ ] **Step 5: Document the fixture**

Append to `native/contracts/README.md`:

```markdown
## Push notifications (`fixtures/push.json`)

The seal on a host → phone push and the rule that decides what is pushed (docs/mobile-remote.md "푸시 알림", docs/relay.md "푸시 알림").

- `seal`: a fake device token, `clientId` and handle; the token's `tokenHash` (hex SHA-256, what the host stores); the derived key `keyB64` (HKDF-SHA256, salt = UTF-8 `clientId`, info `mighty-push-v1`); a fixed `nonceB64`; the `aad` (`mighty-push-v1:` + handle); a `plaintext` message; its `envelope` (base64url of `0x01 ‖ nonce ‖ ChaCha20-Poly1305 ciphertext ‖ tag`); `tampered` envelopes (a flipped ciphertext bit, a wrong version byte) that must not open; `otherHandle`, under which the envelope must not open.
- `planner`: `hostId`, `hostName`, `at`, and `steps`. Each step is the whole list of panes a host sees (`MobilePushPane`: `sessionId`, `workspace`, `title`, `status`, `preview?`, `requests: [{ id, runId, kind, summary, buttons }]`, `signInLost`). `expect` lists the messages that observation sends, in order. Every message also carries `v: 1`, `hostId`, `hostName` and `at` from the top.

Implementations: Mac `MightyCore/Remote/MobilePush.swift` (`MobilePushSeal`, `MobilePushPlanner`), Windows `MightyClaude.Core/Remote/MobilePush.cs`, phone `mobile/src/lib/push/seal.ts` (opening only). Readers: Swift `MobilePushSealTests`, `MobilePushPlannerTests`; Windows `MobilePushVerification`; jest `push-seal.test.ts`.
```

- [ ] **Step 6: Run it and expect it to pass**

Run: MAC-RUN with `'\.(MobilePushSealTests)/'`
Expected: 4 tests pass.

- [ ] **Step 7: Commit**

```bash
git add native/contracts/fixtures/push.json native/contracts/README.md native/macos/Sources/MightyCore/Remote/MobilePush.swift native/macos/Tests/MightyCoreTests/MobilePushTests.swift
git commit -m "(mac) a push can be sealed so only the paired phone opens it, with a key both sides derive from the device token, held to a shared fixture" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## Task 6: Mac이 무엇을 언제 푸시할지 정한다

**Files:**
- Modify: `native/macos/Sources/MightyCore/Remote/MobilePush.swift` (메시지 형식, 계획기, 받을 기기 고르기)
- Modify: `native/macos/Tests/MightyCoreTests/MobilePushTests.swift` (`MobilePushPlannerTests`)

**Interfaces:**
- Consumes: `MobilePermission`(agy 병합 후 `antigravity: MobileAntigravityCard?`, `questionnaire`, `plan`, `canAllow`, `summary`), `ClaudePlanMode.toolName`.
- Produces:
  - `public enum MobilePushKind: String, Codable, Sendable, CaseIterable { case completed, failed, permission, question, plan, signin }`
  - `public enum MobilePushButtons: String, Codable, Sendable { case claude, antigravity, antigravityOnce, none }`
  - `public struct MobilePushRequest: Codable, Sendable, Equatable { id, runId: String; kind: MobilePushKind; summary: String; buttons: MobilePushButtons; init(id:runId:kind:summary:buttons:); init(_ permission: MobilePermission) }`
  - `public struct MobilePushPane: Codable, Sendable, Equatable { sessionId, workspace, title, status: String; preview: String?; requests: [MobilePushRequest]; signInLost: Bool }`
  - `public struct MobilePushPermission: Codable, Sendable, Equatable { requestId, runId: String; buttons: MobilePushButtons }`
  - `public struct MobilePushMessage: Codable, Sendable, Equatable { v: Int; id: String; kind: MobilePushKind; hostId, hostName, workspace, title, summary, sessionId: String; permission: MobilePushPermission?; at: String; func encodedForPush() -> Data? }`
  - `public struct MobilePushPlanner: Sendable { init(); mutating func observe(_ panes: [MobilePushPane], hostId: String, hostName: String, at: String) -> [MobilePushMessage] }`
  - `public struct MobilePushDevice: Sendable, Equatable { deviceId, handle, tokenHash: String; kinds: Set<MobilePushKind> }`
  - `public struct MobilePushViewing: Sendable, Equatable { sessionId: String; until: Date }`
  - `public enum MobilePushRecipients { static let viewingWindow: TimeInterval (45); static func targets(for message: MobilePushMessage, devices: [MobilePushDevice], viewing: [String: MobilePushViewing], now: Date) -> [MobilePushDevice] }`

**계획기 규칙 (Windows도 같다, 픽스처가 묶는다)**
1. 처음 `observe`는 기준선이다. 기억만 하고 아무것도 보내지 않는다. 앱이 켜질 때 이미 기다리던 요청을 다시 알리지 않기 위해서다. 기준선 뒤에 새로 나타난 창은 처음 본 그 순간부터 아래 규칙을 따른다.
2. 창마다 요청을 순서대로 본다. 지난 관찰에 없던 요청(`request:<runId>:<id>`)이면 보낸다. 종류는 요청의 `kind`이고, `permission`일 때만 `permission { requestId, runId, buttons }`를 싣는다. 요약은 요청의 `summary`다.
3. 지난 상태가 `running`이고 지금 `completed`·`error`이며 **기다리는 요청이 없으면** `completed`·`failed`를 보낸다. id는 `run:<sessionId>:<n>:<kind>`이고, `n`은 그 창에서 보낸 완료·실패의 차례다(1부터). 요약은 `preview`다. 요청이 남아 있으면(예: Antigravity 카드가 턴 끝에 오면) 요청만 보낸다. `stopped`는 보내지 않는다(사람이 멈췄다).
4. `signInLost`가 거짓에서 참이 되면 `signin`을 보낸다. id는 `signin:<sessionId>:<n>`이고 요약은 비운다.
5. 같은 id는 다시 보내지 않는다(최근 512개를 기억한다). 한 관찰의 메시지 순서는 창 순서이고, 창 안에서는 요청 → 상태 → 로그인 순이다.
6. 문자열은 유니코드 스칼라로 자른다: `hostName`·`workspace`·`title`은 80, `summary`는 200. 요약은 먼저 줄바꿈(`\r\n`, `\n`, `\r`)을 공백 하나로 바꾸고 앞뒤 공백을 지운다. 인코딩한 메시지가 2048바이트를 넘으면 요약을 비우고 다시 인코딩한다.

**버튼 (`MobilePushRequest.init(_ permission:)`)**
- Antigravity 카드는 `permission`이다. `canAllow`가 거짓이거나 규칙 있는 작업이 없으면 `none`이다. 규칙 있는 작업 중 `onceOnly`가 하나라도 있으면 `antigravityOnce`, 아니면 `antigravity`다(폰의 `antigravityOffersAlways`와 같은 판단).
- `questionnaire`가 있거나 `toolName == "AskUserQuestion"`이면 `question`이고 버튼은 `none`이다.
- `plan`이 있거나 `toolName == ClaudePlanMode.toolName`이면 `plan`이고 버튼은 `none`이다.
- 그 밖(Claude·Codex 권한)은 `permission`이다. `canAllow`이면 `claude`, 아니면 `none`이다.

**받을 기기:** 그 종류를 켠 기기다. 그 기기가 이 세션을 보고 있다고 알린 지 45초가 안 됐으면 뺀다.

- [ ] **Step 1: Write the failing test**

Append to `native/macos/Tests/MightyCoreTests/MobilePushTests.swift`:

```swift
struct MobilePushPlannerTests {
    @Test func theSharedStepsSendWhatTheFixtureExpects() throws {
        let planner = try PushFixture.load().planner
        var subject = MobilePushPlanner()
        for step in planner.steps {
            let sent = subject.observe(step.panes, hostId: planner.hostId, hostName: planner.hostName, at: planner.at)
            #expect(sent.map(\.id) == step.expect.map(\.id), "\(step.note)")
            for (message, expected) in zip(sent, step.expect) {
                #expect(message.v == 1 && message.hostId == planner.hostId && message.hostName == planner.hostName && message.at == planner.at, "\(step.note)")
                #expect(message.kind == expected.kind && message.sessionId == expected.sessionId && message.workspace == expected.workspace, "\(step.note)")
                #expect(message.title == expected.title && message.summary == expected.summary && message.permission == expected.permission, "\(step.note)")
            }
        }
    }

    @Test func requestsTakeTheirKindAndButtonsFromTheCard() {
        func permission(_ toolName: String, canAllow: Bool = true, plan: String? = nil, antigravity: MobileAntigravityCard? = nil) -> MobilePermission {
            MobilePermission(id: "r", runId: "u", toolName: toolName, title: "t", summary: "s", canAllow: canAllow, plan: plan, antigravity: antigravity)
        }
        #expect(MobilePushRequest(permission("Bash")).buttons == .claude)
        #expect(MobilePushRequest(permission("Bash", canAllow: false)).buttons == MobilePushButtons.none)
        #expect(MobilePushRequest(permission("AskUserQuestion")).kind == .question)
        #expect(MobilePushRequest(permission(ClaudePlanMode.toolName, plan: "p")).kind == .plan)
        let once = MobileAntigravityCard(actions: [MobileAntigravityAction(kind: "command", action: "command", displayName: "RunCommand", target: "rm x", rule: "command(rm x)", onceOnly: true)])
        let always = MobileAntigravityCard(actions: [MobileAntigravityAction(kind: "command", action: "command", displayName: "RunCommand", target: "ls", rule: "command(ls)")])
        let ruleless = MobileAntigravityCard(actions: [MobileAntigravityAction(kind: "other", action: "mcp", displayName: "Mcp")])
        #expect(MobilePushRequest(permission(MobileAntigravityCard.toolName, antigravity: once)).buttons == .antigravityOnce)
        #expect(MobilePushRequest(permission(MobileAntigravityCard.toolName, antigravity: always)).buttons == .antigravity)
        #expect(MobilePushRequest(permission(MobileAntigravityCard.toolName, canAllow: false, antigravity: ruleless)).buttons == MobilePushButtons.none)
        #expect(MobilePushRequest(permission(MobileAntigravityCard.toolName, antigravity: once)).kind == .permission)
    }

    @Test func aWideMessageStaysWithinThePushLimit() throws {
        var subject = MobilePushPlanner()
        _ = subject.observe([], hostId: "h", hostName: "H", at: "t")
        let wide = String(repeating: "가", count: 80)
        let pane = MobilePushPane(sessionId: "p", workspace: wide, title: wide, status: "running", preview: nil,
                                  requests: [MobilePushRequest(id: "r", runId: "u", kind: .permission, summary: String(repeating: "😀", count: 300), buttons: .claude)], signInLost: false)
        let message = try #require(subject.observe([pane], hostId: "h", hostName: wide, at: "t").first)
        #expect(message.summary.unicodeScalars.count == 200)
        let encoded = try #require(message.encodedForPush())
        #expect(encoded.count <= MobilePushSeal.maximumPlaintext)
    }

    @Test func aDeviceGetsOnlyTheKindsItTurnedOnAndNothingForTheSessionItIsViewing() {
        let message = MobilePushMessage(id: "x", kind: .completed, hostId: "h", hostName: "H", workspace: "w", title: "t", summary: "", sessionId: "pane-1", permission: nil, at: "t")
        let all = MobilePushDevice(deviceId: "a", handle: "ha", tokenHash: String(repeating: "0", count: 64), kinds: Set(MobilePushKind.allCases))
        let quiet = MobilePushDevice(deviceId: "b", handle: "hb", tokenHash: String(repeating: "0", count: 64), kinds: [.permission])
        let now = Date(timeIntervalSince1970: 1_000)
        #expect(MobilePushRecipients.targets(for: message, devices: [all, quiet], viewing: [:], now: now) == [all])
        let watching = ["a": MobilePushViewing(sessionId: "pane-1", until: now.addingTimeInterval(10))]
        #expect(MobilePushRecipients.targets(for: message, devices: [all], viewing: watching, now: now).isEmpty)
        let elsewhere = ["a": MobilePushViewing(sessionId: "pane-2", until: now.addingTimeInterval(10))]
        #expect(MobilePushRecipients.targets(for: message, devices: [all], viewing: elsewhere, now: now) == [all])
        let expired = ["a": MobilePushViewing(sessionId: "pane-1", until: now.addingTimeInterval(-1))]
        #expect(MobilePushRecipients.targets(for: message, devices: [all], viewing: expired, now: now) == [all])
    }
}
```

- [ ] **Step 2: Run it and expect it to fail**

Run: MAC-RUN with `'\.(MobilePushPlannerTests)/'`
Expected: build error `cannot find 'MobilePushPlanner' in scope` (and the other new types).

- [ ] **Step 3: Implement**

Append to `native/macos/Sources/MightyCore/Remote/MobilePush.swift`:

```swift
public enum MobilePushKind: String, Codable, Sendable, CaseIterable {
    case completed, failed, permission, question, plan, signin
}

/// The buttons a permission push carries; the phone draws them, the host still decides.
public enum MobilePushButtons: String, Codable, Sendable {
    case claude, antigravity, antigravityOnce, none
}

public struct MobilePushRequest: Codable, Sendable, Equatable {
    public var id: String
    public var runId: String
    public var kind: MobilePushKind
    public var summary: String
    public var buttons: MobilePushButtons
    public init(id: String, runId: String, kind: MobilePushKind, summary: String, buttons: MobilePushButtons) {
        self.id = id; self.runId = runId; self.kind = kind; self.summary = summary; self.buttons = buttons
    }
    /// A pending card as the phone sees it, read as a push (the rule in docs/mobile-remote.md "푸시 알림").
    public init(_ permission: MobilePermission) {
        id = permission.id; runId = permission.runId; summary = permission.summary
        if let card = permission.antigravity {
            kind = .permission
            let ruled = card.actions.filter { $0.rule != nil }
            if !permission.canAllow || ruled.isEmpty { buttons = .none }
            else { buttons = ruled.contains { $0.onceOnly == true } ? .antigravityOnce : .antigravity }
        } else if permission.questionnaire != nil || permission.toolName == "AskUserQuestion" {
            kind = .question; buttons = .none
        } else if permission.plan != nil || permission.toolName == ClaudePlanMode.toolName {
            kind = .plan; buttons = .none
        } else {
            kind = .permission; buttons = permission.canAllow ? .claude : .none
        }
    }
}

/// One pane as the push planner sees it.
public struct MobilePushPane: Codable, Sendable, Equatable {
    public var sessionId: String
    public var workspace: String
    public var title: String
    /// The pane's own run status (`running`, `completed`, `error`, `stopped`, …), not the phone's display status.
    public var status: String
    public var preview: String?
    public var requests: [MobilePushRequest]
    public var signInLost: Bool
    public init(sessionId: String, workspace: String, title: String, status: String, preview: String?, requests: [MobilePushRequest], signInLost: Bool) {
        self.sessionId = sessionId; self.workspace = workspace; self.title = title; self.status = status
        self.preview = preview; self.requests = requests; self.signInLost = signInLost
    }
}

public struct MobilePushPermission: Codable, Sendable, Equatable {
    public var requestId: String
    public var runId: String
    public var buttons: MobilePushButtons
    public init(requestId: String, runId: String, buttons: MobilePushButtons) { self.requestId = requestId; self.runId = runId; self.buttons = buttons }
}

/// The plaintext inside a push envelope.
public struct MobilePushMessage: Codable, Sendable, Equatable {
    public var v = 1
    public var id: String
    public var kind: MobilePushKind
    public var hostId: String
    public var hostName: String
    public var workspace: String
    public var title: String
    public var summary: String
    public var sessionId: String
    public var permission: MobilePushPermission?
    public var at: String
    public init(id: String, kind: MobilePushKind, hostId: String, hostName: String, workspace: String, title: String, summary: String, sessionId: String,
                permission: MobilePushPermission?, at: String) {
        self.id = id; self.kind = kind; self.hostId = hostId; self.hostName = hostName; self.workspace = workspace
        self.title = title; self.summary = summary; self.sessionId = sessionId; self.permission = permission; self.at = at
    }
    /// UTF-8 JSON within `MobilePushSeal.maximumPlaintext`; the summary goes first when it does not fit.
    public func encodedForPush() -> Data? {
        let encoder = JSONEncoder()
        if let data = try? encoder.encode(self), data.count <= MobilePushSeal.maximumPlaintext { return data }
        var shorter = self; shorter.summary = ""
        guard let data = try? encoder.encode(shorter), data.count <= MobilePushSeal.maximumPlaintext else { return nil }
        return data
    }
}

/// Decides what a host pushes from what its panes show now and what they showed last time.
public struct MobilePushPlanner: Sendable {
    public static let sentMemory = 512
    private struct Memory: Sendable {
        var status: String
        var requests: Set<String>
        var signInLost: Bool
        var runs: Int
        var signins: Int
    }
    private var baseline = false
    private var memory: [String: Memory] = [:]
    private var sent: [String] = []
    private var sentSet = Set<String>()

    public init() {}

    public mutating func observe(_ panes: [MobilePushPane], hostId: String, hostName: String, at: String) -> [MobilePushMessage] {
        var messages: [MobilePushMessage] = []
        var next: [String: Memory] = [:]
        for pane in panes {
            let previous = memory[pane.sessionId]
            var record = Memory(status: pane.status, requests: Set(pane.requests.map(Self.requestId)), signInLost: pane.signInLost,
                                runs: previous?.runs ?? 0, signins: previous?.signins ?? 0)
            if baseline {
                for request in pane.requests where previous?.requests.contains(Self.requestId(request)) != true {
                    let permission = request.kind == .permission ? MobilePushPermission(requestId: request.id, runId: request.runId, buttons: request.buttons) : nil
                    emit(&messages, pane: pane, kind: request.kind, id: Self.requestId(request), summary: request.summary, permission: permission, hostId: hostId, hostName: hostName, at: at)
                }
                if previous?.status == "running", pane.requests.isEmpty, pane.status == "completed" || pane.status == "error" {
                    record.runs += 1
                    let kind: MobilePushKind = pane.status == "completed" ? .completed : .failed
                    emit(&messages, pane: pane, kind: kind, id: "run:\(pane.sessionId):\(record.runs):\(kind.rawValue)", summary: pane.preview ?? "", permission: nil, hostId: hostId, hostName: hostName, at: at)
                }
                if pane.signInLost, previous?.signInLost != true {
                    record.signins += 1
                    emit(&messages, pane: pane, kind: .signin, id: "signin:\(pane.sessionId):\(record.signins)", summary: "", permission: nil, hostId: hostId, hostName: hostName, at: at)
                }
            }
            next[pane.sessionId] = record
        }
        memory = next
        baseline = true
        return messages
    }

    private static func requestId(_ request: MobilePushRequest) -> String { "request:\(request.runId):\(request.id)" }

    private mutating func emit(_ messages: inout [MobilePushMessage], pane: MobilePushPane, kind: MobilePushKind, id: String, summary: String,
                               permission: MobilePushPermission?, hostId: String, hostName: String, at: String) {
        guard !sentSet.contains(id) else { return }
        sent.append(id); sentSet.insert(id)
        if sent.count > Self.sentMemory { sentSet.remove(sent.removeFirst()) }
        messages.append(MobilePushMessage(id: id, kind: kind, hostId: hostId, hostName: Self.clip(hostName, 80), workspace: Self.clip(pane.workspace, 80),
                                          title: Self.clip(pane.title, 80), summary: Self.clip(Self.oneLine(summary), 200), sessionId: pane.sessionId,
                                          permission: permission, at: at))
    }

    static func clip(_ text: String, _ limit: Int) -> String {
        var scalars = String.UnicodeScalarView()
        scalars.append(contentsOf: text.unicodeScalars.prefix(limit))
        return String(scalars)
    }

    static func oneLine(_ text: String) -> String {
        text.replacingOccurrences(of: "\r\n", with: " ").replacingOccurrences(of: "\n", with: " ").replacingOccurrences(of: "\r", with: " ")
            .trimmingCharacters(in: .whitespaces)
    }
}

public struct MobilePushDevice: Sendable, Equatable {
    public var deviceId: String
    public var handle: String
    public var tokenHash: String
    public var kinds: Set<MobilePushKind>
    public init(deviceId: String, handle: String, tokenHash: String, kinds: Set<MobilePushKind>) {
        self.deviceId = deviceId; self.handle = handle; self.tokenHash = tokenHash; self.kinds = kinds
    }
}

/// "This phone has this session on screen", until `until`.
public struct MobilePushViewing: Sendable, Equatable {
    public var sessionId: String
    public var until: Date
    public init(sessionId: String, until: Date) { self.sessionId = sessionId; self.until = until }
}

public enum MobilePushRecipients {
    public static let viewingWindow: TimeInterval = 45
    public static func targets(for message: MobilePushMessage, devices: [MobilePushDevice], viewing: [String: MobilePushViewing], now: Date) -> [MobilePushDevice] {
        devices.filter { device in
            guard device.kinds.contains(message.kind) else { return false }
            if let seen = viewing[device.deviceId], seen.sessionId == message.sessionId, seen.until > now { return false }
            return true
        }
    }
}
```

- [ ] **Step 4: Run it and expect it to pass**

Run: MAC-RUN with `'\.(MobilePushSealTests|MobilePushPlannerTests)/'`
Expected: 8 tests pass.

- [ ] **Step 5: Commit**

```bash
git add native/macos/Sources/MightyCore/Remote/MobilePush.swift native/macos/Tests/MightyCoreTests/MobilePushTests.swift
git commit -m "(mac) the Mac decides once what each finished run, failure, lost sign-in, permission, question, plan or Antigravity card should push, skipping a phone that is looking at that session" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## Task 7: 폰이 Mac에 핸들과 보고 있는 세션을 알린다

**Files:**
- Modify: `native/macos/Sources/MightyCore/Remote/MobilePush.swift` (`MobilePushWire`, 요청 모양)
- Modify: `native/macos/Sources/MightyCore/Remote/MobileDeviceRegistry.swift` (`MobilePushRegistration`, `MobileDevice.push`, `setPush`·`clearPush`·`pushDevices`, `load()`의 정리)
- Modify: `native/macos/Sources/MightyCore/Remote/MobileRemoteService.swift` (`route`의 `/m1/push/*`, `pushViewing`, `pushClock`)
- Modify: `native/macos/Sources/MightyCore/Remote/MobileRemoteModels.swift` (`MobileCapability.all`에 `"push"`)
- Modify: `native/macos/Tests/MightyCoreTests/MobileRemoteTests.swift` (파일 끝의 `extension MobileRemoteTests`)
- Modify: `locales/{ko,en,zh,ja}.json` (`"remote.error.notPendingPermission"` 줄 바로 뒤에 두 키)
- Modify (copies): `native/macos/Sources/MightyCore/Resources/Locales/{ko,en,zh,ja}.json`, `mobile/src/locales/{ko,en,zh,ja}.json`
- Modify: `docs/i18n.md` (재생성)

**Interfaces:**
- Consumes: Task 6 `MobilePushKind`, `MobilePushDevice`, `MobilePushViewing`, `MobilePushRecipients.viewingWindow`.
- Produces:
  - `public enum MobilePushWire { static func validHandle(_ value: String) -> Bool; static func kinds(_ values: [String]) -> [MobilePushKind] }`. 모르는 종류 이름은 버린다(새 폰과 옛 호스트가 함께 쓰일 수 있게).
  - `public struct MobilePushRegisterRequest: Codable, Sendable { handle: String; kinds: [String] }`, `public struct MobilePushViewingRequest: Codable, Sendable { sessionId: String? }`
  - `public struct MobilePushRegistration: Codable, Sendable, Equatable { handle: String; kinds: [String]; updatedAt: String }`
  - `MobileDevice.push: MobilePushRegistration?`. 옛 파일은 키가 없어도 그대로 읽힌다.
  - `public enum MobilePushRegistrationResult: Sendable, Equatable { case saved, needsToken, unsaved }`
  - `MobileDeviceRegistry.setPush(_ id: String, handle: String, kinds: [MobilePushKind]) -> MobilePushRegistrationResult`, `clearPush(_ id: String, handle: String? = nil)`, `pushDevices() -> [MobilePushDevice]`
  - m1 라우트(요청한 기기 = 인증된 `deviceId`):
    - `POST /m1/push/register { handle, kinds }` → `{ protocol, ok }`. 핸들 형식이 틀리면 400 `remote.error.badPushHandle`. 토큰이 없는 기기(구버전 앱 묶음)는 409 `remote.error.pushNeedsToken`. 저장에 실패하면 503 `mobileRemote.device.saveFailed`.
    - `POST /m1/push/unregister {}` → `{ protocol, ok }`.
    - `POST /m1/push/viewing { sessionId: string | null }` → `{ protocol, ok }`. 45초 동안 유효하다. 형식이 틀린 id는 404 `remote.error.paneNotFound`.
  - 기능 `"push"`를 광고한다.
  - 새 키 `remote.error.badPushHandle`, `remote.error.pushNeedsToken`.

- [ ] **Step 1: Write the failing test**

Append to the end of `native/macos/Tests/MightyCoreTests/MobileRemoteTests.swift`:

```swift
// MARK: Push (docs/mobile-remote.md "푸시 알림")

extension MobileRemoteTests {
    /// A host whose device list already holds the fixture's phone with its token hash.
    fileprivate static func pushService(_ directory: URL) throws -> (MobileRemoteService, PushFixture.Seal) {
        let seal = try PushFixture.load().seal
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let stamp = "2026-10-23T00:00:00.000Z"
        let devices = [MobileDevice(id: seal.clientId, name: "Phone", tokenHash: seal.tokenHash, firstSeen: stamp, lastSeen: stamp)]
        try JSONEncoder().encode(devices).write(to: directory.appendingPathComponent("devices.json"))
        let service = MobileRemoteService(dataDirectory: directory, hostName: "Test Mac", appVersion: "9.9.9",
                                          defaultRelayURL: "wss://relay.example.invalid", watchesNetwork: false)
        return (service, seal)
    }

    @Test func aPhoneWithATokenRegistersAHandleAndTheKindsItWants() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("mobile-push-" + UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let (service, seal) = try Self.pushService(directory)
        await service.attach(FakeMobileHost())
        let (status, _) = try await call(service, "POST", "/m1/push/register", body: ["handle": seal.handle, "kinds": ["permission", "completed", "future-kind"]], device: seal.clientId)
        #expect(status == 200)
        let registry = MobileDeviceRegistry(url: directory.appendingPathComponent("devices.json"))
        #expect(registry.pushDevices() == [MobilePushDevice(deviceId: seal.clientId, handle: seal.handle, tokenHash: seal.tokenHash, kinds: [.permission, .completed])])
        #expect(try await call(service, "POST", "/m1/push/unregister", body: [:], device: seal.clientId).0 == 200)
        #expect(MobileDeviceRegistry(url: directory.appendingPathComponent("devices.json")).pushDevices().isEmpty)
    }

    @Test func aBadHandleOrALegacyPhoneIsRefused() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("mobile-push-" + UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let (service, seal) = try Self.pushService(directory)
        await service.attach(FakeMobileHost())
        #expect(try await call(service, "POST", "/m1/push/register", body: ["handle": "short", "kinds": []], device: seal.clientId).0 == 400)
        #expect(try await call(service, "POST", "/m1/push/register", body: ["handle": seal.handle, "kinds": []], device: MobileDeviceRegistry.legacyId).0 == 409)
        #expect(try await call(service, "POST", "/m1/push/viewing", body: ["sessionId": "bad id!"], device: seal.clientId).0 == 404)
        #expect(try await call(service, "POST", "/m1/push/viewing", body: ["sessionId": NSNull()], device: seal.clientId).0 == 200)
    }

    @Test func theHostAdvertisesPush() {
        #expect(MobileCapability.all.contains("push"))
    }
}
```

- [ ] **Step 2: Run it and expect it to fail**

Run: MAC-RUN with `'\.(MobileRemoteTests)/'`
Expected: build error `value of type 'MobileDeviceRegistry' has no member 'pushDevices'`.

- [ ] **Step 3: Implement the wire types**

Append to `native/macos/Sources/MightyCore/Remote/MobilePush.swift`:

```swift
public enum MobilePushWire {
    /// A relay push handle: 32 random bytes, base64url without padding.
    public static func validHandle(_ value: String) -> Bool { value.range(of: "^[A-Za-z0-9_-]{43}$", options: .regularExpression) != nil }
    /// The kinds this host knows, in its own order; a newer phone's unknown names are left out, not refused.
    public static func kinds(_ values: [String]) -> [MobilePushKind] { MobilePushKind.allCases.filter { values.contains($0.rawValue) } }
}

/// `POST /m1/push/register`.
public struct MobilePushRegisterRequest: Codable, Sendable {
    public var handle: String
    public var kinds: [String]
}

/// `POST /m1/push/viewing`: the session on the phone's screen, or null when none.
public struct MobilePushViewingRequest: Codable, Sendable {
    public var sessionId: String?
}
```

- [ ] **Step 4: Implement the registry**

In `MobileDeviceRegistry.swift`, add this above `public struct MobileDevice`:

```swift
/// Where and what a phone asked to be pushed (docs/mobile-remote.md "푸시 알림"). The handle
/// means nothing without the relay, and the relay's token means nothing without the handle.
public struct MobilePushRegistration: Codable, Sendable, Equatable {
    public var handle: String
    public var kinds: [String]
    public var updatedAt: String
    public init(handle: String, kinds: [String], updatedAt: String) { self.handle = handle; self.kinds = kinds; self.updatedAt = updatedAt }
}

public enum MobilePushRegistrationResult: Sendable, Equatable {
    case saved
    /// Unknown device, or the legacy group: the push key comes from a device token.
    case needsToken
    case unsaved
}
```

In `public struct MobileDevice`:
- add `public var push: MobilePushRegistration?` after `lastSeen`;
- replace the initializer with:

```swift
    public init(id: String, name: String, tokenHash: String? = nil, firstSeen: String, lastSeen: String, push: MobilePushRegistration? = nil) {
        self.id = id; self.name = name; self.tokenHash = tokenHash; self.firstSeen = firstSeen; self.lastSeen = lastSeen; self.push = push
    }
```

In `load()`, right after the `devices = decoded.filter { … }` statement and before the `if devices.count > Self.maximum` line, add:

```swift
        // A hand-edited or older row may carry a handle it cannot use: drop the handle, keep the phone.
        devices = devices.map { device in
            var device = device
            if let push = device.push, device.tokenHash == nil || !MobilePushWire.validHandle(push.handle) { device.push = nil }
            return device
        }
```

Add these methods after `tokenHolders()`:

```swift
    // MARK: Push

    public func setPush(_ id: String, handle: String, kinds: [MobilePushKind]) -> MobilePushRegistrationResult {
        lock.lock(); defer { lock.unlock() }
        load()
        guard let index = devices.firstIndex(where: { $0.id == id }), devices[index].tokenHash != nil else { return .needsToken }
        let snapshot = devices
        devices[index].push = MobilePushRegistration(handle: handle, kinds: kinds.map(\.rawValue), updatedAt: timestamp())
        do { try save() } catch { devices = snapshot; return .unsaved }
        return .saved
    }

    /// Forgets a phone's handle; with `handle`, only while it is still that one (the relay said it is gone).
    public func clearPush(_ id: String, handle: String? = nil) {
        lock.lock(); defer { lock.unlock() }
        load()
        guard let index = devices.firstIndex(where: { $0.id == id }), let current = devices[index].push, handle == nil || current.handle == handle else { return }
        let snapshot = devices
        devices[index].push = nil
        do { try save() } catch { devices = snapshot }
    }

    /// The phones to push to, with what their key is made from. Never leaves the host.
    public func pushDevices() -> [MobilePushDevice] {
        lock.lock(); defer { lock.unlock() }
        load()
        return devices.compactMap { device in
            guard let push = device.push, let hash = device.tokenHash else { return nil }
            return MobilePushDevice(deviceId: device.id, handle: push.handle, tokenHash: hash, kinds: Set(push.kinds.compactMap(MobilePushKind.init(rawValue:))))
        }
    }
```

A revoke or key rotation already removes the device row through `remove(_:)` / `clearAll()`, so its handle goes with it.

- [ ] **Step 5: Implement the routes**

In `MobileRemoteService`, add these stored properties next to `connectedDevices`:

```swift
    /// The session each phone said is on its screen; a push about it is not sent while it holds.
    private var pushViewing: [String: MobilePushViewing] = [:]
    /// Clock for `pushViewing` and push timestamps; tests move it.
    var pushClock: @Sendable () -> Date = { Date() }
```

In `route(method:path:body:deviceId:)`, insert this block right before `if method == "POST", route.count == 3, route[0] == "sessions", url.query == nil {`:

```swift
            if method == "POST", route.count == 2, route[0] == "push", url.query == nil {
                switch route[1] {
                case "register":
                    let request = try decode(body, as: MobilePushRegisterRequest.self)
                    guard MobilePushWire.validHandle(request.handle) else { throw Failure(400, L("remote.error.badPushHandle")) }
                    switch deviceRegistry.setPush(deviceId, handle: request.handle, kinds: MobilePushWire.kinds(request.kinds)) {
                    case .saved: return reply(200, MobileOK())
                    case .needsToken: throw Failure(409, L("remote.error.pushNeedsToken"))
                    case .unsaved: throw Failure(503, L("mobileRemote.device.saveFailed"))
                    }
                case "unregister":
                    deviceRegistry.clearPush(deviceId)
                    pushViewing.removeValue(forKey: deviceId)
                    return reply(200, MobileOK())
                case "viewing":
                    let request = try decode(body, as: MobilePushViewingRequest.self)
                    if let id = request.sessionId {
                        pushViewing[deviceId] = MobilePushViewing(sessionId: try sessionID(id), until: pushClock().addingTimeInterval(MobilePushRecipients.viewingWindow))
                    } else {
                        pushViewing.removeValue(forKey: deviceId)
                    }
                    return reply(200, MobileOK())
                default:
                    break
                }
            }
```

In `MobileRemoteModels.swift`, change the end of `MobileCapability.all` from `"structure", "direct"]` to `"structure", "direct", "push"]`.

- [ ] **Step 6: Locale keys**

Insert after the `"remote.error.notPendingPermission"` line in each file:

`locales/ko.json`:
```json
  "remote.error.badPushHandle": "알림 핸들 형식이 맞지 않습니다.",
  "remote.error.pushNeedsToken": "이 휴대폰은 알림을 받을 수 없습니다. 휴대폰 앱을 업데이트한 뒤 다시 페어링하세요.",
```
`locales/en.json`:
```json
  "remote.error.badPushHandle": "The notification handle is malformed.",
  "remote.error.pushNeedsToken": "This phone can't receive notifications. Update the phone app and pair it again.",
```
`locales/zh.json`:
```json
  "remote.error.badPushHandle": "通知句柄格式不正确。",
  "remote.error.pushNeedsToken": "此手机无法接收通知。请更新手机应用后重新配对。",
```
`locales/ja.json`:
```json
  "remote.error.badPushHandle": "通知ハンドルの形式が正しくありません。",
  "remote.error.pushNeedsToken": "このスマートフォンは通知を受け取れません。アプリを更新してからもう一度ペアリングしてください。",
```

Then copy:

```bash
for lang in ko en zh ja; do cp locales/$lang.json native/macos/Sources/MightyCore/Resources/Locales/$lang.json; cp locales/$lang.json mobile/src/locales/$lang.json; done
```

- [ ] **Step 7: Run it and expect it to pass**

Run: MAC-RUN with `'\.(MobileRemoteTests|MobilePushPlannerTests|MobilePushSealTests|MobileDeviceRegistryTests)/'`, then LOCALES
Expected: the suites pass; the line-364 test (`advertised == Set(MobileCapability.all)`) still passes because it reads the list; both locale runs print no error.

- [ ] **Step 8: Commit**

```bash
git add native/macos/Sources/MightyCore/Remote/MobilePush.swift native/macos/Sources/MightyCore/Remote/MobileDeviceRegistry.swift native/macos/Sources/MightyCore/Remote/MobileRemoteService.swift native/macos/Sources/MightyCore/Remote/MobileRemoteModels.swift native/macos/Tests/MightyCoreTests/MobileRemoteTests.swift locales/ko.json locales/en.json locales/zh.json locales/ja.json native/macos/Sources/MightyCore/Resources/Locales/ko.json native/macos/Sources/MightyCore/Resources/Locales/en.json native/macos/Sources/MightyCore/Resources/Locales/zh.json native/macos/Sources/MightyCore/Resources/Locales/ja.json mobile/src/locales/ko.json mobile/src/locales/en.json mobile/src/locales/zh.json mobile/src/locales/ja.json docs/i18n.md
git commit -m "(mac) a paired phone can give the Mac its push handle and the kinds it wants, and say which session is on its screen; the Mac advertises push" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## Task 8: Mac이 봉인한 푸시를 릴레이로 보낸다

**Files:**
- Modify: `native/macos/Sources/MightyCore/Remote/MobilePush.swift` (`MobilePushRelay`, 전송 결과, HTTP 전송기)
- Modify: `native/macos/Sources/MightyCore/Remote/MobileRemoteService.swift` (`pushObserve`, `deliver`, `setPushTransport`)
- Modify: `native/macos/Tests/MightyCoreTests/MobileRemoteTests.swift` (푸시 확장에 전송 테스트)
- Modify: `native/macos/Tests/MightyCoreTests/MobilePushTests.swift` (`MobilePushRelayTests`)

**Interfaces:**
- Consumes: Task 5–7. `relayURL`, `loadOrCreateControlToken()`, `RelayEndpoint.serverId(hostToken:)`, `hostId`, `hostName`, `Self.log`.
- Produces:
  - `public enum MobilePushRelay { static func sendURL(_ relay: String) -> URL? }`. `wss`/`https`는 `https://<host[:port]>/push/v1/send`, `ws`/`http`는 `http://…`가 되고, 그 밖은 nil이다.
  - `public struct MobilePushSendRequest: Codable, Sendable, Equatable { serverId, handle, payload: String }`
  - `public enum MobilePushSendOutcome: Sendable, Equatable { case sent, gone, retry(after: TimeInterval?), failed(status: Int); init(status: Int, retryAfter: TimeInterval?) }`. 2xx는 `sent`, 403·404·410은 `gone`, 429·500·502·504는 `retry`, 그 밖(400·401·413·503 …)은 `failed`다.
  - `public protocol MobilePushTransport: Sendable { func send(_ request: MobilePushSendRequest, to url: URL, hostToken: String) async -> MobilePushSendOutcome }`
  - `public struct MobilePushHTTPTransport: MobilePushTransport { init(session: URLSession = …) }`. 네트워크 오류는 `retry(after: nil)`이다.
  - `MobileRemoteService.pushObserve(_ panes: [MobilePushPane]) async -> [Task<Void, Never>]` (`@discardableResult`)
  - `MobileRemoteService.setPushTransport(_ transport: any MobilePushTransport, retryDelays: [TimeInterval], now: @escaping @Sendable () -> Date)` (internal, 테스트용)

- [ ] **Step 1: Write the failing tests**

Append to `MobilePushTests.swift`:

```swift
struct MobilePushRelayTests {
    @Test func theSendAddressFollowsTheRelaysScheme() {
        #expect(MobilePushRelay.sendURL("wss://relay.example.invalid")?.absoluteString == "https://relay.example.invalid/push/v1/send")
        #expect(MobilePushRelay.sendURL("ws://127.0.0.1:8787")?.absoluteString == "http://127.0.0.1:8787/push/v1/send")
        #expect(MobilePushRelay.sendURL("wss://relay.example.invalid/ws?x=1")?.absoluteString == "https://relay.example.invalid/push/v1/send")
        #expect(MobilePushRelay.sendURL("ftp://relay.example.invalid") == nil)
    }

    @Test func theRelaysAnswerSaysWhetherToRetryOrForget() {
        #expect(MobilePushSendOutcome(status: 202, retryAfter: nil) == .sent)
        for gone in [403, 404, 410] { #expect(MobilePushSendOutcome(status: gone, retryAfter: nil) == .gone) }
        #expect(MobilePushSendOutcome(status: 429, retryAfter: 5) == .retry(after: 5))
        #expect(MobilePushSendOutcome(status: 502, retryAfter: nil) == .retry(after: nil))
        #expect(MobilePushSendOutcome(status: 503, retryAfter: nil) == .failed(status: 503))
        #expect(MobilePushSendOutcome(status: 401, retryAfter: nil) == .failed(status: 401))
    }
}
```

Append inside the push `extension MobileRemoteTests` in `MobileRemoteTests.swift` (before its closing brace):

```swift
    @Test func aNewPermissionIsSealedForThePhoneAndSentToTheRelay() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("mobile-push-" + UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let (service, seal) = try Self.pushService(directory)
        await service.attach(FakeMobileHost())
        let transport = RecordingPushTransport()
        await service.setPushTransport(transport, retryDelays: [0, 0], now: { Date(timeIntervalSince1970: 1_000) })
        _ = try await call(service, "POST", "/m1/push/register", body: ["handle": seal.handle, "kinds": ["permission"]], device: seal.clientId)
        let idle = MobilePushPane(sessionId: "pane-1", workspace: "demo", title: "Claude 1", status: "running", preview: nil, requests: [], signInLost: false)
        for task in await service.pushObserve([idle]) { await task.value }
        var asking = idle
        asking.requests = [MobilePushRequest(id: "req-1", runId: "run-1", kind: .permission, summary: "git status", buttons: .claude)]
        for task in await service.pushObserve([asking]) { await task.value }

        let sent = transport.sent
        #expect(sent.count == 1)
        #expect(sent.first?.url.absoluteString == "https://relay.example.invalid/push/v1/send")
        let first = try #require(sent.first)
        #expect(first.request.serverId == RelayEndpoint.serverId(hostToken: first.hostToken))
        #expect(first.request.handle == seal.handle)
        let key = try #require(MobilePushSeal.key(tokenHash: seal.tokenHash, clientId: seal.clientId))
        let plaintext = try #require(MobilePushSeal.open(first.request.payload, key: key, handle: seal.handle))
        let message = try JSONDecoder().decode(MobilePushMessage.self, from: plaintext)
        #expect(message.id == "request:run-1:req-1")
        #expect(message.hostName == "Test Mac")
        #expect(message.permission == MobilePushPermission(requestId: "req-1", runId: "run-1", buttons: .claude))
        #expect(message.at == "1970-01-01T00:16:40Z")
    }

    @Test func nothingIsSentForAKindThePhoneTurnedOffOrASessionItIsViewing() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("mobile-push-" + UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let (service, seal) = try Self.pushService(directory)
        await service.attach(FakeMobileHost())
        let transport = RecordingPushTransport()
        await service.setPushTransport(transport, retryDelays: [0, 0], now: { Date(timeIntervalSince1970: 1_000) })
        _ = try await call(service, "POST", "/m1/push/register", body: ["handle": seal.handle, "kinds": ["completed"]], device: seal.clientId)
        _ = try await call(service, "POST", "/m1/push/viewing", body: ["sessionId": "pane-1"], device: seal.clientId)
        let running = MobilePushPane(sessionId: "pane-1", workspace: "demo", title: "Claude 1", status: "running", preview: nil, requests: [], signInLost: false)
        var question = running; question.sessionId = "pane-2"
        for task in await service.pushObserve([running, question]) { await task.value }
        var done = running; done.status = "completed"
        question.requests = [MobilePushRequest(id: "q", runId: "r", kind: .question, summary: "?", buttons: .none)]
        for task in await service.pushObserve([done, question]) { await task.value }
        #expect(transport.sent.isEmpty)
    }

    @Test func aHandleTheRelayNoLongerKnowsIsForgottenAndABusyRelayIsTriedThreeTimes() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("mobile-push-" + UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let (service, seal) = try Self.pushService(directory)
        await service.attach(FakeMobileHost())
        let transport = RecordingPushTransport()
        transport.outcomes = [.retry(after: nil), .retry(after: nil), .retry(after: nil), .gone]
        await service.setPushTransport(transport, retryDelays: [0, 0], now: { Date(timeIntervalSince1970: 1_000) })
        _ = try await call(service, "POST", "/m1/push/register", body: ["handle": seal.handle, "kinds": ["permission"]], device: seal.clientId)
        var pane = MobilePushPane(sessionId: "pane-1", workspace: "demo", title: "Claude 1", status: "running", preview: nil, requests: [], signInLost: false)
        for task in await service.pushObserve([pane]) { await task.value }
        pane.requests = [MobilePushRequest(id: "a", runId: "r", kind: .permission, summary: "", buttons: .claude)]
        for task in await service.pushObserve([pane]) { await task.value }
        #expect(transport.sent.count == 3)
        pane.requests.append(MobilePushRequest(id: "b", runId: "r", kind: .permission, summary: "", buttons: .claude))
        for task in await service.pushObserve([pane]) { await task.value }
        #expect(transport.sent.count == 4)
        #expect(MobileDeviceRegistry(url: directory.appendingPathComponent("devices.json")).pushDevices().isEmpty)
    }
```

Add this class at file scope in `MobileRemoteTests.swift`, right after the push extension:

```swift
/// Records what would have gone to the relay and answers from a queue (`sent` when it runs dry).
final class RecordingPushTransport: MobilePushTransport, @unchecked Sendable {
    struct Sent { let request: MobilePushSendRequest; let url: URL; let hostToken: String }
    private let lock = NSLock()
    private var log: [Sent] = []
    var outcomes: [MobilePushSendOutcome] = []
    var sent: [Sent] { lock.lock(); defer { lock.unlock() }; return log }
    func send(_ request: MobilePushSendRequest, to url: URL, hostToken: String) async -> MobilePushSendOutcome {
        lock.lock(); defer { lock.unlock() }
        log.append(Sent(request: request, url: url, hostToken: hostToken))
        return outcomes.isEmpty ? .sent : outcomes.removeFirst()
    }
}
```

- [ ] **Step 2: Run them and expect them to fail**

Run: MAC-RUN with `'\.(MobilePushRelayTests|MobileRemoteTests)/'`
Expected: build error `cannot find 'MobilePushRelay' in scope` and `value of type 'MobileRemoteService' has no member 'pushObserve'`.

- [ ] **Step 3: Implement the transport**

Append to `MobilePush.swift`:

```swift
public enum MobilePushRelay {
    /// The relay's push route next to the WebSocket the host already dials.
    public static func sendURL(_ relay: String) -> URL? {
        guard var parts = URLComponents(string: relay), let scheme = parts.scheme?.lowercased(), parts.host?.isEmpty == false else { return nil }
        switch scheme {
        case "wss", "https": parts.scheme = "https"
        case "ws", "http": parts.scheme = "http"
        default: return nil
        }
        parts.path = "/push/v1/send"; parts.query = nil; parts.fragment = nil
        return parts.url
    }
}

public struct MobilePushSendRequest: Codable, Sendable, Equatable {
    public var serverId: String
    public var handle: String
    public var payload: String
    public init(serverId: String, handle: String, payload: String) { self.serverId = serverId; self.handle = handle; self.payload = payload }
}

public enum MobilePushSendOutcome: Sendable, Equatable {
    case sent
    /// The relay no longer knows the handle, it is another host's, or FCM dropped the token.
    case gone
    case retry(after: TimeInterval?)
    case failed(status: Int)
    public init(status: Int, retryAfter: TimeInterval?) {
        switch status {
        case 200...299: self = .sent
        case 403, 404, 410: self = .gone
        case 429, 500, 502, 504: self = .retry(after: retryAfter)
        default: self = .failed(status: status)
        }
    }
}

public protocol MobilePushTransport: Sendable {
    func send(_ request: MobilePushSendRequest, to url: URL, hostToken: String) async -> MobilePushSendOutcome
}

public struct MobilePushHTTPTransport: MobilePushTransport {
    private let session: URLSession
    public init(session: URLSession = URLSession(configuration: .ephemeral)) { self.session = session }
    public func send(_ request: MobilePushSendRequest, to url: URL, hostToken: String) async -> MobilePushSendOutcome {
        var http = URLRequest(url: url)
        http.httpMethod = "POST"
        http.timeoutInterval = 15
        http.setValue("application/json", forHTTPHeaderField: "Content-Type")
        http.setValue("Bearer " + hostToken, forHTTPHeaderField: "Authorization")
        http.httpBody = try? JSONEncoder().encode(request)
        guard let (_, response) = try? await session.data(for: http), let answer = response as? HTTPURLResponse else { return .retry(after: nil) }
        return MobilePushSendOutcome(status: answer.statusCode, retryAfter: answer.value(forHTTPHeaderField: "Retry-After").flatMap(TimeInterval.init))
    }
}
```

- [ ] **Step 4: Implement the delivery**

In `MobileRemoteService`, add next to `pushViewing`:

```swift
    private var pushPlanner = MobilePushPlanner()
    private var pushTransport: any MobilePushTransport = MobilePushHTTPTransport()
    /// Waits before the second and third attempt; a relay's Retry-After (up to 30 s) wins when longer.
    private var pushRetryDelays: [TimeInterval] = [2, 8]
```

Add these methods (in a `// MARK: Push` section after `mintTurnCredential()`):

```swift
    func setPushTransport(_ transport: any MobilePushTransport, retryDelays: [TimeInterval], now: @escaping @Sendable () -> Date) {
        pushTransport = transport; pushRetryDelays = retryDelays; pushClock = now
    }

    /// Feeds the planner what the panes show now and sends what it decides, sealed per phone.
    /// Best effort: a phone that is off or a relay that is down only misses the notification,
    /// and failures go to the log, never to the screen.
    @discardableResult
    public func pushObserve(_ panes: [MobilePushPane]) async -> [Task<Void, Never>] {
        let now = pushClock()
        let stamp = ISO8601DateFormatter().string(from: now)
        let messages = pushPlanner.observe(panes, hostId: hostId, hostName: hostName, at: stamp)
        guard !messages.isEmpty else { return [] }
        let devices = deviceRegistry.pushDevices()
        guard !devices.isEmpty else { return [] }
        guard let relay = relayURL, let url = MobilePushRelay.sendURL(relay), let hostToken = try? loadOrCreateControlToken() else {
            Self.log.info("push: no relay to send through")
            return []
        }
        let serverId = RelayEndpoint.serverId(hostToken: hostToken)
        pushViewing = pushViewing.filter { $0.value.until > now }
        var tasks: [Task<Void, Never>] = []
        for message in messages {
            guard let plaintext = message.encodedForPush() else { continue }
            for device in MobilePushRecipients.targets(for: message, devices: devices, viewing: pushViewing, now: now) {
                guard let key = MobilePushSeal.key(tokenHash: device.tokenHash, clientId: device.deviceId),
                      let payload = try? MobilePushSeal.seal(plaintext, key: key, handle: device.handle) else { continue }
                let request = MobilePushSendRequest(serverId: serverId, handle: device.handle, payload: payload)
                tasks.append(Task { await self.deliver(request, to: url, hostToken: hostToken, deviceId: device.deviceId) })
            }
        }
        return tasks
    }

    private func deliver(_ request: MobilePushSendRequest, to url: URL, hostToken: String, deviceId: String) async {
        var waits = pushRetryDelays
        while true {
            switch await pushTransport.send(request, to: url, hostToken: hostToken) {
            case .sent:
                return
            case .gone:
                deviceRegistry.clearPush(deviceId, handle: request.handle)
                Self.log.info("push: the relay no longer knows a phone's handle; dropped it")
                return
            case .failed(let status):
                Self.log.error("push: the relay refused a push (\(status, privacy: .public))")
                return
            case .retry(let after):
                guard !waits.isEmpty else { Self.log.error("push: the relay could not be reached; gave up"); return }
                let wait = max(waits.removeFirst(), min(after ?? 0, 30))
                if wait > 0 { try? await Task.sleep(nanoseconds: UInt64(wait * 1_000_000_000)) }
            }
        }
    }
```

`ISO8601DateFormatter()` prints `1970-01-01T00:16:40Z` for 1,000 s, which the first test checks.

- [ ] **Step 5: Run them and expect them to pass**

Run: MAC-RUN with `'\.(MobilePushRelayTests|MobilePushPlannerTests|MobilePushSealTests|MobileRemoteTests)/'`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add native/macos/Sources/MightyCore/Remote/MobilePush.swift native/macos/Sources/MightyCore/Remote/MobileRemoteService.swift native/macos/Tests/MightyCoreTests/MobileRemoteTests.swift native/macos/Tests/MightyCoreTests/MobilePushTests.swift
git commit -m "(mac) the Mac seals each push for the phone and hands it to the relay, tries a busy relay up to three times, forgets a handle the relay no longer knows, and only logs a failure" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## Task 9: Mac 앱이 창의 변화를 푸시 계획기에 넘긴다

**Files:**
- Create: `native/macos/Sources/MightyClaude/AppStore+MobilePush.swift`
- Modify: `native/macos/Sources/MightyClaude/AppStore.swift` (`var mobilePushTask: Task<Void, Never>?`를 `mobileSubscriptions` 옆에)
- Modify: `native/macos/Sources/MightyClaude/AppStore+MobileRemote.swift` (`configureMobileRemote()`에서 `configureMobilePush()` 호출)

**Interfaces:**
- Consumes: `$snapshot`, `$toolPermissions`, `$antigravityBoard`, `$loginRequired`, `guidedAutoAllowing`, `permissionResponseKey(sessionId:request:)`, `MobilePermission(request:)`, `MobilePermission(antigravity:)`, `FilePaneKind.phoneVisible`, `RemoteWorkspacePhone.sessions`, `mobileRemote.pushObserve(_:)`.
- Produces: `AppStore.configureMobilePush()`, `AppStore.mobilePushObserve(snapshot:permissions:antigravity:login:)`.

**어디서 무엇을 읽는가.**
- 폰이 볼 수 있는 창만 본다. 그 목록은 `mobileObserve`와 같다(파일 창·원격 워크스페이스는 빠진다).
- 요청은 `toolPermissions`의 `pending` 가운데 스타일이 자동 허용 중인 것(`guidedAutoAllowing`)을 뺀 것과, 그 창의 Antigravity 카드다.
- 로그인 만료는 확인을 마친 `loginRequired`다. 날 `reason == "auth"`는 쓰지 않는다. 실패 알림 뒤에 로그인 알림이 오면 폰에서는 같은 자리(세션의 상태 알림)를 바꾼다(Task 16).
- 모바일 리모트가 꺼져 있으면 아무것도 하지 않는다.
- 관찰은 하나씩 순서대로 넘긴다(앞 작업이 끝난 뒤 다음 작업).

- [ ] **Step 1: Implement**

Create `native/macos/Sources/MightyClaude/AppStore+MobilePush.swift`:

```swift
import Combine
import Foundation
import MightyCore

/// Phone notifications (docs/mobile-remote.md "푸시 알림"): what the panes show is handed to the
/// mobile service, whose planner decides what to push. The Mac is the reference for Windows.
extension AppStore {
    func configureMobilePush() {
        // @Published fires before the property is assigned: pass the incoming value on.
        $snapshot.sink { [weak self] value in self?.mobilePushObserve(snapshot: value) }.store(in: &mobileSubscriptions)
        $toolPermissions.sink { [weak self] value in self?.mobilePushObserve(permissions: value) }.store(in: &mobileSubscriptions)
        $antigravityBoard.sink { [weak self] value in self?.mobilePushObserve(antigravity: value) }.store(in: &mobileSubscriptions)
        $loginRequired.sink { [weak self] value in self?.mobilePushObserve(login: value) }.store(in: &mobileSubscriptions)
    }

    func mobilePushObserve(snapshot incoming: AppSnapshot? = nil, permissions: [String: [ToolPermissionRequest]]? = nil,
                           antigravity: AntigravityPermissionBoard? = nil, login: [String: String]? = nil) {
        guard !ending, mobileBridge != nil else { return }
        let snapshot = incoming ?? self.snapshot
        guard snapshot.mobileRemote?.enabled == true else { return }
        let permissions = permissions ?? toolPermissions
        let antigravity = antigravity ?? antigravityBoard
        let login = login ?? loginRequired
        let names = Dictionary(snapshot.workspaces.map { ($0.id, $0.name) }, uniquingKeysWith: { first, _ in first })
        let sessions = FilePaneKind.phoneVisible(RemoteWorkspacePhone.sessions(snapshot.sessions, workspaces: snapshot.workspaces))
        let panes = sessions.map { session -> MobilePushPane in
            var requests = (permissions[session.id] ?? [])
                .filter { $0.state == "pending" && !guidedAutoAllowing.contains(permissionResponseKey(sessionId: session.id, request: $0)) }
                .map { MobilePushRequest(MobilePermission(request: $0)) }
            if let card = antigravity[session.id] { requests.append(MobilePushRequest(MobilePermission(antigravity: card))) }
            let last = session.logs.last(where: { $0.activity == nil && !$0.text.isEmpty })
            return MobilePushPane(sessionId: session.id, workspace: names[session.workspaceId] ?? "", title: session.title, status: session.status,
                                  preview: last.map { String($0.text.prefix(400)) }, requests: requests, signInLost: login[session.id] != nil)
        }
        let previous = mobilePushTask
        mobilePushTask = Task { [mobileRemote] in
            await previous?.value
            await mobileRemote.pushObserve(panes)
        }
    }
}
```

In `AppStore.swift`, add next to `var mobileSubscriptions = Set<AnyCancellable>()`:

```swift
    /// The last hand-off to the push planner; each waits for the one before, so observations keep their order.
    var mobilePushTask: Task<Void, Never>?
```

In `AppStore+MobileRemote.swift` `configureMobileRemote()`, add `configureMobilePush()` on the line after the `$delegationChildren.sink { … }.store(in: &mobileSubscriptions)` line.

- [ ] **Step 2: Build**

Run: MAC-BUILD
Expected: `Build complete!` and no errors. Nothing is launched.

- [ ] **Step 3: Run the core suites again**

Run: MAC-RUN with `'\.(MobilePush.*|MobileRemoteTests)/'`
Expected: pass.

- [ ] **Step 4: Commit**

```bash
git add native/macos/Sources/MightyClaude/AppStore+MobilePush.swift native/macos/Sources/MightyClaude/AppStore.swift native/macos/Sources/MightyClaude/AppStore+MobileRemote.swift
git commit -m "(mac) with the phone remote on, the Mac pushes finished and failed runs, permission requests, questions, plan approvals, Antigravity cards and a lost sign-in to the phones that asked" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## Task 10: Windows가 Mac과 같은 봉인과 계획기를 갖는다

**Files:**
- Create: `native/windows/MightyClaude.Core/Remote/MobilePush.cs`
- Create: `native/windows/MightyClaude.Core.Tests/MobilePushVerification.cs`
- Modify: `native/windows/MightyClaude.Core.Tests/MightyClaude.Core.Tests.csproj` (픽스처 포함)
- Modify: `native/windows/MightyClaude.Core.Tests/Verification.cs` (등록 줄, `await Test("Mobile remote SharedCipherVectors", …)` 바로 뒤)

**Interfaces:**
- Consumes: 픽스처 `push.json`. `MobilePermission`(agy 병합 후 `Antigravity`), `ClaudePlanMode.ToolName`, `Wire.Json`.
- Produces (Mac Task 5–6, 8의 C# 짝; 종류·버튼은 JSON과 같은 문자열):
  - `public static class MobilePushKinds { const string Completed, Failed, Permission, Question, Plan, Signin; static readonly string[] All }`, `public static class MobilePushButtonKinds { const string Claude, Antigravity, AntigravityOnce, None }`
  - `public static class MobilePushSeal { const string Info; const byte Version; const int MaximumPlaintext, MaximumEnvelope; static byte[]? Key(string tokenHash, string clientId); static string Seal(byte[] plaintext, byte[] key, string handle, byte[]? nonce = null); static byte[]? Open(string envelope, byte[] key, string handle) }`
  - `public sealed record MobilePushRequest(string Id, string RunId, string Kind, string Summary, string Buttons) { static MobilePushRequest From(MobilePermission permission) }`
  - `public sealed record MobilePushPane(string SessionId, string Workspace, string Title, string Status, string? Preview, IReadOnlyList<MobilePushRequest> Requests, bool SignInLost)`
  - `public sealed record MobilePushPermission(string RequestId, string RunId, string Buttons)`
  - `public sealed record MobilePushMessage(string Id, string Kind, string HostId, string HostName, string Workspace, string Title, string Summary, string SessionId, MobilePushPermission? Permission, string At) { int V { get; init; } = 1; byte[]? EncodedForPush() }`
  - `public sealed class MobilePushPlanner { IReadOnlyList<MobilePushMessage> Observe(IReadOnlyList<MobilePushPane> panes, string hostId, string hostName, string at) }`
  - `public sealed record MobilePushDevice(string DeviceId, string Handle, string TokenHash, IReadOnlySet<string> Kinds)`, `public sealed record MobilePushViewing(string SessionId, DateTimeOffset Until)`
  - `public static class MobilePushRecipients { static readonly TimeSpan ViewingWindow; static IReadOnlyList<MobilePushDevice> Targets(MobilePushMessage message, IEnumerable<MobilePushDevice> devices, IReadOnlyDictionary<string, MobilePushViewing> viewing, DateTimeOffset now) }`
  - `public static class MobilePushWire { static bool ValidHandle(string value); static IReadOnlyList<string> Kinds(IEnumerable<string> values) }`
  - `public static class MobilePushRelay { static Uri? SendUri(string relay) }`
  - `public enum MobilePushSendStatus { Sent, Gone, Retry, Failed }`, `public sealed record MobilePushSendOutcome(MobilePushSendStatus Status, TimeSpan? RetryAfter = null, int Code = 0) { static MobilePushSendOutcome FromStatus(int status, TimeSpan? retryAfter) }`
  - `public sealed record MobilePushSendRequest(string ServerId, string Handle, string Payload)`
  - `public interface IMobilePushTransport { Task<MobilePushSendOutcome> SendAsync(MobilePushSendRequest request, Uri url, string hostToken, CancellationToken token) }`, `public sealed class MobilePushHttpTransport : IMobilePushTransport`

규칙은 Mac Task 6의 "계획기 규칙"과 "버튼" 그대로다. 유니코드 스칼라 자르기는 `string.EnumerateRunes()`로 한다.

- [ ] **Step 1: Write the failing test**

Create `native/windows/MightyClaude.Core.Tests/MobilePushVerification.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MightyClaude.Core;

/// <summary>
/// Push notifications, held to the Mac's shared fixture (native/contracts/fixtures/push.json;
/// M/Tests/MightyCoreTests/MobilePushTests.swift).
/// </summary>
internal static class MobilePushVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static readonly Lazy<JsonNode> Fixture = new(() =>
    {
        using var stream = typeof(MobilePushVerification).Assembly.GetManifestResourceStream("MightyClaude.Core.Tests.Push.json") ?? throw new InvalidOperationException("push.json is not embedded");
        return JsonNode.Parse(stream)!;
    });
    private static string S(JsonNode node, string key) => node[key]!.GetValue<string>();

    internal static Task SealFollowsTheFixture()
    {
        var seal = Fixture.Value["seal"]!;
        var key = MobilePushSeal.Key(S(seal, "tokenHash"), S(seal, "clientId")) ?? throw new InvalidOperationException("no key");
        Check(Convert.ToBase64String(key) == S(seal, "keyB64"), "the key is the Mac's");
        Check(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(S(seal, "deviceToken")))).ToLowerInvariant() == S(seal, "tokenHash"), "the token hash is SHA-256 of the token");
        Check(MobilePushSeal.Seal(Encoding.UTF8.GetBytes(S(seal, "plaintext")), key, S(seal, "handle"), Convert.FromBase64String(S(seal, "nonceB64"))) == S(seal, "envelope"), "the envelope is the Mac's");
        Check(MobilePushSeal.Open(S(seal, "envelope"), key, S(seal, "handle")) is { } plain && Encoding.UTF8.GetString(plain) == S(seal, "plaintext"), "the envelope opens");
        foreach (var tampered in seal["tampered"]!.AsArray()) Check(MobilePushSeal.Open(tampered!.GetValue<string>(), key, S(seal, "handle")) is null, "a tampered envelope does not open");
        Check(MobilePushSeal.Open(S(seal, "envelope"), key, S(seal, "otherHandle")) is null, "another handle does not open it");
        Check(MobilePushSeal.Key("not hex", S(seal, "clientId")) is null, "a bad hash makes no key");
        var refused = false;
        try { MobilePushSeal.Seal(new byte[2049], key, S(seal, "handle")); } catch (ArgumentException) { refused = true; }
        Check(refused, "a message over 2048 bytes is not sealed");
        return Task.CompletedTask;
    }

    internal static Task PlannerFollowsTheFixture()
    {
        var planner = Fixture.Value["planner"]!;
        var subject = new MobilePushPlanner();
        foreach (var step in planner["steps"]!.AsArray())
        {
            var note = S(step!, "note");
            var panes = step!["panes"].Deserialize<List<MobilePushPane>>(Wire.Json)!;
            var sent = subject.Observe(panes, S(planner, "hostId"), S(planner, "hostName"), S(planner, "at"));
            var expected = step["expect"]!.AsArray();
            Check(sent.Count == expected.Count, note + ": count " + sent.Count);
            for (var i = 0; i < sent.Count; i++)
            {
                var message = sent[i]; var want = expected[i]!;
                Check(message.V == 1 && message.HostId == S(planner, "hostId") && message.HostName == S(planner, "hostName") && message.At == S(planner, "at"), note + ": constants");
                Check(message.Id == S(want, "id") && message.Kind == S(want, "kind") && message.SessionId == S(want, "sessionId"), note + ": id " + message.Id);
                Check(message.Workspace == S(want, "workspace") && message.Title == S(want, "title") && message.Summary == S(want, "summary"), note + ": text");
                var permission = want["permission"];
                Check(permission is null ? message.Permission is null : message.Permission == new MobilePushPermission(S(permission, "requestId"), S(permission, "runId"), S(permission, "buttons")), note + ": permission");
            }
        }
        return Task.CompletedTask;
    }

    internal static Task RequestsTakeTheirButtonsFromTheCard()
    {
        MobilePermission Card(string tool, bool canAllow = true, string? plan = null, MobileAntigravityCard? agy = null) => new("r", "u", tool, "t", null, [], "s", canAllow, null, plan, null, agy);
        Check(MobilePushRequest.From(Card("Bash")).Buttons == MobilePushButtonKinds.Claude, "a tool permission gets allow and deny");
        Check(MobilePushRequest.From(Card("Bash", false)).Buttons == MobilePushButtonKinds.None, "a deny-only request gets no buttons");
        Check(MobilePushRequest.From(Card("AskUserQuestion")).Kind == MobilePushKinds.Question, "a question");
        Check(MobilePushRequest.From(Card(ClaudePlanMode.ToolName, plan: "p")).Kind == MobilePushKinds.Plan, "a plan");
        var once = new MobileAntigravityCard([new MobileAntigravityAction("command", "command", "RunCommand", "rm x", "command(rm x)", true)]);
        var always = new MobileAntigravityCard([new MobileAntigravityAction("command", "command", "RunCommand", "ls", "command(ls)")]);
        var ruleless = new MobileAntigravityCard([new MobileAntigravityAction("other", "mcp", "Mcp")]);
        Check(MobilePushRequest.From(Card("AntigravityPermission", agy: once)).Buttons == MobilePushButtonKinds.AntigravityOnce, "once only");
        Check(MobilePushRequest.From(Card("AntigravityPermission", agy: always)).Buttons == MobilePushButtonKinds.Antigravity, "once or always");
        Check(MobilePushRequest.From(Card("AntigravityPermission", false, agy: ruleless)).Buttons == MobilePushButtonKinds.None, "no rule, no buttons");
        return Task.CompletedTask;
    }

    internal static Task RecipientsAndRelayAnswers()
    {
        var message = new MobilePushMessage("x", MobilePushKinds.Completed, "h", "H", "w", "t", "", "pane-1", null, "t");
        var all = new MobilePushDevice("a", "ha", new string('0', 64), MobilePushKinds.All.ToHashSet());
        var quiet = new MobilePushDevice("b", "hb", new string('0', 64), new HashSet<string> { MobilePushKinds.Permission });
        var now = DateTimeOffset.FromUnixTimeSeconds(1000);
        Check(MobilePushRecipients.Targets(message, [all, quiet], new Dictionary<string, MobilePushViewing>(), now).Select(d => d.DeviceId).SequenceEqual(["a"]), "only the kinds a phone turned on");
        Check(MobilePushRecipients.Targets(message, [all], new Dictionary<string, MobilePushViewing> { ["a"] = new("pane-1", now.AddSeconds(10)) }, now).Count == 0, "not the session it is viewing");
        Check(MobilePushRecipients.Targets(message, [all], new Dictionary<string, MobilePushViewing> { ["a"] = new("pane-1", now.AddSeconds(-1)) }, now).Count == 1, "an expired view does not hold");
        Check(MobilePushRelay.SendUri("wss://relay.example.invalid")?.ToString() == "https://relay.example.invalid/push/v1/send", "wss → https");
        Check(MobilePushRelay.SendUri("ws://127.0.0.1:8787")?.ToString() == "http://127.0.0.1:8787/push/v1/send", "ws → http");
        Check(MobilePushRelay.SendUri("ftp://relay.example.invalid") is null, "other schemes are refused");
        Check(MobilePushSendOutcome.FromStatus(202, null).Status == MobilePushSendStatus.Sent, "202");
        Check(new[] { 403, 404, 410 }.All(s => MobilePushSendOutcome.FromStatus(s, null).Status == MobilePushSendStatus.Gone), "gone");
        Check(MobilePushSendOutcome.FromStatus(429, TimeSpan.FromSeconds(5)) == new MobilePushSendOutcome(MobilePushSendStatus.Retry, TimeSpan.FromSeconds(5), 429), "429 retries");
        Check(MobilePushSendOutcome.FromStatus(503, null).Status == MobilePushSendStatus.Failed, "503 is not retried");
        var wide = new string('가', 80);
        var subject = new MobilePushPlanner();
        subject.Observe([], "h", "H", "t");
        var pane = new MobilePushPane("p", wide, wide, "running", null, [new("r", "u", MobilePushKinds.Permission, string.Concat(Enumerable.Repeat("😀", 300)), MobilePushButtonKinds.Claude)], false);
        var wideMessage = subject.Observe([pane], "h", wide, "t").Single();
        Check(wideMessage.Summary.EnumerateRunes().Count() == 200, "the summary is clipped to 200 scalars");
        Check(wideMessage.EncodedForPush() is { Length: <= MobilePushSeal.MaximumPlaintext }, "a wide message fits");
        return Task.CompletedTask;
    }
}
```

In `MightyClaude.Core.Tests.csproj`, add next to the `claude-plan-mode.json` `EmbeddedResource` line:

```xml
    <EmbeddedResource Include="../../contracts/fixtures/push.json" LogicalName="MightyClaude.Core.Tests.Push.json" />
```

In `Verification.cs`, add right after `await Test("Mobile remote SharedCipherVectors", …);`:

```csharp
        await Test("mobile push: the seal follows the shared fixture", MobilePushVerification.SealFollowsTheFixture);
        await Test("mobile push: the planner follows the shared steps", MobilePushVerification.PlannerFollowsTheFixture);
        await Test("mobile push: requests take their buttons from the card", MobilePushVerification.RequestsTakeTheirButtonsFromTheCard);
        await Test("mobile push: recipients, the relay address and its answers", MobilePushVerification.RecipientsAndRelayAnswers);
```

- [ ] **Step 2: Run it and expect it to fail**

Run: CORE-RUN with `'mobile push'`
Expected: build errors `CS0103: The name 'MobilePushSeal' does not exist in the current context` (and the other new types).

- [ ] **Step 3: Implement**

Create `native/windows/MightyClaude.Core/Remote/MobilePush.cs`:

```csharp
using System.Buffers.Text;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using BcChaCha = Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305;

namespace MightyClaude.Core;

/// <summary>The push kinds, as the wire spells them (M/MightyCore/Remote/MobilePush.swift MobilePushKind).</summary>
public static class MobilePushKinds
{
    public const string Completed = "completed", Failed = "failed", Permission = "permission", Question = "question", Plan = "plan", Signin = "signin";
    public static readonly string[] All = [Completed, Failed, Permission, Question, Plan, Signin];
}

/// <summary>The buttons a permission push carries (MobilePushButtons).</summary>
public static class MobilePushButtonKinds
{
    public const string Claude = "claude", Antigravity = "antigravity", AntigravityOnce = "antigravityOnce", None = "none";
}

/// <summary>
/// The seal on a push, the Mac's MobilePushSeal: key = HKDF-SHA256(SHA-256 of the device token — the stored TokenHash —,
/// salt UTF-8 clientId, info "mighty-push-v1"); envelope = base64url(0x01 ‖ nonce ‖ ChaCha20-Poly1305 ‖ tag), AAD "mighty-push-v1:" + handle.
/// </summary>
public static class MobilePushSeal
{
    public const string Info = "mighty-push-v1";
    public const byte Version = 1;
    public const int MaximumPlaintext = 2048, MaximumEnvelope = 3072;
    public static byte[]? Key(string tokenHash, string clientId)
        => Regex.IsMatch(tokenHash, "^[0-9a-f]{64}\\z") ? HKDF.DeriveKey(HashAlgorithmName.SHA256, Convert.FromHexString(tokenHash), 32, Encoding.UTF8.GetBytes(clientId), Encoding.UTF8.GetBytes(Info)) : null;
    private static byte[] Aad(string handle) => Encoding.UTF8.GetBytes(Info + ":" + handle);
    public static string Seal(byte[] plaintext, byte[] key, string handle, byte[]? nonce = null)
    {
        if (plaintext.Length > MaximumPlaintext) throw new ArgumentException("Push message too long.");
        nonce ??= RandomNumberGenerator.GetBytes(12);
        var cipher = new BcChaCha();
        cipher.Init(true, new AeadParameters(new KeyParameter(key), 128, nonce, Aad(handle)));
        var output = new byte[cipher.GetOutputSize(plaintext.Length)];
        var length = cipher.ProcessBytes(plaintext, 0, plaintext.Length, output, 0);
        length += cipher.DoFinal(output, length);
        var envelope = new byte[13 + length];
        envelope[0] = Version; nonce.CopyTo(envelope, 1); Array.Copy(output, 0, envelope, 13, length);
        return Base64Url.EncodeToString(envelope);
    }
    public static byte[]? Open(string envelope, byte[] key, string handle)
    {
        if (envelope.Length > MaximumEnvelope || !Regex.IsMatch(envelope, "^[A-Za-z0-9_-]*\\z")) return null;
        byte[] bytes;
        try { bytes = Base64Url.DecodeFromChars(envelope); } catch (FormatException) { return null; }
        if (bytes.Length < 29 || bytes[0] != Version) return null;
        try
        {
            var cipher = new BcChaCha();
            cipher.Init(false, new AeadParameters(new KeyParameter(key), 128, bytes[1..13], Aad(handle)));
            var output = new byte[cipher.GetOutputSize(bytes.Length - 13)];
            var length = cipher.ProcessBytes(bytes, 13, bytes.Length - 13, output, 0);
            length += cipher.DoFinal(output, length);
            return output[..length];
        }
        catch (InvalidCipherTextException) { return null; }
    }
}

public sealed record MobilePushRequest(string Id, string RunId, string Kind, string Summary, string Buttons)
{
    /// <summary>A pending card read as a push (M/MobilePushRequest.init(_ permission:)).</summary>
    public static MobilePushRequest From(MobilePermission permission)
    {
        if (permission.Antigravity is { } card)
        {
            var ruled = card.Actions.Where(a => a.Rule is not null).ToArray();
            var buttons = !permission.CanAllow || ruled.Length == 0 ? MobilePushButtonKinds.None : ruled.Any(a => a.OnceOnly == true) ? MobilePushButtonKinds.AntigravityOnce : MobilePushButtonKinds.Antigravity;
            return new(permission.Id, permission.RunId, MobilePushKinds.Permission, permission.Summary, buttons);
        }
        if (permission.Questionnaire is not null || permission.ToolName == "AskUserQuestion") return new(permission.Id, permission.RunId, MobilePushKinds.Question, permission.Summary, MobilePushButtonKinds.None);
        if (permission.Plan is not null || permission.ToolName == ClaudePlanMode.ToolName) return new(permission.Id, permission.RunId, MobilePushKinds.Plan, permission.Summary, MobilePushButtonKinds.None);
        return new(permission.Id, permission.RunId, MobilePushKinds.Permission, permission.Summary, permission.CanAllow ? MobilePushButtonKinds.Claude : MobilePushButtonKinds.None);
    }
}

/// <summary>One pane as the planner sees it; Status is the pane's own run status.</summary>
public sealed record MobilePushPane(string SessionId, string Workspace, string Title, string Status, string? Preview, IReadOnlyList<MobilePushRequest> Requests, bool SignInLost);
public sealed record MobilePushPermission(string RequestId, string RunId, string Buttons);

/// <summary>The plaintext inside a push envelope.</summary>
public sealed record MobilePushMessage(string Id, string Kind, string HostId, string HostName, string Workspace, string Title, string Summary, string SessionId, MobilePushPermission? Permission, string At)
{
    public int V { get; init; } = 1;
    /// <summary>UTF-8 JSON within MaximumPlaintext; the summary goes first when it does not fit.</summary>
    public byte[]? EncodedForPush()
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(this, Wire.Json);
        if (data.Length <= MobilePushSeal.MaximumPlaintext) return data;
        data = JsonSerializer.SerializeToUtf8Bytes(this with { Summary = "" }, Wire.Json);
        return data.Length <= MobilePushSeal.MaximumPlaintext ? data : null;
    }
}

/// <summary>Decides what a host pushes (M/MobilePushPlanner; the rules are in docs/mobile-remote.md "푸시 알림").</summary>
public sealed class MobilePushPlanner
{
    public const int SentMemory = 512;
    private sealed record Memory(string Status, HashSet<string> Requests, bool SignInLost, int Runs, int Signins);
    private bool baseline;
    private Dictionary<string, Memory> memory = [];
    private readonly Queue<string> sent = new();
    private readonly HashSet<string> sentSet = [];
    public IReadOnlyList<MobilePushMessage> Observe(IReadOnlyList<MobilePushPane> panes, string hostId, string hostName, string at)
    {
        var messages = new List<MobilePushMessage>(); var next = new Dictionary<string, Memory>();
        foreach (var pane in panes)
        {
            memory.TryGetValue(pane.SessionId, out var previous);
            var runs = previous?.Runs ?? 0; var signins = previous?.Signins ?? 0;
            if (baseline)
            {
                foreach (var request in pane.Requests)
                {
                    var id = RequestId(request);
                    if (previous?.Requests.Contains(id) == true) continue;
                    Emit(messages, pane, request.Kind, id, request.Summary, request.Kind == MobilePushKinds.Permission ? new(request.Id, request.RunId, request.Buttons) : null, hostId, hostName, at);
                }
                if (previous?.Status == "running" && pane.Requests.Count == 0 && (pane.Status == "completed" || pane.Status == "error"))
                {
                    runs++;
                    var kind = pane.Status == "completed" ? MobilePushKinds.Completed : MobilePushKinds.Failed;
                    Emit(messages, pane, kind, $"run:{pane.SessionId}:{runs}:{kind}", pane.Preview ?? "", null, hostId, hostName, at);
                }
                if (pane.SignInLost && previous?.SignInLost != true)
                {
                    signins++;
                    Emit(messages, pane, MobilePushKinds.Signin, $"signin:{pane.SessionId}:{signins}", "", null, hostId, hostName, at);
                }
            }
            next[pane.SessionId] = new(pane.Status, pane.Requests.Select(RequestId).ToHashSet(), pane.SignInLost, runs, signins);
        }
        memory = next; baseline = true;
        return messages;
    }
    private static string RequestId(MobilePushRequest request) => $"request:{request.RunId}:{request.Id}";
    private void Emit(List<MobilePushMessage> messages, MobilePushPane pane, string kind, string id, string summary, MobilePushPermission? permission, string hostId, string hostName, string at)
    {
        if (!sentSet.Add(id)) return;
        sent.Enqueue(id);
        if (sent.Count > SentMemory) sentSet.Remove(sent.Dequeue());
        messages.Add(new(id, kind, hostId, Clip(hostName, 80), Clip(pane.Workspace, 80), Clip(pane.Title, 80), Clip(OneLine(summary), 200), pane.SessionId, permission, at));
    }
    internal static string Clip(string text, int limit) => string.Concat(text.EnumerateRunes().Take(limit).Select(rune => rune.ToString()));
    internal static string OneLine(string text) => text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
}

public sealed record MobilePushDevice(string DeviceId, string Handle, string TokenHash, IReadOnlySet<string> Kinds);
public sealed record MobilePushViewing(string SessionId, DateTimeOffset Until);

public static class MobilePushRecipients
{
    public static readonly TimeSpan ViewingWindow = TimeSpan.FromSeconds(45);
    public static IReadOnlyList<MobilePushDevice> Targets(MobilePushMessage message, IEnumerable<MobilePushDevice> devices, IReadOnlyDictionary<string, MobilePushViewing> viewing, DateTimeOffset now)
        => devices.Where(d => d.Kinds.Contains(message.Kind) && !(viewing.TryGetValue(d.DeviceId, out var seen) && seen.SessionId == message.SessionId && seen.Until > now)).ToArray();
}

public static class MobilePushWire
{
    public static bool ValidHandle(string value) => Regex.IsMatch(value, "^[A-Za-z0-9_-]{43}\\z");
    /// <summary>The kinds this host knows, in its own order; unknown names from a newer phone are left out.</summary>
    public static IReadOnlyList<string> Kinds(IEnumerable<string> values) { var wanted = values.ToHashSet(); return MobilePushKinds.All.Where(wanted.Contains).ToArray(); }
}

public static class MobilePushRelay
{
    /// <summary>The relay's push route beside the WebSocket the host dials.</summary>
    public static Uri? SendUri(string relay)
    {
        if (!Uri.TryCreate(relay, UriKind.Absolute, out var uri) || uri.Host.Length == 0) return null;
        var scheme = uri.Scheme.ToLowerInvariant() switch { "wss" or "https" => "https", "ws" or "http" => "http", _ => null };
        if (scheme is null) return null;
        return new UriBuilder(scheme, uri.Host, uri.IsDefaultPort ? -1 : uri.Port, "/push/v1/send").Uri;
    }
}

public enum MobilePushSendStatus { Sent, Gone, Retry, Failed }
public sealed record MobilePushSendOutcome(MobilePushSendStatus Status, TimeSpan? RetryAfter = null, int Code = 0)
{
    public static MobilePushSendOutcome FromStatus(int status, TimeSpan? retryAfter) => status switch
    {
        >= 200 and <= 299 => new(MobilePushSendStatus.Sent, null, status),
        403 or 404 or 410 => new(MobilePushSendStatus.Gone, null, status),
        429 or 500 or 502 or 504 => new(MobilePushSendStatus.Retry, retryAfter, status),
        _ => new(MobilePushSendStatus.Failed, null, status),
    };
}
public sealed record MobilePushSendRequest(string ServerId, string Handle, string Payload);

public interface IMobilePushTransport
{
    Task<MobilePushSendOutcome> SendAsync(MobilePushSendRequest request, Uri url, string hostToken, CancellationToken token);
}

public sealed class MobilePushHttpTransport : IMobilePushTransport
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
    public async Task<MobilePushSendOutcome> SendAsync(MobilePushSendRequest request, Uri url, string hostToken, CancellationToken token)
    {
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(request, Wire.Json)) };
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", hostToken);
            using var response = await Client.SendAsync(message, token);
            return MobilePushSendOutcome.FromStatus((int)response.StatusCode, response.Headers.RetryAfter?.Delta);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            Trace.TraceInformation("mobile push: relay unreachable (" + ex.GetType().Name + ")");
            return new(MobilePushSendStatus.Retry);
        }
    }
}
```

- [ ] **Step 4: Run it and expect it to pass**

Run: CORE-RUN with `'mobile push'`
Expected: the four `mobile push:` checks print PASS, and `RESULT: BASELINE`.

- [ ] **Step 5: Commit**

```bash
git add native/windows/MightyClaude.Core/Remote/MobilePush.cs native/windows/MightyClaude.Core.Tests/MobilePushVerification.cs native/windows/MightyClaude.Core.Tests/MightyClaude.Core.Tests.csproj native/windows/MightyClaude.Core.Tests/Verification.cs
git commit -m "(windows) a Windows host seals pushes and decides what to push exactly as the Mac does, held to the shared fixture" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## Task 11: 폰이 Windows에 핸들과 보고 있는 세션을 알린다

**Files:**
- Modify: `native/windows/MightyClaude.Core/Remote/MobileIdentity.cs` (`MobilePushRegistration`, `Device.Push`, `SetPush`·`ClearPush`·`PushDevices`, 읽을 때의 정리)
- Modify: `native/windows/MightyClaude.Core/Remote/MobileRelayHost.cs` (`Identity`, `RelayForPush`, `Enabled`)
- Create: `native/windows/MightyClaude.Core/Remote/MobilePushHub.cs` (등록·보기 부분. 전송은 Task 12)
- Modify: `native/windows/MightyClaude.Core/Remote/MobileDesktopRouter.cs` (`Push` 속성, `/m1/push/*`, `Capabilities`에 `"push"`)
- Modify: `native/windows/MightyClaude.Core.Tests/MobilePushVerification.cs`, `native/windows/MightyClaude.Core.Tests/Verification.cs`

**Interfaces:**
- Consumes: Task 10의 형식. 공유 키 `remote.error.badPushHandle`·`remote.error.pushNeedsToken`(Task 7), `mobileRemote.device.saveFailed`, `remote.error.paneNotFound`.
- Produces:
  - `public sealed record MobilePushRegistration(string Handle, IReadOnlyList<string> Kinds, string UpdatedAt)`, `public enum MobilePushRegistrationResult { Saved, NeedsToken, Unsaved }`
  - `MobileIdentity.SetPush(string id, string handle, IReadOnlyList<string> kinds) -> MobilePushRegistrationResult`, `ClearPush(string id, string? handle = null)`, `PushDevices() -> IReadOnlyList<MobilePushDevice>`
  - `MobileRelayHost`: `internal MobileIdentity Identity`, `public string RelayForPush`, `public bool Enabled`
  - `public sealed class MobilePushHub : IDisposable { MobilePushHub(DesktopService service, MobileRelayHost host, string hostName); MobilePushRegistrationResult Register(string device, string handle, IReadOnlyList<string> kinds); void Unregister(string device); void View(string device, string? sessionId) }`
  - `MobileDesktopRouter.Push: MobilePushHub?`. 라우트 세 개는 Mac Task 7과 같은 상태와 문구를 쓴다. 바디 없는 `viewing`과 `sessionId: null`은 지운다.

- [ ] **Step 1: Write the failing test**

Append to `MobilePushVerification.cs` (inside the class):

```csharp
    /// <summary>A host with one phone paired the ordinary way; returns the phone's id and token.</summary>
    internal static (MobileRelayHost Host, string ClientId, string Token) PairedHost(string directory)
    {
        var host = new MobileRelayHost(Path.Combine(directory, "mobile"), "Test PC", MobileDesktopRouter.Capabilities, (_, _, _, _, _) => Task.FromResult(new MobileReply(404, new { })));
        const string clientId = "AAECAwQFBgcICQoLDA0ODw";
        var auth = JsonSerializer.SerializeToElement(new { type = "auth", pairingKey = host.Identity.PairingKey, clientId, clientName = "Phone" });
        var result = host.Identity.Authenticate(auth, false);
        return (host, clientId, result.DeviceToken ?? throw new InvalidOperationException("no token: " + result.Error));
    }

    internal static async Task RoutesRegisterAHandleAndAView()
    {
        var directory = Verification.Temp();
        await using var service = new DesktopService(Path.Combine(directory, "state"), null, directory);
        await service.InitializeAsync();
        var (host, clientId, token) = PairedHost(directory);
        await using var _ = host;
        using var push = new MobilePushHub(service, host, "Test PC");
        using var router = new MobileDesktopRouter(service, Path.Combine(directory, "router"), "Test PC", (_, _) => Task.FromResult<object?>("started"), _ => new([])) { Push = push };
        Task<MobileReply> Route(string path, object body, string device) => router.RouteAsync("POST", path, JsonSerializer.SerializeToElement(body), device, CancellationToken.None);
        var handle = new string('h', 43);
        Check((await Route("/m1/push/register", new { handle, kinds = new[] { "permission", "completed", "future-kind" } }, clientId)).Status == 200, "a phone with a token registers");
        var devices = host.Identity.PushDevices();
        Check(devices.Count == 1 && devices[0].Handle == handle && devices[0].Kinds.SetEquals(["permission", "completed"]), "the handle and the known kinds are kept");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
        Check(devices[0].TokenHash == hash, "the key material is the token hash");
        Check((await Route("/m1/push/register", new { handle = "short", kinds = Array.Empty<string>() }, clientId)).Status == 400, "a bad handle is refused");
        Check((await Route("/m1/push/register", new { handle, kinds = Array.Empty<string>() }, "legacy")).Status == 409, "a phone without a token is refused");
        Check((await Route("/m1/push/viewing", new { sessionId = "bad id!" }, clientId)).Status == 404, "a malformed pane id");
        Check((await Route("/m1/push/viewing", new { sessionId = (string?)null }, clientId)).Status == 200, "null clears the view");
        Check((await Route("/m1/push/unregister", new { }, clientId)).Status == 200 && host.Identity.PushDevices().Count == 0, "unregister forgets the handle");
        Check(MobileDesktopRouter.Capabilities.Contains("push"), "the host advertises push");
    }
```

Register it in `Verification.cs` after the Task 10 lines:

```csharp
        await Test("mobile push: a phone registers a handle and a view", MobilePushVerification.RoutesRegisterAHandleAndAView);
```

- [ ] **Step 2: Run it and expect it to fail**

Run: CORE-RUN with `'mobile push'`
Expected: build errors `CS0246: The type or namespace name 'MobilePushHub' could not be found` and `'MobileRelayHost' does not contain a definition for 'Identity'`.

- [ ] **Step 3: Implement the identity**

In `MobileIdentity.cs`:
- Add above `public sealed class MobileIdentity`:

```csharp
/// <summary>Where and what a phone asked to be pushed (M/MobileDeviceRegistry.swift MobilePushRegistration).</summary>
public sealed record MobilePushRegistration(string Handle, IReadOnlyList<string> Kinds, string UpdatedAt);
public enum MobilePushRegistrationResult { Saved, NeedsToken, Unsaved }
```

- Change the `Device` record to `private sealed record Device(string Id, string Name, string? TokenHash, string FirstSeen, string LastSeen, MobilePushRegistration? Push = null);`.
- In the constructor, inside the `if (File.Exists(path))` block, add this line right after the long validation `if (...) throw new IOException("Invalid mobile identity.");`:

```csharp
            // A handle the row cannot use is dropped, the phone kept (M/MobileDeviceRegistry.swift load()).
            state = state with { Devices = state.Devices.Select(d => d.Push is { } push && (d.TokenHash is null || !MobilePushWire.ValidHandle(push.Handle)) ? d with { Push = null } : d).ToList() };
```

- Add these methods after `Contains(...)`:

```csharp
    public MobilePushRegistrationResult SetPush(string id, string handle, IReadOnlyList<string> kinds)
    {
        lock (sync)
        {
            if (state.Devices.FirstOrDefault(d => d.Id == id)?.TokenHash is null) return MobilePushRegistrationResult.NeedsToken;
            try { Save(state with { Devices = state.Devices.Select(d => d.Id == id ? d with { Push = new(handle, kinds.ToArray(), Wire.Now()) } : d).ToList() }); }
            catch (IOException) { return MobilePushRegistrationResult.Unsaved; }
            catch (UnauthorizedAccessException) { return MobilePushRegistrationResult.Unsaved; }
            return MobilePushRegistrationResult.Saved;
        }
    }
    /// <summary>Forgets a phone's handle; with <paramref name="handle"/>, only while it is still that one (the relay said it is gone).</summary>
    public void ClearPush(string id, string? handle = null)
    {
        lock (sync)
        {
            if (state.Devices.FirstOrDefault(d => d.Id == id)?.Push is not { } current || handle is not null && current.Handle != handle) return;
            try { Save(state with { Devices = state.Devices.Select(d => d.Id == id ? d with { Push = null } : d).ToList() }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Trace.TraceWarning("mobile push: " + ex.GetType().Name); }
        }
    }
    public IReadOnlyList<MobilePushDevice> PushDevices()
    {
        lock (sync) return state.Devices.Where(d => d.Push is not null && d.TokenHash is not null).Select(d => new MobilePushDevice(d.Id, d.Push!.Handle, d.TokenHash!, d.Push.Kinds.ToHashSet())).ToArray();
    }
```

Add `using System.Diagnostics;` at the top. `Rotate()` already clears every device, so the handles go with them.

- [ ] **Step 4: Implement the host accessors and the hub's routes**

In `MobileRelayHost.cs`, add next to `public string HostId => identity.HostId;`:

```csharp
    internal MobileIdentity Identity => identity;
    /// <summary>The relay this host dials; the push route sits beside it (docs/relay.md "푸시 알림").</summary>
    public string RelayForPush => NormalizeRelay(settings.RelayURL) ?? DefaultRelay;
    public bool Enabled => settings.Enabled;
```

Create `native/windows/MightyClaude.Core/Remote/MobilePushHub.cs`:

```csharp
using System.Diagnostics;
using System.Globalization;

namespace MightyClaude.Core;

/// <summary>
/// Phone notifications on a Windows host (M/MightyClaude/AppStore+MobilePush.swift and MobileRemoteService push):
/// which phone wants what, which session each has on screen, and what to send.
/// </summary>
public sealed class MobilePushHub : IDisposable
{
    private readonly DesktopService service;
    private readonly MobileRelayHost host;
    private readonly object sync = new();
    private readonly Dictionary<string, MobilePushViewing> viewing = [];
    public string HostName { get; }
    internal Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;
    public MobilePushHub(DesktopService service, MobileRelayHost host, string hostName)
    {
        this.service = service; this.host = host; HostName = hostName;
    }
    public MobilePushRegistrationResult Register(string device, string handle, IReadOnlyList<string> kinds) => host.Identity.SetPush(device, handle, kinds);
    public void Unregister(string device) { host.Identity.ClearPush(device); lock (sync) viewing.Remove(device); }
    public void View(string device, string? sessionId)
    {
        lock (sync)
        {
            if (sessionId is null) viewing.Remove(device);
            else viewing[device] = new(sessionId, Clock() + MobilePushRecipients.ViewingWindow);
        }
    }
    public void Dispose() { }
}
```

In `MobileDesktopRouter.cs`:
- Change the `Capabilities` array to end with `"structure", "direct", "push"];`.
- Add the property `public MobilePushHub? Push { get; set; }` next to `public Func<string, int>? QueueCount { get; set; }`.
- In `RouteAsync`, right after the `if (method == "POST" && query.Count != 0) throw …;` line, insert:

```csharp
            if (method == "POST" && route.Length == 3 && route[1] == "push" && Push is { } push)
            {
                var value = Object(body);
                switch (route[2])
                {
                    case "register":
                        var handle = Required(value, "handle");
                        if (!MobilePushWire.ValidHandle(handle)) throw new MobileRequestException(400, Locale.Get("remote.error.badPushHandle"));
                        if (!value.TryGetProperty("kinds", out var list) || list.ValueKind != JsonValueKind.Array) throw new MobileRequestException(400, "Missing kinds.");
                        var kinds = MobilePushWire.Kinds(list.EnumerateArray().Where(k => k.ValueKind == JsonValueKind.String).Select(k => k.GetString()!));
                        return push.Register(device, handle, kinds) switch
                        {
                            MobilePushRegistrationResult.Saved => Reply(OK()),
                            MobilePushRegistrationResult.NeedsToken => throw new MobileRequestException(409, Locale.Get("remote.error.pushNeedsToken")),
                            _ => throw new MobileRequestException(503, Locale.Get("mobileRemote.device.saveFailed")),
                        };
                    case "unregister":
                        push.Unregister(device);
                        return Reply(OK());
                    case "viewing":
                        if (!value.TryGetProperty("sessionId", out var session) || session.ValueKind == JsonValueKind.Null) push.View(device, null);
                        else if (session.ValueKind == JsonValueKind.String && session.GetString() is { } shown && Wire.Identifier(shown)) push.View(device, shown);
                        else throw new MobileRequestException(404, Locale.Get("remote.error.paneNotFound"));
                        return Reply(OK());
                }
            }
```

- [ ] **Step 5: Run it and expect it to pass**

Run: CORE-RUN with `'mobile push|Mobile remote|Mobile host'`
Expected: the five `mobile push:` checks PASS, the existing mobile checks still PASS, `RESULT: BASELINE`.

- [ ] **Step 6: Commit**

```bash
git add native/windows/MightyClaude.Core/Remote/MobileIdentity.cs native/windows/MightyClaude.Core/Remote/MobileRelayHost.cs native/windows/MightyClaude.Core/Remote/MobilePushHub.cs native/windows/MightyClaude.Core/Remote/MobileDesktopRouter.cs native/windows/MightyClaude.Core.Tests/MobilePushVerification.cs native/windows/MightyClaude.Core.Tests/Verification.cs
git commit -m "(windows) a paired phone can give a Windows host its push handle and the kinds it wants, and say which session is on its screen; the host advertises push" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## Task 12: Windows 호스트가 창의 변화를 푸시로 보낸다

**Files:**
- Modify: `native/windows/MightyClaude.Core/Remote/MobilePushHub.cs` (요청 추적, `Observe`, `Push`, `Deliver`)
- Modify: `native/windows/MightyClaude.WinUI/MainWindow.MobileRemote.cs` (허브 만들기, 라우터 연결, 타이머에서 관찰, 종료)
- Modify: `native/windows/MightyClaude.Core.Tests/MobilePushVerification.cs`, `native/windows/MightyClaude.Core.Tests/Verification.cs`

**Interfaces:**
- Consumes: `DesktopService.ToolPermissionChanged`, `IsRemoteSession`, `Snapshot`, `AntigravityCard(sessionId)`(agy), `RemoteWorkspacePhone.Sessions`, `MobilePermission.From`, `MobilePermission.FromAntigravity`, `MobileRelayHost.Identity`·`RelayForPush`·`Enabled`·`HostId`. WinUI `loginRetries.Requests`(확인된 로그인 만료).
- Produces:
  - `MobilePushHub.SignInLost: Func<string, bool>` (WinUI가 채운다)
  - `MobilePushHub.Observe() -> IReadOnlyList<Task>`: 모바일 리모트가 켜졌을 때만 동작한다. 폰이 볼 수 있는 창을 읽어 `Push`에 넘긴다.
  - `internal MobilePushHub.Push(IReadOnlyList<MobilePushPane> panes) -> IReadOnlyList<Task>`, `internal Transport`, `internal RetryDelays`, `internal Relay: Func<string>`
  - 실패는 `Trace`에만 남긴다(`mobile push: …`).

**왜 타이머에서 관찰하는가.** WinUI는 이미 250ms마다 `mobileNotify`를 돈다. 그 틱에서 `Observe()`를 부르면 UI 스레드에서 스냅숏과 로그인 상태를 안전하게 읽는다. 연결된 폰이 없어도 부른다. 푸시는 폰이 연결되지 않았을 때 쓰는 것이기 때문이다. 전송은 `Task.Run`으로 UI 밖에서 한다.

- [ ] **Step 1: Write the failing test**

Append to `MobilePushVerification.cs` (inside the class):

```csharp
    private sealed class RecordingTransport : IMobilePushTransport
    {
        private readonly object gate = new();
        public readonly List<(MobilePushSendRequest Request, Uri Url, string HostToken)> Sent = [];
        public readonly Queue<MobilePushSendOutcome> Outcomes = new();
        public Task<MobilePushSendOutcome> SendAsync(MobilePushSendRequest request, Uri url, string hostToken, CancellationToken token)
        {
            lock (gate) { Sent.Add((request, url, hostToken)); return Task.FromResult(Outcomes.Count > 0 ? Outcomes.Dequeue() : new MobilePushSendOutcome(MobilePushSendStatus.Sent)); }
        }
    }

    internal static async Task ANewPermissionIsSealedAndSentAndAGoneHandleForgotten()
    {
        var directory = Verification.Temp();
        await using var service = new DesktopService(Path.Combine(directory, "state"), null, directory);
        await service.InitializeAsync();
        var (host, clientId, token) = PairedHost(directory);
        await using var _ = host;
        var transport = new RecordingTransport();
        using var push = new MobilePushHub(service, host, "Test PC") { Transport = transport, RetryDelays = [TimeSpan.Zero, TimeSpan.Zero], Relay = () => "wss://relay.example.invalid", Clock = () => DateTimeOffset.FromUnixTimeSeconds(1000) };
        var handle = new string('h', 43);
        Check(push.Register(clientId, handle, [MobilePushKinds.Permission]) == MobilePushRegistrationResult.Saved, "registered");
        var idle = new MobilePushPane("pane-1", "demo", "Claude 1", "running", null, [], false);
        await Task.WhenAll(push.Push([idle]));
        var asking = idle with { Requests = [new("req-1", "run-1", MobilePushKinds.Permission, "git status", MobilePushButtonKinds.Claude)] };
        await Task.WhenAll(push.Push([asking]));
        Check(transport.Sent.Count == 1, "one push: " + transport.Sent.Count);
        var (request, url, hostToken) = transport.Sent[0];
        Check(url.ToString() == "https://relay.example.invalid/push/v1/send", "to the relay's push route");
        Check(request.ServerId == Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hostToken))).ToLowerInvariant(), "serverId is SHA-256 of the host token");
        var key = MobilePushSeal.Key(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant(), clientId)!;
        var plain = MobilePushSeal.Open(request.Payload, key, handle) ?? throw new InvalidOperationException("the phone could not open it");
        using var message = JsonDocument.Parse(plain);
        Check(message.RootElement.GetProperty("id").GetString() == "request:run-1:req-1" && message.RootElement.GetProperty("v").GetInt32() == 1, "the message");
        Check(message.RootElement.GetProperty("at").GetString() == "1970-01-01T00:16:40Z", "the Mac's timestamp form");
        Check(message.RootElement.GetProperty("permission").GetProperty("buttons").GetString() == "claude", "the buttons");

        push.View(clientId, "pane-1");
        await Task.WhenAll(push.Push([asking with { Requests = [.. asking.Requests, new("req-2", "run-1", MobilePushKinds.Permission, "", MobilePushButtonKinds.Claude)] }]));
        Check(transport.Sent.Count == 1, "nothing for the session the phone is viewing");
        push.View(clientId, null);

        transport.Outcomes.Enqueue(new(MobilePushSendStatus.Retry)); transport.Outcomes.Enqueue(new(MobilePushSendStatus.Retry)); transport.Outcomes.Enqueue(new(MobilePushSendStatus.Gone, null, 410));
        await Task.WhenAll(push.Push([asking with { Requests = [.. asking.Requests, new("req-3", "run-1", MobilePushKinds.Permission, "", MobilePushButtonKinds.Claude)] }]));
        Check(transport.Sent.Count == 4 && host.Identity.PushDevices().Count == 0, "two retries, then a gone handle is forgotten");
    }
```

Register it in `Verification.cs` after the Task 11 line:

```csharp
        await Test("mobile push: a new permission is sealed, sent, and a gone handle forgotten", MobilePushVerification.ANewPermissionIsSealedAndSentAndAGoneHandleForgotten);
```

- [ ] **Step 2: Run it and expect it to fail**

Run: CORE-RUN with `'mobile push'`
Expected: build errors: `'MobilePushHub' does not contain a definition for 'Transport'`, and the same for `Push`.

- [ ] **Step 3: Implement the hub**

Replace `native/windows/MightyClaude.Core/Remote/MobilePushHub.cs` with:

```csharp
using System.Diagnostics;
using System.Globalization;

namespace MightyClaude.Core;

/// <summary>
/// Phone notifications on a Windows host (M/MightyClaude/AppStore+MobilePush.swift and MobileRemoteService push):
/// which phone wants what, which session each has on screen, and what to send. Best effort: failures are traced only.
/// </summary>
public sealed class MobilePushHub : IDisposable
{
    private readonly DesktopService service;
    private readonly MobileRelayHost host;
    private readonly object sync = new();
    private readonly Dictionary<string, ToolPermissionRequest> permissions = [];
    private readonly Dictionary<string, MobilePushViewing> viewing = [];
    private readonly MobilePushPlanner planner = new();
    public string HostName { get; }
    /// <summary>Whether the pane's sign-in was confirmed lost (the WinUI login recovery's remembered requests).</summary>
    public Func<string, bool> SignInLost { get; set; } = _ => false;
    internal IMobilePushTransport Transport { get; set; } = new MobilePushHttpTransport();
    /// <summary>Waits before the second and third attempt; a relay's Retry-After (up to 30 s) wins when longer.</summary>
    internal IReadOnlyList<TimeSpan> RetryDelays { get; set; } = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8)];
    internal Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;
    internal Func<string> Relay { get; set; }
    public MobilePushHub(DesktopService service, MobileRelayHost host, string hostName)
    {
        this.service = service; this.host = host; HostName = hostName; Relay = () => host.RelayForPush;
        service.ToolPermissionChanged += Permission;
    }
    private void Permission(ToolPermissionRequest value)
    {
        // A remote pane's requests are its host's (MobileDesktopRouter.Permission).
        if (service.IsRemoteSession(value.RunId)) return;
        lock (sync) { if (value.State == "pending") permissions[value.Id] = value; else permissions.Remove(value.Id); }
    }
    public MobilePushRegistrationResult Register(string device, string handle, IReadOnlyList<string> kinds) => host.Identity.SetPush(device, handle, kinds);
    public void Unregister(string device) { host.Identity.ClearPush(device); lock (sync) viewing.Remove(device); }
    public void View(string device, string? sessionId)
    {
        lock (sync)
        {
            if (sessionId is null) viewing.Remove(device);
            else viewing[device] = new(sessionId, Clock() + MobilePushRecipients.ViewingWindow);
        }
    }

    /// <summary>Reads what a phone may see now and pushes what changed; call on the UI thread (SignInLost reads UI state).</summary>
    public IReadOnlyList<Task> Observe()
    {
        if (!host.Enabled) return [];
        var snapshot = service.Snapshot;
        var names = snapshot.Workspaces.GroupBy(w => w.Id).ToDictionary(g => g.Key, g => g.First().Name);
        ToolPermissionRequest[] pending;
        lock (sync) pending = permissions.Values.ToArray();
        var panes = RemoteWorkspacePhone.Sessions(snapshot.Sessions, snapshot.Workspaces).Where(pane => pane.Kind != "files").Select(pane =>
        {
            var requests = pending.Where(p => p.RunId == pane.Id).Select(p => MobilePushRequest.From(MobilePermission.From(p))).ToList();
            if (service.AntigravityCard(pane.Id) is { } card) requests.Add(MobilePushRequest.From(MobilePermission.FromAntigravity(card)));
            var last = pane.Logs.LastOrDefault(l => l.Activity is null && l.Text.Length > 0)?.Text;
            return new MobilePushPane(pane.Id, names.GetValueOrDefault(pane.WorkspaceId, ""), pane.Title, pane.Status, last is null ? null : MobilePushPlanner.Clip(last, 400), requests, SignInLost(pane.Id));
        }).ToArray();
        return Push(panes);
    }

    internal IReadOnlyList<Task> Push(IReadOnlyList<MobilePushPane> panes)
    {
        var now = Clock();
        IReadOnlyList<MobilePushMessage> messages;
        lock (sync) messages = planner.Observe(panes, host.HostId, HostName, now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        if (messages.Count == 0) return [];
        var devices = host.Identity.PushDevices();
        if (devices.Count == 0) return [];
        if (MobilePushRelay.SendUri(Relay()) is not { } url) { Trace.TraceInformation("mobile push: no relay to send through"); return []; }
        var hostToken = host.Identity.HostToken; var serverId = host.Identity.ServerId;
        Dictionary<string, MobilePushViewing> seen;
        lock (sync)
        {
            foreach (var stale in viewing.Where(v => v.Value.Until <= now).Select(v => v.Key).ToArray()) viewing.Remove(stale);
            seen = new(viewing);
        }
        var tasks = new List<Task>();
        foreach (var message in messages)
        {
            if (message.EncodedForPush() is not { } plaintext) continue;
            foreach (var device in MobilePushRecipients.Targets(message, devices, seen, now))
            {
                if (MobilePushSeal.Key(device.TokenHash, device.DeviceId) is not { } key) continue;
                var request = new MobilePushSendRequest(serverId, device.Handle, MobilePushSeal.Seal(plaintext, key, device.Handle));
                tasks.Add(Task.Run(() => Deliver(request, url, hostToken, device.DeviceId)));
            }
        }
        return tasks;
    }

    private async Task Deliver(MobilePushSendRequest request, Uri url, string hostToken, string deviceId)
    {
        var waits = new Queue<TimeSpan>(RetryDelays);
        while (true)
        {
            MobilePushSendOutcome outcome;
            try { outcome = await Transport.SendAsync(request, url, hostToken, CancellationToken.None); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Trace.TraceWarning("mobile push: " + ex.GetType().Name); outcome = new(MobilePushSendStatus.Retry); }
            switch (outcome.Status)
            {
                case MobilePushSendStatus.Sent:
                    return;
                case MobilePushSendStatus.Gone:
                    host.Identity.ClearPush(deviceId, request.Handle);
                    Trace.TraceInformation("mobile push: the relay no longer knows a phone's handle; dropped it");
                    return;
                case MobilePushSendStatus.Failed:
                    Trace.TraceWarning("mobile push: the relay refused a push (" + outcome.Code + ")");
                    return;
                default:
                    if (waits.Count == 0) { Trace.TraceWarning("mobile push: the relay could not be reached; gave up"); return; }
                    var wait = waits.Dequeue();
                    if (outcome.RetryAfter is { } after && after > wait) wait = after > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : after;
                    if (wait > TimeSpan.Zero) await Task.Delay(wait);
                    break;
            }
        }
    }

    public void Dispose() => service.ToolPermissionChanged -= Permission;
}
```

- [ ] **Step 4: Wire WinUI**

In `MainWindow.MobileRemote.cs`:
- Add the field `private MobilePushHub? mobilePush;` next to `private MobileDesktopRouter? mobileRouter;`.
- In `InitializeMobileRemote()`, right after `mobileRouter.DirectEndpoints = () => mobileHost?.DirectEndpoints ?? [];`, add:

```csharp
        // Phone notifications (M/MightyClaude/AppStore+MobilePush.swift): a confirmed lost sign-in is the login recovery's remembered request.
        mobilePush = new MobilePushHub(service, mobileHost, Environment.MachineName) { SignInLost = id => loginRetries.Requests.ContainsKey(id) };
        mobileRouter.Push = mobilePush;
```

- In the `mobileNotify.Tick` handler, replace the first line `if (closing || mobileNotifying || mobileRouter is null || mobileHost is null || mobileHost.Status.Connections == 0) return;` with:

```csharp
            if (closing) return;
            // Pushes are for phones that are not connected, so they are planned on every tick.
            try { mobilePush?.Observe(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.TraceWarning("mobile push: " + ex.GetType().Name); }
            if (mobileNotifying || mobileRouter is null || mobileHost is null || mobileHost.Status.Connections == 0) return;
```

- In `ShutdownMobileRemote()`, before `mobileRouter?.Dispose(); mobileRouter = null;`, add `mobilePush?.Dispose(); mobilePush = null;`.

- [ ] **Step 5: Run it and expect it to pass**

Run: CORE-RUN with `'mobile push'`, then WINUI-COMPILE
Expected: the six `mobile push:` checks PASS, `RESULT: BASELINE`; `WinUI: C# 컴파일 오류 없음`.

- [ ] **Step 6: Commit**

```bash
git add native/windows/MightyClaude.Core/Remote/MobilePushHub.cs native/windows/MightyClaude.WinUI/MainWindow.MobileRemote.cs native/windows/MightyClaude.Core.Tests/MobilePushVerification.cs native/windows/MightyClaude.Core.Tests/Verification.cs
git commit -m "(windows) with the phone remote on, a Windows host pushes the same finished and failed runs, requests, questions, plans, Antigravity cards and lost sign-ins as the Mac, sealed per phone through the relay" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## Task 13: 폰이 호스트의 봉투를 연다

**Files:**
- Create: `mobile/src/lib/push/seal.ts`
- Create: `mobile/src/lib/push/message.ts`
- Create: `mobile/src/__tests__/push-seal.test.ts`

**Interfaces:**
- Consumes: `@noble/ciphers/chacha` `chacha20poly1305`, `@noble/hashes/hkdf` `hkdf`, `@noble/hashes/sha256` `sha256`, `fromBase64`·`utf8Encode`·`utf8Decode` (`@/api/relay/crypto`). 픽스처 `push.json`의 `seal`.
- Produces:
  - `PUSH_INFO = 'mighty-push-v1'`, `PUSH_VERSION = 1`, `PUSH_ENVELOPE_MAX = 3072`
  - `pushKey(deviceToken: string, clientId: string): Uint8Array`
  - `openPush(envelope: string, key: Uint8Array, handle: string): Uint8Array | null`
  - `PUSH_KINDS`, `type PushKind`, `PUSH_BUTTONS`, `type PushButtons`
  - `interface PushMessage { v: 1; id: string; kind: PushKind; hostId: string; hostName: string; workspace: string; title: string; summary: string; sessionId: string; permission?: { requestId: string; runId: string; buttons: PushButtons }; at: string }`
  - `parsePushMessage(bytes: Uint8Array): PushMessage | null`. 모르는 종류·버튼, 빠진 필드, 너무 긴 값은 null이다(그 알림은 버린다).

- [ ] **Step 1: Write the failing test**

Create `mobile/src/__tests__/push-seal.test.ts`:

```ts
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

import { utf8Decode, utf8Encode } from '@/api/relay/crypto';
import { parsePushMessage } from '@/lib/push/message';
import { openPush, pushKey } from '@/lib/push/seal';

interface SealFixture {
  deviceToken: string;
  clientId: string;
  handle: string;
  keyB64: string;
  plaintext: string;
  envelope: string;
  tampered: string[];
  otherHandle: string;
}

const fixture = JSON.parse(
  readFileSync(resolve(__dirname, '../../../native/contracts/fixtures/push.json'), 'utf8'),
) as { seal: SealFixture };
const seal = fixture.seal;

describe('push seal (native/contracts/fixtures/push.json)', () => {
  it('derives the key the host derives from its token hash', () => {
    expect(Buffer.from(pushKey(seal.deviceToken, seal.clientId)).toString('base64')).toBe(seal.keyB64);
  });

  it('opens the host envelope and reads the message', () => {
    const opened = openPush(seal.envelope, pushKey(seal.deviceToken, seal.clientId), seal.handle);
    expect(opened).not.toBeNull();
    expect(utf8Decode(opened!)).toBe(seal.plaintext);
    expect(parsePushMessage(opened!)).toEqual({
      v: 1,
      id: 'request:run-1:req-1',
      kind: 'permission',
      hostId: 'host-1',
      hostName: 'Test Mac',
      workspace: 'demo',
      title: 'Claude 1',
      summary: 'git status',
      sessionId: 'pane-1',
      permission: { requestId: 'req-1', runId: 'run-1', buttons: 'claude' },
      at: '2026-10-23T01:02:03Z',
    });
  });

  it('refuses a tampered envelope, another handle, another key and an oversized one', () => {
    const key = pushKey(seal.deviceToken, seal.clientId);
    for (const tampered of seal.tampered) expect(openPush(tampered, key, seal.handle)).toBeNull();
    expect(openPush(seal.envelope, key, seal.otherHandle)).toBeNull();
    expect(openPush(seal.envelope, pushKey('another-token', seal.clientId), seal.handle)).toBeNull();
    expect(openPush('A'.repeat(3073), key, seal.handle)).toBeNull();
    expect(openPush('not base64url!', key, seal.handle)).toBeNull();
  });
});

describe('parsePushMessage', () => {
  const base = {
    v: 1, id: 'run:p:1:completed', kind: 'completed', hostId: 'h', hostName: 'H',
    workspace: 'w', title: 't', summary: '', sessionId: 'p', at: '2026-10-23T01:02:03Z',
  };
  const parse = (value: unknown) => parsePushMessage(utf8Encode(JSON.stringify(value)));

  it('reads a message without buttons', () => {
    expect(parse(base)).toEqual(base);
  });

  it('drops what it cannot show safely', () => {
    expect(parse({ ...base, kind: 'unknown' })).toBeNull();
    expect(parse({ ...base, v: 2 })).toBeNull();
    expect(parse({ ...base, sessionId: undefined })).toBeNull();
    expect(parse({ ...base, title: 'x'.repeat(400) })).toBeNull();
    expect(parse({ ...base, kind: 'permission', permission: { requestId: 'r', runId: 'u', buttons: 'later' } })).toBeNull();
    expect(parsePushMessage(utf8Encode('{not json'))).toBeNull();
  });
});
```

The fixture's `plaintext` uses the id `request:run-1:req-1`. That message only tests the seal; the planner's own ids (`request:…`) are checked by the Mac and Windows tests.

- [ ] **Step 2: Run it and expect it to fail**

Run: PHONE-RUN with `src/__tests__/push-seal.test.ts`
Expected: FAIL — `Cannot find module '@/lib/push/message'`.

- [ ] **Step 3: Implement**

Create `mobile/src/lib/push/seal.ts`:

```ts
import { chacha20poly1305 } from '@noble/ciphers/chacha';
import { hkdf } from '@noble/hashes/hkdf';
import { sha256 } from '@noble/hashes/sha256';

import { fromBase64, utf8Encode } from '@/api/relay/crypto';

/**
 * Opening a host's push (docs/mobile-remote.md "푸시 알림"). The key comes from this
 * phone's device token for that host: HKDF-SHA256(SHA-256(token), salt clientId,
 * info "mighty-push-v1"). The host keeps exactly that SHA-256, so neither side needs a
 * new secret, and the relay and FCM only ever carry the envelope.
 */
export const PUSH_INFO = 'mighty-push-v1';
export const PUSH_VERSION = 1;
export const PUSH_ENVELOPE_MAX = 3072;
const ENVELOPE = /^[A-Za-z0-9_-]+$/;

export function pushKey(deviceToken: string, clientId: string): Uint8Array {
  return hkdf(sha256, sha256(utf8Encode(deviceToken)), utf8Encode(clientId), utf8Encode(PUSH_INFO), 32);
}

/** The plaintext, or null for anything that is not this host's envelope for this handle. */
export function openPush(envelope: string, key: Uint8Array, handle: string): Uint8Array | null {
  if (envelope.length > PUSH_ENVELOPE_MAX || !ENVELOPE.test(envelope)) return null;
  let bytes: Uint8Array;
  try {
    bytes = fromBase64(envelope);
  } catch {
    return null;
  }
  if (bytes.length < 1 + 12 + 16 || bytes[0] !== PUSH_VERSION) return null;
  try {
    return chacha20poly1305(key, bytes.subarray(1, 13), utf8Encode(`${PUSH_INFO}:${handle}`)).decrypt(bytes.subarray(13));
  } catch {
    return null;
  }
}
```

Create `mobile/src/lib/push/message.ts`:

```ts
import { utf8Decode } from '@/api/relay/crypto';

export const PUSH_KINDS = ['completed', 'failed', 'permission', 'question', 'plan', 'signin'] as const;
export type PushKind = (typeof PUSH_KINDS)[number];
export const PUSH_BUTTONS = ['claude', 'antigravity', 'antigravityOnce', 'none'] as const;
export type PushButtons = (typeof PUSH_BUTTONS)[number];

/** The plaintext a host seals (M/MightyCore/Remote/MobilePush.swift MobilePushMessage). */
export interface PushMessage {
  v: 1;
  id: string;
  kind: PushKind;
  hostId: string;
  hostName: string;
  workspace: string;
  title: string;
  summary: string;
  sessionId: string;
  permission?: { requestId: string; runId: string; buttons: PushButtons };
  at: string;
}

const LIMITS: Record<'id' | 'hostId' | 'hostName' | 'workspace' | 'title' | 'summary' | 'sessionId' | 'at', number> = {
  id: 300, hostId: 128, hostName: 80, workspace: 80, title: 80, summary: 200, sessionId: 128, at: 40,
};

function text(value: unknown, limit: number): value is string {
  return typeof value === 'string' && [...value].length <= limit;
}

export function parsePushMessage(bytes: Uint8Array): PushMessage | null {
  let value: unknown;
  try {
    value = JSON.parse(utf8Decode(bytes));
  } catch {
    return null;
  }
  if (typeof value !== 'object' || value === null) return null;
  const m = value as Record<string, unknown>;
  if (m['v'] !== 1 || !PUSH_KINDS.includes(m['kind'] as PushKind)) return null;
  for (const [key, limit] of Object.entries(LIMITS)) if (!text(m[key], limit)) return null;
  const message: PushMessage = {
    v: 1,
    id: m['id'] as string,
    kind: m['kind'] as PushKind,
    hostId: m['hostId'] as string,
    hostName: m['hostName'] as string,
    workspace: m['workspace'] as string,
    title: m['title'] as string,
    summary: m['summary'] as string,
    sessionId: m['sessionId'] as string,
    at: m['at'] as string,
  };
  const permission = m['permission'];
  if (permission !== undefined) {
    if (typeof permission !== 'object' || permission === null) return null;
    const p = permission as Record<string, unknown>;
    if (!text(p['requestId'], 128) || !text(p['runId'], 128) || !PUSH_BUTTONS.includes(p['buttons'] as PushButtons)) return null;
    message.permission = { requestId: p['requestId'], runId: p['runId'], buttons: p['buttons'] as PushButtons };
  }
  return message;
}
```

- [ ] **Step 4: Run it and expect it to pass**

Run: PHONE-RUN with `src/__tests__/push-seal.test.ts`
Expected: 5 tests pass; typecheck clean.

- [ ] **Step 5: Commit**

```bash
git add mobile/src/lib/push/seal.ts mobile/src/lib/push/message.ts mobile/src/__tests__/push-seal.test.ts
git commit -m "(mobile) the phone can open a host's sealed push with the key from its device token and read only well-formed messages" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## Task 14: 폰 빌드에 Firebase와 알림 라이브러리를 넣는다

**Files:**
- Modify: `mobile/package.json`, `mobile/package-lock.json` (의존성 4개)
- Create: `mobile/app.config.js`
- Create: `mobile/firebase.json`
- Modify: `mobile/app.json` (`android.permissions`에 `POST_NOTIFICATIONS`, `blockedPermissions`에서 `WAKE_LOCK` 빼기)
- Create: `mobile/modules/device-lock/expo-module.config.json`
- Create: `mobile/modules/device-lock/android/build.gradle`
- Create: `mobile/modules/device-lock/android/src/main/java/dev/mightyclaude/devicelock/DeviceLockModule.kt`
- Create: `mobile/src/lib/push/device-lock.ts`
- Create: `mobile/src/__tests__/push-config.test.ts`

**Interfaces:**
- Consumes: `expo` `requireOptionalNativeModule`.
- Produces:
  - 의존성: `@react-native-firebase/app`·`@react-native-firebase/messaging`·`@react-native-firebase/crashlytics` `~26.4.0`, `react-native-notify-kit` `~10.9.0`.
  - `app.config.js`가 `GOOGLE_SERVICES_JSON`이 가리키는 파일을 `android.googleServicesFile`로 넣고 RNFB 플러그인 둘을 더한다. 변수가 없으면 app.json 그대로다(로컬 개발, jest, typecheck). EAS 빌드(`EAS_BUILD`)에서 변수가 없으면 빌드를 멈춘다.
  - 로컬 Expo 모듈 `MightyDeviceLock.isDeviceLocked(): boolean`, JS `isDeviceLocked(): boolean`. 모듈이 없거나 실패하면 `true`다(잠긴 것으로 친다).

**라이브러리 선택과 이유.**
- **알림 표시와 버튼: `react-native-notify-kit`(Notifee API).**
  - `@notifee/react-native`는 2026-04에 보관되었고 마지막 판이 2024-12다.
  - `expo-notifications`는 자기 `FirebaseMessagingService`를 등록해 RNFB messaging과 부딪친다. 백그라운드 버튼 처리도 `expo-task-manager` 작업에 묶인다.
  - notify-kit은 유지되는 포크다. New Architecture·Expo 빌드에서 Maven 저장소 설정이 필요 없다. 문서화된 흐름이 그대로 맞는다: RNFB 데이터 메시지 → `displayNotification` → 헤드리스 `onBackgroundEvent(ACTION_PRESS)`.
- **잠금 해제 요구.** 세 라이브러리 모두 Android의 `Notification.Action.Builder.setAuthenticationRequired`를 열어 주지 않는다(iOS 필드만 있다). 그래서 버튼 처리기가 작은 로컬 모듈로 `KeyguardManager.isDeviceLocked`를 직접 확인한다. 잠겨 있으면 아무것도 보내지 않고 알림을 "잠금을 푼 뒤 다시 누르세요"로 바꾼다. 라이브러리 내부를 고치지 않고도 "잠금을 푼 뒤에만 동작"이 코드로 보장된다. notify-kit의 버튼은 보이지 않는 액티비티를 거치므로 OS가 먼저 잠금 해제를 요구할 수도 있다. 그렇더라도 결과는 같다.
- **RNFB 26.4.x.** 27.0은 Kotlin Gradle 플러그인 2.4 이상을 요구한다. RN 0.86/Expo 57의 기본 Kotlin(2.1.x)과 맞지 않을 수 있다. API는 같은 모듈식(`getMessaging()` 등)이다.
- **WAKE_LOCK.** RN의 헤드리스 작업 서비스(`HeadlessJsTaskService`)가 깨우기 잠금을 잡으므로 이 권한 없이는 백그라운드 메시지 처리기가 죽는다. 그래서 막힌 권한 목록에서 뺀다.

- [ ] **Step 1: Write the failing test**

Create `mobile/src/__tests__/push-config.test.ts`:

```ts
import { mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const appJson = require('../../app.json') as { expo: { android: { permissions: string[]; blockedPermissions: string[] }; plugins: unknown[] } };
const makeConfig = require('../../app.config.js') as (input: { config: typeof appJson.expo }) => typeof appJson.expo & {
  android: { googleServicesFile?: string };
};

const saved = { ...process.env };
afterEach(() => {
  process.env = { ...saved };
});

describe('push build config', () => {
  it('asks for notifications and lets headless tasks hold a wake lock', () => {
    expect(appJson.expo.android.permissions).toContain('android.permission.POST_NOTIFICATIONS');
    expect(appJson.expo.android.blockedPermissions).not.toContain('android.permission.WAKE_LOCK');
  });

  it('leaves the config alone without a google-services file outside a build', () => {
    delete process.env['GOOGLE_SERVICES_JSON'];
    delete process.env['EAS_BUILD'];
    const config = makeConfig({ config: appJson.expo });
    expect(config.android.googleServicesFile).toBeUndefined();
    expect(config.plugins).toEqual(appJson.expo.plugins);
  });

  it('adds the file and the Firebase plugins from GOOGLE_SERVICES_JSON', () => {
    const file = join(mkdtempSync(join(tmpdir(), 'gs-')), 'google-services.json');
    writeFileSync(file, '{}');
    process.env['GOOGLE_SERVICES_JSON'] = file;
    const config = makeConfig({ config: appJson.expo });
    expect(config.android.googleServicesFile).toBe(file);
    expect(config.plugins).toEqual([...appJson.expo.plugins, '@react-native-firebase/app', '@react-native-firebase/crashlytics']);
  });

  it('stops an EAS build that has no google-services file', () => {
    delete process.env['GOOGLE_SERVICES_JSON'];
    process.env['EAS_BUILD'] = 'true';
    expect(() => makeConfig({ config: appJson.expo })).toThrow(/GOOGLE_SERVICES_JSON/);
  });

  it('stops when the variable points at nothing', () => {
    process.env['GOOGLE_SERVICES_JSON'] = join(tmpdir(), 'missing-google-services.json');
    expect(() => makeConfig({ config: appJson.expo })).toThrow(/missing file/);
  });
});
```

- [ ] **Step 2: Run it and expect it to fail**

Run: PHONE-RUN with `src/__tests__/push-config.test.ts`
Expected: FAIL — `Cannot find module '../../app.config.js'`.

- [ ] **Step 3: Dependencies**

If `mobile/node_modules` is a symbolic link (into the main checkout), remove the link itself, so this worktree gets its own folder: `test -L mobile/node_modules && rm mobile/node_modules`. Do not delete the link target. Then:

Run: `cd /Users/young/Work/MightyClaude-firebase/mobile && npm install @react-native-firebase/app@~26.4.0 @react-native-firebase/messaging@~26.4.0 @react-native-firebase/crashlytics@~26.4.0 react-native-notify-kit@~10.9.0`
Expected: `package.json` gains the four dependencies; `package-lock.json` is updated.

- [ ] **Step 4: Config files**

Create `mobile/app.config.js`:

```js
// The Expo config is app.json plus what a public repository must not hold.
// GOOGLE_SERVICES_JSON names the Firebase Android config kept outside git (the release tooling
// sets it, docs/mobile-remote.md "푸시 알림"). Without it the config is app.json alone, which keeps
// jest, typecheck and `expo config` working; an EAS build without it stops instead of shipping an
// app whose push and crash reports silently do nothing.
const fs = require('node:fs');

module.exports = ({ config }) => {
  const file = process.env.GOOGLE_SERVICES_JSON;
  if (!file) {
    if (process.env.EAS_BUILD) throw new Error('GOOGLE_SERVICES_JSON is not set: point it at google-services.json before building.');
    return config;
  }
  if (!fs.existsSync(file)) throw new Error(`GOOGLE_SERVICES_JSON points at a missing file: ${file}`);
  return {
    ...config,
    android: { ...config.android, googleServicesFile: file },
    plugins: [...config.plugins, '@react-native-firebase/app', '@react-native-firebase/crashlytics'],
  };
};
```

Create `mobile/firebase.json`:

```json
{
  "react-native": {
    "crashlytics_auto_collection_enabled": false,
    "crashlytics_debug_enabled": false,
    "crashlytics_javascript_exception_handler_chaining_enabled": false,
    "messaging_auto_init_enabled": true,
    "messaging_android_headless_task_timeout": 30000
  }
}
```

Collection starts off. The app turns it on from the user's setting (default on, Task 18) before anything can be sent. The JS handler that Crashlytics installs is replaced by one that scrubs the error first (Task 18).

In `mobile/app.json`:
- Change `"permissions": ["android.permission.CAMERA"]` to `"permissions": ["android.permission.CAMERA", "android.permission.POST_NOTIFICATIONS"]`.
- Remove the line `"android.permission.WAKE_LOCK",` from `blockedPermissions`.

- [ ] **Step 5: The device-lock module**

Create `mobile/modules/device-lock/expo-module.config.json`:

```json
{
  "platforms": ["android"],
  "android": {
    "modules": ["dev.mightyclaude.devicelock.DeviceLockModule"]
  }
}
```

Create `mobile/modules/device-lock/android/build.gradle`:

```gradle
plugins {
  id 'com.android.library'
  id 'expo-module-gradle-plugin'
}

group = 'dev.mightyclaude'
version = '1.0.0'

android {
  namespace "dev.mightyclaude.devicelock"
  defaultConfig {
    versionCode 1
    versionName '1.0.0'
  }
}
```

Create `mobile/modules/device-lock/android/src/main/java/dev/mightyclaude/devicelock/DeviceLockModule.kt`:

```kotlin
package dev.mightyclaude.devicelock

import android.app.KeyguardManager
import android.content.Context
import expo.modules.kotlin.modules.Module
import expo.modules.kotlin.modules.ModuleDefinition

/**
 * Whether the phone is locked right now. A notification button that answers a permission
 * request checks this first and does nothing while the phone is locked, because no notification
 * library exposes Android's own "authentication required" flag for actions.
 * Anything unknown counts as locked.
 */
class DeviceLockModule : Module() {
  override fun definition() = ModuleDefinition {
    Name("MightyDeviceLock")

    Function("isDeviceLocked") {
      val context = appContext.reactContext ?: return@Function true
      val keyguard = context.getSystemService(Context.KEYGUARD_SERVICE) as? KeyguardManager ?: return@Function true
      keyguard.isDeviceLocked
    }
  }
}
```

Create `mobile/src/lib/push/device-lock.ts`:

```ts
import { requireOptionalNativeModule } from 'expo';

interface MightyDeviceLockModule {
  isDeviceLocked(): boolean;
}

/** Locked unless the native module says otherwise: a missing module must never let a button act. */
export function isDeviceLocked(): boolean {
  const native = requireOptionalNativeModule<MightyDeviceLockModule>('MightyDeviceLock');
  if (!native) return true;
  try {
    return native.isDeviceLocked();
  } catch {
    return true;
  }
}
```

- [ ] **Step 6: Run it and expect it to pass**

Run: PHONE-RUN with `src/__tests__/push-config.test.ts src/__tests__/app-assets.test.ts`, then `cd mobile && GOOGLE_SERVICES_JSON=/tmp/fake-google-services.json sh -c 'echo {} > /tmp/fake-google-services.json && npx expo config --json' | node -e "const c=JSON.parse(require('fs').readFileSync(0,'utf8')); if(!c.android.googleServicesFile||!c.plugins.includes('@react-native-firebase/app')) process.exit(1); console.log('ok')"`
Expected: both test files pass; typecheck clean; the config check prints `ok`.

- [ ] **Step 7: Commit**

```bash
git add mobile/package.json mobile/package-lock.json mobile/app.config.js mobile/firebase.json mobile/app.json mobile/modules/device-lock/expo-module.config.json mobile/modules/device-lock/android/build.gradle mobile/modules/device-lock/android/src/main/java/dev/mightyclaude/devicelock/DeviceLockModule.kt mobile/src/lib/push/device-lock.ts mobile/src/__tests__/push-config.test.ts
git commit -m "(mobile) the phone build carries Firebase messaging and Crashlytics from a google-services file kept outside git, a maintained notification library, and a check that the phone is unlocked" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

- [ ] **Step 8: 사용자 설정 B — Firebase 안드로이드 앱과 google-services.json (사용자가 직접)**

Tell the user, then go on; only Task 21's APK build needs it.
1. Firebase 콘솔 → 기존 프로젝트 → 앱 추가 → Android. 패키지 이름은 `dev.mightyclaude.mobile`이다. SHA-1은 이 기능에 필요 없다.
2. `google-services.json`을 받아 `~/Work/.mc-release/google-services.json`에 둔다. 저장소에는 넣지 않는다(`.gitignore`가 `mobile/google-services.json`도 막는다).
3. 같은 프로젝트에서 Crashlytics를 연다(콘솔의 Crashlytics → 시작하기). 첫 충돌 보고가 오면 대시보드가 켜진다.

---

## Task 15: 폰이 릴레이에서 핸들을 받아 호스트에 준다

**Files:**
- Modify: `mobile/src/api/types.ts` (`Capability`·`CAPABILITIES`에 `'push'`)
- Modify: `mobile/src/api/client.ts` (`MobileClient`에 `registerPush`·`unregisterPush`·`reportViewing`과 그 구현)
- Create: `mobile/src/lib/push/registry.ts`
- Modify: `mobile/src/store/hosts.ts` (`removeHost`에서 그 호스트의 핸들 지우기)
- Create: `mobile/src/__tests__/push-registry.test.ts`

**Interfaces:**
- Consumes: `SecretStore` (`@/lib/host-secrets`), `hasCapability`, `ApiError`, Task 13 `PushKind`.
- Produces:
  - `MobileClient.registerPush(input: { handle: string; kinds: PushKind[] }, signal?: AbortSignal): Promise<OkResponse>`, `unregisterPush(signal?: AbortSignal): Promise<OkResponse>`, `reportViewing(sessionId: string | null, signal?: AbortSignal): Promise<OkResponse>`
  - `PUSH_HANDLE_PREFIX = 'mightyclaude.push.v1.'`
  - `interface PushHandleRecord { handle: string; secret: string; relayUrl: string; fcmToken: string }`
  - `type PushFetch = (url: string, init: { method: string; headers: Record<string, string>; body?: string }) => Promise<{ status: number; json(): Promise<unknown> }>`
  - `relayHttpBase(relayUrl: string): string | null`
  - `readPushHandle(store: SecretStore, storageId: string): Promise<PushHandleRecord | null>`
  - `ensurePushHandle(deps: { store: SecretStore; fetch: PushFetch }, storageId: string, relayUrl: string, fcmToken: string): Promise<PushHandleRecord | null>`
  - `forgetPushHandle(deps: { store: SecretStore; fetch: PushFetch }, storageId: string): Promise<void>`
  - `findHostByHandle(store: SecretStore, storageIds: readonly string[], handle: string): Promise<string | null>`
  - `syncHostPush(deps: { store: SecretStore; fetch: PushFetch; client: Pick<MobileClient, 'info' | 'registerPush'> }, host: { id: string; relayUrl: string }, fcmToken: string, kinds: PushKind[]): Promise<'registered' | 'unsupported' | 'failed'>`

**흐름.**
- 핸들은 페어링마다 하나이고, 그 페어링의 릴레이(QR의 `relay=`)에서 받는다. 호스트는 자기 릴레이로 보내므로 둘은 같은 릴레이다.
- 기록(핸들·비밀·릴레이·FCM 토큰)은 SecureStore `mightyclaude.push.v1.<storageId>`에 둔다.
- FCM 토큰이 바뀌면 `PUT`으로 갱신한다. 404면 새로 등록한다. 릴레이가 바뀌었으면 옛 핸들을 지우고 새로 받는다.
- 호스트가 `"push"`를 광고하지 않거나 409(토큰 없는 구버전 묶음)면 `unsupported`다.
- 페어링을 지우면 릴레이에서 핸들을 지운다. 호스트는 다음 전송에서 404를 받고 자기 기록을 지운다.

- [ ] **Step 1: Write the failing test**

Create `mobile/src/__tests__/push-registry.test.ts`:

```ts
import { ApiError, type MobileClient } from '@/api/client';
import type { HostInfo, OkResponse } from '@/api/types';
import type { SecretStore } from '@/lib/host-secrets';
import {
  ensurePushHandle,
  findHostByHandle,
  forgetPushHandle,
  readPushHandle,
  relayHttpBase,
  syncHostPush,
  type PushFetch,
} from '@/lib/push/registry';

function memoryStore(): SecretStore & { items: Map<string, string> } {
  const items = new Map<string, string>();
  return {
    items,
    getItemAsync: async (key) => items.get(key) ?? null,
    setItemAsync: async (key, value) => void items.set(key, value),
    deleteItemAsync: async (key) => void items.delete(key),
  };
}

interface Call { url: string; method: string; body?: string; auth?: string }

function relay(answers: Array<{ status: number; body?: unknown }>, calls: Call[]): PushFetch {
  return async (url, init) => {
    const call: Call = { url, method: init.method };
    if (init.body !== undefined) call.body = init.body;
    const auth = init.headers['authorization'];
    if (auth !== undefined) call.auth = auth;
    calls.push(call);
    const next = answers.shift() ?? { status: 500 };
    return { status: next.status, json: async () => next.body };
  };
}

const HANDLE = 'h'.repeat(43);
const SECRET = 's'.repeat(43);

describe('relayHttpBase', () => {
  it('follows the pairing relay scheme', () => {
    expect(relayHttpBase('wss://relay.example.invalid')).toBe('https://relay.example.invalid');
    expect(relayHttpBase('ws://127.0.0.1:8787/ws')).toBe('http://127.0.0.1:8787');
    expect(relayHttpBase('ftp://relay.example.invalid')).toBeNull();
  });
});

describe('push handles', () => {
  it('registers once, then refreshes only when the FCM token changes', async () => {
    const store = memoryStore();
    const calls: Call[] = [];
    const fetch = relay([{ status: 201, body: { handle: HANDLE, secret: SECRET } }, { status: 204 }], calls);
    const first = await ensurePushHandle({ store, fetch }, 'host-a', 'wss://relay.example.invalid', 'token-1');
    expect(first).toEqual({ handle: HANDLE, secret: SECRET, relayUrl: 'wss://relay.example.invalid', fcmToken: 'token-1' });
    expect(calls[0]).toEqual({ url: 'https://relay.example.invalid/push/v1/handles', method: 'POST', body: JSON.stringify({ fcmToken: 'token-1' }) });
    expect(await ensurePushHandle({ store, fetch }, 'host-a', 'wss://relay.example.invalid', 'token-1')).toEqual(first);
    expect(calls).toHaveLength(1);
    await ensurePushHandle({ store, fetch }, 'host-a', 'wss://relay.example.invalid', 'token-2');
    expect(calls[1]).toEqual({ url: `https://relay.example.invalid/push/v1/handles/${HANDLE}`, method: 'PUT', body: JSON.stringify({ fcmToken: 'token-2' }), auth: `Bearer ${SECRET}` });
    expect((await readPushHandle(store, 'host-a'))?.fcmToken).toBe('token-2');
  });

  it('registers again when the relay forgot the handle', async () => {
    const store = memoryStore();
    await store.setItemAsync('mightyclaude.push.v1.host-a', JSON.stringify({ handle: HANDLE, secret: SECRET, relayUrl: 'wss://relay.example.invalid', fcmToken: 'old' }));
    const calls: Call[] = [];
    const other = 'o'.repeat(43);
    const fetch = relay([{ status: 404 }, { status: 201, body: { handle: other, secret: SECRET } }], calls);
    expect((await ensurePushHandle({ store, fetch }, 'host-a', 'wss://relay.example.invalid', 'new'))?.handle).toBe(other);
    expect(calls.map((c) => c.method)).toEqual(['PUT', 'POST']);
  });

  it('forgets a handle on the relay and on the phone', async () => {
    const store = memoryStore();
    await store.setItemAsync('mightyclaude.push.v1.host-a', JSON.stringify({ handle: HANDLE, secret: SECRET, relayUrl: 'wss://relay.example.invalid', fcmToken: 't' }));
    const calls: Call[] = [];
    await forgetPushHandle({ store, fetch: relay([{ status: 204 }], calls) }, 'host-a');
    expect(calls[0]).toEqual({ url: `https://relay.example.invalid/push/v1/handles/${HANDLE}`, method: 'DELETE', auth: `Bearer ${SECRET}` });
    expect(await readPushHandle(store, 'host-a')).toBeNull();
  });

  it('finds the pairing a push came for by its handle', async () => {
    const store = memoryStore();
    await store.setItemAsync('mightyclaude.push.v1.host-b', JSON.stringify({ handle: HANDLE, secret: SECRET, relayUrl: 'wss://r', fcmToken: 't' }));
    expect(await findHostByHandle(store, ['host-a', 'host-b'], HANDLE)).toBe('host-b');
    expect(await findHostByHandle(store, ['host-a', 'host-b'], 'x'.repeat(43))).toBeNull();
  });
});

describe('syncHostPush', () => {
  const info = (capabilities: string[]): HostInfo => ({ protocol: 1, hostId: 'h', hostName: 'H', appVersion: '1', platform: 'darwin', capabilities } as HostInfo);
  const ok: OkResponse = { protocol: 1, ok: true } as OkResponse;

  it('gives the host the handle and the kinds', async () => {
    const store = memoryStore();
    const registered: unknown[] = [];
    const client: Pick<MobileClient, 'info' | 'registerPush'> = {
      info: async () => info(['push']),
      registerPush: async (input) => { registered.push(input); return ok; },
    };
    const fetch = relay([{ status: 201, body: { handle: HANDLE, secret: SECRET } }], []);
    expect(await syncHostPush({ store, fetch, client }, { id: 'host-a', relayUrl: 'wss://relay.example.invalid' }, 'token', ['permission', 'completed'])).toBe('registered');
    expect(registered).toEqual([{ handle: HANDLE, kinds: ['permission', 'completed'] }]);
  });

  it('leaves a host without push alone, and a legacy pairing is unsupported', async () => {
    const store = memoryStore();
    const none: Pick<MobileClient, 'info' | 'registerPush'> = { info: async () => info([]), registerPush: async () => ok };
    expect(await syncHostPush({ store, fetch: relay([], []), client: none }, { id: 'a', relayUrl: 'wss://r' }, 't', ['completed'])).toBe('unsupported');
    const legacy: Pick<MobileClient, 'info' | 'registerPush'> = {
      info: async () => info(['push']),
      registerPush: async () => { throw new ApiError(409, 'needs token'); },
    };
    expect(await syncHostPush({ store, fetch: relay([{ status: 201, body: { handle: HANDLE, secret: SECRET } }], []), client: legacy }, { id: 'a', relayUrl: 'wss://r' }, 't', ['completed'])).toBe('unsupported');
  });
});
```

- [ ] **Step 2: Run it and expect it to fail**

Run: PHONE-RUN with `src/__tests__/push-registry.test.ts`
Expected: FAIL — `Cannot find module '@/lib/push/registry'`.

- [ ] **Step 3: Implement the client routes**

In `mobile/src/api/types.ts`, add `| 'push'` after `| 'structure'` in `Capability`, and add `'push',` after `'structure',` in `CAPABILITIES`.

In `mobile/src/api/client.ts`, add `import type { PushKind } from '@/lib/push/message';` to the imports. Add to the `MobileClient` interface after `answerPlan(...)`:

```ts
  /** Gives this host the relay push handle and the kinds this phone wants ("push" capability). */
  registerPush(input: { handle: string; kinds: PushKind[] }, signal?: AbortSignal): Promise<OkResponse>;
  /** Asks this host to stop pushing to this phone. */
  unregisterPush(signal?: AbortSignal): Promise<OkResponse>;
  /** The session on screen, or null; the host skips pushes about it for 45 s. */
  reportViewing(sessionId: string | null, signal?: AbortSignal): Promise<OkResponse>;
```

Add to the object `createClient` returns, after `answerPlan`:

```ts
    registerPush: (input, signal) => request<OkResponse>('POST', '/m1/push/register', input, signal),
    unregisterPush: (signal) => request<OkResponse>('POST', '/m1/push/unregister', {}, signal),
    reportViewing: (sessionId, signal) => request<OkResponse>('POST', '/m1/push/viewing', { sessionId }, signal),
```

- [ ] **Step 4: Implement the registry**

Create `mobile/src/lib/push/registry.ts`:

```ts
import { ApiError, type MobileClient } from '@/api/client';
import { hasCapability, parseCapabilities } from '@/lib/capabilities';
import type { SecretStore } from '@/lib/host-secrets';
import type { PushKind } from '@/lib/push/message';

/** SecureStore key per pairing (storage id) for its relay push handle. */
export const PUSH_HANDLE_PREFIX = 'mightyclaude.push.v1.';
const HANDLE = /^[A-Za-z0-9_-]{43}$/;

export interface PushHandleRecord {
  handle: string;
  /** Proves this phone to the relay when the token changes or the handle goes; never leaves the phone otherwise. */
  secret: string;
  relayUrl: string;
  fcmToken: string;
}

export type PushFetch = (
  url: string,
  init: { method: string; headers: Record<string, string>; body?: string },
) => Promise<{ status: number; json(): Promise<unknown> }>;

interface Deps {
  store: SecretStore;
  fetch: PushFetch;
}

/** `https://host[:port]` for a pairing's `wss://` relay (`http` for `ws`); null for anything else. */
export function relayHttpBase(relayUrl: string): string | null {
  let url: URL;
  try {
    url = new URL(relayUrl);
  } catch {
    return null;
  }
  const scheme = url.protocol === 'wss:' || url.protocol === 'https:' ? 'https:' : url.protocol === 'ws:' || url.protocol === 'http:' ? 'http:' : null;
  return scheme === null || url.host === '' ? null : `${scheme}//${url.host}`;
}

function parseRecord(text: string | null): PushHandleRecord | null {
  if (text === null) return null;
  try {
    const value = JSON.parse(text) as Partial<PushHandleRecord>;
    if (typeof value.handle !== 'string' || !HANDLE.test(value.handle) || typeof value.secret !== 'string' || !HANDLE.test(value.secret)) return null;
    if (typeof value.relayUrl !== 'string' || typeof value.fcmToken !== 'string') return null;
    return { handle: value.handle, secret: value.secret, relayUrl: value.relayUrl, fcmToken: value.fcmToken };
  } catch {
    return null;
  }
}

export async function readPushHandle(store: SecretStore, storageId: string): Promise<PushHandleRecord | null> {
  return parseRecord(await store.getItemAsync(PUSH_HANDLE_PREFIX + storageId));
}

async function save(store: SecretStore, storageId: string, record: PushHandleRecord): Promise<PushHandleRecord> {
  await store.setItemAsync(PUSH_HANDLE_PREFIX + storageId, JSON.stringify(record));
  return record;
}

async function drop(deps: Deps, record: PushHandleRecord): Promise<void> {
  const base = relayHttpBase(record.relayUrl);
  if (base === null) return;
  try {
    await deps.fetch(`${base}/push/v1/handles/${record.handle}`, { method: 'DELETE', headers: { authorization: `Bearer ${record.secret}` } });
  } catch {
    // The relay forgets idle handles on its own.
  }
}

/** This pairing's handle for `fcmToken`, registering or refreshing it on the relay as needed; null when the relay cannot be reached. */
export async function ensurePushHandle(deps: Deps, storageId: string, relayUrl: string, fcmToken: string): Promise<PushHandleRecord | null> {
  const base = relayHttpBase(relayUrl);
  if (base === null) return null;
  const existing = await readPushHandle(deps.store, storageId);
  if (existing !== null && existing.relayUrl === relayUrl) {
    if (existing.fcmToken === fcmToken) return existing;
    const res = await deps.fetch(`${base}/push/v1/handles/${existing.handle}`, {
      method: 'PUT',
      headers: { 'content-type': 'application/json', authorization: `Bearer ${existing.secret}` },
      body: JSON.stringify({ fcmToken }),
    });
    if (res.status === 204) return save(deps.store, storageId, { ...existing, fcmToken });
    if (res.status !== 404) return null;
  } else if (existing !== null) {
    await drop(deps, existing);
  }
  const res = await deps.fetch(`${base}/push/v1/handles`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ fcmToken }),
  });
  if (res.status !== 201) return null;
  const made = (await res.json()) as { handle?: unknown; secret?: unknown };
  if (typeof made.handle !== 'string' || !HANDLE.test(made.handle) || typeof made.secret !== 'string' || !HANDLE.test(made.secret)) return null;
  return save(deps.store, storageId, { handle: made.handle, secret: made.secret, relayUrl, fcmToken });
}

export async function forgetPushHandle(deps: Deps, storageId: string): Promise<void> {
  const existing = await readPushHandle(deps.store, storageId);
  if (existing !== null) await drop(deps, existing);
  await deps.store.deleteItemAsync(PUSH_HANDLE_PREFIX + storageId);
}

/** Which pairing a push was sent through: the `h` of an FCM message is that pairing's handle. */
export async function findHostByHandle(store: SecretStore, storageIds: readonly string[], handle: string): Promise<string | null> {
  for (const id of storageIds) {
    if ((await readPushHandle(store, id))?.handle === handle) return id;
  }
  return null;
}

export async function syncHostPush(
  deps: Deps & { client: Pick<MobileClient, 'info' | 'registerPush'> },
  host: { id: string; relayUrl: string },
  fcmToken: string,
  kinds: PushKind[],
): Promise<'registered' | 'unsupported' | 'failed'> {
  try {
    if (!hasCapability(parseCapabilities(await deps.client.info()), 'push')) return 'unsupported';
    const record = await ensurePushHandle(deps, host.id, host.relayUrl, fcmToken);
    if (record === null) return 'failed';
    await deps.client.registerPush({ handle: record.handle, kinds });
    return 'registered';
  } catch (error) {
    if (error instanceof ApiError && error.status === 409) return 'unsupported';
    return 'failed';
  }
}
```

In `mobile/src/store/hosts.ts`:
- Add the import `import { forgetPushHandle } from '@/lib/push/registry';`.
- In `removeHost`, right after `closeHostClient(id);`, add:

```ts
    // The relay forgets this pairing's push handle; the host drops it on its next push (404).
    await forgetPushHandle({ store: SecureStore, fetch: (url, init) => fetch(url, init) }, id).catch(() => undefined);
```

- [ ] **Step 5: Run it and expect it to pass**

Run: PHONE-RUN with `src/__tests__/push-registry.test.ts src/__tests__/client.test.ts src/__tests__/capabilities.test.ts`
Expected: all pass; typecheck clean. If `capabilities.test.ts` pins the exact list, add `'push'` to its expectation in the same commit.

- [ ] **Step 6: Commit**

```bash
git add mobile/src/api/types.ts mobile/src/api/client.ts mobile/src/lib/push/registry.ts mobile/src/store/hosts.ts mobile/src/__tests__/push-registry.test.ts
git commit -m "(mobile) the phone gets a push handle from each pairing's relay, keeps it current when its FCM token changes, gives it to hosts that support push, and drops it when the pairing is removed" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

If `capabilities.test.ts` changed, add it to `git add`.

---

## Task 16: 알림의 모양과 버튼의 답

**Files:**
- Create: `mobile/src/lib/push/notification.ts`
- Create: `mobile/src/lib/push/actions.ts`
- Create: `mobile/src/__tests__/push-notification.test.ts`
- Modify: `locales/{ko,en,zh,ja}.json` (`"phone.hosts.help"` 줄 바로 뒤에 `phone.push.*` 키)
- Modify (copies): `native/macos/Sources/MightyCore/Resources/Locales/{ko,en,zh,ja}.json`, `mobile/src/locales/{ko,en,zh,ja}.json`
- Modify: `docs/i18n.md` (재생성)

**Interfaces:**
- Consumes: Task 13 `PushMessage`, `PushKind`, `PushButtons`. `t()`. 공유 키 `permission.allow`, `permission.deny`, `permission.allowOnce`, `antigravity.permission.allowAlways`, `antigravity.permission.deny`. `ApiError`.
- Produces:
  - `CHANNEL_ATTENTION = 'attention'`, `CHANNEL_WORK = 'work'`, `channelName(id: string): string`
  - `type PushAction = 'allow' | 'deny' | 'once' | 'always'`
  - `interface PushNotificationData { storageId: string; sessionId: string; kind: PushKind; requestId?: string; runId?: string; buttons?: PushButtons }`
  - `interface PushNotificationSpec { id: string; title: string; body: string; subText: string; channelId: string; actions: Array<{ id: PushAction; title: string }>; data: Record<string, string> }`
  - `notificationFor(message: PushMessage, storageId: string, handle: string): PushNotificationSpec`
  - `actionsFor(buttons: PushButtons | undefined): Array<{ id: PushAction; title: string }>`
  - `notificationData(data: Record<string, unknown> | undefined): PushNotificationData | null`
  - `sessionPath(data: PushNotificationData): string` (`/host/<storageId>/session/<sessionId>`)
  - `type PushOutcome = 'allowed' | 'denied' | 'already' | 'locked' | 'failed'`
  - `outcomeUpdate(outcome: PushOutcome): { body: string; keepActions: boolean; opensApp: boolean }`
  - `interface PermissionBody { requestId: string; runId: string; allow: boolean; always?: boolean }`
  - `answerBody(data: PushNotificationData, action: PushAction): PermissionBody | null`
  - `interface PushActionDeps { isDeviceLocked(): boolean; respond(storageId: string, sessionId: string, body: PermissionBody): Promise<void>; stillPending(storageId: string, sessionId: string, requestId: string): Promise<boolean> }`
  - `answerFromNotification(deps: PushActionDeps, data: PushNotificationData, action: PushAction): Promise<PushOutcome>`

**규칙.**
- **채널.** 권한·질문·계획은 `attention`(높은 중요도), 완료·실패·로그인은 `work`다.
- **알림 id.** 요청 알림은 `<handle>:<메시지 id>`다. 상태 알림(완료·실패·로그인)은 세션마다 한 자리 `<handle>:<sessionId>:status`라서, 실패 뒤에 온 로그인 만료가 그 자리를 바꾼다.
- **글.** 제목은 `phone.push.title.<kind>`(`{title}` = 창 제목)다. 본문은 요약이고, 요약이 비면 `phone.push.body.<kind>`다. 작은 글은 `<호스트> · <워크스페이스>`다.
- **버튼.**
  - `claude`: 허용 `allow`, 거절 `deny`.
  - `antigravity`: 이번만 허용 `once`, 항상 허용 `always`, 거절 `deny`.
  - `antigravityOnce`: 이번만 허용 `once`, 거절 `deny`.
  - `none`과 질문·계획: 버튼 없음(누르면 그 세션이 열린다).
- **답.** 버튼이 그 알림의 버튼이 아니면 `failed`다. 잠겨 있으면 보내지 않고 `locked`(버튼을 남긴다)다. 성공하면 `allowed`/`denied`다.
  - 409를 받으면 호스트에 그 요청이 아직 있는지 다시 본다. 없으면 `already`("이미 처리됨")다. 있으면 `failed`다. 409인데 요청이 남아 있으면, 예를 들어 설정 파일을 고칠 수 없는 Antigravity 카드처럼 그 이유를 앱에서 봐야 한다.
  - 그 밖의 실패와 시간 초과도 `failed`다. 알림은 "응답하지 못했습니다. 눌러서 여세요."로 바뀌고, 누르면 그 세션이 열린다.

- [ ] **Step 1: Write the failing test**

Create `mobile/src/__tests__/push-notification.test.ts`:

```ts
import { ApiError } from '@/api/client';
import { resetLanguage, t } from '@/lib/i18n';
import { answerFromNotification, type PushActionDeps } from '@/lib/push/actions';
import type { PushMessage } from '@/lib/push/message';
import {
  actionsFor,
  CHANNEL_ATTENTION,
  CHANNEL_WORK,
  notificationData,
  notificationFor,
  outcomeUpdate,
  sessionPath,
  type PushNotificationData,
} from '@/lib/push/notification';

beforeAll(() => resetLanguage('ko'));

const HANDLE = 'h'.repeat(43);
const message = (overrides: Partial<PushMessage>): PushMessage => ({
  v: 1, id: 'request:run-1:req-1', kind: 'permission', hostId: 'host-1', hostName: 'Test Mac', workspace: 'demo',
  title: 'Claude 1', summary: 'git status', sessionId: 'pane-1', at: '2026-10-23T01:02:03Z',
  permission: { requestId: 'req-1', runId: 'run-1', buttons: 'claude' }, ...overrides,
});

describe('notificationFor', () => {
  it('draws a permission request with allow and deny on the attention channel', () => {
    const spec = notificationFor(message({}), 'host-a', HANDLE);
    expect(spec.id).toBe(`${HANDLE}:request:run-1:req-1`);
    expect(spec.channelId).toBe(CHANNEL_ATTENTION);
    expect(spec.title).toBe(t('phone.push.title.permission', { title: 'Claude 1' }));
    expect(spec.body).toBe('git status');
    expect(spec.subText).toBe('Test Mac · demo');
    expect(spec.actions).toEqual([{ id: 'allow', title: t('permission.allow') }, { id: 'deny', title: t('permission.deny') }]);
    expect(notificationData(spec.data)).toEqual({ storageId: 'host-a', sessionId: 'pane-1', kind: 'permission', requestId: 'req-1', runId: 'run-1', buttons: 'claude' });
  });

  it('gives finished, failed and sign-in one status slot per session, with a fallback text', () => {
    const done = notificationFor(message({ id: 'run:pane-1:1:completed', kind: 'completed', summary: '' }), 'host-a', HANDLE);
    const lost = notificationFor(message({ id: 'signin:pane-1:1', kind: 'signin', summary: '' }), 'host-a', HANDLE);
    expect(done.id).toBe(`${HANDLE}:pane-1:status`);
    expect(lost.id).toBe(done.id);
    expect(done.channelId).toBe(CHANNEL_WORK);
    expect(done.body).toBe(t('phone.push.body.completed'));
    expect(done.actions).toEqual([]);
  });

  it('offers Antigravity choices the card allows and none for a question', () => {
    expect(actionsFor('antigravity').map((a) => a.id)).toEqual(['once', 'always', 'deny']);
    expect(actionsFor('antigravityOnce').map((a) => a.id)).toEqual(['once', 'deny']);
    expect(actionsFor('antigravity')[1]?.title).toBe(t('antigravity.permission.allowAlways'));
    expect(actionsFor('none')).toEqual([]);
    expect(notificationFor(message({ kind: 'question', permission: undefined }), 'host-a', HANDLE).actions).toEqual([]);
  });

  it('opens the session the notification is about', () => {
    expect(sessionPath({ storageId: 'host a', sessionId: 'pane/1', kind: 'question' })).toBe('/host/host%20a/session/pane%2F1');
    expect(notificationData({ storageId: 'a' })).toBeNull();
  });

  it('says what happened after a button', () => {
    expect(outcomeUpdate('allowed')).toEqual({ body: t('phone.push.outcome.allowed'), keepActions: false, opensApp: false });
    expect(outcomeUpdate('already')).toEqual({ body: t('phone.push.outcome.already'), keepActions: false, opensApp: false });
    expect(outcomeUpdate('locked')).toEqual({ body: t('phone.push.outcome.locked'), keepActions: true, opensApp: false });
    expect(outcomeUpdate('failed')).toEqual({ body: t('phone.push.outcome.failed'), keepActions: false, opensApp: true });
  });
});

describe('answerFromNotification', () => {
  const claude: PushNotificationData = { storageId: 'host-a', sessionId: 'pane-1', kind: 'permission', requestId: 'req-1', runId: 'run-1', buttons: 'claude' };
  const agy: PushNotificationData = { ...claude, requestId: 'agy-1', buttons: 'antigravity' };

  function deps(overrides: Partial<PushActionDeps> = {}): PushActionDeps & { sent: unknown[] } {
    const sent: unknown[] = [];
    return {
      sent,
      isDeviceLocked: () => false,
      respond: async (_storage, _session, body) => void sent.push(body),
      stillPending: async () => false,
      ...overrides,
    };
  }

  it('answers with the existing permission route', async () => {
    const d = deps();
    expect(await answerFromNotification(d, claude, 'allow')).toBe('allowed');
    expect(await answerFromNotification(d, claude, 'deny')).toBe('denied');
    expect(await answerFromNotification(d, agy, 'always')).toBe('allowed');
    expect(d.sent).toEqual([
      { requestId: 'req-1', runId: 'run-1', allow: true },
      { requestId: 'req-1', runId: 'run-1', allow: false },
      { requestId: 'agy-1', runId: 'run-1', allow: true, always: true },
    ]);
  });

  it('does nothing while the phone is locked', async () => {
    const d = deps({ isDeviceLocked: () => true });
    expect(await answerFromNotification(d, claude, 'allow')).toBe('locked');
    expect(d.sent).toEqual([]);
  });

  it('refuses a button the notification did not offer', async () => {
    const d = deps();
    expect(await answerFromNotification(d, { ...agy, buttons: 'antigravityOnce' }, 'always')).toBe('failed');
    expect(await answerFromNotification(d, claude, 'once')).toBe('failed');
    expect(await answerFromNotification(d, { ...claude, kind: 'question' }, 'allow')).toBe('failed');
    expect(d.sent).toEqual([]);
  });

  it('calls a 409 already handled only when the request is gone', async () => {
    const conflict = async (): Promise<void> => { throw new ApiError(409, 'not pending'); };
    expect(await answerFromNotification(deps({ respond: conflict, stillPending: async () => false }), claude, 'allow')).toBe('already');
    expect(await answerFromNotification(deps({ respond: conflict, stillPending: async () => true }), claude, 'allow')).toBe('failed');
    expect(await answerFromNotification(deps({ respond: conflict, stillPending: async () => { throw new Error('offline'); } }), claude, 'allow')).toBe('failed');
    expect(await answerFromNotification(deps({ respond: async () => { throw new Error('timeout'); } }), claude, 'allow')).toBe('failed');
  });
});
```

- [ ] **Step 2: Run it and expect it to fail**

Run: PHONE-RUN with `src/__tests__/push-notification.test.ts`
Expected: FAIL — `Cannot find module '@/lib/push/notification'`.

- [ ] **Step 3: Locale keys**

Insert after the `"phone.hosts.help"` line in each file.

`locales/ko.json`:
```json
  "phone.push.channel.attention": "답이 필요한 일",
  "phone.push.channel.work": "끝난 작업",
  "phone.push.title.completed": "완료: {title}",
  "phone.push.title.failed": "실패: {title}",
  "phone.push.title.permission": "허용 요청: {title}",
  "phone.push.title.question": "질문: {title}",
  "phone.push.title.plan": "계획 승인: {title}",
  "phone.push.title.signin": "로그인 필요: {title}",
  "phone.push.body.completed": "작업을 마쳤습니다.",
  "phone.push.body.failed": "작업이 실패했습니다.",
  "phone.push.body.permission": "허용할지 골라 주세요.",
  "phone.push.body.question": "답을 기다리고 있습니다.",
  "phone.push.body.plan": "계획을 확인해 주세요.",
  "phone.push.body.signin": "컴퓨터에서 다시 로그인해야 합니다.",
  "phone.push.outcome.allowed": "허용함",
  "phone.push.outcome.denied": "거절함",
  "phone.push.outcome.already": "이미 처리됨",
  "phone.push.outcome.locked": "잠금을 푼 뒤 다시 누르세요.",
  "phone.push.outcome.failed": "응답하지 못했습니다. 눌러서 여세요.",
```
`locales/en.json`:
```json
  "phone.push.channel.attention": "Needs you",
  "phone.push.channel.work": "Finished work",
  "phone.push.title.completed": "Done: {title}",
  "phone.push.title.failed": "Failed: {title}",
  "phone.push.title.permission": "Permission: {title}",
  "phone.push.title.question": "Question: {title}",
  "phone.push.title.plan": "Plan to approve: {title}",
  "phone.push.title.signin": "Sign-in needed: {title}",
  "phone.push.body.completed": "The run finished.",
  "phone.push.body.failed": "The run failed.",
  "phone.push.body.permission": "Choose whether to allow it.",
  "phone.push.body.question": "It's waiting for your answer.",
  "phone.push.body.plan": "Review the plan.",
  "phone.push.body.signin": "Sign in again on the computer.",
  "phone.push.outcome.allowed": "Allowed",
  "phone.push.outcome.denied": "Denied",
  "phone.push.outcome.already": "Already handled",
  "phone.push.outcome.locked": "Unlock your phone, then tap again.",
  "phone.push.outcome.failed": "Couldn't answer. Tap to open.",
```
`locales/zh.json`:
```json
  "phone.push.channel.attention": "需要你处理",
  "phone.push.channel.work": "已完成的工作",
  "phone.push.title.completed": "完成：{title}",
  "phone.push.title.failed": "失败：{title}",
  "phone.push.title.permission": "权限请求：{title}",
  "phone.push.title.question": "问题：{title}",
  "phone.push.title.plan": "待批准的计划：{title}",
  "phone.push.title.signin": "需要登录：{title}",
  "phone.push.body.completed": "运行已完成。",
  "phone.push.body.failed": "运行失败。",
  "phone.push.body.permission": "请选择是否允许。",
  "phone.push.body.question": "正在等待你的回答。",
  "phone.push.body.plan": "请查看计划。",
  "phone.push.body.signin": "需要在电脑上重新登录。",
  "phone.push.outcome.allowed": "已允许",
  "phone.push.outcome.denied": "已拒绝",
  "phone.push.outcome.already": "已处理",
  "phone.push.outcome.locked": "请解锁手机后再点一次。",
  "phone.push.outcome.failed": "无法回复。点按以打开。",
```
`locales/ja.json`:
```json
  "phone.push.channel.attention": "対応が必要",
  "phone.push.channel.work": "終わった作業",
  "phone.push.title.completed": "完了: {title}",
  "phone.push.title.failed": "失敗: {title}",
  "phone.push.title.permission": "許可の依頼: {title}",
  "phone.push.title.question": "質問: {title}",
  "phone.push.title.plan": "計画の承認: {title}",
  "phone.push.title.signin": "ログインが必要: {title}",
  "phone.push.body.completed": "実行が終わりました。",
  "phone.push.body.failed": "実行に失敗しました。",
  "phone.push.body.permission": "許可するか選んでください。",
  "phone.push.body.question": "回答を待っています。",
  "phone.push.body.plan": "計画を確認してください。",
  "phone.push.body.signin": "コンピューターでもう一度ログインしてください。",
  "phone.push.outcome.allowed": "許可しました",
  "phone.push.outcome.denied": "拒否しました",
  "phone.push.outcome.already": "処理済み",
  "phone.push.outcome.locked": "ロックを解除してからもう一度タップしてください。",
  "phone.push.outcome.failed": "応答できませんでした。タップして開きます。",
```

Then copy:

```bash
for lang in ko en zh ja; do cp locales/$lang.json native/macos/Sources/MightyCore/Resources/Locales/$lang.json; cp locales/$lang.json mobile/src/locales/$lang.json; done
```

- [ ] **Step 4: Implement**

Create `mobile/src/lib/push/notification.ts`:

```ts
import { t } from '@/lib/i18n';
import { PUSH_BUTTONS, PUSH_KINDS, type PushButtons, type PushKind, type PushMessage } from '@/lib/push/message';

export const CHANNEL_ATTENTION = 'attention';
export const CHANNEL_WORK = 'work';

const ATTENTION: readonly PushKind[] = ['permission', 'question', 'plan'];

// Literal keys, so the locale checker sees every one in use.
const TITLE: Record<PushKind, string> = {
  completed: 'phone.push.title.completed',
  failed: 'phone.push.title.failed',
  permission: 'phone.push.title.permission',
  question: 'phone.push.title.question',
  plan: 'phone.push.title.plan',
  signin: 'phone.push.title.signin',
};
const BODY: Record<PushKind, string> = {
  completed: 'phone.push.body.completed',
  failed: 'phone.push.body.failed',
  permission: 'phone.push.body.permission',
  question: 'phone.push.body.question',
  plan: 'phone.push.body.plan',
  signin: 'phone.push.body.signin',
};

export type PushAction = 'allow' | 'deny' | 'once' | 'always';

/** What a notification carries for its tap and its buttons (all strings: Android extras). */
export interface PushNotificationData {
  storageId: string;
  sessionId: string;
  kind: PushKind;
  requestId?: string;
  runId?: string;
  buttons?: PushButtons;
}

export interface PushNotificationSpec {
  id: string;
  title: string;
  body: string;
  subText: string;
  channelId: string;
  actions: Array<{ id: PushAction; title: string }>;
  data: Record<string, string>;
}

export function channelName(id: string): string {
  return id === CHANNEL_ATTENTION ? t('phone.push.channel.attention') : t('phone.push.channel.work');
}

export function actionsFor(buttons: PushButtons | undefined): Array<{ id: PushAction; title: string }> {
  switch (buttons) {
    case 'claude':
      return [{ id: 'allow', title: t('permission.allow') }, { id: 'deny', title: t('permission.deny') }];
    case 'antigravity':
      return [
        { id: 'once', title: t('permission.allowOnce') },
        { id: 'always', title: t('antigravity.permission.allowAlways') },
        { id: 'deny', title: t('antigravity.permission.deny') },
      ];
    case 'antigravityOnce':
      return [{ id: 'once', title: t('permission.allowOnce') }, { id: 'deny', title: t('antigravity.permission.deny') }];
    default:
      return [];
  }
}

export function notificationFor(message: PushMessage, storageId: string, handle: string): PushNotificationSpec {
  const attention = ATTENTION.includes(message.kind);
  const data: Record<string, string> = { storageId, sessionId: message.sessionId, kind: message.kind };
  if (message.permission) {
    data['requestId'] = message.permission.requestId;
    data['runId'] = message.permission.runId;
    data['buttons'] = message.permission.buttons;
  }
  return {
    id: attention ? `${handle}:${message.id}` : `${handle}:${message.sessionId}:status`,
    title: t(TITLE[message.kind], { title: message.title }),
    body: message.summary !== '' ? message.summary : t(BODY[message.kind]),
    subText: message.workspace !== '' ? `${message.hostName} · ${message.workspace}` : message.hostName,
    channelId: attention ? CHANNEL_ATTENTION : CHANNEL_WORK,
    actions: message.kind === 'permission' ? actionsFor(message.permission?.buttons) : [],
    data,
  };
}

export function notificationData(data: Record<string, unknown> | undefined): PushNotificationData | null {
  if (!data) return null;
  const { storageId, sessionId, kind, requestId, runId, buttons } = data;
  if (typeof storageId !== 'string' || typeof sessionId !== 'string' || !PUSH_KINDS.includes(kind as PushKind)) return null;
  const parsed: PushNotificationData = { storageId, sessionId, kind: kind as PushKind };
  if (typeof requestId === 'string') parsed.requestId = requestId;
  if (typeof runId === 'string') parsed.runId = runId;
  if (PUSH_BUTTONS.includes(buttons as PushButtons)) parsed.buttons = buttons as PushButtons;
  return parsed;
}

export function sessionPath(data: PushNotificationData): string {
  return `/host/${encodeURIComponent(data.storageId)}/session/${encodeURIComponent(data.sessionId)}`;
}

export type PushOutcome = 'allowed' | 'denied' | 'already' | 'locked' | 'failed';

const OUTCOME: Record<PushOutcome, string> = {
  allowed: 'phone.push.outcome.allowed',
  denied: 'phone.push.outcome.denied',
  already: 'phone.push.outcome.already',
  locked: 'phone.push.outcome.locked',
  failed: 'phone.push.outcome.failed',
};

export function outcomeUpdate(outcome: PushOutcome): { body: string; keepActions: boolean; opensApp: boolean } {
  return { body: t(OUTCOME[outcome]), keepActions: outcome === 'locked', opensApp: outcome === 'failed' };
}
```

Create `mobile/src/lib/push/actions.ts`:

```ts
import { ApiError } from '@/api/client';
import type { PushAction, PushNotificationData, PushOutcome } from '@/lib/push/notification';

/** `POST /m1/sessions/{id}/permission`; `always` only for an Antigravity card. */
export interface PermissionBody {
  requestId: string;
  runId: string;
  allow: boolean;
  always?: boolean;
}

const OFFERED: Record<string, readonly PushAction[]> = {
  claude: ['allow', 'deny'],
  antigravity: ['once', 'always', 'deny'],
  antigravityOnce: ['once', 'deny'],
};

/** The answer a button stands for, or null when the notification never offered it. */
export function answerBody(data: PushNotificationData, action: PushAction): PermissionBody | null {
  if (data.kind !== 'permission' || !data.requestId || !data.runId || !data.buttons) return null;
  if (!(OFFERED[data.buttons] ?? []).includes(action)) return null;
  if (action === 'deny') return { requestId: data.requestId, runId: data.runId, allow: false };
  if (action === 'always') return { requestId: data.requestId, runId: data.runId, allow: true, always: true };
  return { requestId: data.requestId, runId: data.runId, allow: true };
}

export interface PushActionDeps {
  isDeviceLocked(): boolean;
  respond(storageId: string, sessionId: string, body: PermissionBody): Promise<void>;
  stillPending(storageId: string, sessionId: string, requestId: string): Promise<boolean>;
}

/** Answers from a notification button; the host still decides (the same checks as the app's own card). */
export async function answerFromNotification(deps: PushActionDeps, data: PushNotificationData, action: PushAction): Promise<PushOutcome> {
  const body = answerBody(data, action);
  if (body === null) return 'failed';
  if (deps.isDeviceLocked()) return 'locked';
  try {
    await deps.respond(data.storageId, data.sessionId, body);
    return body.allow ? 'allowed' : 'denied';
  } catch (error) {
    if (!(error instanceof ApiError) || error.status !== 409) return 'failed';
    try {
      return (await deps.stillPending(data.storageId, data.sessionId, body.requestId)) ? 'failed' : 'already';
    } catch {
      return 'failed';
    }
  }
}
```

- [ ] **Step 5: Run it and expect it to pass**

Run: PHONE-RUN with `src/__tests__/push-notification.test.ts src/__tests__/i18n.test.ts`, then LOCALES
Expected: tests pass; typecheck clean; the locale checks print no error (every new key is a literal string in `notification.ts`, which the unused-key check counts).

- [ ] **Step 6: Commit**

```bash
git add mobile/src/lib/push/notification.ts mobile/src/lib/push/actions.ts mobile/src/__tests__/push-notification.test.ts locales/ko.json locales/en.json locales/zh.json locales/ja.json native/macos/Sources/MightyCore/Resources/Locales/ko.json native/macos/Sources/MightyCore/Resources/Locales/en.json native/macos/Sources/MightyCore/Resources/Locales/zh.json native/macos/Sources/MightyCore/Resources/Locales/ja.json mobile/src/locales/ko.json mobile/src/locales/en.json mobile/src/locales/zh.json mobile/src/locales/ja.json docs/i18n.md
git commit -m "(mobile) a push shows as a notification in four languages with the buttons its card allows; a button answers only on an unlocked phone and says allowed, denied, already handled, or tap to open" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## Task 17: 알림 설정과 "보고 있음" 보고

**Files:**
- Create: `mobile/src/lib/push/settings.ts`
- Create: `mobile/src/lib/push/viewing.ts`
- Create: `mobile/src/__tests__/push-settings.test.ts`
- Create: `mobile/app/settings.tsx`
- Modify: `mobile/app/_layout.tsx` (`Stack.Screen name="settings"`)
- Modify: `mobile/app/(tabs)/hosts.tsx` (목록 바닥에 설정 버튼)
- Modify: `mobile/src/__tests__/routes.test.ts` (`'/settings'`)
- Modify: `locales/{ko,en,zh,ja}.json` (`phone.push.outcome.failed` 뒤에 `phone.settings.*`), 사본 둘, `docs/i18n.md`

**Interfaces:**
- Consumes: `SecretStore`, `expo-secure-store`, `PUSH_KINDS`, `PushKind`, UI `Button`·`ScreenTitle`·`Card` (`@/components/ui`), `spacing`·`typeScale`·`usePalette` (`@/theme`).
- Produces:
  - `PUSH_SETTINGS_KEY = 'mightyclaude.push.settings.v1'`
  - `interface PushSettings { kinds: Record<PushKind, boolean>; crashReports: boolean }`, `DEFAULT_PUSH_SETTINGS`
  - `parsePushSettings(text: string | null): PushSettings`, `enabledKinds(settings: PushSettings): PushKind[]`
  - `createPushSettings(store: SecretStore): { get(): PushSettings; set(next: PushSettings): Promise<void>; subscribe(listener: () => void): () => void; ready: Promise<void> }`
  - `pushSettings` (SecureStore에 묶인 싱글턴. `settings.ts`가 export한다)
  - `VIEWING_REFRESH_MS = 30_000`
  - `createViewingReporter(deps: { send(storageId: string, sessionId: string | null): Promise<void>; setInterval(run: () => void, ms: number): unknown; clearInterval(handle: unknown): void }): { enter(storageId: string, sessionId: string): void; leave(): void; setActive(active: boolean): void }`
  - 화면 `/settings`: 종류별 알림 스위치 6개, 충돌 보고 스위치, 휴대폰의 알림이 꺼졌을 때 "휴대폰 설정 열기"

- [ ] **Step 1: Write the failing test**

Create `mobile/src/__tests__/push-settings.test.ts`:

```ts
import type { SecretStore } from '@/lib/host-secrets';
import {
  createPushSettings,
  DEFAULT_PUSH_SETTINGS,
  enabledKinds,
  parsePushSettings,
  PUSH_SETTINGS_KEY,
} from '@/lib/push/settings';
import { createViewingReporter, VIEWING_REFRESH_MS } from '@/lib/push/viewing';

function memoryStore(): SecretStore & { items: Map<string, string> } {
  const items = new Map<string, string>();
  return {
    items,
    getItemAsync: async (key) => items.get(key) ?? null,
    setItemAsync: async (key, value) => void items.set(key, value),
    deleteItemAsync: async (key) => void items.delete(key),
  };
}

describe('push settings', () => {
  it('turns everything on by default and reads a partial file', () => {
    expect(parsePushSettings(null)).toEqual(DEFAULT_PUSH_SETTINGS);
    expect(enabledKinds(DEFAULT_PUSH_SETTINGS)).toEqual(['completed', 'failed', 'permission', 'question', 'plan', 'signin']);
    const partial = parsePushSettings(JSON.stringify({ kinds: { completed: false }, crashReports: false }));
    expect(partial.kinds.completed).toBe(false);
    expect(partial.kinds.permission).toBe(true);
    expect(partial.crashReports).toBe(false);
    expect(parsePushSettings('{broken')).toEqual(DEFAULT_PUSH_SETTINGS);
  });

  it('keeps a change and tells listeners', async () => {
    const store = memoryStore();
    const settings = createPushSettings(store);
    await settings.ready;
    let told = 0;
    settings.subscribe(() => { told += 1; });
    await settings.set({ ...settings.get(), crashReports: false });
    expect(told).toBe(1);
    expect(JSON.parse(store.items.get(PUSH_SETTINGS_KEY)!).crashReports).toBe(false);
    const again = createPushSettings(store);
    await again.ready;
    expect(again.get().crashReports).toBe(false);
  });
});

describe('viewing reporter', () => {
  function harness() {
    const sent: Array<[string, string | null]> = [];
    let timer: (() => void) | null = null;
    const reporter = createViewingReporter({
      send: async (storageId, sessionId) => void sent.push([storageId, sessionId]),
      setInterval: (run, ms) => { expect(ms).toBe(VIEWING_REFRESH_MS); timer = run; return 1; },
      clearInterval: () => { timer = null; },
    });
    return { sent, reporter, tick: () => timer?.(), running: () => timer !== null };
  }

  it('says which session is on screen, repeats it, and clears it on leaving', () => {
    const h = harness();
    h.reporter.enter('host-a', 'pane-1');
    h.tick();
    h.reporter.leave();
    expect(h.sent).toEqual([['host-a', 'pane-1'], ['host-a', 'pane-1'], ['host-a', null]]);
    expect(h.running()).toBe(false);
  });

  it('clears the view while the app is in the background and says it again on return', () => {
    const h = harness();
    h.reporter.enter('host-a', 'pane-1');
    h.reporter.setActive(false);
    expect(h.running()).toBe(false);
    h.reporter.setActive(true);
    expect(h.sent).toEqual([['host-a', 'pane-1'], ['host-a', null], ['host-a', 'pane-1']]);
  });

  it('tells the old host it is no longer viewed when another host opens', () => {
    const h = harness();
    h.reporter.enter('host-a', 'pane-1');
    h.reporter.enter('host-b', 'pane-9');
    expect(h.sent).toEqual([['host-a', 'pane-1'], ['host-a', null], ['host-b', 'pane-9']]);
  });
});
```

- [ ] **Step 2: Run it and expect it to fail**

Run: PHONE-RUN with `src/__tests__/push-settings.test.ts`
Expected: FAIL — `Cannot find module '@/lib/push/settings'`.

- [ ] **Step 3: Implement the stores**

Create `mobile/src/lib/push/settings.ts`:

```ts
import * as SecureStore from 'expo-secure-store';

import type { SecretStore } from '@/lib/host-secrets';
import { PUSH_KINDS, type PushKind } from '@/lib/push/message';

export const PUSH_SETTINGS_KEY = 'mightyclaude.push.settings.v1';

export interface PushSettings {
  kinds: Record<PushKind, boolean>;
  /** Crashlytics collection; on by default, never with user content (crash.ts). */
  crashReports: boolean;
}

export const DEFAULT_PUSH_SETTINGS: PushSettings = {
  kinds: { completed: true, failed: true, permission: true, question: true, plan: true, signin: true },
  crashReports: true,
};

export function parsePushSettings(text: string | null): PushSettings {
  if (text === null) return DEFAULT_PUSH_SETTINGS;
  try {
    const value = JSON.parse(text) as { kinds?: Partial<Record<string, unknown>>; crashReports?: unknown };
    const kinds = { ...DEFAULT_PUSH_SETTINGS.kinds };
    for (const kind of PUSH_KINDS) {
      if (typeof value.kinds?.[kind] === 'boolean') kinds[kind] = value.kinds[kind] as boolean;
    }
    return { kinds, crashReports: typeof value.crashReports === 'boolean' ? value.crashReports : true };
  } catch {
    return DEFAULT_PUSH_SETTINGS;
  }
}

export function enabledKinds(settings: PushSettings): PushKind[] {
  return PUSH_KINDS.filter((kind) => settings.kinds[kind]);
}

export function createPushSettings(store: SecretStore) {
  let current = DEFAULT_PUSH_SETTINGS;
  const listeners = new Set<() => void>();
  const ready = store
    .getItemAsync(PUSH_SETTINGS_KEY)
    .then((text) => {
      current = parsePushSettings(text);
    })
    .catch(() => undefined);
  return {
    get: (): PushSettings => current,
    ready,
    subscribe(listener: () => void): () => void {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    async set(next: PushSettings): Promise<void> {
      current = next;
      for (const listener of listeners) listener();
      await store.setItemAsync(PUSH_SETTINGS_KEY, JSON.stringify(next)).catch(() => undefined);
    },
  };
}

export const pushSettings = createPushSettings(SecureStore);
```

Create `mobile/src/lib/push/viewing.ts`:

```ts
/** How often the phone repeats "this session is on screen"; the host holds it for 45 s. */
export const VIEWING_REFRESH_MS = 30_000;

export interface ViewingDeps {
  send(storageId: string, sessionId: string | null): Promise<void>;
  setInterval(run: () => void, ms: number): unknown;
  clearInterval(handle: unknown): void;
}

/**
 * Tells a host which of its sessions this phone shows, so it does not push about it
 * (docs/mobile-remote.md "푸시 알림"). Only while the app is in front; best effort.
 */
export function createViewingReporter(deps: ViewingDeps) {
  let current: { storageId: string; sessionId: string } | null = null;
  let active = true;
  let timer: unknown = null;

  const say = (storageId: string, sessionId: string | null): void => {
    void deps.send(storageId, sessionId).catch(() => undefined);
  };
  const stop = (): void => {
    if (timer !== null) deps.clearInterval(timer);
    timer = null;
  };
  const start = (): void => {
    stop();
    if (current === null || !active) return;
    const { storageId, sessionId } = current;
    say(storageId, sessionId);
    timer = deps.setInterval(() => say(storageId, sessionId), VIEWING_REFRESH_MS);
  };

  return {
    enter(storageId: string, sessionId: string): void {
      if (current !== null && current.storageId !== storageId && active) say(current.storageId, null);
      current = { storageId, sessionId };
      start();
    },
    leave(): void {
      stop();
      if (current !== null && active) say(current.storageId, null);
      current = null;
    },
    setActive(next: boolean): void {
      if (active === next) return;
      active = next;
      if (current === null) return;
      if (next) start();
      else {
        stop();
        say(current.storageId, null);
      }
    },
  };
}
```

- [ ] **Step 4: Locale keys**

Insert after the `"phone.push.outcome.failed"` line.

`locales/ko.json`:
```json
  "phone.settings.title": "설정",
  "phone.settings.notifications": "알림",
  "phone.settings.notificationsHint": "알림은 연결된 컴퓨터가 릴레이를 거쳐 보냅니다. 내용은 이 휴대폰만 열 수 있습니다.",
  "phone.settings.kind.completed": "작업 완료",
  "phone.settings.kind.failed": "작업 실패",
  "phone.settings.kind.permission": "권한 요청",
  "phone.settings.kind.question": "질문",
  "phone.settings.kind.plan": "계획 승인",
  "phone.settings.kind.signin": "로그인 만료",
  "phone.settings.permissionOff": "휴대폰 설정에서 이 앱의 알림이 꺼져 있습니다.",
  "phone.settings.openSystem": "휴대폰 설정 열기",
  "phone.settings.crashReports": "충돌 보고 보내기",
  "phone.settings.crashReportsHint": "앱이 멈추면 멈춘 곳만 보냅니다. 대화·명령·경로는 보내지 않습니다.",
```
`locales/en.json`:
```json
  "phone.settings.title": "Settings",
  "phone.settings.notifications": "Notifications",
  "phone.settings.notificationsHint": "Your computer sends notifications through the relay. Only this phone can read them.",
  "phone.settings.kind.completed": "Run finished",
  "phone.settings.kind.failed": "Run failed",
  "phone.settings.kind.permission": "Permission requests",
  "phone.settings.kind.question": "Questions",
  "phone.settings.kind.plan": "Plans to approve",
  "phone.settings.kind.signin": "Sign-in expired",
  "phone.settings.permissionOff": "Notifications for this app are off in the phone's settings.",
  "phone.settings.openSystem": "Open phone settings",
  "phone.settings.crashReports": "Send crash reports",
  "phone.settings.crashReportsHint": "Only where the app stopped is sent, never conversations, commands or paths.",
```
`locales/zh.json`:
```json
  "phone.settings.title": "设置",
  "phone.settings.notifications": "通知",
  "phone.settings.notificationsHint": "通知由已连接的电脑经中继发送。只有这部手机能读取内容。",
  "phone.settings.kind.completed": "运行完成",
  "phone.settings.kind.failed": "运行失败",
  "phone.settings.kind.permission": "权限请求",
  "phone.settings.kind.question": "问题",
  "phone.settings.kind.plan": "待批准的计划",
  "phone.settings.kind.signin": "登录过期",
  "phone.settings.permissionOff": "手机设置中已关闭此应用的通知。",
  "phone.settings.openSystem": "打开手机设置",
  "phone.settings.crashReports": "发送崩溃报告",
  "phone.settings.crashReportsHint": "应用崩溃时只发送出错位置，不发送对话、命令或路径。",
```
`locales/ja.json`:
```json
  "phone.settings.title": "設定",
  "phone.settings.notifications": "通知",
  "phone.settings.notificationsHint": "通知は接続したコンピューターがリレー経由で送ります。内容はこのスマートフォンだけが読めます。",
  "phone.settings.kind.completed": "実行完了",
  "phone.settings.kind.failed": "実行失敗",
  "phone.settings.kind.permission": "許可の依頼",
  "phone.settings.kind.question": "質問",
  "phone.settings.kind.plan": "計画の承認",
  "phone.settings.kind.signin": "ログイン切れ",
  "phone.settings.permissionOff": "スマートフォンの設定でこのアプリの通知がオフになっています。",
  "phone.settings.openSystem": "スマートフォンの設定を開く",
  "phone.settings.crashReports": "クラッシュレポートを送信",
  "phone.settings.crashReportsHint": "アプリが止まった場所だけを送ります。会話・コマンド・パスは送りません。",
```

Then copy (the same `for lang …` loop as Task 16).

- [ ] **Step 5: The screen**

Create `mobile/app/settings.tsx`:

```tsx
import { useEffect, useMemo, useState, useSyncExternalStore } from 'react';
import { AppState, Linking, PermissionsAndroid, Platform, ScrollView, StyleSheet, Switch, Text, View } from 'react-native';

import { Button, Card } from '@/components/ui';
import { t } from '@/lib/i18n';
import { PUSH_KINDS, type PushKind } from '@/lib/push/message';
import { pushSettings } from '@/lib/push/settings';
import { spacing, typeScale, usePalette, type Palette } from '@/theme';

const KIND_LABEL: Record<PushKind, string> = {
  completed: 'phone.settings.kind.completed',
  failed: 'phone.settings.kind.failed',
  permission: 'phone.settings.kind.permission',
  question: 'phone.settings.kind.question',
  plan: 'phone.settings.kind.plan',
  signin: 'phone.settings.kind.signin',
};

/** Whether Android lets this app post notifications (Android 13 asks once, Task 18). */
async function notificationsAllowed(): Promise<boolean> {
  if (Platform.OS !== 'android' || Number(Platform.Version) < 33) return true;
  return PermissionsAndroid.check(PermissionsAndroid.PERMISSIONS.POST_NOTIFICATIONS);
}

export default function SettingsScreen() {
  const palette = usePalette();
  const styles = useMemo(() => makeStyles(palette), [palette]);
  const settings = useSyncExternalStore(pushSettings.subscribe, pushSettings.get);
  const [allowed, setAllowed] = useState(true);

  useEffect(() => {
    const check = () => void notificationsAllowed().then(setAllowed).catch(() => undefined);
    check();
    const subscription = AppState.addEventListener('change', (state) => { if (state === 'active') check(); });
    return () => subscription.remove();
  }, []);

  const setKind = (kind: PushKind, value: boolean) =>
    void pushSettings.set({ ...settings, kinds: { ...settings.kinds, [kind]: value } });

  return (
    <ScrollView contentContainerStyle={styles.page}>
      <Card>
        <Text style={styles.heading}>{t('phone.settings.notifications')}</Text>
        <Text style={styles.hint}>{t('phone.settings.notificationsHint')}</Text>
        {!allowed ? (
          <View style={styles.block}>
            <Text style={styles.warning}>{t('phone.settings.permissionOff')}</Text>
            <Button label={t('phone.settings.openSystem')} tone="neutral" onPress={() => void Linking.openSettings()} />
          </View>
        ) : null}
        {PUSH_KINDS.map((kind) => (
          <View key={kind} style={styles.row}>
            <Text style={styles.label}>{t(KIND_LABEL[kind])}</Text>
            <Switch value={settings.kinds[kind]} onValueChange={(value) => setKind(kind, value)} accessibilityLabel={t(KIND_LABEL[kind])} />
          </View>
        ))}
      </Card>
      <Card>
        <View style={styles.row}>
          <Text style={styles.label}>{t('phone.settings.crashReports')}</Text>
          <Switch
            value={settings.crashReports}
            onValueChange={(value) => void pushSettings.set({ ...settings, crashReports: value })}
            accessibilityLabel={t('phone.settings.crashReports')}
          />
        </View>
        <Text style={styles.hint}>{t('phone.settings.crashReportsHint')}</Text>
      </Card>
    </ScrollView>
  );
}

function makeStyles(palette: Palette) {
  return StyleSheet.create({
    page: { gap: spacing.xl, padding: spacing.xl },
    block: { gap: spacing.md, paddingVertical: spacing.md },
    heading: { ...typeScale.heading, color: palette.text },
    hint: { ...typeScale.caption, color: palette.textMuted, paddingTop: spacing.sm },
    warning: { ...typeScale.body, color: palette.warning },
    row: { alignItems: 'center', flexDirection: 'row', justifyContent: 'space-between', paddingVertical: spacing.md },
    label: { ...typeScale.body, color: palette.text, flexShrink: 1 },
  });
}
```

In `mobile/app/_layout.tsx`, add after `<Stack.Screen name="pair" … />`:

```tsx
            <Stack.Screen name="settings" options={{ title: t('phone.settings.title') }} />
```

In `mobile/app/(tabs)/hosts.tsx`, add this button after the `phone.hosts.help` button in `ListFooterComponent`:

```tsx
            <Button
              label={t('phone.settings.title')}
              tone="ghost"
              onPress={() => router.push('/settings')}
            />
```

In `mobile/src/__tests__/routes.test.ts`, add `'/settings',` after `'/connect',` in the `keeps every link the app and the Mac hand out` expectation.

- [ ] **Step 6: Run it and expect it to pass**

Run: PHONE-RUN with `src/__tests__/push-settings.test.ts src/__tests__/routes.test.ts src/__tests__/design-spacing.test.ts`, then LOCALES, then `node scripts/check-design-tokens.js --check`
Expected: all pass; no Korean literal and no raw spacing number in the new screen.

- [ ] **Step 7: Commit**

```bash
git add mobile/src/lib/push/settings.ts mobile/src/lib/push/viewing.ts mobile/src/__tests__/push-settings.test.ts mobile/app/settings.tsx mobile/app/_layout.tsx "mobile/app/(tabs)/hosts.tsx" mobile/src/__tests__/routes.test.ts locales/ko.json locales/en.json locales/zh.json locales/ja.json native/macos/Sources/MightyCore/Resources/Locales/ko.json native/macos/Sources/MightyCore/Resources/Locales/en.json native/macos/Sources/MightyCore/Resources/Locales/zh.json native/macos/Sources/MightyCore/Resources/Locales/ja.json mobile/src/locales/ko.json mobile/src/locales/en.json mobile/src/locales/zh.json mobile/src/locales/ja.json docs/i18n.md
git commit -m "(mobile) a phone settings screen turns each kind of notification and crash reports on or off, and says when Android has notifications off; the phone can report which session it shows" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## Task 18: 폰이 푸시를 받아 보여 주고, 버튼과 탭을 처리하고, 충돌을 깨끗하게 보고한다

**Files:**
- Create: `mobile/index.js`
- Modify: `mobile/package.json` (`"main": "index.js"`)
- Create: `mobile/src/lib/push/default-error-handler.ts`
- Create: `mobile/src/lib/push/crash.ts`
- Create: `mobile/src/lib/push/host-lease.ts`
- Create: `mobile/src/lib/push/runtime.ts`
- Create: `mobile/src/lib/push/background.ts`
- Create: `mobile/src/components/push-root.tsx`
- Modify: `mobile/app/_layout.tsx` (`<PushRoot />`)
- Modify: `mobile/app/host/[hostId]/session/[sessionId].tsx` (보고 있음 보고)
- Create: `mobile/src/__tests__/push-crash.test.ts`

**Interfaces:**
- Consumes: Task 13–17 모두. `useHostsStore`, `credentialsFor`, `hostLeaseOptions` (`@/store/hosts`), `acquireHostClient` (`@/api/client`), RNFB `getMessaging`·`getToken`·`onTokenRefresh`·`onMessage`·`setBackgroundMessageHandler`, `getCrashlytics`·`recordError`·`setCrashlyticsCollectionEnabled`, notify-kit `notifee`·`EventType`·`AndroidImportance`.
- Produces:
  - `scrubError(error: unknown): Error`. 이름과 스택 프레임(`at 함수 (파일:줄:열)` 꼴)만 남긴다. 메시지는 이름으로 바꾼다.
  - `installCrashReporting(): void`
  - `withHostClient<T>(storageId: string, use: (client: MobileClient) => Promise<T>, timeoutMs?: number): Promise<T>`. 앱이 꺼진 상태에서도 연결을 연다.
  - `ensureChannels()`, `showPush(remote: { data?: Record<string, unknown> }): Promise<void>`, `handleNotificationEvent(event: Event, open?: (path: string) => void): Promise<void>`, `startPush(): Promise<() => void>`, `syncAllHosts(fcmToken?: string): Promise<void>`, `pushViewing`(앱 전체에 하나뿐인 보고기)
  - `<PushRoot />`

**순서.**
- `index.js`는 먼저 RN의 기본 오류 처리기를 잡아 두고, 그다음 `background.ts`(백그라운드 메시지·알림 이벤트 등록과 충돌 보고 설치), 마지막으로 `expo-router/entry`를 불러온다.
- 앱이 앞에 있을 때는 `PushRoot`가 맡는다:
  - 알림 채널을 만든다.
  - Android 13 이상에서는 알림 권한을 **한 번만** 묻는다(`mightyclaude.push.asked.v1`).
  - FCM 토큰을 받아 모든 호스트에 동기화한다. 토큰이 바뀔 때, 호스트 목록이나 설정이 바뀔 때도 다시 한다.
  - 앞에서 받은 메시지를 보이고, 알림 탭으로 화면을 연다.
- 세션 화면이 포커스를 얻으면 `pushViewing.enter`, 잃으면 `leave`다. 앱 상태가 바뀌면 `setActive`다.

- [ ] **Step 1: Write the failing test**

Create `mobile/src/__tests__/push-crash.test.ts`:

```ts
jest.mock('@react-native-firebase/crashlytics', () => ({ getCrashlytics: jest.fn(), recordError: jest.fn(), setCrashlyticsCollectionEnabled: jest.fn() }));
jest.mock('@/lib/push/default-error-handler', () => ({ defaultErrorHandler: jest.fn() }));
jest.mock('expo-secure-store', () => ({ getItemAsync: jest.fn(async () => null), setItemAsync: jest.fn(async () => undefined), deleteItemAsync: jest.fn(async () => undefined) }));

import { scrubError } from '@/lib/push/crash';

describe('scrubError', () => {
  it('keeps the error name and stack frames, never the message', () => {
    const error = new TypeError('cannot read /Users/someone/secret-project/plan.md: rm -rf build');
    error.stack = [
      'TypeError: cannot read /Users/someone/secret-project/plan.md: rm -rf build',
      '    at decide (address at index.android.bundle:1:23456)',
      '    at anonymous (index.android.bundle:12:34)',
      '    at a line that is not a frame /Users/someone',
    ].join('\n');
    const scrubbed = scrubError(error);
    expect(scrubbed.name).toBe('TypeError');
    expect(scrubbed.message).toBe('TypeError');
    expect(scrubbed.stack).toBe(['TypeError', '    at decide (address at index.android.bundle:1:23456)', '    at anonymous (index.android.bundle:12:34)'].join('\n'));
    expect(JSON.stringify({ m: scrubbed.message, s: scrubbed.stack })).not.toMatch(/secret|rm -rf|Users/);
  });

  it('names anything that is not an Error plainly', () => {
    expect(scrubError('a string with content').message).toBe('Error');
    expect(scrubError({ name: 'Bad Name With Spaces' }).name).toBe('Error');
  });
});
```

- [ ] **Step 2: Run it and expect it to fail**

Run: PHONE-RUN with `src/__tests__/push-crash.test.ts`
Expected: FAIL — `Cannot find module '@/lib/push/crash'`.

- [ ] **Step 3: Crash reporting**

Create `mobile/src/lib/push/default-error-handler.ts`:

```ts
/**
 * React Native's own fatal-error handler, taken before Crashlytics installs its own (index.js
 * imports this first), so the app still fails the normal way after the scrubbed report.
 */
export const defaultErrorHandler = ErrorUtils.getGlobalHandler();
```

Create `mobile/src/lib/push/crash.ts`:

```ts
import { getCrashlytics, recordError, setCrashlyticsCollectionEnabled } from '@react-native-firebase/crashlytics';

import { defaultErrorHandler } from '@/lib/push/default-error-handler';
import { pushSettings } from '@/lib/push/settings';

const NAME = /^[A-Za-z][A-Za-z0-9_]{0,63}$/;
const FRAME = /^at [^()\n]{1,120} \([^()\n]{1,200}:\d+:\d+\)$/;

/**
 * What a crash report may say: the error's type and where it happened. The message is
 * dropped because it can quote a conversation, a command or a path (docs/mobile-remote.md
 * "Crashlytics"); a stack line that is not a plain frame is dropped for the same reason.
 */
export function scrubError(error: unknown): Error {
  const raw = error instanceof Error ? error : null;
  const name = raw !== null && NAME.test(raw.name) ? raw.name : 'Error';
  const frames = (raw?.stack ?? '')
    .split('\n')
    .map((line) => line.trim())
    .filter((line) => FRAME.test(line))
    .slice(0, 50);
  const scrubbed = new Error(name);
  scrubbed.name = name;
  scrubbed.stack = [name, ...frames.map((frame) => `    ${frame}`)].join('\n');
  return scrubbed;
}

/** Collection follows the setting; JS errors go out scrubbed, then to React Native's handler. */
export function installCrashReporting(): void {
  let crashlytics: ReturnType<typeof getCrashlytics>;
  try {
    crashlytics = getCrashlytics();
  } catch {
    return; // A build without Firebase.
  }
  const apply = () => void setCrashlyticsCollectionEnabled(crashlytics, pushSettings.get().crashReports).catch(() => undefined);
  void pushSettings.ready.then(apply);
  pushSettings.subscribe(apply);
  ErrorUtils.setGlobalHandler((error, isFatal) => {
    if (pushSettings.get().crashReports) recordError(crashlytics, scrubError(error));
    defaultErrorHandler(error, isFatal);
  });
}
```

- [ ] **Step 4: Run it and expect it to pass**

Run: PHONE-RUN with `src/__tests__/push-crash.test.ts`
Expected: 2 tests pass. The three mocks keep the native Crashlytics module, React Native's `ErrorUtils` and the keychain out of jest.

- [ ] **Step 5: The headless lease and the runtime**

Create `mobile/src/lib/push/host-lease.ts`:

```ts
import { acquireHostClient, type MobileClient } from '@/api/client';
import type { HostSecrets } from '@/lib/host-secrets';
import { credentialsFor, hostLeaseOptions, useHostsStore } from '@/store/hosts';

/** A connection to one paired host that works with the app closed (a notification button's headless task). */
export async function withHostClient<T>(storageId: string, use: (client: MobileClient) => Promise<T>, timeoutMs = 15_000): Promise<T> {
  if (!useHostsStore.getState().loaded) await useHostsStore.getState().load();
  const state = useHostsStore.getState();
  const host = state.hosts.find((entry) => entry.id === storageId);
  if (!host) throw new Error('unknown host');
  const secrets: HostSecrets = {};
  const key = state.keys[storageId];
  const token = state.tokens[storageId];
  if (key) secrets.pairingKey = key;
  if (token) secrets.deviceToken = token;
  const lease = acquireHostClient(storageId, credentialsFor(host, secrets, state.clientIds[storageId] ?? state.clientId), hostLeaseOptions(storageId));
  let timer: ReturnType<typeof setTimeout> | undefined;
  try {
    return await Promise.race([
      use(lease.client),
      new Promise<never>((_, reject) => {
        timer = setTimeout(() => reject(new Error('timeout')), timeoutMs);
      }),
    ]);
  } finally {
    if (timer !== undefined) clearTimeout(timer);
    lease.release();
  }
}
```

Create `mobile/src/lib/push/runtime.ts`:

```ts
import { getMessaging, getToken, onMessage, onTokenRefresh } from '@react-native-firebase/messaging';
import * as SecureStore from 'expo-secure-store';
import { AppState, PermissionsAndroid, Platform } from 'react-native';
import notifee, { AndroidImportance, EventType, type Event, type Notification } from 'react-native-notify-kit';

import { answerFromNotification } from '@/lib/push/actions';
import { isDeviceLocked } from '@/lib/push/device-lock';
import { withHostClient } from '@/lib/push/host-lease';
import { parsePushMessage } from '@/lib/push/message';
import {
  CHANNEL_ATTENTION,
  CHANNEL_WORK,
  channelName,
  notificationData,
  notificationFor,
  outcomeUpdate,
  sessionPath,
  type PushAction,
  type PushNotificationSpec,
} from '@/lib/push/notification';
import { findHostByHandle, syncHostPush } from '@/lib/push/registry';
import { openPush, pushKey } from '@/lib/push/seal';
import { enabledKinds, pushSettings } from '@/lib/push/settings';
import { createViewingReporter } from '@/lib/push/viewing';
import { useHostsStore } from '@/store/hosts';

const ASKED_KEY = 'mightyclaude.push.asked.v1';
const pushFetch = (url: string, init: { method: string; headers: Record<string, string>; body?: string }) => fetch(url, init);

export async function ensureChannels(): Promise<void> {
  await notifee.createChannel({ id: CHANNEL_ATTENTION, name: channelName(CHANNEL_ATTENTION), importance: AndroidImportance.HIGH });
  await notifee.createChannel({ id: CHANNEL_WORK, name: channelName(CHANNEL_WORK), importance: AndroidImportance.DEFAULT });
}

function toNotification(spec: PushNotificationSpec): Notification {
  return {
    id: spec.id,
    title: spec.title,
    body: spec.body,
    data: spec.data,
    android: {
      channelId: spec.channelId,
      subText: spec.subText,
      pressAction: { id: 'default', launchActivity: 'default' },
      actions: spec.actions.map((action) => ({ title: action.title, pressAction: { id: action.id } })),
    },
  };
}

async function loadedHosts() {
  if (!useHostsStore.getState().loaded) await useHostsStore.getState().load();
  return useHostsStore.getState();
}

/** An FCM data message → the host's sealed message → a local notification. Anything else is dropped. */
export async function showPush(remote: { data?: Record<string, unknown> }): Promise<void> {
  const data = remote.data ?? {};
  const handle = data['h'];
  const payload = data['p'];
  if (data['v'] !== '1' || typeof handle !== 'string' || typeof payload !== 'string') return;
  const state = await loadedHosts();
  const storageId = await findHostByHandle(SecureStore, state.hosts.map((host) => host.id), handle);
  if (storageId === null) return;
  const token = state.tokens[storageId];
  const clientId = state.clientIds[storageId] ?? state.clientId;
  if (!token || !clientId) return;
  const opened = openPush(payload, pushKey(token, clientId), handle);
  const message = opened === null ? null : parsePushMessage(opened);
  if (message === null) return;
  await pushSettings.ready;
  if (!pushSettings.get().kinds[message.kind]) return;
  await ensureChannels();
  await notifee.displayNotification(toNotification(notificationFor(message, storageId, handle)));
}

/** A tap opens the session; a button answers from the background (docs/mobile-remote.md "푸시 알림"). */
export async function handleNotificationEvent({ type, detail }: Event, open?: (path: string) => void): Promise<void> {
  const notification = detail.notification;
  const data = notificationData(notification?.data);
  if (data === null || notification === undefined) return;
  if (type === EventType.PRESS) {
    open?.(sessionPath(data));
    return;
  }
  if (type !== EventType.ACTION_PRESS) return;
  const outcome = await answerFromNotification(
    {
      isDeviceLocked,
      respond: (storageId, sessionId, body) => withHostClient(storageId, async (client) => { await client.respondPermission(sessionId, body); }),
      stillPending: (storageId, sessionId, requestId) =>
        withHostClient(storageId, async (client) => (await client.session(sessionId)).permissions.some((permission) => permission.id === requestId)),
    },
    data,
    detail.pressAction?.id as PushAction,
  );
  const update = outcomeUpdate(outcome);
  await notifee.displayNotification({
    ...notification,
    body: update.body,
    android: {
      ...notification.android,
      channelId: notification.android?.channelId ?? CHANNEL_ATTENTION,
      actions: update.keepActions ? notification.android?.actions ?? [] : [],
      pressAction: { id: 'default', launchActivity: 'default' },
    },
  });
}

async function askOnce(): Promise<void> {
  if (Platform.OS !== 'android' || Number(Platform.Version) < 33) return;
  if ((await SecureStore.getItemAsync(ASKED_KEY)) === '1') return;
  await SecureStore.setItemAsync(ASKED_KEY, '1');
  await PermissionsAndroid.request(PermissionsAndroid.PERMISSIONS.POST_NOTIFICATIONS);
}

let lastToken: string | undefined;

/** Gives every paired host that supports push this phone's current handle and kinds. */
export async function syncAllHosts(fcmToken?: string): Promise<void> {
  if (fcmToken !== undefined) lastToken = fcmToken;
  const token = lastToken;
  if (token === undefined) return;
  const state = await loadedHosts();
  await pushSettings.ready;
  const kinds = enabledKinds(pushSettings.get());
  await Promise.all(
    state.hosts
      .filter((host) => state.tokens[host.id])
      .map((host) =>
        withHostClient(host.id, (client) => syncHostPush({ store: SecureStore, fetch: pushFetch, client }, host, token, kinds)).catch(() => 'failed' as const),
      ),
  );
}

/** Foreground start: channels, the one-time Android 13 prompt, the token, and listeners. Returns a cleanup. */
export async function startPush(): Promise<() => void> {
  await ensureChannels();
  await askOnce().catch(() => undefined);
  let messaging: ReturnType<typeof getMessaging>;
  try {
    messaging = getMessaging();
    await syncAllHosts(await getToken(messaging));
  } catch {
    return () => undefined; // A build without Firebase, or no network yet: the next start tries again.
  }
  const stopToken = onTokenRefresh(messaging, (token) => void syncAllHosts(token));
  const stopMessages = onMessage(messaging, (remote) => showPush(remote as { data?: Record<string, unknown> }));
  return () => {
    stopToken();
    stopMessages();
  };
}

/** The one viewing reporter: the session screen enters and leaves; app state pauses it. */
export const pushViewing = createViewingReporter({
  send: (storageId, sessionId) => withHostClient(storageId, async (client) => { await client.reportViewing(sessionId); }, 10_000),
  setInterval: (run, ms) => setInterval(run, ms),
  clearInterval: (handle) => clearInterval(handle as ReturnType<typeof setInterval>),
});
AppState.addEventListener('change', (state) => {
  if (state !== 'inactive') pushViewing.setActive(state === 'active');
});
```

Create `mobile/src/lib/push/background.ts`:

```ts
import { getMessaging, setBackgroundMessageHandler } from '@react-native-firebase/messaging';
import notifee from 'react-native-notify-kit';

import { installCrashReporting } from '@/lib/push/crash';
import { handleNotificationEvent, showPush } from '@/lib/push/runtime';

// Registered at the JS entry (index.js) so a message or a button that wakes a closed app finds them.
installCrashReporting();
try {
  setBackgroundMessageHandler(getMessaging(), (remote) => showPush(remote as { data?: Record<string, unknown> }));
} catch {
  // A build without Firebase: no pushes arrive, nothing to handle.
}
notifee.onBackgroundEvent((event) => handleNotificationEvent(event));
```

Create `mobile/index.js`:

```js
// The JS entry. React Native's own error handler is kept first, then background push and
// notification handling is registered before the app mounts (docs/mobile-remote.md "푸시 알림").
import './src/lib/push/default-error-handler';
import './src/lib/push/background';
import 'expo-router/entry';
```

In `mobile/package.json`, change `"main": "expo-router/entry"` to `"main": "index.js"`.

Create `mobile/src/components/push-root.tsx`:

```tsx
import { router } from 'expo-router';
import { useEffect } from 'react';
import notifee from 'react-native-notify-kit';

import { notificationData, sessionPath } from '@/lib/push/notification';
import { handleNotificationEvent, startPush, syncAllHosts } from '@/lib/push/runtime';
import { pushSettings } from '@/lib/push/settings';
import { useHostsStore } from '@/store/hosts';

/** Foreground push: start-up, taps, and re-syncing when hosts or settings change. Renders nothing. */
export function PushRoot() {
  const hostKey = useHostsStore((state) => state.hosts.map((host) => host.id).join(','));

  useEffect(() => {
    let stop: (() => void) | undefined;
    void startPush().then((cleanup) => { stop = cleanup; });
    void notifee.getInitialNotification().then((initial) => {
      const data = notificationData(initial?.notification.data);
      if (data !== null) router.push(sessionPath(data) as never);
    });
    const stopForeground = notifee.onForegroundEvent((event) => void handleNotificationEvent(event, (path) => router.push(path as never)));
    const stopSettings = pushSettings.subscribe(() => void syncAllHosts());
    return () => {
      stop?.();
      stopForeground();
      stopSettings();
    };
  }, []);

  useEffect(() => {
    void syncAllHosts();
  }, [hostKey]);

  return null;
}
```

In `mobile/app/_layout.tsx`, add `import { PushRoot } from '@/components/push-root';`, and put `<PushRoot />` right before `<ToastHost />`.

In `mobile/app/host/[hostId]/session/[sessionId].tsx`:
- Add the import `import { pushViewing } from '@/lib/push/runtime';`.
- After `const canStyle = hasCapability(capabilities, 'style');`, add `const canPush = hasCapability(capabilities, 'push');`.
- Next to the existing `useFocusEffect` for commands, add:

```tsx
  // The host does not push about the session the phone is showing (docs/mobile-remote.md "푸시 알림").
  useFocusEffect(
    useCallback(() => {
      if (!hostId || !sessionId || !canPush) return undefined;
      pushViewing.enter(hostId, sessionId);
      return () => pushViewing.leave();
    }, [canPush, hostId, sessionId]),
  );
```

- [ ] **Step 6: Check**

Run: `cd /Users/young/Work/MightyClaude-firebase/mobile && npm run typecheck && npx jest --silent`
Expected: typecheck clean; the whole jest suite passes.

If a test that imports the session screen or `_layout` now fails because notify-kit or RNFB has no jest mock, add `jest.mock('@/lib/push/runtime', () => ({ pushViewing: { enter: jest.fn(), leave: jest.fn(), setActive: jest.fn() } }))` to that test. Do not add a global mock in `jest.setup.js`.

Run: `cd /Users/young/Work/MightyClaude-firebase && node scripts/check-locales.js --check && node scripts/check-design-tokens.js --check`
Expected: no error (no Korean literal and no spacing literal in the new files).

- [ ] **Step 7: Commit**

```bash
git add mobile/index.js mobile/package.json mobile/src/lib/push/default-error-handler.ts mobile/src/lib/push/crash.ts mobile/src/lib/push/host-lease.ts mobile/src/lib/push/runtime.ts mobile/src/lib/push/background.ts mobile/src/components/push-root.tsx mobile/app/_layout.tsx "mobile/app/host/[hostId]/session/[sessionId].tsx" mobile/src/__tests__/push-crash.test.ts
git commit -m "(mobile) the phone shows a host's push even when closed, answers permission buttons in the background after unlock, opens the session on a tap, asks for Android 13 notification permission once, tells the host which session is on screen, and sends crash reports with no user content" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

If Step 6 needed per-test mocks, add those test files to `git add`.

---

## Task 19: 계약 문서와 도움말이 푸시를 설명한다

**Files:**
- Modify: `docs/mobile-remote.md` (`### 라우트` 표에 세 줄, 기능 `"push"`, 새 절 `### 푸시 알림 (push)`을 `### 파일 (files)` 절 뒤, "휴대폰에서 의도적으로 제외한 Mac 기능"에서 "알림(푸시·로컬)"과 "앱 설정" 빼기)
- Modify: `docs/help/content/ko/phone.md`, `docs/help/content/en/phone.md`, `docs/help/content/zh/phone.md`, `docs/help/content/ja/phone.md` (66번째 줄의 "푸시 없음" 문장 빼기, `## 승인과 질문 {#approvals}` 절 뒤에 `## 알림 {#notifications}`)

**Interfaces:**
- Consumes: Task 1–18의 계약.
- Produces: 문서만 바뀐다. `node scripts/help/build.mjs --check`가 통과한다(네 언어의 제목·`{#id}`·`{{ui:…}}` 키가 같다).

- [ ] **Step 1: docs/mobile-remote.md**

Add these rows to the table under `### 라우트`:

```markdown
| POST | `/m1/push/register` | `{ handle, kinds: [string] }` | `{ protocol, ok }` · 핸들 형식 오류 400(`remote.error.badPushHandle`) · 기기 토큰이 없는 기기 409(`remote.error.pushNeedsToken`) · 저장 실패 503 — `push` 기능 |
| POST | `/m1/push/unregister` | `{}` | `{ protocol, ok }` |
| POST | `/m1/push/viewing` | `{ sessionId: string \| null }` | `{ protocol, ok }` · 형식이 틀린 id 404 — 45초 유효 |
```

Add this item to the capability strings in `### 추가 필드` (or wherever the capability names are listed in bold): `**"push"**: 위 세 라우트가 있고, 이 호스트가 푸시를 보낸다(아래 "푸시 알림").`

Insert this section after the `### 파일 (files)` section, before `### 휴대폰에서 의도적으로 제외한 Mac 기능`:

````markdown
### 푸시 알림 (push)

호스트(Mac이 기준, Windows 같음)가 일이 끝나거나 사람이 필요할 때 안드로이드 폰에 알린다. 릴레이 쪽 라우트는 [relay.md](relay.md) "푸시 알림"이다.

**핸들.** 폰은 FCM 토큰을 페어링의 릴레이(`POST /push/v1/handles`)에 등록하고 받은 핸들을 그 호스트에게만 `POST /m1/push/register`로 준다. 폰이 켠 종류(`kinds`)도 함께 준다. 호스트는 기기 목록의 그 기기 줄에 `push: { handle, kinds, updatedAt }`로 둔다(Mac `devices.json`, Windows `mobile-identity.json`). 기기를 해제하거나 키를 다시 만들면 함께 지워진다. 호스트는 핸들만 알고 토큰은 모른다. 기기 토큰이 없는 기기(구버전 앱 묶음)는 409다. 폰이 페어링을 지우면 릴레이에서 핸들을 지운다. 호스트는 다음 전송에서 404를 받고 자기 기록을 지운다.

**봉인.**
- `key = HKDF-SHA256(ikm = SHA-256(UTF-8 deviceToken) 32B, salt = UTF-8 clientId, info = "mighty-push-v1", 32B)`. 호스트는 기기 줄의 `tokenHash`(16진수)를 풀어 같은 32바이트를 얻는다. 릴레이 연결과 Tailscale 직접 연결이 같은 기기 토큰으로 인증하므로 키도 같다.
- 봉투는 `base64url(0x01 ‖ nonce 12B ‖ ChaCha20-Poly1305 암호문 ‖ 태그 16B)`이고, AAD는 `"mighty-push-v1:" + handle`, 평문은 2048바이트 이하, 봉투는 3072자 이하다.
- 공유 고정 파일은 `native/contracts/fixtures/push.json`이다.
- 호스트의 기기 목록 파일을 읽을 수 있는 사람은 푸시를 열 수 있다(그 파일은 소유자만 읽는다). 릴레이와 Google은 열 수 없다.

**평문.**

```
MobilePushMessage {
  v: 1, id,                       // id: "request:<runId>:<requestId>" | "run:<sessionId>:<n>:completed|failed" | "signin:<sessionId>:<n>"
  kind: "completed" | "failed" | "permission" | "question" | "plan" | "signin",
  hostId, hostName, workspace, title, summary, sessionId,   // hostName·workspace·title 80, summary 200 (유니코드 스칼라)
  permission?: { requestId, runId, buttons: "claude" | "antigravity" | "antigravityOnce" | "none" },  // kind permission만
  at                              // ISO 8601 UTC, 초 단위
}
```

**무엇을 언제 보내는가** (`MobilePushPlanner`, 공유 고정 파일의 `planner` 단계):
1. 호스트가 처음 보는 상태는 기준선이다(앱이 켜질 때 이미 있던 요청은 알리지 않는다).
2. 새 요청(권한, 질문, 계획, Antigravity 카드)은 한 번 알린다. 스타일이 자동 허용하는 요청은 알리지 않는다.
3. `running`에서 `completed`·`error`로 바뀌고 기다리는 요청이 없으면 완료·실패를 알린다. 요청이 남아 있으면 요청만 알리고, `stopped`는 알리지 않는다.
4. 로그인 만료는 호스트가 확인한 뒤(Mac `loginRequired`, Windows 로그인 복구가 기억한 요청) 한 번 알린다.
5. 폰이 `POST /m1/push/viewing`으로 그 세션을 보고 있다고 한 지 45초가 안 됐으면 그 폰에는 보내지 않는다. 폰은 세션 화면이 앞에 있는 동안 30초마다 다시 알리고, 떠나거나 뒤로 가면 `null`을 보낸다.
6. 버튼: Claude·Codex 권한은 `claude`(허용 가능할 때). Antigravity 카드는 규칙 있는 작업이 없거나 `canAllow`가 거짓이면 `none`, 규칙 중 `onceOnly`가 있으면 `antigravityOnce`, 아니면 `antigravity`. 질문·계획은 `none`.

**전송.** 호스트는 자기 릴레이(`wss://…` → `https://…/push/v1/send`)로 `Authorization: Bearer <hostToken>`과 `{ serverId, handle, payload }`를 보낸다. 2초·8초 뒤 두 번 더 시도하고, `Retry-After`(30초 이하)가 길면 그것을 쓴다. 403·404·410은 그 기기의 핸들을 지운다. 실패는 진단 기록에만 남는다(Mac `os.Logger` 범주 `mobile-remote`, Windows `Trace` `mobile push:`). 모바일 리모트가 꺼져 있으면 아무것도 보내지 않는다. Tailscale 직접 연결로 붙은 폰에도 알림은 릴레이로 간다.

**폰.**
- FCM 데이터 메시지 `{ v: "1", h, p }`를 백그라운드 처리기가 받는다. `h`로 페어링을 찾아 열고, 열 수 없거나 모양이 틀리거나 그 종류를 껐으면 버린다.
- 채널: `attention`(권한·질문·계획, 높음), `work`(완료·실패·로그인).
- 요청 알림의 id는 `<handle>:<id>`다. 상태 알림의 id는 `<handle>:<sessionId>:status`라서, 실패 뒤의 로그인 만료가 같은 자리를 바꾼다.
- 버튼은 기존 `POST /m1/sessions/{id}/permission`(Antigravity `always` 포함)으로 백그라운드에서 답한다.
  - **잠금 확인**: 휴대폰이 잠겨 있으면(로컬 모듈 `MightyDeviceLock`, 모르면 잠긴 것으로 친다) 보내지 않고 "잠금을 푼 뒤 다시 누르세요"로 바꾼다.
  - 성공하면 "허용함"/"거절함"이다.
  - 409를 받으면 세션을 다시 읽는다. 요청이 없으면 "이미 처리됨"이다. 있으면(예: Antigravity 설정 파일 오류) 또는 다른 실패·15초 초과면 "응답하지 못했습니다. 눌러서 여세요."다.
- 알림을 누르면 `mightyclaude://host/<storageId>/session/<sessionId>` 화면이 열린다.
- 안드로이드 13 이상은 처음 한 번 알림 권한을 묻는다. 설정 화면에 종류별 스위치(기본 모두 켬)와 충돌 보고 스위치(기본 켬)가 있다.

**Crashlytics.** 폰 앱의 네이티브 충돌과 처리되지 않은 JS 오류만 보낸다. JS 오류는 이름과 스택 프레임만 남기고 메시지를 버린 뒤 보낸다(`scrubError`). `log`·속성은 쓰지 않는다. 수집은 설정을 읽은 뒤에만 켠다(`firebase.json`의 자동 수집은 꺼 둔다).

**빌드.** `google-services.json`은 저장소에 없다. `mobile/app.config.js`가 환경 변수 `GOOGLE_SERVICES_JSON`의 파일을 넣고, EAS 빌드에서 변수가 없으면 멈춘다. 릴리즈 도구가 그 변수를 채운다.
````

In `### 휴대폰에서 의도적으로 제외한 Mac 기능`, change the start of the sentence from `알림(푸시·로컬), 터미널 실행 창 조작,` to `로컬 알림(휴대폰의 푸시 알림은 위 "푸시 알림"), 터미널 실행 창 조작,`. Change `CLI 계정 전환·CLI 업데이트·앱 자체 업데이트·앱 설정,` to `CLI 계정 전환·CLI 업데이트·앱 자체 업데이트·데스크톱 앱 설정,`.

- [ ] **Step 2: Help pages**

In each `phone.md`, delete line 66 (the "no push" sentence: ko `휴대폰 알림(푸시)은 보내지 않습니다. 앱을 열어 확인하세요.`, en `The phone doesn't get push notifications. Open the app to check.`, zh `手机不会收到推送通知。请打开应用查看。`, ja `スマートフォンへのプッシュ通知は送信されません。アプリを開いて確認してください。`) and the blank line before it. Insert the matching section right before `## 화면 보기 {#screen}` (en `## Screen view {#screen}`, and so on).

`ko`:
```markdown
## 알림 {#notifications}
연결된 컴퓨터(Mac 또는 Windows)에서 작업이 끝나거나 실패했을 때, 그리고 권한 요청·질문·계획 승인·로그인 만료로 사람이 필요할 때 휴대폰에 알림이 옵니다. 지금은 Android 앱만 받습니다.
- 처음 앱을 열 때 알림을 허용할지 한 번 묻습니다. 종류별로 끄려면 {{ui:phone.hosts.help}} 아래의 {{ui:phone.settings.title}}로 갑니다.
- 권한 요청 알림에서 바로 {{ui:permission.allow}}나 {{ui:permission.deny}}를 누를 수 있습니다. Antigravity 카드는 {{ui:permission.allowOnce}}·{{ui:antigravity.permission.allowAlways}}·{{ui:antigravity.permission.deny}}입니다. 버튼은 휴대폰 잠금을 푼 뒤에만 동작하고, 컴퓨터에서 먼저 답했다면 {{ui:phone.push.outcome.already}}로 바뀝니다.
- 질문과 계획 알림, 그리고 알림 자체를 누르면 그 세션이 열립니다. 휴대폰에서 그 세션을 보고 있는 동안에는 알림이 오지 않습니다.
- 알림 내용은 컴퓨터가 이 휴대폰만 열 수 있게 암호화합니다. 릴레이와 Google은 읽지 못합니다. 알림은 릴레이를 거치므로 Tailscale로 직접 연결해도 컴퓨터가 인터넷에 닿으면 옵니다.
- 컴퓨터 앱과 휴대폰 앱이 모두 이 기능이 있는 버전이어야 합니다. 아주 오래된 휴대폰 앱으로 페어링했다면 다시 페어링하세요.

앱이 멈추면 멈춘 곳만 개발자에게 보냅니다. 대화·명령·경로는 보내지 않고, {{ui:phone.settings.title}}에서 {{ui:phone.settings.crashReports}}를 끌 수 있습니다.

```

`en`:
```markdown
## Notifications {#notifications}
Your phone gets a notification when a run on a connected computer (Mac or Windows) finishes or fails, and when an agent needs you: a permission request, a question, a plan to approve or an expired sign-in. Only the Android app gets them for now.
- The first time you open the app, it asks once whether to allow notifications. To turn kinds off, open {{ui:phone.settings.title}} under {{ui:phone.hosts.help}}.
- On a permission request you can tap {{ui:permission.allow}} or {{ui:permission.deny}} right in the notification. An Antigravity card offers {{ui:permission.allowOnce}}, {{ui:antigravity.permission.allowAlways}} and {{ui:antigravity.permission.deny}}. The buttons work only after you unlock the phone, and if you already answered on the computer the notification changes to {{ui:phone.push.outcome.already}}.
- Tapping a question, a plan or any notification opens that session. You get no notifications about a session while it is on your phone's screen.
- The computer encrypts each notification so only this phone can read it; the relay and Google can't. Notifications go through the relay, so they arrive over a direct Tailscale connection too, as long as the computer can reach the internet.
- Both the desktop app and the phone app need a version with this feature. If you paired with a very old phone app, pair again.

When the app crashes it sends only where it stopped, never conversations, commands or paths. You can turn {{ui:phone.settings.crashReports}} off in {{ui:phone.settings.title}}.

```

`zh`:
```markdown
## 通知 {#notifications}
已连接的电脑（Mac 或 Windows）上的运行完成或失败时，以及需要你处理权限请求、问题、待批准的计划或登录过期时，手机会收到通知。目前只有 Android 应用会收到。
- 第一次打开应用时会询问一次是否允许通知。要按类型关闭，请打开 {{ui:phone.hosts.help}} 下方的 {{ui:phone.settings.title}}。
- 收到权限请求通知时，可以直接点 {{ui:permission.allow}} 或 {{ui:permission.deny}}。Antigravity 卡片提供 {{ui:permission.allowOnce}}、{{ui:antigravity.permission.allowAlways}} 和 {{ui:antigravity.permission.deny}}。按钮只有在手机解锁后才起作用；如果已在电脑上回答，通知会变为 {{ui:phone.push.outcome.already}}。
- 点按问题、计划或任何通知都会打开对应的会话。手机正在显示某个会话时，不会收到关于它的通知。
- 电脑会加密每条通知，只有这部手机能读取，中继和 Google 无法读取。通知经由中继发送，因此通过 Tailscale 直接连接时，只要电脑能上网也会收到。
- 电脑应用和手机应用都必须是带有此功能的版本。如果你是用很旧的手机应用配对的，请重新配对。

应用崩溃时只发送出错位置，不发送对话、命令或路径。可以在 {{ui:phone.settings.title}} 中关闭 {{ui:phone.settings.crashReports}}。

```

`ja`:
```markdown
## 通知 {#notifications}
接続したコンピューター（Mac または Windows）で実行が終わったとき、失敗したとき、そして許可の依頼・質問・計画の承認・ログイン切れで対応が必要なときに、スマートフォンへ通知が届きます。今は Android アプリだけが受け取ります。
- 初めてアプリを開いたとき、通知を許可するか一度だけ聞きます。種類ごとにオフにするには、{{ui:phone.hosts.help}} の下の {{ui:phone.settings.title}} を開きます。
- 許可の依頼の通知では、その場で {{ui:permission.allow}} か {{ui:permission.deny}} をタップできます。Antigravity のカードは {{ui:permission.allowOnce}}・{{ui:antigravity.permission.allowAlways}}・{{ui:antigravity.permission.deny}} です。ボタンはスマートフォンのロックを解除してからでないと動きません。コンピューターで先に答えていた場合は {{ui:phone.push.outcome.already}} に変わります。
- 質問や計画の通知、また通知そのものをタップすると、そのセッションが開きます。スマートフォンでそのセッションを表示している間は、通知は届きません。
- 通知の内容は、このスマートフォンだけが読めるようにコンピューターが暗号化します。リレーと Google は読めません。通知はリレーを通るので、Tailscale で直接接続していても、コンピューターがインターネットにつながっていれば届きます。
- コンピューターのアプリとスマートフォンのアプリの両方が、この機能のあるバージョンである必要があります。とても古いスマートフォンアプリでペアリングした場合は、もう一度ペアリングしてください。

アプリが止まったときは、止まった場所だけを送ります。会話・コマンド・パスは送りません。{{ui:phone.settings.title}} で {{ui:phone.settings.crashReports}} をオフにできます。

```

- [ ] **Step 3: Check**

Run: `cd /Users/young/Work/MightyClaude-firebase && node scripts/help/build.mjs --check && node scripts/check-locales.js --check`
Expected: both exit 0. `build.mjs` accepts every `{{ui:…}}` key above: they all exist and none has a `{placeholder}`.

- [ ] **Step 4: Commit**

```bash
git add docs/mobile-remote.md docs/help/content/ko/phone.md docs/help/content/en/phone.md docs/help/content/zh/phone.md docs/help/content/ja/phone.md
git commit -m "(docs) the phone contract and the help pages (four languages) explain push notifications: what is sent and when, the encryption, the answer buttons that need an unlocked phone, the settings, and crash reports without user content" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## Task 20: 릴리즈 도구가 같은 APK를 App Distribution으로도 올린다 (저장소 밖)

**Files (저장소 밖, `~/Work/.mc-release`, git 아님 — 커밋 없음):**
- Modify: `~/Work/.mc-release/apk-local.sh` (빌드 전에 `GOOGLE_SERVICES_JSON`)
- Create: `~/Work/.mc-release/appdist.sh`
- 사용자가 만든다: `~/Work/.mc-release/appdist.env`(`FIREBASE_ANDROID_APP_ID=…`), `~/Work/.mc-release/appdist-testers.txt`(쉼표로 이은 테스터 주소), `~/Work/.mc-release/google-services.json`(Task 14 Step 8)

**Interfaces:**
- Consumes: `staging/MightyClaude-android.apk`, `staging/notes-<V>.txt`(지금 릴리즈 흐름이 만든다), Firebase CLI(`npx -y firebase-tools@15`).
- Produces: `bash ~/Work/.mc-release/appdist.sh <버전>`. R2 업로드(`upload.sh … apk`)와 따로 돈다. R2 다운로드 페이지는 그대로다.

- [ ] **Step 1: apk-local.sh**

In `~/Work/.mc-release/apk-local.sh`, insert these lines right before the `npx eas-cli build …` line:

```bash
# Firebase(푸시·Crashlytics) 설정 파일. 저장소 밖에만 있다(mobile/app.config.js가 읽는다).
export GOOGLE_SERVICES_JSON="$HOME/Work/.mc-release/google-services.json"
[ -f "$GOOGLE_SERVICES_JSON" ] || { echo "✘ $GOOGLE_SERVICES_JSON 이 없습니다 (Firebase 콘솔 → Android 앱 → google-services.json)" >&2; exit 1; }
```

- [ ] **Step 2: appdist.sh**

Create `~/Work/.mc-release/appdist.sh`:

```bash
#!/usr/bin/env bash
# 용법: bash ~/Work/.mc-release/appdist.sh 0.4.34
# R2에 올린 것과 같은 APK(staging/MightyClaude-android.apk)를 Firebase App Distribution으로
# 테스터에게 보낸다. 앱 ID와 테스터 주소는 이 폴더의 로컬 파일에만 있다(공개 저장소에 넣지 않는다).
# 처음 한 번 사용자가 터미널 창에서 `npx -y firebase-tools@15 login`을 직접 해야 한다.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
VERSION="${1:?버전을 주세요 (예: 0.4.34)}"
APK="${HERE}/staging/MightyClaude-android.apk"
NOTES="${HERE}/staging/notes-${VERSION}.txt"
TESTERS="${HERE}/appdist-testers.txt"
ENV_FILE="${HERE}/appdist.env"
for f in "$APK" "$NOTES" "$TESTERS" "$ENV_FILE"; do
    [ -f "$f" ] || { echo "✘ 없음: $f" >&2; exit 1; }
done
# shellcheck source=/dev/null
source "$ENV_FILE"
: "${FIREBASE_ANDROID_APP_ID:?appdist.env에 FIREBASE_ANDROID_APP_ID=… 줄이 없습니다}"
for attempt in 1 2 3; do
    if npx -y firebase-tools@15 appdistribution:distribute "$APK" \
        --app "$FIREBASE_ANDROID_APP_ID" \
        --release-notes-file "$NOTES" \
        --testers-file "$TESTERS"; then
        echo "✔ App Distribution: ${VERSION}"
        exit 0
    fi
    echo "… 다시 시도 (${attempt}/3)" >&2
    sleep 5
done
echo "✘ App Distribution 업로드 실패 (firebase login이 되어 있는지 확인)" >&2
exit 1
```

Run: `chmod +x ~/Work/.mc-release/appdist.sh && bash -n ~/Work/.mc-release/appdist.sh && bash -n ~/Work/.mc-release/apk-local.sh`
Expected: no output.

- [ ] **Step 3: 사용자 설정 C — App Distribution과 로그인 (사용자가 직접)**

Tell the user these steps:
1. Firebase 콘솔 → App Distribution → 시작하기(Android 앱 `dev.mightyclaude.mobile`). 테스터 그룹은 쓰지 않아도 된다.
2. 프로젝트 설정 → 일반 → Android 앱의 **앱 ID**(`1:…:android:…`)를 복사해 `~/Work/.mc-release/appdist.env`에 한 줄로 둔다: `FIREBASE_ANDROID_APP_ID=<복사한 값>`.
3. 테스터 주소를 쉼표로 이어 `~/Work/.mc-release/appdist-testers.txt`에 둔다. 이 두 파일은 저장소에 넣지 않는다.
4. 터미널 창에서 직접 `npx -y firebase-tools@15 login`을 실행하고 브라우저로 로그인한다. 에이전트는 이 프롬프트를 몰지 않는다(필요하면 `run_in_terminal`로 열어 사용자가 답하게 한다).
5. 확인: `npx -y firebase-tools@15 projects:list`에 그 프로젝트가 보인다.

- [ ] **Step 4: 릴리즈 순서에 넣기**

A release now runs, after `apk-local.sh` and `upload.sh <V> apk`: `bash ~/Work/.mc-release/appdist.sh <V>`. The memory note for releases (`cloudflare-release.md`) gets one line: "APK도 App Distribution으로: appdist.sh <V>(로컬 appdist.env·appdist-testers.txt, firebase login 필요)".

---

## Task 21: 전체 검증과 기기 확인

**Files:** 없음(검사만). 실패하면 그 태스크로 돌아가 고치고 거기서 커밋한다.

- [ ] **Step 1: Relay.** Run `cd /Users/young/Work/MightyClaude-firebase/relay && npm test && npx tsc --noEmit`. Expected: every suite passes (`relay`, `push-store`, `fcm`, `push`), tsc silent.

- [ ] **Step 2: Mac.** Run `cd /Users/young/Work/MightyClaude-firebase && DEVELOPER_DIR=/Library/Developer/CommandLineTools bash scripts/test-native-macos.sh --scratch-path /tmp/mc-fb --filter '\.(MobilePushSealTests|MobilePushPlannerTests|MobilePushRelayTests|MobileRemoteTests|MobileRemoteStructureTests|MobileDeviceRegistryTests|AntigravityPermissionTests|WorkDashboardTests|ScreenShareEngineTests)/'`, then MAC-BUILD. Expected: all pass; `Build complete!`. Nothing is launched.

- [ ] **Step 3: Windows.** Run `bash ~/Work/MightyClaude-wincatch/.superpowers/sdd/bin/core-checks.sh /Users/young/Work/MightyClaude-firebase 'mobile push|Mobile remote|Mobile host|antigravity cards'` and `cd /Users/young/Work/MightyClaude-firebase && bash scripts/check-winui-compile.sh`. Expected: `RESULT: BASELINE`, every `mobile push:` line PASS; `WinUI: C# 컴파일 오류 없음`.

- [ ] **Step 4: Phone.** Run `cd /Users/young/Work/MightyClaude-firebase/mobile && npm run typecheck && npx jest`. Expected: clean typecheck; every suite passes.

- [ ] **Step 5: Repository checks.** Run from the repo root:
  - `node scripts/check-locales.js && node scripts/check-locales.js --check`
  - `node scripts/check-design-tokens.js --check`
  - `node scripts/help/build.mjs --check`
  - `bash scripts/check-style-freeze.sh`

  Expected: all exit 0; the style freeze prints OK (no frozen path changed).

- [ ] **Step 6: Public-repo hygiene.** Run `git diff origin/main -- . ':(exclude)docs/superpowers' | grep -nE '/Users/|C:\\\\Users\\\\|@gbike|BEGIN (RSA )?PRIVATE KEY|"private_key"|AIza[0-9A-Za-z_-]{20}|client_email"\s*:\s*"[^"]*iam' ; git ls-files | grep -E 'google-services\.json|fcm-service-account'`
  Expected: no output. The only `private_key` text is in `relay/test/fcm.test.ts`, which builds it from a key generated at run time, not a literal; the pattern looks for the quoted JSON key with a literal value. If that test line matches, check it by eye and go on.

- [ ] **Step 7: A real APK (after 사용자 설정 B).** Run in the background, since it takes minutes: `cd /Users/young/Work/MightyClaude-firebase/mobile && JAVA_HOME=$(/usr/libexec/java_home -v 17) GOOGLE_SERVICES_JSON=$HOME/Work/.mc-release/google-services.json npx eas-cli build -p android --profile preview --local --non-interactive --output /tmp/mc-fb.apk`. Expected: the build ends with the APK at `/tmp/mc-fb.apk`. This only builds; it installs and launches nothing. If the Kotlin version stops the Firebase modules, check that RNFB is 26.4.x, not 27.

- [ ] **Step 8: The live relay (after 사용자 설정 A and a deploy of this branch's relay).** Run `node relay/deploy/oracle/push-check.mjs mightyclaude.duckdns.org`. Expected: `✔ 푸시 경로: 호스트 인증 401, FCM까지 가서 가짜 토큰을 410으로 지움`.

- [ ] **Step 9: 사용자 기기 확인 목록 (사용자가 직접; 결과를 받아 기록한다)**
  1. Install `/tmp/mc-fb.apk` on an Android 13+ phone and open it. It asks for notifications once. Open 설정 (호스트 tab, bottom): six switches and crash reports, all on.
  2. Pair (or reconnect) with a Mac running this build, phone remote on. In a Claude pane, start a run that asks for a Bash permission, with the phone locked and the app closed. A notification arrives with 허용 and 거절.
  3. On the lock screen, tap 허용. Android asks to unlock, or the notification says "잠금을 푼 뒤 다시 누르세요". After unlocking, tap 허용 again; the notification says 허용함, and the Mac's card is gone.
  4. Do it again, but answer on the Mac first, then tap 거절 on the phone: "이미 처리됨".
  5. An Antigravity pane whose card has a chained command shows 이번만 허용 and 거절 only; a plain command shows 항상 허용 too.
  6. Run to completion with the phone showing another pane: a "완료" notification. Run again with the phone showing that pane: none.
  7. Tap a notification: the app opens on that session.
  8. On Windows (after the Windows build of this branch): steps 2 and 6.
  9. Turn off 작업 완료 in settings: a finished run sends nothing.
  10. Over a direct Tailscale connection only, step 6 still notifies.
  11. A crash report: deliberately not tested on a user device. The first real crash shows in the Crashlytics dashboard with no message text.
  12. Firebase App Distribution: the tester gets the release e-mail for the version uploaded by `appdist.sh`.

---

## 사용자 설정 단계 모음

| 단계 | 언제(어느 태스크) | 무엇을 | 막는 것 |
|---|---|---|---|
| A | Task 4 Step 9 | Firebase 서비스 계정 키(FCM API 사용 설정) → 릴레이 서버 `relay/deploy/oracle/fcm-service-account.json` → `setup.sh` 다시 실행 → `push-check.mjs` | Task 21 Step 8과 실제 알림 도착 |
| B | Task 14 Step 8 | Firebase 콘솔에 Android 앱 `dev.mightyclaude.mobile` 등록 → `google-services.json`을 `~/Work/.mc-release/`에 둔다. Crashlytics 시작하기 | Task 21 Step 7(APK 빌드) |
| C | Task 20 Step 3 | App Distribution 시작하기, `appdist.env`(앱 ID), `appdist-testers.txt`, 터미널 창에서 `npx -y firebase-tools@15 login` | 릴리즈의 App Distribution 업로드 |

---

## Self-Review Checklist (spec `docs/superpowers/specs/2026-10-23-firebase-push-design.md` → tasks)

| 설계 항목 | Task |
|---|---|
| Firebase 프로젝트에 Android 앱 `dev.mightyclaude.mobile` 등록 | 14 (사용자 설정 B) |
| 릴레이가 푸시를 보내고 서비스 계정 키는 릴레이에만 | 2, 3, 4 (사용자 설정 A) |
| 호스트가 폰 키로 봉인, 릴레이·Google은 못 읽음(데이터 메시지) | 5, 8, 10, 12, 13; FCM 데이터 전용은 2 |
| 푸시 핸들(추측 불가), 토큰 갱신 시 같은 핸들 | 1, 3, 15 |
| 핸들은 페어링된 호스트에게만 m1로 | 7, 11, 15 |
| 첫 serverId에 묶기, 다른 serverId 거절 | 1, 3 |
| 페어링 삭제 시 핸들 삭제, FCM 무효 토큰 시 릴레이가 삭제 | 3, 8, 12, 15 |
| HKDF `mighty-push-v1` + AEAD, 릴레이·직접 연결 같은 키, 공유 픽스처 | 5, 10, 13 |
| 평문 내용과 4KB 한도 | 6, 10, 13 (2048B 평문, 3072자 봉투) |
| 이벤트 6종(완료·실패·권한·질문·계획·로그인) + Antigravity 카드 | 6, 9, 10, 12 |
| 이벤트 id로 중복 제거 | 6, 10 |
| 보고 있는 세션은 보내지 않음(짧은 TTL) | 6, 7, 11, 12, 17, 18 |
| 릴레이 실패 시 짧게 재시도 후 버림, 진단 기록만 | 8, 12 |
| Tailscale 직접 연결 폰도 릴레이로 알림 | 5(같은 키), 8, 19 |
| 백그라운드 처리기 복호화·로컬 알림, 못 열면 버림 | 13, 18 |
| 버튼은 잠금 해제 후에만, 기존 응답 경로(Antigravity `always`) | 14 (device-lock), 16, 18 |
| 실패·시간 초과 → 세션 열기, 성공 → 허용함/거절함 | 16, 18 |
| 탭 → 호스트·세션 화면 | 16 (`sessionPath`), 18 |
| 409 → 이미 처리됨 | 16, 18 |
| 설정: 종류별 켜기/끄기(기본 켬), Android 13 권한 한 번 | 17, 18 |
| Crashlytics: 네이티브·JS, 사용자 내용 없음, 끌 수 있음(기본 켬) | 14, 17, 18 |
| App Distribution 함께 쓰기, 테스터는 로컬에만 | 20 (사용자 설정 C) |
| 보안: 키·토큰은 릴레이에만, google-services.json 로컬, 한도 | 3, 4, 14 |
| 테스트: 릴레이 단위·가짜 전송기 / 암호 왕복·변조 / 호스트 이벤트·중복·보고 있음 / 폰 jest / 기기 확인 | 1–3 / 5, 10, 13 / 6–8, 10–12 / 13–18 / 21 |
| 범위 밖: iOS 푸시, 데스크톱 Crashlytics, 알림에서 질문 답하기 | 다루지 않음(질문·계획 알림은 세션을 연다) |
