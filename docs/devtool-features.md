# 개발 도구 기능 (Mac)

[`docs/devtool-gap-review.md`](devtool-gap-review.md)에서 고른 항목을 구현한 기록이다. 항목마다 모델 규칙은 `MightyCore`에, 화면은 `MightyClaude`에 둔다. Windows는 아직 따라가지 않았다.

## 1. 요청 되돌리기

- 모델: `MightyCore/RunRevert.swift`, 검사: `Tests/MightyCoreTests/RunRevertTests.swift`.
- 화면: 마이티 보기의 결과 블록 머리에 검토 단추(`doc.text.magnifyingglass`)가 붙는다. `FileOriginalStore`가 그 요청이 바꾼 파일의 원본을 하나라도 갖고 있을 때만 보인다. 누르면 `RunChangesSheet`가 열린다.
- 되돌리기는 `FileOriginalStore`가 보관한 "요청이 처음 바꾸기 전 내용"을 파일에 다시 쓴다. 변경사항 보기가 기준으로 쓰는 바로 그 내용이다.
- 거부 규칙(한 파일의 거부가 다른 파일을 막지 않는다):
  - `laterRun`: 같은 창의 더 뒤 요청도 그 파일을 바꿨다. 앞 요청을 되돌리면 뒤 요청의 일까지 사라지므로 뒤 요청부터 되돌려야 한다.
  - `missing`: 파일이 없어졌거나 일반 파일이 아니거나 폴더 밖을 가리킨다.
  - `unreadable`: 지금 파일이 1 MiB를 넘거나 바이너리라 diff로 읽을 수 없다.
  - `unchanged`: 이미 원본과 같다.
- 요청이 새로 만든 파일은 지우지 않고 휴지통으로 옮긴다.
- 다시 쓸 때 지금 파일의 인코딩(BOM 포함, CP949·UTF-16도)과 권한을 그대로 둔다.
- 확인 창은 "요청 뒤에 직접 고친 내용도 함께 사라진다"고 알린다. 각 줄에 되돌리면 돌아올 줄 수와 빠질 줄 수를 보여 준다.

## 2. 변경 리뷰 화면과 줄 의견

- 모델: `MightyCore/ReviewNotes.swift`, 검사: `Tests/MightyCoreTests/ReviewNotesTests.swift`.
- 화면: 1번의 `RunChangesSheet`를 검토 화면으로 넓혔다. 왼쪽은 요청이 바꾼 파일 목록(되돌리기 체크 포함), 오른쪽은 고른 파일의 diff(`FileChanges.read`, 기준은 요청 전 원본)다. diff 뷰(`FileDiffView`)는 `onLine`을 줄 때만 줄을 누를 수 있고, 다른 곳(참조 말풍선·파일 창)은 그대로 텍스트 선택만 한다.
- 줄을 누르면 그 줄에 의견을 쓴다. 의견이 달린 줄은 왼쪽 가장자리에 표시된다. 지운 줄에는 "요청 전 n행"으로, 나머지 줄에는 `path:line`으로 자리를 남긴다.
- **의견 n개 입력창에 넣기**는 의견을 파일·줄 순서로 정렬한 후속 요청 한 덩어리로 만들어 창의 입력창 뒤에 붙이고 검토 화면을 닫는다. 바로 보내지 않으므로 사용자가 고친 뒤 보낸다.
- 되돌리기를 하면 목록과 diff를 다시 읽는다. 기본으로는 아무 파일도 되돌리기로 체크하지 않는다(검토가 주 목적).

## 3. 커밋·PR

- 모델: `MightyCore/GitWorkbench.swift`, 검사: `Tests/MightyCoreTests/GitWorkbenchTests.swift`(파서와 임시 저장소에서의 실제 커밋).
- 화면: 워크스페이스 머리의 git 배지를 누르면 `GitWorkbenchSheet`가 열린다(원격 워크스페이스는 배지가 없어 쓸 수 없다).
- 바뀐 파일 목록은 `git status --porcelain=v2 -z`로 읽는다(이름에 공백·한글이 있어도 그대로). 충돌 파일은 처음에 체크하지 않는다.
- **커밋**은 고른 파일만 넣는다: `git add -A -- <파일>` 뒤 `git commit --only -- <파일>`. 이미 스테이징돼 있던 다른 파일은 스테이징된 채로 남고 이 커밋에 들어가지 않는다. 저장소의 hook은 그대로 돈다. git은 `GitTool.environment()`로 실행해 사용자의 설정·자격 증명을 쓰고, 프롬프트는 절대 띄우지 않는다.
- **Push**는 upstream이 없으면 `push -u origin HEAD`.
- **에이전트에게 맡기기**는 "고른 파일을 검토하고 메시지를 써서 이 파일만 커밋해 달라"는 요청을 그 워크스페이스에서 마지막으로 쓴 에이전트 창(자식 창 제외)의 입력창에 넣는다. 보내지는 않는다.
- PR 줄: `origin` 주소에 `gitlab`이 들어 있으면 `glab`, 아니면 `gh`를 로그인 셸 PATH에서 찾는다. 있으면 브랜치의 PR/MR 번호·상태·검사 결과(통과/진행 중/실패)를 보이고, 없으면 **Push하고 PR 만들기**(`gh pr create --fill`, `glab mr create --fill --yes`)를 보인다. CLI가 없으면 그 사실만 알린다.
- 검사가 실패한 GitHub PR에는 **실패 고쳐 달라고 하기**가 붙는다. 브랜치의 마지막 Actions 실행의 `gh run view --log-failed` 끝 80줄(ANSI 색 코드 제거)을 붙인 요청을 같은 방식으로 입력창에 넣는다.

