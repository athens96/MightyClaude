# 마이티 스타일 엔진 — 계약에서 벗어난 자리

docs/mighty-styles.md를 구현하면서 계약이 모호하거나 이 환경에서 사실이 아니었던 지점들이다. 각 항목은 **계약의 절 → 실제로 한 일 → 왜**로 적는다. 리드가 계약에 되접어 넣을 대상이다.

## 레인 A

### 1. 폴더의 `linkCount == 1`은 이 파일 시스템에서 성립하지 않는다 (§1.8 · §3.1)

계약은 스캔하는 각 항목에 대해 `isSymbolicLink == false`, `isRegularFile == true`(폴더는 `isDirectory == true`), **`linkCount == 1`**을 요구한다. 실측(APFS, macOS 15): 디렉터리의 `st_nlink`는 언제나 2 이상이다(`.`과 `..`, 하위 폴더마다 하나 더). 규칙을 글자 그대로 적용하면 **모든 폴더가 거부되어** 케이스북이 영원히 `absent`가 된다.

한 일: `linkCount == 1`은 **일반 파일에만** 적용하고(`StylePathBoundary.isPlainFile`), 폴더에는 링크 아님 + 디렉터리임만 검사한다(`isPlainDirectory`). 하드 링크로 파일을 밀반입하는 것은 여전히 막히고, 하드 링크된 **디렉터리**는 macOS에서 일반 사용자가 만들 수 없다.

### 2. 번들 리소스의 실제 경로와 탐색 후보 (§3.2)

계약은 `<후보>/MightyClaude_MightyCore.bundle/Styles`를 찾으라고 하고, 후보를 `Bundle.main.resourceURL` · `Bundle(for:).resourceURL` · `Bundle.main.bundleURL` 셋으로 고정한다. 실측: ① macOS에서 SwiftPM이 만드는 리소스 번들은 **`Contents/Resources/Styles`** 구조다(평평한 `Styles`가 아니다). ② `swift test`에서 번들은 테스트 러너(`MightyCoreTests.xctest`)의 **형제**로 놓이므로 세 후보 중 어느 것도 그 폴더를 가리키지 않는다.

한 일: 후보에 `Bundle(for:).bundleURL` · `Bundle.main.bundleURL.deletingLastPathComponent()` · 실행 파일의 폴더를 더하고, 각 후보 아래에서 `Contents/Resources/Styles`와 평평한 `Styles`를 **둘 다** 본다. 트랩 없는 탐색이라는 원칙은 그대로다(`Bundle.module` 미사용, 못 찾으면 빈 배열). `scripts/build-macos.sh`의 존재 확인도 두 경로를 모두 받는다.

### 3. `0644` 기록 파일은 §4.3의 규칙으로 잠기지 않는다 (§4.3 ↔ §9.3)

§4.3은 "현재 uid 소유가 아니거나 **group/other에 쓰기 비트**가 있으면" 잠금이라고 정한다. §9.3의 `worldWritableApprovalsRefused`는 픽스처를 "0644로 만든 기록 파일"이라고 적는데, `0644`에는 group/other 쓰기 비트가 없다. 두 문장이 서로 어긋난다.

한 일: 규범인 §4.3을 따랐다(쓰기 비트 검사). 테스트 픽스처는 `0666`(파일)과 `0777`(폴더)을 쓴다. 읽기만 열린 `0644`는 무결성 문제가 아니므로 잠그지 않는다.

### 4. `placeholder(...)`가 "앱 기본"을 표현하는 방법 (§1.7 · §5.4)

§1.7은 실행 중 placeholder가 없으면 "앱 기본"으로 떨어진다고 하지만, §5.4의 서명은 `-> String`이라 "없음"을 돌려줄 자리가 없다. 앱의 기본 문구는 MightyCore가 아니라 앱에 있다.

한 일: 값이 없을 때 **빈 문자열**을 돌려준다. 레인 B는 `isEmpty`일 때 자기 공통 문구를 쓴다.

### 5. 최상위가 객체가 아닌 JSON의 코드 (§2)

계약은 최상위가 객체라고만 적고, 배열이나 스칼라가 왔을 때의 코드를 정하지 않았다. `E_SCHEMA_MISSING`은 "객체인데 schema가 없다"는 뜻으로 읽히므로 쓰지 않았다.

