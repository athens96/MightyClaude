# 앱 자체 업데이트

앱이 Cloudflare에 올려 둔 `latest.json`을 읽어 자기 버전과 비교하고, 더 높은 버전이 있으면 패키지를 받아 검증한 뒤 교체한다. 교체는 앱이 종료된 뒤에만 일어난다(실행 중인 번들을 바꾸면 입력기 등이 깨진다). 설정 › 앱 업데이트의 "새 버전을 자동으로 받아 설치" 스위치(기본 켬, defaults 키 `appUpdate.automatic`)가 켜져 있으면 확인·받기·설치까지 앱이 스스로 한다([자동 업데이트](#자동-업데이트)). Windows도 같은 규칙이다(`docs/windows-app-update.md`).

## 흐름

1. **확인** — 설정 › 앱 업데이트 › "업데이트 확인", 또는 스위치가 켜져 있으면 자동으로(마지막 확인에서 6시간 뒤, 아래). `latest.json`을 캐시 없이 받아 `version`을 앱의 `CFBundleShortVersionString`과 비교한다. 스위치가 꺼져 있을 때 새 버전이 있으면 상태 표시줄에 "새 버전 x.y.z" 배지가 뜨고, 누르면 설정이 열린다. 스위치가 켜져 있으면 확인이 곧바로 다운로드로 이어진다.
2. **다운로드** — 이미 같은 버전이 준비되어 있으면 다시 받지 않는다. `macos.url`의 zip을 `<상태 저장 위치>/updates/<버전>/MightyClaude-macos.zip`에 받는다(다른 버전 폴더는 지운다). 디스크에 쓰인 파일의 `size`·`sha256`을 대조하고, 다르면 지운다. 512 MiB 상한. `minimumSystemVersion`보다 macOS가 낮으면 확인 단계에서 안내하고 받지 않는다.
3. **준비** — zip을 풀어 `.app`이 하나인지, 심볼릭 링크가 아닌지, `CFBundleIdentifier`가 `dev.mightyclaude.native`인지, 실행 파일이 있는지 확인한다.
4. **설치** — 세 가지 길이 있다: 사용자가 누르는 "지금 다시 시작"(설정·상태 표시줄), 쉬는 동안의 60초 카운트다운이 끝남(둘 다 `relaunch: true`), 앱을 종료함(`relaunch: false`). 준비된 번들과 패키지의 `size`·`sha256`을 한 번 더 확인한 뒤(해시는 메인 스레드 밖에서) 도우미 셸 스크립트(`AppUpdateService.installScript`)를 `updates/<버전>/install.sh`에 쓰고 최소 환경(PATH=/usr/bin:/bin)으로 분리 실행한다. 도우미는 다음 순서로 일한다.
   1. 설치 위치에 앱이 없고 `<앱>.update-old`만 있으면(이전 도우미가 두 이름 바꾸기 사이에서 끊김) 그것을 먼저 제자리로 되돌린다.
   2. 앱 프로세스가 끝날 때까지 기다린다(최대 5분, 넘으면 건드리지 않고 멈춘다). 끝나면 2초 더 기다린다.
   3. 새 번들을 `<앱>.update-new`로 `ditto` 복사한다.
   4. `relaunch: false`이면 여기서 앱이 같은 경로에서 다시 떴는지 `pgrep`으로 본다. 명령줄이 `<앱>/Contents/MacOS/MightyClaude`로 시작하는 프로세스가 있으면 복사본을 지우고 업데이트를 다음 종료로 미룬다. 앱 자신의 헤드리스 모드(`--agent-io-mcp`, `--agent-delegation-mcp`, `--verify-resources`)는 창보다 잠깐 오래 남을 수 있고 번들을 붙잡지 않으므로 세지 않는다.
   5. HUP·INT·TERM을 무시한 채 두 번 이름을 바꾼다: 설치된 앱을 `<앱>.update-old`로 옮기고, `<앱>.update-new`를 제자리로 옮긴다. 두 번째가 실패하면 `.update-old`를 되돌린다.
   6. 성공하면 `.update-old`를 LaunchServices에서 등록 해제하고 지운다. 준비 폴더도 등록 해제하고 지운 뒤, 설치된 앱만 다시 등록한다.
   7. `relaunch: true`면 새 앱을 연다(`open -n`). `relaunch: false`면 열지 않는다.

   로그는 같은 폴더의 `install.log`. 실행 중인 앱의 위치에 쓸 수 없으면 도우미를 띄우기 전에 멈춘다.

## 자동 업데이트

규칙은 Core `AppUpdateAuto`(`AppUpdateAuto.swift`, `AppUpdateAutoSteps.swift`)에 있고 앱(`AppStore+AppUpdate.swift`)은 그 판단을 따른다. Windows는 같은 상수와 이름을 `MightyClaude.Core/AppUpdateAutoPolicy.cs`에 둔다. 모두 "새 버전을 자동으로 받아 설치" 스위치 뒤에 있다.

| 값 | |
|---|---|
| 확인 주기 | 6시간(`checkInterval`) |
| 확인할 때가 됐는지 보는 간격 | 10분(`tick`) |
| 키보드·마우스 유휴 | 5분(`userIdleDelay`) |
| 에이전트 유휴(마지막 실행 종료나 보낸 입력) | 3분(`agentIdleDelay`) |
| 카운트다운 | 60초(`countdown`), 1초마다 다시 판단 |
| "나중에" | 4시간(`snooze`) |

1. **확인** — 앱을 열 때와 10분마다 `checkDue`를 묻는다. 저장된 마지막 성공 확인(`appUpdate.lastCheck`)과 이번 실행의 마지막 시도(메모리) 가운데 늦은 쪽에서 6시간이 지났으면 확인한다. 실패한 확인은 다음 주기까지 기다리고, 실패는 설정 행에만 보인다. 미래 시각(시계를 뒤로 돌림)은 무시한다.
2. **받기** — 새 버전이 있으면 묻지 않고 받아 위와 같이 검증·준비한다(`afterCheck`). 같은 버전이 이미 준비되어 있거나, 더 새 버전이 이 Mac의 macOS에서 돌지 않으면 준비된 업데이트를 그대로 둔다. 확인이 실패해도 준비된 업데이트(`.ready`)는 버리지 않는다.
3. **표시** — 준비되면 상태 표시줄과 설정에 "다음 실행 때 {version} 설치"와 "지금 다시 시작" 단추가 보인다.
4. **종료할 때 설치** — 앱을 정상 종료하면 `shutdown()` 맨 앞에서 도우미를 `relaunch: false`로 띄운다(`installsOnQuit`). 종료 Apple 이벤트의 `kAEQuitReason`이 없거나 보통 종료(`kAEQuitApplication`)일 때만 정상 종료다. 그 밖의 이유는 로그아웃·재시동·시스템 종료로 본다(`quitReasonPowersOff`). 알 수 없거나 읽히지 않는 이유도 여기에 들어간다. 이때는 교체가 중간에 끊길 수 있으므로 설치하지 않고 다음 종료를 기다린다. 이 판단은 종료하는 순간에 하므로, 사용자가 취소한 로그아웃은 다음 종료의 설치를 막지 않는다. 앱은 이런 종료에 `.terminateLater`로 답하고 정리가 끝나면 `reply(toApplicationShouldTerminate: true)`로 답한다. 그래서 로그아웃을 취소하지 않는다. 정리가 멈춰도 메인 런루프의 `.common` 모드 타이머가 6초(`terminationDeadline`) 뒤에 답을 보낸다. 답을 기다리는 동안 두 번째 종료가 와도 거절하지 않는다(`.terminateLater`).
5. **쉴 때 다시 시작** — 업데이트가 준비되어 있고 막는 사유가 하나도 없으면 창 위에 60초 카운트다운 배너("{version} 업데이트를 설치하려고 {seconds}초 뒤 다시 시작합니다", "나중에"·"지금 다시 시작")를 띄운다(`restartOffer`). 시간이 다 되면 다음 순서로 진행한다.
   - 패키지를 메인 스레드 밖에서 다시 검증한다. 그동안 설정과 상태 표시줄은 업데이트를 계속 준비됨으로 보인다.
   - 종료 바로 앞에서, 한 번의 메인 액터 차례 안에 쉬는 상태인지 다시 묻는다. 입력만 있는 것은 카운트다운 때처럼 괜찮다.
   - 그사이 무언가 시작됐으면 업데이트를 준비 상태로 되돌리고, 다음에 쉴 때 새 카운트다운을 띄운다.
   - 아무것도 시작되지 않았으면 `relaunch: true`로 설치하고 다시 연다.

   끝난 지 5초(`countdownGrace`)가 넘은 카운트다운은 끝까지 지켜보지 않은 것이다(예: Mac이 그동안 잠들어 있었다). 이런 카운트다운은 실행하지 않고 취소하므로, 깨어나자마자 다시 시작하는 일은 없다. 카운트다운 중에 `userActive`가 아닌 사유가 생기거나, 스위치가 꺼지거나, 업데이트가 더는 준비 상태가 아니면 배너는 조용히 사라진다. 키보드·마우스 입력만으로는 취소하지 않는다(배너 단추를 누를 수 있게). "나중에"는 4시간 동안 배너를 띄우지 않고, 그사이 종료하면 4번으로 설치된다.
6. **스위치를 끄면** 루프·다운로드·카운트다운을 멈춘다. 준비된 업데이트는 "지금 다시 시작"으로만 설치되고 종료할 때는 설치하지 않는다. 설정은 그 업데이트가 준비되어 있다고 알린다.
7. **예외** — 공개 키나 manifest 주소가 없는 빌드, 스모크 테스트, 도움말 캡처에서는 자동으로 아무것도 하지 않는다.
8. **정리** — 앱을 열 때 `updates/` 안에서 실행 중인 버전과 같거나 낮은 버전 폴더를 지운다(`staleStagedFolders`). 교체 뒤 도우미가 끊겨 남은 사본이거나 더 오래된 버전이다.

### 쉬고 있음

`AppUpdateAuto.idleBlockers(_:now:)`가 아래 사유를 돌려주면 쉬는 상태가 아니다(`AppIdleFacts`를 앱이 채운다).

| 사유 | 앱에서 읽는 곳 |
|---|---|
| `agentRun` | 셸이 아닌 실행 중 세션(원격 포함), 시작을 기다리는 실행, 업데이트로 붙잡힌 실행, 끝을 기록 중인 위임 실행, 시작을 기다리는 위임 |
| `pendingCard` | 권한 요청, Antigravity 권한 카드, 웹 페이지 열기 요청 |
| `queuedInput` | 비지 않은 대기열(위임이 붙잡은 항목은 디스크에 남으므로 제외) |
| `draft` | 공백이 아닌 입력창 초안, 첨부 초안, 가져오는 중인 첨부 |
| `terminal` | 실행 중인 셸 세션, 프롬프트에 있어도 백그라운드·정지된 작업(자식 프로세스)을 붙잡은 셸, 에이전트 터미널의 살아 있는 프로세스, setup 실행·터미널, 진행 중인 CLI 로그인 |
| `cliWork` | CLI 업데이트, 플러그인 관리, 모델 리셋, 도구 설치 |
| `remote` | 화면 공유, 휴대폰 업로드, 이 Mac이 호스트로 돌리는 원격 실행, 켜져 있는 워크스페이스 공유(돌고 있는 원격 실행이 없어도) |
| `modal` | 막는 모달, setup 시트, 열린 설정, 시트가 붙은 창 |
| `userActive` | 로그인 세션 어디서든 키보드·마우스 입력이 5분 안에 있었음 |
| `recentAgentActivity` | 마지막 실행 종료나 에이전트에 보낸 입력이 3분 안 |

주소는 https만 받는다. 패키지 주소도 https여야 하며, 그렇지 않은 항목은 무시한다. 리디렉션도 https 밖으로 나가면 끊는다(앱은 ATS를 꺼 두고 있어 이 검사를 직접 한다).

## 서명

`latest.json`이 신뢰의 뿌리이므로 Ed25519로 서명한다. 배포 빌드에는 공개 키를 넣고(`MIGHTY_UPDATE_PUBLIC_KEY`, Info.plist `MightyUpdatePublicKey`), 그 앱은 **서명된 봉투만** 받는다. **공개 키가 없는 빌드는 업데이트 확인을 전혀 하지 않는다** — `check()`가 즉시 오류를 반환하고, 자동 확인은 예약되지 않으며, 설정 화면에 "이 빌드는 업데이트 확인을 지원하지 않습니다"가 표시되고 확인 버튼은 비활성화된다. 우회 경로는 없다.

```bash
# 한 번만: 키 쌍 생성 (개인 키는 CI 시크릿 MIGHTY_UPDATE_SIGNING_KEY로, 공개 키는 빌드 변수로)
python3 scripts/make-update-manifest.py --generate-key ~/.config/mightyclaude/update-signing.key
# 릴리스마다: 서명된 latest.json (+ 사람이 읽을 latest.unsigned.json)
python3 scripts/make-update-manifest.py --macos release/MightyClaude-macos.zip \
  --base-url https://<cloudflare>/mightyclaude/0.2.0 --out release/latest.json \
  --sign-key ~/.config/mightyclaude/update-signing.key
```

서명된 파일은 봉투 형식이다. 앱은 `payload`(평문 manifest의 base64)의 바이트 그대로에 대한 서명을 검증한 뒤 안의 JSON을 읽는다.

```json
{ "format": "mightyclaude-update-v1", "version": "0.2.0", "payload": "<base64 JSON>", "signature": "<base64 Ed25519>" }
```

서명은 `scripts/update-manifest-sign.swift`(CryptoKit)로 하며 처음 쓸 때 `swiftc`로 컴파일된다. 별도 Python 패키지가 필요 없다.

## latest.json 형식

```json
{
  "version": "0.2.0",
  "build": 57,
  "publishedAt": "2026-09-18T09:00:00Z",
  "minimumSystemVersion": "14.0",
  "notes": "무엇이 바뀌었는지 (설정 화면에 표시)",
  "macos": { "url": "https://<cloudflare>/mightyclaude/0.2.0/MightyClaude-macos.zip", "sha256": "<hex 64자>", "size": 26395344 },
  "windows": {
    "x64":   { "url": "https://<cloudflare>/mightyclaude/0.2.0/MightyClaude-native-windows-x64.zip", "sha256": "…", "size": 0 },
    "arm64": { "url": "…", "sha256": "…", "size": 0 }
  }
}
```

`version`은 `1.2.3` 같은 숫자 버전(선택적으로 `-beta.1` 같은 프리릴리스, 앞의 `v` 허용). **`sha256`·`size`는 필수**다 — 둘 중 하나라도 없으면 해당 asset이 파싱 단계에서 거부된다(다운로드 전). `"macos": "https://…zip"` 같은 문자열 형태는 sha256·size를 담을 수 없으므로 거부된다. `scripts/make-update-manifest.py`가 이 두 값을 자동으로 채운다. `latest.unsigned.json`(CI 아티팩트에 포함됨)은 서명 검증·다운로드 테스트용 개발 보조 파일이며, 앱은 이 파일을 절대 소비하지 않는다.

`scripts/make-update-manifest.py`가 이 파일을 만든다.

```bash
python3 scripts/make-update-manifest.py --macos release/MightyClaude-macos.zip \
  --base-url https://<cloudflare>/mightyclaude/0.2.0 --out release/latest.json
```

## 버전과 주소를 빌드에 넣기

- 저장소 루트 `VERSION` 파일이 앱 버전이다(또는 `MIGHTY_APP_VERSION`). 빌드 번호는 커밋 수(또는 `MIGHTY_BUILD_NUMBER`). `scripts/build-macos.sh`가 Info.plist에 써 넣는다.
- `MIGHTY_UPDATE_URL`을 주면 `MightyUpdateManifestURL`로 Info.plist에 들어가 **유일한** 주소가 된다. 빌드에 이 값이 있으면 설정 화면에 읽기 전용으로 표시되며, 사용자 입력 칸은 나타나지 않는다. 빌드에 없을 때만 설정 화면에서 주소를 입력할 수 있다.
- GitHub Actions는 저장소 변수 `MIGHTY_UPDATE_URL`(앱이 확인할 latest.json 주소), `MIGHTY_UPDATE_PUBLIC_KEY`(공개 키), `MIGHTY_DOWNLOAD_BASE`(패키지를 올릴 폴더의 루트 주소, 여기에 `/<버전>`이 붙는다)와 시크릿 `MIGHTY_UPDATE_SIGNING_KEY`(개인 키 파일 내용)를 읽는다. `MIGHTY_DOWNLOAD_BASE`가 없으면 manifest 단계를 건너뛰고, 있으면 macOS 아티팩트에 `MightyClaude-macos.zip`, `latest.json`, `latest.unsigned.json`을 담는다.

## 크기 게이트

앱은 512 MiB보다 큰 패키지를 아예 받지 않으므로(`AppUpdate.maximumPackageBytes`), 그보다 커진 릴리스는 기존 설치본이 업데이트할 수 없는 죽은 패키지가 된다. 그래서 **패키징 직후, 아무것도 올리기 전에** 480 MiB 게이트를 돌린다. 번들 브라우저 엔진과 WebRTC 프레임워크가 릴리스마다 조금씩 커져도 다음 빌드가 갑자기 설치 불가가 되지 않도록 512 MiB 상한보다 일찍 막는다.

```bash
ditto -c -k --sequesterRsrc --keepParent release/native-macos/MightyClaude.app release/MightyClaude-macos.zip
scripts/check-release-size.sh release/MightyClaude-macos.zip   # 480 MiB를 넘으면 0이 아닌 코드로 끝난다
```

인자 없이 부르면 `release/MightyClaude-macos.zip`을 본다. 게이트가 실패하면 올리지 않고 패키지를 줄인다(번들 리소스·프레임워크부터). `.github/workflows/native-macos.yml`도 패키징 단계와 아티팩트 업로드 사이에서 같은 스크립트를 돌린다. `scripts/tests/test-release-size-gate.sh`가 479 MiB·481 MiB 희소 파일로 이 경계를 확인한다.

## Cloudflare에 올리기

R2 공개 버킷이나 Pages 어느 쪽이든 같은 폴더 구조면 된다.

```
mightyclaude/latest.json                      ← MIGHTY_UPDATE_URL
mightyclaude/0.2.0/MightyClaude-macos.zip     ← latest.json의 macos.url
```

새 버전을 낼 때: `VERSION`을 올리고 → 빌드·패키징 → `scripts/check-release-size.sh`로 크기 게이트 통과 → `make-update-manifest.py`로 `latest.json` 생성 → 패키지를 버전 폴더에, `latest.json`을 고정 위치에 업로드. `latest.json`은 캐시가 길게 잡히지 않도록 `Cache-Control: no-cache` 정도로 둔다.

## 한계

- 앱은 ad-hoc 서명이라 Gatekeeper 격리 속성이 붙으면 실행이 막힐 수 있다. 앱이 직접 내려받은 파일에는 격리 속성이 붙지 않으므로 도우미는 속성을 건드리지 않는다.
- `updates/` 폴더에는 마지막으로 받은 버전 하나만 남는다.
- 같은 번들 ID의 앱 복사본(빌드 결과물, 백업)이 LaunchServices에 여러 개 등록되면 입력기 세션이 실행 중인 앱을 다른 번들로 볼 수 있다(한글 자소 분리). 도우미는 옛 번들을 `/tmp`에 백업하지 않고 설치 위치 옆 `<앱>.update-old`로 이름만 바꿔 두었다가, 교체가 끝나면 등록 해제하고 지운다. 준비 폴더의 번들도 등록 해제하고 설치된 앱만 다시 등록하며, 옛 프로세스가 사라진 뒤 2초 기다렸다가 다시 연다. 개발용 `scripts/install-macos.sh`는 여전히 `/tmp/MightyClaude-app-backup-*/MightyClaude.app.bak`에 백업을 두 개까지 남긴다(`.bak`이라 LaunchServices가 앱으로 보지 않는다). `scripts/build-macos.sh`도 빌드 결과물을 등록 해제한다.
- 개발용 `scripts/install-macos.sh`와 종료할 때 설치하는 도우미가 서로 겹치지 않게 한다. 스크립트는 앱이 끝난 뒤 다음 순서로 일한다.
  - `<상태 저장 위치>/updates/` 아래 `install.sh`를 돌리는 도우미가 있으면 그것이 끝날 때까지 기다린다.
  - 준비된 업데이트(`updates/*`)를 지운다.
  - 그다음 개발 빌드를 복사한다.

  그래서 준비된 릴리스가 개발 빌드를 덮어쓰지 않는다. 지금 덮어쓰는 일도, 다음 종료 때 덮어쓰는 일도 없다. `--profile` 폴더를 쓰면 `MIGHTY_DATA_DIR`로 그 폴더를 알려 준다(기본 `~/Library/Application Support/MightyClaude Native`).
- 실행 중인 앱의 위치에 쓸 수 없으면(예: 읽기 전용 볼륨) 설치 단계에서 안내하고 멈춘다.
- `pgrep` 비교는 경로를 바이트 그대로, 대소문자를 구분해 맞춘다. APFS는 대소문자와 유니코드 정규화를 접으므로 다른 철자의 경로(`/applications/…`, 분해된 악센트)로 다시 띄운 앱은 보지 못하고, 그때는 이 검사가 생기기 전처럼 교체된다.
- 스모크 테스트 프로필과 도움말 캡처에서는 자동 확인·받기·설치를 하지 않는다.
