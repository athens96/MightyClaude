# macOS CI 실패 조사

상태: 진단 — 5라운드 진행 중 (마지막 라운드, 실행 확인 대기)

## 라운드 기록

| 라운드 | SHA | 빨간 단계 | 주석 | 수정 |
| --- | --- | --- | --- | --- |
| 1 | ed33f18 | Test Swift core | (주석 스크립트 추가 전) | 동결 단계 마지막으로 이동 + 주석 스크립트 추가 |
| 2 | 0359c63 | Test Swift core | `Sendable` 경고만(1500자 잘림, 실제 오류 불명) | `#expect` 옵셔널 체인 바인딩, `Date.init`→`{ Date() }` Sendable 수정, 주석 스크립트 개선 |
| 3 | f9ec5b1 | Test Swift core | `::error title=macOS Swift tests::` — `StyleEvaluator.swift:264:52: error: cannot call value of non-function type '[T]'` 외 264~270행 컴파일 오류 4건 (전문은 아래 '확정된 원인') | (없음 — 이 라운드로 원인이 확정됐다) |
| 4 | 4b151ea | Test Swift core | 3라운드 주석이 원인을 지목 | `StylePrerequisiteProbe.evaluate`의 지역 변수 `satisfied` 섀도잉 제거(`Self.satisfied` + `outcomes`로 개명), 같은 식의 키패스-함수 형태를 클로저로 교체, 주석에서 체크아웃 밖 경로 제거 + 1500자 청크 분할 |
| 5 | 9ffeb42 | Test Swift core | 4라운드(`f2dbbe2` 실행) 주석: 컴파일은 통과, 테스트 실행 중 `MightyCoreTests/StyleFixtures.swift:92: Fatal error: Unexpectedly found nil` — `BundledStyles.shared.style(id)!`가 nil. 러너의 SwiftPM은 리소스 번들 `MightyClaude_MightyCore.bundle`을 `.xctest` **안**(Contents/Resources)이 아니라 **옆**(.build/debug)에 두는데, `BundledStyleSource.searchRoots()`에는 `.xctest`의 부모 폴더가 없었다(로컬 Swift 6.4는 안에 복사하므로 통과) | `searchRoots()`에 `.xctest` 번들의 부모를 더함(확장자가 xctest일 때만 — `.app`의 부모는 여전히 제외). `StyleFixtures.bundled`는 nil이면 탐색한 폴더·파일 수·거절 목록을 적고 멈춰 다음 주석이 원인을 말하게 함 |

실패 단계: `macos` 작업(분리 전 `.github/workflows/native.yml`, 이 브랜치부터 `.github/workflows/native-macos.yml`)의 **Test Swift core and loopback remote execution**
(`bash scripts/test-native-macos.sh`, 약 42초 뒤 exit 1, 40회 이상 연속 실패). 로컬에서는 같은 스크립트가 459개 검사를 모두 통과한다.
3라운드(`f9ec5b1`)의 공개 주석으로 원인이 확정됐다 — 아래 '확정된 원인'. 그 아래의 '원인 후보' 표와
증거 절은 확정 전의 기록으로 남겨 두되, 판정 칸을 실제 주석에 비추어 갱신했다.

---

## 확정된 원인 (3라운드 공개 주석, SHA `f9ec5b1`)

`macos` 작업의 **Test Swift core and loopback remote execution** 단계가 남긴 공개 체크런 주석
(`::error title=macOS Swift tests::`, 자격 증명 없는 공개 REST API로 SHA를 통해 읽음):

```
native/macos/Sources/MightyCore/Styles/StyleEvaluator.swift:264:52: error: cannot call value of non-function type '[T]'
native/macos/Sources/MightyCore/Styles/StyleEvaluator.swift:266:65: error: trailing closure passed to parameter of type 'Predicate<Zip2Sequence<[StyleProbe], Sequence2>.Element>' (aka 'Predicate<(StyleProbe, Sequence2.Element)>') that does not accept a closure
native/macos/Sources/MightyCore/Styles/StyleEvaluator.swift:268:82: error: cannot infer key path type from context; consider explicitly specifying a root type
native/macos/Sources/MightyCore/Styles/StyleEvaluator.swift:268:106: error: cannot infer key path type from context; consider explicitly specifying a root type
native/macos/Sources/MightyCore/Styles/StyleEvaluator.swift:270:92: error: cannot infer key path type from context; consider explicitly specifying a root type
```

