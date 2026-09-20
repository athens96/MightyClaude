# Windows 앱 자체 업데이트

Windows 클라이언트는 macOS와 **같은** 서명된 `latest.json`을 읽고, 자기 버전과 비교하고,
이 기기의 아키텍처에 맞는 패키지를 받아 검증한 뒤, 앱이 종료된 다음에 설치 폴더를 교체한다.
화면과 문구는 macOS와 같고(`AppUpdateSettingsView.swift`), **신뢰 규칙만 처음부터 더 엄격하다**.

공유 계약은 건드리지 않았다. `docs/app-update.md`(manifest 형식),
`scripts/make-update-manifest.py`(생성기), macOS 코드는 그대로다.
실제 manifest는 이미 `sha256`·`size`를 모두 써 넣으므로 아래 규칙을 이미 만족한다.

---

## Windows가 macOS보다 엄격한 네 가지

macOS `AppUpdate.swift`에는 2026-09-20 보안 검토에서 찾은 신뢰 결함 세 가지가 남아 있다.
Windows는 그것을 따라가지 않는다.

| # | Windows 규칙 | macOS 현재 동작 | 어디에 있나 |
|---|---|---|---|
| 1 | **공개 키가 없는 빌드는 업데이트 확인을 아예 하지 않는다.** 개발 빌드도 예외가 아니다. 섹션에는 상태 문장 한 줄(`이 빌드에는 업데이트 공개 키가 없어 업데이트 확인을 지원하지 않습니다.`)만 나오고 확인 버튼은 비활성이다. 네트워크 요청도 만들지 않는다. | 공개 키가 없으면 **평문 manifest도 받고** 화면에 "서명 검증 없음"만 표시한다 | `AppUpdateService.CheckAsync`(키가 null이면 즉시 거절), `AppUpdatePresentation.Describe`(섹션 비활성) |
| 2 | **`sha256` 또는 `size`가 없는 Windows 자산은 거절한다.** 둘 다 있고 `size > 0`이고 `sha256`이 소문자 hex 64자일 때만 자산으로 읽는다. 그 아키텍처는 "패키지 없음"이 된다. | `sha256`·`size`는 선택이고, 없으면 **검사를 건너뛴다** | `AppUpdateManifest.ParseAsset` |
| 3 | **빌드에 기본 manifest 주소가 있으면 사용자가 입력한 주소는 무시한다.** 주소 칸은 비활성으로 표시되고 빌드에 포함된 주소만 조회한다. | 입력한 주소가 **우선**한다 | `AppUpdateCoordinator.EffectiveManifestUrl` |
| 4 | **교체 직전에 패키지 `sha256`을 한 번 더 확인한다.** 다르면 설치 폴더를 건드리지 않고 멈춘다. | 도우미가 다시 확인하지 않고 그대로 교체한다 | `AppUpdateReplacement.VerifyBeforeSwapAsync`, `AppUpdateReplacement.RunAsync` |

`AppUpdateManifest.Parse`는 서명 봉투만 받는다. 평문 manifest, 다른 키로 만든 서명,
`payload`를 고친 봉투, 모르는 `format`, base64가 아닌 `payload`는 모두 거절한다.

---

## 서명 검증을 무엇으로 하는가

**결정: 클라이언트 안에 검증 전용 Ed25519 구현을 둔다** (`MightyClaude.Core/Ed25519Verify.cs`).

**이유**: .NET 10의 `System.Security.Cryptography`에는 이 앱이 빌드하는 세 대상
(Windows x64 · Windows arm64 · macOS) 어디에도 Ed25519 기본 제공 API가 없다.
ECDsa·ECDiffieHellman과 새 PQC 형식은 있지만 curve25519는 없다.
Windows CNG에도 세 대상 모두에서 쓸 수 있는 Ed25519 경로가 없다.
외부 패키지 하나로 해결할 수도 있지만, Mac에서 도는 Core.Tests가 함께 빌드해야 하므로
의존성을 늘리지 않고 작은 검증 전용 구현을 넣는 쪽을 택했다.

- **검증만** 있다. 서명도 키 생성도 클라이언트에 없다.
- RFC 8032 7.1 시험 벡터로 증명한다 — `app update ed25519 matches the RFC 8032 vectors`.
  벡터마다 메시지·서명·공개 키를 한 비트씩 바꾼 경우와 길이가 모자란 경우도 함께 거절하는지 본다.
