# 마이티 스타일 엔진 — 계약에서 벗어난 자리

docs/mighty-styles.md를 구현하면서 계약이 모호하거나 이 환경에서 사실이 아니었던 지점들이다. 각 항목은 **계약의 절 → 실제로 한 일 → 왜**로 적는다. 리드가 계약에 되접어 넣을 대상이다.

## 레인 A

### 1. 폴더의 `linkCount == 1`은 이 파일 시스템에서 성립하지 않는다 (§1.8 · §3.1)

계약은 스캔하는 각 항목에 대해 `isSymbolicLink == false`, `isRegularFile == true`(폴더는 `isDirectory == true`), **`linkCount == 1`**을 요구한다. 실측(APFS, macOS 15): 디렉터리의 `st_nlink`는 언제나 2 이상이다(`.`과 `..`, 하위 폴더마다 하나 더). 규칙을 글자 그대로 적용하면 **모든 폴더가 거부되어** 케이스북이 영원히 `absent`가 된다.

한 일: `linkCount == 1`은 **일반 파일에만** 적용하고(`StylePathBoundary.isPlainFile`), 폴더에는 링크 아님 + 디렉터리임만 검사한다(`isPlainDirectory`). 하드 링크로 파일을 밀반입하는 것은 여전히 막히고, 하드 링크된 **디렉터리**는 macOS에서 일반 사용자가 만들 수 없다.

### 2. 번들 리소스의 실제 경로와 탐색 후보 (§3.2)

계약은 `<후보>/MightyClaude_MightyCore.bundle/Styles`를 찾으라고 하고, 후보를 `Bundle.main.resourceURL` · `Bundle(for:).resourceURL` · `Bundle.main.bundleURL` 셋으로 고정한다. 실측: ① macOS에서 SwiftPM이 만드는 리소스 번들은 **`Contents/Resources/Styles`** 구조다(평평한 `Styles`가 아니다). ② `swift test`에서 번들은 테스트 러너(`MightyCoreTests.xctest`)의 **형제**로 놓이므로 세 후보 중 어느 것도 그 폴더를 가리키지 않는다.

한 일: 후보에 `Bundle(for:).bundleURL` · 실행 파일의 폴더를 더하고, 각 후보 아래에서 `Contents/Resources/Styles`와 평평한 `Styles`를 **둘 다** 본다. 트랩 없는 탐색이라는 원칙은 그대로다(`Bundle.module` 미사용, 못 찾으면 빈 배열). `scripts/build-macos.sh`의 존재 확인도 두 경로를 모두 받는다. `.app`을 **담고 있는** 폴더는 후보에서 뺐다 — 리뷰 지적대로 거기 떨어진 번들이 사전 승인된 `bundled` 출처로 읽히며, 올바른 배치에서는 쓰이지 않는 후보다. 계약 §3.2에 그 문장을 되접어 넣었다.

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

### 12. 키 이름의 한도는 400이 아니라 64다 (§1.11)

§1.11은 "어떤 문자열도 400자를 넘지 않는다. JSON 키 이름도 문자열과 똑같이 다룬다"고만 적어, 키의 한도를 400으로 읽을 수 있었다.

한 일: 키는 **64자**다. 스키마 1의 키는 전부 짧은 영문 낱말이고 작성자가 고르는 키는 id(≤40)뿐이므로 400은 아무것도 막지 않는 숫자였다. 계약 §1.11의 표에 키 전용 줄로 되접어 넣었다.

### 13. `…`는 잘린 길이 **안**에 든다 (§2 · §1.8)

"64자로 자르고(넘으면 뒤에 `…`)"는 65자로도, 64자로도 읽혔다. 구현은 65였다.

한 일: 화면에 나가는 길이가 정확히 한도가 되도록 `prefix(limit - 1) + "…"`로 바꿨다. §1.8의 80자도 같다. 두 문장 모두 계약에 못박았다.

### 14. §3.3의 "이름순"은 §1.10의 "id 사전순"과 다른 규칙이다 — 바꾸지 않았다

