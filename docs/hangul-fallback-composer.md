# 한글 조합 재설계 — 입력기 세션이 죽어도 자모가 분리되지 않게

상태: 설계 확정(2026-09-20), 브랜치 `fix/hangul-fallback-composer`. 기존 진단 문서는 `docs/input-method-diagnostics.md`.

## 1. 문제와 증거

입력창에서 한글이 음절로 조합되지 않고 자모가 하나씩 들어온다(`ㅎㅏㄷㅏ`). 앱이 남긴 진단 두 건(`ime-2026-09-18T115156Z.json`, `ime-2026-09-19T155414Z.json`)의 공통점:

- `insertText`가 계속 `replace=none`으로 호환 자모(U+3131–U+3163)만 넣는다. 키가 입력기를 거치지 않고 두벌식 **키보드 레이아웃**의 글자가 그대로 온 것이다.
- 그 순간 `NSApp.isActive == false`, `keyWindow == nil`, `NSTextInputContext.current == nil` 인데 입력창은 첫 응답자이고 키를 받고 있다.
- 두 번 모두 앱을 띄운 지 몇 시간 뒤였다(4시간 17분, 2시간 13분). 번들 교체 직후가 아니다.

2026-09-20 건에 대해 확인한 것: 마지막 정상 조합(22:49:18)과 고장(00:54:14) 사이에 두 번째 앱 인스턴스, `lsregister`, 테스트 러너 실행은 시스템 로그에 **없다**. ⌘Tab으로 나갔다 돌아와도 고쳐지지 않았다. 그 사이 앱 로그에 `AppleEvent activation suspension timed out`(00:22:28)이 한 번 있었으나 9월 18일 구간에서는 같은 메시지를 찾지 못했다. 즉 **OS 쪽 입력 세션이 왜 죽는지는 아직 확정하지 못했다.** 지금까지의 대책(번들을 바꾸지 말 것, 두 번째 인스턴스를 띄우지 말 것, 입력기 다시 연결 버튼)은 절차와 사후 복구일 뿐이고 재발을 막지 못했다.

## 2. 원칙

앱은 OS 입력기 세션을 통제할 수 없다. 통제할 수 있는 것은 **결과**다. 그래서 입력창의 한글 조합을 "살아 있는 입력기 세션"에 의존하지 않게 만든다: 입력기가 조합을 보내 주면 그대로 쓰고, 보내 주지 않으면 앱이 같은 규칙으로 직접 조합한다. 사용자는 어느 쪽이 동작 중인지 몰라도 된다.

## 3. 구조

### 3.1 `HangulComposer` (MightyCore, 순수)

두벌식 자동기계. 파일 `native/macos/Sources/MightyCore/HangulComposer.swift`.

```swift
public struct HangulComposer: Sendable, Equatable {
    /// 캐럿 앞 `deleteBackward` 개의 UTF-16 단위를 지우고 `insert`를 넣는다.
    /// `previous`는 그 단위들에 들어 있어야 하는 글자, 즉 조합기가 자기가 넣었다고
    /// 믿는 텍스트다(지울 게 없으면 빈 문자열). 앱은 적용 전에 이것을 대조한다.
    public struct Edit: Sendable, Equatable {
        public var deleteBackward: Int; public var insert: String; public var previous: String
    }
    public init()
    public var isComposing: Bool { get }
    /// 호환 자모 한 글자(U+3131...U+3163). 그 밖의 글자는 호출하지 않는다(호출자가 reset).
    public mutating func input(_ jamo: Character) -> Edit
    /// 조합 중이면 자모 하나를 되돌린 Edit, 아니면 nil(에디터의 기본 삭제에 맡긴다).
    public mutating func backspace() -> Edit?
    /// 조합을 끝낸다(캐럿 이동, 자모가 아닌 입력, 포커스 상실, 초안 교체).
    public mutating func reset()
    public static func isCompatibilityJamo(_ c: Character) -> Bool
}
```

