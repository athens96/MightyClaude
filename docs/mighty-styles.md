# 마이티 스타일 엔진 (계약 문서)

상태: **계약 확정, 미구현**. 이 문서는 구현 이전에 고정되는 계약이다. 마이티 모드의 "스타일"(지금의 [Ouroboros](ouroboros-mode.md) · [Paperthin](paperthin-mode.md))을 Swift 분기가 아니라 **선언적 JSON 매니페스트**로 구동하는 범용 엔진을 정의한다. 내장 두 스타일은 번들 매니페스트 2개로 이관되고, 폰(`mobile/`)은 매니페스트 결과를 받아 그리는 범용 렌더러가 되며, 엔진은 `mighty-style-engine-v1` 태그로 고정된 뒤 **JSON과 테스트만 추가하는 diff**로 oh-my-claudecode와 gstack이 붙는다.

읽는 순서: 스키마(1) → 검증(2) → 출처·신뢰(3·4) → 엔진 API(5) → 앱(6) → 폰(7) → 고정(8) → 테스트(9) → 후속(10) → 부록 A → 구현 순서(11). 부록 A는 네 스타일이 이 스키마로 표현되는지 확인하는 스케치이고, 11장은 이 계약을 서로 부딪히지 않는 세 갈래 작업으로 나눈다.

**범위 밖**: 그래프 레이아웃 엔진·블록 배치·블록 종류(`MightyGraph*`), Windows 클라이언트, 질문 패널(`AgentQuestionPanel`·`QuestionnaireProgress`)의 동작 자체, 원격 워크스페이스, Claude 이외의 프로바이더.

**적격성은 매니페스트가 정하지 않는다.** 가이드 스타일은 언제나 *로컬 워크스페이스의 Claude 실행 창 + 마이티 보기*에서만 쓸 수 있다(`AppStore+Ouroboros.swift:9-13`, `MobileRemoteSupport.swift:104-107`). 스키마에 적격성 필드는 없다.

**위협 모델.** 이 계약이 막는 상대는 둘이다. ① 사용자가 클론한 저장소에 `.claude/mighty-styles/*.json`을 넣어 둔 작성자 — 사용자의 행동은 "클론하고 실행 창을 연다"뿐이다. ② 사용자가 한 번 설득당해 등록한 `user` 매니페스트의 작성자. 사용자는 승인 카드를 **한 번, 보통의 주의력으로** 읽고 다시는 읽지 않는다고 가정한다. 1장·2장·4장의 규칙은 이 가정 위에서 설계됐다.

---

## 1. 매니페스트 스키마 v1

한 스타일 = UTF-8 JSON 파일 하나. 최상위는 객체이며 `"schema": 1`이 반드시 첫 필드로 존재한다(값이 정수 1이 아니면 즉시 거부). 모든 식별자·키·고정 어휘는 영어, 사람에게 보이는 문자열은 작성자의 언어 그대로 출력된다.

### 1.1 최상위 필드

| 필드 | 타입 | 필수 | 한도 · 규칙 | 뜻 |
|---|---|---|---|---|
| `schema` | int | ✓ | `1`만, **파일의 첫 키** | 스키마 버전 |
| `id` | string | ✓ | `^[a-z0-9][a-z0-9-]{0,39}$` | `RunSession.mightyStyle`에 저장되는 값. 예약 id는 1.2 참조 |
| `name` | string | ✓ | 1–40자, 예약 이름 금지(1.2) | 선택기·설정·그래프 머리말·폰에 보이는 이름 |
| `summary` | string | ✓ | 1–240자 | 선택기 툴팁 한 줄 |
| `subtitle` | string | ✓ | 1–80자 | 입력창 위 선택기 옆 한 줄 (오늘의 `styleHint`, `SessionPaneView.swift:56-60`) |
| `placeholders` | object | ✓ | 1.7 | 입력창 placeholder 4종 |
| `guidance` | object | ✓ | 1.7 | 패널 맨 아래 안내 한 줄 3종 |
| `prerequisites` | object | ✓ | 1.5 | 준비물 검사 |
| `install` | object | — | 1.5 | 설치 명령. 없으면 설치 버튼이 없다 |
| `phases` | array | ✓ | ≤16, 비어 있어도 됨 | 1.4 |
| `groups` | array | ✓ | 1–16 | 1.4 |
| `actions` | array | ✓ | 1–100 | 1.3 |
| `aliases` | array | ✓ | ≤64, 비어 있어도 됨 | 1.4 |
| `recognition` | object | ✓ | 1.4 | 과거 요청에서 행동을 되읽는 규칙 |
| `rules` | object | ✓ | 1.6 | 시작·단계·다음 행동·Enter·추천·초기 그룹 **여섯** |
| `capabilities` | array&lt;string&gt; | ✓ | ≤4, 비어 있어도 됨 | 이 매니페스트가 이름으로 참조하는 앱 내장 기능(1.8) |
| `autoAllow` | array | ✓ | ≤32, 비어 있어도 됨 | 1.9 |
| `presentation` | object | ✓ | 1.10 | 스타일 단위 아이콘·색 |

**최상위·중첩을 통틀어 모르는 키는 거부한다**(`E_UNKNOWN_FIELD`). 이유 한 줄: 승인 화면이 "파일 내용 전부"를 보여 주기로 한 이상, 앱이 뜻을 모르는 필드는 보여 줄 수도 설명할 수도 없어 약속을 깨기 때문이다. 앞으로의 확장은 `schema` 값으로 받는다 — v2 파일은 절반만 읽히는 대신 "이 앱은 schema 1만 읽습니다"로 분명히 거부된다(폰·Windows가 나중에 같은 파일을 읽을 때도 같은 판정을 내린다).

**같은 객체 안에 같은 키가 두 번 나오면 거부한다**(`E_DUPLICATE_KEY`). 2장의 사전 스캔이 원시 바이트에서 잡는다. 승인 화면이 원본 JSON 조각을 보여 주기로 한 이상, 보이는 것과 쓰이는 것이 다를 수 있는 입력은 받으면 안 된다. (실측: 이 툴체인의 `JSONSerialization`은 중복 키에서 **먼저 나온 값**을 남긴다 — 어느 쪽을 남기든 읽는 사람과 엔진이 갈라질 수 있다는 사실은 같다.)

### 1.2 예약 id와 예약 이름

**id.** `cli`(스타일 없음을 뜻하는 와이어 단어), `ouroboros`, `paperthin`. 번들이 아닌 매니페스트가 이 셋 중 하나를 쓰면 `E_RESERVED_ID`로 등록이 거부된다. 번들 id는 오늘 문자열 그대로이므로 저장된 실행 창이 마이그레이션 없이 열린다.

**이름.** 번들이 아닌 매니페스트의 `name`은, **NFKC 정규화 → 대소문자 접기 → 모든 공백 제거** 후 번들 스타일의 `name`(`Ouroboros`, `Paperthin`)에 같은 처리를 한 값과 같아서는 안 된다(`E_RESERVED_NAME`). 예약이 id에만 걸리면 사용자가 보는 화면에서는 아무것도 예약되지 않는다 — 사용자는 id가 아니라 이름을 읽는다. `id: "ouroboros-team"` + `name: "Ouroboros"`는 이 규칙이 없으면 선택기·그래프 머리말·폰에서 번들 스타일과 구별되지 않는다.

이름의 유사성(동형 문자·너비 0 문자)은 1.11의 금지 문자 목록이, 출처의 가시성은 1.10의 출처 배지 규칙이 맡는다. 셋이 함께 있어야 위조가 막힌다.

### 1.3 `actions`

```
{
  "id":        string,   // ^[A-Za-z0-9][A-Za-z0-9_.:-]{0,63}$ , actions 안에서 유일
  "title":     string,   // 1–40자, 버튼 라벨
  "help":      string,   // 0–400자, 툴팁·폰 설명
  "scope":     string?,  // 0–120자, "무엇 하나에 적용되는가"
  "prompt":    string,   // 1–400자, 1.3.1
  "takesText": bool,     // prompt에 {text}가 있는지와 반드시 일치
  "foldText":  "trimOnly" | "oneLine",   // takesText가 true면 필수, false면 금지, 1.3.2
  "requiresText": bool,  // 기본 false. UI 힌트다 — 1.3.3
  "match":     string?,  // 1–64자. recognition이 이 행동으로 이어져야 하는 이름, 1.3.4
  "phase":     string?,  // phases[].id 중 하나. 없으면 단계를 바꾸지 않는 행동
  "flags":     ["userInvoked" | "readOnly"],   // 닫힌 집합, 중복 불가, 기본 []
  "icon":      string?,  // 닫힌 아이콘 목록의 이름, 1.10
  "glyph":     string?,  // 정확히 1자(이모지 grapheme cluster). 폰과 요청 제목이 쓴다
  "tint":      string?,  // 팔레트 이름, 1.10
  "requestTitle": string?  // 1–40자. 요청 블록 제목 접두사를 직접 지정(기본 규칙은 1.10)
}
```

`foldText`가 `takesText: true`인데 없으면 `E_MISSING_FIELD`, `takesText: false`인데 있으면 `E_FOLD_TEXT`. 기본값을 두지 않는 이유: 두 접기 방식이 폰에서 온 글과 Mac에서 친 글을 서로 다르게 다루므로, 작성자가 고르지 않은 채로 어느 한쪽이 조용히 이기면 안 된다.

`requestTitle`은 1자 이상이다. 빈 문자열을 허용하면 "제목을 지운다"와 "제목을 지정하지 않는다"가 구별되지 않는다.

#### 1.3.1 프롬프트 템플릿

닫힌 치환 집합은 **`{text}` 하나뿐**이다.

- 템플릿에 `{text}`는 0회 또는 1회. 2회 이상은 `E_PROMPT_PLACEHOLDER`.
- `{text}` 이외의 `{` 또는 `}` 문자가 나타나면 `E_PROMPT_PLACEHOLDER`. 장래의 치환자를 밀반입할 수 없게 하려는 것이다.
- `takesText`가 true ⟺ `{text}`가 있다. 어긋나면 `E_TAKES_TEXT_MISMATCH`.
- 치환 규칙: 접은 텍스트가 **비어 있지 않으면** `{text}`를 그 텍스트로 바꾼다. **비어 있으면** `{text}`와 그 **바로 앞에 붙어 있는 U+0020 연속**을 함께 지운다.

이 규칙이 오늘 두 스타일의 문자열을 그대로 만든다.

| 오늘 | 매니페스트 | 결과 |
|---|---|---|
| `OuroborosFlow.prompt(skill:"interview", text:"  결제 모듈 리팩터링 ")` | `"prompt": "/ouroboros:interview {text}"`, `foldText: "trimOnly"` | `/ouroboros:interview 결제 모듈 리팩터링` |
| `OuroborosFlow.prompt(skill:"seed")` | `"prompt": "/ouroboros:seed"` | `/ouroboros:seed` |
| `PaperthinCatalog.prompt(skill:"re0", text:" docs/spec.md\n\n  tighten it \n")` | `"prompt": "/re0 {text}"`, `foldText: "oneLine"` | `/re0 docs/spec.md tighten it` |
| `PaperthinCatalog.prompt(skill:"nba", text:"")` | `"prompt": "/nba {text}"`, `foldText: "oneLine"` | `/nba` |

#### 1.3.2 텍스트 접기

- `trimOnly` — 앞뒤 공백·줄바꿈만 제거하고 내부 줄바꿈은 그대로 둔다(`OuroborosFlow.swift:61`).
- `oneLine` — 줄 단위로 쪼개 각 줄 앞뒤 공백을 제거하고 빈 줄을 버린 뒤 공백 하나로 잇는다(`PaperthinCatalog.swift:83`).

**폰에서 온 텍스트는 두 방식 모두 도달 전에 이미 한 줄로 접힌다**(`MobileRemoteSupport.swift:281-292`, docs/mobile-remote.md:152). 이 규칙은 엔진이 아니라 `/guided` 라우트의 규칙이며 그대로 유지된다.

#### 1.3.3 `requiresText`는 UI 힌트다

`requiresText: true`는 **Mac 패널과 폰 화면이 입력창이 비었을 때 그 버튼을 비활성으로 그린다**는 뜻일 뿐이다. `/guided` 라우트는 이 값을 **보지 않는다**. 근거: 오늘 `MobileRemoteExtensionTests.swift:367`이 `guidedPrompt(style:"ouroboros", skill:"interview", text:"\n \n") == "/ouroboros:interview"`를 단언한다 — 폰은 오늘도 빈 글로 `interview`를 쏠 수 있고, 라우트가 이를 막기 시작하면 그 단언값이 바뀐다. 완료 기준 1이 "같은 값"을 요구하므로 라우트는 오늘 그대로 둔다.

#### 1.3.4 `match` — 프롬프트와 인식의 결속

`recognition`(1.4)은 프롬프트 문자열에서 이름을 되읽는다. `prompt`는 자유 문자열이므로, 둘이 어긋나면 그 행동은 **영원히 인식되지 않는다** — 요청 제목도 안 붙고, 단계도 안 움직이고, 골든도 무의미해진다. 그런데 어긋나는 것을 아무것도 잡지 못한다.

그래서 **모든 행동에 대해** 다음이 성립해야 한다(`E_PROMPT_RECOGNITION`):

> `prompt`의 `{text}`를 빈 글로 치환한 문자열에 이 매니페스트의 `recognition`을 적용한 결과가, 그 행동의 `id` **또는** `match`와 같아야 한다.

`match`는 이름 충돌을 피해 프롬프트를 바꿔야 할 때의 탈출구다. 예: `id: "gstack-review"`, `prompt: "/review {text}"`, `match: "review"`. `match`는 `actions`의 다른 id·`aliases`의 이름과 겹칠 수 없다(`E_ALIAS_COLLISION`).

### 1.4 `phases` · `groups` · `aliases` · `recognition`

`phases[].id` · `groups[].id` · `actions[].id` · `aliases[].name`은 **각 배열 안에서** 유일해야 한다(`E_DUPLICATE_ID`). 넷은 **서로 다른 이름 공간**이다 — 같은 이름을 단계 id와 행동 id로 함께 쓸 수 있다(gstack의 `review`·`qa`·`ship`·`retro`가 그렇다). 단 하나의 교차 제약은 `aliases[].name`과 `actions[].id`(그리고 `actions[].match`)가 겹칠 수 없다는 것이다(`E_ALIAS_COLLISION`) — 인식된 이름 하나가 두 뜻을 가지면 안 되기 때문이다.

```
phases:  [{ "id": string, "title": string, "order": int }]
```
`id`는 `^[a-z0-9][a-z0-9-]{0,39}$`, `title` 1–24자, `order`는 0 이상 99 이하의 정수이며 매니페스트 안에서 유일해야 한다. 단계 표시줄은 `order` 오름차순으로 그린다. 빈 배열이면 단계 개념이 없는 스타일이다(Paperthin).

```
groups:  [{ "id": string, "title": string, "axis": string?, "question": string?, "actions": [actionId] }]
```
`title` 1–24자, `axis` 0–60자, `question` 0–200자, `actions`는 이 매니페스트의 행동 id만 담고 비어 있을 수 없다. 한 행동이 여러 그룹에 속해도 된다. 그룹이 **2개 이상이고 그중 하나 이상이 `axis`를 가지면** 패널이 그룹 지도를 그린다. 그렇지 않으면 그룹은 단순한 구획이고 패널은 평평한 목록을 그린다(Ouroboros).

**단계와 그룹은 실질적으로 배타적이다.** `rules.next`가 `byPhase`면 패널은 단계 막대와 단계별 행동만 그리고 **그룹 지도는 그리지 않는다** — 그룹은 승인 카드의 설명과 폰 페이로드의 구획 정보로만 남는다. `byGroup`이면 단계 막대의 순서와 prominent 행동을 잃는 대신 그룹 지도가 산다. 둘을 함께 그리는 화면은 v1에 없다(10장 16번).

```
aliases: [{ "name": string, "phase": phaseId }]
```
행동이 아니지만 과거 요청에서 인식되면 단계를 옮기는 이름들. `name`은 1–64자다. `phase`는 필수다 — 단계를 옮기지 않는 별칭은 아무 일도 하지 않으므로 표현할 이유가 없다.

```
recognition: { "prefixes": [string], "lowercase": bool }
```
`prefixes`는 1–4개, 각 1–32자. 과거 요청 문자열의 앞뒤 공백을 제거한 뒤 `prefixes`를 **배열 순서대로** 검사해 처음 일치하는 것을 떼어 내고, 남은 부분에서 **첫 공백 앞까지**를 이름으로 삼는다. `lowercase`가 true면 그 이름을 소문자로 바꾼다. 이름이 비면 인식 실패다. 인식된 이름은 ① 행동 id ② 행동 `match` ③ 별칭 이름 순으로 찾는다.

| 오늘 | 매니페스트 |
|---|---|
| `OuroborosFlow.skill(inPrompt:)` (`OuroborosFlow.swift:66-74`) | `{"prefixes": ["/ouroboros:", "ooo "], "lowercase": true}` |
| `PaperthinCatalog.skill(inPrompt:)` (`PaperthinCatalog.swift:88-93`) | `{"prefixes": ["/"], "lowercase": false}` |

**두 개의 다른 질문.** 평가기는 이 규칙 위에 서로 다른 두 가지를 노출한다(1.6의 Enter 규칙이 둘을 구별해 쓴다).
- `recognised(inPrompt:)` — 이름이 **행동·`match`·별칭으로 해석되는가**. 요청 제목·단계 계산이 쓴다.
- `namesSomething(inPrompt:)` — 접두사 하나가 붙어 있고 **이름 자리가 비어 있지 않은가**. 해석 여부는 보지 않는다. Enter 규칙이 쓴다.

`OuroborosFlow.skill(inPrompt:)`는 후자다: `"ooo 이거 해줘"`에 대해 `"이거"`를 돌려주며(행동이 아니다) 그래서 오늘 Enter가 다시 쓰이지 않는다. 반면 `OuroborosFlow.requestTitle`은 전자라 같은 입력에 `nil`을 준다(`OuroborosFlowTests.swift:26`). 하나로 합치면 `ooo <자유 문장>`이 전부 `/ouroboros:interview ooo <자유 문장>`으로 바뀌는 회귀가 난다.

**인식의 적용 범위는 실행 창이 아니라 레지스트리다** — 요청 블록 제목은 1.10이 정의하는 대로 그 실행 창에서 **실행 가능한 모든 스타일**을 훑는다. 단계 계산·Enter 규칙은 그 실행 창이 실제로 쓰는 매니페스트 하나만 본다.

### 1.5 `prerequisites` · `install`

```
prerequisites: { "mode": "all" | "any", "report": "first" | "all", "probes": [Probe] }   // probes 0–16개
```

`report`는 선택이고 기본값은 `"first"`다.

| Probe 종류 | 모양 | 판정 |
|---|---|---|
| `plugin` | `{ "kind": "plugin", "prefix": string, "missing": string, "hint": string?, "install": bool? }` | `~/.claude/plugins/installed_plugins.json`의 `plugins` 키 중 `prefix`로 시작하는 것이 있는가 (`OuroborosFlow.swift:152-154`) |
| `executable` | `{ "kind": "executable", "name": string, "missing": string, "hint": string?, "install": bool? }` | `PATH`의 각 구성 요소 아래 `name`이 실행 가능한 파일인가 (`OuroborosFlow.swift:155`) |
| `skill` | `{ "kind": "skill", "name": string, "scopes": ["user"\|"workspace"], "missing": string, "hint": string?, "install": bool? }` | `scopes`에 든 각 뿌리(`user`=홈, `workspace`=실행 창의 워크스페이스) 아래 `.claude/skills/<name>/SKILL.md`가 있는가 (`PaperthinCatalog.swift:101`) |

- `prefix`·`name`은 `^[A-Za-z0-9_][A-Za-z0-9@._-]{0,63}$`(아니면 `E_PROBE_NAME_SHAPE`). **맨 앞 밑줄을 허용한다** — 실제 스킬 이름에 쓰인다(`~/.claude/skills/_gstack-command/SKILL.md`, 부록 A.4).
- `missing` 1–200자, `hint` 0–200자.
- `scopes`는 1–2개, 중복 불가, 비어 있을 수 없다(`E_SCOPES`). 빈 배열은 `mode: "any"`에서 공허참인지 공허거짓인지 정해지지 않는다.
- `install`은 선택이고 기본값 `true`다. **"이 probe의 실패는 설치 명령이 고쳐 준다"**는 뜻이다.

**판정과 표시.**
- `mode: "all"`이면 모든 probe가 참이어야 준비 완료, `mode: "any"`면 하나라도 참이면 준비 완료다.
- 준비 미완료일 때 보이는 줄은 `report`가 정한다. `"first"`는 **미충족 probe 중 첫 번째**의 `missing`과 `hint`만 보여 준다. `"all"`은 미충족 probe 전부의 `missing`을 차례로 보여 주고 `hint`는 첫 번째 것만 보여 준다.
- `[설치]` 버튼은 `install`이 있고 **미충족 probe 중 `install: true`인 것이 하나라도 있을 때만** 그린다.

`report`와 per-probe `install`이 없으면 두 내장 스타일이 오늘과 달라진다. Ouroboros는 미충족 줄을 **하나만** 보여 주고(플러그인이 없으면 플러그인, 있으면 uvx), 설치 버튼은 **플러그인이 없을 때만** 그린다 — 설치 명령이 uvx를 설치하지 않기 때문이다(`OuroborosPanel.swift:111-116`). 그래서 Ouroboros는 `report: "first"` + uvx probe에 `install: false`다. Paperthin은 `mode: "any"`에 probe 4개가 모두 같은 `missing` 문자열이므로 `"first"`가 오늘 화면 그대로다.

`probes`가 0개인 것도 허용한다 — 설치할 것이 정말 없는 스타일이 "없다"고 말할 수 있어야 한다. 그때 준비물은 언제나 충족이고 준비물 블록은 그려지지 않는다.

레지스트리 파일은 4 MiB까지만 읽는다(`CLIAccountSupport.boundedData`, 오늘과 동일). 판정은 항상 워커에서 하고 결과만 메인 액터로 올린다.

```
install: { "command": string, "paneTitle": string }
```
`command` 1–400자(1.11의 금지 문자 포함 불가), `paneTitle` 1–40자.

**명령은 실행되지 않는다 — 그리고 이것은 오늘 동작의 변경이다.**

`pendingTerminalInput`의 값은 `{ text: String, autoRun: Bool }`이 된다.
- **매니페스트에서 온 문자열은 언제나 `autoRun: false`이며, 번들 매니페스트도 예외가 아니다.**
- `LocalTerminalSession`은 `autoRun`이 false이면 `paste(text:)`만 하고 `sendKey(.enter)`를 **호출하지 않는다**.
- `autoRun: true`는 **앱 자신이 만든 명령**에만 쓴다. v1에서 그런 곳은 CLI 로그인 하나뿐이다(`AppStore+CLIAccounts.swift:77`).
- 붙여넣기 직전, 출처와 무관하게 U+000A·U+000D·U+2028·U+2029가 하나라도 있으면 붙여넣지 않고 `"명령에 줄바꿈이 있어 터미널에 넣지 않았습니다."`로 실패한다 — 1.11의 금지 문자 검사와 **두 겹**이다(`AskUserQuestion`에 쓴 것과 같은 원칙).

> **무엇이 바뀌는가.** 오늘 `LocalTerminalSession.swift:118-125`는 셸이 붙고 0.9초 뒤 `view.paste(text:)`에 이어 `view.sendKey(.enter)`를 부르고, 실패하면 최대 3번까지 다시 시도한다. 즉 오늘은 Ouroboros·Paperthin의 `[설치]`를 누르면 명령이 **자동으로 실행된다**. 소유자의 결정은 "터미널 창에 미리 채워지고 사용자가 Enter를 누른다"이고, 이 계약은 그 결정 쪽으로 코드를 맞춘다. 두 내장 스타일의 설치 버튼도 이제 붙여넣기까지만 한다. 이것이 완료 기준 1에서 **의도적으로 값이 바뀌는 유일한 동작**이며, Ouroboros 매니페스트의 플러그인 probe `hint`도 "명령이 실행됩니다" → "명령이 채워집니다. Enter는 직접 누르세요."로 함께 바뀐다(그 문자열을 단언하는 테스트는 오늘 없다 — 뷰 코드에만 있다).
>
> 이 결정을 되돌리려면 1.5·4.4의 3번·4.6과 소유자 메모를 **모두** 고쳐 "즉시 실행됩니다"라고 적고, 누를 때 명령을 다시 보여 주는 별도 확인 시트를 붙여야 한다. 지금 문구를 둔 채 오늘 동작을 남기는 선택지는 없다.

**터미널 창 제목.** 설치 창의 제목은 `paneTitle`을 그대로 쓰지 않고 `"<paneTitle> · <스타일 name>"`으로 조립하고, 비번들 스타일이면 출처 배지를 함께 그린다. 근거: 앱 자신의 로그인 창도 같은 기구로 제목을 정하므로(`AppStore+CLIAccounts.swift:76`), `"Claude 로그인"`이라는 `paneTitle`을 쓰면 앱이 연 창과 구별되지 않는다.

### 1.6 `rules`

```
rules: {
  "start":        StartRule,
  "phase":        PhaseRule,
  "next":         NextRule,
  "enter":        EnterRule,
  "recommend":    RecommendRule,
  "initialGroup": InitialGroupRule
}
```
여섯 키 모두 필수다. 규칙 종류(`kind`)는 아래가 **전부**이며, 그 밖의 값은 `E_UNKNOWN_RULE`이다.

**StartRule**
- `{ "kind": "none" }` — 시작 단계에 특별한 버튼이 없다(Paperthin).
- `{ "kind": "actions", "phase": phaseId, "actions": [actionId], "resetTitle": string? }` — 현재 단계가 `start.phase`와 같으면 패널은 `next.map[phase]` 대신 **`start.actions`를 그 순서대로** 그린다(첫 번째가 prominent). `resetTitle`(1–24자)이 있으면 **다른 모든 단계에서** 행동 칩 줄 끝에 그 이름의 칩을 그리고, 누르면 단계를 `start.phase`로 되돌린 화면을 보여 준다(되돌린 상태에서는 `취소` 칩으로 원래 단계로 돌아간다). `start.phase`가 `phases`에 없으면 `E_START_PHASE`, `start.actions`가 비어 있으면 `E_MISSING_FIELD`.

이 규칙이 없으면 진입 단계에 버튼이 하나도 없다. Ouroboros의 `next.map.goal`은 빈 배열이고, 오늘 화면은 `phase == .goal`일 때 `인터뷰 시작`·`자동 진행`을 그린다(`OuroborosPanel.swift:43`·`65-74`). `새 목표` 칩도 같은 자리에서 온다(`:84`). oh-my-claudecode·gstack의 여러 진입 행동도 이 규칙으로만 표현된다.

**PhaseRule**
- `{ "kind": "none" }` — 단계 개념이 없다. `phases`가 **빈 배열일 때만** 허용하며, 아니면 `E_PHASE_RULE_NONE`.
- `{ "kind": "lastRecognisedAction", "default": phaseId }` — 실행 창의 요청 입력들(그래프 요청 블록의 `input`, 없으면 로그의 `user` 항목)을 **최신부터 거꾸로** 훑어 처음으로 인식되고 `phase`를 가진 행동/별칭의 단계를 현재 단계로 삼는다. 없으면 `default`. `phase`가 없는 행동(Ouroboros의 `status`·`unstuck`)과 자유 입력은 단계를 바꾸지 않는다(`OuroborosFlow.swift:90-95`). `phases`가 비어 있으면 이 종류를 쓸 수 없다(`E_UNKNOWN_REFERENCE`).

**NextRule**
- `{ "kind": "byPhase", "map": { phaseId: [actionId] } }` — 모든 단계가 키로 있어야 한다(빈 배열 허용, 빠지면 `E_RULE_INCOMPLETE`). 배열 순서가 버튼 순서이고 **첫 번째가 prominent**다.
- `{ "kind": "byGroup" }` — 지금 고른 그룹의 `actions`를 그 순서대로 낸다. prominent는 없다(추천이 그 역할을 한다).

