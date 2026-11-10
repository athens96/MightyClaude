# 작업 지도 (Mac · Windows · 휴대폰)

워크스페이스에서 해 온 일을 **버전 중심**으로 한눈에 보는 창이다. git 기록을 버전 태그(`v0.4.48`…)로 나눠, 버전마다 플랫폼별로 얼마나 일했는지, 릴리스됐는지, 어떤 주제의 일이었는지, 그 일을 시킨 내 요청이 무엇이었는지를 보여 준다. 읽기만 하며 저장소에 아무것도 쓰지 않는다.

모델의 규칙(태그·범위·플랫폼·상태·요약 읽기)은 Mac(`MightyCore/WorkMap.swift`)과 Windows(`MightyCode.Core/WorkMap.cs`)가 똑같이 구현하고, 둘 다 `native/contracts/fixtures/work-map.json`으로 검사한다. 휴대폰은 같은 모델을 `/m1`로 받는다.

## 여는 방법

- 워크스페이스 제목줄 오른쪽의 지도 버튼(파일 버튼 옆)
- 메뉴 **워크스페이스 → 작업 지도 열기**
- 휴대폰: 워크스페이스 카드의 **작업 지도**(호스트가 `workMap`을 알릴 때)

워크스페이스마다 작업 지도는 하나다(`kind = "workmap"`, 창 id `workmap:<workspaceId>`). 파일 창처럼 저장하지 않고(앱을 다시 켜면 사라진다), 처음 열면 현재 실행 창 옆의 탭으로 들어간다. 에이전트가 아니므로 상태 목록·알림·사용량·모델 목록·휴대폰의 실행 창 목록은 세지 않는다.

원격 워크스페이스나 git 저장소가 아닌 폴더에서는 버튼이 꺼지고 이유가 도움말로 보인다(`workMap.unavailable.remote`, `workMap.unavailable.notGit`).

## 화면

```
┌ 작업 지도 · v0.4.41 – 0.4.49 ───────────── [Mac][Windows][폰][CI·릴리스] [요청 있는 주제만] ┐
│ 한눈 보기: 열 = 버전(오래된 것 → 최신, 맨 오른쪽이 미릴리스), 행 = 플랫폼, 점 크기 = 커밋 수    │
├──────────────────────────────────────────────────┬──────────────────────────────────────────┤
│ 버전 목록 (최신이 위)                              │ 고른 주제                                 │
│  0.4.49 [릴리스 진행 중 · ci 단계] 커밋 8 · 주제 5 │  제목 · 플랫폼 점 · 버전                   │
│   [주제 카드] [주제 카드] …                        │  요약                                     │
│  0.4.48 [릴리스됨 · 11/02] 커밋 20                 │  내 요청 (이 워크스페이스의 실행 창)        │
│   주제 요약 없음 · [주제 요약 만들기] [커밋 보기]    │  커밋 (sha · 범위 · 제목)                  │
│                                                    │  바뀐 파일                                │
└──────────────────────────────────────────────────┴──────────────────────────────────────────┘
```

- **한눈 보기**: 열을 누르면 그 버전이 골라지고 목록이 그 버전으로 스크롤된다. 점의 지름은 `6 + 14·√(n / 가장 큰 n)`(px/pt). 미릴리스 열의 점은 점선 테두리.
- **플랫폼 칩**: 끈 플랫폼의 점·카드는 흐려진다(숨기지 않는다). **요청 있는 주제만**은 요청이 하나도 없는 주제 카드를 숨긴다.
- **버전 카드**: 버전 이름(이름 없는 미릴리스는 `workMap.version.next`), 상태 배지, 커밋 수, 주제 수, 플랫폼 비율 막대. 주제가 있으면 주제 카드 격자, 없으면 `workMap.noTopics`와 **주제 요약 만들기**, **커밋 보기**(커밋 목록을 펼친다). 요약이 오래됐으면(`topicsStale`) 배지 `workMap.stale`과 **주제 다시 요약**.
- **주제 카드**: 제목, 요약(두 줄), 플랫폼 점, `커밋 n · 요청 n`. 누르면 오른쪽에 상세.
- **상세**: 요청이 없으면 `workMap.detail.noRequests`. 커밋 sha를 누르면 복사한다. 파일을 누르면 파일 창에서 그 파일을 연다(Mac·Windows).
- **요약 중**: 그 버전 카드에 진행 표시와 `workMap.summarizing`; 다른 버전의 버튼은 꺼진다(워크스페이스마다 한 번에 하나). 실패하면 카드에 오류 문구.
- 고른 버전·주제·플랫폼 칩은 창이 열려 있는 동안만 기억한다.