**소유 불변식:** `deleteBackward == previous.utf16.count` 이고, `previous`는 항상 조합기가 직전에 내보낸 글자다. 그래서 앱은 지우기 직전에 문서에서 그 범위를 읽어 `previous`와 같은지 확인할 수 있고, 다르면 아무것도 지우지 않는다. 폴백이 따라가지 못하는 편집(⌥⌫, 드래그앤드롭, 서비스 메뉴)이 지나갔을 때 최악이 "자모가 분리된다"에서 멈추고 "남의 글자를 먹는다"로는 가지 않는다.

**종성 이동 뒤 backspace(결정):** 이동이 끝나면 조합 중인 것은 **뒤 음절 하나뿐**이므로 backspace 는 그것만 되돌린다. 가가 ⌫ → 가ㄱ, 달가 ⌫ → 달ㄱ, 갑시 ⌫ → 갑ㅅ. 두벌식 입력기의 조합 버퍼도 현재 음절만 들고 있으므로 이것이 맞다고 보고 테스트에 문자열 그대로 박아 둔다. **Apple 입력기와 대조 확인 필요(수동)** — TextEdit 에서 `ㄷㅏㄹㄱㅏ` 를 치고 ⌫ 한 번. 만약 Apple 이 `닭`으로 되돌린다면 이동을 기록해 두고 첫 backspace 가 그것을 되돌리게 바꿔야 하며, 그때는 조합기가 UTF-16 두 단위를 소유하게 되므로 위 불변식의 테스트도 같이 고쳐야 한다.

규칙은 Apple 두벌식과 같게 한다.

- 초성 + 중성 → 음절. 겹모음: ㅗㅏ=ㅘ, ㅗㅐ=ㅙ, ㅗㅣ=ㅚ, ㅜㅓ=ㅝ, ㅜㅔ=ㅞ, ㅜㅣ=ㅟ, ㅡㅣ=ㅢ. 그 밖의 모음 쌍은 합치지 않는다(ㅏㅣ≠ㅐ).
- 종성: 단자음과 겹받침 ㄳ ㄵ ㄶ ㄺ ㄻ ㄼ ㄽ ㄾ ㄿ ㅀ ㅄ. 종성이 될 수 없는 ㄸ ㅃ ㅉ 는 새 음절을 시작한다. 같은 자음 두 번(ㄱㄱ)은 쌍자음으로 합치지 않는다(쌍자음은 Shift 키로 이미 한 글자로 온다).
- 종성 뒤에 모음이 오면 종성(겹받침이면 뒤쪽 자음)이 다음 음절의 초성으로 넘어간다: 각+ㅏ → 가가, 닭+ㅏ → 달가, 값+ㅣ → 갑시.
- 초성 없이 온 모음은 홑 자모로 남고 겹모음만 합친다. 초성만 있는 상태는 홑 자모로 보인다.
- `backspace()`는 조합 중인 음절을 자모 단위로 되돌린다(값 → 갑 → 가 → ㄱ → 없음). 조합이 끝난 글자는 다루지 않는다.
- 출력은 항상 NFC 완성형 음절(U+AC00–U+D7A3) 또는 호환 자모다. 조합용 자모(U+1100대)는 내보내지 않는다.

### 3.2 `HangulFallback` (MightyCore, 순수) — 언제 개입하나

파일 같은 곳 또는 `HangulFallback.swift`. 입력창이 받는 `insertText` 한 건마다 다음을 넘겨 결정을 받는다.

```swift
public struct HangulFallback: Sendable {
    public enum Decision: Equatable { case passThrough, apply(HangulComposer.Edit) }
    public init()
    /// text: 들어온 문자열, hasReplacementRange: 입력기가 대체 범위를 줬는가, hasMarkedText: marked text 가 있는가,
    /// koreanSource: 현재 입력 소스가 두벌식 한국어인가, sessionSuspect: 이 키에서 세션이 죽어 보였는가.
    public mutating func insert(_ text: String, hasReplacementRange: Bool, hasMarkedText: Bool,
                                koreanSource: Bool, sessionSuspect: Bool) -> Decision
    public mutating func backspace() -> HangulComposer.Edit?
    public mutating func reset()
    /// 증상 감지기가 증거를 잡았다 — 세션이 어떻게 보이든 여기서 조합한다.
    public mutating func confirmBroken()
    /// 직접 조합이 실제로 글자를 바꾼 적이 있는가(진단·안내용).
    public private(set) var hasEngaged: Bool
    public var isComposing: Bool { get }
}
```