**EnterRule**
- `{ "kind": "verbatim" }` — 입력창의 글을 그대로 요청으로 보낸다(Paperthin).
- `{ "kind": "rewriteBareDraftTo", "action": actionId, "phase": phaseId }` — 아래 조건이 **전부** 참일 때만 입력창의 글을 그 행동의 프롬프트로 바꿔 보낸다. 하나라도 거짓이면 `verbatim`과 같다.
  1. 실행 중이 아니다.
  2. 첨부가 없다.
  3. 앞뒤 공백을 제거한 글이 `/`로 시작하지 않는다.
  4. 그 글이 `recognition.prefixes` 중 하나로 시작하고 접두사를 뗀 첫 이름이 비어 있지 않은 경우가 **아니다** — 즉 `namesSomething(inPrompt:)`가 거짓이다. 그 이름이 행동·별칭으로 해석되는지는 **보지 않는다**(1.4).
  5. 현재 단계가 `phase`와 같다.
  6. **이 실행 창에 요청이 하나도 없다**, 또는 사용자가 방금 `rules.start.resetTitle` 칩(되돌리기 칩)을 눌렀다.

  (1–5는 오늘의 `SessionPaneView.swift:608-622`와 같다. 대기 중인 질문이 있으면 Enter는 언제나 그 질문에 답하며, 이 규칙보다 먼저다.)

  **조건 6에 되돌리기 칩이 들어 있는 이유.** 되돌리기 칩은 오늘 Ouroboros의 `새 목표`다. 누르면 화면이 진입 단계로 돌아가고 `인터뷰 시작`·`자동 진행`이 다시 그려지는데, 그 상태에서 사용자가 새 목표를 치고 Enter를 누르는 것이 그 칩의 **유일한 용도**다. "요청이 하나도 없다"만 보면 그 Enter가 맨 문장으로 나가 버린다 — `새 목표`를 누를 수 있는 실행 창에는 언제나 요청이 있기 때문이다. 그러면서도 제약 2는 깨지지 않는다: **누가 눌렀는지가 사실**이고, 매니페스트는 자기 칩을 대신 눌러 줄 수 없다. 매니페스트가 정할 수 있는 것은 칩의 이름뿐이다.

  `rewriteBareDraftTo`에는 세 가지 제약이 더 붙는다.
  1. `action`이 가리키는 행동은 `takesText: true`여야 한다(`E_ENTER_ACTION_TEXT`). 사용자가 친 글이 **버려지는** 규칙은 표현할 수 없다. 그렇지 않으면 `"아니 아직 배포하지 마"`를 치고 Enter를 눌렀을 때 앱이 정확히 `/ship`만 보내는 매니페스트를 쓸 수 있다.
  2. 조건 6이 그 제약이다. 조건 4(`recognition`)와 조건 5(`phase`)는 **둘 다 매니페스트가 정하므로 스스로를 영원히 참으로 만들 수 있다** — 아무도 치지 않을 접두사 하나(`"zzzzz-never"`)와 언제나 같은 값을 내는 `phase` 규칙이면 사용자의 **모든** Enter가 작성자가 고른 명령으로 바뀐다. "첫 요청"도 "사용자가 되돌리기 칩을 눌렀다"도 매니페스트가 조작할 수 없는 사실이다.
  3. 규칙이 발동 가능한 상태일 때 입력창은 **보낼 명령의 접두사를 비편집 칩으로 왼쪽에 그린다**(예: `/ouroboros:interview`). `placeholders.initial`은 매니페스트의 글이므로 이 사실을 알리는 데 쓰지 않는다 — 작성자가 "그냥 메모하세요"라고 적을 수 있다. 칩은 **지금 입력창에 들어 있는 글까지 포함해** 여섯 조건을 모두 본다: 조건 3·4가 거짓인 글(`/help`, `ooo 뭐 좀`)을 치고 있는 동안 칩이 떠 있으면, 칩이 약속한 것과 Enter가 보내는 것이 달라진다.

**RecommendRule**
- `{ "kind": "none" }`
- `{ "kind": "capability", "capability": name, "map": { state: actionId }, "group": groupId? }` — 내장 기능이 돌려주는 닫힌 상태 값을 행동 id로 옮긴다. `map`은 그 기능의 상태 값을 **빠짐없이** 담아야 한다(`E_CAPABILITY_MAP`). 추천 값 자체는 **보고 있는 그룹과 무관하다**: 기능의 상태가 답이고, 그 행동이 지금 그려지는 줄에 없으면 강조될 칩이 없을 뿐이다. `group`은 **어느 그룹의 첨부 줄에 이 기능이 붙는지만** 말한다(6.1의 6번).
  > 근거: 폰에는 고른 그룹이 없고 투영은 언제나 `selectedGroupId: nil`로 부른다. 추천을 그룹으로 막으면 `initialGroup`이 고른 기본 그룹이 `rules.recommend.group`과 다른 순간 — Paperthin에서 케이스북이 `absent`일 때가 정확히 그렇다 — 폰의 `mighty.paperthin.recommended`가 `re0-plan`에서 `null`로 떨어진다. 8.1의 완료 기준 1이 금지하는 값 변화다.

**InitialGroupRule**
- `{ "kind": "fixed", "group": groupId }`
- `{ "kind": "capabilityState", "capability": name, "map": { state: groupId } }` — 내장 기능을 **처음 읽었을 때 한 번** 그룹을 정한다. 이후 상태가 바뀌어도 보고 있던 그룹은 움직이지 않는다(`PaperthinPanel.swift:43-46`). `map`은 `RecommendRule`과 같은 규칙으로 그 기능의 상태 값을 빠짐없이 담아야 하며, 같은 코드 `E_CAPABILITY_MAP`을 쓴다.

### 1.7 `placeholders`와 `guidance`

```
placeholders: { "idle": string, "initial": string?, "running": string?, "answering": string }
```
각 0–120자. 고르는 순서는 고정이다: 질문 대기 중이면 `answering` → 실행 중이면 `running`(없으면 앱 기본) → 현재 단계가 `rules.enter.phase`와 같으면 `initial` → 그 밖에는 `idle`. `initial`은 `EnterRule`이 `rewriteBareDraftTo`일 때만 허용된다(`E_PLACEHOLDER_INITIAL`).

```
guidance: { "start": string?, "next": string?, "running": string? }
```
각 0–160자. **패널의 맨 아랫줄**(6.1의 8번)이며 입력창 placeholder와는 다른 자리다. 고르는 순서: 실행 중이면 `running` → `rules.start`가 그려지고 있으면 `start` → 그 밖에는 `next`. 해당 값이 없으면 그 줄을 그리지 않는다.

치환자는 **`{phase}` 하나**뿐이며 현재 단계의 `title`로 바뀐다. 단계가 없으면 `{phase}`와 **바로 뒤에 붙어 있는 U+0020 하나**를 함께 지운다. `{phase}` 이외의 중괄호는 `E_PROMPT_PLACEHOLDER`.

이 필드가 없으면 오늘 패널의 세 줄이 사라진다. Ouroboros는 `OuroborosPanel.swift:67`(목표)·`:78`(`"\(phase.title) 단계가 끝났습니다…"`)·`:93`(`"\(phase.title) 진행 중…"`)이 모두 단계 제목을 끼워 넣고, Paperthin은 `PaperthinPanel.swift:30`이 실행 중/아닐 때 두 줄을 가른다. 오늘 Paperthin의 실행 중 줄은 **패널** 줄이지 입력창 placeholder가 아니다 — 실행 중인 Paperthin 실행 창의 placeholder는 오늘 앱 공통 문구로 떨어진다(`SessionPaneView.swift:75`).

### 1.8 이름으로 참조하는 앱 내장 기능 (`capabilities`)

매니페스트는 **이름만** 적고 앱이 구현을 가진다. v1에서 고정되는 목록은 **하나**다.

| 이름 | 무엇을 읽는가 | 무엇을 돌려주는가 |
|---|---|---|
| `paperthin.casebook` | 실행 창 워크스페이스의 `.re0/iteration/` 아래, 최근 수정된 폴더 24개만. 각 폴더의 `*.local.md` 파일 목록을 `DESIGN` → `WORKFLOW` → `EVIDENCE` → `RETRO` 순, 나머지는 이름순으로 정렬해 24개까지. (`PaperthinCatalog.swift:116-152` 그대로) | ① **상태** `absent` \| `open` \| `complete` — 케이스북이 없으면 `absent`, `DESIGN.local.md`와 `RETRO.local.md`가 **둘 다** 있으면 `complete`, 그 밖에는 `open`. ② **첨부 항목** `{ id: 파일명, title: ".local.md"를 뗀 이름, detail: 폴더명 + " · " + weight, readOnly: true }` 목록. `weight`는 `DESIGN.local.md`가 있으면 `full`, 없으면 `lightweight`. ③ **빈 상태 문구** `"열린 사이클이 없습니다. re0-plan으로 케이스북을 여세요."` |

**링크와 경계.** 폴더 목록을 열기 전에 `.re0/iteration`을 `resolvingSymlinksInPath`로 풀고, 그 결과가 같은 방식으로 푼 워크스페이스 경로 **아래에 있을 때만** 진행한다. 각 항목은 `URLResourceValues`로 `isSymbolicLink == false`, `isRegularFile == true`(폴더는 `isDirectory == true`), **`linkCount == 1`**을 확인한다. `openPath`는 푼 경로가 워크스페이스 아래일 때만 채운다. 하드 링크는 일반 파일과 구별되지 않으므로 `linkCount`로만 걸린다.

**출력 문자열의 정규화.** `StyleAttachmentItem`의 `id`·`title`·`detail`, `StyleCasebook.name`·`files`는 **파일 시스템에서 온 값**이므로 매니페스트 문자열과 **같은 정규화를 거친다**: 1.11의 금지 문자를 U+FFFD로 바꾸고, `title`·`detail`은 80자로 자른다. 맥 화면과 폰 페이로드 양쪽에서, 투영 직전에 **한 번** 한다. 근거: 이 경로에는 승인 관문이 없다(Paperthin은 번들이다). 저장소가 `<U+202E>…`라는 폴더 이름 하나로 Mac 패널과 폰 페이로드에 방향 제어 문자를 넣을 수 있어서는 안 된다. 폰의 방어적 파싱(7.6)은 두 번째 겹이지 첫 번째가 아니다.

**첨부 줄은 언제나 그린다.** 내장 기능이 첨부를 선언한 스타일이 해당 그룹을 보고 있으면, 항목이 0개여도 그 줄을 그리고 기능이 준 빈 상태 문구와 새로고침 버튼을 보여 준다(오늘 `PaperthinPanel.swift:116-119`). 파일 칩은 **6개까지** 그린다(`:107`의 `prefix(6)`). 페이로드는 24개까지 나르되 화면은 6개다.

`capabilities`에 없는 이름을 `rules`가 참조하면 `E_CAPABILITY_UNDECLARED`, 목록에 **앱이 모르는 이름**이 있으면 `E_UNKNOWN_CAPABILITY`로 매니페스트 전체가 무효다. 조용히 무시하지 않는다 — 기능이 빠진 채로 동작하는 스타일이 승인 화면에서 설명한 것과 달라지기 때문이다.

이 목록은 `mighty-style-engine-v1` 태그에서 규칙 어휘·팔레트·아이콘 목록과 함께 고정된다.

### 1.9 `autoAllow`

```
autoAllow: [{ "server": string?, "tool": string }]
```
- `server` — `^[A-Za-z0-9_-]{1,64}$`이고 `__`를 **포함할 수 없으며** `_`로 **끝날 수 없다**. 와이어 이름은 정확히 `mcp__<server>__<tool>`로 조립된다. Ouroboros 플러그인의 서버 이름은 `plugin_ouroboros_ouroboros`다(`OuroborosFlow.swift:38`). 하이픈을 허용하는 이유: 실제로 쓰이는 서버 이름에 하이픈이 들어간다(oh-my-claudecode의 `plugin_oh-my-claudecode_t`).
- `tool` — `^[A-Za-z0-9_-]{1,64}$`이고 `__`를 포함할 수 없으며 `_`로 **시작할 수 없다**.
- 끝/앞 밑줄을 함께 막아야 `mcp__<server>__<tool>`의 분해가 **유일해진다**. `__`만 막으면 `server: "a_"` + `tool: "_b"`가 `mcp__a____b`를 만들고, 이는 서버 `a`의 도구 `__b`로도 읽힌다. 위반은 `E_AUTOALLOW_SHAPE`.
- **소속 규칙.** `server`가 있는 항목은, 이 매니페스트의 `prerequisites.probes` 중 `kind: "plugin"`이고 `prefix`가 **`@`로 끝나는** probe에 대해, 그 `@`를 뗀 이름 `P`에 대해 `plugin_<P>_`로 **시작해야 한다**. 아니면 `E_AUTOALLOW_FOREIGN_SERVER`. `@`로 끝나지 않는 `prefix`는 소속을 만들지 못한다: 그런 probe는 설치 목록의 키를 `hasPrefix`로 보므로, `prefix: "a"` 하나가 설치된 플러그인 `a_b`를 만족시키면서 `plugin_a_b_*` 서버 전부를 주장하게 된다. 남는 헐거움은 `docs/styles-followups.md`에 적었다 — Claude Code의 플러그인 이름에 `_`가 들어갈 수 있는 한, 이름 하나를 정확히 짚는 검사는 probe가 설치된 키로 풀려야 가능하고 그것은 검증 시점에 없는 정보다. 스타일은 **자기가 설치를 요구하는 플러그인의 도구만** 자동 허용할 수 있고, 사용자가 따로 설정한 MCP 서버(`mcp-atlassian`, 사내 서버 등)나 **다른 플러그인의 도구는 어떤 매니페스트도 자동 허용할 수 없다**. 근거: 오늘 `ouroboros@` probe ↔ `plugin_ouroboros_ouroboros`, `oh-my-claudecode@` probe ↔ `plugin_oh-my-claudecode_t`가 모두 이 규칙을 만족한다(1.13·A.3). Paperthin·gstack은 `autoAllow`가 비어 있어 무관하다.
- `server`를 생략할 수 있는 것은 **`tool`이 정확히 `ToolSearch`일 때뿐**이다(런타임의 도구 검색, MCP가 아니다). 그 밖에 `server` 없는 항목은 `E_AUTOALLOW_SERVER`. 그리고 `ToolSearch`는 **번들 매니페스트에서만** 허용한다 — 비번들이 쓰면 `E_AUTOALLOW_TOOLSEARCH_BUNDLED`. 근거: `ToolSearch`는 실행이 아니라 *도구 표면의 확장*이며, 프롬프트 없이 런타임이 새 도구 스키마(`WebFetch`·`RemoteTrigger`·`CronCreate`·쓰기 가능한 MCP 도구 전부)를 끌어오게 하는 유일한 열쇠다. 제3자가 이것을 조용히 가질 이유가 없다.
- `tool`이 `AskUserQuestion`이면 `E_AUTOALLOW_QUESTION`으로 파일 전체가 거부된다. 또한 엔진은 런타임에도 이 이름을 무조건 거절한다 — 두 겹으로 막는다.
- 같은 `(server, tool)` 쌍이 두 번 나오면 `E_AUTOALLOW_DUPLICATE`.
- 한도는 **32**다. 사람이 한 화면에서 읽을 수 있는 수가 상한의 근거다. 번들 최대치는 17개(`ToolSearch` + 상태 도구 16개, 1.13)이고 A.3의 oh-my-claudecode는 16개이므로, 이 한도는 계획된 네 스타일을 모두 담는다.

판정은 **와이어 이름 문자열의 완전 일치**다. 접두사 일치도, 글롭도, 정규식도 없다. 두 정규식이 `*`·`(`·`)`·`:`·공백을 모두 막으므로 `Bash(*)`·`Write`·`Edit(src/**)` 같은 패턴은 **스키마로 표현 불가능**하다. `__`를 금지하므로 `mcp__plugin_ouroboros_ouroboros__evil__ouroboros_interview` 같은 경계 위조도 만들 수 없고, 다른 서버가 `ouroboros_interview`라는 이름을 흉내 내도 `server`가 다르므로 일치하지 않는다(오늘의 스푸핑 방지 근거를 그대로 유지, `OuroborosFlowTests.swift:34-38`).

**여기서 막지 못하는 것을 분명히 적는다.** 소속 규칙 안에서 **쓰기 도구·실행 도구를 고르는 것은 여전히 매니페스트의 자유다**. `plugin_<P>_` 소속 규칙은 "남의 서버"를 막을 뿐 "그 플러그인의 위험한 도구"를 막지 않는다 — 읽기 전용 분류는 앱이 갖고 있지 않고, 태그 전에 만들 수 있는 것도 아니다. 그래서 승인 카드의 자동 허용 구역은 **접히지 않고 맨 위에 온다**(4.4).

자동 허용은 **승인된(또는 번들) 스타일의 실행 창**에서만 적용되고, 허용이 실제로 전달되는 동안만 권한 카드를 숨긴다. 전달이 실패하면 일반 권한 카드로 나타난다(`AppStore+Ouroboros.swift:174-185`).

### 1.10 `presentation`과 팔레트

```
presentation: { "icon": string?, "tint": string? }
```

**팔레트**는 닫힌 이름 집합이며 앱이 이미 쓰는 색에 1:1로 맞춘다.

| 이름 | Mac 색 (근거) |
|---|---|
| `accent` | `Palette.accent` (`Palette.swift:9`) — **기본값** |
| `purple` | `.purple` (`MightyGraphView.swift:53`) |
| `teal` | `.teal` (`:52`) |
| `indigo` | `.indigo` (`:51`) |
| `mint` | `.mint` (`:50`) |
| `orange` | `.orange` (`:49`) |
| `green` | `.green` (`:227`) |
| `red` | `.red` (`:227`) |
| `secondary` | `Color.secondary` |

폰에는 이름 그대로 실려 가고, 폰의 테마가 같은 이름을 자기 팔레트로 옮긴다. 목록에 없는 이름은 `E_UNKNOWN_TINT`.

**아이콘은 닫힌 목록이다.** 팔레트와 같은 방식이며, 태그에서 고정된다. 목록 밖이면 `E_UNKNOWN_ICON`. 이렇게 하면 "MightyCore는 AppKit에 의존하지 않아 SF Symbol의 존재를 검증할 수 없다"는 문제와 대체 심볼 규칙이 함께 사라지고, **앱 자신의 보안·상태 어휘를 매니페스트가 빌려 쓸 수 없다**.

v1 목록(33개):

| 묶음 | 이름 |
|---|---|
| 번들이 쓰는 것 (11) | `point.3.connected.trianglepath.dotted` · `questionmark.bubble` · `wand.and.stars` · `leaf` · `play.fill` · `checkmark.seal` · `arrow.triangle.2.circlepath` · `infinity` · `gauge.with.dots.needle.33percent` · `lightbulb` · `square.grid.2x2` |
| 앱 기본·대체 (2) | `arrow.up.message` · `questionmark.square.dashed` |
| 중립 (20) | `arrow.right` · `arrow.clockwise` · `arrow.triangle.branch` · `bolt` · `book` · `bookmark` · `calendar` · `chart.bar` · `cube` · `doc.text` · `flag` · `folder` · `hammer` · `list.bullet` · `magnifyingglass` · `map` · `paintbrush` · `puzzlepiece` · `sparkles` · `tray` |

목록에 **없는** 것과 그 이유: `lock*` · `shield*` · `checkmark.shield*` · `checkmark.circle*` · `exclamationmark.*` · `xmark.*` · `hand.raised*`는 앱이 권한·오류·차단을 말할 때 쓰는 어휘라 행동 칩이 보안 제어처럼 읽히게 만든다. `eye*`·`person*`은 패널이 `readOnly`·`userInvoked` 표식으로 직접 쓴다(6.1의 7번).

`checkmark.seal`은 **하나의 예외로 남긴다**: 오늘 Ouroboros의 `evaluate` 행동이 쓰는 심볼이고(`OuroborosFlow.swift:46`), 완료 기준 1이 값 보존을 요구한다. 대신 아래 두 장치가 남는다 — 요청 블록 제목은 언제나 앱이 쓴 `요청 N · <provider>` 꼬리를 달고 있고(아래), 비번들 스타일의 이름 옆에는 언제나 출처 배지가 붙는다.

`glyph`는 **정확히 1자**(grapheme cluster 하나)이고, 유니코드의 이모지 표현을 갖는 문자여야 한다(아니면 `E_TYPE`). 8자를 허용하면 칩 하나가 문장이 된다. 이 검사 **하나로만** 판정한다 — 1.11의 금지 문자 목록은 `glyph`에 적용되지 않는다. ZWJ(U+200D)가 그 목록에 있고 `👩‍💻`·`🏳️‍🌈`·`👨‍👩‍👧`가 모두 ZWJ로 묶인 **한 자**이기 때문이며, 근거는 1.11에 적었다. 폰은 SF Symbol 렌더러가 없으므로 `icon`을 무시하고 `glyph`(없으면 종류별 중립 표식)를 그린다 — 오늘 `mobile/src/lib/mighty.ts:32-42`가 하는 일 그대로다.

**요청 블록 제목 접두사.** 매니페스트가 정할 수 있는 것은 제목 전체가 아니라 **접두사**다.

접두사를 정하는 순서:
1. 행동의 `requestTitle`이 있으면 그것.
2. `glyph`가 있으면 `glyph + " " + title`.
3. 행동에 `phase`가 있으면 그 단계의 `title`.
4. 그 밖에는 `title`.

2번과 3번의 순서가 중요하다. 단계 제목이 위에 있으면 단계를 가진 행동이 모두 자기 이름 대신 단계 이름으로 보여서, gstack처럼 단계를 촘촘히 쓰는 카탈로그에서 `/qa`와 `/qa-only`가 그래프에서 구별되지 않는다. 이 순서는 두 내장 스타일의 오늘 값을 모두 그대로 만든다(Ouroboros 행동에는 `glyph`가 없고, Paperthin 행동에는 `phase`가 없다): `/ouroboros:evaluate` → `평가`(3), `/ouroboros:unstuck` → `막힘 풀기`(4), `/prism README.md` → `🔺 prism`(2), `/nba` → `🎯 nba`(2), `/ouroboros:seed` → `시드`(3).

**접두사는 레지스트리가 정한다.** 한 실행 창의 요청 블록 제목은 그 창의 매니페스트 하나가 아니라, **그 실행 창에서 실행 가능한(`runnable`) 모든 스타일**을 우선순위(`bundled` > `user` > `workspace`, 같은 출처 안에서는 id 사전순)로 훑어 **처음 인식되는 스타일의 규칙**으로 정한다. **훑기는 그 실행 창이 실제로 스타일을 돌리고 있을 때만 한다**(`guidedStyle(_:) != nil`) — CLI 실행 창의 요청 블록에는 접두사도 아이콘도 색도 붙지 않는다. 오늘 `MightyStyles.requestTitle(forInput:style: nil)`이 `nil`을 내는 것과 같고, 맥과 폰 와이어 양쪽에 똑같이 적용된다.

- 근거: 오늘 `MightyStyles.requestTitle(forInput:style:)`(`OuroborosFlow.swift:30-33`)이 가이드 스타일 창이기만 하면 Ouroboros → Paperthin 순으로 둘 다 시도한다. 실행 창은 스타일이 바뀌어도 이전 블록을 그대로 갖고 있기 때문이다(`:28-29`의 주석). 오라클이 이 값을 단언한다: `requestTitle(forInput: "/ouroboros:seed", style: "paperthin") == "시드"`, `("/nba", style: "paperthin") == "🎯 nba"`(`PaperthinCatalogTests.swift:24`). 매니페스트 하나만 보면 이 두 값이 `nil`로 회귀한다.
- **미승인 스타일은 여기에 참여하지 않는다.** `runnable`만 훑으므로, 승인 전 스타일은 제목을 통해서도 존재를 드러내지 않는다(4.5).
- **해시가 어긋난 실행 창은 접두사를 잃는다.** 3.4의 해시 결속으로 실행 창이 `nil` 스타일이 되면 그 창은 일반 CLI이므로 접두사가 아예 그려지지 않는다. 옛 블록이 제목을 잃는 쪽이지, 같은 id를 주장하는 **낯선 매니페스트의 제목을 입는 쪽이 아니다** — 그것이 이 결속의 목적이다. 번들 두 스타일은 해시 검사를 받지 않으므로 오늘 값이 그대로다.
- 구현 위치: `StyleRegistry.requestTitle(forInput:workspacePath:)`. 평가기 하나가 아니라 레지스트리가 갖는다.

**Mac의 조립.** `MightyGraphView.swift:215`는 오늘 `(접두사.map { $0 + " · " } ?? "") + "요청 \(index + 1) · \(ProviderOptions.label(provider))"`를 만든다. 이 조립을 그대로 둔다 — 매니페스트가 정하는 것은 앞부분뿐이고 **`요청 N · <provider>` 꼬리는 언제나 앱이 쓴다**. 그래서 매니페스트 문자열만으로는 앱이 쓴 블록을 완전히 흉내 낼 수 없다.

**폰의 사용.** `MobileMightyRun.title`(`MobileRemoteSupport.swift:193`)은 접두사 **하나만** 싣는다. 폰은 그것을 블록 이름으로 쓰고 번호·프로바이더는 자기 화면 규칙대로 붙인다. 오늘과 같다.

**요청 블록 아이콘·색**은 행동 → 단계 → 스타일 → 앱 기본(`arrow.up.message` / `accent`) 순으로 처음 지정된 값을 쓴다. 둘 다 지정하지 않은 내장 두 스타일은 오늘과 픽셀 단위로 같다(`MightyGraphView.swift:215-216`).

**그래프 머리말.** 오늘은 `Label("마이티", systemImage:)` 뒤에 `headerSummary`(요청·에이전트·토큰 수)가 붙는다(`MightyGraphView.swift:74-79`). 가이드 스타일에서는 라벨의 글이 `마이티 · <name>`이 되고, 현재 단계가 있으면 `마이티 · <name> · <단계 title>`이 된다. **`headerSummary`는 그 뒤에 그대로 남는다** — 자리도 문구도 바뀌지 않는다. 블록 배치·종류·레이아웃은 건드리지 않는다. 내장 두 스타일도 머리말만은 오늘과 달라진다(`마이티` → `마이티 · Ouroboros · 시드`).

**출처 표식은 이름을 따라다닌다.** 비번들 스타일의 `name`이 나오는 **모든 자리** — 선택기, 그래프 머리말, 설정, 승인 카드·시트, 설치 터미널 창 제목, 폰의 `presentation.headerTitle` — 에 출처 배지(`사용자 등록` / `저장소에서 발견됨`)를 **이름 바로 옆에** 함께 그린다. 설정 화면에만 있는 배지는 위조를 막지 못한다. 폰 페이로드의 `style`에 `source: "bundled"|"user"|"workspace"`를 더한다(7.3).

설치 터미널 창의 제목은 셸 세션에 문자열 하나로만 실려 가므로, 배지를 **그 문자열 안에** 넣는다: `<paneTitle> · <name>`, 비번들이면 뒤에 `· <배지>`. 이 자리가 특히 중요한 이유는 하나뿐이다 — 여섯 자리 중 **프리필된 셸 명령으로 끝나는 유일한 화면**이고, 1.11이 인정하듯 동형 문자로 지은 이름(`Ouroborοs`, 그리스 ο)을 막는 것은 배지뿐이다.

### 1.11 한도

| 대상 | 한도 |
|---|---|
| 파일 | 262 144 바이트 (256 KB). 초과 시 파싱 전에 `E_TOO_LARGE` |
| `actions` | 100 |
| `groups` | 16 · `phases` 16 · `aliases` 64 · `autoAllow` **32** · `capabilities` 4 · `prerequisites.probes` 16 · `recognition.prefixes` 4 |
| JSON 중첩 깊이 | 8. **파싱 전** 사전 스캔에서 센다(2장) |
| 문자열 | 1.1–1.10의 각 필드에 적힌 값. 어떤 문자열도 400자를 넘지 않는다 |
| JSON 키 이름 | **문자열과 똑같이 다룬다**: 금지 문자 검사(`E_CONTROL_CHAR`)와 길이 **64자**(`E_STRING_LENGTH`). 스키마 1의 키는 전부 짧은 영문 낱말이고, 작성자가 고르는 키는 id(최대 40)뿐이다. 그리고 키 리터럴에는 **이스케이프를 쓸 수 없다**(`E_KEY_ESCAPE`, 2장) |
| 제어·보이지 않는 문자 | 모든 문자열과 키에서 금지: U+0000–U+001F, U+007F–U+009F, U+00AD, U+061C, U+200B–U+200F, U+202A–U+202E, U+2028, U+2029, U+2060, U+2066–U+2069, U+FEFF (`E_CONTROL_CHAR`). **`actions[].glyph`만은 예외**로, 1.10의 "이모지 한 자" 검사 하나로 판정한다 |
| 구분자 | `name` · `phases[].title` · `actions[].requestTitle` · `install.paneTitle`에 U+00B7(`·`)를 쓸 수 없다 (`E_RESERVED_SEPARATOR`) |
| 정수 | `schema`·`phases[].order`는 정수값이고 `Int` 범위 안이어야 한다 (`E_TYPE`) |

