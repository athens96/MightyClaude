# macOS CI 실패 조사

상태: 진단 — 수정은 CI에서 아직 검증되지 않음(v3 이후 첫 푸시에서 확인)

실패 단계: `.github/workflows/native.yml` `macos` 작업의 **Test Swift core and loopback remote execution**
(`bash scripts/test-native-macos.sh`, 약 42초 뒤 exit 1, 40회 이상 연속 실패). 로컬에서는 같은 스크립트가 459개 검사를 모두 통과한다.
CI 로그 본문은 아직 확보되지 않았으므로 아래 내용은 전부 코드 증거에 기반한 후보이며, 확정된 원인이 아니다.

---

## 원인 후보

| # | 후보 | 가능성 | 판정 |
| --- | --- | --- | --- |
| A | `CLIUpdateTests`의 셸 픽스처가 CI 러너의 낮은 CPU·IO에서 `metadataTimeout`을 넘겨 실패 | 높음 | 로그 필요 |
| B | 러너의 Xcode/Swift 버전과 Swift Testing 매크로 플러그인 경로 불일치로 빌드 단계에서 exit 1 | 중간 | 로그 필요 |
| C | `libghostty-spm` 등 SwiftPM 원격 의존성 해결 실패 | 낮음 | 로그 필요 |
| D | 교차 언어 검사(`MIGHTY_NATIVE_PEER_MANIFEST`) | — | 배제 |
| E | `RelayIntegrationTests`의 `relay/dist` 부재 | — | 배제 |

---

## 증거

### A. CLIUpdateTests 병렬 부하 타임아웃

`CLIUpdateService`의 기본 경로는 메타데이터 조회에 매우 짧은 타임아웃을 쓰고, 테스트용 이니셜라이저는
그보다 긴 값을 기본값으로 둔다.

```
native/macos/Sources/MightyCore/CLIUpdateService.swift:61
    configuration = CLIUpdateConfiguration(… metadataTimeout: 4, updateTimeout: 300)
native/macos/Sources/MightyCore/CLIUpdateService.swift:69
    init(environment: [String: String], homeDirectory: URL, metadataTimeout: TimeInterval = 15, updateTimeout: TimeInterval = 30)
native/macos/Sources/MightyCore/CLIUpdateService.swift:200
                    timeout: configuration.metadataTimeout, maximumBytes: 16_384)
```

`homebrewUsesActualCaskOrFormulaAndOnlyNamedPackage`는 실제 셸 픽스처 프로세스를 띄운다. 커밋
`981fa1a`("test(macos): stop the Homebrew update test failing under parallel load") 이전에는 2초
기본값이었고 약 300개 검사가 동시에 프로세스를 생성할 때 절반가량이 실패했다. 값을 올린 뒤 로컬에서는
7회 연속 통과(317 검사·50 스위트)했지만, CI 러너가 더 느리면 같은 형태로 다시 실패할 수 있다.

### B. Swift Testing 플러그인 경로 — Xcode 버전 불일치

```
scripts/test-native-macos.sh:5
# Respect an explicit DEVELOPER_DIR, otherwise use xcode-select (including CI's selected Xcode).
scripts/test-native-macos.sh:8
TESTING_PLUGIN="$(dirname "$SWIFT_EXECUTABLE")/../lib/swift/host/plugins/testing/libTestingMacros.dylib"
scripts/test-native-macos.sh:10-11
if [[ -f "$TESTING_PLUGIN" ]]; then
  TEST_ARGUMENTS+=(-Xswiftc -load-plugin-library -Xswiftc "$TESTING_PLUGIN")
```

로컬은 Command Line Tools의 Swift 6.4를 쓰고, CI는 `macos-latest`가 선택한 Xcode를 그대로 따른다
(`.github/workflows/native.yml:24` **Report the selected Apple toolchain**). 러너 이미지가 갱신되어
플러그인 경로가 달라지거나 매크로 버전이 어긋나면 개별 검사 실패 없이 빌드 단계에서 exit 1이 난다.
42초라는 짧은 실행 시간은 "검사가 하나씩 실패"보다 "빌드/로딩 단계에서 조기 종료"에 가깝다.

### C. SwiftPM 원격 의존성 해결 실패

`native/macos/Package.swift`가 `libghostty-spm`을 원격 의존성으로 선언한다. 러너에 패키지 캐시가 없는
상태에서 네트워크나 태그 문제로 해결이 실패하면 검사 0개 실행 후 같은 단계가 exit 1로 끝난다.

### D. 교차 언어 검사 — 배제

`RemoteTests`의 교차 언어 검사는 `ProcessInfo.processInfo.environment["MIGHTY_NATIVE_PEER_MANIFEST"] != nil`
일 때만 실행된다. CI에는 이 변수가 없어 skip되므로 실패 원인이 아니다.

### E. RelayIntegrationTests — 배제

