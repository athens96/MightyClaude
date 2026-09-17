# MightyClaude Mobile

데스크톱 MightyClaude를 휴대폰에서 원격 조종하는 React Native(Expo) 클라이언트입니다.
iOS / Android 모두 **Expo Go**에서 바로 실행됩니다. (커스텀 네이티브 모듈 없음)

- 페어링된 호스트 목록 / 연결 상태 확인
- 작업 공간별 세션 목록 (상태 칩, 권한 대기 배지, 새 창 만들기)
- 세션 상세: 대화 기록, 권한 허용/거부, 질문 답변, 대기열, 메시지 전송/중지
- 서버 프로토콜: `m1` (protocol 1), 기본 포트 `43138`

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

## 2. Tailscale 준비 (필수)

이 앱은 데스크톱 MightyClaude가 여는 HTTP 서버에 **직접** 접속합니다.
따라서 휴대폰과 데스크톱이 **같은 tailnet**에 있어야 합니다.

1. 데스크톱에 Tailscale 설치 및 로그인
2. 휴대폰에 Tailscale 앱 설치 후 **같은 계정**으로 로그인 → VPN 연결 ON
3. 데스크톱의 Tailscale IP(`100.x.y.z`)를 확인
4. 데스크톱 MightyClaude에서 모바일 연결(서버)을 켜기

Tailscale이 연결되지 않으면 호스트 목록에 **연결 불가**로 표시됩니다.

---

## 3. 페어링

데스크톱 MightyClaude가 아래 형식의 페어링 문자열을 QR로 표시합니다.

```
mightyclaude://pair?v=1&host=<ip>&port=<port>&key=<key>&name=<이름>
```

앱에서:

1. 첫 화면 하단 **+ 호스트 추가**
2. **QR 스캔** 탭에서 카메라 권한을 허용하고 데스크톱 화면의 QR을 비춥니다.
   - 카메라를 쓸 수 없으면 **직접 입력** 탭에서 호스트 / 포트 / 키 / 이름을 넣습니다.
3. 앱이 `GET /m1/info`로 검증한 뒤 키를 **expo-secure-store**(iOS 키체인 / Android Keystore)에 저장합니다.

호스트를 지우려면 목록에서 **길게 누르기** → 삭제.

`재페어링 필요`가 보이면 저장된 키가 더 이상 유효하지 않은 것입니다(HTTP 401).
호스트를 삭제하고 새 QR로 다시 페어링하세요.

---

## 4. 사용 중 참고

- 화면이 열려 있는 동안만 롱폴링(`?since=&wait=10`)합니다. 다른 화면으로 나가면 요청이 중단되어 배터리를 아낍니다.
- 네트워크 오류 시 1초 → 10초까지 지수 백오프로 재시도합니다.
- 메시지 전송 결과는 토스트로 표시됩니다: `전송` / `실행 중인 작업에 전달` / `대기열에 추가`.
- 전송 가능한 텍스트는 최대 32KiB입니다.

---

## 5. 제한 사항

- **로컬 터미널 창**(`terminal: true`)에는 모바일에서 명령을 보낼 수 없습니다. 읽기 전용입니다.
- 전송 기록은 서버가 주는 **최근 80건**만 표시됩니다. 과거 기록 무한 스크롤은 없습니다.
- 파일 첨부, 이미지 업로드, 세션 삭제/이름 변경은 지원하지 않습니다.
- 통신은 tailnet 내부 **평문 HTTP**입니다. 공개 네트워크에 포트를 노출하지 마세요.
- 푸시 알림은 없습니다. 앱이 백그라운드면 폴링이 멈춥니다.

---

## 6. 개발

```bash
npm run typecheck   # tsc --noEmit
npm test            # jest (순수 로직 단위 테스트)
```

주요 구조:

```
app/                     expo-router 화면
  _layout.tsx            루트 스택 + 다크 테마
  index.tsx              호스트 목록
  pair.tsx               QR/수동 페어링
  host/[hostId]/index.tsx                작업 공간 + 세션 목록
  host/[hostId]/session/[sessionId].tsx  세션 상세
src/
  api/       client.ts(타입 클라이언트), types.ts(프로토콜 타입)
  components/ UI 컴포넌트
  hooks/     use-long-poll.ts (AbortController + 포커스 연동)
  lib/       pairing.ts, merge.ts (순수 로직, 테스트 대상)
  store/     hosts.ts(secure store), live.ts(상태 캐시), toast.ts
  theme/     색상 / 간격 / 상태 라벨
```