**금지 문자 목록의 근거.** 방향 제어(U+202A–U+202E, U+2066–U+2069, U+200E/F, U+061C)는 승인 화면의 글을 뒤집는 데 쓰인다. 너비 0 문자와 연결자(U+200B–U+200D, U+2060, U+FEFF, U+00AD)는 `"Ouro<U+200B>boros"`처럼 **눈에는 같고 문자열 비교에는 다른** 이름을 만든다 — 1.2의 예약 이름 검사를 그냥 통과해 버린다. 줄·문단 구분자(U+2028·U+2029)는 SwiftUI `Text`가 줄을 바꾸므로, 400자짜리 `install.command`나 `help`의 뒷부분을 승인 카드 밖으로 밀어내고 그 뒤를 새 문단처럼 보이게 한다.

**막지 못하는 것.** 키릴 `О`·그리스 `Ο`·전각 문자 같은 **동형 문자**는 이 목록으로 걸리지 않는다. `Оuroboros`(키릴 О)는 유효한 이름이다. 그것을 막는 것은 1.10의 출처 배지다 — 이름이 아무리 비슷해도 `저장소에서 발견됨`이 이름 바로 옆에 붙는다.

**`·` 금지의 근거.** 앱이 머리말과 요청 제목을 조립할 때 쓰는 문자다. `"name": "Ouroboros · 승인됨"`은 머리말 안에서 앱 크롬을 위조하고, `"requestTitle": "✅ 권한 허용됨"`은 그래프와 폰에서 앱이 쓴 상태처럼 읽힌다. `install.paneTitle`이 이 목록에 있는 이유는 1.10의 조립(`<paneTitle> · <name> · <배지>`)에 그대로 들어가기 때문이다 — 작성자가 `·`를 쓸 수 있으면 프리필된 셸 명령이 든 탭의 제목 전체를 스스로 짤 수 있다.

**`glyph`가 금지 문자 목록에서 빠지는 근거.** U+200B–U+200D를 막는 목적은 `"Ouro<U+200B>boros"`처럼 **이름**을 위조하는 것이고, 그중 U+200D(ZWJ)는 `👩‍💻`·`🏳️‍🌈`·`👨‍👩‍👧`를 **한 grapheme cluster**로 묶는 문자다. `glyph`는 정의상 한 자이므로 위조할 이름이 없다. 그래서 `glyph`는 1.10의 이모지 검사 하나로만 판정한다.

**정수의 근거.** 실측: 이 툴체인의 `JSONSerialization`은 `1e400`을 값으로 만들지 않고 **잡을 수 있는 오류로 거부하며**(`NSCocoaErrorDomain 3840, "Number wound up as NaN"`), `1.0`과 `1`을 구별하지 않고 둘 다 정수 1로 읽는다. 위험한 것은 그 사이다: `1e308`은 유한한 `Double`로 파싱되고 `Int(1e308)`은 **오류가 아니라 트랩**이다. 그래서 정수 필드는 값이 정확한 정수이고 `Int` 범위 안일 때만 받고, `Double`을 거쳐 `Int(_:)`로 바꾸지 않는다. `1.0`은 정수 1과 같은 값으로 받아들인다 — 파서가 이미 구별하지 않는다.

### 1.12 지역화

매니페스트의 문자열은 **작성자가 쓴 그대로** 표시된다. 번역 테이블도, 치환도, 복수형 처리도 없다. 앱 자신의 문구(버튼 "허용", 오류 메시지)는 앱의 언어를 따른다.

### 1.13 Ouroboros 번들 매니페스트 (전문)

```json
{
  "schema": 1,
  "id": "ouroboros",
  "name": "Ouroboros",
  "summary": "인터뷰로 요구를 또렷하게 만든 뒤 시드 → 실행 → 평가 → 진화",
  "subtitle": "인터뷰 → 시드 → 실행 → 평가 → 진화",
  "placeholders": {
    "idle": "이어서 요청하거나 위에서 다음 단계를 고르세요…",
    "initial": "무엇을 만들까요? 목표를 적고 Enter로 인터뷰를 시작하세요…",
    "answering": "직접 답하려면 여기에 적고 Enter…"
  },
  "guidance": {
    "start": "무엇을 만들까요? 목표를 아래에 적으면 질문을 주고받으며 요구를 또렷하게 만듭니다.",
    "next": "{phase} 단계가 끝났습니다. 다음 단계를 고르거나, 아래에 적어 같은 대화를 이어가세요.",
    "running": "{phase} 진행 중 · 질문이 오면 여기에 표시됩니다"
  },
  "prerequisites": {
    "mode": "all",
    "report": "first",
    "probes": [
      { "kind": "plugin", "prefix": "ouroboros@", "install": true,
        "missing": "Ouroboros 플러그인이 설치되어 있지 않습니다",
        "hint": "설치는 Claude CLI의 플러그인 명령으로 진행합니다. 터미널 실행 창이 열리고 명령이 채워집니다. Enter는 직접 누르세요." },
      { "kind": "executable", "name": "uvx", "install": false,
        "missing": "uvx가 필요합니다 (Ouroboros MCP 서버 실행용)",
        "hint": "터미널에서 `brew install uv` 또는 https://docs.astral.sh/uv 의 안내로 설치한 뒤 다시 확인하세요." }
    ]
  },
  "install": {
    "command": "claude plugin marketplace add Q00/ouroboros && claude plugin install ouroboros@ouroboros",
    "paneTitle": "Ouroboros 설치"
  },
  "phases": [
    { "id": "goal",      "title": "목표",   "order": 0 },
    { "id": "interview", "title": "인터뷰", "order": 1 },
    { "id": "seed",      "title": "시드",   "order": 2 },
    { "id": "run",       "title": "실행",   "order": 3 },
    { "id": "evaluate",  "title": "평가",   "order": 4 },
    { "id": "evolve",    "title": "진화",   "order": 5 }
  ],
  "groups": [
    { "id": "flow", "title": "흐름",
      "actions": ["interview", "auto", "seed", "run", "evaluate", "evolve", "ralph", "status", "unstuck"] }
  ],
  "actions": [
    { "id": "interview", "title": "인터뷰 시작", "help": "소크라테스식 질문으로 요구를 또렷하게 만듭니다 (모호도 0.2 이하까지)", "prompt": "/ouroboros:interview {text}", "takesText": true,  "foldText": "trimOnly", "requiresText": true, "phase": "interview", "icon": "questionmark.bubble" },
    { "id": "auto",      "title": "자동 진행",   "help": "목표에서 시드 생성과 실행까지 한 번에 진행합니다",                    "prompt": "/ouroboros:auto {text}",      "takesText": true,  "foldText": "trimOnly", "requiresText": true, "phase": "interview", "icon": "wand.and.stars" },
    { "id": "seed",      "title": "시드 생성",   "help": "인터뷰 결과를 불변 명세(시드)로 굳힙니다",                           "prompt": "/ouroboros:seed",             "takesText": false, "phase": "seed",      "icon": "leaf" },
    { "id": "run",       "title": "실행",        "help": "시드를 Double Diamond 흐름으로 실행합니다",                          "prompt": "/ouroboros:run",              "takesText": false, "phase": "run",       "icon": "play.fill" },
    { "id": "evaluate",  "title": "평가",        "help": "기계 · 의미 · 합의 3단계로 결과를 검증합니다",                       "prompt": "/ouroboros:evaluate",         "takesText": false, "phase": "evaluate",  "icon": "checkmark.seal" },
    { "id": "evolve",    "title": "진화",        "help": "평가를 반영해 다음 세대 시드로 수렴할 때까지 반복합니다",             "prompt": "/ouroboros:evolve",           "takesText": false, "phase": "evolve",    "icon": "arrow.triangle.2.circlepath" },
    { "id": "ralph",     "title": "랄프 루프",   "help": "수렴할 때까지 진화 단계를 계속 돌립니다",                            "prompt": "/ouroboros:ralph",            "takesText": false, "phase": "evolve",    "icon": "infinity" },
    { "id": "status",    "title": "상태",        "help": "세션 상태와 목표 이탈(drift)을 확인합니다",                          "prompt": "/ouroboros:status",           "takesText": false, "icon": "gauge.with.dots.needle.33percent" },
    { "id": "unstuck",   "title": "막힘 풀기",   "help": "다섯 가지 관점으로 막힌 지점을 다시 봅니다",                         "prompt": "/ouroboros:unstuck {text}",   "takesText": true,  "foldText": "trimOnly", "icon": "lightbulb" }
  ],
  "aliases": [
    { "name": "pm",          "phase": "interview" },
    { "name": "socratic",    "phase": "interview" },
    { "name": "crystallize", "phase": "seed" },
    { "name": "execute",     "phase": "run" },
    { "name": "eval",        "phase": "evaluate" },
    { "name": "qa",          "phase": "evaluate" }
  ],
  "recognition": { "prefixes": ["/ouroboros:", "ooo "], "lowercase": true },
  "rules": {
    "start": { "kind": "actions", "phase": "goal", "actions": ["interview", "auto"], "resetTitle": "새 목표" },
    "phase": { "kind": "lastRecognisedAction", "default": "goal" },
    "next": {
      "kind": "byPhase",
      "map": {
        "goal":      [],
        "interview": ["seed", "status", "unstuck"],
        "seed":      ["run", "evaluate", "status"],
        "run":       ["evaluate", "evolve", "status", "unstuck"],
        "evaluate":  ["evolve", "run", "status", "unstuck"],
        "evolve":    ["ralph", "evaluate", "status", "unstuck"]
      }
    },
    "enter": { "kind": "rewriteBareDraftTo", "action": "interview", "phase": "goal" },
    "recommend": { "kind": "none" },
    "initialGroup": { "kind": "fixed", "group": "flow" }
  },
  "capabilities": [],
  "autoAllow": [
    { "tool": "ToolSearch" },
    { "server": "plugin_ouroboros_ouroboros", "tool": "ouroboros_interview" },
    { "server": "plugin_ouroboros_ouroboros", "tool": "ouroboros_pm_interview" },
    { "server": "plugin_ouroboros_ouroboros", "tool": "ouroboros_lateral_think" },
    { "server": "plugin_ouroboros_ouroboros", "tool": "ouroboros_generate_seed" },
    { "server": "plugin_ouroboros_ouroboros", "tool": "ouroboros_brownfield" },
    { "server": "plugin_ouroboros_ouroboros", "tool": "ouroboros_session_status" },
    { "server": "plugin_ouroboros_ouroboros", "tool": "ouroboros_job_status" },
    { "server": "plugin_ouroboros_ouroboros", "tool": "ouroboros_job_wait" },
    { "server": "plugin_ouroboros_ouroboros", "tool": "ouroboros_job_result" },
    { "server": "plugin_ouroboros_ouroboros", "tool": "ouroboros_query_events" },
    { "server": "plugin_ouroboros_ouroboros", "tool": "ouroboros_query_projection" },
    { "server": "plugin_ouroboros_ouroboros", "tool": "ouroboros_lineage_status" },
    { "server": "plugin_ouroboros_ouroboros", "tool": "ouroboros_measure_drift" },
    { "server": "plugin_ouroboros_ouroboros", "tool": "ouroboros_ac_dashboard" },
    { "server": "plugin_ouroboros_ouroboros", "tool": "ouroboros_ac_tree_hud" },
    { "server": "plugin_ouroboros_ouroboros", "tool": "ouroboros_session_signal_targets" }
  ],
  "presentation": { "icon": "point.3.connected.trianglepath.dotted", "tint": "accent" }
}
```

`autoAllow`는 `ToolSearch` + 상태 도구 **16개** = 17개이고, 1.9의 한도 32 안이다(`OuroborosFlow.swift:131-136`·`139-143`). 소속 규칙도 만족한다: 유일한 `plugin` probe의 `prefix`가 `ouroboros@`이므로 `P = "ouroboros"`이고 `plugin_ouroboros_ouroboros`는 `plugin_ouroboros_`로 시작한다. `ToolSearch`는 번들이라 허용된다.

> **수치 정정.** 시드와 소유자 메모는 "자동 허용 15개 정확 이름"이라고 적고 있으나, `OuroborosFlow.stateTools`를 실제로 세면 16개다(`ouroboros_interview`, `ouroboros_pm_interview`, `ouroboros_lateral_think`, `ouroboros_generate_seed`, `ouroboros_brownfield`, `ouroboros_session_status`, `ouroboros_job_status`, `ouroboros_job_wait`, `ouroboros_job_result`, `ouroboros_query_events`, `ouroboros_query_projection`, `ouroboros_lineage_status`, `ouroboros_measure_drift`, `ouroboros_ac_dashboard`, `ouroboros_ac_tree_hud`, `ouroboros_session_signal_targets`). **동등성의 기준은 코드이지 시드가 아니다** — 테스트는 이 16개 이름을 그대로 단언한다. 오늘의 `OuroborosFlowTests`도 개수가 아니라 이름을 단언하므로 값은 바뀌지 않는다.

실행을 시작하는 도구(`ouroboros_execute_seed`, `ouroboros_start_*`, `ouroboros_ralph`, `ouroboros_evolve_step`, `ouroboros_evaluate`, 취소·되감기)는 목록에 없으므로 실행 창의 권한 모드를 그대로 따른다.

`E_PROMPT_RECOGNITION` 확인: 행동 9개 모두 `{text}`를 지운 프롬프트가 `/ouroboros:<id>`이고, `recognition`이 `/ouroboros:` 접두사를 떼면 이름이 곧 `id`다. `match`가 필요 없다.

### 1.14 Paperthin 번들 매니페스트 (전문)

```json
{
  "schema": 1,
  "id": "paperthin",
  "name": "Paperthin",
  "summary": "덜어내는 작은 스킬들을 지도에서 골라 실행",
  "subtitle": "더하지 말고 덜어내기 · depth · breadth · coil · mesh",
  "placeholders": {
    "idle": "대상(파일 경로·지시)을 적고 위에서 스킬을 고르세요 · Enter는 그대로 요청합니다…",
    "answering": "직접 답하려면 여기에 적고 Enter…"
  },
  "guidance": {
    "next": "대상(파일 경로나 지시)을 아래에 적고 스킬을 누르세요. 비워 두면 스킬만 보냅니다.",
    "running": "실행 중 · 고른 스킬은 다음 요청으로 대기합니다"
  },
  "prerequisites": {
    "mode": "any",
    "report": "first",
    "probes": [
      { "kind": "skill",  "name": "re0",       "scopes": ["user", "workspace"], "missing": "Paperthin 스킬이 설치되어 있지 않습니다",
        "hint": "터미널 실행 창을 열어 다음 명령을 넣습니다(Claude Code의 전역 스킬 폴더에 연결): npx skills@latest add LilMGenius/paperthin --global --agent claude-code" },
      { "kind": "skill",  "name": "nba",       "scopes": ["user", "workspace"], "missing": "Paperthin 스킬이 설치되어 있지 않습니다" },
      { "kind": "skill",  "name": "re0-loop",  "scopes": ["user", "workspace"], "missing": "Paperthin 스킬이 설치되어 있지 않습니다" },
      { "kind": "plugin", "prefix": "paperthin@",                               "missing": "Paperthin 스킬이 설치되어 있지 않습니다" }
    ]
  },
  "install": {
    "command": "npx skills@latest add LilMGenius/paperthin --global --agent claude-code",
    "paneTitle": "Paperthin 설치"
  },
  "phases": [],
  "groups": [
    { "id": "depth",   "title": "depth",   "axis": "하나 · 지금",  "question": "이 하나가 깨끗하고 참인가?",
      "actions": ["re0","readchk","aim","modelchk","hate","macrothink","feynman","autobahn","reorder","detool","dedash","debloat","shower","factchk","mandela","sip","re0-git","re0-release","re0-merge"] },
    { "id": "breadth", "title": "breadth", "axis": "여럿 · 지금",  "question": "하나의 진실이 모든 곳에서 일관적인가?",
      "actions": ["ssotize","re0-upgrade"] },
    { "id": "coil",    "title": "coil",    "axis": "하나 · 반복",  "question": "각 패스가 다음 패스를 가르쳤는가?",
      "actions": ["re0-plan","re0-loop","re0-memo","re0-work","catchup","nba"] },
    { "id": "mesh",    "title": "mesh",    "axis": "여러 시선",    "question": "집단이 진실로 수렴하는가?",
      "actions": ["prism"] }
  ],
  "actions": [
    { "id": "re0",          "title": "re0",          "glyph": "♻️", "scope": "아티팩트 하나",        "help": "drift된 아티팩트를 또 다른 패치가 아니라 깨끗한 v0로 다시 씁니다", "prompt": "/re0 {text}",          "takesText": true, "foldText": "oneLine" },
    { "id": "readchk",      "title": "readchk",      "glyph": "🧭", "scope": "지시 하나",            "help": "요청을 어떻게 읽었는지 확인하고, 실제로 남은 갈림길만 드러냅니다", "prompt": "/readchk {text}",      "takesText": true, "foldText": "oneLine", "flags": ["readOnly"] },
    { "id": "aim",          "title": "aim",          "glyph": "🏹", "scope": "넘겨받은 데이터 하나",  "help": "넘겨받은 데이터를 읽고, 물어보는 대신 확인할 의도를 먼저 제안합니다", "prompt": "/aim {text}",          "takesText": true, "foldText": "oneLine", "flags": ["readOnly"] },
    { "id": "modelchk",     "title": "modelchk",     "glyph": "📏", "scope": "작업 하나",            "help": "충분한 가장 싼 tier와 reasoning effort를 고릅니다", "prompt": "/modelchk {text}",     "takesText": true, "foldText": "oneLine", "flags": ["readOnly"] },
    { "id": "hate",         "title": "hate",         "glyph": "😈", "scope": "계획 하나",            "help": "친절하기를 거부합니다. 계획을 죽일 수 있는 반론 하나와 가장 싼 테스트를 냅니다", "prompt": "/hate {text}",         "takesText": true, "foldText": "oneLine", "flags": ["userInvoked"] },
    { "id": "macrothink",   "title": "macrothink",   "glyph": "🧠", "scope": "방향 하나",            "help": "bait를 걷어내고 새 읽기를 펼친 뒤 divergence를 먼저 보고합니다", "prompt": "/macrothink {text}",   "takesText": true, "foldText": "oneLine", "flags": ["userInvoked", "readOnly"] },
    { "id": "feynman",      "title": "feynman",      "glyph": "🧐", "scope": "결정 하나",            "help": "방금 내린 결정을 설명할 수 있을 때까지 밀어붙이고, 안 되면 그 빈틈을 드러냅니다", "prompt": "/feynman {text}",      "takesText": true, "foldText": "oneLine", "flags": ["userInvoked", "readOnly"] },
    { "id": "autobahn",     "title": "autobahn",     "glyph": "🛣️", "scope": "작업 하나",            "help": "안전하지 않은 스코프를 앞에서 도려내고, 안전한 나머지는 전력으로 실행한 뒤 descope를 기록합니다", "prompt": "/autobahn {text}",     "takesText": true, "foldText": "oneLine" },
    { "id": "reorder",      "title": "reorder",      "glyph": "🔃", "scope": "목록 하나",            "help": "drift된 목록을 하나의 명시된 원칙 아래 논리적 순서로 다시 맞춥니다. 항목만 옮기고, 표현은 바꾸지 않습니다", "prompt": "/reorder {text}",      "takesText": true, "foldText": "oneLine", "flags": ["userInvoked"] },
    { "id": "detool",       "title": "detool",       "glyph": "🧰", "scope": "durable 아티팩트 하나", "help": "우연히 섞인 도구 이름을 그것이 뜻한 메커니즘으로 바꿉니다", "prompt": "/detool {text}",       "takesText": true, "foldText": "oneLine" },
    { "id": "dedash",       "title": "dedash",       "glyph": "✂️", "scope": "내 문장",              "help": "em dash와 비슷한 tell을 지우고, 각 위치에 맞는 문장부호를 고릅니다", "prompt": "/dedash {text}",       "takesText": true, "foldText": "oneLine", "flags": ["userInvoked"] },
    { "id": "debloat",      "title": "debloat",      "glyph": "🗜️", "scope": "아티팩트 하나",        "help": "bloat된 아티팩트를 load-bearing한 밀도까지 압축합니다. 단어는 잘라내되, 규칙은 절대 잘라내지 않습니다", "prompt": "/debloat {text}",      "takesText": true, "foldText": "oneLine", "flags": ["userInvoked"] },
    { "id": "shower",       "title": "shower",       "glyph": "🚿", "scope": "아티팩트 하나",        "help": "맥락 없는 새 눈으로 차갑게 읽습니다. 이것이 혼자서도 서는가?", "prompt": "/shower {text}",       "takesText": true, "foldText": "oneLine", "flags": ["readOnly"] },
    { "id": "factchk",      "title": "factchk",      "glyph": "🔬", "scope": "클레임 하나",          "help": "주장된 것을 양방향으로 소스에 대조합니다. 말도 안 되는 것이 팩트일 수 있고, 당연한 것이 거짓일 수 있는가?", "prompt": "/factchk {text}",      "takesText": true, "foldText": "oneLine" },
    { "id": "mandela",      "title": "mandela",      "glyph": "🧪", "scope": "eval 하나",            "help": "leakage가 있는지 audit합니다. 외부 ground truth가 실제로 들어오는가?", "prompt": "/mandela {text}",      "takesText": true, "foldText": "oneLine", "flags": ["readOnly"] },
    { "id": "sip",          "title": "sip",          "glyph": "🥄", "scope": "내 아웃풋",            "help": "변경 뒤마다 레포 자체의 clean-and-true 체크로 아웃풋을 맛봅니다", "prompt": "/sip {text}",          "takesText": true, "foldText": "oneLine" },
    { "id": "re0-git",      "title": "re0-git",      "glyph": "🧾", "scope": "커밋 하나",            "help": "완료된 커밋 메시지를 다시 써서 `git log`만으로 handoff가 되게 합니다", "prompt": "/re0-git {text}",      "takesText": true, "foldText": "oneLine", "flags": ["userInvoked"] },
    { "id": "re0-release",  "title": "re0-release",  "glyph": "🚀", "scope": "릴리스 하나",          "help": "shipping·releasing 체크리스트를 실행하고, 확인 후 태그·퍼블리시합니다", "prompt": "/re0-release {text}",  "takesText": true, "foldText": "oneLine", "flags": ["userInvoked"] },
    { "id": "re0-merge",    "title": "re0-merge",    "glyph": "🤝", "scope": "기여 하나",            "help": "기여를 리뷰하고 반영합니다: gate를 통과시키고, 작성자 크레딧을 유지하고, 닫기 전에 승인하고, 변경 사항을 설명합니다", "prompt": "/re0-merge {text}",    "takesText": true, "foldText": "oneLine", "flags": ["userInvoked"] },
    { "id": "ssotize",      "title": "ssotize",      "glyph": "🧲", "scope": "팩트 하나, 여러 위치",  "help": "흩어진 곳을 감사한 뒤 한 집으로 모아 나머지가 그곳을 가리키게 합니다", "prompt": "/ssotize {text}",      "takesText": true, "foldText": "oneLine" },
    { "id": "re0-upgrade",  "title": "re0-upgrade",  "glyph": "🧰", "scope": "내 스킬 설치",         "help": "한 번에 현재 전체 카탈로그로 올립니다: 이름 바뀐 건 정리, 새 건 추가, 전부 먼저 확인", "prompt": "/re0-upgrade {text}",  "takesText": true, "foldText": "oneLine", "flags": ["userInvoked"] },
    { "id": "re0-plan",     "title": "re0-plan",     "glyph": "🗂️", "scope": "새 사이클 하나",       "help": "re0-loop의 첫 turn 전에 새 iteration 폴더를 열고 DESIGN/WORKFLOW/EVIDENCE를 씁니다", "prompt": "/re0-plan {text}",     "takesText": true, "foldText": "oneLine", "flags": ["userInvoked"] },
    { "id": "re0-loop",     "title": "re0-loop",     "glyph": "🌀", "scope": "전체 루프",            "help": "build → QA → re0-memo → re0-work 루프를 돌려 배움이 코드가 아니라 축적되게 합니다", "prompt": "/re0-loop {text}",     "takesText": true, "foldText": "oneLine" },
    { "id": "re0-memo",     "title": "re0-memo",     "glyph": "🧭", "scope": "완료된 사이클 하나",    "help": "끝났거나 실패한 사이클에서 교훈과 anti-pattern을 뽑아냅니다", "prompt": "/re0-memo {text}",     "takesText": true, "foldText": "oneLine" },
    { "id": "re0-work",     "title": "re0-work",     "glyph": "🧱", "scope": "재시작 하나",          "help": "재사용할 자격을 얻은 교훈만 남기고 v0에서 다시 시작합니다", "prompt": "/re0-work {text}",     "takesText": true, "foldText": "oneLine" },
    { "id": "catchup",      "title": "catchup",      "glyph": "🗺️", "scope": "재진입 하나",          "help": "실시간 state에서 잃어버린 context를 재구성합니다: 누구에게 필요한지, 무엇이 바뀌었는지, 새 단어가 무엇을 뜻하는지", "prompt": "/catchup {text}",      "takesText": true, "foldText": "oneLine", "flags": ["readOnly"] },
    { "id": "nba",          "title": "nba",          "glyph": "🎯", "scope": "현재 사이클",          "help": "살아 있는 사이클 state를 읽고 메뉴가 아니라 단 하나의 다음 최선 행동을 돌려줍니다", "prompt": "/nba {text}",          "takesText": true, "foldText": "oneLine", "flags": ["readOnly"] },
    { "id": "prism",        "title": "prism",        "glyph": "🔺", "scope": "아티팩트 하나",        "help": "아티팩트 하나를 독립적인 렌즈들로 쪼갠 뒤, 충돌하는 지점과 그것을 푸는 질문을 돌려줍니다", "prompt": "/prism {text}",        "takesText": true, "foldText": "oneLine", "flags": ["userInvoked", "readOnly"] }
  ],
  "aliases": [],
  "recognition": { "prefixes": ["/"], "lowercase": false },
  "rules": {
    "start": { "kind": "none" },
    "phase": { "kind": "none" },
    "next": { "kind": "byGroup" },
    "enter": { "kind": "verbatim" },
    "recommend": {
      "kind": "capability", "capability": "paperthin.casebook", "group": "coil",
      "map": { "absent": "re0-plan", "open": "re0-loop", "complete": "re0-work" }
    },
    "initialGroup": {
      "kind": "capabilityState", "capability": "paperthin.casebook",
      "map": { "absent": "depth", "open": "coil", "complete": "coil" }
    }
  },
  "capabilities": ["paperthin.casebook"],
  "autoAllow": [],
  "presentation": { "icon": "square.grid.2x2", "tint": "accent" }
}
```

행동 28개, 그룹별 19/2/6/1, `userInvoked` 12개(`hate`, `macrothink`, `feynman`, `reorder`, `dedash`, `debloat`, `re0-git`, `re0-release`, `re0-merge`, `re0-upgrade`, `re0-plan`, `prism`) — `PaperthinCatalogTests.swift:7-14`가 단언하는 값과 같다. 자동 허용이 비어 있으므로 파일 수정·명령 실행은 실행 창의 권한 모드를 따른다.

`placeholders`에서 `running`이 빠졌다. 오늘 그 문구는 입력창 placeholder가 아니라 패널의 맨 아랫줄이므로 `guidance.running`으로 옮겼고, 실행 중 Paperthin 실행 창의 placeholder는 오늘처럼 앱 공통 문구가 된다(`SessionPaneView.swift:75`). 첫 probe의 `hint`는 오늘 설치 안내 줄(`PaperthinPanel.swift:128`)을 그대로 옮긴 것이며, "명령을 실행합니다" 한 마디만 1.5의 결정에 맞춰 "명령을 넣습니다 …"로 바뀐다.

`E_PROMPT_RECOGNITION` 확인: 28개 모두 `{text}`를 지운 프롬프트가 `/<id>`이고, `recognition` 접두사 `/`를 떼면 이름이 곧 `id`다. `match`가 필요 없다. 모든 `glyph`는 이모지 표현을 갖는 grapheme cluster 하나다(`♻️`·`🛣️`·`✂️`·`🗜️`·`🗂️`처럼 VS16이 붙은 것들도 클러스터 하나로 센다).

---

## 2. 검증

디코딩은 한 번에 끝나지 않고 **⓪ 사전 스캔 → ① 크기 → ② JSON 파싱 → ③ 구조·타입 → ④ 참조 무결성** 순으로 진행하며, 처음 실패한 곳에서 멈추고 코드 하나와 한국어 한 줄을 돌려준다. 오류는 등록 화면·워크스페이스 확인 카드·설정 목록에 **그대로** 보인다.