## 4. 비교 실행

- 모델: `MightyCore/CompareRun.swift`, 검사: `Tests/MightyCoreTests/CompareRunTests.swift`.
- 시작: 쉬고 있는 창은 보내기 단추의 우클릭 메뉴, 실행 중인 창은 worktree 단추의 메뉴에 **비교 실행: Claude · Codex …**가 붙는다. 지금 자식을 만들 수 있는 에이전트가 둘 이상일 때만 보인다(설치·로그인·모드, 그리고 사람이 연 자식 3개 한도 안에서 남은 자리만큼). 창 자신의 에이전트가 먼저 선다.
- 누르면 입력창의 요청이 에이전트마다 `runInNewWorktree`로 새 worktree의 자식 창에서 동시에 실행된다. 자식 하나가 실패하면 기존 worktree 메뉴와 같은 규칙으로 그 이유가 창에 남는다. 이 동작을 위해 worktree 준비 확인(`refreshWorktreeReadiness`)을 창이 쉴 때도 한 번 한다.
- **비교 결과 보기**(같은 메뉴): 같은 요청 글로 사람이 연 자식이 둘 이상이면 가장 최근 묶음을 나란히 보인다. 열마다 에이전트, 상태, 부모 브랜치 대비 바꾼 파일 수와 +/− 줄(`git diff --shortstat <부모 브랜치>...mighty/<id>`), CLI가 알려 준 비용, 그리고 **창 보기 · 병합 · 폐기…**(사이드바 카드와 같은 동작). 병합은 보고를 마친 자식만 된다.

## 5. 대화 검색

- 모델: `MightyCore/ConversationSearch.swift`, 검사: `Tests/MightyCoreTests/ConversationSearchTests.swift`.
- 여는 곳: 워크스페이스 머리의 돋보기 단추, 메뉴 **워크스페이스 → 대화 검색**(⇧⌘F). 원격 워크스페이스에서는 꺼진다.
- 대상은 세션 이어가기 목록과 같다(`ResumableSessions`: 이 폴더의 Claude·Codex·Antigravity 세션, 60일 이내, 지금 창에 열린 세션 제외, 자동 실행 제외). 색인을 만들지 않고 검색할 때마다 세션 파일을 읽는다(파일당 마지막 24 MiB). 입력을 멈추고 250ms 뒤에 찾고, 다시 치면 앞의 검색을 취소한다.
- 요청과 답 메시지만 본다: Claude `message.content`의 text 조각, Codex `response_item`의 `message`와 `user_message`/`agent_message` 이벤트, 그 밖의 `role`+`content|parts|text` 줄. 도구 호출과 결과(`tool_result`, `function_call_output`)는 보지 않는다.
- 모든 단어(대소문자 무시)가 한 메시지에 들어 있어야 맞다. 결과는 최신 세션부터 최대 50개, 첫 맞은 메시지의 앞뒤 60자와 맞은 메시지 수를 보인다.
- **이어가기**는 세션 이어가기와 같은 `resumeSession`으로 새 창을 연다. **Markdown으로 저장…**은 세션을 화자 제목 + 본문으로 저장한다(Codex가 같은 말을 두 번 남긴 줄은 한 번만).

## 6. 저장한 요청

