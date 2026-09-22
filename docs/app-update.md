# 앱 자체 업데이트

앱이 Cloudflare에 올려 둔 `latest.json`을 읽어 자기 버전과 비교하고, 더 높은 버전이 있으면 패키지를 받아 검증한 뒤 교체한다. 교체는 앱이 종료된 뒤에만 일어난다(실행 중인 번들을 바꾸면 입력기 등이 깨진다).

## 흐름

1. **확인** — 설정 › 앱 업데이트 › "업데이트 확인", 또는 앱 시작 시 하루 한 번 자동(끌 수 있음). `latest.json`을 캐시 없이 받아 `version`을 앱의 `CFBundleShortVersionString`과 비교한다. 새 버전이 있으면 상태 표시줄에 "새 버전 x.y.z" 배지가 뜬다. 자동 확인은 다운로드까지 하지 않는다.
2. **다운로드** — `macos.url`의 zip을 `<상태 저장 위치>/updates/<버전>/MightyClaude-macos.zip`에 받는다(다른 버전 폴더는 지운다). 디스크에 쓰인 파일의 `size`·`sha256`을 대조하고, 다르면 지운다. 512 MiB 상한. `minimumSystemVersion`보다 macOS가 낮으면 확인 단계에서 안내하고 받지 않는다.
3. **준비** — zip을 풀어 `.app`이 하나인지, 심볼릭 링크가 아닌지, `CFBundleIdentifier`가 `dev.mightyclaude.native`인지, 실행 파일이 있는지 확인한다.
4. **설치하고 다시 실행** — 준비된 번들을 한 번 더 확인한 뒤 도우미 셸 스크립트를 `updates/<버전>/install.sh`에 쓰고 최소 환경(PATH=/usr/bin:/bin)으로 분리 실행하고 앱을 종료한다. 도우미는 앱 프로세스가 끝날 때까지 기다렸다가(최대 5분) 새 번들을 먼저 `<앱>.update-new`로 복사하고, 현재 번들을 `/tmp/MightyClaude-app-backup-<시각>/`에 백업한 뒤, 한 번의 이동으로 바꾸고 다시 연다. 이동이 실패하면 백업을 되돌리고, 성공하면 백업과 준비 폴더를 지운다. 로그는 같은 폴더의 `install.log`.

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

## Cloudflare에 올리기

R2 공개 버킷이나 Pages 어느 쪽이든 같은 폴더 구조면 된다.

```
mightyclaude/latest.json                      ← MIGHTY_UPDATE_URL
mightyclaude/0.2.0/MightyClaude-macos.zip     ← latest.json의 macos.url
```

새 버전을 낼 때: `VERSION`을 올리고 → 빌드·패키징 → `make-update-manifest.py`로 `latest.json` 생성 → 패키지를 버전 폴더에, `latest.json`을 고정 위치에 업로드. `latest.json`은 캐시가 길게 잡히지 않도록 `Cache-Control: no-cache` 정도로 둔다.

## 한계

- 앱은 ad-hoc 서명이라 Gatekeeper 격리 속성이 붙으면 실행이 막힐 수 있다. 앱이 직접 내려받은 파일에는 격리 속성이 붙지 않으므로 도우미는 속성을 건드리지 않는다.
- `updates/` 폴더에는 마지막으로 받은 버전 하나만 남는다.
- 같은 번들 ID의 앱 복사본(빌드 결과물, 백업)이 LaunchServices에 여러 개 등록되면 입력기 세션이 실행 중인 앱을 다른 번들로 볼 수 있다(한글 자소 분리). 도우미와 `scripts/install-macos.sh`는 백업을 `MightyClaude.app.bak`으로 두고, 설치 후 복사본을 등록 해제하고 설치된 앱만 다시 등록하며, 옛 프로세스가 사라진 뒤 2초 기다렸다가 재실행한다. `scripts/build-macos.sh`도 빌드 결과물을 등록 해제한다.
- 실행 중인 앱의 위치에 쓸 수 없으면(예: 읽기 전용 볼륨) 설치 단계에서 안내하고 멈춘다.
- 스모크 테스트 프로필에서는 자동 확인을 하지 않는다.