즉 검사 하나가 실패한 것이 아니라 **`MightyCore` 타깃이 컴파일되지 않아** 단계가 42초 만에 exit 1로
끝난 것이다. 후보 A·B·C·F 전부 아니다 — F(`StyleCapabilityTests.swift:86`)는 2라운드에서 이미 고쳤고,
주석에 그 파일은 등장하지 않는다.

문제의 줄(수정 전):

```swift
let satisfied = prerequisites.probes.map { satisfied($0, home: home, workspacePath: workspacePath, environment: environment) }
```

지역 상수 `satisfied`는 **자기 초기화식 안의 클로저에서 이미 스코프에 들어와 있다.** 로컬(Swift 6.4)은
클로저 안의 `satisfied(...)`를 정적 메서드 `StylePrerequisiteProbe.satisfied(_:home:workspacePath:environment:)`로
해석하지만, 러너의 더 오래된 컴파일러는 지역 `[Bool]`로 해석해 `cannot call value of non-function type '[T]'`를
낸다. 266·268·270행의 오류 네 건은 그 타입 실패에서 파생된 것이다. 이 파일도 실패가 시작된 그 푸시에서
들어왔고, 로컬에서만 통과한다는 관측과 정확히 맞는다.

### 4라운드 수정

```swift
let outcomes = prerequisites.probes.map {
    Self.satisfied($0, home: home, workspacePath: workspacePath, environment: environment)
}
let ready = prerequisites.mode == .all ? !outcomes.contains(false) : outcomes.contains(true)
let unmet = zip(prerequisites.probes, outcomes).filter { !$0.1 }.map { $0.0 }
guard !ready else { return StylePrerequisiteResult(ready: true) }
let missing = prerequisites.report == .first
    ? Array(unmet.prefix(1).map { $0.missing })
    : unmet.map { $0.missing }
return StylePrerequisiteResult(ready: false, missing: missing, hint: unmet.first?.hint,
                               canInstall: install != nil && unmet.contains { $0.install })
```

이름을 `outcomes`로 바꿔 섀도잉을 없애고 호출을 `Self.`로 한정했다. 주석이 오류를 지목한 나머지 세 줄의
키패스-함수 형태(`\.0`, `\.missing`, `where: \.install`)도 같은 뜻의 클로저로 바꿔 오래된 컴파일러의
키패스 추론에 기대지 않게 했다. 동작은 동일하고, 로컬에서 467개 검사가 모두 통과한다.

`native/macos/Sources` 전체를 같은 모양(지역 상수가 자기 초기화식의 클로저 안에서 동명 함수를 가림)으로
훑었을 때 걸리는 곳은 이 한 줄뿐이었다. `StyleManifestDecoder`의 `let kind = try kind(&reader)` 같은 줄은
클로저가 아니어서 지역 이름이 아직 스코프에 없고, 어느 컴파일러에서도 메서드로 해석된다.

### 주석 자체의 결함과 그 수정

3라운드 주석은 원인을 알려 줬지만 `​/Users/runner/work/...` 로 시작하는 **체크아웃 밖 경로**를 그대로
실었다. 4라운드에서 `scripts/test-native-macos.sh`의 주석 발행부를 고쳤다.

- 체크아웃 경로 접두사는 저장소 상대 경로로 바꾸고, 남는 절대 경로는 `<path>`로 가린다.
- 마지막 30개의 비어 있지 않은 줄을 싣되, 1500자를 넘으면 잘라 버리는 대신 1500자짜리 주석
  최대 3개로 나눠 발행한다. 2라운드에서 실제 오류가 잘려 나간 것이 바로 이 때문이었다.

---

## 원인 후보

| # | 후보 | 가능성 | 판정 |
| --- | --- | --- | --- |
| F | `StyleCapabilityTests.swift:86`의 `#expect((옵셔널 체인 ?? "").contains(…))`를 CI의 더 오래된 swift-testing 매크로가 컴파일하지 못함 | 높음 | **아님** — 2라운드에서 선제 수정했고 3라운드 주석에 이 파일은 없다 |
| A | `CLIUpdateTests`의 셸 픽스처가 CI 러너의 낮은 CPU·IO에서 `metadataTimeout`을 넘겨 실패 | 낮음 | **아님** — 주석에 검사 실패가 하나도 없다(컴파일 단계에서 죽었다) |
| B | 러너의 Xcode/Swift 버전과 Swift Testing 매크로 플러그인 경로 불일치로 빌드 단계에서 exit 1 | 중간 | **부분적으로 맞음** — 플러그인 경로가 아니라 러너의 더 오래된 **컴파일러**가 원인이다 |
| C | `libghostty-spm` 등 SwiftPM 원격 의존성 해결 실패 | 낮음 | **아님** — 주석에 해결 실패가 없고 컴파일까지 진행됐다 |
| D | 교차 언어 검사(`MIGHTY_NATIVE_PEER_MANIFEST`) | — | 배제 |
| E | `RelayIntegrationTests`의 `relay/dist` 부재 | — | 배제 |

