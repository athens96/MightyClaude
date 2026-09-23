# 마이티 스타일 엔진 후속 작업 (규칙 어휘 확장)

docs/mighty-styles.md 10장의 사본이다. 고정된 어휘로 **지금도 표현되지 않는다고 이미 아는 것들**이며, 이번 범위 밖이고 확장은 `schema: 2`로 간다. 고정 태그 이후에도 고칠 수 있는 문서다(§8.3).

고정 기준(현재, v4 — 2026-09-22): 고정 전 마지막 커밋 `9bfc93accf039d3973fcf1cd4f7a08056ae4e95f`, 태그 `mighty-style-engine-v4`가 가리키는 커밋 `f838331ebebd97d9575ead8f8ea6913334afa39d`(`styles/FREEZE`만 더한 커밋). v3 이후 바뀐 엔진 쪽 파일은 아래 'v4에 묶어 처리한 것'에 적었다(매니페스트의 `job` 선언, 오류 코드 48번째). 허용 목록은 v3과 같다.

고정 기준(이전, v3 — 2026-09-20): 고정 전 마지막 커밋 `2ae58dcf3e6422eabe3f153cc555350a9a315522`, 태그 `mighty-style-engine-v3`가 가리키는 커밋 `f226e31cc146ba0c7dadd3ba8baaeae3ced05802`(`styles/FREEZE`만 더한 커밋). v2 이후 바뀐 엔진 쪽 파일은 아래 'v3에 묶어 처리한 것'에 적었다. 허용 목록에 Windows 전용 경로가 더해졌다(§8.3).

고정 기준(이전, v2 — 2026-09-20): 고정 전 마지막 커밋 `19748917bc0e99ac6f45e6857d9c4a0cb08353e8`, 태그 `mighty-style-engine-v2`가 가리키는 커밋 `24a07e02559081d43fdb4337ebdc0162897aff44`(`styles/FREEZE`만 더한 커밋). v1 이후 스타일 엔진 파일(`native/macos/Sources/MightyCore/Styles/**`)·스키마·규칙 어휘는 바뀌지 않았다. v2를 건 이유는 검사가 저장소 전체를 보는데 main에 입력창의 한글 직접 조합(`docs/hangul-fallback-composer.md`)이 들어왔기 때문이다.

고정 기준(이전, v1 — 2026-09-19): 엔진의 마지막 커밋 `306d8be3f0c56d2466bb3aac025e085e40ecc7b3`, 태그 `mighty-style-engine-v1`이 가리키는 커밋 `b767328c5e47654fffbde224488e5865a139ed8f`(`styles/FREEZE`만 더한 커밋). `scripts/check-style-freeze.sh`는 FREEZE를 새로 쓰고 태그를 다시 거는 것까지는 막지 못하므로, 태그가 이 두 값과 다르면 고정이 깨진 것이다.