**① 크기**는 파일 바이트 수만 본다(`E_TOO_LARGE`). 이것이 제일 먼저다.

**⓪ 사전 스캔**은 크기 검사 직후, `JSONSerialization`에 바이트를 넘기기 **전에** 원시 바이트를 한 번 훑는 재귀 없는 스캐너다. 문자열 리터럴 안팎을 구별하고 `\"` 이스케이프를 건너뛰며 다섯 가지를 본다.

1. **중첩 깊이** — 문자열 밖의 `[`·`{`·`]`·`}`를 세어 최대 깊이를 구한다. 8을 넘으면 `E_TOO_DEEP`이고 파서에는 넘기지 않는다.
2. **중복 키** — 같은 객체 안에 같은 키가 두 번 나오면 `E_DUPLICATE_KEY`.
3. **`schema`가 첫 키인가** — 최상위 객체의 첫 키가 `schema`가 아니면 `E_SCHEMA_NOT_FIRST`.
4. **키 이름의 모양** — 키 리터럴에 역슬래시가 하나라도 있으면 `E_KEY_ESCAPE`. 그리고 키는 값 문자열과 **똑같이** 1.11의 금지 문자 검사(`E_CONTROL_CHAR`)와 길이 검사(64자, `E_STRING_LENGTH`)를 받는다.
5. **최상위 뒤의 잔여물** — 최상위 컨테이너가 닫힌 뒤에는 공백만 올 수 있다. 값이 하나 더 있으면 `E_NOT_JSON`이다(파일 하나에 문서 둘).

다섯 다 파싱 뒤에는 알 수 없거나(JSON 객체는 순서도 중복도 보존하지 않고, 이스케이프도 남지 않는다) 우리 한도가 아닌 파서의 한도로 판정된다. 사전 스캔이 그 다섯 가지가 사는 유일한 자리다.

> **왜 키의 이스케이프를 아예 거절하는가.** `"autoAllow"`와 `"autoAllow"`는 `JSONSerialization`에게 **한 키**이고(먼저 나온 것이 이긴다) 사람과 사전 스캔에게는 **다른 두 문자열**이다. 4.4는 승인 카드가 파일 내용 전부를 보여 주기로 했으므로, 그 한 줄로 카드의 원본 JSON 구역이 `"autoAllow":[]`라고 읽히는 동안 엔진이 `Bash`·`Write`를 허용하는 파일을 쓸 수 있다 — 실측으로 확인된 입력이다. 이스케이프를 **풀어서** 비교하는 대신 **거절**하는 이유는 1.1이 모든 키 이름을 영문 식별자로 고정했기 때문이다: 정상적인 매니페스트에 이스케이프가 필요한 키는 없다.

> **왜 깊이를 앞에서 재는가 — 그리고 무엇은 사실이 아닌가.** "Foundation의 JSON 파서는 재귀 하강이라 깊은 입력에서 스택을 넘기고, 스택 오버플로는 Swift에서 잡을 수 없다"는 흔한 주장은 **이 툴체인에서 사실이 아니다**. 실측(Swift 6.4 / macOS CLT): `[`가 131 072개인 256 KB 입력에 대해 `JSONSerialization.jsonObject(with:)`는 프로세스를 죽이지 않고 `NSCocoaErrorDomain 3840, "Too many nested arrays or dictionaries"`를 **던진다**. 배열 중첩을 이분 탐색하면 513단까지 받고 그 위는 거부한다. 즉 중첩 폭탄으로 앱이 죽지는 않는다.
>
> 그래도 사전 스캔을 두는 이유는 셋이다. ① 앞에서 재지 않으면 우리가 선언한 한도 8이 실제로는 파서의 한도 513이 되고, 사용자가 받는 코드는 `E_TOO_DEEP`이 아니라 `E_NOT_JSON`이라 이유를 알 수 없다. ② 문서화되지 않은 파서 상수에 안전성을 의존하게 된다. ③ 중복 키(`E_DUPLICATE_KEY`)와 `schema` 위치(`E_SCHEMA_NOT_FIRST`)를 잡는 곳이 어차피 필요한데, 같은 한 번의 훑기로 끝난다.

| 코드 | 한국어 메시지 |
|---|---|
| `E_TOO_LARGE` | 매니페스트 파일이 256 KB를 넘습니다. |
| `E_TOO_DEEP` | JSON 중첩이 너무 깊습니다 (최대 8단계). |
| `E_DUPLICATE_KEY` | 같은 항목이 두 번 적혀 있습니다: `<경로>`. |
| `E_SCHEMA_NOT_FIRST` | schema는 파일의 첫 항목이어야 합니다. |
| `E_KEY_ESCAPE` | 항목 이름에는 이스케이프를 쓸 수 없습니다: `<경로>`. |
| `E_NOT_JSON` | JSON 형식이 아닙니다. |
| `E_SCHEMA_MISSING` | schema 필드가 없습니다. |
| `E_SCHEMA_VERSION` | 이 앱은 schema 1만 읽습니다. |
| `E_UNKNOWN_FIELD` | 알 수 없는 항목이 있습니다: `<경로>`. |
| `E_MISSING_FIELD` | 필수 항목이 없습니다: `<경로>`. |
| `E_TYPE` | 항목의 형식이 올바르지 않습니다: `<경로>`. |
| `E_STRING_LENGTH` | 글자 수 한도를 넘었습니다: `<경로>`. |
| `E_CONTROL_CHAR` | 표시할 수 없는 제어 문자가 들어 있습니다: `<경로>`. |
| `E_RESERVED_SEPARATOR` | 이 항목에는 `·`를 쓸 수 없습니다: `<경로>`. |
| `E_ID_SHAPE` | id 형식이 올바르지 않습니다: `<값>`. |
| `E_RESERVED_ID` | `<값>`은 앱이 예약한 스타일 id입니다. |
| `E_RESERVED_NAME` | `<값>`은 앱이 예약한 스타일 이름입니다. |
| `E_DUPLICATE_ID` | 같은 id가 두 번 있습니다: `<경로>` → `<값>`. |
| `E_LIMIT` | 개수 한도를 넘었습니다: `<경로>` (최대 `<N>`개). |
| `E_PROMPT_PLACEHOLDER` | 프롬프트에는 `{text}` 하나만 쓸 수 있습니다: `<행동 id>`. |
| `E_TAKES_TEXT_MISMATCH` | takesText와 프롬프트의 `{text}` 유무가 다릅니다: `<행동 id>`. |
| `E_FOLD_TEXT` | 입력 글을 받지 않는 행동에는 foldText를 쓸 수 없습니다: `<행동 id>`. |
| `E_PROMPT_RECOGNITION` | 이 행동의 프롬프트는 인식 규칙으로 자기 이름이 되지 않습니다: `<행동 id>`. |
| `E_UNKNOWN_FLAG` | 알 수 없는 flag입니다: `<값>`. |
| `E_UNKNOWN_REFERENCE` | 없는 항목을 가리킵니다: `<경로>` → `<값>`. |
| `E_ALIAS_COLLISION` | 별칭 이름이 행동 id와 겹칩니다: `<값>`. |
| `E_UNKNOWN_RULE` | 알 수 없는 규칙 종류입니다: `<경로>` → `<값>`. |
| `E_RULE_INCOMPLETE` | 규칙이 모든 단계를 다루지 않습니다: `<빠진 단계 id>`. |
| `E_START_PHASE` | 시작 규칙이 없는 단계를 가리킵니다: `<값>`. |
| `E_PHASE_RULE_NONE` | 단계가 있는데 단계 규칙이 none입니다. |
| `E_ENTER_ACTION_TEXT` | Enter 규칙이 가리키는 행동은 입력 글을 받아야 합니다: `<행동 id>`. |
| `E_UNKNOWN_CAPABILITY` | 이 앱이 모르는 내장 기능입니다: `<값>`. |
| `E_CAPABILITY_UNDECLARED` | capabilities에 선언하지 않은 내장 기능을 씁니다: `<값>`. |
| `E_CAPABILITY_MAP` | 내장 기능의 상태 값이 모두 매핑되지 않았습니다: `<빠진 상태>`. |
| `E_UNKNOWN_TINT` | 앱 팔레트에 없는 색 이름입니다: `<값>`. |
| `E_UNKNOWN_ICON` | 앱이 제공하지 않는 아이콘 이름입니다: `<값>`. |
| `E_UNKNOWN_PROBE` | 알 수 없는 준비물 검사 종류입니다: `<값>`. |
| `E_PROBE_NAME_SHAPE` | 준비물 이름 형식이 올바르지 않습니다: `<값>`. |
| `E_SCOPES` | scopes는 user·workspace 중 1–2개여야 합니다: `<값>`. |
| `E_AUTOALLOW_SERVER` | 자동 허용에는 서버 이름이 필요합니다 (ToolSearch만 예외): `<도구>`. |
| `E_AUTOALLOW_SHAPE` | 자동 허용 이름에는 패턴을 쓸 수 없습니다: `<값>`. |
| `E_AUTOALLOW_FOREIGN_SERVER` | 자동 허용은 이 스타일이 요구하는 플러그인의 도구만 쓸 수 있습니다: `<와이어 이름>`. |
| `E_AUTOALLOW_TOOLSEARCH_BUNDLED` | ToolSearch는 내장 스타일만 자동 허용할 수 있습니다. |
| `E_AUTOALLOW_QUESTION` | AskUserQuestion은 자동 허용할 수 없습니다. |
| `E_AUTOALLOW_DUPLICATE` | 같은 도구가 두 번 있습니다: `<와이어 이름>`. |
| `E_PLACEHOLDER_INITIAL` | initial placeholder는 Enter 규칙이 rewriteBareDraftTo일 때만 쓸 수 있습니다. |
| `E_ID_COLLISION` | 이미 같은 id의 스타일이 있습니다: `<id>` (`<우선 출처>`). |

**메시지에 끼워 넣는 값은 그 자체가 공격자의 글이다.** `<경로>`·`<값>`·`<도구>`·`<와이어 이름>`은 **64자로 자르고**(넘치면 마지막 한 자리가 `…`이므로 화면에 나가는 길이는 정확히 64다), 1.11의 금지 문자를 U+FFFD로 바꾼 뒤에 쓴다. `E_UNKNOWN_FIELD`의 `<경로>`는 매니페스트의 **키 이름**으로 조립되므로 특히 그렇다 — 승인도 되기 전에 20만 자짜리 키가 설정 화면의 거부 목록에 그대로 그려질 수 있다. 키 이름은 값 문자열과 똑같이 다루므로(1.11) 그 키는 애초에 `E_STRING_LENGTH`로 거부되지만, 메시지의 한도는 그것과 무관하게 걸린다. 같은 "자른 뒤 `…`"는 1.8의 80자 한도에도 그대로 적용된다.

**아이콘은 닫힌 목록이므로 검증된다.** 1.10의 33개 밖이면 `E_UNKNOWN_ICON`으로 매니페스트 전체가 거부된다. 대체 심볼 규칙도, "이름은 맞는데 그려지지 않는다"는 상태도 없다. 승인 화면은 아이콘 이름을 **문자열로도 함께** 보여 준다 — 사용자가 본 것과 그려진 것이 다를 여지를 없앤다.

---

## 3. 출처 · 발견 · 우선순위

### 3.1 세 출처

| 출처 | 위치 | 발견 | 신뢰 |
|---|---|---|---|
| `bundled` | 앱 번들 안 (3.2) | 앱 시작 시 1회 | 사전 승인 |
| `user` | `<데이터 폴더>/styles/*.json` | 앱 시작 시 + 설정 화면을 열 때 | 등록 시 1회 승인 |
| `workspace` | `<워크스페이스>/.claude/mighty-styles/*.json` | 그 워크스페이스에 실행 창이 처음 생길 때 + 사용자가 새로고침할 때 | 첫 사용 전 1회 승인 (워크스페이스 경로별) |

`<데이터 폴더>`는 `AppStore`가 정하는 값이며 `--profile`을 그대로 존중한다(`AppStore.swift:146-148`). 즉 스모크 프로필은 사용자의 스타일을 절대 보지 않는다. 폴더는 0700, 복사된 파일은 0600으로 만든다.

**워크스페이스 위치를 `.claude/mighty-styles/`로 정한 이유 한 줄**: `.claude/skills`·`.claude/commands`는 CLI 자신의 이름 공간이라 여기에 JSON을 끼워 넣으면 슬래시 명령 스캐너(`SlashCommandCatalog.commands`, `SlashCommands.swift:139-160`)와 충돌하고, 전용 폴더라야 "저장소에서 발견됨" 목록이 디렉터리 나열 한 번으로 끝난다.

**원격 워크스페이스는 스캔하지 않는다.** `StyleSourceScanner.workspace(...)`는 `remote == nil`인 워크스페이스에 대해서만 호출한다. 근거: 적격성 규칙상 원격 워크스페이스의 실행 창은 어떤 스타일도 쓸 수 없는데(`AppStore+Ouroboros.swift:9-13`), 스캔하면 **맥 자신의 디스크에서 그 경로에 우연히 있는 파일**을 읽어 결코 실행될 수 없는 스타일로 등록하게 된다. 더 나아가 `applicable`·`runnable`은 `workspacePath: String?`이 아니라 `workspace: Workspace?`를 받고, `remote != nil`이면 번들·사용자 스타일만 돌려준다 — 경로 문자열만 쓰면 원격 호스트의 `/home/ubuntu/proj`와 맥의 같은 경로가 **같은 승인 키를 공유한다**(4.3).

**링크와 경계.** 워크스페이스 스캔은 `<워크스페이스>/.claude/mighty-styles`를 `resolvingSymlinksInPath`로 푼 뒤, 그 결과가 **같은 방식으로 푼 워크스페이스 경로 아래에 있을 때만** 진행한다. 아니면 그 출처는 파일 0개다. 폴더 자체가 링크이거나 상위 `.claude`가 링크이면 여기서 걸린다 — 항목 단위 검사만으로는 막지 못하는 경우다(git은 심볼릭 링크를 저장하고, 클론이 그것을 재현한다). 각 항목은 `URLResourceValues`로 `isSymbolicLink == false`, `isRegularFile == true`, **`linkCount == 1`**을 확인한다. 하드 링크는 일반 파일과 구별되지 않으므로 `linkCount`로만 걸린다.

**나열의 한도.** 폴더 나열은 **1024개 항목에서 멈춘 뒤** 이름순으로 정렬하고, 그중 확장자가 `.json`인 **32개까지** 읽는다. 32개 상한만 두고 전부 나열하면 항목이 백만 개인 폴더가 스캔을 멈춰 세운다. 스캔 전체는 워커에서 돌고 결과만 메인 액터로 올린다. 파일 하나가 실패해도 나머지는 계속 읽고, 실패는 이유와 함께 목록에 남는다.

**언제 다시 읽는가.** 스캔은 앱 시작 · 설정 화면을 열 때 · 그 워크스페이스에 실행 창이 처음 생길 때 · `[다시 스캔]`을 누를 때 일어난다. **파일 감시는 하지 않는다.** 디스크에서 파일이 바뀌어도 다음 스캔 전까지 상태는 그대로다. 그래서 "해시가 바뀌면 즉시 `pending`"이 아니라 **"다음 스캔에서 `pending`"**이다(4.2·9.3).

### 3.2 번들 매니페스트의 포장과 안전한 적재

파일은 `native/macos/Sources/MightyCore/Resources/Styles/{ouroboros,paperthin}.json`에 두고 `Package.swift`의 `MightyCore` 타깃에 `resources: [.copy("Resources/Styles")]`를 선언한다. SwiftPM이 `MightyClaude_MightyCore.bundle`을 빌드 산출물 옆에 만들고, `scripts/build-macos.sh:23-27`의 기존 루프가 `$BIN_PATH/*.bundle`을 `Contents/Resources/`로 그대로 복사한다 — Ghostty의 터미널 리소스가 이미 그렇게 앱 안으로 들어간다.

**`Bundle.module`은 쓰지 않는다.** SwiftPM이 만들어 주는 접근자는 번들을 못 찾으면 `fatalError`로 트랩하므로, 포장 실수가 사용자의 앱을 즉사시킨다. 대신 트랩 없는 탐색을 직접 쓴다.

```
BundledStyleSource.directories() -> [URL]
  후보 = [ Bundle.main.resourceURL, Bundle(for: BundledStyleMarker.self).resourceURL, Bundle.main.bundleURL ]
  각 후보 + "MightyClaude_MightyCore.bundle/Styles" 중 존재하는 디렉터리를 순서대로 돌려준다.
  하나도 없으면 빈 배열.
```
후보에 **`.app`을 담고 있는 폴더는 넣지 않는다.** 거기에 떨어진 `MightyClaude_MightyCore.bundle`은 `bundled` 출처로 읽히고, 그것은 사전 승인이며 예약 id·예약 이름·`ToolSearch` 자동 허용이 모두 허용되고 승인 카드가 뜨지 않는다는 뜻이다. 올바로 조립된 앱은 이 후보를 한 번도 쓰지 않으므로 잃을 것이 없다.

`BundledStyleMarker`는 이 목적만을 위한 **빈 `final class`**다 — `Bundle(for:)`가 `AnyClass`를 받으므로, MightyCore가 그 밖에는 값 타입만 쓰더라도 클래스 하나는 있어야 한다. `Bundle.main.resourceURL`이 `swift test`에서는 테스트 러너의 리소스 폴더를, 조립된 `.app`에서는 `Contents/Resources`를 가리키므로 양쪽 모두 첫 후보에서 잡힌다. 빈 배열이 돌아오면 번들 스타일이 0개가 되고 모든 실행 창이 일반 CLI로 떨어진다 — 크래시는 없다.

그 무해한 실패가 **출시되지 않도록** 두 겹의 관문을 둔다.
1. `StylesBundledTests`가 매니페스트 2개를 찾아 디코딩·검증하고 `id`가 `ouroboros`·`paperthin`임을 단언한다. `swift test`에서 잡힌다.
2. `scripts/build-macos.sh`가 `.app` 조립 직후 `Contents/Resources/MightyClaude_MightyCore.bundle/Styles/ouroboros.json`과 `paperthin.json`의 존재를 확인하고 없으면 0이 아닌 코드로 죽는다.

### 3.3 우선순위와 충돌

우선순위는 `bundled` > `user` > `workspace`다. 같은 `id`가 둘 이상에서 나오면 **우선순위가 높은 쪽만 등록되고 낮은 쪽은 거부된다**(`E_ID_COLLISION`) — 가려지는 것이 아니라 거부다. 거부된 파일은 설정의 스타일 목록에 회색으로, 경로와 `E_ID_COLLISION` 메시지와 함께 남아 사용자가 왜 안 보이는지 알 수 있다. 가림(shadowing)이 아닌 거부인 이유 한 줄: 가리면 저장소 파일이 사용자가 이미 신뢰한 이름을 물려받은 것처럼 보이는데, 승인은 **파일 내용 해시에 묶여 있지 다른 파일의 승인을 물려받지 않는다**.

같은 출처 안에서 두 파일이 같은 id를 가지면 **이름순으로 앞선 파일이 이기고 뒤의 파일이 거부된다**(결정적 순서).

### 3.4 스타일이 사라졌을 때, 그리고 같은 id의 다른 매니페스트가 왔을 때

`RunSession.mightyStyle`은 **형식만** 정규화된다: Claude 실행 창이 아니면 `nil`, 맞으면 `^[a-z0-9][a-z0-9-]{0,39}$`를 통과할 때만 값을 유지한다(`StateRepository.swift:196`의 자리를 대신한다). **등록된 id 집합으로 지우지 않는다** — 워크스페이스 매니페스트는 실행 창이 복원된 뒤에야 스캔되므로, id로 지우면 앱을 켤 때마다 저장소 스타일이 날아간다.

`StateRepository.normalize`는 선택 인자 `knownStyleIds: Set<String>? = nil`을 받는다. `nil`이면 형식만 본다. 테스트는 `["ouroboros", "paperthin"]`을 넘겨 오늘의 단언값(`["ouroboros", nil, nil]`)을 그대로 유지한다. **앱은 이 인자를 넘기지 않는다.**

**`RunSession`은 `mightyStyle`과 함께 `mightyStyleHash: String?`를 저장한다** — 그 실행 창이 마지막으로 그 스타일을 고르거나 승인한 시점의 매니페스트 해시다. `AppStore.guidedStyle(_:)`은 id로 찾은 뒤 **해시가 같을 때만** 그 스타일을 돌려준다. 다르면 `nil`(일반 CLI)이고, 선택기에 `"이 실행 창의 스타일이 바뀌었습니다 — 다시 고르세요"`가 뜬다. 사용자가 다시 고르면 새 해시가 저장된다.

> 근거: `mightyStyle`은 **이름이지 참조가 아니다**. 사용자가 `flow`라는 사용자 스타일을 등록해 몇 주 쓰다가 설정에서 지운다. 몇 달 뒤 `.claude/mighty-styles/flow.json`을 담은 저장소를 클론하고, 선택기가 `저장소에서 발견됨 · 확인 필요`로 보여 주는 그것을 한 번 승인한다. 그 순간 예전 실행 창이 **말없이 낯선 매니페스트를 입는다** — 행동도, Enter 규칙도, 자동 허용도, 그리고 1.10이 제목을 렌더 시점에 계산하므로 **과거 요청 블록의 제목까지** 맥과 폰 양쪽에서. 승인 직전 상태(레지스트리에 없어 `runnable`이 `nil`)는 이미 안전하다. 위험한 것은 승인 순간의 조용한 재결속이다.
>
> **번들 스타일에는 이 검사를 적용하지 않는다.** 번들 매니페스트의 해시는 앱 버전마다 바뀌므로 해시를 요구하면 업데이트마다 모든 Ouroboros 실행 창이 CLI로 떨어진다. `source == bundled`면 id만 본다 — 번들 id는 예약되어 있어 남이 주장할 수 없으므로(1.2), 재결속의 위험이 없다.

이 결속이 1.10의 레지스트리 제목과 만나는 지점은 1.10에 적어 두었다: 해시가 어긋난 실행 창은 스타일이 없으므로 **접두사를 잃지, 낯선 제목을 얻지 않는다**.

실제 해석은 읽을 때 한다: `guidedStyle(_:)`이 레지스트리에서 id를 찾지 못하면 `nil`을 돌려주고, 그 실행 창은 **일반 CLI로 보인다**(오늘 `MightyStyles.normalized`가 `nil`을 내는 것과 같은 결과). 나중에 같은 해시의 매니페스트가 다시 등록되면 스타일이 저절로 돌아온다.

---

## 4. 신뢰 모델

### 4.1 내용 해시

해시는 **파일 바이트 전체의 SHA-256**을 소문자 16진수로 적은 64자다. 파싱 결과가 아니라 바이트다 — 주석도 공백도 바뀌면 다시 묻는다. `StatusLineConfig.fingerprint`가 워크스페이스 명령에 대해 하는 일과 같은 역할이다(`AppStore+StatusLine.swift:39-48`).

### 4.2 상태

`preApproved`(번들, 레코드 없음) · `pending`(레코드 없음, 또는 해시가 다른 레코드) · `approved` · `revoked`.

- 해시가 바뀌면 **다음 스캔에서** `pending`이다(레코드는 옛 해시를 들고 있어 일치하지 않는다). 파일 감시는 없다(3.1).
- **승인은 바이트에, 거부는 자리에 묶인다.** 어떤 해시로든 한 번 `revoked`가 된 자리(`user`: `(source, path)`, `workspace`: `(source, workspacePath, path)`)는 **내용이 바뀌어도 계속 `revoked`**이고, 설정에서 사용자가 명시적으로 `[다시 허용]`을 누르기 전에는 카드조차 열리지 않는다.
  > 좁은 승인 / 넓은 거부. 반대로 하면 작성자가 1바이트씩 고쳐 영원히 다시 물을 수 있다 — 사용자가 가장 신중하게 내린 결정 하나를 겨냥한 프롬프트 피로 기계가 된다. 한편 승인이 넓으면 위험한 버전을 되살리는 커밋 하나가 프롬프트 없이 통과한다(4.3의 "자리당 하나").

### 4.3 승인 레코드의 저장 위치와 모양

**`<데이터 폴더>/style-trust/approvals.json`**(폴더 0700, 파일 0600). `<데이터 폴더>/styles/`가 **아니다**.

> 스캔 대상 폴더 안에 두면 세 가지가 한꺼번에 깨진다. ① `approvals.json`이 `*.json`에 걸려 매 스캔마다 매니페스트로 디코딩을 시도하고 실패해, 설정의 "거부된 파일" 목록에 **영구히** 회색 줄로 남는다 — 진짜 거부를 알리는 채널이 첫날부터 소음이 된다. ② 32개 파일 슬롯 중 하나를 먹는다. ③ `approvals`는 id 정규식 `^[a-z0-9][a-z0-9-]{0,39}$`를 통과하므로, 복사 파일 이름을 `<id>.json`으로 정하는 순간 `id: "approvals"`인 매니페스트 하나가 **신뢰 저장소를 덮어쓴다**.

```json
{ "version": 1,
  "records": [
    { "styleId": "acme", "source": "user",
      "path": "/Users/x/Library/Application Support/MightyClaude Native/styles/acme.json",
      "hash": "9f2c…", "state": "approved", "decidedAt": "2026-09-19T08:00:00Z" },
    { "styleId": "repo-flow", "source": "workspace",
      "workspacePath": "/Users/x/Work/repo",
      "path": "/Users/x/Work/repo/.claude/mighty-styles/repo-flow.json",
      "hash": "4ab1…", "state": "revoked", "decidedAt": "2026-09-19T09:00:00Z" }
  ] }
```

`workspace-state.json`에 넣지 않는 이유: 그 파일은 `save(_:)`가 통째로 덮어쓰고, 로그·그래프 예산(`StateRepository.swift:135-153`)에 따라 내용이 잘려 나가며, `restoring: true` 정규화가 모르는 항목을 버린다. 신뢰 기록이 기록 보존 예산의 희생양이 되어서는 안 된다.

레코드를 찾는 키는 출처마다 다르다.
- `user`: `(source, path, hash)` — 파일 경로와 내용이 모두 같아야 한다.
- `workspace`: `(source, workspacePath, path, hash)` — **같은 파일을 다른 클론에 두면 그 클론에서 다시 묻는다**. 워크스페이스 경로는 `Workspace.path`(심볼릭 링크 해소 후, `StateRepository.swift:50`)를 쓴다.

**승인은 자리당 하나만 살아 있다.** 새 해시가 승인되면 같은 자리 키(`user`: `(source, path)`, `workspace`: `(source, workspacePath, path)`)의 **이전 `approved` 레코드는 삭제된다**. 되돌아온 옛 바이트는 레코드가 없으므로 `pending`이고 다시 묻는다.
> v1(해시 H1)이 위험한 `autoAllow`를 갖고 있어 사용자가 승인했다가, v2(H2)에서 그 항목이 빠져 다시 승인했다고 하자. H1 레코드가 그대로 남아 있으면 **파일을 v1 바이트로 되돌리는 커밋 하나**가 프롬프트도 배지 변화도 없이 그 목록을 되살린다. `revoked` 레코드는 이 삭제 대상이 **아니다**(4.2의 자리 결속).

**읽기 실패는 닫는 쪽으로.** 파일이 없으면 레코드 0개로 시작한다. 파일이 **있는데** 읽히지 않거나 JSON이 깨졌거나 `version`이 1이 아니면, 신뢰 저장소는 **읽기 전용 잠금 상태**에 들어간다: 어떤 스타일도 `approved`·`revoked`로 판정되지 않고(전부 `pending`, 번들만 사용 가능), **`approve`·`revoke`·`forget`이 모두 실패하며 파일을 덮어쓰지 않는다**. 설정 화면이 `"신뢰 기록을 읽을 수 없습니다: <경로>"`를 띄우고, 사용자가 직접 지우거나 고쳐야 풀린다. 깨진 파일 하나를 조용히 새로 쓰면 사용자가 내린 모든 거부가 사라진다.

**권한 검사.** 읽을 때 `approvals.json`이 현재 uid 소유가 아니거나 group/other에 쓰기 비트가 있으면 같은 잠금 상태로 간다. 폴더에도 같은 검사를 한다. 근거: 스타일 파일은 스스로를 지킨다(해시가 검사된다). `approvals.json`은 **내용 자체가 권위인 유일한 파일**인데 검증이 하나도 없었다.

