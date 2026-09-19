# 마이티 모드 스타일: Paperthin

상태: **v1 구현됨**. 마이티 모드의 두 번째 안내형 스타일이다. [Paperthin](https://github.com/LilMGenius/paperthin)(MIT)을 UI만으로 쓸 수 있게 한다. 스타일 구조와 질문 패널은 [ouroboros-mode.md](ouroboros-mode.md)와 공유한다. Paperthin도 이제 하드코딩된 Swift 분기(`PaperthinCatalog`·`PaperthinPanel`)가 아니라 **번들 매니페스트** 하나(`native/macos/Sources/MightyCore/Resources/Styles/paperthin.json`)로 구동되는, Ouroboros와 같은 범용 마이티 스타일 엔진의 한 스타일이다. 매니페스트 스키마·검증·신뢰 모델의 전체 계약은 [mighty-styles.md](mighty-styles.md)에 있으며, 이 문서는 그 스키마를 되풀이하지 않고 Paperthin이 그 스키마로 무엇을 선언했는지만 적는다.

## Paperthin이 실제로 동작하는 방식 (조사 결과)

- 서버도 MCP 도구도 없는 **작은 스킬 28개의 카탈로그**다. 각 스킬은 이름으로 부른다(`/re0`, `/nba`). 원칙은 "더하지 말고 덜어내기".
- 스킬은 **지도** 위에 놓인다. 아티팩트가 몇 개인가(하나·여럿), 시간이 어느 정도에 걸치는가(지금·반복)의 2×2다.

  | 영역 | 축 | 질문 |
  |---|---|---|
  | depth | 하나 · 지금 | 이 하나가 깨끗하고 참인가? |
  | breadth | 여럿 · 지금 | 하나의 진실이 모든 곳에서 일관적인가? |
  | coil | 하나 · 반복 | 각 패스가 다음 패스를 가르쳤는가? |
  | mesh | 여러 시선 | 집단이 진실로 수렴하는가? |

- 호출자가 둘로 나뉜다. **모델 호출** 스킬은 모델이 필요할 때 스스로 꺼내 쓰고, **사용자 호출** 스킬 12개(`hate`, `macrothink`, `feynman`, `reorder`, `dedash`, `debloat`, `re0-git`, `re0-release`, `re0-merge`, `re0-upgrade`, `re0-plan`, `prism`)는 사람만 부를 수 있다. 그래서 UI의 버튼이 가장 쓸모 있는 곳이 이 12개다.
- 반복 루프는 coil에 있다. `re0-plan`이 케이스북(`.re0/iteration/<버전>-<작업명>/`)을 열어 `DESIGN`·`WORKFLOW`·`EVIDENCE`(가벼운 사이클은 `RETRO`만) `.local.md`를 쓰고, `re0-loop`가 FRAME → BUILD → DRIVE → RE0-MEMO → HATE → RE0-WORK → BUILD AGAIN을 돈다. `nba`는 지금 상태에서 단 하나의 다음 행동을, `catchup`은 잃어버린 맥락을 돌려준다.
- 프로젝트가 안내하는 설치는 `npx skills@latest add LilMGenius/paperthin --global --agent '*'`(모든 에이전트)다. 앱은 이 스타일이 구동하는 Claude Code에만 연결하도록 `--agent claude-code`로 좁혀 실행한다. 스킬이 `~/.claude/skills/`에 심볼릭 링크로 연결된다.

아우로보로스와 마찬가지로 **스킬 프롬프트를 실행 창의 다음 요청으로 보내는 것**만으로 구동한다.

## 화면

마이티 모드 입력창 위의 선택기는 메뉴다(`CLI` + 이 실행 창에서 쓸 수 있는 스타일 목록, 이 Mac의 Claude 실행 창). Paperthin은 Ouroboros와 함께 **번들** 스타일이라 사전 승인되어 있어 승인 시트 없이 바로 고를 수 있다.

아래 내용은 전부 매니페스트 `paperthin.json`이 선언한 것이고(그룹·행동·규칙·준비물·설치·표현), 화면은 범용 엔진(`GuidedPanel.swift` + `StyleEvaluator`)이 그 매니페스트를 읽어 그린다. **`groups`가 4개이고 그중 전부 `axis`를 가지므로** 엔진은 그룹 지도를 그리고, 그 지도가 있는 스타일의 행동 줄은 언제나 격자로 그린다(오늘과 같은 모양). 케이스북만은 매니페스트가 표현할 수 없는 부분이라 앱 내장 기능(`capabilities: ["paperthin.casebook"]`)으로 남아 있다 — Paperthin 매니페스트가 이름으로 참조하고 구현은 `StyleCapabilities.swift`에 있다.

| 부분 | 내용 |
|---|---|
| 지도 | depth · breadth · coil · mesh 네 버튼(매니페스트의 `groups`). 축(하나·지금 등)을 함께 보여주고, 고른 영역의 질문이 아래에 나온다. 케이스북을 처음 읽었을 때 한 번 정한다: 열린 사이클이 있으면 coil, 없으면 depth(`rules.initialGroup`). 이후에 케이스북이 생겨도 보고 있던 영역은 바뀌지 않는다 |
| 스킬 | 고른 영역의 스킬 버튼(매니페스트의 `actions`, `NextRule.byGroup`이 고른 그룹의 목록을 그 순서대로 낸다). 이모지(`glyph`)와 이름, 사람 아이콘은 사용자 호출(`userInvoked`) 전용, 눈 아이콘은 읽기 전용(`readOnly`). 마우스를 올리면 하는 일·범위·호출자가 나온다. 누르면 `/스킬 <입력창에 적은 대상>`이 다음 요청으로 나간다(비어 있으면 스킬만, 여러 줄은 한 줄로 합친다 — `foldText: oneLine`). 실행 중이면 대기열로 들어간다. 전송이 거절되면(대기열 가득 참 등) 적어 둔 글은 그대로 남는다 |
| 케이스북 (coil) | 워크스페이스의 최신 `.re0/iteration/` 폴더 이름, 무게(full·lightweight), 파일 버튼(DESIGN·WORKFLOW·EVIDENCE·RETRO·REF-…). 파일을 누르면 기본 앱으로 연다. 실행이 끝날 때마다 다시 읽는다. 최근에 수정된 폴더 24개만 살피고, 심볼릭 링크 폴더(와 항목)는 따라가지 않는다. 내장 기능 `paperthin.casebook`이 만든다 |
| 추천 | coil에서는 케이스북 상태로 다음 스킬을 강조한다: 사이클 없음 → `re0-plan`, 진행 중 → `re0-loop`, DESIGN과 RETRO가 모두 있으면 → `re0-work`(`rules.recommend`). 추천은 지금 보고 있는 그룹과 무관하게 케이스북 상태만 본다 — 다른 그룹을 보고 있어도 값은 살아 있고, 그 값이 지금 그려지는 줄에 없으면 강조될 칩이 없을 뿐이다 |
| 질문 | 에이전트가 `AskUserQuestion`으로 물으면 아우로보로스와 같은 질문 패널이 입력창 자리에 나온다 |
| 준비물 | 스킬이 없으면 설치 명령을 그대로 보여준다. **[설치]를 누르면 명령이 새 터미널 실행 창에 채워지기만 하고, 실행하려면 사용자가 직접 Enter를 눌러야 한다** — 이전에는 앱이 붙여넣은 뒤 자동으로 Enter까지 눌렀다([mighty-styles.md](mighty-styles.md) §1.5, 매니페스트로 설치 명령을 선언하는 모든 스타일에 공통인 의도된 동작 변경). 설치 여부는 `~/.claude/skills`, 워크스페이스의 `.claude/skills`, 플러그인 등록부(`paperthin@…`)에서 확인한다 |

그래프에서는 Paperthin 프롬프트로 시작한 요청 블록 제목에 `♻️ re0`처럼 스킬이 붙는다. 안내형 스타일을 쓰는 실행 창에서만 붙고, CLI 스타일 실행 창에 직접 `/re0`를 입력한 요청은 평소 제목 그대로다. 아이콘·색도 이제 매니페스트에서 온다: Paperthin의 행동은 개별 아이콘을 선언하지 않으므로(이모지 `glyph`만 쓴다), 요청 블록은 스타일 전체 값(`square.grid.2x2`, `accent`)을 쓴다 — 이전에는 앱 기본 아이콘(`arrow.up.message`)이었다. 그래프 머리말도 `마이티 · Paperthin`이 된다(Paperthin은 단계가 없어 `· <단계>`는 붙지 않는다) — 이전에는 `마이티` 하나였다. Enter는 스타일과 상관없이 입력한 글을 그대로 요청한다(아우로보로스의 목표 → 인터뷰 변환은 없다 — 매니페스트의 `rules.enter: verbatim`).

## 범위와 한계

- 자동 허용하는 도구는 없다(매니페스트의 `autoAllow: []`). Paperthin은 도구 서버가 없고, 파일 수정·명령 실행은 실행 창의 권한 모드를 따른다.
- 케이스북은 읽기만 한다. 단계(FRAME·BUILD…) 표시는 하지 않는다. 그 판단은 `nba`의 일이라 버튼으로 둔다.
- 설치 명령은 Claude Code의 전역 스킬 폴더에만 연결한다(`--agent claude-code`). 다른 에이전트에도 쓰려면 프로젝트가 안내하는 `--agent '*'`를 터미널에서 직접 실행한다. 패널은 명령 전문을 보여주고 터미널에 채워 넣을 뿐, 실행은 사용자가 Enter로 한다.
- 카탈로그(이름·요약·호출자·읽기 전용)는 앱에 매니페스트로 내장돼 있다. Paperthin이 스킬을 추가·개명하면 `paperthin.json`을 갱신해야 한다(요약의 출처: Paperthin 한국어 README, MIT). 매니페스트는 [mighty-styles.md](mighty-styles.md) §8이 정하는 고정(freeze) 규칙 아래에 있으므로, 태그 이후에는 새 스타일(`styles/*.json`)만 추가할 수 있고 이 두 번들 매니페스트 자체는 얼어붙는다.
- Claude 실행 창 전용이다.

## 구현 위치

| 부분 | 파일 |
|---|---|
| 매니페스트(그룹·행동·규칙·준비물·자동 허용·표현) | `native/macos/Sources/MightyCore/Resources/Styles/paperthin.json` — 스키마는 [mighty-styles.md](mighty-styles.md) |
| 범용 엔진(디코딩·검증·평가·투영·레지스트리·신뢰 저장소) | `native/macos/Sources/MightyCore/Styles/*.swift` |
| 케이스북 내장 기능(`paperthin.casebook`, 옛 `PaperthinCasebook`을 `StyleCasebook`으로 이름만 바꿔 유지) | `native/macos/Sources/MightyCore/Styles/StyleCapabilities.swift` |
| 스타일 전환, 승인, 전송, 케이스북 갱신, 설치 터미널 | `native/macos/Sources/MightyClaude/AppStore+Styles.swift` |
| 범용 패널, 행동 칩, 선택기(메뉴), 공유 질문 패널 | `native/macos/Sources/MightyClaude/GuidedPanel.swift`, `GuidedActionChip.swift`, `AgentQuestionPanel.swift` |
| 승인 시트, 스타일 설정 화면 | `native/macos/Sources/MightyClaude/StyleApprovalSheet.swift`, `StyleSettingsSection.swift` |
| 테스트(값 보존 확인) | `native/macos/Tests/MightyCoreTests/StylesPaperthinTests.swift`, `StylesBundledTests.swift`, `StyleCapabilityTests.swift`, `StyleGoldenContractTests.swift` |
