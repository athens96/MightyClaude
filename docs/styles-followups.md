# 마이티 스타일 엔진 후속 작업 (규칙 어휘 확장)

docs/mighty-styles.md 10장의 사본이다. 고정된 어휘로 **지금도 표현되지 않는다고 이미 아는 것들**이며, 이번 범위 밖이고 확장은 `schema: 2`로 간다. 고정 태그 이후에 고칠 수 있는 유일한 문서다(§8.3).

고정 기준(현재, v2 — 2026-09-20): 고정 전 마지막 커밋 `19748917bc0e99ac6f45e6857d9c4a0cb08353e8`, 태그 `mighty-style-engine-v2`가 가리키는 커밋 `24a07e02559081d43fdb4337ebdc0162897aff44`(`styles/FREEZE`만 더한 커밋). v1 이후 스타일 엔진 파일(`native/macos/Sources/MightyCore/Styles/**`)·스키마·규칙 어휘는 바뀌지 않았다. v2를 건 이유는 검사가 저장소 전체를 보는데 main에 입력창의 한글 직접 조합(`docs/hangul-fallback-composer.md`)이 들어왔기 때문이다.

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

## 다음 고정(v3)에 한 번에 묶을 것

2026-09-19 결정: 화면 확인에서 나오는 문제와 아래 항목을 묶어 **한 번의** 새 고정으로 처리한다. 그 전에는 고정된 엔진·폰 코드를 건드리지 않는다. 2026-09-20: 한글 자모 분리 수정을 먼저 main에 넣기로 하면서 `mighty-style-engine-v2`는 그 변경만 담아 따로 걸었다. 아래 묶음은 그대로 남아 다음 고정(`mighty-style-engine-v3`)으로 간다.

- 항목 28의 폰 결함: `mobile/src/lib/styles.ts`의 `parseAction`이 `glyph`를 제어 문자 제거가 아니라 "이모지 한 글자인가"로 판정하도록 고친다(맥의 `StyleText.isEmojiGlyph`와 같은 기준).
- 정식 평가(2026-09-19, 9/9 승인)가 지적한 테스트 빈틈: Ouroboros의 단계 6개를 한 줄로 단언하는 검사, 101번째 행동이 `E_LIMIT`으로 거부되는 픽스처.
- 앱 모듈에 테스트 타깃이 없어 `AppStore+Styles`의 연결(승인 뒤 실행 창 결속, 저장소 매니페스트 미복사, 일반 실행 창의 제목 앞머리 차단)은 읽어서만 검증됐다. 타깃을 더하거나 그 규칙을 MightyCore로 더 옮긴다.
- 화면 확인에서 나온 문제: (확인 뒤 여기에 적는다)