- 모델: `MightyCore/SavedPrompts.swift`, 검사: `Tests/MightyCoreTests/SavedPromptsTests.swift`. 파일: `<데이터 폴더>/saved-prompts.json`(앱이 열릴 때 읽는다).
- 쓰기: 입력창에서 `/prompt`를 치면 저장한 요청이 `/prompt:<이름>`으로 슬래시 목록에 함께 뜬다. 고르면 그 글이 자리표시자를 채운 채 입력창에 들어간다(보내지는 않는다).
- 자리표시자: `{branch}` 지금 브랜치(로컬 워크스페이스, 글에 있을 때만 git을 읽는다), `{workspace}` 워크스페이스 이름, `{date}` 오늘(ISO 날짜), `{clipboard}` 클립보드 글. 그 밖의 `{말}`은 그대로 둔다.
- 관리: 새 내장 명령 `/prompts`(Claude·Codex·Antigravity 공통)가 **저장한 요청** 창을 연다. 열 때 입력창의 글이 새 요청 칸에 미리 들어간다. 이름은 글자·숫자·`-`·`_`만(공백은 `-`, 40자). 범위는 모든 워크스페이스 또는 이 워크스페이스이고, 같은 이름이면 워크스페이스 것이 우선한다.
- 휴대폰 명령 목록에는 `/prompts`와 저장한 요청을 보내지 않는다(휴대폰은 이름을 CLI 스킬로 보낼 것이므로). 휴대폰에서 쓰려면 프로토콜에 별도 항목이 필요하다.

## 7. 사용량 집계

- 모델: `MightyCore/UsageLedger.swift`, 검사: `Tests/MightyCoreTests/UsageLedgerTests.swift`. 파일: `<데이터 폴더>/usage-ledger.json`(120일까지).
- 기록: 에이전트 창의 실행이 끝날 때(`completed`·`stopped`·`error`) 그 창의 `sessionUsage`를 한 번 읽어 날짜·워크스페이스·에이전트·모델별로 비용과 토큰을 남긴다. CLI가 `run` 범위로 알리면 그 값을, `session` 범위(세션 누계)로 알리면 같은 세션의 직전 기록보다 늘어난 만큼만 센다. 누계가 줄면 새 세션으로 보고 전부 센다. CLI가 알려 주지 않은 값은 0이다.
- 보기: 메뉴 **워크스페이스 → 사용량 보기**. 7일/30일, 총 비용·토큰·실행 수, 날짜별 비용 막대(하나의 계열이라 범례 없음, 마우스를 올리면 그날의 값), 워크스페이스·모델·에이전트별 표.
- 하루 예산: 같은 창에서 정한다(`UserDefaults` `usage.dailyBudgetUSD`). 그날 비용이 예산을 처음 넘는 실행에서 macOS 알림을 한 번 보낸다(알림 권한이 있을 때). 휴대폰 푸시는 아직 보내지 않는다.

## 8. 에이전트 맥락

- 모델: `MightyCore/AgentContext.swift`, 검사: `Tests/MightyCoreTests/AgentContextTests.swift`.
- 여는 곳: 워크스페이스 머리의 뇌 단추, 메뉴 **워크스페이스 → 에이전트 맥락**. 원격 워크스페이스에서는 꺼진다.
- 지침 파일: 워크스페이스 폴더의 `CLAUDE.md`(Claude), `AGENTS.md`(Codex), `GEMINI.md`(Antigravity)를 탭으로 보고 고쳐 저장한다(512 KiB까지, 원자적 쓰기). 있는 파일들의 내용이 서로 다르면 경고가 뜨고, **다른 파일에 복사**로 지금 파일 내용을 다른 에이전트의 파일에 덮어쓴다(확인 후, 저장된 내용만).
- MCP 서버(읽기만): Claude `.mcp.json`·`~/.claude.json`(이 프로젝트 항목과 전체), Codex `~/.codex/config.toml`의 `[mcp_servers.*]`, Antigravity `.gemini/settings.json`·`~/.gemini/settings.json`. 이름, 범위, 실행 명령이나 주소, 출처 파일을 보인다.
- Claude hook(읽기만): `.claude/settings.local.json`, `.claude/settings.json`, `~/.claude/settings.json`의 `hooks`를 이벤트별로 명령과 함께 보인다.
- 서버·hook을 켜고 끄는 기능은 넣지 않았다. CLI 설정 파일을 앱이 고치는 것은 CLI 버전마다 형식이 달라 위험하다.

## 9. 작업 실행과 열린 포트