1. **진짜 2축 그리드.** `groups[].axis`는 문자열 한 줄이라 정렬만 돕는다. Paperthin의 2×2는 오늘처럼 버튼 4개 줄로 그린다. `axisX`/`axisY`가 필요하다.
2. **중첩 그룹.** 그룹 안의 그룹이 없다. gstack처럼 행동이 30개를 넘는 카탈로그는 1단 그룹으로 평평해진다.
3. **치환자 하나뿐.** `{text}` 외에 `{path}`·`{flag}` 같은 두 번째 입력 칸이 없다. `/oh-my-claudecode:execute <plan> --model opus`처럼 인자가 둘인 호출은 한 줄 자유 텍스트로 내려간다.
4. **조건부 다음 행동.** `NextRule`은 단계 또는 그룹만 본다. "준비물이 미충족이면 설치 행동만", "케이스북이 `complete`면 다른 목록"은 못 쓴다.
5. **상태를 읽는 단계 계산.** `PhaseRule`은 과거 요청 텍스트만 본다. MCP 도구의 응답(예: omc의 `state_get_status`)이나 파일 상태로 단계를 정할 수 없다. 그래서 루프형 스타일의 단계는 "마지막으로 부른 스킬"로만 읽힌다.
6. **루프 진행·중지.** 오래 도는 행동(`ralph`, `autopilot`, `re0-loop`)의 진행률이나 전용 중지 버튼을 선언할 수 없다. 취소도 그냥 또 하나의 행동이다.
7. **토글형 행동.** `/freeze`↔`/unfreeze`, `/guard`↔`/careful`처럼 켜고 끄는 짝을 하나의 on/off로 표현할 수 없고 상태를 되읽을 수도 없다.
8. **추천 규칙의 조합.** 내장 기능 하나의 상태만 본다. 둘을 조합하거나, 최근 사용 순으로 정렬하거나, 여러 개를 추천할 수 없다.
9. **단계별 자동 허용.** `autoAllow`는 스타일 전체에 걸린다. "인터뷰 단계에서만 이 도구"는 못 쓴다. 읽기 전용/쓰기 분류도 없어서, 소속 규칙(§1.9) 안에서는 작성자가 무엇이든 고를 수 있다.
10. **설치 명령 하나.** 준비물이 여럿이어도 설치 버튼은 하나다. probe별 설치 명령이 없다(per-probe `install` 플래그는 "이 실패를 그 하나의 명령이 고치는가"만 말한다).
11. **내장 기능 하나.** `paperthin.casebook`뿐이다. "최근 커밋 읽기", "열린 PR 읽기", "플러그인 상태 읽기" 같은 것은 표현할 수 없다.
12. **적격성 고정.** 로컬 Claude 실행 창 + 마이티 보기가 아닌 곳(Codex·Gemini·원격 워크스페이스·터미널 창)에 스타일을 열어 줄 수 없다.
13. **질문 패널 손대기.** `AskUserQuestion` 패널의 문구·배치는 매니페스트가 못 건드린다.
14. **그래프 구조.** 요청 블록의 제목 **접두사**·아이콘·색만 바꿀 수 있고, `요청 N · <provider>` 꼬리나 단계 레인이나 새 블록 종류는 못 만든다.
15. **접두사 없는 맨 키워드 인식.** `recognition.prefixes`는 비울 수 없고 이름은 한 단어다(첫 공백 앞까지). oh-my-claudecode가 문서화한 `autopilot`·`ralplan`·`deepsearch`·`cancelomc` 같은 **맨 키워드 트리거**와 `deep interview` 같은 **두 단어 이름**은 인식되지 않는다 — 매니페스트에 그 행동이 있어도 사용자가 그렇게 치면 제목이 안 붙고 단계가 안 움직인다. `{"kind":"bareWords","names":[…]}`가 필요하다.
16. **단계 막대와 그룹 선택기의 공존.** `byPhase`와 `byGroup`은 배타적이고(§1.4), `byPhase`를 쓰면 `groups`는 승인 카드와 폰 페이로드의 설명으로만 남는다. "단계로 진행하면서 그룹으로 고르기"는 못 쓴다.
17. **닫힌 아이콘 목록.** 스푸핑을 막기 위해 `icon`을 33개로 고정했다(§1.10). 임의의 SF Symbol을 쓰려는 스타일은 `glyph`(이모지 1자)로 내려가거나 목록 확장을 기다려야 한다. 목록 확장은 앱 업데이트지 매니페스트가 아니다.
18. **`requiresText`는 라우트에서 강제되지 않는다.** UI 힌트이며 `/guided`는 보지 않는다(§1.3.3). "이 행동은 폰에서도 반드시 글이 있어야 한다"는 표현할 수 없다.

---

## 고정 이후로 미룬 것 (이번 리뷰에서 나온 것)

19. **플러그인 이름의 `_` 때문에 소속 규칙이 정확한 이름 하나를 짚지 못한다.** §1.9는 `prefix`가 `@`로 끝나는 probe만 소속을 만들도록 좁혔지만(`prefix: "a"` 하나가 `plugin_a_b_*`를 주장하는 것은 막힌다), `prefix: "a_b@"`와 `prefix: "a@"` 아래에 설치된 `a_b`는 여전히 구별되지 않는다 — Claude Code의 플러그인 이름에 `_`가 허용되는 한, `plugin_<P>_` 접두사 비교로는 `P`와 `P_x`의 경계를 세울 수 없다. 정확히 닫으려면 probe를 **설치된 플러그인 키로 풀어** 그 키의 정확한 `plugin_<키>_` 조각과 맞춰야 하는데, 검증은 디스크를 보지 않으므로 그 정보가 없다. 오늘 설치 가능한 어떤 플러그인 조합으로도 닿지 않는다(`plugin_oh-my-claudecode_t`는 `oh-my-claudecode`에서만 갈라진다).
20. **Ghostty의 위험한 붙여넣기 확인이 자동 응답된다.** `LocalTerminalSession`이 `request.kind == .paste`인 확인을 무조건 승인한다. 매니페스트에서는 닿지 않는다 — U+000A/U+000D/U+2028/U+2029는 두 겹으로 거부되고 ESC(0x1B)는 금지 문자라 `ESC[201~` 탈출을 쓸 수 없다 — 하지만 **사용자 자신의 클립보드**에 대한 터미널의 보호도 함께 없앤다. 이 기능 이전부터 있던 동작이므로 이번 범위 밖이다. 고치려면 앱이 스스로 넣는 `initialInput` 붙여넣기만 자동 응답하고 나머지는 확인을 띄워야 한다.