---

## 증거

### 측정된 이력 (공개 REST API, 자격 증명 없음, 2026-09-20)

- 워크플로 `Native clients`는 기록된 54회 실행이 전부 실패다. `macos` 작업이 초록이었던 적은 없다.
- `macos` 작업의 실패 단계는 시기에 따라 다르다: 첫 실행 `eda0e25`(2026-09-16)는 이 Swift 테스트 단계(83초), 그 뒤 `b9fc86f`(2026-09-19 09:29)까지는 GUI 단계 **Check smoke result**, 그리고 `306d8be`를 올린 푸시(2026-09-19 12:25, `f158bbb`+`48bd76a`+`306d8be` = 스타일 매니페스트 엔진)부터 다시 이 Swift 테스트 단계다.
- 이 단계는 `b9fc86f`에서 **199초 걸려 통과**했다. `306d8be`부터는 42~59초 만에 exit 1이다. 전체 검사를 다 돌고 실패했다면 통과 때와 비슷한 시간이 걸려야 하므로, 검사가 끝까지 돌기 전에 — 빌드 단계이거나 아주 이른 크래시로 — 죽는 것으로 보인다. 그래서 검사 하나의 타임아웃(A)은 시간과 맞지 않는다.
- 그 푸시에서 `Package.swift`의 변경은 `MightyCore` 타깃에 `resources: [.copy("Resources/Styles")]` 한 줄뿐이다.
- 공개 저장소를 새로 복제해 `306d8be`에서 빌드 캐시 없이 `bash scripts/test-native-macos.sh`를 돌리면 **로컬(Apple Swift 6.4)에서는 통과**한다(418개 검사, 빌드 65초, 전체 86초). 따라서 저장소 상태가 아니라 CI 환경(러너의 더 오래된 컴파일러·매크로)에 달린 실패다. 러너의 정확한 Xcode/Swift 버전은 이 조사에서 직접 확인하지 못했다.

### F. `#expect` 매크로가 풀지 못하는 식 — 가장 유력

`native/macos/Tests/MightyCoreTests/StyleCapabilityTests.swift:86`

```swift
#expect((legacy.paperthin?.casebook?.name ?? "").contains("\u{FFFD}"))
```

- 이 파일은 실패가 시작된 바로 그 푸시(`f158bbb`)에서 처음 추가됐다.
- 테스트 타깃 전체에서 이 모양(괄호 안 옵셔널 체인 + `??` 뒤에 멤버 호출)의 `#expect`는 이 한 줄뿐이다.
- 로컬 컴파일러도 이 줄의 매크로 전개에서 서로 모순되는 경고 둘을 낸다: `result of call to 'contains' is unused`, `left side of nil coalescing operator '??' has non-optional type 'String?'`. 매크로가 이 식을 잘못 풀어 쓴다는 뜻이고, 더 오래된 매크로는 이를 오류로 처리할 수 있다 → 테스트 타깃 컴파일 실패 → 코어 빌드 직후 exit 1(시간과 맞는다).
- 확인되지 않았다. 이 브랜치에서는 고치지 않았다.


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
(분리 전 `native.yml:24`, 지금은 `native-macos.yml`의 **Report the selected Apple toolchain**). 러너 이미지가 갱신되어
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

0. (F가 맞다면) 식을 먼저 값으로 받는다 — 동작은 같고 로컬 경고 둘도 사라진다. **아직 적용하지 않았다.**

   ```swift
   let name = legacy.paperthin?.casebook?.name ?? ""
   #expect(name.contains("\u{FFFD}"))
   ```

   F를 고쳐도 `macos` 작업은 그 뒤의 GUI 단계 **Check smoke result**(실행/중지 버튼의 접근성 탐색)에서 계속 실패할 가능성이 높다 — `306d8be` 이전의 모든 실행이 거기서 실패했다. 별개의 문제다.

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