- 테스트용 키 쌍은 `Core.Tests/Ed25519Fixture.cs`에서 **테스트만을 위해** 만든다.
  이 서명기도 RFC 8032 벡터를 그대로 재현하는지 먼저 확인한 뒤에야 manifest 픽스처에 쓴다.

---

## 전송·다운로드·풀기

- **https만.** manifest 주소도, 패키지 주소도, **모든 리디렉션 구간**도 https여야 한다.
  자동 리디렉션을 끄고 `Location`을 따라가기 **전에** 검사하므로, http로 내려가는 구간은
  따라간 뒤에 알아차리는 것이 아니라 아예 따라가지 않는다. 구간은 최대 5회.
- **512 MiB 상한.** manifest의 `size`가 넘으면 받지 않고, 받는 도중에도 `size`를 넘는 순간 멈춘다.
- **저장 위치**는 `<상태 저장 폴더>/updates/<버전>/MightyClaude-windows.zip`이고,
  받기 전에 다른 버전 폴더를 모두 지운다.
- **디스크에 쓰인 바이트**로 `size`와 `sha256`을 확인한다. 다르면 버전 폴더째 지운다.
- **취소하면 아무것도 남지 않는다.** 실패한 다운로드도 마찬가지다.
- **풀기**는 새 폴더에 풀고 다음을 거절한다: 절대 경로 항목, `..` 구간이 있는 항목,
  대상 폴더를 벗어나는 항목, 링크로 저장된 항목(Unix 모드 `S_IFLNK`),
  `MightyClaude.exe`가 하나가 아니거나 예상 위치에 없는 패키지, 다른 아키텍처용 패키지
  (PE 머신 타입 `0x8664` = x64, `0xAA64` = arm64).

예상 위치는 zip의 최상위이거나 최상위 폴더 하나 아래다.
`scripts/build-windows.ps1`이 publish 폴더 자체를 담기 때문이고,
macOS zip이 `.app` 하나를 담는 것과 같은 모양이다.

---

## 교체 순서 (OS 결속)

실행 중인 자체 포함 앱의 폴더는 자기 자신이 바꿀 수 없다. 그래서 도우미가 한다.

1. 앱이 도우미를 **새 버전의 스테이지된 폴더에서 분리 실행**한다. 도우미는 같은
   실행 파일의 두 번째 모드(`--update-helper …`)이고, **최소 환경**(`SystemRoot`만)으로
   시작한다. 토큰·자격 증명·사용자를 알려 주는 값은 하나도 넘기지 않는다. 승격하지 않는다.
   앱은 Core가 제공하는 `StagedHelperExecutable`(스테이지된 폴더 안의 경로)만 실행하고,
   그 경로가 스테이지된 폴더 밖이면 시작하지 않는다.
2. 앱이 종료한다. 도우미는 **앱 프로세스가 끝날 때까지 기다린다(최대 5분)**.
   시간 안에 끝나지 않으면 설치 폴더를 건드리지 않고 멈춘다.
3. 도우미가 **패키지 `sha256`을 다시 확인한다**(규칙 4). 다르면 여기서 멈춘다.
4. 현재 설치 폴더를 `<설치 폴더>.backup-<시각>`으로 **이름을 바꾼다**(백업).
   도우미가 설치 폴더가 아닌 스테이지된 폴더에서 실행 중이므로 이름 변경이 허용된다.
5. 스테이지된 폴더를 설치 위치로 **복사한다**. 도우미가 스테이지된 폴더에서
   실행 중이므로 그 폴더를 이동할 수 없어 복사를 쓴다. 복사 뒤 설치 폴더에
   실행 파일이 있는지 확인한다.
6. 새 앱을 **시작한다**.
7. **새 앱이 시작된 뒤에야** 백업을 지운다.
4–6 중 어디서든 실패하면 부분 복사를 삭제하고 백업을 되돌리고 이전 앱을 시작한다.
스테이지된 폴더는 도우미가 삭제하지 않고 다음 버전을 내려받을 때 정리된다.

순수한 부분(복사, 트리 확인, 되돌리기 판단, 인자 목록, 재검증)은 Core에 있고
macOS에서 임시 폴더로 그대로 실행해 증명한다 —
`app update helper executable is inside the staged folder`,
`app update helper copies the staged folder to the install path`,
`app update helper rolls back after a copy that fails half way`,
`app update replacement verifies again and replaces the install`,
`app update replacement leaves the install untouched when it cannot proceed`.

### OS 결속 차이 (한 줄 이유)