---

## 완료 기준 2에서 실제로 낮춘 것 (`styles/oh-my-claudecode.json` · `styles/gstack.json`)

엔진은 고정된 그대로 두고 매니페스트와 테스트만 더해 두 스타일을 붙이면서 **어휘로 담기지 않아 내려서 담은 것들**이다. 앞의 항목과 겹치면 그 번호를 적고, 이번에 확인된 **구체적 재현**을 덧붙인다.

21. **진입 단계의 `next` 행은 영원히 그려지지 않는다.** `StyleEvaluator.visibleActions`는 `startActions`가 비어 있지 않으면 그것만 내므로(`StyleEvaluator.swift:118-122`), `rules.start.phase == P`인 단계 `P`의 `rules.next.map[P]`는 맥에서도 폰 투영에서도 도달할 수 없다. 재현: `rules.start = {"kind":"actions","phase":"plan","actions":["plan"]}` + `rules.next.map.plan = ["execute"]`인 매니페스트에서 `visibleActions(phase: plan, group: nil, running: false).map(\.id) == ["plan"]`. §1.6이 그렇게 정의한 동작이지 버그가 아니지만, "진입에는 이 칩, 그 단계로 돌아오면 저 칩"을 한 단계로 말할 수 없다는 뜻이다(부록 A.3의 스케치가 정확히 이 죽은 행을 갖고 있었다). 이번에 내린 방법 둘: oh-my-claudecode는 `goal` 진입 단계를 따로 두어 `plan`의 다음 행을 살렸고, gstack은 `rules.start`를 `none`으로 두어 `plan` 행 자체를 진입 메뉴로 썼다(그 대신 되돌리기 칩을 잃는다 — `resetTitle`은 `start.kind == "actions"`에만 있다).
22. **비번들 스타일은 번들이 이미 쓰는 bare 이름의 제목·아이콘·색을 가질 수 없다.** 요청 제목은 레지스트리가 `bundled` > `user` > `workspace` 순으로 훑으므로(§1.10), bare `/name` 인식을 쓰는 제3자 카탈로그가 번들과 같은 이름을 갖게 되는 순간 그 이름은 조용히 번들의 것이 된다. 재현: gstack만 돌고 있는 실행 창에서 `/re0 docs/spec.md`를 보내면 `StyleRegistry.requestTitle`이 Paperthin의 `♻️ re0`를 돌려준다(`StylesThirdPartyGstackTests.requestTitlesAreGlyphedAndSweptByPrecedence`). 오늘은 두 카탈로그가 겹치지 않아 무해하지만, gstack이 나중에 `nba`·`prism` 같은 이름의 스킬을 추가하면 그 칩은 자기 제목을 잃는다. 단계 계산과 Enter 규칙은 실행 창의 매니페스트 하나만 보므로 영향이 없다.
23. **oh-my-claudecode의 맨 키워드 트리거를 담지 못한다**(항목 15). 재현: `evaluator.recognised(inPrompt: "autopilot 결제 모듈") == nil`. 매니페스트에 `autopilot` 행동이 있고 버튼으로는 정상 동작하지만, 플러그인이 문서화한 `autopilot`·`ralplan`·`deepsearch`·`cancelomc`를 접두사 없이 치면 제목도 안 붙고 단계도 움직이지 않는다. `deep interview`처럼 두 단어인 이름은 `recognition`이 첫 공백 앞까지만 읽으므로 구조적으로 불가능하다.
24. **둘째 입력 칸이 없다**(항목 3). `/oh-my-claudecode:execute <계획> --model opus`, `/oh-my-claudecode:team 3:executor <작업>`, gstack `/office-hours`의 두 모드처럼 인자가 둘인 호출은 한 줄 자유 텍스트로 내려갔다. 재현: `actions[].prompt`의 닫힌 치환 집합이 `{text}` 하나뿐이라 `"/oh-my-claudecode:execute {text} --model {model}"`은 `E_PROMPT_PLACEHOLDER`로 거부된다.
25. **루프의 진행률도 전용 중지도 선언할 수 없다**(항목 6). `ralph`·`autopilot`·`qa`는 스스로 도는 루프인데 패널은 실행 중 표시 외에 아무것도 모른다. omc의 `cancel`은 그냥 또 하나의 칩이고, gstack에는 중지에 해당하는 스킬이 아예 없다.
26. **토글형 행동을 한 칩으로 못 묶는다**(항목 7). gstack의 `/freeze`↔`/unfreeze`는 세션 전역 상태를 바꾸는 짝인데 서로 다른 칩 두 개로 뒀고, 패널은 지금 고정되어 있는지 되읽지 못한다. 같은 이유로 `/guard`·`/careful`은 아예 담지 않았다.
27. **단계가 상태가 아니라 "마지막으로 부른 스킬"로만 읽힌다**(항목 5). omc는 `mcp__plugin_oh-my-claudecode_t__state_get_status`를 자동 허용까지 해 두고도 그 응답으로 단계를 정할 수 없다. 재현: `/oh-my-claudecode:execute` 뒤에 실행이 실패해도 `currentPhase`는 계속 `execute`다. gstack도 같아서, 사람이 `/ship`을 부르지 않고 직접 머지하면 단계가 `qa`에 머문다.
28. **ZWJ로 묶인 이모지 `glyph`가 폰에서 두 자로 갈라진다.** §1.11은 `actions[].glyph`를 금지 문자 검사에서 **명시적으로 면제**하고(근거: U+200D가 여성 기술자 이모지를 한 grapheme으로 묶는다) 맥 디코더도 `StyleText.isEmojiGlyph` 하나로만 판정해 통과시킨다. 그런데 폰의 두 번째 겹인 `mobile/src/lib/styles.ts`의 `UNSAFE_INLINE`은 U+200B–U+200F를 통째로 지우고 `parseAction`이 `glyph`에도 그 함수를 쓴다. 재현: 스칼라 세 개(U+1F9D1, U+200D, U+1F4BB)로 된 glyph를 담아 골든을 기록하면 맥은 grapheme 1개로 그리고 폰의 `normalizeStylePanel`은 U+200D가 빠진 grapheme 2개를 돌려준다(`node -e`로 `UNSAFE_INLINE` 정규식에 직접 먹여 확인). 맥과 폰이 같은 칩을 다르게 그리는, 이번에 확인된 유일한 자리다. **엔진도 폰도 고치지 않았다**(둘 다 고정 범위). 이번에는 gstack의 `plan-devex-review` 글리프를 ZWJ 없는 톱니바퀴(U+2699 U+FE0F)로 내려 담았다. 고치려면 폰의 `parseAction`이 `glyph`만은 제어 문자 제거 대신 'grapheme 하나인가'로 판정해야 한다 — §1.10이 맥에서 하는 것과 같은 판정이다.
29. **카탈로그를 잘라 담았고 그룹은 하나다**(항목 2·16). 설치본의 스킬은 oh-my-claudecode 39개·gstack 90개가 넘는데 각각 16개·27개만 담았다. `rules.next`가 `byPhase`라 그룹 지도는 그려지지 않으므로(§1.4), `axis` 없는 그룹을 여러 개 선언하면 맥에서도 폰에서도 안 그려지고 승인 카드에서만 진짜 구획인 척하는 죽은 JSON이 된다 — 그래서 둘 다 `flow` 그룹 하나로 평평하다. 나머지 스킬은 입력창에 직접 쳐서 쓴다(gstack은 bare `/name`이라 요청은 정상으로 나가고 단계만 반응하지 않는다).