한 일: 최상위가 객체가 아니면 `E_NOT_JSON`이다. `{}`(빈 객체)는 예정대로 `E_SCHEMA_MISSING`이다.

### 6. 필수 항목이 빠진 파일은 `E_SCHEMA_NOT_FIRST`가 먼저 걸린다 (§2)

사전 스캔이 최상위의 **첫 키**를 보므로, `schema`가 아예 없는 파일은 `E_SCHEMA_MISSING`이 아니라 `E_SCHEMA_NOT_FIRST`로 거부된다(첫 키가 `schema`가 아니기 때문). `E_SCHEMA_MISSING`은 키가 하나도 없는 `{}`에서만 난다. 두 코드 모두 픽스처가 있다.

### 7. 계약이 코드를 지정하지 않은 자리들 (§1.4 · §1.5 · §2)

다음은 §2의 목록 중 가장 가까운 코드를 골랐다. 되접을 때 확정이 필요하다.
- `phases[].order`가 중복 → `E_DUPLICATE_ID`(경로 `phases[n].order`).
- `phases[].order`가 0–99 밖 → `E_TYPE`.
- `groups`가 빈 배열(1–16 위반) → `E_MISSING_FIELD`.
- `foldText`·`prerequisites.mode`·`report`의 값이 어휘 밖 → `E_UNKNOWN_RULE`(경로와 값을 함께 낸다).
- `rules.next.map`에 없는 단계 id가 키로 있음 → `E_UNKNOWN_REFERENCE`.
- `rules.recommend.map`/`initialGroup.map`에 그 기능이 내지 않는 상태 값이 있음 → `E_UNKNOWN_REFERENCE`(빠진 상태는 계약대로 `E_CAPABILITY_MAP`).

### 8. 승인 레코드의 `decidedAt`은 `Date` + ISO-8601 (§4.3)

계약의 예시 JSON은 `"2026-09-19T08:00:00Z"` 문자열이다. Swift 쪽 타입은 `Date`로 두고 `JSONEncoder`/`Decoder`의 `.iso8601` 전략을 쓴다 — 와이어 모양은 예시와 같고, 병합의 "더 최근 `decidedAt`이 이긴다"와 상한의 "오래된 approved부터"가 문자열 비교 없이 성립한다.

### 9. `/guided`와 `/settings`의 스타일 검사 (§7.5)

계약은 "내장 두 id 외의 스타일을 400으로 거절하지 않는다"고 하면서 오류 문자열은 그대로 두라고 한다. 라우트에는 레지스트리가 없으므로(델리게이트가 갖는다) 라우트는 **모양만** 본다: `cli`이거나 id 정규식 밖이면 `400 "알 수 없는 스타일입니다."`, 그 밖에는 델리게이트로 넘긴다. 오늘의 `"style은 ouroboros 또는 paperthin이어야 합니다."`는 사라진다 — 그 문자열을 단언하는 테스트는 없었고, §4.5는 미등록과 미승인이 **같은 문자열**이기를 요구한다.

델리게이트 서명(`mobileGuided(sessionId:style:skill:text:)`)은 그대로 두었다. 레인 B가 아직 바뀌지 않았기 때문이며, 해석된 `styleId`/`actionId`가 그 두 인자로 들어간다.

### 10. 아주 큰 파일은 거부 목록에 남지 않고 건너뛴다 (§3.1)

스캐너는 각 후보를 `boundedData(maximumBytes: 262 145)`로 읽는다. 그래서 **256 KB + 1바이트**짜리 파일은 읽혀서 `E_TOO_LARGE`로 거부 목록에 남지만(§9.3의 `oversizeAndDepthRefused`가 요구하는 값), 그보다 더 큰 파일은 읽히지 않아 **목록에 아무 줄도 남기지 않는다**. §3.1의 "실패는 이유와 함께 목록에 남는다"와 어긋난다.

한 일: 지금은 그대로 두었다. 고치려면 `DiscoveredStyleFile` 없이 거부만 낼 수 있는 통로가 스캐너에 필요하고, 그 대안(더 큰 상한으로 전부 읽기)은 파일 32개를 동시에 메모리에 들고 있게 만든다.

### 11. `MobileCapability.all`에 `"style"`을 지금 더했다 (§7.1)