`relay/dist/`는 `relay/.gitignore`에 있어 checkout에 포함되지 않고, `RelayIntegrationTests`는
`relayHarnessAvailable`(`relayScript != nil && node != nil`)을 활성화 조건으로 걸어 두어 skip된다.

---

## 로컬 검증 실행 기록

### 기준선 (v3 번들 변경 전, 커밋 0ea530d)

`chore/style-freeze-v3-and-windows-parity` 브랜치의 v3 번들 변경(`01b16d1`) 이전 커밋에서
임시 git worktree를 만들어 실행했다. 콜드 빌드.

```
✔ Test run with 459 tests in 69 suites passed after 12.949 seconds.
EXIT_STATUS:0
```

### 변경 후 (현재 HEAD, 커밋 3c01371)

v3 번들 변경이 포함된 현재 HEAD에서 실행. `StylesV3BundleTests` 5개가 추가되어 464개 검사.

```
✔ Suite StylesV3BundleTests passed after 0.771 seconds.
✔ Test run with 464 tests in 70 suites passed after 8.310 seconds.
EXIT_STATUS:0
```

`StylesV3BundleTests` 상세:

```
✔ Test ouroborosDeclaresSixPhasesInOrder() passed after 0.761 seconds.
✔ Test styleLaunchWiringBindsRunWindowAfterApproval() passed after 0.761 seconds.
✔ Test hundredAndFirstActionIsRejectedWithELimit() passed after 0.761 seconds.
✔ Test styleLaunchWiringDoesNotCopyWorkspaceManifests() passed after 0.762 seconds.
✔ Test styleLaunchWiringBlocksTitlePrefixForOrdinaryRunWindows() passed after 0.762 seconds.
```

두 실행 모두 600초 타임아웃 이내에 완료되었고, 어느 실행도 hung 처리하지 않았다.

## 재현

로컬(현재 통과, 대조군):

```bash
bash scripts/test-native-macos.sh
```

병렬 부하를 높인 재현 시도:

```bash
swift test --package-path native/macos --disable-xctest --enable-swift-testing --parallel
```

후보 B 재현(툴체인 차이):

```bash
xcode-select -p && swift --version
ls "$(dirname "$(xcrun --find swift)")/../lib/swift/host/plugins/testing/"
```

CI 수준의 CPU·IO 부하와 러너 이미지 조합은 로컬에서 재현되지 않는다. 현재까지 로컬 재현은 실패했고,
그래서 원인을 확정하지 못했다.

---

## 필요한 로그

아래 자료가 있어야 후보를 하나로 좁힐 수 있다. **토큰·자격증명·`***` 마스킹 값이 포함된 줄은 제외하고**
필요한 부분만 발췌해 공유해 주세요.

1. 실패한 **Test Swift core and loopback remote execution** 단계 출력의 첫 30줄 — 의존성 해결(`resolving …`)과
   컴파일 오류(`error:`) 여부 확인용 (후보 B·C 판별).
2. 같은 단계 출력에서 `CLIUpdateTests`, `homebrewUsesActualCaskOrFormulaAndOnlyNamedPackage`,
   `Test … failed`, `exited with signal`을 포함하는 줄 전부 (후보 A 판별).
3. 같은 단계의 마지막 20줄 — 실패 요약(`Executed N tests … with M failures`)과 종료 코드.
4. **Report the selected Apple toolchain** 단계 전체 출력 — Xcode 경로와 Swift 버전 (후보 B 판별).

---

## 수정안

세 후보 모두 로그 없이는 확정할 수 없으므로 **아직 어떤 수정도 적용하지 않았다.** 로그가 확보된 뒤
아래 순서로 진행한다.

1. 로그에 `CLIUpdateTests`가 보이면 → 후보 A. 해당 검사를 `@Test(.serialized)`로 직렬화하고,
   `CLIUpdateService.swift:69`의 `metadataTimeout` 기본값을 CI 여유분까지 올린다.
2. 로그에 `error:` 컴파일 오류나 플러그인 로드 실패가 보이면 → 후보 B. `native-macos.yml`에
   `maxim-lobanov/setup-xcode`로 Xcode 버전을 고정하거나 `TESTING_PLUGIN` 탐색을 버전 독립적으로 고친다.
3. 로그에 `could not be resolved`가 보이면 → 후보 C. 의존성 캐시 단계를 추가하거나 버전을 고정한다.
4. 어느 경우든 수정 후 실제 CI 실행이 녹색이 되기 전까지 이 문서의 상태 줄은 "진단"으로 유지한다.

---

## 범위 밖

이 조사는 v3 태그 전 준비 브랜치(`chore/style-freeze-v3-and-windows-parity`)에서 수행되었다. 실제 수정
커밋과 CI 확인은 v3 태그 이후 첫 푸시에서 이루어진다. 이 문서와 `docs/mighty-styles.md`,
`.github/workflows/native-macos.yml`은 v3 동결 허용 목록 **밖**에 유지된다.