## v3에 묶어 처리한 것 (2026-09-20)

`mighty-style-engine-v3`는 v2 이후 처음으로 **엔진 쪽 파일을 바꾼** 고정이다: `MightyCore/Styles/StyleSurfaces.swift`(연결 규칙 `StyleLaunchWiring`)와 폰의 `mobile/src/lib/styles.ts`(글리프 판정). 스키마·규칙 어휘·47개 오류 코드·투영 규칙은 그대로다.

- 항목 28의 폰 결함: `parseAction`이 `glyph`를 "이모지 한 글자인가"로 판정한다(맥의 `StyleText.isEmojiGlyph`와 같은 기준). 한 줄 텍스트에서는 ZWJ(U+200D)를 보존하고 블록 텍스트에서는 지운다.
- 테스트 빈틈: `StylesV3BundleTests`가 Ouroboros의 단계 6개를 한 줄로 단언하고, 행동 101개짜리 매니페스트가 `E_LIMIT`으로 거부되는 것을 검사한다.
- `AppStore+Styles`의 연결 규칙 셋 — 승인 뒤 실행 창 결속, 저장소 매니페스트 미복사, 일반 실행 창의 제목 앞머리 차단 — 을 `MightyCore`의 `StyleLaunchWiring`으로 옮겼고 앱(`AppStore+Styles.swift`, `SessionPaneView.swift`)이 그 함수를 직접 부른다. `StylesV3BundleTests`가 규칙마다 하나씩 검사한다. 앱 모듈 자체에는 여전히 테스트 타깃이 없으므로, 규칙을 부르는 자리(승인 시트의 흐름, 패널 렌더링)는 화면 확인 대상이다.
- 허용 목록에 Windows 전용 경로를 더했고 CI 설정을 `native-macos.yml`/`native-windows.yml`로 나눴다(§8.3). 검사 방식(저장소 전체 diff + 허용 목록)은 그대로 둔다는 결정이다(2026-09-20).
- 화면 확인은 v3 전에 하지 못했다. 거기서 나오는 문제는 아래 다음 고정으로 간다.