리뷰가 `StyleRegistry.make`의 충돌 정렬(파일 이름순)이 §1.10의 "같은 출처 안에서는 id 사전순"과 어긋난다고 보았다. 두 규칙은 서로 다른 질문에 답한다: §1.10은 **요청 제목 훑기의 우선순위**이고(그 정렬은 `runnableInPrecedence`가 id로 한다), §3.3은 **같은 id를 주장하는 두 파일 중 누가 이기는가**이며 계약이 "이름순으로 앞선 파일이 이긴다"고 직접 정해 두었다. 충돌 상황에서 두 파일의 id는 정의상 같으므로 id 정렬은 결정적이지도 않다. 코드는 그대로 두었다.

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

앱 모듈에는 테스트 타깃이 없으므로, 화면이 내리는 판단을 `Sources/MightyCore/Styles/StyleSurfaces.swift`로 옮기고 `StyleSurfacesTests`가 단언한다: `StyleChrome`(요청 제목 조립 · 그래프 머리말 · 설치 창 제목 · 출처 배지 · 해시 앞 12자), `StyleChips`(칩 줄과 prominent·추천·되돌리기 칩, 툴팁, 격자/줄, 실행 중 표시), `StyleMenu`(선택기 행), `StyleSettingsList`(설정 행), `StyleApprovalCard`(위험 순서의 승인 카드 구역과 두 번째 확인). 리뷰 뒤에 `StyleComposer.enter`(질문 → Enter 규칙의 순서)와 `MobileRemoteSupport.guidedDecision`(`/guided`의 관문)도 같은 이유로 옮겨 `StyleEvaluatorTests`·`MobileRemoteExtensionTests`가 단언한다. 태그가 얼리는 §5의 공개 모양은 아니다.

### 10. 행동 줄의 격자/줄과 실행 중 표시는 계약이 정하지 않았다 (§6.1의 7번)

§6.1의 7번은 칩 줄의 **내용**만 적고, 그것을 격자로 그리는지 한 줄로 그리는지, 실행 중에 무엇이 보이는지는 적지 않았다. 첫 구현은 개수(`groups > 1 && actions > 6`)로 갈라, Paperthin의 네 그룹 중 셋이 줄로 그려지며 오늘 모습에서 벗어났다. 실행 중에도 Ouroboros의 칩이 그대로 남아, 오늘 있던 진행 표시가 사라졌다.

한 일: **그룹 지도를 그리는 스타일의 행동 줄은 언제나 격자**(= 오늘 Paperthin), **`byPhase` 스타일은 실행 중에 칩 대신 진행 표시와 `guidance.running`**(= 오늘 Ouroboros), `byGroup`은 실행 중에도 칩을 그대로 둔다(= 오늘 Paperthin, 누른 것이 다음 요청으로 줄을 선다). 두 규칙 모두 계약 §6.1에 되접어 넣었고, 같은 규칙이 폰 투영(§7.3)에도 간다.

### 11. `rules.recommend.group`은 추천을 막지 않는다 (§1.6)

§1.6은 "`group`이 있으면 그 그룹을 보고 있을 때만 추천이 나온다"고 적었다. 폰 투영은 고른 그룹이 없어 언제나 `selectedGroupId: nil`로 부르므로, 그 규칙대로면 케이스북이 `absent`일 때(기본 그룹이 `depth`) 폰의 `paperthin.recommended`가 `re0-plan`에서 `null`로 떨어진다 — §8.1의 완료 기준 1이 금지하는 값 변화다.

한 일: 추천 값은 기능의 상태만 본다. `group`은 **첨부 줄이 어느 그룹에 붙는지**만 말한다(§6.1의 6번). 계약 §1.6에 되접어 넣었다.

## 레인 C

### 1. `tintColor(name)`은 `theme/index.ts`에 있다 (§7.6)

§7.6의 표는 `tintColor(name)`을 `mobile/src/lib/styles.ts`에 놓는다.