결정 규칙:

1. `hasReplacementRange` 이거나 `hasMarkedText` 이면 입력기가 살아 있다 → `reset()`, `confirmBroken` 빗장도 풀고 `passThrough`.
2. 두벌식 한국어 입력 소스가 아니거나, `text`가 호환 자모 **한 글자**가 아니면 → `reset()` 하고 `passThrough`.
3. 개입 조건이 없으면(`!sessionSuspect && !confirmedBroken`) → `reset()` 하고 `passThrough`. 조합 상태를 남기지 않으므로 나중에 조건이 서도 빈 상태에서 시작한다.
4. 그 밖(대체 범위 없는 홑 자모, 개입 조건 충족) → `composer.input(jamo)`. 결과 Edit 이 "지우지 않고 그 자모를 그대로 넣기"와 같으면 `passThrough`(상태만 기억), 아니면 `apply(edit)` 이고 `hasEngaged = true`.

**개입 조건(근거):** 기록된 고장 두 건 모두 문제의 키 순간에 `NSApp.isActive == false` 이고 `NSTextInputContext.current` 가 입력창의 컨텍스트가 아니었다. 그래서 앱은 `sessionSuspect = !NSApp.isActive || NSTextInputContext.current !== inputContext` 를 키마다 계산해 넘긴다. 이 신호가 없는데 자모가 홑으로 오는 경우는 **살아 있는 입력기가 앱 몰래 조합을 끝낸 것**(⌘Tab, 한자 변환)일 수 있고, 그때 다음 자모를 붙이면 틀린 글자가 된다. 대신 알려지지 않은 종류의 죽은 세션도 스스로 낫도록, 기존 증상 감지기(10초 안에 자모 쌍 두 번)가 문제를 올리면 앱이 `confirmBroken()` 을 불러 빗장을 건다. 빗장은 규칙 1(대체 범위나 marked text = 입력기가 살아 있다는 증거)에서 풀린다. `fallback apply …` 기록 줄에 `suspect= active= contextCurrent=` 를 남기므로, 이 조건이 옳았는지는 다음 진단 파일로 확인할 수 있다.

**핵심 성질(테스트로 고정):** 살아 있는 입력기는 음절이 이어질 때 항상 대체 범위를 붙여 보낸다(ㅎ → `하` replace={n,1}). 그래서 정상 이벤트 열에서는 규칙 4가 글자를 바꾸는 일이 없다 — `sessionSuspect` 가 참이든 거짓이든 그렇다. 대체 범위 없이 자음 뒤에 모음이 오는 열은 고장 난 세션에서만 나온다. 즉 이 폴백은 정상일 때 아무것도 하지 않고, 고장일 때만 입력기와 같은 결과를 낸다.

### 3.3 입력창 통합 (`ComposerTextView`)