## v4에 묶어 처리한 것 (2026-09-22)

항목 5(상태를 읽는 단계 계산)와 6(루프 진행·중지)의 범위 안에서, 패널이 백그라운드 잡을 인식하는 선언을 추가했다. 풀어낸 것과 수용된 한계는 아래와 같다.

**풀어낸 것.** 매니페스트의 선택적 `job` 필드(§1.13)를 도입했다. 도구 이름을 매처로 선언하면 엔진이 실행 창 로그에서 가장 최신 열림/닫힘 결과를 찾아 잡 상태를 결정한다. 잡이 열린 동안 패널은 `whileOpen` 목록만 보여 주고 `job.guidance` 한 줄을 쓴다. Ouroboros 번들 매니페스트가 `ouroboros_start_*` 네 도구를 열림, `ouroboros_job_result` · `ouroboros_cancel_job` · 터미널 `ouroboros_job_status`를 닫힘으로 선언하고, `status` · `cancel` · `unstuck`을 while-open 목록으로 쓴다. `cancel` 행동(`/ouroboros:cancel`)과 `run`을 run/evolve next 목록에 추가했다. 매처에 `contains`와 `notContains`를 동시에 쓰면 `E_JOB_MATCHER_LITERAL`(오류 코드 48번째)로 거부된다. 폰은 호스트가 투영한 `guidance`와 `next` 목록을 그대로 렌더링하며 트랜스크립트를 직접 파싱하지 않는다.

**수용된 한계.** 차례가 끝난 뒤 잡이 끝나도 닫힘 도구 결과가 도달할 때까지 패널은 잡이 열린 것으로 본다. 선언이 없는 매니페스트는 오늘과 동일하게 동작한다.

**남긴 것.** `◆ … → next: ooo <동작>` 줄을 읽어 다음 행동을 추천하는 `recommend` 규칙은 이번에 추가하지 않았다 — 잡 선언만으로 Ouroboros의 실용 요구를 충족할 수 있어서다.

### v4에 함께 처리한 보안 수정 (2026-09-20 검토 결과)

스타일 엔진 밖의 코드 변경이지만 같은 v4 범위에서 완료한 네 가지 보안 수정이다.

1. **macOS 업데이트 신뢰 규칙 — Windows와 동일하게 강화, 우회 없음.** `AppUpdate.swift`·`AppStore+AppUpdate.swift`·`AppUpdateSettingsView.swift`에 네 규칙을 적용했다. ① `MightyUpdatePublicKey`가 없는 빌드는 업데이트 확인 자체를 하지 않는다(자동 예약도, 수동 버튼도, 개발자 우회도 없음). ② 매니페스트 파싱 시 `sha256`과 `size` 두 필드가 없으면 다운로드 전에 거부된다. ③ `MightyUpdateManifestURL`이 바이너리에 새겨진 경우 그 주소만 사용하고 사용자 설정 주소 필드는 무시된다. ④ 설치 직전 스테이징된 패키지를 다시 해시하여 매니페스트 값과 다르면 현재 앱을 그대로 두고 중단된다. `docs/app-update.md`에서 `sha256`·`size`는 필수(`필수`)로, 키 없는 빌드는 업데이트 미지원으로 기술이 바뀌었다.