**쓰기는 원자적으로.** 같은 폴더의 임시 파일에 0600으로 쓰고 `rename(2)`으로 바꾼다. 쓰기 직전 파일의 `(mtime, size, inode)`가 `load()` 때와 다르면 다시 읽어 **병합한다**. `StyleTrustStore`는 `actor`지만 actor는 **한 프로세스 안에서만** 직렬화한다 — 같은 프로필의 두 번째 실행, 크래시 후 재실행, 기본 프로필의 스모크 러너가 서로의 기록을 통째로 지우지 않게 하려는 것이다. 같은 이유로 `load()`도 스탬프가 달라졌으면 다시 읽는다: 한 번 읽고 캐시해 버리면 두 번째 실행이 쓴 승인이 이 실행의 모든 스캔에 보이지 않는다.

병합의 키는 **상태에 따라 다르다.**
- `approved`: `(source, workspacePath, path)` — **자리뿐이다**. 같은 자리의 승인은 병합 뒤에도 하나만 남고, 더 최근 `decidedAt`이 이긴다. 해시를 키에 넣으면 위의 "자리당 하나"가 두 프로세스 창에서 깨진다: A가 H1 승인을 들고 있는 동안 B가 H2를 승인하고, A의 다음 쓰기가 둘을 다시 합쳐 버리면 파일을 H1 바이트로 되돌리는 커밋 하나가 프롬프트 없이 통과한다.
- 그 밖(`revoked`): `(source, workspacePath, path, state, hash)` — **거부는 하나도 버리지 않는다**(4.2의 자리 결속, 상한의 규칙과 같은 이유).

**다시 읽기도 같은 관문을 지난다.** 병합 직전의 재읽기는 처음 읽을 때와 똑같이 소유·권한을 확인하고, 그중 하나라도 어긋나거나 JSON이 깨졌거나 `version`이 1이 아니면 **잠금 상태로 가고 파일을 덮어쓰지 않는다**. 처음 읽기만 닫히고 재읽기가 열려 있으면, 로드 뒤에 깨진 파일이 이 프로세스의 메모리로 조용히 덮어써진다 — 그 파일에 든 거부 전부가 사라진다.

**상한에서 버리는 것은 `approved`뿐이다.** 레코드 수는 256개로 제한하고, 넘으면 `decidedAt`이 오래된 **`approved`** 레코드부터 버린다. `revoked`는 버리지 않는다. 남은 것이 전부 `revoked`라 버릴 것이 없으면 새 레코드를 쓰지 않고 `"신뢰 기록이 가득 찼습니다. 설정에서 오래된 항목을 지우세요."`로 실패한다. 거부를 버리는 것은 스스로 허용으로 바뀌는 일이다.

### 4.4 하나의 공개 규칙

**번들이 아닌 모든 출처는, 처음 쓰이기 전에 매니페스트 내용 전부를 보여 주고 한 번 확인받는다.** 시드의 QA 검토를 반영해 사용자 등록과 워크스페이스 발견을 하나의 규칙으로 좁힌 것이며, 소유자의 결정(출처별 차등 신뢰, 1회 승인, 해시 결속)과 충돌하지 않는다 — 차등은 *번들이냐 아니냐*에 남아 있고, 비번들 두 출처가 같은 화면을 쓰게 될 뿐이다.

**카드는 위험 순서로 그린다.** 스키마 순서가 아니다.

1. 출처 배지 · **절대 경로** · 해시 앞 12자.
2. **자동 허용 목록** — 조립된 와이어 이름(`mcp__plugin_x__tool_y`)으로 한 줄씩. **접히지 않는다.** 비어 있으면 "자동 허용 없음"이라고 분명히 쓴다. 1.9의 소속 규칙은 남의 서버를 막을 뿐 그 플러그인 안의 쓰기·실행 도구를 막지 못하므로, 이 구역이 맨 위에 와야 한다.
3. **설치 명령 원문**과 **"누르면 터미널 창에 채워지기만 하고 실행은 직접 Enter"** 안내(1.5).
4. **Enter 규칙.** `rewriteBareDraftTo`이면 `"이 실행 창의 첫 Enter는 <그 행동의 prompt 원문>의 {text} 자리에 들어갑니다"`를 **프롬프트 원문 그대로** 보여 준다. 사람 말 요약만으로는 보낼 바이트를 알 수 없다.
5. `name` · `id` · `summary` · `subtitle`.
6. 그룹 · 단계 · 별칭 · 인식 접두사.
7. 나머지 규칙 다섯 개를 사람 말로 푼 요약과, 원본 JSON 조각.
8. **행동 전부**: `title`, `help`, `scope`, flags, `takesText`, `requiresText`, `match`, 그리고 **프롬프트 템플릿 원문 그대로**. 100개면 100개 다. 접어 두되 "전부 펼치기"가 기본으로 보이게 한다.
9. 아이콘 이름 · 색 이름 · placeholder · guidance 문자열.

**`autoAllow`가 비어 있지 않으면** `[허용]`은 자동 허용 구역이 화면에 **실제로 보인 뒤에만** 활성화되고, 누르면 `"이 스타일은 도구 <N>개를 권한 창 없이 실행할 수 있게 됩니다"`를 그 목록과 함께 다시 확인한다.

**보여 준 바이트가 곧 쓰이는 바이트다.** 승인 카드는 `DiscoveredStyleFile.data`(**한 번의 읽기**)에서 만들고, 해시도 그 `data`에서 계산하고, `styles/`로의 복사도 **그 `data`를 쓴다** — 원본 파일을 다시 읽지 않는다. 카드가 떠 있는 동안 원본이 바뀌어도 사용자가 승인한 것은 읽은 그 바이트이며, 바뀐 파일은 다음 스캔에서 `pending`으로 다시 나타난다.
> 없으면: 사용자가 100개짜리 카드를 읽는 몇 분 동안 Dropbox 동기화·`git pull`·같이 사는 프로세스가 원본을 바꿔치기하고, `FileManager.copyItem`이 **새 바이트**를 복사하며, 해시를 복사본에서 계산하면 사용자가 본 적 없는 `autoAllow`가 곧장 `approved`가 된다.

등록·발견 이후의 모든 사용(행동 목록, 자동 허용 판정, 투영)도 그 `data`에서 디코딩된 `RegisteredStyle.manifest`만 쓴다. **디스크는 스캔 시점에만 읽는다.** 그 한 번의 읽기도 **메모리 매핑이 아니다**: 매핑된 `Data`는 파일의 살아 있는 창이라 카드가 떠 있는 동안 원본이 바뀌면 카드·해시·복사본이 함께 흔들리고, 원본을 잘라 내면 `SIGBUS`로 앱이 죽는다.

**복사는 사용자가 고른 파일에만 일어난다.** 승인 시 `styles/`로 바이트를 쓰는 것은 `user` 출처이면서 **아직 그 자리에 파일이 없을 때**뿐이다. 저장소에서 발견된 매니페스트를 승인하면서 복사하면, 다음 스캔에서 `user` 사본이 우선순위로 이기고 저장소 원본이 `E_ID_COLLISION`으로 거부되며, 승인 레코드는 `workspace` 자리에 적혔으므로 사본은 `pending`이다 — 사용자가 "허용"을 눌렀는데 그 스타일이 사라지고, 저장소를 지워도 공격자가 쓴 바이트가 앱의 데이터 폴더에 남는다.

시점:
- `user` — **등록할 때** 묻는다. 설정에서 파일을 고르면 먼저 검증하고, 카드를 띄우고, 사용자가 허용해야 `styles/`로 복사하고 레코드를 남긴다. 사용자가 그 폴더에 직접 떨어뜨린 파일은 목록에 `pending`으로 나타나고 같은 카드를 거쳐야 한다.
- `workspace` — **그 워크스페이스에서 처음 쓰려 할 때** 묻는다. 선택기에는 `저장소에서 발견됨 · 확인 필요 · 행동 <N> · 자동 허용 <M>` 배지와 함께 보이되 고를 수 없고, 고르려고 누르면 카드가 열린다. 배지에 두 숫자를 넣는 이유: 사용자가 카드를 열기 전에 규모를 알 수 있어야 한다.

### 4.5 승인 전 스타일은 고를 수 없다

선택 가능하되 무력(inert)한 상태는 **두지 않는다**. 미승인 스타일은 Mac의 선택기에 **보이지만 비활성**이며, 누르면 **승인 시트**가 열리고, `허용`을 눌러야 그때 비로소 실행 창의 `mightyStyle`(과 `mightyStyleHash`)이 바뀐다. 결정 이유 한 줄: 무력 상태를 두면 그래프·권한·폰 투영마다 "스타일이지만 아무것도 안 하는" 세 번째 경우를 하나하나 방어해야 하고, 시드는 바로 그 상태가 폰에 노출되는 것을 금지하고 있다 — 문 하나, 상태 둘.

**시트인 이유.** 4.4의 카드는 행동 100개와 각 400자 프롬프트를 원문으로 보여 주기로 했다. 그것을 입력창 위 크롬 줄에 펼칠 수는 없다. 패널에는 `"확인이 필요합니다 · [내용 보기]"` **한 줄짜리 띠**만 남고, `[내용 보기]`가 시트를 연다(6.1의 4번).

폰에서는:
- `MobileSettings.options.styles`에 **나타나지 않는다**.
- `MobileMighty.panel`이 만들어지지 않는다(실행 창은 `cli`로 보인다).
- `POST /m1/sessions/{id}/guided`는 **400 "알 수 없는 스타일입니다."** — 존재하지 않는 id와 구별되지 않는 답이다. 미승인 스타일이 거기 있다는 사실조차 알리지 않는다.
- `POST /m1/sessions/{id}/settings`의 `styleId` 거절 메시지도 **같은 문자열** `"알 수 없는 스타일입니다."`이며, 미승인·미등록 두 경우는 **같은 코드 경로에서 같은 즉시 응답**을 낸다 — 어느 쪽도 디스크를 읽지 않고, 준비물 검사나 재스캔을 유발하지 않는다. 그 차이는 타이밍으로 읽힌다.
- 승인은 **Mac에서만** 한다. 폰에는 승인 라우트가 없다.

### 4.6 매니페스트 문자열은 데이터다

- 화면에 나갈 때 **일반 텍스트**로 그린다(Mac은 `Text(verbatim:)`, 폰은 마크다운 렌더러를 태우지 않는 `<Text>`). 마크다운·HTML·링크로 해석하지 않는다.
- 어떤 문자열도 **자동으로 전송되지 않는다**. 프롬프트는 사용자가 버튼을 누르거나 Enter를 쳤을 때만 나간다.
- 어떤 문자열도 **자동으로 실행되지 않는다**. 설치 명령은 터미널 창에 `autoRun: false`로 채워질 뿐이고, 붙여넣기 직전 줄바꿈 검사를 한 번 더 받는다(1.5).
- 매니페스트는 **자기 플러그인의 도구를 정확한 이름으로만** 자동 허용할 수 있다(1.9의 소속 규칙). 패턴·글롭·정규식은 표현 불가능하고, 다른 서버·다른 플러그인·사용자가 설정한 MCP 서버도 표현 불가능하며, 승인은 해시에 묶이고, `AskUserQuestion`과 `ToolSearch`는 따로 막힌다. **그 플러그인 안에서 쓰기·실행 도구를 고르는 것은 여전히 매니페스트의 자유이므로, 승인 카드의 자동 허용 구역은 접히지 않고 맨 위에 온다(4.4).**
- 취소는 설정 → 마이티 스타일에서 언제든 한다. 취소 즉시 그 스타일을 쓰던 실행 창은 일반 CLI로 떨어진다.
- **자동 허용은 권한 요청 하나하나가 도착하는 순간** 그때의 승인 상태·해시·실행 창의 스타일로 판정한다. 취소·해시 변경·스타일 전환 뒤에 도착하는 요청은, 이미 돌고 있는 요청 안의 도구 호출이라도 자동 허용되지 않는다. 되돌릴 수 없는 것은 **이미 전달 중인 허용 한 건**뿐이다. 오늘 코드가 그렇다: `autoAllowOuroborosTool`이 권한 이벤트마다 불리고(`AppStore.swift:690`) 매번 `usesOuroboros(session)`를 다시 본다. "다음 요청부터"라고 적으면 요청 단위 캐시를 허락하게 되는데, 요청 하나는 몇 분에 걸쳐 수십 개의 권한 이벤트를 낸다.
- **승인·취소·재스캔은 스냅샷을 바꾸지 않으므로 `mobileObserve()`를 직접 불러 리비전을 올린다.** 그러지 않으면 롱 폴링 중인 폰이 취소를 보지 못한다 — 오늘 `refreshOuroborosPrerequisites`(`AppStore+Ouroboros.swift:41-43`)와 `refreshPaperthin`(`:93-95`)이 같은 이유로 손수 부르고 있고, 주석도 그 이유를 적어 두었다.

### 4.7 폰에서 누른 행동

승인·번들 스타일이라면 폰에서 누른 가이드 행동은 **Mac에서 누른 것과 똑같은 규칙**을 따른다. 프롬프트는 같은 함수가 만들고, 같은 submit 경로로 나가며, 자동 허용도 같은 판정을 받는다(`AppStore+MobileRemote.swift:461-468`의 구조를 유지). 예외는 `requiresText` 하나이며, 그것은 UI 힌트라 라우트가 보지 않는다(1.3.3).

---

## 5. MightyCore 엔진 API

새 파일은 `native/macos/Sources/MightyCore/Styles/` 아래 둔다. 순수 값 타입과 순수 함수가 기본이고, 파일 시스템을 만지는 것은 `StyleSourceScanner`·`StyleTrustStore`·`StyleCapabilities`·`StylePrerequisiteProbe`뿐이다.

이 장의 `public` 선언이 **레인 B(앱)와 레인 A(엔진)의 경계**다(11장). 레인 B는 여기 적힌 것만 부른다.

### 5.1 모델과 디코딩 — `StyleManifest.swift`

```swift
public struct StyleManifest: Sendable, Equatable {
    public var schema: Int
    public var id, name, summary, subtitle: String
    public var placeholders: StylePlaceholders
    public var guidance: StyleGuidance
    public var prerequisites: StylePrerequisites
    public var install: StyleInstall?
    public var phases: [StylePhase]
    public var groups: [StyleGroup]
    public var actions: [StyleAction]
    public var aliases: [StyleAlias]
    public var recognition: StyleRecognition
    public var rules: StyleRules
    public var capabilities: [String]
    public var autoAllow: [StyleAutoAllowEntry]
    public var presentation: StylePresentation
}

public struct StyleAction: Sendable, Equatable, Identifiable {
    public var id, title, help: String
    public var scope: String?
    public var prompt: String
    public var takesText, requiresText: Bool
    public var foldText: StyleFold?          // takesText가 true일 때만 값이 있다
    public var match: String?
    public var phase: String?
    public var flags: Set<StyleActionFlag>
    public var icon: StyleIcon?
    public var glyph: String?
    public var tint: StyleTint?
    public var requestTitle: String?
}
public enum StyleFold: String, Sendable { case trimOnly, oneLine }
public enum StyleActionFlag: String, Sendable { case userInvoked, readOnly }

public struct StylePhase: Sendable, Equatable, Identifiable { public var id, title: String; public var order: Int }
public struct StyleGroup: Sendable, Equatable, Identifiable {
    public var id, title: String; public var axis, question: String?; public var actions: [String] }
public struct StyleAlias: Sendable, Equatable { public var name, phase: String }
public struct StyleRecognition: Sendable, Equatable { public var prefixes: [String]; public var lowercase: Bool }
public struct StylePlaceholders: Sendable, Equatable { public var idle, answering: String; public var initial, running: String? }
public struct StyleGuidance: Sendable, Equatable { public var start, next, running: String? }
public struct StyleInstall: Sendable, Equatable { public var command, paneTitle: String }
public struct StylePresentation: Sendable, Equatable { public var icon: StyleIcon?; public var tint: StyleTint? }

public enum StyleProbe: Sendable, Equatable {
    case plugin(prefix: String, missing: String, hint: String?, install: Bool)
    case executable(name: String, missing: String, hint: String?, install: Bool)
    case skill(name: String, scopes: [StyleScope], missing: String, hint: String?, install: Bool)
}
public enum StyleScope: String, Sendable { case user, workspace }
public struct StylePrerequisites: Sendable, Equatable {
    public enum Mode: String, Sendable { case all, any }
    public enum Report: String, Sendable { case first, all }
    public var mode: Mode; public var report: Report; public var probes: [StyleProbe]
}
public struct StylePrerequisiteResult: Sendable, Equatable {
    public var ready: Bool
    public var missing: [String]         // report 규칙이 이미 적용된 목록 (1.5)
    public var hint: String?
    public var canInstall: Bool          // install이 있고 미충족 probe 중 install:true가 있다
}

public struct StyleAutoAllowEntry: Sendable, Equatable {
    public var server: String?; public var tool: String
    public var wireName: String }        // mcp__server__tool, 또는 tool

public enum StylePhaseRule: Sendable, Equatable { case none, lastRecognisedAction(default: String) }
public enum StyleStartRule: Sendable, Equatable {
    case none
    case actions(phase: String, actions: [String], resetTitle: String?) }
public enum StyleNextRule: Sendable, Equatable { case byPhase([String: [String]]), byGroup }
public enum StyleEnterRule: Sendable, Equatable { case verbatim, rewriteBareDraftTo(action: String, phase: String) }
public enum StyleRecommendRule: Sendable, Equatable {
    case none
    case capability(name: String, map: [String: String], group: String?) }
public enum StyleInitialGroupRule: Sendable, Equatable {
    case fixed(group: String)
    case capabilityState(name: String, map: [String: String]) }
public struct StyleRules: Sendable, Equatable {
    public var start: StyleStartRule; public var phase: StylePhaseRule; public var next: StyleNextRule
    public var enter: StyleEnterRule; public var recommend: StyleRecommendRule
    public var initialGroup: StyleInitialGroupRule }

public enum StyleTint: String, Sendable, CaseIterable {
    case accent, purple, teal, indigo, mint, orange, green, red, secondary }
public struct StyleIcon: Sendable, Equatable, RawRepresentable {
    public let rawValue: String                     // 1.10의 33개 중 하나
    public init?(rawValue: String)                  // 목록 밖이면 nil
    public static let all: [String]                 // 태그에서 고정되는 목록
    public static let requestDefault = StyleIcon(rawValue: "arrow.up.message")!
}

public enum StyleLimits {                 // 1.11의 숫자가 사는 유일한 곳
    public static let maximumBytes = 262_144
    public static let maximumActions = 100
    public static let maximumAutoAllow = 32
    public static let maximumDepth = 8
    public static let maximumFilesPerSource = 32
    public static let maximumDirectoryEntries = 1024
    public static let maximumApprovalRecords = 256
    public static let maximumCasebookChips = 6
    …
}

public struct StyleManifestError: Error, Sendable, Equatable {
    public var code: String                // "E_…"
    public var message: String             // 한국어 한 줄 (2장)
}

public enum StyleManifestDecoder {
    /// ⓪ 사전 스캔 → ① 크기 → ② JSON → ③ 구조 → ④ 참조 무결성. 모르는 키는 거부한다.
    public static func decode(_ data: Data, source: StyleSource) throws -> StyleManifest
}
public enum StyleManifestValidator {
    public static func validate(_ manifest: StyleManifest, source: StyleSource,
                                knownCapabilities: Set<String>) throws
}
```

**`StyleManifest`는 `Codable`이 아니다.** Swift가 합성해 주는 `Codable`은 모르는 키를 조용히 버리고, `JSONDecoder`는 중첩 깊이를 재지도, 중복 키를 보지도, `E_UNKNOWN_FIELD`가 요구하는 `<경로>` 문자열을 만들지도 못한다. 1.1과 2장이 약속한 것은 전부 **손수 쓴 패스**가 있어야 성립한다: `JSONSerialization`으로 읽은 `Any` 트리를 스키마 표와 대조하며 경로를 들고 걷는 워커. 깊이·중복 키·`schema` 위치는 그 앞의 바이트 사전 스캔에서 나온다(2장). `Codable`은 골든 직렬화용 `StylePanel`에만 쓴다.

`source`를 디코더·검증기가 받는 이유는 하나다: `ToolSearch` 자동 허용과 예약 id·예약 이름이 번들에서만 다르게 판정되기 때문이다(1.2·1.9).

### 5.2 출처와 레지스트리 — `StyleRegistry.swift`

```swift
public enum StyleSource: String, Codable, Sendable { case bundled, user, workspace
    public var precedence: Int { … }       // bundled 0 < user 1 < workspace 2
}
public struct DiscoveredStyleFile: Sendable { public var source: StyleSource
    public var url: URL; public var workspacePath: String?; public var hash: String; public var data: Data }
public struct RegisteredStyle: Sendable, Identifiable {
    public var manifest: StyleManifest; public var source: StyleSource
    public var path: String; public var workspacePath: String?
    public var hash: String; public var approval: StyleApprovalState
    public var id: String { manifest.id }
    public var evaluator: StyleEvaluator { get }     // 매니페스트당 한 번 만들어 캐시된다
}
public struct StyleRejection: Sendable { public var path: String; public var source: StyleSource
    public var error: StyleManifestError }

public enum StyleSourceScanner {           // 파일을 만지는 유일한 지점
    public static func bundled() -> [DiscoveredStyleFile]
    public static func user(directory: URL) -> [DiscoveredStyleFile]
    public static func workspace(path: String) -> [DiscoveredStyleFile]   // remote == nil 일 때만 호출
}

public struct StyleRegistry: Sendable {
    public static func make(files: [DiscoveredStyleFile],
                            approvals: [StyleApprovalRecord]) -> (styles: [RegisteredStyle], rejections: [StyleRejection])
    public func resolve(_ id: String) -> RegisteredStyle?
    /// 이 워크스페이스의 실행 창이 고를 수 있는 것: 번들·사용자 전부 + 이 워크스페이스의 것.
    /// workspace가 nil이거나 remote != nil이면 번들·사용자만.
    public func applicable(workspace: StyleWorkspaceRef?) -> [RegisteredStyle]
    /// 실제로 돌릴 수 있는 것: 승인·번들이고, hash가 주어지면 일치할 때만 (3.4).
    public func runnable(_ id: String, workspace: StyleWorkspaceRef?, hash: String?) -> RegisteredStyle?
    /// 요청 블록 제목 접두사 (1.10). runnable한 스타일만, 우선순위 → id 사전순으로 훑는다.
    public func requestTitle(forInput: String, workspace: StyleWorkspaceRef?) -> String?
    public func requestIcon(forInput: String, workspace: StyleWorkspaceRef?) -> StyleIcon?
    public func requestTint(forInput: String, workspace: StyleWorkspaceRef?) -> StyleTint
}
/// 경로 문자열만으로는 원격 호스트의 같은 경로와 구별되지 않는다(3.1).
public struct StyleWorkspaceRef: Sendable, Equatable, Hashable {
    public var path: String; public var isRemote: Bool
}
```

### 5.3 신뢰 저장소 — `StyleTrustStore.swift`

```swift
public struct StyleApprovalRecord: Codable, Sendable, Equatable { /* 4.3 */ }
public enum StyleApprovalState: String, Sendable { case preApproved, pending, approved, revoked }
public enum StyleTrustFailure: Error, Sendable, Equatable {
    case locked(path: String)      // 4.3의 잠금 상태
    case full                      // revoked만 남아 버릴 것이 없다
}

public actor StyleTrustStore {
    public init(directory: URL)            // <데이터 폴더>/style-trust
    public var isLocked: Bool { get }
    public func load() throws -> [StyleApprovalRecord]
    public func approve(_ style: RegisteredStyle) throws     // 같은 자리의 이전 approved를 지운다
    public func revoke(_ style: RegisteredStyle) throws      // 자리에 묶인다
    public func allowAgain(styleId: String, path: String, workspacePath: String?) throws  // revoked 해제
    public func forget(styleId: String, path: String) throws          // 목록에서 제거
    public nonisolated static func state(for file: DiscoveredStyleFile,
                                         in records: [StyleApprovalRecord]) -> StyleApprovalState
}
```

### 5.4 평가기 — `StyleEvaluator.swift` (전부 순수)

```swift
public struct StyleEvaluator: Sendable {
    public init(_ manifest: StyleManifest)
    public var manifest: StyleManifest { get }

    // 인식 — 1.4의 두 질문
    public func recognised(inPrompt: String) -> StyleRecognisedName?   // .action(id) | .alias(name, phase)
    public func namesSomething(inPrompt: String) -> Bool
    public func prompt(actionId: String, text: String) -> String?
    public func requestTitle(forInput: String) -> String?              // 접두사 (1.10)
    public func requestIcon(forInput: String) -> StyleIcon?
    public func requestTint(forInput: String) -> StyleTint

    // 상태
    public func currentPhase(prompts: [String]) -> StylePhase?
    public func currentPhase(session: RunSession) -> StylePhase?
    public func startActions(phase: StylePhase?) -> [StyleAction]      // start 규칙이 걸리면 비어 있지 않다
    public var resetTitle: String? { get }
    public func nextActions(phase: StylePhase?, group: StyleGroup?) -> [StyleAction]
    /// 6.1의 7번: 실행 중인 byPhase 스타일은 빈 배열, 그 밖에는 start 또는 next.
    public func visibleActions(phase: StylePhase?, group: StyleGroup?, running: Bool) -> [StyleAction]
    public func recommendedAction(capabilityStates: [String: String]) -> String?
    public var recommendGroupId: String? { get }                       // 첨부 줄이 붙는 그룹 (1.6)
    public func initialGroup(capabilityStates: [String: String]) -> StyleGroup?
    public func drawsGroupMap() -> Bool                                // 그룹 ≥2 && axis 있음 && next == byGroup
    public var drawsPhaseProgress: Bool { get }                        // next == byPhase

    // 입력창
    public func enterBehaviour(draft: String, phase: StylePhase?, hasAttachments: Bool,
                               running: Bool, hasRequests: Bool,
                               startingNew: Bool = false) -> StyleEnterBehaviour          // .verbatim | .rewrite(actionId)
    public func enterArmedPrefix(draft: String, phase: StylePhase?, hasAttachments: Bool = false,
                                 running: Bool, hasRequests: Bool, startingNew: Bool = false) -> String?
    public func placeholder(phase: StylePhase?, running: Bool, answering: Bool) -> String
    public func guidanceLine(phase: StylePhase?, running: Bool) -> String?

    // 권한
    public func autoAllowed(toolName: String) -> Bool                  // AskUserQuestion은 언제나 false
}
public enum StyleRecognisedName: Sendable, Equatable { case action(String), alias(name: String, phase: String) }
public enum StyleEnterBehaviour: Sendable, Equatable { case verbatim, rewrite(actionId: String) }

public enum StylePrerequisiteProbe {
    public static func evaluate(_ prerequisites: StylePrerequisites,
                                install: StyleInstall?,
                                home: URL = FileManager.default.homeDirectoryForCurrentUser,
                                workspacePath: String?,
                                environment: [String: String] = ProviderService.runtimeEnvironment())
        -> StylePrerequisiteResult
}
```

### 5.5 내장 기능 — `StyleCapabilities.swift`

```swift
public enum StyleCapabilityID {
    public static let casebook = "paperthin.casebook"
    public static let all: Set<String> = [casebook]        // 태그에서 고정되는 목록
    public static func states(of name: String) -> [String]  // 상태 값 전체 (E_CAPABILITY_MAP이 쓴다)
    public static func emptyDetail(of name: String) -> String?   // 1.8의 빈 상태 문구
}
public struct StyleAttachmentItem: Codable, Sendable, Equatable, Identifiable {
    public var id, title: String; public var detail: String?; public var readOnly: Bool
    public var openPath: String?                            // Mac만 사용, 워크스페이스 아래일 때만
}
public struct StyleCasebook: Sendable, Equatable { /* 오늘의 PaperthinCasebook 그대로 */
    public var name, path: String; public var files: [String]; public var modifiedAt: Date
    public var weight: String
    public static func latest(workspacePath: String) -> StyleCasebook?
}
public enum StyleCapabilities {
    /// 모든 내장 기능을 한 번에 읽어 상태와 첨부 항목을 돌려준다(워커에서 호출).
    /// 돌려주는 문자열은 이미 1.8의 정규화를 거쳤다.
    public static func evaluate(_ names: [String], workspacePath: String)
        -> (states: [String: String], attachments: [StyleAttachmentItem])
}
```

### 5.6 범용 패널 투영 — `StylePanelProjection.swift`

Mac 패널과 폰 페이로드를 **같은 함수 하나**가 만든다.

