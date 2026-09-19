# 마이티 모드 스타일: 아우로보로스

상태: **v1 구현됨**. 마이티 모드 안에서 요청 방식을 고르는 "스타일"을 두고, 첫 추가 스타일로 [Ouroboros](https://github.com/Q00/ouroboros)를 UI만으로 쓸 수 있게 했다. Ouroboros는 이제 하드코딩된 Swift 분기(`OuroborosFlow`·`OuroborosPanel`)가 아니라 **번들 매니페스트** 하나(`native/macos/Sources/MightyCore/Resources/Styles/ouroboros.json`)로 구동되는, 범용 마이티 스타일 엔진의 한 스타일이다. 매니페스트 스키마·검증·신뢰 모델의 전체 계약은 [mighty-styles.md](mighty-styles.md)에 있으며, 이 문서는 그 스키마를 되풀이하지 않고 Ouroboros가 그 스키마로 무엇을 선언했는지만 적는다. 스타일은 실행 창별로 저장된다(`RunSession.mightyStyle` + `mightyStyleHash`, 후자는 승인 시점의 매니페스트 해시에 묶는다).

## 아우로보로스가 실제로 동작하는 방식 (조사 결과)

- 에이전트 세션 **안에서** 도는 스킬 + MCP 도구 묶음이다. `/ouroboros:interview <목표>` 같은 스킬 프롬프트가 `ouroboros_*` MCP 도구(질문 생성·상태 저장·모호도 채점)를 부르고, 사람에게 물을 것은 런타임의 질문 기능(Claude의 `AskUserQuestion`)으로 묻는다.
- 흐름: **인터뷰**(모호도 ≤ 0.2까지 소크라테스식 질문) → **시드**(불변 명세 YAML) → **실행**(Double Diamond) → **평가**(기계·의미·합의 3단계) → **진화/랄프**(온톨로지 유사도 ≥ 0.95로 3세대 수렴, 최대 30세대). `auto`는 목표에서 실행까지 한 번에 간다.
- MCP 응답은 평문이다(`Interview started. Session ID: interview_…` + 질문). 구조화된 값은 도구 호출의 입력(`session_id`, `answer`, `seed_path`)과 `AskUserQuestion`의 질문·선택지에서 얻을 수 있다.
- 이 앱의 Claude 실행 창은 이미 `claude -p`에서 플러그인 MCP 서버와 `AskUserQuestion`(질문 카드)을 지원한다. 즉 **새 엔진 없이 스킬 프롬프트를 보내고 스트림을 관찰하는 것만으로** 전체 흐름을 구동할 수 있다.

## 스타일 선택

마이티 모드 입력창 위의 선택기는 메뉴다(`CLI` + 이 실행 창에서 쓸 수 있는 스타일 목록, Claude 실행 창만). 선택은 실행 창별로 저장한다. CLI는 지금과 같다. Ouroboros는 **번들** 스타일이라 사전 승인되어 있어 승인 시트 없이 바로 고를 수 있다(사용자가 등록하는 스타일과 저장소에서 발견되는 스타일은 한 번의 승인 화면을 거친다 — [mighty-styles.md](mighty-styles.md) §4.4).

## 아우로보로스 스타일의 화면

아래 표의 값은 전부 번들 매니페스트 `ouroboros.json`이 선언한 것이고, 화면과 규칙은 범용 엔진(`GuidedPanel.swift` + `StyleEvaluator`)이 그 매니페스트를 읽어 그린다. Ouroboros는 내장 기능(`capabilities`)을 쓰지 않는다(매니페스트의 `capabilities: []`) — 준비물 확인, 단계 계산, 다음 행동, Enter 규칙까지 전부 범용 규칙만으로 표현된다. `AskUserQuestion` 질문 패널은 매니페스트가 손댈 수 없는 공유 컴포넌트로 그대로 남는다.

### 입력창 = 단계별 전용 패널

| 상태 | 입력창 |
|---|---|
| 시작 전 | "무엇을 만들까요?" + **인터뷰 시작**(→ `/ouroboros:interview <목표>`), 보조 **자동 진행**(→ `/ouroboros:auto`) |
| 질문 대기 | 질문 문장과 선택지 칩을 입력창이 직접 보여준다. 칩을 누르거나 글로 답하고 Enter. 답은 기존 질문 응답 채널로 간다 |
| 인터뷰 턴 종료 | 다음 단계 버튼: **시드 생성** · 상태 · 막힘 풀기(그 뒤 단계는 실행 · 평가 · 진화 · 랄프 루프 · 상태 · 막힘 풀기 순으로 이어진다). 자유 입력은 같은 대화에 이어서 보낸다(요구 보완) |
| 실행·평가·진화 중 | 진행 표시 + 중지. 질문이 오면 다시 "질문 대기" |

시작 전 버튼과 "새 목표" 칩은 매니페스트의 `rules.start`(단계 `goal`에서 `interview`·`auto`를 이 순서로, `resetTitle: "새 목표"`)가 정한다. 이후 단계의 다음 단계 버튼은 `rules.next`(단계별 목록, `NextRule.byPhase`)가 정한다.

상단에 단계 표시줄(목표 → 인터뷰 → 시드 → 실행 → 평가 → 진화, 매니페스트의 `phases`)을 둔다. 단계는 실행 창의 요청 기록(그래프의 요청 블록 입력, 최근 128개) 중 마지막으로 **인식된** `/ouroboros:<행동>` 요청으로 정한다(`rules.phase: lastRecognisedAction`, 없으면 `goal`) — `status`·`unstuck`은 매니페스트에 `phase`가 없어 단계를 바꾸지 않고, 자유 입력도 마찬가지다. 도구 활동으로 금방 잘리는 실행 로그는 쓰지 않는다. 모호도 점수는 에이전트가 본문에 적는 값이라 v1에서는 따로 뽑아내지 않는다.

질문이 여러 개인 요청은 한 번에 하나씩 보여준다(`1/3`). 하나 선택 질문은 칩을 누르면 바로 다음으로 넘어가고, 복수 선택 질문은 칩을 켜고 끈 뒤 Enter 또는 "선택 완료"로 확정한다. 직접 적은 글은 선택지 대신(복수 선택이면 함께) 답이 된다. "이전"으로 앞 질문을 고칠 수 있고(복수 선택이면 앞서 고른 항목이 다시 표시된다) "답하지 않기"는 질문 요청을 거부한다. 답 전달이 실패하면 적은 답을 유지한 채 다시 시도할 수 있다. 이 스타일에서는 입력창이 답하고 있는 질문의 카드만 숨기고, 입력창이 처리할 수 없는 질문 요청은 기존 카드로 보인다. 대기 중인 질문은 준비물 안내보다 항상 먼저 표시된다.

다음 단계 버튼은 입력창의 글을 건드리지 않는다. 글을 함께 보내는 것은 `interview`·`auto`·`unstuck`뿐이다(매니페스트에서 `takesText: true`인 행동).

시작 전(목표 단계)에 Enter를 누르면 적은 목표가 `/ouroboros:interview <목표>`로 바뀌어 나간다(`/`로 시작하는 명령이나 첨부가 있으면, 또는 이 실행 창에 이미 요청이 있으면 그대로 보낸다) — 매니페스트의 `rules.enter: rewriteBareDraftTo(action: interview, phase: goal)`다. **"새 목표"**(매니페스트의 `rules.start.resetTitle`) 칩을 누르면 화면이 목표 단계로 돌아가고, 그 상태에서 다음으로 치는 글의 Enter만 다시 `/ouroboros:interview`로 바뀐다 — 이 되돌리기 칩을 누르지 않고서는, 요청이 이미 있는 실행 창에서 목표 단계로 보이는 것만으로 이 변환이 다시 걸리지 않는다.

### 그래프 = 질문이 블록으로 쌓인다

- `AskUserQuestion` 호출마다 **질문 블록**(새 노드 종류 `question`)이 요청 블록 아래에 생긴다. 질문·선택지가 입력, 고른 답이 출력이다. 답하기 전에는 "대기" 상태로 강조된다.
- 각 단계(인터뷰·시드·실행·평가·진화)는 지금처럼 요청 블록 하나다. 제목·아이콘·색은 이제 매니페스트에서 온다: 접두사는 그 행동의 `requestTitle`이 없으면 행동이 속한 단계의 제목(`인터뷰`·`평가`·`막힘 풀기`처럼 `phase`가 없는 행동은 자기 `title`)이고, 아이콘은 행동마다 선언된 값(`questionmark.bubble`·`wand.and.stars`·`leaf`·`play.fill`·`checkmark.seal`·`arrow.triangle.2.circlepath`·`infinity`·`gauge.with.dots.needle.33percent`·`lightbulb`)이며, 색은 스타일 전체 값(`accent`)이다. 꼬리 `요청 N · <provider>`는 예전처럼 앱이 붙인다. 그래프 머리말도 `마이티 · Ouroboros`가 되고, 현재 단계가 있으면 `마이티 · Ouroboros · <단계 제목>`까지 붙는다 — 이전에는 `마이티` 하나였다.
- 하위 에이전트·백그라운드 작업·컨텍스트 정리 블록은 그대로 나온다.

### 권한

이 스타일에서는 Ouroboros 플러그인 서버의 **상태 도구 16개**(인터뷰 질문·답 기록, 시드 생성, 상태·이벤트·계보 조회 등, 매니페스트의 `autoAllow`에 정확한 이름으로 적힌 목록)와 도구 검색(`ToolSearch`)까지 합쳐 **17개**만 앱이 자동 허용한다(매 질문마다 권한 창이 뜨지 않게). 실행을 시작하는 도구(`execute_seed`, `start_*`, `ralph`, `evolve_step`, `evaluate`, 취소·되감기)와 파일 수정·명령 실행은 선택한 권한 모드 그대로 승인을 묻는다. 자동 허용은 접두사가 아니라 정확한 도구 이름으로 판단하고, 이 스타일이 요구하는 플러그인(`ouroboros@ouroboros`)의 도구만 자동 허용할 수 있게 매니페스트 스키마가 강제하므로 다른 서버가 이름을 흉내 낼 수 없다([mighty-styles.md](mighty-styles.md) §1.9). 자동 허용이 실제로 전달되는 동안만 권한 카드를 숨기고, 전달에 실패하면 일반 권한 카드로 나타난다. 승인 카드(비번들 스타일의 경우)는 이 목록을 접지 않고 맨 위에 보여준다.

### 준비물

Ouroboros 플러그인(`ouroboros@ouroboros`)과 `uvx`가 필요하다. 없으면 입력창이 설치 안내와 **[설치]** 버튼을 보여준다(플러그인 probe만 `install: true`이고 `uvx` probe는 `install: false`라 두 번째 준비물이 미충족이어도 설치 버튼은 뜨지 않는다 — 오늘과 같은 값이다). **[설치]를 누르면 명령이 새 터미널 실행 창에 채워지기만 하고, 실행하려면 사용자가 직접 Enter를 눌러야 한다** — 이전에는 앱이 붙여넣은 뒤 자동으로 Enter까지 눌렀다. 이 스타일뿐 아니라 매니페스트로 설치 명령을 선언하는 모든 스타일(번들 포함)에 공통으로 적용되는 의도된 동작 변경이다([mighty-styles.md](mighty-styles.md) §1.5).

## 범위

- **v1 (구현)**: 스타일 선택, 전용 입력창(시작·질문 응답·다음 단계 버튼), 질문 블록, 자동 허용, 준비물 확인.
- **이후**: 시드 YAML 미리보기 블록, 세대(lineage) 타임라인, AC 트리 HUD, `unstuck` 페르소나, PM 인터뷰.
- Claude 전용. Codex·Gemini는 질문 채널이 없어 제외한다.
- 질문 블록도 실행당 128개 노드 한도를 함께 쓴다. 질문이 아주 많은 실행에서는 뒤쪽 하위 에이전트 블록이 생략될 수 있다.

## 구현 위치

| 부분 | 파일 |
|---|---|
| 매니페스트(행동·단계·규칙·준비물·자동 허용·표현) | `native/macos/Sources/MightyCore/Resources/Styles/ouroboros.json` — 스키마는 [mighty-styles.md](mighty-styles.md) |
| 범용 엔진(디코딩·검증·평가·투영·레지스트리·신뢰 저장소) | `native/macos/Sources/MightyCore/Styles/*.swift` |
| 질문 진행 상태(`QuestionnaireProgress`, 매니페스트와 무관한 공유 컴포넌트) | `native/macos/Sources/MightyCore/QuestionnaireProgress.swift` |
| 질문 블록(`question` 노드) | `ExecutionGraphTracker.swift`, `ExecutionGraph.swift`, `MightyGraph.swift`, `MightyGraphView.swift` |
| 스타일 전환, 승인, 프롬프트 전송, 질문 응답, 자동 허용, 설치 터미널 | `native/macos/Sources/MightyClaude/AppStore+Styles.swift` |
| 범용 패널, 행동 칩, 선택기(메뉴) | `native/macos/Sources/MightyClaude/GuidedPanel.swift`, `GuidedActionChip.swift`, `SessionPaneView.swift` |
| 승인 시트, 스타일 설정 화면 | `native/macos/Sources/MightyClaude/StyleApprovalSheet.swift`, `StyleSettingsSection.swift` |
| 테스트(값 보존 확인) | `native/macos/Tests/MightyCoreTests/StylesOuroborosTests.swift`, `StylesBundledTests.swift`, `StyleGoldenContractTests.swift` |