2. **릴레이 소켓별 송신 버퍼 상한.** `relay/src/config.ts`에 `maxSocketBufferedBytes`(기본값 4 MiB, `RELAY_MAX_SOCKET_BUFFERED_BYTES` 환경 변수로 재정의)를 추가했다. `relay/src/hub.ts`의 `forward()`가 대상 소켓의 `bufferedAmount`를 확인해 초과 시 해당 연결만 닫고(닫기 코드 4414 `socketBufferOverflow`), 호스트 제어 소켓과 다른 연결은 그대로 유지한다. 클라이언트→호스트, 호스트→클라이언트 양방향 모두 적용된다. `relay/src/protocol.ts`에 닫기 코드를 추가했고 `docs/relay.md`에 모든 제한과 코드 목록을 기재했다.

3. **페어링 키 재생성 시 기기 목록 전체 삭제.** `MobileDeviceRegistry.swift`에 `clearAll()` 메서드를 추가하고, `MobileRemoteService.swift`의 `regenerateKey()`가 키를 새로 쓰고 키 의존 클라이언트를 닫는 것에 더해 기기 레지스트리 전체를 원자적으로 비운다(`devices.json` 빈 배열로 대체). 재생성 후 설정 화면의 기기 목록은 비어 있다. `docs/mobile-remote.md`에 새 키를 생성하면 모든 기기가 다시 페어링해야 한다고 명시했다.

4. **CI 서명 작업 분리.** `.github/workflows/native-macos.yml`의 서명 스텝을 `sign-manifest`라는 별도 잡으로 옮겼다. `sign-manifest`는 `macos` 잡에 `needs`로 의존하고, `secrets.MIGHTY_UPDATE_SIGNING_KEY`를 참조하는 유일한 잡이다. `macos` 잡은 더 이상 그 시크릿을 참조하지 않는다.

### v4의 나머지 단계 (2026-09-22)

- **macOS CI.** 다섯 라운드(`ed33f18`·`0359c63`·`f9ec5b1`·`4b151ea`·`9ffeb42`)를 썼다. Swift 테스트 단계는 `ac6cbf8`에서 처음으로 초록이 됐고 빌드·스모크 실행 단계도 초록이다. `Check smoke result`만 빨갛다(`단일 실행·중지 버튼의 접근성 상태를 확인하지 못했습니다`). 상한을 지켜 더 고치지 않았고, 화면 확인 때 사용자가 정한다. 라운드 기록은 `docs/ci-macos-failure.md`.
- **다국어 토대.** `locales/ko.json`·`en.json`(272키), Swift·C#·TypeScript 로더, `scripts/check-locales.js`, 설정의 언어 선택(맥·Windows), 설정 화면·안내 패널 문구 이전(`39eadee`…`0f4222e`·`4f60a8a`). 남은 하드코딩 문구 3,239개는 `docs/i18n.md`에 세어 두었고 다음 묶음이다. 영어 초안의 검토 목록도 같은 문서에 있다.
- **화면 확인.** 이 단계들 뒤에 새 빌드를 설치하고 멈춘다. 확인에서 나온 문제는 아래 목록에 적고 태그 전에 고친다.
- 화면 확인에서 나온 문제: 없음. 2026-09-22 사용자가 새 빌드(안내 패널의 잡 인식, 언어 선택, 업데이트 신뢰 문구)를 확인하고 태그를 승인했다. macOS `Check smoke result`는 다섯 라운드 상한을 지켜 빨간 채로 태그했다(후보는 `docs/ci-macos-failure.md`).
- **플러그인 저장소.** `~/Work/mighty-styles`의 고정 사본은 아직 v1(`b767328`)을 가리킨다. v4 태그 뒤에 같은 태그를 로컬로 걸고 엔진 사본·코퍼스·체크섬·기준 상수를 v4 시점으로 올린다(별도 시드).

## 다음 고정(v5)에 묶을 것

검사가 저장소 전체를 보므로, 허용 목록 밖을 건드리는 작업이 main에 들어갈 때마다 새 고정이 필요하다. 예정된 것:

