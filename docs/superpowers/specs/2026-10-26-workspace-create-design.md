# 워크스페이스 추가: 새 폴더 만들기 · Git으로 생성

2026-10-26 · 사용자 결정 (brainstorming)

## 목표
지금 워크스페이스는 "기존 폴더 열기" 하나로만 추가된다. 두 방법을 더한다.
1. **새 폴더 만들기**: 이름과 위치를 정해 폴더를 만들고(원하면 `git init`) 그 폴더를 워크스페이스로 추가한다.
2. **Git으로 생성**: Git 주소를 받아 지금 컴퓨터의 자격 증명으로 접근되는지 확인한 뒤, 정한 위치에 내려받고(`git clone`) 그 폴더를 워크스페이스로 추가한다. 모양은 Fork의 Clone 창을 따르되 계정 칸은 없다.

## 사용자가 정한 것
- 맥 + 윈도우(웹 화면과 WinUI 대체 화면). 폰은 바꾸지 않는다.
- 지금 "폴더 열기"가 있는 모든 곳에 세 방법을 둔다.
- 새 폴더 창: 이름 + 위치 + "Git 저장소로 시작(git init)" 체크.
- 위치 기본값: 마지막에 쓴 위치. 처음에는 지금 워크스페이스의 상위 폴더, 없으면 홈 폴더.
- Git으로 생성의 폴더 이름: URL에서 자동으로 채우고 고칠 수 있다.
- 접근 확인: URL 입력이 멈추면 자동으로 확인하고, 성공해야 생성 버튼이 켜진다.
- 이름은 "복제"가 아니라 **"Git으로 생성"**.

## 진입점 (맥·윈도우)
| 곳 | 지금 | 바뀐 뒤 |
|---|---|---|
| 사이드바 머리글 + 버튼 | 바로 폴더 고르기 | 세 항목 메뉴 |
| 사이드바 빈 화면 | "폴더 열기" 버튼 | 세 버튼 |
| 파일 메뉴 | 프로젝트 폴더 열기… ⌘O | 그 아래 "새 폴더 만들기…", "Git으로 생성…" |
| 시작 화면(워크스페이스 없음) | 프로젝트 폴더 열기 | 세 버튼 (기존 것이 가장 앞) |
| 창 추가 메뉴 | 프로젝트 폴더 열기… | 세 항목 |

- 세 항목 이름: "기존 폴더 열기…"(⌘O / Ctrl+O 그대로), "새 폴더 만들기…", "Git으로 생성…". 새 두 항목에는 단축키를 두지 않는다.
- 윈도우 창 추가 메뉴는 Core `AddPaneMenu` 항목 두 개(`newFolder`, `fromGit`)를 더해 웹과 WinUI가 같이 쓴다.

## 새 폴더 만들기 창
- 칸: 폴더 이름, 위치(경로 글자 + 폴더 아이콘 → 시스템 폴더 고르기), ☐ Git 저장소로 시작, 만들어질 경로 미리보기, 취소 / 만들기.
- 만들기 버튼이 잠기는 경우(이유를 칸 아래에 보임):
  - 이름이 비었거나 공백뿐
  - 이름에 `/` `\` `:` 또는 제어 문자, 이름이 `.`·`..`, 윈도우 예약 이름(CON, NUL, COM1 …)이나 `<>"|?*`(윈도우), 끝이 점·공백(윈도우)
  - 위치가 없거나 폴더가 아님
  - 같은 경로에 파일·폴더가 이미 있음
- 만들기: 폴더를 만들고(상위 폴더는 이미 있어야 함), 체크했으면 `git init`(실패하면 폴더는 남기고 워크스페이스는 추가하되 "git init 실패" 안내), 그다음 기존 추가 흐름(승인 → 추가 → 선택 → Claude 창).

## Git으로 생성 창
- 제목 "Git으로 생성", 설명 "Git 저장소를 내려받아 새 워크스페이스를 만듭니다".
- 칸: URL, "연결 확인" 링크, 위치(폴더 아이콘), 폴더 이름(자동 + 고침), 만들어질 경로, 취소 / 생성.
- **URL → 폴더 이름**(공통 규칙): 끝의 `/`와 `.git`을 떼고 마지막 경로 조각. 지원 형식 `https://host/owner/repo(.git)`, `http://…`, `ssh://[user@]host[:port]/owner/repo(.git)`, `user@host:owner/repo(.git)`(scp 형식), `git://…`, `file:///…`, 로컬 절대 경로. 사용자가 이름 칸을 직접 고친 뒤에는 URL이 바뀌어도 덮어쓰지 않는다.
- **자동 확인**: URL 입력이 0.6초 멈추면 `git ls-remote --heads <url>`(시간 제한 20초)로 확인한다. 진행 중 "확인 중…", 결과 "✓ 접근할 수 있습니다" 또는 "✕ 이유". 새 입력이 오면 진행 중인 확인을 버린다. "연결 확인" 링크는 다시 확인.
- **오류 분류**(공통 규칙, stderr와 종료 코드로):
  | 분류 | 예 | 문구 |
  |---|---|---|
  | `gitMissing` | git 실행 파일 없음 | Git이 없습니다 · "필요한 도구" 열기 버튼 |
  | `invalidURL` | 형식이 아님 | 올바른 Git 주소가 아닙니다 |
  | `auth` | `Authentication failed`, `could not read Username`, `Permission denied (publickey)`, HTTP 401/403 | 이 컴퓨터의 자격 증명으로 접근할 수 없습니다 |
  | `notFound` | `Repository not found`, `not found`, HTTP 404, `does not appear to be a git repository` | 저장소를 찾을 수 없습니다 |
  | `network` | `Could not resolve host`, `Connection refused`, `timed out`, `Network is unreachable`, 시간 초과 | 서버에 연결할 수 없습니다 |
  | `other` | 그 밖 | git 출력 마지막 줄 |