- `insertText(_:replacementRange:)`: 입력 소스를 **한 번만** 읽어 폴백과 `noteInsert` 양쪽에 넘긴다. `fallback.insert(...)` 결정이 `.apply(edit)` 이면 캐럿 앞 `edit.deleteBackward` 단위를 대체 범위로 삼아 `super.insertText(edit.insert, replacementRange:)` 한 번으로 처리한다(실행 취소·모델 반영이 한 번의 입력 트랜잭션에 묶이도록 기존 `performInputTransaction` 안에서). 선택 영역이 있으면 먼저 `reset()`.
- **소유 확인:** 지우기 전에 그 범위의 글자가 `edit.previous` 와 같은지 본다. 다르면 폴백을 호출 전 상태로 되돌린 뒤 `reset()` 하고 원래 호출을 그대로 통과시킨다. 되돌리기 때문에 적용되지 않은 편집이 `hasEngaged` 를 켜는 일도 없다(삽입·백스페이스 양쪽).
- `deleteBackward(_:)`: marked text 가 없고 `fallback.backspace()` 가 Edit 을 주며 소유 확인을 통과하면 그것을 적용하고, 아니면 `super`.
- `reset()` 시점: 선택/캐럿이 우리 편집이 아닌 이유로 바뀔 때(`setSelectedRange` 계열, 마우스 클릭, 화살표), `resignFirstResponder`, `replaceDraft`, 모델 문자열 적용(`applyModelText`), 붙여넣기, `prepareForSubmission`. 여기에 **앱이 모르는 사이 입력기가 조합을 끝내는 경로**를 더한다: `NSApplication.didResignActive`, 입력창 윈도의 `NSWindow.didResignKey`, `NSTextInputContext.keyboardSelectionDidChange`, 그리고 `doCommand(by:)` 의 `deleteBackward:` 를 뺀 모든 셀렉터(⌥⌫, ⌘⌫, 화살표 등. `insertText:` 는 이 경로로 오지 않는다). 알림 관찰자는 토큰 방식(`addObserver(forName:object:queue:using:)`)으로 `viewWillMove(toWindow:)` 에서 다시 걸고 `deinit` 에서 토큰만 뗀다 — AppKit 이 텍스트 뷰에 걸어 둔 관찰자를 건드리지 않기 위해서다.
- 기존 입력 트랜잭션, 진단 기록(`InputMethodMonitor`)은 그대로 둔다. `noteInsert` 에는 폴백이 적용한 결과가 아니라 **입력기가 보낸 원래 호출**을 기록한다(진단의 의미를 지키기 위해). 조합용 자모(U+1100대)가 보이면 같은 줄에 `conjoining=true` 로 남긴다(폴백 범위 밖, 3.4 참고). 폴백 적용은 `record("fallback apply … suspect= active= contextCurrent=")` 로 따로 남긴다. `noteInsert` 는 증상 감지기가 걸렸는지를 돌려주고, 걸리면 입력창이 `confirmBroken()` 을 부른다.
- 고장 감지 배너: 폴백이 개입 중이면 사용자는 피해를 보지 않으므로 경고 배너 대신 한 줄 안내("입력기 연결이 끊겨 앱이 직접 한글을 조합하고 있습니다 · 입력기 다시 연결")로 바꾸고, 다시 연결을 누르면 그 아래에 시도했다는 줄을 붙인다. 증상 감지기가 먼저 경고를 올린 경우(그것이 빗장을 거는 경로다)에도 폴백이 개입하는 순간 같은 안내로 바뀐다. 진단 파일은 **앱 실행당 한 번**만 저장한다(`fallbackReported`; 감지기가 이미 파일을 남겼으면 폴백은 새로 쓰지 않는다). 그래야 안내를 닫아도 다음 키 입력에 파일이 새로 생기지 않는다.
- **안내는 고장보다 오래 남지 않는다.** 경고든 폴백 안내든, 키 입력 중에 한국어 소스에서 완성형 음절 하나가 대체 범위와 함께 오면(`InputMethodSymptom.provesComposition`) 입력기가 다시 조합하고 있다는 증거이므로 안내를 스스로 내린다(`onRecovered`). 2026-09-20 에 조합이 되살아난 뒤에도 "한글 조합이 끊긴 것 같습니다"가 계속 떠 있었고 다시 연결을 눌러도 문구만 바뀔 뿐 사라지지 않던 문제의 수정이다.
- 폴백의 한국어 판정은 두벌식 소스(`2SetKorean`, `.Korean`)로 좁힌다(`InputMethodSymptom.isTwoSetKoreanInputSource`). 세벌식이나 다른 한글 입력기에 두벌식 규칙을 적용하면 틀린 음절이 나오고, 그냥 통과시키면 자모가 분리될 뿐이기 때문이다. 증상 **감지기**의 넓은 판정(`isKoreanInputSource`)은 기록용이므로 그대로 둔다.

### 3.4 범위 밖(후속)

- SwiftUI `TextField` 를 쓰는 곳(이름 바꾸기, 질문 카드의 직접 입력, 설정)과 터미널 패널. 같은 증상이 나는지 먼저 확인한 뒤 같은 폴백을 붙인다.
- 조합용 자모(U+1100–U+11FF)로 오는 고장 변종. 기록된 두 건은 모두 호환 자모였으므로 폴백은 호환 자모만 본다. 진단 줄의 `conjoining=true` 가 실제로 찍히면 그때 경계에서 호환 자모로 바꿔 넣는다.
- OS 입력 세션이 죽는 근본 원인 추적(`AppleEvent activation suspension timed out`, deprecated `NSApp.activate(ignoringOtherApps:)` 세 곳, 비활성 패널인 펫에서의 `focus()` 경로). 폴백이 들어가면 급하지 않다.