```swift
public struct StylePanel: Codable, Sendable, Equatable { /* 7.3의 JSON과 1:1 */ }
public enum StylePanelProjection {
    public static func make(style: RegisteredStyle,
                            prompts: [String],
                            selectedGroupId: String?,
                            capabilityStates: [String: String],
                            attachments: [StyleAttachmentItem],
                            prerequisites: StylePrerequisiteResult,
                            running: Bool = false) -> StylePanel   // 6.1의 7번을 폰에도 (7.3)
    /// 골든 직렬화: UTF-8, 키 사전순, 들여쓰기 2칸, `\/` 이스케이프 없음, 마지막 줄바꿈 1개.
    public static func serialise(_ panel: StylePanel) throws -> Data
}
```

### 5.7 터미널 입력 정책 — `TerminalInputPolicy.swift`

1.5의 `autoRun` 결정이 사는 곳이다. **엔진(MightyCore)에 둔다.**

```swift
public struct TerminalInput: Sendable, Equatable {
    public var text: String
    public var autoRun: Bool
    public init(text: String, autoRun: Bool)
}
public protocol TerminalPasteSink: AnyObject {
    func paste(text: String) -> Bool
    func sendEnter() -> Bool
}
public enum TerminalInputPolicy {
    public enum Outcome: Sendable, Equatable { case pasted, pastedAndRan, refused(String), failed }
    /// 붙여넣기 → (autoRun일 때만) Enter. 줄바꿈이 있으면 아무것도 하지 않는다.
    @discardableResult
    public static func apply(_ input: TerminalInput, to sink: TerminalPasteSink) -> Outcome
}
```

> **왜 엔진에 두는가 (검증된 제약).** 보안 검토는 "`LocalTerminalSession` 계층에서 스파이로 단언하라"고 했고 그 의도는 옳다 — 오늘 `installIsNeverRun`은 `AppStore` 계층이라 싱크 한 층 위에서 통과한다. 그런데 `Package.swift`에 테스트 타깃은 **`MightyCoreTests` 하나뿐**이고, `LocalTerminalSession`은 실행 파일 타깃 `MightyClaude`에 살며 `libghostty-spm`에 의존한다. 그 자리에 테스트를 놓으려면 새 테스트 타깃과 CI에서의 Ghostty 링크가 필요하다.
>
> 그래서 **싱크 자체를 엔진으로 옮긴다**. 붙여넣기와 Enter를 실제로 부르는 반복문 전체가 `TerminalInputPolicy.apply`이고, `LocalTerminalSession`은 `TerminalPasteSink`를 구현한 얇은 어댑터가 된다(오늘의 재시도 3회·0.9초 지연은 `LocalTerminalSession`에 남는다). 스파이 단언은 요구된 그대로 **싱크에서** 이뤄지고, 새 테스트 타깃은 필요 없다.

### 5.8 삭제 · 축소되는 기존 타입

| 기존 | 처분 |
|---|---|
| `OuroborosPhase`, `OuroborosAction`, `OuroborosFlow` (`OuroborosFlow.swift:7-158`) | **삭제**. `Prerequisites`는 `StylePrerequisiteResult`가 대신한다 |
| `PaperthinDomain`, `PaperthinSkill`, `PaperthinCatalog` (`PaperthinCatalog.swift:9-114`) | **삭제** |
| `PaperthinCasebook` (`PaperthinCatalog.swift:116-152`) | **유지, `StyleCasebook`으로 이름 변경**. 내장 기능의 구현이다 |
| `MightyStyles` (`OuroborosFlow.swift:23-34`) | **축소**: `MightyStyleIDs` — 예약 id 상수와 `isValidShape(_:)`만. `requestTitle(forInput:style:)`은 **레지스트리로** 이동(1.10) |
| `QuestionnaireProgress` (`OuroborosFlow.swift:162-210`) | **유지, `QuestionnaireProgress.swift`로 파일만 분리**. 질문 흐름은 스타일 규칙이 아니다 |
| `MobileOuroboros`, `MobilePaperthin*`, `MobileGuidedSkill` (`MobileRemoteModels.swift:312-373`) | **유지**. 구버전 폰용 레거시 어댑터의 출력 타입이 된다 |
| `MobileWire.mightyStyles` (`MobileRemoteModels.swift:53`) | **유지**. 와이어 어휘는 `["cli","ouroboros","paperthin"]`으로 고정된다(7.2) |

### 5.9 기존 테스트의 재조준

| 기존 | 새 이름 | 같은 값으로 남는 단언 |
|---|---|---|
| `OuroborosFlowTests.promptsPhasesAndNextStepsFollowTheOuroborosLoop` | `StylesOuroborosTests.promptsPhasesAndNextSteps` | 프롬프트 문자열, `.seed`/`.goal`/`.run` 단계 계산, 단계별 첫 다음 행동(`seed`/`run`/`evaluate`/`evolve`), `requestTitle` `"평가"`·`"막힘 풀기"`·`nil`, `takesText` 4개 |
| `…onlyOuroborosStateToolsAndToolDiscoveryAreAutoAllowed` | `StylesOuroborosTests.autoAllowIsExactNamesOnly` | 상태 도구 **16개 정확 이름**(1.13의 정정) + `ToolSearch`, 거부 목록 11개 그대로 |
| `…prerequisitesReadThePluginRegistryAndPath` | `StylesOuroborosTests.prerequisites` | 플러그인 레지스트리·PATH 판정 |
| `…questionnaireProgressWalksQuestions…` | `QuestionnaireProgressTests` (신규 파일) | 변경 없음 |
| `…askUserQuestionBecomesAQuestionBlock…` | 앞부분은 `ExecutionGraphTests`로, 뒷부분(`mightyStyle` 정규화)은 `StyleRegistryTests` | `["ouroboros", nil, nil]` — `knownStyleIds`를 넘겨 유지 (3.4) |
| `PaperthinCatalogTests.catalogFollowsPaperthinsIndex…` | `StylesPaperthinTests.catalog` | **28개**, 그룹별 **19/2/6/1**, `userInvoked` **12개 이름**, `nba` readOnly, coil 질문·depth 축 문자열 |
| `…promptsAndRequestTitlesNameTheSkill` | `StylesPaperthinTests.promptsAndTitles` | `/re0 docs/spec.md`, `/nba`, `"🔺 prism"`, 한 줄 접기, 설치 명령 접미사, **교차 스타일 제목**(`"/ouroboros:seed"` → `"시드"`, `"/nba"` → `"🎯 nba"`) — 1.10의 레지스트리 제목이 이 값을 지킨다 |
| `…installationAndCasebookAreReadFromDisk` | `StylesPaperthinTests.prerequisitesAndCasebook` | `re0-plan`/`re0-loop`/`re0-work` 추천, 케이스북 파일 순서, 링크 미추적, 24개 스캔 |
| `MobileRemoteExtensionTests`의 마이티 관련 6개 | `MobileRemoteExtensionTests`에 그대로 + `StyleProjectionTests` 추가 | 레거시 페이로드 값 전부. `guidedPrompt(…, text:"\n \n") == "/ouroboros:interview"`도 그대로(1.3.3) |

**값이 바뀌는 단언은 하나뿐이다**: 설치 버튼이 터미널에 명령을 넣기만 하고 Enter를 누르지 않는다(1.5·9.3의 `installIsNeverRun`). 그 밖에 그래프 머리말 문구(`마이티` → `마이티 · <name>[ · <단계>]`)와 Paperthin의 실행 중 placeholder(매니페스트 문구 → 앱 공통 문구)는 오늘 어떤 테스트도 단언하지 않는 뷰 문자열이다.

---

## 6. 앱(MightyClaude) 변경

### 6.1 범용 가이드 패널 — `GuidedPanel.swift` (신규, `OuroborosPanel.swift`·`PaperthinPanel.swift` 대체)

위에서 아래로, **있을 때만** 그린다.

1. **단계 표시줄** — `phases`가 비어 있지 않을 때. `order` 순, 현재 단계 강조, 지나온 단계 옅게 (오늘 `OuroborosPanel.swift:50-63` 그대로).
2. **질문 패널** — 대기 중인 `AskUserQuestion`이 있으면 **이 자리 하나만** 그리고 아래를 모두 감춘다(오늘 `OuroborosPanel.swift:39-40`).
3. **준비물 블록** — `prerequisites`가 미충족일 때. `report` 규칙이 고른 `missing` 줄들 + `hint` + `[설치]`(`canInstall`일 때만) + `[다시 확인]`.
4. **승인 띠** — 이 실행 창이 고르려 한 스타일이 `pending`일 때 `"확인이 필요합니다 · [내용 보기]"` **한 줄**. `[내용 보기]`가 4.4의 승인 **시트**를 연다. 시트에 `[허용]` `[지금은 안 함]`. 3.4의 `"이 실행 창의 스타일이 바뀌었습니다 — 다시 고르세요"`와 **동시에 나오지 않는다**: 승인된 매니페스트가 디스크에서 바뀌면 두 조건이 함께 참이 되는데, 한 상태에 두 문장은 사용자에게 두 문제처럼 읽힌다. 띠가 있으면 띠만 그린다 — 무엇을 하면 되는지 말하는 쪽이 그쪽이다.
5. **그룹 지도** — `evaluator.drawsGroupMap()`일 때(그룹 ≥2, `axis` 하나 이상, `next`가 `byGroup`). 오늘의 2×2 버튼 줄과 같은 모양, 선택된 그룹의 `question`이 아래에 한 줄.
6. **첨부 줄** — 내장 기능이 첨부를 선언했고 `rules.recommend.group`(또는 첨부를 낸 그룹)을 보고 있을 때. **항목이 0개여도 그린다**: 폴더 이름 · `detail` 배지 · 파일 칩 **6개까지**(누르면 기본 앱으로 연다) · 새로고침, 비었으면 기능이 준 빈 상태 문구(1.8).
7. **행동 칩** — 현재 단계가 `rules.start.phase`면 `startActions`, 아니면 `NextRule`의 결과. `byPhase`·`start`는 첫 번째가 prominent, `byGroup`은 추천 행동이 강조된다. `glyph`·`title`·`userInvoked`(사람 아이콘)·`readOnly`(눈 아이콘), 툴팁은 `help · 범위: scope · 호출자 · 읽기 전용`. `requiresText`인 행동은 입력창이 비면 비활성. `resetTitle`이 있으면 줄 끝에 그 칩(되돌린 상태에서는 `취소` 칩).

   **그리는 모양은 그룹 지도가 정한다.** 5번을 그리는 스타일의 7번은 **격자**(`LazyVGrid`, 세 칸 기준 높이 상한)이고, 그렇지 않으면 가로 한 줄이다. 그룹 지도가 있다는 것은 이 줄이 고른 그룹의 **카탈로그**라는 뜻이고, 카탈로그는 오늘 Paperthin이 그리던 격자다. 개수로 가르면(`> 6`) 같은 스타일의 네 그룹 중 셋이 줄로, 하나가 격자로 그려진다.

   **실행 중에는 `next`의 종류가 가른다.** `byPhase` 스타일은 순서이므로 진행 중인 단계가 끝나기 전에는 **고를 다음 단계가 없다**: 칩 줄 전체(되돌리기 칩 포함) 대신 진행 표시(spinner)를 그리고, 8번은 `guidance.running`을 쓴다(오늘 Ouroboros). `byGroup` 스타일은 카탈로그이므로 칩을 그대로 두고, 누른 것이 다음 요청으로 줄을 선다(오늘 Paperthin). 같은 규칙이 폰 투영에도 그대로 간다(7.3).
8. **안내 한 줄** — `evaluator.guidanceLine(phase:running:)`(1.7). 값이 없으면 줄을 그리지 않는다.

**입력창의 무장 표시.** Enter 규칙이 지금 발동 가능하면(`enterArmedPrefix`가 값을 주면) 입력창 왼쪽에 그 접두사를 **비편집 칩**으로 그린다(1.6).

**`MightyStylePicker`는 `Menu`(= `Picker(.menu)`)가 된다.** 오늘은 `.segmented` `Picker`에 리터럴 태그 3개다(`OuroborosPanel.swift:5-19`). segmented는 ① 항목이 4개를 넘으면 무너지고 ② 비활성 태그에 탭을 받을 수 없는데, 4.5는 미승인 스타일이 **보이고·비활성이고·눌리는** 것을 요구한다. 메뉴 행이면 행마다 이름 + 출처 배지 + `확인 필요 · 행동 N · 자동 허용 M` 배지를 그리고, `pending` 행은 탭 제스처로 승인 시트를 연다(스타일은 바뀌지 않는다). 목록은 `cli` + `registry.applicable(workspace:)`가 채운다.

### 6.2 설정 화면 — `StyleSettingsSection` (신규, `SettingsViews.swift:188-191` 옆)

- `[파일에서 스타일 등록…]` — `NSOpenPanel`(`.json`) → 한 번 읽기 → 검증 → 승인 시트 → 허용 시 **읽은 그 바이트를** `styles/`로 쓰기(0600) + 레코드(4.4).
- 복사되는 파일 이름은 **검증을 통과한 `id`에서 만든 `<id>.json`**이며 원본 파일 이름은 쓰지 않는다. 같은 이름이 이미 있으면 `E_ID_COLLISION`으로 거부한다(덮어쓰지 않는다).
- 목록: 이름 · **출처 배지** · id · 경로 · 해시 앞 12자 · 상태 · 행동 수 · 자동 허용 수.
- 항목마다 `[내용 보기]`(승인 시트를 읽기 모드로) · `[허용]`/`[취소]`/`[다시 허용]` · `[제거]`(user 출처만: 파일 삭제 + 레코드 삭제).
- 거부된 파일은 회색 줄로, 코드와 메시지를 그대로 보여 준다.
- 신뢰 저장소가 잠금 상태면 목록 맨 위에 `"신뢰 기록을 읽을 수 없습니다: <경로>"`와 경로를 띄우고 모든 허용·취소 버튼을 비활성으로 만든다(4.3).
- `[다시 스캔]` — 세 출처를 다시 읽고 `mobileObserve()`를 부른다.

### 6.3 브랜치 자리 대조표

오늘 스타일 값으로 갈라지는 자리와 그 대체다(Swift 51개소, 시드가 세던 "약 25곳"은 이 중 값 분기만 센 것이다).

| # | 오늘 (file:line) | 대체 |
|---|---|---|
| 1 | `MightyCore/OuroborosFlow.swift:23-34` `MightyStyles` | `MightyStyleIDs` + `StyleRegistry.requestTitle` |
| 2 | `OuroborosFlow.swift:36-158` `OuroborosFlow` | `Resources/Styles/ouroboros.json` |
| 3 | `OuroborosFlow.swift:162-210` `QuestionnaireProgress` | `QuestionnaireProgress.swift`로 이동 (동작 불변) |
| 4 | `PaperthinCatalog.swift:9-114` | `Resources/Styles/paperthin.json` |
| 5 | `PaperthinCatalog.swift:116-152` `PaperthinCasebook` | `StyleCasebook` (`StyleCapabilities.swift`) |
| 6 | `StateRepository.swift:196` | 형식만 정규화 + 선택 `knownStyleIds` (3.4) |
| 7 | `Models.swift:146-147` 주석 + `RunSession` | "nil은 일반 CLI, 그 밖은 등록된 스타일 id" + `mightyStyleHash` 추가 (3.4) |
| 8 | `Remote/MobileRemoteService.swift:579-587` `/guided` | `styleId`/`actionId` 수용 + 레지스트리 조회 (7.5) |
| 9 | `AppStore.swift:65-77` 스타일 전용 `@Published` 7개 | `styleRegistry`, `styleRejections`, `styleTrust`, `guidedProgress`, `guidedState: [String: GuidedPaneState]` |
| 10 | `AppStore.swift:365`, `:386` `ouroborosProgress.removeValue` | `guidedProgress.removeValue` |
| 11 | `AppStore.swift:690` `autoAllowOuroborosTool` | `autoAllowGuidedTool` (호출 자리·빈도 불변, 4.6) |
| 12 | `AppStore+Ouroboros.swift:9-13` `guidedStyle` | 적격성 그대로 + `registry.runnable(id, workspace:, hash:)` |
| 13 | `:14-15` `usesOuroboros`/`usesPaperthin` | 삭제 |
| 14 | `:17` `usesGuidedStyle` | 유지 (id 비교 없음) |
| 15 | `:19-27` `setMightyStyle` | 승인 관문 + `mightyStyleHash` 기록 + 범용 새로고침 |
| 16 | `:29-46` `refreshOuroborosPrerequisites` | `refreshStylePrerequisites(_:)` — 스타일 id별 캐시 |
| 17 | `:51-55` `sendOuroboros` | `sendStyleAction(_:actionId:text:)` |
| 18 | `:60-65` `sendGuidedPrompt` | 유지 |
| 19 | `:70-73` `sendPaperthin` | 17번에 흡수 |
| 20 | `:76-98` `refreshPaperthin` | `refreshStyleCapabilities(for:)` — 워크스페이스별 캐시 |
| 21 | `:100-105` `startPaperthinInstall` | `startStyleInstall(from:)` 하나 — `pendingTerminalInput[id] = TerminalInput(text:, autoRun: false)` |
| 22 | `:198-203` `startOuroborosInstall` | 21번에 흡수 |
| 23 | `:109-120` `ouroborosQuestion` | `guidedQuestion` |
| 24 | `:122-131` `ouroborosCanConfirm`/`ouroborosProgress` | `guided…` |
| 25 | `:133-155` `ouroborosChoose`/`Answer`/`Back` | `guided…` |
| 26 | `:157-170` `finishOuroborosStep` | `finishGuidedStep` |
| 27 | `:174-185` `autoAllowOuroborosTool` | `autoAllowGuidedTool` — 승인·해시 확인 후 `evaluator.autoAllowed(toolName:)` |
| 28 | `:189-195` `ouroborosVisibleRequests` | `guidedVisibleRequests` |
| 29 | `MightyClaude/OuroborosPanel.swift:5-20` `MightyStylePicker` | `Menu` + 배지 + `pending` 행 탭 (6.1) |
| 30 | `OuroborosPanel.swift:24-122` | `GuidedPanel` |
| 31 | `PaperthinPanel.swift:8-163` | `GuidedPanel` + `GuidedActionChip` |
| 32 | `SessionPaneView.swift:14-15` 두 개의 스타일 전용 `@ViewState` | `guidedSelection: GuidedSelection`(startingNew + groupId) 하나 |
| 33 | `SessionPaneView.swift:50` `ouroborosCanConfirm` | `guidedCanConfirm` |
| 34 | `:53-55` `ouroboros`/`paperthin` | 삭제 |
| 35 | `:56-60` `styleHint` | `manifest.subtitle` |
| 36 | `:62` `guided` | `store.guidedStyle(session) != nil` |
| 37 | `:63-66` `offersMightyStyle` | 유지 |
| 38 | `:67` `ouroborosPhase` | `evaluator.currentPhase(session:)` |
| 39 | `:68-77` `composerPlaceholder` | `evaluator.placeholder(…)` (1.7) |
| 40 | `:348` `style:` 인자 | `styleView: GuidedStyleView?`(평가기 + 이름 + 출처) |
| 41 | `:384-385` 두 패널 | `GuidedPanel` 하나 |
| 42 | `:491-495` `.task`/`.onChange` 새로고침 | `refreshStylePrerequisites` + `refreshStyleCapabilities` |
| 43 | `:608-623` Enter 처리 | `evaluator.enterBehaviour(…)` (1.6, 조건 6 포함) |
| 44 | `MightyGraphView.swift:15-16` `style: String?` | `styleTitles: StyleTitleSource`(레지스트리 + 워크스페이스, 1.10) |
| 45 | `MightyGraphView.swift:215` 요청 카드 제목 | 접두사·아이콘·색 모두 레지스트리에서, 꼬리 `요청 N · <provider>`는 그대로 (1.10) |
| 46 | `MightyGraphView.swift:74-79` 머리말 | `마이티 · <name>[ · <단계>]` 라벨 + `headerSummary` 그대로 |
| 47 | `ToolPermissionBar.swift:12-15` | `guidedVisibleRequests` |
| 48 | `AgentQuestionPanel.swift:15,24,32,50` | `guided…` 이름으로 |
| 49 | `AppStore+MobileRemote.swift:322-343`, `:350-375`, `:402-419`, `:461-468`, `:559-581` | 7장 |
| 50 | `LocalTerminalSession.swift:27-28`·`116-125`, `AppStore+Terminals.swift:31` | `TerminalInput{text,autoRun}` + `TerminalInputPolicy.apply` (1.5·5.7) |
| 51 | `SettingsViews.swift:188-191` 옆 | `StyleSettingsSection` 삽입 |

---

## 7. 폰 프로토콜 확장 (m1, 추가 전용)

docs/mobile-remote.md의 진화 규칙을 따른다: 기존 라우트·필드는 그대로, 추가는 새 capability와 선택 필드로만, 허용 값 밖의 값을 조용히 기본값으로 바꾸지 않는다. **이 절의 내용은 태그 이전에 docs/mobile-remote.md에 반영한다.**

### 7.1 새 capability

`MobileInfo.capabilities`의 허용 값에 **`"style"`**을 더한다. 전체 목록은
`["submit-mode","queue","pane","history","settings","commands","mighty","status","attachments","style"]`
(`MobileRemoteModels.swift:45`, `mobile/src/api/types.ts:64-74`).

### 7.2 `mightyStyle`의 고정 값 유지와 `styleId`

capability는 **호스트가** 광고하고 폰은 그것을 읽을 뿐이다. 그래서 "폰이 새 스타일을 아는가"는 호스트가 알 수 없다. 해결은 **두 필드를 항상 함께 보내는 것**이다.

- `mightyStyle` — 허용 값은 그대로 `cli` `ouroboros` `paperthin`(`MobileWire.mightyStyles`, `MobileRemoteModels.swift:53`). **내장 둘이 아닌 스타일에는 언제나 `cli`를 싣는다.**
- `styleId` (신규, 선택) — 열린 문자열.

**`styleId`는 `registry.runnable(...)`의 결과에서 나온다.** 실행 창이 실제로 그 스타일로 돌고 있지 않으면 — 미승인 · 취소 · 미등록 · 해시 불일치(3.4) · 원격 워크스페이스 — **언제나 `"cli"`다**. 저장된 `mightyStyle` 값은 폰에 나가지 않는다.
> 없으면 4.5가 공들여 감춘 것이 세션 요약 필드 하나로 새어 나간다. `options.styles`에서 빼고 `panel`을 안 만들고 400을 구별 불가능하게 만들어 놓고서, 모든 세션 요약에 미승인 스타일의 id를 실어 보내면 아무 의미가 없다.

즉 값이 조용히 기본값으로 바뀌는 일은 없다: `mightyStyle`은 자기 어휘 안에서 정직한 값(`cli` = "네가 아는 스타일이 아니다")을 싣고, **진실은 `styleId`가 나른다**. 구버전 폰은 `styleId`를 모르고 무시하므로 그 실행 창을 일반 CLI로 본다 — 시드가 요구한 폴백 그대로다.

**둘이 어긋날 때의 규칙은 하나다: `styleId`가 있으면 `mightyStyle`은 무시한다.** `styleId`가 없고 `mightyStyle`만 있으면 그 값이 쓰인다. 거부(400)는 없다 — 호스트 자신이 `mightyStyle: "cli"` + `styleId: "oh-my-claudecode"`를 보내므로, 받은 설정을 그대로 되돌려주는 폰을 거부하면 호스트의 페이로드가 왕복 불가능해진다.

| 필드가 붙는 곳 | 추가 |
|---|---|
| `MobileSessionSummary` | `styleId?: string` |
| `MobileSettings` | `styleId: string`, `options.styles: [{ id, label, source }]` |
| `MobileMighty` | `styleId: string`, `panel?: MobileStylePanel` |

`options.styles`는 `cli` + 이 실행 창이 실제로 고를 수 있는 **승인·번들** 스타일만 담고, `source`는 `bundled`\|`user`\|`workspace`다. 기존 `options.mightyStyles`는 `cli`와 내장 둘 중 적격한 것만 담은 채로 계속 전송된다.

### 7.3 `MobileMighty.panel`

```
MobileStylePanel {
  style: { id: string, name: string, source: "bundled"|"user"|"workspace",
           icon?: string, tint?: PaletteName },
  phase?: { id: string, title: string, index: number, count: number },   // index는 0부터
  groups: [{ id, title, axis?, question?, selected: boolean, actions: [actionId] }],
  actions: [{ id, title, icon?, glyph?, help, scope?,
              takesText: boolean, requiresText: boolean,
              flags: ("userInvoked" | "readOnly")[],
              prominent: boolean }],
  next: [actionId],                       // 순서대로, 첫 번째가 prominent
  recommended?: actionId,
  attachments: [{ id, title, detail?, readOnly: boolean }],
  setup: { ready: boolean, missing: string[], hint?: string, installCommand?: string },
  guidance?: string,                      // 1.7, {phase}가 이미 치환된 문자열
  presentation: { headerTitle: string, source: "bundled"|"user"|"workspace",
                  icon?: string, tint?: PaletteName }
}
```
- `actions`는 **카탈로그 전체**(≤100)다. 화면에 무엇을 낼지는 `groups`와 `next`가 정한다.
- `next`는 시작 규칙이 걸린 상태면 `start.actions`, 아니면 `NextRule`의 결과다. 폰은 어느 쪽인지 알 필요가 없다. 실행 중인 실행 창에서는 6.1의 7번 규칙이 그대로 적용된다: `byPhase` 스타일이면 `next`가 **빈 배열**이고 `guidance`는 `guidance.running`이며, `byGroup` 스타일이면 둘 다 평소와 같다. 맥이 칩을 감추는 동안 폰이 같은 칩을 내밀면 두 화면이 서로 다른 말을 한다.
- `prominent`는 `next`의 첫 항목에만 true다. 폰은 둘 중 무엇을 봐도 된다.
- `requiresText`는 **UI 힌트**다. 폰은 입력이 비었을 때 그 칩을 비활성으로 그리되, 라우트는 이 값을 강제하지 않는다(1.3.3).
- `attachments`는 **읽기 전용 정보**다. 폰에는 파일을 여는 길이 없으므로 경로를 싣지 않는다. 문자열은 이미 1.8의 정규화를 거쳤다.
- `setup.installCommand`는 **보여 주기 위한 것**이다. 폰에서 실행할 수 있는 라우트는 없다.
- `headerTitle`은 `"<name>"` 또는 `"<name> · <단계 title>"`. **`source`가 언제나 함께 실린다** — 폰은 `bundled`가 아니면 이름 옆에 출처 배지를 그린다(1.10). 이름 위조를 막는 것은 폰에서도 배지다.
- 팔레트 이름은 1.10의 9개, 아이콘 이름은 1.10의 33개. 폰이 모르는 이름을 받으면 자기 기본값으로 그리고 크래시하지 않는다.

`panel`은 **승인·번들 스타일이고 그 실행 창이 실제로 그 스타일일 때만**(즉 `styleId != "cli"`일 때만) 붙는다. 그 밖에는 필드 자체가 없다.

### 7.4 레거시 페이로드

내장 두 스타일에 한해 `mighty.ouroboros` / `mighty.paperthin`을 **계속 보낸다**. 엔진 → 레거시 어댑터(`MobileLegacyStyleAdapter`)가 만든다.

| 레거시 필드 | 엔진에서 |
|---|---|
| `ouroboros.phase` | 현재 단계 `id` |
| `ouroboros.ready` | `prerequisites.ready` |
| `ouroboros.takesText` | `actions.filter(takesText).map(id)` (매니페스트 순서) |
| `ouroboros.next` / `.all` | **`NextRule`의 결과만** / 전체 목록을 `{skill: id, title, help}`로 |
| `paperthin.installed` | `prerequisites.ready` |
| `paperthin.recommended` | `recommended` |
| `paperthin.domains[]` | `groups[]` → `{id, title, axis, question, skills}`; `skills[]`는 `{name: actionId, emoji: glyph, summary: help, scope, userInvoked, readOnly}` |
| `paperthin.casebook` | `attachments`를 낸 내장 기능의 원본 — `{name: 폴더명, weight, files}` |

어댑터는 `id == "ouroboros"` / `"paperthin"`일 때만 동작한다.

**`ouroboros.next`가 `panel.next`가 아닌 이유.** 옛 `OuroborosFlow.nextActions(after:)`는 단계 지도 하나만 보았고 `goal`에서 빈 배열을 돌려주었다 — 옛 폰은 그때 `all`로 떨어지도록 만들어져 있다. `panel.next`는 진입 단계에서 `start.actions`(`인터뷰 시작`·`자동 진행`)까지 실으므로, 그대로 쓰면 옛 폰의 `goal` 화면이 오늘과 달라진다. 레거시 필드만 단계 지도를 보고, 새 `panel.next`는 위의 규칙 그대로다.

### 7.5 라우트

기존 경로·메서드·오류는 그대로다.