한 일: `mobile/src/theme/index.ts`에 두었다. 이 함수가 돌려주는 것은 문자열이 아니라 팔레트 색이고, 팔레트는 `usePalette`가 라이트·다크로 나누어 주는 테마 값이다. `styles.ts`는 순수 파싱·뷰 모델만 담고 테마를 import하지 않는다. 계약이 요구하는 동작(1.10의 9개 이름, 모르는 이름은 accent)은 그대로다.

### 2. `icon`은 폰의 타입에 아예 없다 (§7.3)

§7.3의 페이로드에는 `style.icon`·`actions[].icon`·`presentation.icon`이 있다. §1.10이 "폰은 SF Symbol 렌더러가 없으므로 `icon`을 무시한다"고 적었으므로 무시하는 것 자체는 계약대로지만, 폰은 그 필드를 **타입에도 두지 않는다** — 파싱 단계에서 버린다. 골든은 그대로 통과한다.

### 3. 지도가 떠 있으면 어떤 칩도 채워 그리지 않는다 (§7.3)

§7.3은 "`prominent`는 `next`의 첫 항목에만 true다. 폰은 둘 중 무엇을 봐도 된다"고 적어 선택을 폰에 맡긴다. 폰의 규칙은 이렇다: **그룹 지도가 떠 있으면 아무 칩도 prominent가 아니고**, 지도가 없으면 호스트가 표시한 행동, 없으면 목록의 머리다.

근거: Paperthin의 `next`는 `byGroup`이라 호스트는 **처음 열린 그룹의** 첫 행동에 `prominent`를 건다. 그것을 채워 그리면 사용자가 다른 그룹을 누르는 순간 그 칩이 화면에서 사라져, 같은 제스처가 "주 행동"을 있게 했다 없앴다 한다. 지도에서는 강조를 그룹 칸이 맡는다 — 오늘의 Paperthin 모습 그대로다. `styles.test.ts`와 `styles-thirdparty-contract.test.ts`가 이 규칙을 단언한다.

### 4. 행동 상세 시트에 "모델도 스스로 꺼내 씀"이 없다 (§6.1의 7번 · 레인 B의 2번)

레인 B가 Mac에서 내린 결정과 같다: 호출자 줄은 `userInvoked`일 때만 나오고(`사람만 부를 수 있는 행동`), 그 반대 경우의 옛 Paperthin 문구 `모델도 스스로 꺼내 씀`은 그리지 않는다. 두 화면이 같은 카탈로그를 같은 말로 설명한다.

### 5. 안내 줄은 세 줄이 될 수 있고, 길게 누르기 힌트는 앱의 것이다 (§1.7 · §7.3)

옛 패널은 매니페스트의 `guidance`와 "입력창의 내용을 함께 보냅니다" 중 **하나만** 그렸다. 지금은 둘 다 그린다 — 서로 다른 질문에 답하는 줄이고, `guidance`를 쓴 스타일이 그 때문에 입력창 텍스트를 안 받게 되는 것은 아니기 때문이다.

레거시 어댑터의 Paperthin `guidance`는 번들 매니페스트의 `guidance.next`와 **글자 그대로 같게** 맞췄다(`… 비워 두면 스킬만 보냅니다.`). 거기 붙어 있던 `길게 누르면 설명이 나옵니다.`는 매니페스트 문자열이 아니라 앱 크롬이므로, 칩이 하나라도 있으면 **모든 스타일에서** 패널이 따로 그린다. 그 전에는 범용 경로(= 새 호스트)에서 상세 시트를 찾을 길이 없었다.

### 6. 패널 본문은 창 높이의 40%로 잘리고 세로로 스크롤한다 (§7.6)

계약에 높이 규칙은 없다. 한 그룹이 §1.11의 상한인 행동 100개를 담을 수 있고 그룹은 16개까지인데, 패널은 `FlatList` 바깥에서 입력창 위에 앉아 있다. 한도가 없으면 칩 줄이 입력창과 기록을 화면 밖으로 밀어내고 되돌릴 방법이 없다. 머리글 줄은 고정, 나머지는 `maxHeight: 창 높이 × 0.4`의 `ScrollView` 안이다.