- **생성 버튼**은 확인 성공 + 대상 경로가 없음 + 폴더 이름 규칙 통과일 때만 켜진다.
- **생성 중**: 칸을 잠그고 진행 막대와 단계 글자(`git clone --progress`의 stderr: 받는 중 42% / 압축 푸는 중 / 파일 쓰는 중). 취소 버튼은 git을 멈추고 만들어진 대상 폴더를 지운다. 실패도 대상 폴더를 지우고 위 분류로 오류를 보여 주며 창은 그대로 남는다.
- **성공**: 기존 추가 흐름(승인 → 추가 → 선택 → Claude 창), 창 닫힘.
- **자격 증명**: 지금 컴퓨터의 것을 그대로 쓴다(git credential helper·키체인/자격 증명 관리자, ssh-agent, gh/glab helper). 묻는 창이 뜨지 않게 `GIT_TERMINAL_PROMPT=0`, `GCM_INTERACTIVE=never`, `GIT_SSH_COMMAND="ssh -o BatchMode=yes"`(사용자 `GIT_SSH_COMMAND`/`core.sshCommand`가 없을 때)로 돌린다. 앱이 자격 증명을 저장하거나 묻지 않는다.
- 브랜치는 원격 기본 브랜치, 서브모듈·얕은 복제 없음.

## 공통 값과 규칙
- `native/contracts/fixtures/workspace-create.json`: URL → 폴더 이름 사례, 폴더 이름 검사 사례(맥/윈도우 차이 표시), stderr → 오류 분류 사례. 맥 MightyCore 테스트와 윈도우 Core 검사가 같은 파일을 읽는다.
- 위치 기억: 앱 상태(스냅샷)에 `lastCreateLocation` 하나(옵셔널, 옛 파일과 호환). 새 폴더·Git으로 생성이 성공할 때 저장한다.
- 창이 열려 있는 동안은 다른 모달처럼 막힘(맥 `hasBlockingModal`에 포함).

## 플랫폼
- **맥**
  - MightyCore: `WorkspaceCreate`(이름 규칙·URL→이름·경로 미리보기·폴더 만들기·git init), `GitRemoteProbe`(ls-remote, 분류), `GitCloneJob`(NativeChildProcess로 `git clone --progress`, stderr 진행률 파싱, 취소·정리). git 찾기는 `DelegationGit`과 같은 경로 목록 + 로그인 셸 PATH.
  - 앱: `NewFolderSheet`, `GitCreateSheet`(RenameSheet와 같은 시트 모양), `AppStore` 상태와 함수, 메뉴·사이드바·시작 화면·창 추가 메뉴 항목.
- **윈도우**
  - Core: 같은 이름 규칙·분류(`WorkspaceCreate.cs`, `GitRemoteProbe.cs`, `GitCloneJob.cs`), `DelegationGit.Executable` 재사용, `AddPaneMenu` 두 항목, `DesktopService`에 위치 기억.
  - 웹: `dialogs`에 두 창, 사이드바 메뉴·빈 화면·대시보드·창 추가 메뉴, 브리지 메서드(`createFolder`, `gitProbe`, `gitCreate`, `gitCreateCancel`, `pickLocation`)와 진행 알림.
  - WinUI 대체 화면: 같은 두 대화 상자.
- **문구**: `locales/{ko,en,zh,ja}.json` + 사본, 하드코딩 한국어 없음.
- **도움말**: 4개 언어 `getting-started.md`의 "워크스페이스 열기" 절을 세 방법으로, `layout.md`·`windows.md`의 메뉴 줄 갱신.

## 테스트
- 공통 픽스처 기반 순수 함수 테스트(맥 MightyCore, 윈도우 Core): URL→이름, 이름 검사, 오류 분류, 진행률 파싱.
- 로컬 bare 저장소(`git init --bare` + 커밋)로 ls-remote 성공·없는 경로 실패, clone 성공, 취소 시 폴더 정리, 이미 있는 대상 거부. 새 폴더 + git init.
- 웹 unit(vitest): 창의 버튼 잠금 규칙, 이름 자동 채움과 사용자 수정 유지.
- 빌드·테스트 실행은 사용자가 요청할 때만(2026-10-23 규칙). 텍스트 검사(locales, design-tokens, help, style-freeze)는 돌린다.

## 범위 밖
폰, 원격(Tailscale) 워크스페이스로 만들기, 브랜치 선택, 서브모듈, 얕은 복제, 계정 선택·자격 증명 입력, 새로 만든 워크스페이스를 원격 공유 목록에 자동 추가.
