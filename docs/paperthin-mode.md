# 마이티 모드 스타일: Paperthin

상태: **v1 구현됨**. 마이티 모드의 두 번째 안내형 스타일이다. [Paperthin](https://github.com/LilMGenius/paperthin)(MIT)을 UI만으로 쓸 수 있게 한다. 스타일 구조와 질문 패널은 [ouroboros-mode.md](ouroboros-mode.md)와 공유한다.

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

마이티 모드 입력창 위 선택기가 `CLI | Ouroboros | Paperthin`이 된다(이 Mac의 Claude 실행 창).

| 부분 | 내용 |
|---|---|
| 지도 | depth · breadth · coil · mesh 네 버튼. 축(하나·지금 등)을 함께 보여주고, 고른 영역의 질문이 아래에 나온다. 케이스북을 처음 읽었을 때 한 번 정한다: 열린 사이클이 있으면 coil, 없으면 depth. 이후에 케이스북이 생겨도 보고 있던 영역은 바뀌지 않는다 |
| 스킬 | 고른 영역의 스킬 버튼. 이모지와 이름, 사람 아이콘은 사용자 호출 전용, 눈 아이콘은 읽기 전용. 마우스를 올리면 하는 일·범위·호출자가 나온다. 누르면 `/스킬 <입력창에 적은 대상>`이 다음 요청으로 나간다(비어 있으면 스킬만, 여러 줄은 한 줄로 합친다). 실행 중이면 대기열로 들어간다. 전송이 거절되면(대기열 가득 참 등) 적어 둔 글은 그대로 남는다 |
| 케이스북 (coil) | 워크스페이스의 최신 `.re0/iteration/` 폴더 이름, 무게(full·lightweight), 파일 버튼(DESIGN·WORKFLOW·EVIDENCE·RETRO·REF-…). 파일을 누르면 기본 앱으로 연다. 실행이 끝날 때마다 다시 읽는다. 최근에 수정된 폴더 24개만 살피고, 심볼릭 링크 폴더는 따라가지 않는다 |
| 추천 | coil에서는 케이스북 상태로 다음 스킬을 강조한다: 사이클 없음 → `re0-plan`, 진행 중 → `re0-loop`, DESIGN과 RETRO가 모두 있으면 → `re0-work` |
| 질문 | 에이전트가 `AskUserQuestion`으로 물으면 아우로보로스와 같은 질문 패널이 입력창 자리에 나온다 |
| 준비물 | 스킬이 없으면 실행할 설치 명령을 그대로 보여주고, 누르면 터미널 실행 창에서 실행한다. 설치 여부는 `~/.claude/skills`, 워크스페이스의 `.claude/skills`, 플러그인 등록부(`paperthin@…`)에서 확인한다 |

그래프에서는 Paperthin 프롬프트로 시작한 요청 블록 제목에 `♻️ re0`처럼 스킬이 붙는다. 안내형 스타일을 쓰는 실행 창에서만 붙고, CLI 스타일 실행 창에 직접 `/re0`를 입력한 요청은 평소 제목 그대로다. Enter는 스타일과 상관없이 입력한 글을 그대로 요청한다(아우로보로스의 목표 → 인터뷰 변환은 없다).

## 범위와 한계

- 자동 허용하는 도구는 없다. Paperthin은 도구 서버가 없고, 파일 수정·명령 실행은 실행 창의 권한 모드를 따른다.
- 케이스북은 읽기만 한다. 단계(FRAME·BUILD…) 표시는 하지 않는다. 그 판단은 `nba`의 일이라 버튼으로 둔다.
- 설치 명령은 Claude Code의 전역 스킬 폴더에만 연결한다(`--agent claude-code`). 다른 에이전트에도 쓰려면 프로젝트가 안내하는 `--agent '*'`를 터미널에서 직접 실행한다. 실행 전에 패널이 명령 전문을 보여준다.
- 카탈로그(이름·요약·호출자·읽기 전용)는 앱에 내장돼 있다. Paperthin이 스킬을 추가·개명하면 `PaperthinCatalog.swift`를 갱신해야 한다(요약의 출처: Paperthin 한국어 README, MIT).
- Claude 실행 창 전용이다.

## 구현 위치

| 부분 | 파일 |
|---|---|
| 카탈로그·지도·프롬프트·설치 감지·케이스북 | `native/macos/Sources/MightyCore/PaperthinCatalog.swift` |
| 스타일 값 정규화, 요청 블록 제목 | `MightyStyles` (`OuroborosFlow.swift`) |
| 전송·케이스북 갱신·설치 터미널 | `native/macos/Sources/MightyClaude/AppStore+Ouroboros.swift` |
| 패널, 공유 질문 패널 | `PaperthinPanel.swift`, `AgentQuestionPanel.swift` |
| 테스트 | `native/macos/Tests/MightyCoreTests/PaperthinCatalogTests.swift` |
