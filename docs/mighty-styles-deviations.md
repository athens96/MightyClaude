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