| 라우트 | 추가 |
|---|---|
| `POST /m1/sessions/{id}/settings` | `styleId?: string`. `styleId`가 있으면 `mightyStyle`은 **무시한다**(7.2). `styleId`가 `options.styles`에 없으면 400 `"알 수 없는 스타일입니다."` (미승인·미등록 구분 없음, 4.5) |
| `POST /m1/sessions/{id}/guided` | `{ styleId?, actionId?, style?, skill?, text? }`. `styleId`+`actionId`가 새 형식, `style`+`skill`이 레거시(내장 둘만). 둘 다 없으면 400. 새 형식이 있으면 새 형식이 이긴다 |

`/guided`의 오류는 **오늘의 문자열 그대로** 유지한다.
- 스타일을 모른다 / 승인되지 않았다 → **400** `"알 수 없는 스타일입니다."`
- 행동을 모른다 → **400** `"이 스타일에 없는 스킬입니다."` (오늘 `AppStore+MobileRemote.swift:465`의 문자열. "행동"으로 바꾸지 않는다 — 이 절은 오류를 바꾸지 않겠다고 적어 두었고, 바꾸면 완료 기준 1의 "같은 값"이 깨진다.)
- 실행 창이 그 스타일이 아니다 → **409** `"이 실행 창은 <스타일 id> 스타일이 아닙니다."` (오늘 `:463`이 **id**를 끼워 넣는다. `name`이 아니다.)
- `actionId` 형식 검사는 오늘의 `skill` 검사를 넓힌 `^[A-Za-z0-9][A-Za-z0-9_.:-]{0,63}$`, 64바이트 이하(`MobileRemoteService.swift:582-584`).
- `text`는 지금처럼 32 KiB까지이고 **두 방식 모두 한 줄로 접힌 뒤** 프롬프트가 만들어진다.
- `requiresText`는 **검사하지 않는다**(1.3.3).

**내장 두 id 외의 스타일을 400으로 거절하지 않는다** — 시드의 완료 기준 2가 요구하는 바로 그 점이다(`MobileRemoteService.swift:581`의 `MightyStyles.all.contains` 검사가 레지스트리 조회로 바뀐다).

### 7.6 폰 렌더러

| 파일 | 처분 |
|---|---|
| `mobile/src/components/guided-panel.tsx` | **신규.** `MobileStylePanel` 하나로 단계 표시줄·그룹 지도·행동 칩·추천·첨부·준비물·안내 줄을 그린다. 이름 옆 출처 배지 포함 |
| `mobile/src/components/guided-action-chip.tsx` | **신규.** `glyph`·`title`·flags 마크·busy·`requiresText` 비활성 |
| `mobile/src/lib/styles.ts` | **신규.** `normalizeStylePanel(raw) -> StylePanel \| undefined`, `styleViewModel(panel)`, `guidedRequestFor(panel, actionId, text)`, `tintColor(name)`, `sourceBadge(source)`. 오늘 `mighty.ts`와 같은 방어적 파싱 규칙(제어·bidi 제거, 길이 절단, 못 그리는 항목은 버리기) |
| `mobile/src/lib/mighty.ts` | **유지·축소.** 블록 목록 파싱(`normalizeMighty`, `runHeading`, `blockKind*`)만 남고, `parseOuroboros`/`parsePaperthin`/`guidedStyleOf`/`ouroborosPhaseLabel`은 `styles.ts`로 흡수 |
| `mobile/src/components/ouroboros-panel.tsx` | **삭제** |
| `mobile/src/components/paperthin-panel.tsx` | **삭제** |
| `mobile/src/api/types.ts` | `Capability`에 `'style'`, `StylePanel` 계열 타입 추가, `GuidedStyle`(`'ouroboros' \| 'paperthin'`) **삭제**, `GuidedRequest`를 `{styleId, actionId, text?}`로 |
| `mobile/app/host/[hostId]/session/[sessionId].tsx:713-729` | 두 조건부 패널 → `GuidedPanel` 하나 |

폰은 스타일 id를 닫힌 집합으로 다루지 않는다. `panel`이 오면 그리고, 오지 않으면 블록 목록만 그린다.

---

## 8. 완료 기준과 고정

### 8.1 태그 이전에 끝나는 것 (완료 기준 1)

1. 내장 둘이 번들 매니페스트로 구동되고, 5.9의 재조준된 테스트가 **같은 값**으로 통과한다(의도된 예외 하나는 5.9 끝에 적었다).
2. Swift의 스타일 값 분기(6.3의 51개소)가 사라진다.
3. 폰이 범용 렌더러가 되고, 내장 둘에는 레거시 페이로드가 계속 나간다.
4. 신뢰·등록 규칙이 강제되고 9.3의 보안 테스트가 통과한다.
5. `docs/mobile-remote.md`가 7장으로 갱신되고, `docs/ouroboros-mode.md`·`docs/paperthin-mode.md`의 "구현 위치" 표가 엔진을 가리킨다.
6. **골든 기록 모드가 이미 존재한다**(8.4). 태그 이후에는 엔진 코드를 못 고치므로, 태그 이전에 들어 있어야 한다.
7. **`StyleGoldenContractTests`가 이미 존재한다**(8.4). 태그 이후에 추가되는 매니페스트를 검사하는 것이 **태그 이전에 얼어붙은 코드**가 되게 하는 장치다.
8. `scripts/check-style-freeze.sh`가 저장소에 있고 **macOS 워크플로가 그것을 부른다**(태그가 있을 때만 돌고, 없으면 건너뛴 줄을 찍는다). `styles/FREEZE`는 태그 커밋이 더하는 **유일한 파일**이고 내용은 그 커밋의 부모 SHA다(8.3).

검증: `cd native/macos && swift test` 전부 통과, `cd mobile && npm run typecheck && npx jest` 전부 통과.

> **문자열 잔존 검사는 두지 않는다.** 이전 초안에는 `scripts/check-style-literals.sh`와 `StyleLiteralTests`가 있었다. 소유자의 결정 어디에도 "Swift에 `"ouroboros"` 리터럴이 없어야 한다"는 요구는 없고, 그것이 대신하려던 "스타일별 분기 제거"는 6.3의 51행 표와 동등성 테스트가 이미 덮는다. 게다가 첫날부터 틀린다: 와이어 어휘 `MobileWire.mightyStyles`(`MobileRemoteModels.swift:53`)는 7.2가 **고정하기로 한** 문자열 리터럴인데 `Sources` 안에 있고, `Models.swift:146`의 주석도 그 단어를 쓴다. 규칙을 지키려면 허용 목록을 계속 손봐야 하고, 손보는 순간 규칙이 아니게 된다.

### 8.2 무엇이 고정되는가

태그 `mighty-style-engine-v1`이 다음을 얼린다.

> **고정 이력.** `mighty-style-engine-v1`(2026-09-19)은 스타일 엔진을 처음 얼린 태그다. `mighty-style-engine-v2`(2026-09-20)는 같은 방식으로 다시 건 고정이며 검사 스크립트와 CI의 기본 태그다. v1 과 v2 사이에 스타일 엔진 파일(`native/macos/Sources/MightyCore/Styles/**`)·스키마·규칙 어휘는 **바뀌지 않았다.** 달라진 것은 입력창의 한글 직접 조합(`docs/hangul-fallback-composer.md`)과 적합성 코퍼스(`styles/conformance/`)뿐이고, 검사가 저장소 전체를 보기 때문에 앱 소스 변경을 담으려면 고정을 다시 걸어야 했다. 외부 도구가 v1 시점의 엔진 파일을 고정 사본으로 쓰는 것은 그대로 유효하다.

- **스키마 v1** — 1장의 모든 필드·타입·한도. `guidance`(1.7)와 `rules.start`(1.6)를 포함한다.
- **규칙 어휘** — `StartRule` 2종, `PhaseRule` 2종, `NextRule` 2종, `EnterRule` 2종(제약 3개 포함), `RecommendRule` 2종, `InitialGroupRule` 2종.
- **Probe 종류** — `plugin`, `executable`, `skill`. `mode`·`report` 어휘와 per-probe `install`.
- **내장 기능 목록** — `paperthin.casebook` 하나. 그 상태 값 3개와 빈 상태 문구.
- **팔레트** — 1.10의 9개 이름.
- **아이콘 목록** — 1.10의 33개 이름.
- **오류 코드 목록** — 2장의 **47개**.
- **검증 순서** — 2장의 ⓪①②③④와 사전 스캔이 보는 다섯 가지.
- **투영의 모양과 직렬화 규칙** — `StylePanel`의 필드 구성(7.3), 실행 중 `next`·`guidance` 규칙(6.1의 7번), 8.4의 골든 직렬화 규칙(UTF-8, 키 사전순, 들여쓰기 2칸, `\/` 이스케이프 없음, 마지막 줄바꿈 1개), 그리고 골든의 **고정 입력 6가지**(8.4).
- **폰 페이로드** — `MobileStylePanel`의 모양, `style` capability, `styleId` 규칙, `/guided`의 새 필드, 와이어 어휘 `["cli","ouroboros","paperthin"]`.
- **터미널 입력 정책** — `TerminalInput`의 모양과 `autoRun`의 출처 규칙(1.5·5.7).

### 8.3 `scripts/check-style-freeze.sh`

```
용법: scripts/check-style-freeze.sh [<tag>]        # 기본 tag = mighty-style-engine-v2
```
**태그 커밋의 모양.** 태그 `T`는 **`styles/FREEZE` 하나만 더하는 커밋**이고, 그 내용은 **`T`의 부모 `P`의 SHA 40자 한 줄**이다. `P`가 마지막 엔진 커밋이다. 자기 자신의 SHA를 담을 수는 없으므로(자기 참조) 부모를 담는다.

동작:
1. **`git show "<tag>:styles/FREEZE"`로 읽는다**(작업 트리가 아니다). 그리고 셋을 모두 확인한다.
   - 내용이 40자리 16진수인가. 아니면 `종료 코드 2`와 `styles/FREEZE의 내용이 40자리 커밋 SHA가 아닙니다.`
   - `git rev-parse "<tag>^{commit}^"`(태그 커밋의 부모)가 그 SHA와 같은가.
   - `git diff --no-renames --name-only "<tag>^" "<tag>"`가 **정확히 `styles/FREEZE` 한 줄**인가.

   하나라도 어긋나면 `종료 코드 2`와 `태그 <tag>가 FREEZE와 맞지 않습니다.`
2. `git -c core.quotePath=false diff --no-renames --name-only <tag>..HEAD`를 읽는다.
3. 각 경로를 **먼저 금지 목록**, 그다음 허용 목록에 대어 본다.
   - 금지(먼저 본다): `styles/FREEZE`
   - 허용: `styles/**` · `native/macos/Tests/MightyCoreTests/StylesThirdParty*Tests.swift` · `mobile/src/__tests__/styles-thirdparty-*.test.ts` · `docs/styles-followups.md`
   - 테스트 두 글롭의 `*`는 **경로 조각 하나**이므로 `/`를 넘지 않는다. `StylesThirdPartyEvil/DeepTests.swift`는 바깥이다.
4. 금지 목록에 걸리거나 허용 목록 밖의 경로가 하나라도 있으면 그 목록을 한 줄씩 찍고 `종료 코드 1`.
5. 전부 안쪽이면 `OK: <N> files, all inside the manifest-only allow-list`와 `종료 코드 0`.

**git이 실패하면 2다.** 저장소가 아니거나, 태그가 없거나, `diff`가 오류로 끝나면(부분 클론의 promisor 불통, 개체 손상) 검사는 **성립하지 않은 것**이지 통과가 아니다. 목록이 비어서 `OK: 0 files`를 찍는 것과 목록을 읽지 못한 것은 같은 상태가 아니다.

**`--no-renames`가 필요한 이유.** 이름 변경은 기본 출력에서 목적지 한 줄로만 나오므로, `git mv <엔진 파일> styles/`가 허용 목록 안의 경로 하나로 보이고 엔진 파일이 사라진 사실은 보이지 않는다. 이름 변경 감지는 유사도 50%에서 걸리므로 파일을 도려내면서 동시에 옮길 수도 있다.

**1번이 1번인 이유.** 태그의 존재만 보면 `git tag -f mighty-style-engine-v1 HEAD` 한 줄로 `git diff`가 비고 스크립트가 `OK: 0 files`를 찍는다 — 고정 장치가 스스로를 고정하지 못한다. 커밋 SHA를 저장소 안의 파일에 박아 두고, 그 파일 자체를 허용 목록 **밖**에 두고, 태그 커밋이 그 파일 하나만 담게 해야 태그를 옮기는 순간 셋 중 하나가 반드시 깨진다.

**허용 목록을 좁힌 세 가지.**
- `docs/**`를 뺐다. 이 계약·고정된 스키마·고정된 규칙 어휘가 전부 `docs/` 안에 있어서, 태그 이후에 문서를 고쳐 이미 한 일을 합법화할 수 있었다. 남는 것은 `docs/styles-followups.md`(10장의 기록) 하나뿐이고, 다른 문서 수정은 태그 **이전**의 별도 커밋으로 간다.
- 테스트 글롭 `Styles*Tests.swift`를 `StylesThirdParty*Tests.swift`로 좁혔다. 앞의 글롭은 `StylesOuroborosTests`·`StylesPaperthinTests`·`StylesBundledTests`, 즉 **동등성 오라클 전체**를 태그 이후에 고칠 수 있게 한다. 태그 이후에 생기는 두 파일의 이름은 `StylesThirdPartyOhMyClaudecodeTests.swift` / `StylesThirdPartyGstackTests.swift`다.
- jest 쪽도 같은 이유로 `styles-*.test.ts` → `styles-thirdparty-*.test.ts`다.

삭제·이름 변경도 `--name-only`에 잡히므로 엔진 파일을 지우는 것도 실패한다. 스크립트는 `DEVELOPER_DIR` 설정을 요구하지 않는다(`git`만 쓴다).

### 8.4 태그 이후의 두 스타일 (완료 기준 2)

매니페스트는 **저장소 안**에 산다.

```
styles/FREEZE                              # 태그 커밋 SHA 한 줄 (8.3)
styles/oh-my-claudecode.json
styles/gstack.json
styles/golden/oh-my-claudecode.panel.json
styles/golden/gstack.panel.json
```

`styles/`의 파일은 자동으로 등록되지 않는다 — 사용자가 설정에서 골라 `<데이터 폴더>/styles/`로 복사하고 승인한다. 저장소의 사본은 **테스트가 읽는 원본**이다.

**고정된 계약 테스트가 먼저다.** `StyleGoldenContractTests`는 **태그 이전에** 쓰이고 허용 목록 **밖**에 산다. 하는 일:

> `styles/*.json`을 글롭해서, 찾은 **모든** 매니페스트에 대해 ① 디코딩·검증이 통과하고 ② `styles/golden/<id>.panel.json`이 존재하며 ③ 그 내용이 아래 고정 입력들에 대한 `StylePanelProjection.make(...)` 결과를 `serialise`한 바이트와 **정확히 같은지** 단언한다.

고정 입력 **6가지**: 빈 요청 기록(`empty`) · 그 스타일의 첫 행동을 부른 기록 하나(`afterFirstAction`) · 준비물 미충족(`notReady`) · 첫 그룹 선택(`firstGroup`) · **마지막 그룹 선택(`lastGroup`)** · **선언된 내장 기능이 모두 `open`이고 첨부 2개(`capabilityOpen`)**.

> 뒤의 두 가지가 없으면 여섯이 아니라 넷이고, 넷은 서로 겹친다. 앞의 네 입력은 모두 `initialGroup` 규칙이 고르는 **기본 그룹**에 떨어지므로, 기본이 아닌 그룹의 `byGroup` 결과도, 추천 규칙 전체(`recommended`가 언제나 `null`)도, 첨부 투영(`attachments`가 언제나 `[]`)도 어느 골든에도 기록되지 않는다. 기능을 선언하지 않은 스타일도 `capabilityOpen`에서 첨부 2개를 받는다 — 첨부의 모양은 스타일과 무관한 고정 계약이기 때문이다.

이 파일이 없으면 고정 장치에 구멍이 남는다. 태그 이후에 쓰이는 `StylesThirdPartyGstackTests.swift`는 허용 목록 **안**에 있고 골든을 기록한 바로 그 손이 쓰므로, 무엇이든 단언할 수 있다 — "기록 모드가 태그 이전에 존재한다"는 사실만으로는 그것이 **실제로 쓰였는지** 아무 얼어붙은 코드도 확인하지 않는다. 글롭이 그 고리를 닫는다: 매니페스트를 추가하는 것만으로 얼어붙은 코드가 골든을 요구하고 검사한다. 태그 이후의 테스트 파일은 장식이지 계약이 아니다.

**양쪽 계약.**
- Swift: `StyleGoldenContractTests`(위)와, 스타일별 `StylesThirdParty<Name>Tests`가 읽기 좋은 단언을 덧붙인다.
- jest: `mobile/src/__tests__/styles-thirdparty-<name>.test.ts`가 **같은 골든 파일**을 읽어 `normalizeStylePanel`에 먹이고, 뷰 모델이 그리는 그룹·행동·추천·준비물 문구와 `guidedRequestFor`의 결과를 단언한다. 얼어붙은 짝은 `mobile/src/__tests__/styles-thirdparty-contract.test.ts`로, `styles/golden/*.panel.json`을 **글롭해서** 찾은 모든 골든이 정규화기와 뷰 모델을 통과하는지 본다.

**jest는 `fs`로 읽는다, `import`가 아니다.** `mobile/tsconfig.json`의 `include`는 `["**/*.ts", "**/*.tsx", …]`이고 뿌리가 `mobile/`이며 `resolveJsonModule`도 켜져 있지 않다. `../../../styles/golden/…`을 `import`하면 `npm run typecheck`(`tsc --noEmit`)가 실패한다. `fs.readFileSync` + `JSON.parse`는 통과하고, `mobile/jest.config.js`의 `testMatch: ['**/src/__tests__/**/*.test.ts']`는 그 파일을 그대로 수집한다.

같은 파일이 양쪽 계약이므로, 엔진이 내는 것과 폰이 읽는 것이 한 줄이라도 어긋나면 어느 한쪽이 실패한다. 태그 이후의 테스트는 **엔진·폰 코드를 한 줄도 건드리지 않는다.**

**골든은 어떻게 만드나 — 기록 모드.** 태그 이전에 들어가는 엔진 기능이다.

```
MIGHTY_STYLE_GOLDEN=record swift test --filter Style
```
환경 변수가 `record`일 때만, 골든 비교 헬퍼가 단언하는 대신 기대 파일을 **덮어쓰고** 테스트를 실패시킨다(기록했음을 알리고 재실행을 강제). 값이 없거나 `record`가 아니면 순수 비교다. 새 스타일 작성자는 매니페스트를 쓰고 → 기록 모드로 골든을 뽑고 → 눈으로 확인하고 → 두 골든과 두 테스트 파일을 커밋한다. 커밋되는 것은 JSON과 테스트뿐이다.

직렬화 규칙(골든의 안정성을 위해 고정, 8.2): UTF-8, 키 사전순, 들여쓰기 2칸, `\/` 이스케이프 없음, 마지막 줄바꿈 1개.

### 8.5 완료 판정

```bash
export DEVELOPER_DIR=/Library/Developer/CommandLineTools
scripts/check-style-freeze.sh                       # 0
(cd native/macos && swift test)                     # 전부 통과
(cd mobile && npm run typecheck && npx jest)        # 전부 통과
```
화면·실기기 확인은 사용자의 별도 단계다(시드의 `verify_exemption_reason`).

**`check-style-freeze.sh`는 태그 이후 모든 푸시에서 CI가 돌린다.** 사람이 기억해서 돌리는 검사는 고정 장치가 아니다 — 잊은 한 번이 곧 완료 기준 2의 증거를 없앤다.

---

## 9. 고정 이전 테스트 계획

### 9.1 MightyCore (swift-testing)

| 파일 | 단언 |
|---|---|
| `StylesBundledTests` | 번들 매니페스트 2개가 3.2의 탐색으로 발견되고 디코딩·검증을 통과한다. id가 `ouroboros`·`paperthin`. `swift test`와 `.app` 양쪽 후보 경로에서 실패 없이 돈다 |
| `StylesOuroborosTests` | 5.9의 값 전부 |
| `StylesPaperthinTests` | 5.9의 값 전부 |
| `StyleManifestTests` | 2장 오류 코드 **하나마다 최소 한 개**의 거부 픽스처(47개). 목록은 **프로덕션의 `StyleErrorCodes.all`과 집합으로 같아야** 한다 — 테스트 안의 숫자 리터럴은 새 코드를 알아차리지 못한다. 유효 최소 매니페스트가 통과한다. 키의 이스케이프(`E_KEY_ESCAPE`, 확인된 `autoAllow` 공격 입력 포함) · 키의 제어 문자·길이 · 최상위 뒤 잔여물 · `map` 없는 규칙 · `glyph`의 ZWJ 이모지 · `lowercase` 인식 아래의 대문자 id/match/별칭 |
| `StyleRegistryTests` | 우선순위 3종, 같은 출처 안 충돌의 결정적 순서(§3.3의 이름순), `applicable`이 남의 워크스페이스 매니페스트를 내지 않음, 원격 워크스페이스가 workspace 출처를 내지 않음, `mightyStyle` 정규화(3.4), **레지스트리 제목**이 교차 스타일 값을 낸다(1.10), 인식되지 않은 입력은 아이콘·색도 얻지 않는다, 스타일 없는 실행 창은 훑을 것이 없다, 해시 불일치 창이 제목을 잃는다, 링크·하드 링크 픽스처는 32개 상한 **안쪽**에 정렬되는 이름을 쓴다 |
| `StyleTrustTests` | 승인 → `approved`, 1바이트 수정 → 스캔 후 `pending`, 취소 → `revoked`, 내용이 바뀌어도 자리가 `revoked`, 레코드 파일 권한 0600, 256개 상한, 잠금 상태, 원자적 병합, **병합이 자리당 승인 하나를 지키고 거부는 전부 남긴다**(4.3), 로드 뒤 깨진 파일은 잠기고 덮어쓰이지 않는다, 두 번째 프로세스의 승인이 다음 `load()`에 보인다 |
| `StyleEvaluatorTests` | 프롬프트 치환 4경우(텍스트 있음/없음 × 두 접기), `recognised` vs `namesSomething`의 차이(`"ooo 이거 해줘"`), 별칭, 단계 계산, `start`/`byPhase`/`byGroup`, Enter 6조건 각각의 거짓 경우와 **되돌리기 칩 예외**, 무장 칩이 입력창의 글까지 본다, 질문이 Enter 규칙보다 먼저다(`StyleComposer`), 실행 중 `byPhase`는 칩이 없고 `byGroup`은 그대로다, `initialGroup` 3상태, 추천이 그룹과 무관하다, `guidance`의 `{phase}` 치환과 단계 없을 때의 삭제 |
| `StyleCapabilityTests` | 케이스북 3상태, 파일 순서, 링크 미추적(폴더 링크·항목 링크·하드 링크), **정렬 뒤** 24개 상한(긴 이력에서 최신 폴더를 놓치지 않는다), 6개 칩 상한, 빈 상태 문구, **출력 문자열 정규화**(U+202E가 든 폴더명 → U+FFFD), 80자 한도가 `…`를 포함해 정확히 80 |
| `StyleProjectionTests` | 투영이 매니페스트와 상태를 빠짐없이 옮긴다. `source`가 실린다. 레거시 어댑터가 내장 둘의 옛 페이로드를 **오늘과 같은 값**으로 만든다 |
| `StyleGoldenContractTests` | 8.4 — `styles/*.json` 글롭, 골든 존재·일치. `styles/` 폴더가 아예 없으면 **소리 내어 실패한다**(빈 글롭과 잘못된 경로가 구별되지 않으면 관문 전체가 조용한 no-op이 된다). 태그 이전에 쓰이고 허용 목록 밖에 산다 |
| `TerminalInputPolicyTests` | 9.3의 `installIsNeverRun` |
| `MobileRemoteExtensionTests` (기존) | `styleId`/`panel` 인코딩, `options.styles`, `/guided` 새·옛 형식, 미승인 400, 빈 글로도 `interview`가 나간다(1.3.3). 레거시 페이로드의 단언은 **매니페스트에서 다시 읽은 값이 아니라 리터럴**이다(삭제된 `OuroborosFlowTests`·`PaperthinCatalogTests`가 박아 두었던 id 목록 그대로). `/guided`의 관문 판정(`guidedDecision`), 폰이 그룹을 고르지 않아도 추천이 살아 있다, 128개 요청 창에서 밀려난 단계는 진입 단계로 읽힌다 |

픽스처가 자명하지 않은 코드들: `E_SCHEMA_NOT_FIRST`(`{"id":"x","schema":1,…}`) · `E_DUPLICATE_KEY`(`"enter"` 두 번) · `E_TOO_DEEP`(9단, 그리고 `[`가 131 072개) · `E_RESERVED_NAME`(`"ＯＵＲＯＢＯＲＯＳ"` 전각) · `E_RESERVED_SEPARATOR`(`"name": "Flow · 승인됨"`) · `E_FOLD_TEXT`(`takesText:false` + `foldText`) · `E_PROMPT_RECOGNITION`(`id:"a"`, `prompt:"/b"`) · `E_START_PHASE`(없는 단계) · `E_PHASE_RULE_NONE`(`phases` 있음 + `phase:none`) · `E_ENTER_ACTION_TEXT`(`takesText:false` 행동을 가리키는 Enter) · `E_PROBE_NAME_SHAPE`(`"-bad"`) · `E_SCOPES`(`[]`, `["user","user"]`) · `E_AUTOALLOW_FOREIGN_SERVER`(`plugin_other_x`) · `E_AUTOALLOW_TOOLSEARCH_BUNDLED`(비번들 + `ToolSearch`) · `E_AUTOALLOW_SHAPE`(`server:"a_"`, `tool:"_b"`) · `E_UNKNOWN_ICON`(`"lock.fill"`) · `E_TYPE`(`"order": 1e308`, `glyph` 2자, `glyph`가 이모지가 아닌 문자).

### 9.2 jest

| 파일 | 단언 |
|---|---|
| `styles.test.ts` | `normalizeStylePanel`이 문서화된 페이로드를 읽는다. 못 그리는 항목은 던지지 않고 버린다. 제어·bidi 제거. 모르는 팔레트·아이콘에 크래시하지 않음. `guidedRequestFor`가 `takesText`를 존중. `source`가 `bundled`가 아니면 배지가 나온다 |
| `mighty.test.ts` (기존) | 블록 목록 단언은 그대로. `guidedStyleOf`의 "두 스타일만 안다" 테스트는 **열린 집합** 기준으로 교체: 임의 id가 `panel`과 함께 오면 렌더 대상이고, `panel`이 없으면 블록만 그린다 |
| `capabilities.test.ts` (기존) | `'style'`을 아는 호스트에서만 새 화면이 켜진다 |
| `client.test.ts` (기존) | `/guided`가 `{styleId, actionId}`를 보낸다 |
| `styles-thirdparty-contract.test.ts` | 8.4 — `styles/golden/*.panel.json`을 `fs`로 글롭·읽기(import 아님), 전부 정규화기와 뷰 모델을 통과 |

### 9.3 보안 테스트 (이름과 기대)