휴대폰: 위에 버전 칩(가로 스크롤, 최신이 왼쪽, 각 칩에 비율 막대), 아래에 고른 버전의 상태와 주제 카드. 카드를 누르면 아래에서 상세 시트(요청, 커밋, 파일 수). 주제가 없으면 **주제 요약 만들기**(호스트에 요청하고, 끝날 때까지 2초마다 다시 읽는다).

## 모델

호스트가 만들어 웹 페이지(브리지 `workMapState`)와 휴대폰(`GET /m1/workspaces/{id}/workmap`)에 같은 JSON으로 준다. 휴대폰에는 `files`를 빼고 보낸다(`filesCount`만).

```jsonc
{
  "available": true,            // false면 reason만 의미가 있다
  "reason": null,               // "notGit" | "remote" | "gitFailed"
  "head": "4d4d3ba9",           // HEAD의 8자
  "lanes": ["mac", "windows", "phone", "ci", "etc"],
  "summarizing": null,          // 요약 중인 버전 키 또는 null
  "versions": [                 // 최신이 먼저
    {
      "key": "next",            // 태그면 태그 이름("v0.4.48"), 미릴리스면 "next" — 요약 캐시와 요청의 키
      "version": "0.4.49",      // 표시 이름; 미릴리스이고 VERSION 파일로 알 수 없으면 null
      "tag": null,
      "date": "2026-11-03",     // 태그 날짜, 미릴리스는 가장 새 커밋의 날짜 (YYYY-MM-DD)
      "status": "releasing",    // "released" | "releasing" | "unreleased"
      "releaseStep": "ci",      // releasing일 때 다음 단계
      "rangeEnd": "<40자 sha>", // 범위의 끝 커밋 — 요약이 오래됐는지 가린다
      "total": 8,
      "truncated": false,       // 커밋이 maximumCommitsPerVersion을 넘어 잘렸는지
      "counts": { "mac": 4, "windows": 2, "phone": 2, "ci": 1 },
      "commits": [
        { "sha": "bd06fa36", "date": "2026-11-03T01:14:00+09:00", "lanes": ["mac"], "scope": "mac tests",
          "subject": "ToolkitProcess's deadline tests time …", "files": ["native/macos/…"], "filesCount": 1 }
      ],
      "topics": null,           // 요약 전이면 null
      "topicsStale": false,
      "summaryError": null,     // 마지막 요약이 실패했으면 그 문구(현지화된)
      "requests": [
        { "id": "<paneId>/<runId>", "paneId": "…", "text": "원유아이로 변경해주고 …", "date": "2026-11-02T20:58:00+09:00",
          "files": ["native/windows/web/src/native/Surfaces.tsx"], "commits": ["19c184ab"] }
      ]
    }
  ]
}
```

주제: `{ "title", "summary", "lanes": [...], "commits": ["sha8", …], "requests": ["<id>", …] }`.

### 버전과 범위