와이어 어휘는 §8.2가 태그에서 고정하는 것이고 레인 A가 `MobileRemoteModels.swift`를 소유하므로 지금 더했다. 다만 **`panel`을 실제로 채우는 것은 레인 B의 배선**이므로, 레인 B가 끝나기 전까지 호스트는 capability를 광고하면서 `panel` 없는 페이로드를 보낸다. 레인 B의 7번 단계(`AppStore+MobileRemote`의 투영 배선)가 끝나야 광고가 정직해진다.

## 레인 B

### 1. 승인 띠는 패널 안이 아니라 패널 바로 위에 있다 (§6.1의 4번)

§6.1은 `"확인이 필요합니다 · [내용 보기]"` 한 줄을 `GuidedPanel`의 네 번째 블록으로 적는다. 그런데 §4.5는 미승인 스타일이 실행 창의 스타일이 되는 것을 금지하므로, 그 상태에서는 `guidedStyle(_:)`이 `nil`이고 **패널 자체가 그려지지 않는다** — 패널 안에 두면 영원히 보이지 않는 블록이 된다.

한 일: 띠(`GuidedApprovalStrip`)를 선택기 바로 아래, 패널이 시작되는 그 자리에 둔다(`SessionPaneView`). 화면에서 보이는 위치는 계약과 같고, 실행 창이 `pending` 스타일을 가리키고 있을 때(승인 뒤 파일이 바뀌어 다음 스캔에서 `pending`이 된 경우) 나타난다. 같은 이유로 §3.4의 `"이 실행 창의 스타일이 바뀌었습니다 — 다시 고르세요"`도 메뉴 안이 아니라 선택기 아래 한 줄로 그린다.

### 2. 행동 칩 툴팁의 "호출자"는 `userInvoked`일 때만 나온다 (§6.1의 7번)

오늘 Paperthin은 `userInvoked`가 아닌 스킬에도 `"모델도 스스로 꺼내 씀"`을 붙인다(`PaperthinPanel.helpText`). 그 문장을 모든 스타일에 붙이면 Ouroboros의 아홉 칩 전부에 오늘 없던 줄이 생긴다.

한 일: `StyleChips.help(_:)`는 `help · 범위: scope · 사람만 부를 수 있는 스킬 · 읽기 전용`을 **해당하는 것만** 잇는다. Ouroboros 칩의 툴팁은 오늘과 같고, Paperthin의 비-`userInvoked` 칩은 그 한 줄을 잃는다. 어떤 테스트도 그 문자열을 단언하지 않는다.

### 3. `GuidedStyleView` 대신 네 개의 인자 (§6.3의 40번)

§6.3은 `MightyGraphView`의 `style:` 인자를 `styleView: GuidedStyleView?`(평가기 + 이름 + 출처) 하나로 바꾸라고 적는다. 그래프가 실제로 쓰는 것은 **두 가지 서로 다른 것**이다: 요청 블록 제목·아이콘·색은 레지스트리 전체를 훑고(§1.10), 머리말은 이 실행 창의 스타일 하나만 본다. 한 묶음으로 만들면 뷰가 "이 값은 어느 쪽이냐"를 매번 풀어야 한다.

한 일: `styleTitles: StyleTitleSource`(레지스트리 + 워크스페이스)와 `styleName` · `styleSource` · `stylePhase` 세 개로 나눴다. 기본값이 있으므로 기존 진단 뷰의 `MightyGraphView(...)` 호출은 그대로 컴파일된다.

### 4. 내장 두 스타일의 요청 블록 아이콘·색은 오늘과 달라진다 (§1.10)

§1.10은 "행동 → 단계 → 스타일 → 앱 기본 순으로 처음 지정된 값"이라는 규칙과, "둘 다 지정하지 않은 내장 두 스타일은 오늘과 픽셀 단위로 같다"는 문장을 함께 적는다. 두 문장이 어긋난다: 1.13의 Ouroboros 매니페스트는 **행동마다 `icon`을 지정하고** `presentation.icon`도 있으며, 1.14의 Paperthin도 `presentation.icon`이 있다. 규칙을 적용하면 `/ouroboros:seed` 블록의 심볼은 `arrow.up.message`가 아니라 `leaf`가 되고, Paperthin 블록은 `square.grid.2x2`가 된다.

