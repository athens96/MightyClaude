# 마이티 모드 스타일: Superpowers

상태: **v5 구현됨**. [Superpowers](https://github.com/obra/superpowers) 스킬 플러그인을 마이티 모드에서 UI로 구동하는 번들 스타일이다. 브레인스토밍 → 계획 작성 → 계획 실행 → 완료 루프를 패널 버튼과 단계 표시줄로 안내한다. Superpowers는 **번들 스타일**이 아니라 **워크스페이스·사용자 등록 스타일**로도 쓰이는 패턴과 달리, 앱에 **번들**로 내장(`native/macos/Sources/MightyCore/Resources/Styles/superpowers.json`)되며 사전 승인된다.

이 스타일은 범용 마이티 스타일 엔진(**v5**)의 **상태 소스(stateSources)** 기능을 처음 쓰는 스타일이다. 계획 파일의 존재 여부와 체크리스트 완료 여부가 단계 표시줄에 반영된다. 매니페스트 스키마·검증·신뢰 모델·상태 소스 스키마의 전체 계약은 [mighty-styles.md](mighty-styles.md)에 있다(상태 소스는 §1.16). 이 문서는 그 스키마가 Superpowers에 어떻게 적용됐는지만 적는다.

## Superpowers가 실제로 동작하는 방식

- Claude Code 플러그인 마켓플레이스에서 설치하는 **스킬 묶음**이다(`claude plugin enable superpowers@claude-community`).
- 각 스킬은 슬래시 명령(`/superpowers:brainstorming`, `/superpowers:writing-plans`, `/superpowers:executing-plans` 등)으로 부른다.
- MCP 서버는 없다 — 모든 도구 호출이 실행 창의 권한 모드를 따른다. `autoAllow`는 빈 배열이다.
- 계획 파일은 워크스페이스 안의 `docs/superpowers/plans/*.md` 글롭에 일치하는 마크다운 파일이다. 계획 작성 스킬이 이 경로에 파일을 만들고, 실행 스킬이 그 체크리스트를 채워 나간다.

## 스타일 선택

마이티 모드 입력창 위의 선택기 메뉴에서 `Superpowers`를 고른다. Superpowers는 번들 스타일이라 사전 승인되어 있어 승인 시트 없이 바로 고를 수 있다. 선택은 실행 창별로 저장한다.

## 단계 표시줄

단계는 네 가지다: **브레인스토밍 → 계획 → 실행 → 완료**. 단계 결정 우선순위는 다음과 같다.

1. **`stateOverrides`(파일 소스 상태)는 앞으로만 옮긴다** — 현재 계획(`docs/superpowers/plans/*.md` 중 아래 mtime 조건을 넘는 가장 최근 파일)의 체크리스트 항목이 하나 이상이고 모두 `[x]`이면 → **완료** 단계, 현재 계획이 있으면 → **실행** 단계. 요청 기록이 이미 더 뒤 단계를 가리키면 그 단계를 유지한다.
2. **과거 요청 인식** — 위 조건이 모두 거짓이면, 요청 기록에서 마지막으로 인식된 `/superpowers:<행동>`의 `phase`로 결정한다.
3. **기본** — 인식된 것도 없으면 **브레인스토밍** 단계.

이 규칙 덕분에 **새 세션은 항상 브레인스토밍에서 열리고**, 계획 파일이 생기면 실행으로 건너뛰며, 모든 항목을 체크하면 완료로 이동한다 — 사용자가 단계 버튼을 직접 누르지 않아도 된다.

> 계획 파일의 mtime 조건: 수정 시각이 **이 실행 창이 Superpowers 스타일로 보낸 첫 요청** 이후인 계획 파일만 현재 계획이다([mighty-styles.md](mighty-styles.md) §1.16.1). 이전 작업에서 남은 계획 파일은 단계를 움직이지 않으므로, 새 세션은 계획 파일이 남아 있어도 브레인스토밍에서 열린다. 같은 창에서 계획을 끝낸 뒤 새 아이디어를 시작하면 끝난 계획이 여전히 현재 계획이라 "완료"로 보인다 — 이때는 "새 아이디어" 칩으로 돌아간다.

## 화면

### 입력창 = 단계별 전용 패널

| 상태 | 입력창 |
|---|---|
| 시작 전 (브레인스토밍 단계) | **브레인스토밍** 버튼 하나 (→ `/superpowers:brainstorming <목표>`). 자유 텍스트와 함께 보낸다 |
| 계획 단계 | **계획 작성** · **서브에이전트 개발** 버튼 |
| 실행 단계 | **계획 실행** · **서브에이전트 개발** 버튼 + 보조 **디버깅** |
| 완료 단계 | **완료 전 검증** · **브랜치 완료** · **코드 리뷰 요청** 버튼 |

"새 아이디어" 칩(`resetTitle`)이 모든 단계에 있다 — 누르면 브레인스토밍 단계로 돌아간다.

Enter 동작: **`verbatim`** — Superpowers 스타일은 Enter를 절대 가로채지 않는다. 사용자가 친 글은 그대로 요청으로 나간다. 패널은 다음 버튼을 **권고(advisory)**할 뿐이다.

### 상태 위젯

폰 패널 아래쪽에 두 위젯이 표시된다.

| 소스 | 위젯 종류 | 표시 내용 |
|---|---|---|
| `docs/superpowers/plans/*.md` (markdownChecklist) | **진행 막대** (`progressBar`) | 현재 계획의 완료 항목 수 / 전체 항목 수 (`N/M`) |
| 서브에이전트 시작 이벤트 (count) | **라벨** (`label`) | 현재 세션에서 서브에이전트가 시작된 횟수 |

지금은 **폰 화면만** 위젯을 그린다. Mac은 같은 상태로 단계 표시줄을 정하지만(폰과 같은 단계가 나온다), Mac 패널에 위젯을 그리는 일은 다음 단계에서 한다. 현재 계획 파일이 없으면 진행 막대는 비어 있는 상태(`0/0`)로 그린다(숨기지 않는다).

### 그래프

각 스킬 호출 결과는 요청 블록 하나다. 제목 접두사·아이콘·색은 매니페스트에서 온다.

| 행동 | 아이콘 | 색 |
|---|---|---|
| brainstorming | `questionmark.bubble` | teal |
| writing-plans | `doc.text` | teal |
| executing-plans | `play.fill` | teal |
| subagent-driven-development | `sparkles` | teal |
| verification-before-completion | `checkmark.seal` | teal |
| finishing-a-development-branch | `arrow.triangle.2.circlepath` | teal |
| systematic-debugging | `hammer` | teal |
| requesting-code-review | `magnifyingglass` | teal |

그래프 머리말은 `마이티 · Superpowers`가 되고, 현재 단계가 있으면 `마이티 · Superpowers · <단계 제목>`까지 붙는다.

### 권한

`autoAllow`가 빈 배열이다 — Superpowers 플러그인은 MCP 서버를 제공하지 않는다. 모든 도구 허용 결정은 실행 창의 권한 모드를 따른다. `ToolSearch`도 없다(번들 스타일이지만 Superpowers 플러그인 probe의 `prefix`가 `superpowers@`이라 소속 규칙상 빈 배열이 유일하게 올바른 값이다).

### 준비물

Superpowers 플러그인(`superpowers@claude-community`)이 필요하다. 없으면 입력창이 설치 안내와 **[설치]** 버튼을 보여준다. **[설치]를 누르면 명령이 새 터미널 실행 창에 채워지기만 하고, 사용자가 직접 Enter를 눌러야 실행된다** — 모든 번들 스타일에 공통인 동작이다([mighty-styles.md](mighty-styles.md) §1.5).

설치 명령: `claude plugin enable superpowers@claude-community`

## 범위

- **v5 (구현)**: 스타일 선택, 4단계 표시줄, 전용 패널 버튼, 상태 소스(계획 파일 진행 막대 + 서브에이전트 카운트 라벨), Enter는 그대로(verbatim), 준비물 확인, Mac + 폰 양면(상태 위젯 그리기는 폰만; Mac 위젯은 다음 단계).
- **Enter 가로채기 없음**: 이 스타일은 어떤 상황에서도 사용자가 친 평문을 바꾸지 않는다. 버튼과 단계 표시줄이 안내하고, 사용자가 직접 고른다.
- Claude 전용. Codex·Gemini는 Superpowers 스킬 구동 방식이 다르므로 이 스타일의 적격성 밖이다.

## 명령 인벤토리 (Command Inventory)

`superpowers.json`의 모든 `actions[].prompt`는 `/superpowers:<스킬-이름>` 형식이다. 이 형식은 설치된 플러그인 파일에서 직접 도출됐다.

### 호출 형식 근거

플러그인 `.claude-plugin/plugin.json`은 이름·버전·저자만 담은 메타데이터이고, `commands/` 디렉터리는 없다. 스킬은 `package.json`의 `pi.skills: ["./skills"]` 선언으로 등록된다. Claude Code는 `<플러그인-이름>:<스킬-폴더-이름>` 형식의 슬래시 명령(`/superpowers:<스킬>`)으로 스킬을 부른다.

- **근거 파일**: `.claude-plugin/plugin.json` (`name: "superpowers"`), `package.json` (`pi.skills: ["./skills"]`)
- **플러그인 캐시 경로**: `~/.claude/plugins/cache/claude-community/superpowers/6.4.2/`
- **설치 버전**: 6.4.2 (obra/superpowers, MIT)

### 전체 스킬 인벤토리 (플러그인 버전 6.4.2)

아래 목록은 `skills/` 디렉터리의 모든 폴더를 열거한 것이다. `superpowers.json`의 행동은 반드시 이 목록 안에 있어야 하며, `SuperpowersStyleTests.swift::buttonsSendInstalledPluginSkillCommands()`가 이를 검증한다.

| 스킬 이름 | 소스 경로 | superpowers.json 사용 여부 |
|---|---|---|
| `brainstorming` | `skills/brainstorming/SKILL.md` | ✓ (브레인스토밍) |
| `diagnosing-superpowers` | `skills/diagnosing-superpowers/SKILL.md` | — |
| `dispatching-parallel-agents` | `skills/dispatching-parallel-agents/SKILL.md` | — |
| `executing-plans` | `skills/executing-plans/SKILL.md` | ✓ (계획 실행) |
| `finishing-a-development-branch` | `skills/finishing-a-development-branch/SKILL.md` | ✓ (브랜치 완료) |
| `receiving-code-review` | `skills/receiving-code-review/SKILL.md` | — |
| `requesting-code-review` | `skills/requesting-code-review/SKILL.md` | ✓ (코드 리뷰 요청) |
| `subagent-driven-development` | `skills/subagent-driven-development/SKILL.md` | ✓ (서브에이전트 개발) |
| `systematic-debugging` | `skills/systematic-debugging/SKILL.md` | ✓ (디버깅) |
| `test-driven-development` | `skills/test-driven-development/SKILL.md` | — |
| `using-git-worktrees` | `skills/using-git-worktrees/SKILL.md` | — |
| `using-superpowers` | `skills/using-superpowers/SKILL.md` | — |
| `verification-before-completion` | `skills/verification-before-completion/SKILL.md` | ✓ (완료 전 검증) |
| `writing-plans` | `skills/writing-plans/SKILL.md` | ✓ (계획 작성) |
| `writing-skills` | `skills/writing-skills/SKILL.md` | — |

소스 경로는 플러그인 캐시 루트(`~/.claude/plugins/cache/claude-community/superpowers/6.4.2/`) 기준 상대 경로다.

## 구현 위치

| 부분 | 파일 |
|---|---|
| 매니페스트(행동·단계·규칙·준비물·상태 소스·표현) | `native/macos/Sources/MightyCore/Resources/Styles/superpowers.json` |
| 상태 소스 스키마 | [mighty-styles.md](mighty-styles.md) §1.16 |
| 범용 엔진(디코딩·검증·평가·상태 읽기·투영·레지스트리) | `native/macos/Sources/MightyCore/Styles/*.swift` |
| 상태 읽기 및 stateOverrides 평가 | `native/macos/Sources/MightyCore/Styles/StyleEvaluator.swift` |
| 스타일 전환, 승인, 프롬프트 전송, 설치 터미널 | `native/macos/Sources/MightyClaude/AppStore+Styles.swift` |
| 범용 패널, 행동 칩 (Mac은 아직 상태 위젯을 그리지 않는다) | `native/macos/Sources/MightyClaude/GuidedPanel.swift`, `GuidedActionChip.swift` |
| 상태 읽기 엔진, 감시기 | `native/macos/Sources/MightyCore/Styles/StyleStateEngine.swift`, `native/macos/Sources/MightyClaude/StyleStateWatcher.swift` |
| 폰 렌더링 (상태 위젯 포함) | `mobile/src/components/guided-panel.tsx`, `mobile/src/lib/styles.ts` |
| 명령 인벤토리 검증 | `native/macos/Tests/MightyCoreTests/SuperpowersStyleTests.swift::buttonsSendInstalledPluginSkillCommands()` |
| 테스트 | `native/macos/Tests/MightyCoreTests/SuperpowersStyleTests.swift`, `StylesBundledTests.swift` |