1. 태그: `git for-each-ref refs/tags --format=%(refname:short)%1f%(creatordate:iso-strict)%1f%(*objectname)%1f%(objectname)` 중 이름이 `^v\d+\.\d+\.\d+$`인 것. 다른 태그(`mighty-style-engine-v3` 등)는 버린다. 버전 숫자 순으로 정렬해 최신 `maximumVersions`(12)개만 쓴다.
2. 태그 버전의 범위는 `<바로 아래 태그>..<태그>`. 보이는 것 중 가장 오래된 태그는 그 아래 태그가 있으면 그것부터, 없으면 처음부터(그래도 `maximumCommitsPerVersion`까지).
3. 미릴리스: 가장 새 태그에서 HEAD까지(`<태그>..HEAD`, 태그가 없으면 HEAD 전체)에 커밋이 하나라도 있으면 맨 위에 `key = "next"`로 둔다.
   - `version`: 워크스페이스 루트의 `VERSION` 파일(앞뒤 공백 제거)이 `x.y.z`이고 가장 새 태그보다 크면 그 값, 아니면 null.
   - `status`: `version`이 있고 `release/<version>/state.json`의 `done.push`가 true이고 `done.verify`가 true가 아니면 `"releasing"`이고 `releaseStep`은 `check, version, push, ci, download, manifest, page, upload, github, verify` 중 처음으로 끝나지 않은 단계. 그 밖에는 `"unreleased"`. (`release/`는 git에 올라가지 않으므로 릴리스를 하는 PC에서만 보인다.)
4. 커밋: `git log --no-merges --format=%x1e%H%x1f%aI%x1f%s --name-only <범위>`, 최신 먼저, 버전마다 `maximumCommitsPerVersion`(400)개, 커밋마다 파일 `maximumFilesPerCommit`(50)개까지(`filesCount`는 원래 수).
5. git은 각 플랫폼의 `DelegationGit` 실행기로, 명령마다 10초 제한. 실패하면 `available = false, reason = "gitFailed"`.

### 플랫폼(레인)

제목 앞의 범위를 읽는다.
- `(mac, windows, phone) 제목` — 괄호 안을 쉼표로 나눈다.
- `fix(windows): 제목` — 관례식 커밋의 괄호 안.

범위마다 첫 낱말(소문자)로 레인을 정한다: `mac`·`macos` → `mac`, `windows`·`web`·`winui` → `windows`, `phone`·`mobile`·`android` → `phone`, `ci`·`release` → `ci`. 하나도 정해지지 않으면 `["etc"]`. 레인은 `lanes` 순서로, 중복 없이. `scope`는 괄호 안 그대로, `subject`는 나머지(관례식은 `fix(windows): ` 뒤). 범위가 없으면 `scope = null`, 제목 전체. `counts`는 레인별 커밋 수(한 커밋이 여러 레인이면 각각 센다).

### 내 요청

이 워크스페이스의 열린 실행 창마다 `GraphRuns`(알림 실행 제외, 입력이 빈 것 제외)를 읽고, 그 실행이 고친 파일은 `FileOriginalStore`의 그 실행 기록(`files[].path`, 루트 기준 상대 경로)에서 가져온다.
- `date`: 그 실행의 첫 루트 기록 시각(없으면 null). `text`: 입력의 앞 200자.
- 이어진 커밋: 요청의 파일 중 하나라도 건드린 커밋 가운데, `date`가 있으면 그 시각 이후 커밋만, 오래된 것부터 최대 20개.
- 요청은 이어진 커밋 중 가장 오래된 것이 든 버전에 놓인다. 이어진 커밋이 없으면, `date`가 가장 새 태그 날짜 이후일 때만 미릴리스 버전에 놓는다(나머지는 버린다).
- 워크스페이스 전체에서 최신 200개까지.

닫은 창의 요청은 앱에 남지 않으므로 보이지 않는다(`workMap.detail.noRequests`).

### 주제 요약

**주제 요약 만들기**(`workMapSummarize { workspaceId, key }`, 휴대폰 `POST /m1/workspaces/{id}/workmap/summarize { key }` → 202)가 그 버전의 커밋·요청·릴리스 노트로 Claude CLI를 한 번 부른다. 워크스페이스마다 한 번에 하나(진행 중이면 409).