- 모델: `MightyCore/ProjectTasks.swift`, 검사: `Tests/MightyCoreTests/ProjectTasksTests.swift`.
- 여는 곳: 워크스페이스 머리의 재생 단추, 메뉴 **워크스페이스 → 작업 실행**(⇧⌘R). 원격 워크스페이스에서는 꺼진다.
- 명령 찾기: `package.json` scripts(잠금 파일로 npm·pnpm·yarn·bun 선택, 파일 순서 유지), `Makefile` 대상(변수·패턴 규칙·`.` 특수 대상 제외), `justfile` 레시피, `Package.swift`(swift build/test), `Cargo.toml`(cargo build/test/run), `go.mod`(go build/test ./...). 이름은 셸 한 단어로 감싼다.
- 누르면 새 터미널 창을 열고 명령을 **입력만** 해 둔다(`TerminalInput.autoRun = false`). 프로젝트 파일에서 온 글이라 앱이 Enter를 대신 누르지 않는다는 기존 규칙(§1.5)을 따른다.
- 열린 포트: `lsof -nP -iTCP -sTCP:LISTEN -F pcn`으로 내 프로세스가 듣는 1024번 이상 포트를 보인다. 누르면 그 워크스페이스의 가장 최근 에이전트 창 옆 웹 열기 규칙(브라우저 창 또는 시스템 브라우저)으로, 에이전트 창이 없으면 기본 브라우저로 `http://localhost:<포트>`를 연다.
- 실패한 명령의 출력을 에이전트에게 보내는 기능은 아직 없다(터미널 출력을 읽는 경로가 필요하다).

## 10. OS 연결 (링크·셸 명령·웹훅)

- 모델: `MightyCore/OSIntegration.swift`, 검사: `Tests/MightyCoreTests/OSIntegrationTests.swift`.
- **링크** `mightyclaude://open?path=<절대 경로>&prompt=<글>&agent=<claude|codex|gemini>`: `scripts/build-macos.sh`가 Info.plist에 URL 스킴을 등록한다. 이미 워크스페이스인 폴더면 고르고, 아니면 **확인 창을 띄운 뒤에만** 워크스페이스로 더한다. `prompt`는 그 워크스페이스의 가장 최근 에이전트 창(또는 `agent`의 창, 없으면 새 창) 입력창에 넣기만 하고 **절대 보내지 않는다**. 웹 페이지도 링크를 열 수 있기 때문이다. 앱이 링크로 처음 켜졌으면 저장된 상태를 읽을 때까지(최대 10초) 기다린다.
- **셸 명령** `mighty`: 메뉴 **워크스페이스 → 셸 명령 'mighty' 설치**가 `~/.local/bin/mighty`를 쓴다(관리자 권한 없음). `mighty`(지금 폴더), `mighty <폴더>`, `mighty "요청"`은 위 링크를 `open`한다(인코딩은 `osascript` JavaScript). 그 폴더가 로그인 셸 PATH에 없으면 더하는 방법을 알려 준다.
- **웹훅**: 설정 → 펫과 알림의 Mac 알림 아래에 웹훅 주소 칸이 있다(`UserDefaults` `notifications.webhookURL`). 요청이 끝나면 Mac 알림과 같은 문구(워크스페이스·창 이름)만 `{"text","content"}` JSON으로 POST한다(Slack·Discord 모두 읽는다). https만, 또는 이 Mac의 http만 허용한다.
- macOS 단축어(App Intents)와 Finder 빠른 동작은 넣지 않았다. SwiftPM으로 만드는 지금 빌드에서는 App Intents 메타데이터를 만들 수 없다.

## 11. 신뢰와 진단

- 모델: `MightyCore/TrustTools.swift`(`SecretScan`·`RunAudit`·`DiagnosticsSummary`), 검사: `Tests/MightyCoreTests/TrustToolsTests.swift`.
- **커밋 전 비밀값 검사**: 커밋·PR 창(3번)의 **커밋**은 먼저 고른 파일이 더할 줄(추적 파일은 `git diff HEAD -U0`의 `+` 줄, 새 파일은 1 MiB까지 전체)에서 AWS·GitHub·GitLab·Slack·Anthropic·OpenAI·Google·Stripe 키 모양과 개인 키 블록을, 그리고 `.env*`·`*.pem` 같은 이름(`ChildAutoCommit.looksSecret`)을 찾는다. 하나라도 있으면 파일·줄·종류(값은 보이지 않는다)를 보이고 **그래도 커밋**을 눌러야 커밋한다.
- **이 요청이 한 일**: 마이티 보기의 결과 블록 머리에 목록 단추가 붙는다(도구를 쓴 요청만). 요청과 하위 에이전트의 도구 활동을 실행한 명령·바꾼 파일·웹·읽은 파일·그 밖으로 나눠 한 줄씩(같은 활동은 마지막 상태로 한 번) 보이고, 실패한 것을 표시하며, 글로 복사할 수 있다.
- **진단 정보 내보내기…**(메뉴 워크스페이스): 앱 버전·빌드, macOS·로캘·아키텍처, CLI별 설치 상태와 안내 글, 워크스페이스·창 수, 기능 켜짐 여부, 마지막 오류를 JSON으로 저장한다. 요청·출력·워크스페이스 경로·토큰은 넣지 않고 홈 폴더는 `~`로 바꾼다.