한 일: 규범인 규칙을 따랐다(레인 A의 `StyleRegistry.requestIcon`/`requestTint`가 그대로 그 규칙이다). 제목 꼬리 `요청 N · <provider>`는 계약대로 앱이 쓴다. **화면에서 확인이 필요한 변경이다.**

### 5. `[허용]`이 열리는 조건 (§4.4)

§4.4는 "`[허용]`은 자동 허용 구역이 화면에 **실제로 보인 뒤에만** 활성화된다"고 적는다. 스크롤 위치를 추적하는 장치는 두지 않았다.

한 일: 자동 허용 구역은 접히지 않고 시트의 **두 번째 블록**(640×560 시트의 맨 위)이므로, 그 구역 뷰의 `onAppear`가 곧 "그려졌다"는 신호다. 그 신호 전까지 `[허용]`은 비활성이고, `autoAllow`가 비어 있지 않으면 계약대로 두 번째 확인을 받는다.

### 6. 레인 A의 공개 모양 중 바꾼 것 (§5 · §7)

레거시 shim(`LegacyStyleShims.swift`)과 그 마지막 호출자가 사라지면서 `MobileRemoteSupport`/`MobileMightySupport`에서 함께 바뀐 것들이다.

- `MobileMightySupport.runs(_:style:)` → `runs(_:title:)`. 제목은 매니페스트 하나가 아니라 **레지스트리**가 정하므로(§1.10) 문자열 id로는 표현할 수 없다.
- `MobileMightySupport.guidedPrompt(style:skill:text:)` → `guidedPrompt(_ style: RegisteredStyle, actionId:text:)`. 내장 두 id로 분기하던 자리다. `singleLine(_:)`은 `public`이 됐다.
- `MobileMightySupport.ouroboros(phase:ready:)` · `paperthin(installed:casebook:)` **삭제**. 레거시 페이로드는 `MobileLegacyStyleAdapter`가 만든다(§7.4).
- `MobileRemoteSupport.styleOptionIds(guided:)` **삭제**. 호출자는 `guided ? MobileWire.mightyStyles : [MobileWire.cliStyle]`을 쓴다 — 어휘가 이미 `MobileWire`에 고정되어 있다.
- `MobileRemoteSupport.style(_:)`은 이제 **해석된 스타일 id**를 받는다(저장된 `mightyStyle`이 아니다). 세 단어 어휘 안이면 그대로, 아니면 `cli`다 — §7.2의 규칙 그대로이고 기존 단언값(`nil`→`cli`, `"ouroboros"`→`"ouroboros"`, `"zzz"`→`"cli"`)은 바뀌지 않는다.

### 7. 앱 모듈이 쓸 수 없는 엔진 헬퍼 (§4.4)

승인 카드는 "한 번의 읽기"를 요구하는데, 그 읽기를 하는 `CLIAccountSupport.boundedData`는 MightyCore 내부(`internal`)다.

한 일: `AppStore.boundedStyleData(_:)`가 `fileSizeKey`로 256 KB를 먼저 보고 `Data(contentsOf:)`로 한 번 읽는다. 등록 화면의 파일 한 개에만 쓰이고, 스캐너는 계속 엔진의 것을 쓴다.

### 8. 내장 스타일도 `mightyStyleHash`를 저장한다 (§3.4)

§3.4는 번들에 해시 검사를 적용하지 않는다고만 적고, 저장 여부는 정하지 않았다. 고를 때마다 그 시점의 해시를 저장한다 — `runnable`이 `source == .bundled`에서 해시를 보지 않으므로 판정은 계약과 같고, 필드가 "마지막으로 고른 시점의 바이트"라는 한 가지 뜻만 갖는다.

### 9. 레인 B가 MightyCore에 더한 순수 규칙 (§6)

앱 모듈에는 테스트 타깃이 없으므로, 화면이 내리는 판단을 `Sources/MightyCore/Styles/StyleSurfaces.swift`로 옮기고 `StyleSurfacesTests`가 단언한다: `StyleChrome`(요청 제목 조립 · 그래프 머리말 · 설치 창 제목 · 출처 배지 · 해시 앞 12자), `StyleChips`(칩 줄과 prominent·추천·되돌리기 칩, 툴팁), `StyleMenu`(선택기 행), `StyleSettingsList`(설정 행), `StyleApprovalCard`(위험 순서의 승인 카드 구역과 두 번째 확인). 태그가 얼리는 §5의 공개 모양은 아니다.