| 테스트 | 기대 |
|---|---|
| `autoAllowRejectsPatterns` | `"Bash(*)"`, `"Write"`+`server:"*"`, `"Edit(src/**)"`, `server:"a__b"`, `tool:"x__y"`, `server:"a_"`, `tool:"_b"` → 각각 `E_AUTOALLOW_SHAPE` / `E_AUTOALLOW_SERVER` |
| `autoAllowRejectsForeignServers` | 비번들 매니페스트가 `plugin_ouroboros_ouroboros`·`mcp-atlassian`·probe 없는 서버를 쓰면 `E_AUTOALLOW_FOREIGN_SERVER`; 자기 `plugin` probe의 접두사와 맞으면 통과 |
| `toolSearchIsBundledOnly` | 비번들 + `{"tool":"ToolSearch"}` → `E_AUTOALLOW_TOOLSEARCH_BUNDLED`; 번들은 통과 |
| `autoAllowRejectsAskUserQuestion` | 매니페스트 거부 `E_AUTOALLOW_QUESTION`; 또한 목록에 강제로 넣은 매니페스트를 만들어도 `evaluator.autoAllowed("AskUserQuestion") == false` |
| `autoAllowBindsServerPrefix` | `mcp__other__ouroboros_interview`, `mcp__plugin_ouroboros_evil__ouroboros_interview`, `mcp__plugin_ouroboros_ouroboros__evil__ouroboros_interview`, `mcp__plugin_ouroboros_ouroboros__` → 전부 false |
| `autoAllowNeedsApproval` | `pending`인 스타일의 실행 창에서 `autoAllowGuidedTool`이 아무 도구도 허용하지 않는다. 해시가 어긋난 실행 창도 마찬가지 |
| `collisionIsRefusedNotShadowed` | 워크스페이스 파일이 `ouroboros`를 주장하면 `E_RESERVED_ID`; 사용자 파일과 워크스페이스 파일이 같은 사용자 정의 id면 워크스페이스 쪽이 `E_ID_COLLISION` |
| `reservedNameIsRefused` | NFKC+대소문자 접기+공백 제거 후 `Ouroboros`와 같아지는 비번들 `name` → `E_RESERVED_NAME` |
| `changedHashGoesPending` | 승인 후 파일 1바이트 수정 → **다음 스캔에서** `pending`이고, 그 뒤로 자동 허용이 없다 (파일 감시는 없다, 3.1) |
| `downgradeIsRefused` | v1 승인 → v2 승인 → 파일을 v1 바이트로 되돌림 → 상태가 `approved`가 아니라 `pending`이고 자동 허용이 적용되지 않는다 |
| `revokedStaysRevokedAcrossEdits` | 거부된 **자리**는 내용이 바뀌어도 `revoked`이고 카드가 열리지 않는다 |
| `corruptApprovalsFailClosed` | 깨진 `approvals.json`에서 모든 비번들 스타일이 `pending`이고, `approve()`가 실패하며, 파일 바이트가 그대로다 |
| `worldWritableApprovalsRefused` | 0644로 만든 기록 파일이 잠금 상태를 유발한다. 폴더도 같다 |
| `evictionKeepsRevocations` | 256개 상한에서 `revoked`가 살아남고, 버릴 `approved`가 없으면 `StyleTrustFailure.full`로 실패한다 |
| `shownBytesAreUsedBytes` | 카드를 만든 뒤 원본 파일을 바꿔도 복사되는 바이트·기록되는 해시가 카드의 것과 같다 |
| `trustStoreIsOutsideScanDirectory` | `<데이터 폴더>/styles`를 스캔해도 신뢰 기록이 후보로 잡히지 않고 거부 목록에 나타나지 않는다. `id: "approvals"`인 매니페스트를 등록해도 신뢰 기록이 덮이지 않는다 |
| `paneStyleIsBoundToHash` | 같은 id·다른 해시의 매니페스트가 등록되면 저장된 실행 창이 `nil` 스타일이 되고, 과거 요청 블록이 **새 제목을 얻지 않는다** |
| `unapprovedIsInvisibleToPhone` | `options.styles`·`panel`·`styleId`에 없음(`styleId == "cli"`) |
| `unapprovedGuidedIs400` | `/guided`와 `/settings`가 **같은 문자열**로 400 `"알 수 없는 스타일입니다."` (404도 409도 아님), 둘 다 디스크를 읽지 않는다 |
| `oversizeAndDepthRefused` | 256 KB+1바이트 → `E_TOO_LARGE`; 9단 중첩 → `E_TOO_DEEP`; **`[`가 131 072개인 파일 → `E_TOO_DEEP`이고**(`E_NOT_JSON`이 아니고) **프로세스가 살아 있다** |
| `duplicateKeysRefused` | `"enter"`가 두 번 → `E_DUPLICATE_KEY` |
| `escapedKeysRefused` | 키 리터럴에 `\uXXXX`·`\"`가 있으면 `E_KEY_ESCAPE`. 확인된 공격 입력(`"autoAllow":[]`가 원본에 보이는데 엔진은 `Bash`·`Write`를 허용)이 거부된다 |
| `installTitleCannotForgeChrome` | `install.paneTitle`에 `·` → `E_RESERVED_SEPARATOR`. 비번들 스타일의 설치 창 제목에 출처 배지가 붙는다 |
| `approvingARepositoryStyleCopiesNothing` | 워크스페이스 매니페스트 승인이 `<데이터 폴더>/styles/`에 파일을 만들지 않는다 |
| `mergeKeepsOneApprovalPerPlace` | 두 프로세스 창에서 같은 자리의 `approved`가 하나만 남고, 되돌린 옛 바이트는 `pending`이다. `revoked`는 전부 남는다 |
| `controlCharsRefused` | `title`에 U+202E → `E_CONTROL_CHAR`; `name`에 U+200B → `E_CONTROL_CHAR`; `help`에 U+2028 → `E_CONTROL_CHAR` |
| `errorMessagesAreBounded` | 20만 자 키 이름 → 메시지의 `<경로>`가 64자 + `…`이고 제어 문자가 U+FFFD로 바뀌어 있다 |
| `capabilityOutputIsNormalised` | U+202E가 든 케이스북 폴더 이름이 Mac 패널과 폰 페이로드 양쪽에서 U+FFFD로 바뀌어 나온다 |
| `enterRewriteIsFirstRequestOnly` | 요청이 하나라도 있으면 `enterBehaviour`가 `.verbatim`이다 — **되돌리기 칩을 누른 상태만 예외**이며, 그 상태에서도 나머지 다섯 조건은 그대로 걸린다. `takesText:false` 행동을 가리키면 매니페스트가 `E_ENTER_ACTION_TEXT`로 거부된다 |
| `installIsNeverRun` | `TerminalInputPolicy.apply`가 `autoRun: false` 입력에 대해 스파이 싱크의 `sendEnter()`를 **부르지 않는다**(`paste(text:)`만 부른다). `autoRun: true`면 둘 다 부른다. 줄바꿈이 든 입력은 붙여넣기 자체가 거부되고 `.refused` 를 돌려준다. 그리고 `AppStore`가 매니페스트 설치를 시작하면 `pendingTerminalInput`의 `autoRun`이 false다 |

---

## 10. 후속 작업(규칙 어휘 확장)

고정된 어휘로 **지금도 표현되지 않는다고 이미 아는 것들**이다. 이번 범위 밖이며, 확장은 `schema: 2`로 간다. 이 목록의 사본이 `docs/styles-followups.md`로 살고, 태그 이후에 고칠 수 있는 유일한 문서다(8.3).

1. **진짜 2축 그리드.** `groups[].axis`는 문자열 한 줄이라 정렬만 돕는다. Paperthin의 2×2는 오늘처럼 버튼 4개 줄로 그린다. `axisX`/`axisY`가 필요하다.
2. **중첩 그룹.** 그룹 안의 그룹이 없다. gstack처럼 행동이 30개를 넘는 카탈로그는 1단 그룹으로 평평해진다.
3. **치환자 하나뿐.** `{text}` 외에 `{path}`·`{flag}` 같은 두 번째 입력 칸이 없다. `/oh-my-claudecode:execute <plan> --model opus`처럼 인자가 둘인 호출은 한 줄 자유 텍스트로 내려간다.
4. **조건부 다음 행동.** `NextRule`은 단계 또는 그룹만 본다. "준비물이 미충족이면 설치 행동만", "케이스북이 `complete`면 다른 목록"은 못 쓴다.
5. **상태를 읽는 단계 계산.** `PhaseRule`은 과거 요청 텍스트만 본다. MCP 도구의 응답(예: omc의 `state_get_status`)이나 파일 상태로 단계를 정할 수 없다. 그래서 루프형 스타일의 단계는 "마지막으로 부른 스킬"로만 읽힌다.
6. **루프 진행·중지.** 오래 도는 행동(`ralph`, `autopilot`, `re0-loop`)의 진행률이나 전용 중지 버튼을 선언할 수 없다. 취소도 그냥 또 하나의 행동이다.
7. **토글형 행동.** `/freeze`↔`/unfreeze`, `/guard`↔`/careful`처럼 켜고 끄는 짝을 하나의 on/off로 표현할 수 없고 상태를 되읽을 수도 없다.
8. **추천 규칙의 조합.** 내장 기능 하나의 상태만 본다. 둘을 조합하거나, 최근 사용 순으로 정렬하거나, 여러 개를 추천할 수 없다.
9. **단계별 자동 허용.** `autoAllow`는 스타일 전체에 걸린다. "인터뷰 단계에서만 이 도구"는 못 쓴다. 읽기 전용/쓰기 분류도 없어서, 소속 규칙(1.9) 안에서는 작성자가 무엇이든 고를 수 있다.
10. **설치 명령 하나.** 준비물이 여럿이어도 설치 버튼은 하나다. probe별 설치 명령이 없다(per-probe `install` 플래그는 "이 실패를 그 하나의 명령이 고치는가"만 말한다).
11. **내장 기능 하나.** `paperthin.casebook`뿐이다. "최근 커밋 읽기", "열린 PR 읽기", "플러그인 상태 읽기" 같은 것은 표현할 수 없다.
12. **적격성 고정.** 로컬 Claude 실행 창 + 마이티 보기가 아닌 곳(Codex·Gemini·원격 워크스페이스·터미널 창)에 스타일을 열어 줄 수 없다.
13. **질문 패널 손대기.** `AskUserQuestion` 패널의 문구·배치는 매니페스트가 못 건드린다.
14. **그래프 구조.** 요청 블록의 제목 **접두사**·아이콘·색만 바꿀 수 있고, `요청 N · <provider>` 꼬리나 단계 레인이나 새 블록 종류는 못 만든다.
15. **접두사 없는 맨 키워드 인식.** `recognition.prefixes`는 비울 수 없고 이름은 한 단어다(첫 공백 앞까지). oh-my-claudecode가 문서화한 `autopilot`·`ralplan`·`deepsearch`·`cancelomc` 같은 **맨 키워드 트리거**와 `deep interview` 같은 **두 단어 이름**은 인식되지 않는다 — 매니페스트에 그 행동이 있어도 사용자가 그렇게 치면 제목이 안 붙고 단계가 안 움직인다. `{"kind":"bareWords","names":[…]}`가 필요하다. omc는 이것을 낮춰 담는다(A.3).
16. **단계 막대와 그룹 선택기의 공존.** `byPhase`와 `byGroup`은 배타적이고(1.4), `byPhase`를 쓰면 `groups`는 승인 카드와 폰 페이로드의 설명으로만 남는다. "단계로 진행하면서 그룹으로 고르기"는 못 쓴다.
17. **닫힌 아이콘 목록.** 스푸핑을 막기 위해 `icon`을 33개로 고정했다(1.10). 임의의 SF Symbol을 쓰려는 스타일은 `glyph`(이모지 1자)로 내려가거나 목록 확장을 기다려야 한다. 목록 확장은 앱 업데이트지 매니페스트가 아니다.
18. **`requiresText`는 라우트에서 강제되지 않는다.** UI 힌트이며 `/guided`는 보지 않는다(1.3.3). "이 행동은 폰에서도 반드시 글이 있어야 한다"는 표현할 수 없다.

리뷰에서 나와 고정 이후로 미룬 두 가지도 같은 파일에 적혀 있다: 플러그인 이름의 `_` 때문에 1.9의 소속 규칙이 이름 하나를 정확히 짚지 못하는 잔여 헐거움(19), 그리고 이 기능 이전부터 있던 Ghostty 붙여넣기 확인의 자동 응답(20). 둘 다 매니페스트에서 닿지 않는다.

---

## 부록 A. 네 스타일 표현 가능성 스케치

확정이 아니라, 어휘가 넷을 담을 수 있는지 확인하는 스케치다. 각 항목 끝에 **무엇을 낮춰 담았는지** 정직하게 적는다.

### A.1 Ouroboros (번들, 1.13에 전문)

행동 9 · 단계 6 · 그룹 1 · 별칭 6 · 자동 허용 **17**(`ToolSearch` + 상태 16) · 준비물 `all`+`first`(plugin + executable, executable은 `install:false`) · `start: actions` · `byPhase` · `rewriteBareDraftTo`.
**낮춘 것: 없다.** 오늘 동작이 그대로 표현된다. `autoAllow`는 한도 32 안이고 소속 규칙을 만족한다(1.13).

### A.2 Paperthin (번들, 1.14에 전문)

행동 28 · 단계 0 · 그룹 4(axis+question) · 자동 허용 0 · 준비물 `any`+`first`(skill×3 + plugin) · `start: none` · `byGroup` · `verbatim` · `paperthin.casebook`로 추천·첨부·초기 그룹.
**낮춘 것**: 2×2 지도가 4버튼 줄이다(항목 1). 오늘 Mac 화면도 같은 모양이므로 회귀는 아니다.

### A.3 oh-my-claudecode (태그 이후, `styles/oh-my-claudecode.json`)

- `id: "oh-my-claudecode"`, `name: "oh-my-claudecode"`.
- `recognition: { "prefixes": ["/oh-my-claudecode:"], "lowercase": true }`.
- `phases`: `plan`(계획) → `execute`(실행) → `review`(리뷰) → `verify`(검증), `rules.phase.default: "plan"`.
- `actions`(15): `plan`, `ralplan`, `deep-interview`, `execute`, `autopilot`, `ralph`, `review`, `verify`, `research`, `trace`, `debug`, `team`, `wiki`, `remember`, `cancel`. 프롬프트는 `"/oh-my-claudecode:<id> {text}"`, `foldText: "trimOnly"` — 그래서 `E_PROMPT_RECOGNITION`이 모두 성립하고 `match`가 필요 없다.
- `aliases`: `ultragoal`→`execute`, `autoresearch`→`execute`, `drydock`→`plan`, `launch`→`execute`.
- `groups`: **하나뿐** — `{ "id": "flow", "title": "흐름", "actions": [행동 15개] }`. Ouroboros와 같은 모양이다. 근거: `next`가 `byPhase`이므로 그룹 지도는 그려지지 않고(1.4), `axis` 없는 그룹 4개를 선언해도 **죽은 JSON**이 된다 — 맥에서는 안 그려지고, 폰에서는 `selected`가 무의미하고, 승인 카드에서만 진짜 구획인 것처럼 나열된다.
- `prerequisites`: `{ "mode": "all", "report": "first", "probes": [{ "kind": "plugin", "prefix": "oh-my-claudecode@", "missing": "oh-my-claudecode 플러그인이 설치되어 있지 않습니다" }] }`.
- `install.command`: `claude plugin marketplace add https://github.com/Yeachan-Heo/oh-my-claudecode && claude plugin install oh-my-claudecode@omc` (출처: 그 마켓플레이스의 README).
- `autoAllow`(**16**): `server: "plugin_oh-my-claudecode_t"`의 **읽기 전용 상태 도구만** — `state_read`, `state_get_status`, `state_list_active`, `notepad_read`, `notepad_stats`, `project_memory_read`, `trace_summary`, `trace_timeline`, `wiki_read`, `wiki_list`, `wiki_query`, `shared_memory_read`, `shared_memory_list`, `session_search`, `list_omc_skills`, `deepinit_manifest`. 쓰기 도구(`state_write`, `state_clear`, `notepad_write_*`, `project_memory_write`, `wiki_add`/`delete`/`ingest`, `shared_memory_write`/`delete`, `python_repl`, `ast_grep_replace`, `load_omc_skills_*`)는 **넣지 않는다** — Ouroboros에서 실행 도구를 뺀 것과 같은 기준이다.
  - `ToolSearch`는 **없다**. 번들이 아니므로 스키마가 금지한다(1.9의 `E_AUTOALLOW_TOOLSEARCH_BUNDLED`).
  - 소속 규칙 확인: 유일한 `plugin` probe의 `prefix`가 `oh-my-claudecode@`이므로 `P = "oh-my-claudecode"`이고, `plugin_oh-my-claudecode_t`는 `plugin_oh-my-claudecode_`로 시작한다 ✓. 서버 이름에 하이픈이 있는 것이 1.9의 `server` 정규식이 `-`를 허용하는 이유다.
  - 개수 확인: 16 ≤ 32 ✓.
- `rules.start`: `{"kind":"actions","phase":"plan","actions":["plan","ralplan","deep-interview","autopilot","research"],"resetTitle":"새 목표"}` — 이것이 없으면 `next.map.plan`에 없는 진입 행동들에 버튼이 없다.
- `rules.next.map`: `plan`→`[execute, review, research, ralplan]`, `execute`→`[review, verify, trace, debug]`, `review`→`[verify, execute, trace]`, `verify`→`[execute, review, remember]`.
- `rules.enter`: `{"kind":"rewriteBareDraftTo","action":"plan","phase":"plan"}` — 빈 상태에서 친 목표가 `/oh-my-claudecode:plan <목표>`가 된다. `plan`은 `takesText: true`라 `E_ENTER_ACTION_TEXT`를 통과하고, 조건 6에 따라 **그 실행 창의 첫 요청에만** 발동한다.
- `rules.recommend`: `{"kind":"none"}`. `rules.initialGroup`: `{"kind":"fixed","group":"flow"}`.

**낮춘 것**
- **맨 키워드 트리거를 못 담는다**(항목 15). omc가 문서화한 `autopilot`·`ralplan`·`deep interview`·`deepsearch`·`cancelomc`는 접두사 없이 치는 말인데 `recognition`은 접두사를 요구하고 이름은 한 단어다. 매니페스트에 `autopilot`·`ralplan` 행동이 있어도, 사용자가 맨 키워드로 치면 제목도 안 붙고 단계도 안 움직인다. 버튼으로 누르면 정상 동작한다.
- `ralph`·`autopilot`은 스스로 도는 루프인데, 진행률도 전용 중지도 선언할 수 없다. `cancel`을 그냥 또 하나의 칩으로 둔다(항목 6).
- 단계가 `state_get_status`의 실제 상태가 아니라 "마지막으로 부른 스킬"로 읽힌다. 세션이 실행 중간에 멈춰도 단계는 `execute`로 남는다(항목 5).
- `model=opus` 같은 라우팅 인자를 둘째 칸으로 못 준다. 사용자가 자유 텍스트에 함께 적어야 한다(항목 3).
- 스킬 30개 이상 중 15개만 담았고, 그룹은 하나로 평평하다(항목 2·16).

### A.4 gstack (태그 이후, `styles/gstack.json`)

- `id: "gstack"`, `name: "gstack"`.
- `recognition: { "prefixes": ["/"], "lowercase": false }` — Paperthin과 같은 bare `/name`. 인식은 **그 스타일의 실행 창에만** 적용되므로 두 카탈로그가 같은 이름(`re0`, `nba`, `hate`…)을 가져도 충돌하지 않는다. 다만 요청 블록 **제목**은 레지스트리가 우선순위로 훑으므로(1.10), gstack 실행 창의 `/re0`는 번들 Paperthin이 먼저 인식해 `♻️ re0`가 붙는다 — 오늘 내장 둘이 서로의 제목을 보여 주는 것과 같은 동작이며, 그것이 의도다.
- `phases`: `plan`(계획) → `build`(구현) → `qa`(검증) → `ship`(출시) → `retro`(회고), `rules.phase.default: "plan"`. `qa`·`ship`·`retro`는 **단계 id이면서 동시에 행동 id**다 — 1.4가 이름 공간을 분리했으므로 합법이다.
- `actions`(24): `office-hours`, `spec`, `plan-ceo-review`, `plan-eng-review`, `plan-design-review`, `plan-devex-review`, `autoplan`, `investigate`, `qa`, `qa-only`, `review`, `design-review`, `devex-review`, `health`, `benchmark`, `canary`, `ship`, `land-and-deploy`, `document-release`, `retro`, `context-save`, `context-restore`, `freeze`, `unfreeze`. 프롬프트 `"/<id> {text}"`, `foldText: "oneLine"` — `E_PROMPT_RECOGNITION` 성립, `match` 불필요.
- `groups`: **하나** — `{ "id": "flow", "title": "흐름", "actions": [행동 24개] }`. A.3과 같은 이유다(`byPhase`를 쓴다).
- `aliases`: `plan-tune`→`plan`, `landing-report`→`ship`.
- `prerequisites`: `{ "mode": "any", "report": "first", "probes": [ {"kind":"skill","name":"_gstack-command","scopes":["user","workspace"],"missing":"gstack 스킬이 설치되어 있지 않습니다","hint":"…"}, {"kind":"skill","name":"gstack","scopes":["user","workspace"],"missing":"gstack 스킬이 설치되어 있지 않습니다"}, {"kind":"skill","name":"office-hours","scopes":["user","workspace"],"missing":"gstack 스킬이 설치되어 있지 않습니다"} ] }` — `_gstack-command`가 gstack 고유의 표식이라 가장 믿을 만하고, 나머지 둘은 백업이다. **맨 앞 밑줄은 1.5의 `name` 정규식이 허용한다**(디스크 확인: `~/.claude/skills/_gstack-command/SKILL.md`와 `~/.claude/skills/gstack/SKILL.md`가 둘 다 있다).
- `install.command`: `git clone --depth 1 https://github.com/garrytan/gstack.git ~/.claude/skills/gstack && ~/.claude/skills/gstack/setup` (출처: gstack 자신의 `gstack-upgrade` 스킬이 기술하는 clone + `./setup` 절차). 1.5에 따라 터미널에 **채워지기만** 하므로 사용자가 읽고 Enter를 누른다.
- `autoAllow`: **빈 배열.** gstack에는 MCP 서버가 없다. 모든 도구 호출이 실행 창의 권한 모드를 그대로 따른다 — Paperthin과 같다.
- `rules.start`: `{"kind":"actions","phase":"plan","actions":["office-hours","spec","plan-ceo-review","plan-eng-review","plan-design-review"]}` — gstack의 네 갈래 진입 역할이 여기서 나온다. `resetTitle`은 없다(Enter가 `verbatim`이라 되돌릴 상태가 없다).
- `rules.next.map`: `plan`→`[spec, plan-eng-review, autoplan, investigate]`, `build`→`[qa, review, health]`, `qa`→`[review, ship, investigate]`, `ship`→`[canary, document-release, retro]`, `retro`→`[office-hours, spec]`.
- `rules.enter`: `{"kind":"verbatim"}` — gstack은 자유 요청이 정상 경로다.
- `rules.recommend`: `{"kind":"none"}`. `rules.initialGroup`: `{"kind":"fixed","group":"flow"}`.

**낮춘 것**
- `freeze`/`unfreeze`, `guard`/`careful`이 세션 전역 상태를 바꾸는데 패널이 그 상태를 되읽거나 토글로 그릴 수 없다. 서로 다른 칩 두 개로 둔다(항목 7).
- `office-hours`는 모드가 둘인데 대화로 고른다. 두 행동으로 쪼개거나 자유 텍스트로 넘긴다(항목 3).
- 단계가 마지막으로 부른 스킬로만 읽히므로, 사람이 `ship`을 부르지 않고 직접 머지하면 단계가 `qa`에 머문다(항목 5).
- 스킬 90개가 넘는 설치본에서 24개만 골라 담았고 그룹은 하나다(항목 2·16). 나머지는 입력창에 직접 `/이름`을 쳐서 쓴다 — 인식 규칙이 bare `/name`이므로 단계는 반응하지 않지만 요청은 정상으로 나간다. 매니페스트에 없는 이름은 버튼도 없고 단계도 옮기지 않는다.

---

## 11. 구현 순서

세 레인으로 나눈다. **레인마다 파일 소유가 겹치지 않는다** — 같은 파일을 두 레인이 고치는 일이 없도록 아래 목록이 경계다.

### 레인 A — MightyCore 엔진

소유 파일:
- `native/macos/Sources/MightyCore/Styles/**` (신규 전부: `StyleManifest.swift`, `StyleManifestDecoder.swift`, `StyleManifestValidator.swift`, `StyleRegistry.swift`, `StyleTrustStore.swift`, `StyleEvaluator.swift`, `StyleCapabilities.swift`, `StylePanelProjection.swift`, `MightyStyleIDs.swift`, `BundledStyleSource.swift`)
- `native/macos/Sources/MightyCore/Resources/Styles/{ouroboros,paperthin}.json`
- `native/macos/Sources/MightyCore/TerminalInputPolicy.swift` (신규, 5.7)
- `native/macos/Sources/MightyCore/QuestionnaireProgress.swift` (신규, 이동)
- `native/macos/Sources/MightyCore/OuroborosFlow.swift`·`PaperthinCatalog.swift` (삭제)
- `native/macos/Sources/MightyCore/Models.swift` (`RunSession.mightyStyleHash`), `StateRepository.swift` (정규화)
- `native/macos/Sources/MightyCore/Remote/**` — `MobileRemoteModels.swift`, `MobileRemoteSupport.swift`, `MobileRemoteService.swift`, `MobileLegacyStyleAdapter.swift`(신규)
- `native/macos/Package.swift` (리소스 선언)
- `native/macos/Tests/MightyCoreTests/**` (9장 전부, `StyleGoldenContractTests` 포함)
- `scripts/check-style-freeze.sh` (신규), `scripts/build-macos.sh` (번들 존재 확인 추가)
- `styles/FREEZE`

산출물: 5장의 `public` API 전부 + 번들 매니페스트 2개 + `swift test` 통과.

### 레인 B — MightyClaude 앱

**레인 A의 5장 API에 대고 쓴다.** 5장의 선언이 확정되면 레인 A의 구현이 끝나기 전에도 시작할 수 있다.

소유 파일:
- `native/macos/Sources/MightyClaude/GuidedPanel.swift`·`GuidedActionChip.swift`·`StyleApprovalSheet.swift`·`StyleSettingsSection.swift` (신규)
- `native/macos/Sources/MightyClaude/OuroborosPanel.swift`·`PaperthinPanel.swift` (삭제)
- `native/macos/Sources/MightyClaude/AppStore+Styles.swift` (`AppStore+Ouroboros.swift`를 이름 바꿔 대체)
- `native/macos/Sources/MightyClaude/AppStore.swift`·`AppStore+Terminals.swift`·`AppStore+MobileRemote.swift`·`AppStore+CLIAccounts.swift`(`autoRun: true` 한 곳)
- `native/macos/Sources/MightyClaude/LocalTerminalSession.swift` (`TerminalPasteSink` 구현으로 축소)
- `native/macos/Sources/MightyClaude/SessionPaneView.swift`·`MightyGraphView.swift`·`ToolPermissionBar.swift`·`AgentQuestionPanel.swift`·`SettingsViews.swift`

작업 순서: ① `AppStore`의 레지스트리·신뢰 배선과 `guidedStyle`(해시 포함) → ② `TerminalInput` 전환 → ③ `GuidedPanel`·칩·시트 → ④ 선택기(`Menu`)와 설정 화면 → ⑤ 그래프 접두사·아이콘·색·머리말 → ⑥ 권한 훅 이름 변경 → ⑦ `AppStore+MobileRemote`의 투영 배선.

### 레인 C — 폰 (`mobile/`)

**이 문서의 JSON 모양(7장)에만 의존한다.** 레인 A·B를 기다리지 않는다 — 7.3의 페이로드를 손으로 적은 픽스처로 시작하고, 골든이 나오면 그 파일로 갈아탄다.

소유 파일:
- `mobile/src/api/types.ts` (`Capability`에 `'style'`, `StylePanel` 계열, `GuidedStyle` 삭제, `GuidedRequest` 교체)
- `mobile/src/lib/styles.ts` (신규), `mobile/src/lib/mighty.ts` (축소)
- `mobile/src/components/guided-panel.tsx`·`guided-action-chip.tsx` (신규)
- `mobile/src/components/ouroboros-panel.tsx`·`paperthin-panel.tsx` (삭제)
- `mobile/app/host/[hostId]/session/[sessionId].tsx`
- `mobile/src/__tests__/styles.test.ts`(신규) · `styles-thirdparty-contract.test.ts`(신규) · `mighty.test.ts`·`capabilities.test.ts`·`client.test.ts`(수정)

### 세 레인이 만나는 곳

- **`docs/mobile-remote.md`** — 7장을 반영한다. 레인 C가 쓰고 레인 A가 검토한다(둘 다 그 계약을 구현하므로). 태그 **이전**의 별도 커밋이다(8.3이 태그 이후 `docs/` 수정을 막는다).
- **`docs/ouroboros-mode.md`·`docs/paperthin-mode.md`** — "구현 위치" 표를 엔진으로 돌린다. 레인 A. 역시 태그 이전.
- **`docs/styles-followups.md`** — 10장의 사본. 누구든 쓰되 태그 이전에 만들어져 있어야 한다.
- **합류 지점**: 레인 A의 `swift test`와 레인 C의 `npx jest`가 각각 초록이 된 뒤, 레인 B가 화면을 붙이고 8.5의 세 명령이 함께 통과하면 태그를 찍는다. 태그 커밋에 `styles/FREEZE`가 들어간다.
- **태그 이후**: `styles/oh-my-claudecode.json` + 골든 2개 + 테스트 2개를 한 커밋, `styles/gstack.json` + 골든 2개 + 테스트 2개를 다음 커밋. 두 커밋 모두 `scripts/check-style-freeze.sh`가 0이어야 한다.