- 명령: 모델 목록 확인과 같은 헤드리스 설정(`--print --output-format stream-json --input-format stream-json --verbose --no-session-persistence --permission-mode dontAsk --strict-mcp-config --mcp-config {"mcpServers":{}} --tools ""` 등, 같은 조용한 환경)으로 사용자 메시지 하나를 보내고 `result` 이벤트의 글을 읽는다. 제한 시간 120초. 작업 폴더는 임시 폴더(워크스페이스가 아니다).
- 프롬프트는 fixture의 `prompt` 틀을 그대로 쓴다: `{language}`(앱 언어의 영어 이름), `{version}`, `{commits}`(줄마다 `sha8 [lanes] scope: subject`), `{requests}`(줄마다 `id: text`, 없으면 `(none)`), `{notes}`(`release/<version>-notes.md`의 앞 8,000자, 없으면 `(none)`).
- 답 읽기(fixture `summaryCases`): 답에서 처음 `{`부터 짝이 맞는 `}`까지를 JSON으로 읽는다(코드 펜스 안이어도 된다). `topics` 배열에서 제목이 빈 것은 버리고, 최대 8개, 제목 40자·요약 160자로 자른다. 커밋은 이 버전에 있는 sha8만, 요청은 이 버전의 id만 남긴다. 커밋이 하나도 남지 않은 주제는 버린다. 어느 주제에도 들지 않은 커밋이 있으면 끝에 `workMap.topic.other` 주제로 모은다. `lanes`는 그 주제 커밋들의 레인 합(순서는 `lanes`). 읽을 수 없거나 주제가 하나도 없으면 실패.
- 실패 문구: CLI 없음 `workMap.error.cli`, 시간 초과 `workMap.error.timeout`, 그 밖 `workMap.error.output`.
- 저장: `<앱 데이터>/work-map/<워크스페이스 루트 실제 경로의 sha256>.json`에 `{ "versions": { "<key>": { "rangeEnd", "topics", "created" } } }`. 임시 이름에 쓰고 옮긴다. 읽을 때 저장된 `rangeEnd`가 지금과 다르면 `topicsStale = true`(주제는 그대로 보인다). 미릴리스 요약은 태그가 생겨 `next`가 사라지면 그대로 남지만 쓰이지 않는다(새 태그 키로 다시 요약한다).

## 휴대폰

- 호스트 능력 `workMap`(Mac `MobileCapability.all`, Windows `MobileDesktopRouter.Capabilities`).
- `GET /m1/workspaces/{id}/workmap` → 위 모델(`files` 없이). 원격·없는 워크스페이스는 404.
- `POST /m1/workspaces/{id}/workmap/summarize` 본문 `{ "key": "next" }` → 202, 진행 중이면 409, 없는 키는 400.
- 화면 `mobile/app/host/[hostId]/workspace/[workspaceId]/workmap.tsx`.

## 구현

| | Mac | Windows | 휴대폰 |
|---|---|---|---|
| 모델·규칙 | `MightyCore/WorkMap.swift` | `MightyCode.Core/WorkMap.cs` | `mobile/src/lib/work-map.ts`(표시 규칙만) |
| 요약 실행·저장 | `MightyCore/WorkMapSummary.swift` | `MightyCode.Core/WorkMapSummary.cs` | — |
| 창 | `WorkMapView.swift`, `AppStore+WorkMap.swift` | `MainWindow.WorkMap.cs`, `web/src/panes/workmap/` | `workmap.tsx` |
| 휴대폰 경로 | `MobileRemoteService.swift` | `MobileDesktopRouter.cs` | `api/client.ts` |
| 검사 | `WorkMapTests` | `WorkMapVerification.cs` | `work-map.test.ts` |

문구 키는 모두 `workMap.*`(4개 언어, `locales/`).