### 7. "더 보기"는 칩이 하나도 없어도 나온다 (§7.3)

`next`가 비는 것은 계약이 허용하는 상태다(실행 중, 또는 단계 규칙이 아무것도 내지 않을 때). 그때도 카탈로그는 `actions`에 그대로 있으므로 "더 보기"는 칩 줄 밖에서 `rest`가 비어 있지 않은 한 언제나 그려진다. 실행 중이면 그 자리에 스피너와 한 줄이 함께 나온다.

### 8. 라우트가 400을 낼 id에는 칩을 그리지 않는다 (§7.5)

§7.5의 `actionId` 형식 검사(`^[A-Za-z0-9][A-Za-z0-9_.:-]{0,63}$`)를 폰도 파싱 단계에서 적용해, 맞지 않는 행동은 카탈로그에서 버린다(그 결과 `groups`·`next`·`recommended`에서도 사라진다). id는 64자가 아니라 **여유를 두고 읽은 뒤** 검사한다 — 64자에서 자르면 다른 id가 되고, 잘린 머리가 진짜 행동과 같으면 그 이름으로 칩이 그려진다.

### 9. 읽을 수 없는 `panel`은 레거시로 내려가지 않고 그 사실을 그린다 (§7.3 · §7.4)

호스트가 `style`을 광고하고 `panel`을 보냈는데 정규화기가 그것을 거절하면, 폰은 내장 두 스타일의 레거시 페이로드로 조용히 내려가지 않는다. 그렇게 하면 단계 표시줄도 그룹도 첨부도 없는 채로 **정상처럼 보이는** 패널이 뜨고, 무엇이 빠졌는지 알 길이 없다. 대신 준비 안 됨 상태의 패널 하나를 그린다: `호스트가 보낸 스타일 정보를 읽을 수 없습니다.` + 출처 불명 배지, 칩 없음. `style`을 광고하지 않은 호스트에는 영향이 없다.

이를 위해 정규화기가 폰 안에서만 쓰는 표식 `MobileMighty.panelUnreadable`을 둔다. 와이어 필드가 아니다.

### 10. `/guided` 본문은 호스트 버전을 따른다 (§7.5)

`style` capability가 없는 호스트의 라우트는 `{style, skill}`만 디코딩하고 다른 본문에는 400을 낸다. 그래서 본문은 앱이 아니라 **호스트**를 따른다: `style`을 광고한 호스트에는 `{styleId, actionId}`, 그 밖에는 `{style, skill}`. 옛 형식에 도달할 수 있는 id는 내장 둘뿐이다 — 그 호스트는 다른 스타일의 패널을 보내지 않으므로 그릴 칩 자체가 없다. 패널을 고른 것과 **같은** capability 값이 본문도 고른다.

이를 위해 `GuidedRequest.legacy`(폰 안에서만 쓰는 표식, 와이어 필드가 아니다)를 둔다. `POST /settings`는 이미 `canStyle ? styleId : mightyStyle`로 갈라져 있어 그대로 둔다.

### 11. 첨부의 `readOnly`와 제목 (§7.3)

`readOnly`는 칩의 읽기 전용 표식과 같은 `👁`로 그린다 — 파싱만 하고 그리지 않으면 죽은 필드가 된다. 레거시 케이스북 첨부의 제목은 호스트의 `StyleCapabilities.title(of:)`와 같이 `.local.md`를 떼고, `id`에는 파일명을 그대로 둔다. 같은 케이스북이 호스트 버전에 따라 다른 이름으로 보이지 않게 하기 위해서다.

### 12. 고른 그룹은 실행 창 화면이 갖는다 (§7.6)

`AskUserQuestion` 카드가 오면 가이드 패널은 화면에서 비켜서는데, 그때 패널이 그룹 선택을 함께 들고 있으면 사용자가 답을 하고 돌아왔을 때 호스트의 그룹으로 되돌아가 있다. 선택 상태는 `session/[sessionId].tsx`가 갖고 패널에 내려 준다. 스타일이 바뀌면 화면이 그것을 지운다.