## 4. 검증

- `native/macos/Tests/MightyCoreTests/HangulComposerTests.swift`: 음절·겹모음·종성 불가 자음·홑 모음·backspace 단계, 문장 단위 왕복("자모 안 되는 건 완전히 재설계가 필요해"의 두벌식 키 열 → 같은 문장), **겹받침 열한 개 전부**(형성·종성 이동·backspace 를 구현 표와 무관한 문자열 리터럴로), 드문 모음 ㅑ ㅒ ㅠ, 종성 이동 뒤 backspace 리터럴, 그리고 **소유 불변식 속성 테스트**(두벌식 키 33자로 만들 수 있는 길이 3 이하의 모든 키 열 37,059개에 대해 `deleteBackward == previous.utf16.count`, 문서가 실제로 `previous` 로 끝남, 조합 이전 텍스트를 먹지 않음, backspace 사슬이 nil 로 끝남). 길이 4는 같은 방식으로 약 15초가 걸려 넣지 않았다.
- `HangulFallbackTests.swift`: ① **손으로 적은** 정상 이벤트 열 두 문장("값이 없어?", "과일 말고 감자")에서 `.apply` 가 한 번도 나오지 않는다 — `sessionSuspect` 가 참일 때도 거짓일 때도. 열의 모양은 `docs/input-method-diagnostics.md` 와 기록된 실제 열을 따랐다(첫 자모 raw, 이어지는 키마다 음절 전체를 대체 범위와 함께, 공백·문장부호에서는 음절을 대체 범위로 한 번 더 보낸 뒤 그 글자를 raw 로). 완성된 음절 뒤에 자음이 오는 두 가지 변종(raw 자음만 / 대체 범위로 확정한 뒤 raw 자음)을 모두 담았다. ② 고장 이벤트 열(전부 raw)이 `sessionSuspect` 가 참일 때만 조합되고, 거짓이면 자모 그대로 남는다 ③ 개입 조건: 조건이 없으면 조합 상태도 남기지 않고, `confirmBroken()` 이면 조합하고, 대체 범위 하나가 빗장을 푼다 ④ 자모 두 개 사이의 `reset()`(앱의 새 훅이 하는 일)이 둘을 떼어 놓는다 — 그 훅이 없을 때 어떻게 붙는지도 같이 박아 두었다 ⑤ marked text·대체 범위(길이 0 포함)·비두벌식 소스·여러 글자 입력이 조합을 끊는다 ⑥ 진단 파일 `ime-2026-09-19T155414Z.json` 의 실제 고장 열(ㅎㅏㄷㅏ)이 `하다`가 된다.
- 앱 모듈은 테스트 타깃이 없다. 통합은 코드 리뷰와, 앱을 종료해 새 빌드를 설치한 뒤의 수동 확인으로 검증한다. 실행 중인 앱 옆에서 두 번째 인스턴스(스모크·진단 실행)를 띄우지 않는다.
- 명령: `export DEVELOPER_DIR=/Library/Developer/CommandLineTools && bash scripts/test-native-macos.sh --filter Hangul`, 이어서 전체 테스트와 `cd native/macos && swift build --product MightyClaude`(번들 없이 앱 타깃 컴파일만), 설치는 `bash scripts/install-macos.sh`.

## 5. 프리즈와의 관계

이 변경은 `native/macos/Sources/**` 를 건드리므로 스타일 엔진 프리즈(`scripts/check-style-freeze.sh`, 태그 `mighty-style-engine-v1`)의 허용 범위 밖이다. 스타일 엔진 파일은 건드리지 않지만 검사는 저장소 전체를 본다. main 병합은 `mighty-style-engine-v2` 묶음에 넣을지 따로 할지 그때 정한다. 그때까지 이 브랜치에서만 작업한다.