| 차이 | 이유 |
|---|---|
| 도우미가 셸 스크립트가 아니라 같은 실행 파일의 두 번째 모드다 | Windows에는 `sh`가 없고, 같은 코드를 Mac에서 검사로 돌릴 수 있다 |
| 도우미가 **스테이지된 폴더**에서 돌기 때문에 스테이지된 폴더를 이동하지 못한다. **복사**로 설치하고, 스테이지된 폴더는 다음 버전 다운로드 시 정리된다 | Windows는 실행 중인 exe 아래 열린 파일이 있는 폴더의 이름 변경을 허용하지 않는다. 도우미가 설치 폴더가 아닌 곳에서 실행되어야 이름 변경이 가능하다 |
| macOS의 LaunchServices 재등록·2초 대기가 없다 | Windows에는 번들 ID 재등록도 입력기 세션 문제도 없다 |
| 상태·패키지·백업이 같은 볼륨이 아니면 폴더 이동이 실패한다. 그때는 되돌리고 이전 앱을 시작한다 | Windows의 폴더 이동은 볼륨을 넘지 못한다 |
| 검사는 `file://`를 쓰지 않고 메모리에서 답하는 `HttpMessageHandler`를 주입한다. 제품 코드에는 https 외의 경로가 없다 | macOS의 `allowsFileURLs` 같은 시험용 예외를 남기지 않기 위해서다 |

---

## 기기 점검 (실제 업데이트)

| 항목 | 상태 |
|---|---|
| x64 기기에서 한 버전에서 다음 버전으로 실제 업데이트: 도우미가 스테이지된 폴더에서 시작되고 새 버전이 설치 폴더에 복사되어 열린다 | **기기 미확인** |
| arm64 기기에서 한 버전에서 다음 버전으로 실제 업데이트: 같은 순서, arm64 패키지 | **기기 미확인** |

---

## 앱 시작 시 자동 확인

- 저장되는 사용자 설정이고 **기본값은 macOS와 같이 켜짐**
  (`AppSnapshot.AppUpdateAutoCheck`, 기본 `true`).
- **하루에 한 번까지**(`AppUpdateCoordinator.CheckIntervalHours = 24`).
  마지막 확인 시각은 `AppSnapshot.AppUpdateLastCheckedAt`에 ISO 8601로 저장한다.
- 창을 **지연시키지 않는다**. 첫 렌더 뒤에 백그라운드로 시작한다.
- 실패는 **섹션 안에서만** 보인다. 다른 화면에 오류를 띄우지 않는다.
- 공개 키가 없으면 자동 확인도 하지 않는다(규칙 1).
- `--smoke-test`에서는 자동 확인을 하지 않는다.
- 새 필드는 기본값을 가진 추가 필드이고 스냅샷 `Version`은 1로 유지된다
  (`StateStore`는 `Version`이 1이 아니면 저장 상태를 전부 초기화한다).

---

## 문구

문구는 전부 `MightyClaude.Core/AppUpdateStrings.cs`에 있고 WinUI는 한국어를 직접 쓰지 않는다.
`StringsVerification.AppUpdateStringsMatchMacOS`가 macOS 원문과 한 줄씩 대조한다.

| Windows 상수 | macOS 원문 | 비고 |
|---|---|---|
| `SectionTitle` … `InProgressButton` (20개) | `AppUpdateSettingsView.swift` | 그대로 |
| `NoPublicKeyNotice` = `이 빌드에는 업데이트 공개 키가 없어 업데이트 확인을 지원하지 않습니다.` | 없음 | **확인 필요** — 규칙 1은 macOS에 대응 동작이 없다. macOS는 "서명 검증 없음" 경고를 띄우고 확인을 계속 허용한다. `docs/windows-parity.md`의 보류 행 |

macOS의 `SignatureUnverified`(주황색 "서명 검증 없음…") 문장은 Windows에 **없다**.
공개 키 없는 빌드가 확인을 시도할 일이 없으므로 그 문장이 설 자리가 없다.

---

## 스모크

스모크 키는 `appUpdateSection`이다. 실제 컨트롤을 만들어 픽스처 단계
(확인 중 → 새 버전 있음 → 42% 받는 중 → 푸는 중 → 준비 완료 → 교체 중)를 모두 렌더하고,
화면에서 읽어 낸 상태 문장과 버튼 이름을 Core의 기대값과 대조한다.
공개 키 없는 빌드가 확인을 거절하는지도 같은 판정 안에서 본다.
자동 확인 스위치는 뒤집었다가 **원래 값으로 되돌린다**.
manifest를 받지도, 패키지를 내려받지도, 프로세스를 시작하지도 않는다.