- 권한 모드별 기본 모델·등록 이름·그래프의 모델 표시(인터뷰 `interview_20260922_062850`, 시드 `seed_8f34bc3208d3`). v5의 첫 항목이며 태그 직후 실행한다.
- **리셋권(한도 리셋 자격) 표시**: `native/macos/Sources/MightyClaude/AppStore.swift`, `native/macos/Sources/MightyClaude/StatusBarUsage.swift`, `native/macos/Sources/MightyCore/AccountUsageService.swift`, `native/macos/Sources/MightyCore/AccountUsageSnapshot.swift`, `native/macos/Sources/MightyCore/AccountResetEntitlement.swift`, `native/macos/Tests/MightyCoreTests/AccountUsageTests.swift`, `native/macos/Sources/MightyCore/AccountResetSmoke.swift`, `native/macos/Sources/MightyClaude/AppStore+UsageResetSmoke.swift`, `native/windows/MightyClaude.Core/AccountUsage.cs`, `native/windows/MightyClaude.Core/AccountUsageReset.cs`, `native/windows/MightyClaude.Core/AccountUsageRuntime.cs`, `native/windows/MightyClaude.Core.Tests/AccountUsageVerification.cs`, `native/windows/MightyClaude.Core.Tests/Verification.cs`, `native/windows/MightyClaude.Core/AccountUsageResetSmoke.cs`, `native/windows/MightyClaude.WinUI/MainWindow.AccountUsage.cs`, `native/windows/MightyClaude.WinUI/MainWindow.Smoke.cs`, `locales/ko.json`, `locales/en.json`, `scripts/check-ci-steps.py`, `native/macos/Sources/MightyCore/Resources/Locales/ko.json`, `native/macos/Sources/MightyCore/Resources/Locales/en.json`, `mobile/src/locales/ko.json`, `mobile/src/locales/en.json`, `docs/i18n.md`, `docs/mobile-remote.md`, `docs/native-verification.md`, `docs/session-usage.md` — v5 두 번째 항목. `scripts/check-style-freeze.sh`가 이 경로들을 포함해 exit 1로 끝나는 것이 v5 태그 전 정상 상태이다("freeze check passes"는 완료 조건이 아님).
- **블록별 모델·토큰 표시**(시드 `seed_v5_07_block_model_usage`): `docs/i18n.md`, `docs/model-defaults.md`, `locales/en.json`, `locales/ko.json`, `mobile/src/locales/en.json`, `mobile/src/locales/ko.json`, `native/macos/Sources/MightyClaude/AgentTranscriptFormat.swift`, `native/macos/Sources/MightyClaude/AgentTranscriptView.swift`, `native/macos/Sources/MightyClaude/MightyGraphView.swift`, `native/macos/Sources/MightyCore/CLIStreamParser.swift`, `native/macos/Sources/MightyCore/ExecutionGraph.swift`, `native/macos/Sources/MightyCore/ExecutionGraphTracker.swift`, `native/macos/Sources/MightyCore/MightyGraph.swift`, `native/macos/Sources/MightyCore/ModelUsageFormat.swift`, `native/macos/Sources/MightyCore/Resources/Locales/en.json`, `native/macos/Sources/MightyCore/Resources/Locales/ko.json`, `native/macos/Tests/MightyCoreTests/ModelUsageTests.swift`, `native/macos/Sources/MightyCore/GraphChildBlocks.swift`, `native/macos/Sources/MightyClaude/SessionPaneView.swift` — v5 세 번째 항목. 리셋권 줄과 같이 이 경로들도 v5 태그 전까지 `scripts/check-style-freeze.sh`의 exit 1에 포함되는 것이 정상이다.
- 다국어 지원의 나머지: 토대(`locales/ko.json`·`en.json`, 세 클라이언트 로더, 설정·안내 패널)는 v4에 들어갔다. 남은 하드코딩 문구 3,239개와 영어 초안 검토는 `docs/i18n.md`에 있다. 엔진 파일 안의 문구를 옮길지는 그때 정한다. Windows `LocalizationVerification`에 en→ko 되돌림 검사가 없다(평가 후속).
- macOS CI `Check smoke result`의 수정(`docs/ci-macos-failure.md`의 후보 F)과 그 결과 기록.
- `◆ … → next: ooo <동작>` 줄을 읽는 `recommend` 규칙(항목 6의 나머지).