---

## 범위 밖 (이 작업에서 하지 않은 것)

아래는 이 기능의 범위 밖이고, 저장소 운영자가 따로 정해야 한다.

- **Windows 빌드의 Authenticode 서명.** 현재 Windows 빌드는 서명되지 않는다
  (`build-info.json`의 `signed: false`). 업데이트 신뢰의 뿌리는 manifest의 Ed25519 서명이다.
- **CI 서명 키 격리.** 개인 키(`MIGHTY_UPDATE_SIGNING_KEY`)를 어떤 환경에 어떤 권한으로 둘지.
- **키 교체(rotation)와 폐기(revocation).** 지금 클라이언트는 빌드에 박힌 공개 키 하나만 안다.
  키를 바꾸려면 새 공개 키를 담은 빌드를 먼저 배포해야 한다.

## 아직 남은 사용자 단계

이 기능의 완료 기준은 **Core.Tests의 픽스처 서명 manifest 검증**이다.
저장소에 실제 서명 키·공개 키·주소(`MIGHTY_UPDATE_SIGNING_KEY`, `MIGHTY_UPDATE_PUBLIC_KEY`,
`MIGHTY_UPDATE_URL`)가 설정되어 있는지는 **여기서는 확인할 수 없고, 사용자가 나중에 할 일이다.**
설정되기 전까지 Windows 빌드는 공개 키를 갖지 않으므로 규칙 1에 따라 업데이트 확인을 하지 않는다.
`scripts/build-windows.ps1`은 두 빌드 변수가 있을 때만 어셈블리에 새겨 넣고
(`AssemblyMetadata`, macOS의 `Info.plist` 새김과 같은 자리),
`MIGHTY_UPDATE_PUBLIC_KEY`가 base64 32바이트가 아니거나 `MIGHTY_UPDATE_URL`이 https가 아니면 빌드를 멈춘다.

## Core.Tests 검사

`dotnet run --project native/windows/MightyClaude.Core.Tests --artifacts-path /tmp/mc-artifacts`

| 검사 이름 | 무엇을 증명하나 |
|---|---|
| `app update ed25519 matches the RFC 8032 vectors` | 검증 전용 Ed25519가 표준 벡터를 맞히고 한 비트 변조를 거절한다 |
| `app update strings match macOS` | 문구 20개가 macOS 원문과 같고 Windows 전용 문장 1개만 추가된다 |
| `app update manifest accepts a fixture signature and refuses everything else` | 봉투·서명·변조·다른 키·모르는 형식 |
| `app update manifest refuses an asset without sha256 or size` | 규칙 2와 https 규칙 |
| `app update version comparison orders releases and pre-releases` | `1.2.0 > 1.2.0-beta.2 > 1.1.9`, 빠진 자리는 0, 앞의 `v` |
| `app update without a public key there is no check at all` | 규칙 1 (요청도 만들지 않는다) |
| `app update a built-in address ignores a user address` | 규칙 3 |
| `app update transport refuses a non-https hop` | 리디렉션 구간 |
| `app update download verifies the bytes on disk and cleans up` | 크기·sha256·상한·취소·다른 버전 폴더 정리 |
| `app update staging refuses an escaping entry or the wrong package` | 탈출·링크·실행 파일 개수와 위치·아키텍처 |
| `app update replacement verifies again and replaces the install` | 규칙 4, 복사 후 스테이지 폴더 보존, 새 앱 시작 뒤 백업 삭제, 남은 백업 정리 |
| `app update replacement leaves the install untouched when it cannot proceed` | 변조·미종료·되돌리기 |
| `app update helper executable is inside the staged folder` | `StagedHelperExecutable`이 스테이지된 폴더 안에 있다 |
| `app update helper copies the staged folder to the install path` | 복사 기반 교체, 스테이지 폴더 보존 |
| `app update helper rolls back after a copy that fails half way` | 중간 실패 → 부분 복사 삭제 → 백업 복원 → 이전 앱 시작 |
| `app update automatic check happens at most once a day` | 저장 설정·기본값·하루 한 번 |
| `app update section shows the macOS copy for every phase` | 단계별 문장과 버튼, 스모크 판정 |
| `app update pipeline runs from a fixture-signed manifest to a ready install plan` | 확인 → 다운로드 → 풀기 → 계획 → 교체 전체 |
