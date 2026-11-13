# Tailscale 원격 워크스페이스

Mac이나 Windows 컴퓨터 두 대의 Mighty Code를 Tailscale로 이으면, 한쪽(클라이언트)에서 다른 쪽(호스트)의 워크스페이스를 열고 호스트에서 Claude Code·Codex·Antigravity와 명령을 실행한다. 파일과 CLI 로그인은 호스트에 그대로 있고, 프로젝트 파일을 복사하거나 동기화하지 않는다. 사용 안내는 도움말 [원격 연결 (Tailscale)](help/content/ko/remote.md)에 있다.

연결하면 호스트가 공유한 워크스페이스가 호스트에 열려 있는 에이전트 창 그대로 클라이언트 사이드바에 나타나고, 클라이언트에서 그 창을 계속 쓴다(아래 [그 컴퓨터의 창 그대로](#그-컴퓨터의-창-그대로-원격-미러), 2026-11-13 Mac·Windows). 그 전부터 있던, 클라이언트가 가진 창을 호스트에서 실행만 하는 방식([실행](#실행))도 그대로 쓴다.

2026-09-28 `e4f973b`에서 지웠다가 2026-10-17에 Mac에 다시 만들었고, Windows 앱에도 같은 프로토콜로 만들었다(아래 [Windows와 Mac↔Windows](#windows와-macwindows), [windows-parity.md](windows-parity.md)). 휴대폰은 릴레이로 Mac에 붙는 별도 기능이고([mobile-remote.md](mobile-remote.md)), 원격 워크스페이스를 휴대폰에 비추지 않는다.

## 화면

**설정 → 원격 연결 (Tailscale)** 탭(`RemoteSettingsView.swift`, 이 절은 Mac 기준이고 Windows의 같은 탭은 아래 [Windows 절](#windows와-macwindows)에 있다). 사이드바 워크스페이스 머리글의 네트워크 단추, 워크스페이스의 **창 추가** 메뉴의 **다른 컴퓨터 연결…**, 첫 화면의 같은 단추가 이 탭을 연다.

- **이 컴퓨터 공유**: 공유할 로컬 워크스페이스를 고르고(기본은 앞에 있는 워크스페이스), 포트(기본 `43137`)를 정해 **공유 시작**. 공유 중에는 이 Mac의 Tailscale 주소(`주소:포트`)마다 한 줄과 연결 키가 **복사** 단추와 함께 보이고, 이 Mac에서 도는 원격 실행 수가 보인다. 키의 **복사**는 클립보드 항목을 숨김(Concealed)·임시(Transient)로 표시해 클립보드 관리 앱이 보이거나 남기지 않게 하고, 60초 뒤에도 클립보드에 그 키가 있으면 지운다. 공유 중에는 **원격에서 전체 권한·명령 실행 허용** 스위치가 있다. 꺼져 있으면(기본, 공유를 시작할 때마다 꺼짐) 호스트는 원격 명령 창(kind `shell`)과 전체 권한 실행(Claude bypassPermissions, Codex danger-full-access, Antigravity 전체 권한)을 실행 전에 403으로 거절하고, 클라이언트 창에 이유가 보인다. **공유 중지**는 원격 실행을 멈추고 키를 폐기한다. 앱을 열 때 공유는 항상 꺼져 있다.
- **다른 컴퓨터 연결**: 주소(Tailscale IP, `[IPv6]`, MagicDNS 이름; 포트와 `http://`는 생략 가능)와 키, 이름(비우면 호스트 이름)을 넣고 **연결**. 같은 주소로 새 키를 넣으면 저장된 연결의 키를 바꾼다.
- **연결한 컴퓨터**: 연결마다 상태 점, 주소, 상태 문장, **새로고침**·**연결 해제**·지우기(키도 지운다: Mac은 Keychain, Windows는 아래 절). 연결된 컴퓨터 아래에 공유 워크스페이스와 단추가 있다. **보기**(Mac·Windows): 미러를 시작하고(돌고 있으면 깨우고) 그 워크스페이스를 고른다([원격 미러](#그-컴퓨터의-창-그대로-원격-미러)). 호스트가 미러 목록에 404로 답하면(미러가 없는 이전 버전) 단추가 예전의 **열기**로 돌아온다. **열기**는 그 워크스페이스를 사이드바에 원격 워크스페이스로 넣고 Claude 창 하나를 연다.
- **원격 워크스페이스에서 되는 것과 안 되는 것**: 아래 범위를 화면에서 그대로 말한다.

원격 워크스페이스는 사이드바에서 컴퓨터 아이콘과 호스트 이름, 워크스페이스 머리글의 호스트 배지(연결 여부에 따라 강조색·회색), 아래 상태 막대의 "{호스트}에서 실행 · 연결됨/연결 안 됨"으로 구분한다. 세션 정보에는 **실행 컴퓨터** 줄이, 실행 설정에는 "이 창은 {호스트}에서 그 컴퓨터의 CLI와 계정으로 실행됩니다" 줄이 붙는다. 빈 원격 창은 같은 내용과 쓸 수 없는 기능을 안내한다.

## 실행

- 원격 창의 요청은 호스트의 `ProcessRunner`가 호스트의 CLI·계정·환경으로 실행한다. 모델, 권한 모드(계획 모드와 Claude Auto, Codex 승인 요청 포함), 사고 강도, Fast·웹 검색·네트워크 설정, 재개 ID, 첨부, 설정 → 모델의 페이즈 모델이 그대로 넘어간다. 호스트 CLI가 없거나 지원하지 않는 설정은 클라이언트가 호스트의 `/info` 응답으로 먼저 거르고, 호스트도 실행 전에 다시 확인해 거절한다.
- 호스트가 CLI 출력을 같은 파서(`CLIStreamParser`, Codex 세션 기록과 Antigravity 하위 에이전트 감시 포함)로 읽어 만든 실행 이벤트를 클라이언트가 커서로 받아 자기 창의 이벤트로 적용한다. 그래서 기본 보기와 마이티 보기(흐름·기록·파이프라인)가 로컬 실행과 같은 경로로 그려진다. CLI의 원시 줄 대신 파싱된 이벤트를 보내는 이유는, 파서가 호스트에만 있는 파일(Codex 세션 기록, Antigravity 하위 에이전트 기록, mods 브리지)을 함께 읽기 때문이다.
- 권한·질문·계획 요청은 클라이언트의 같은 카드로 뜨고, 답은 호스트 실행으로 전해진다. 실행 중에 보낸 요청은 클라이언트의 대기열에 들어가 지금 실행이 끝난 뒤 새 실행으로 호스트에 간다. 실행 중인 턴에 덧붙이는 길은 없다.
- 원격 명령 창(kind `shell`, 호스트가 **원격에서 전체 권한·명령 실행 허용**을 켰을 때만)은 명령마다 호스트의 공유 폴더에서 `$SHELL -l -c`를 새로 띄운다(Mac 호스트. Windows 호스트는 아래 절). 대화형 프로그램과 비밀번호 입력은 지원하지 않는다. 로컬 터미널 창(Ghostty PTY)은 원격에 쓰지 않는다.
- 첨부는 내용 자체를 보낸다. 최대 8개, 파일당 5 MiB, 합계 8 MiB.
- 앱을 다시 열면 원격 창과 연결 목록은 남고 연결은 "저장됨" 상태다. 원격 창에서 처음 보내거나 **새로고침**할 때 키를 읽어(Mac은 Keychain, Windows는 아래 절) 다시 연결한다.
- 호스트에서 다른 컴퓨터의 실행이 도는 동안에는 그 컴퓨터의 CLI 업데이트와 플러그인 변경을 미룬다(Windows 호스트도 같다. 아래 절).

원격 워크스페이스에서 쓰지 않는 것과 이유(모두 이 컴퓨터의 디스크·네트워크·프로세스를 읽는다):

| 기능 | 원격에서 |
| --- | --- |
| 마이티 오케스트레이션(위임, 자식 작업 트리) | 끔. 호스트 실행에는 위임 서버를 붙이지 않고, 입력창의 작업 트리 단추도 나오지 않는다 |
| 파일 창, 브라우저 창 | 만들 수 없음(메뉴에서 빠지고, 단추는 꺼짐) |
| 파일을 읽는 스타일(작업 폴더 스타일, 상태 파일, 사전 조건 검사) | 스타일 선택기가 나오지 않음. 원격 창은 자유 요청만 |
| 에이전트 터미널·웹 열기 도구(`run_in_terminal`, `open_url`) | 호스트 실행에는 창별 MCP 서버를 붙이지 않음 |
| 상태 줄, git 배지, 작업 폴더의 슬래시 명령, 이전 세션 이어가기, 이전 요청 불러오기 | 쓰지 않음(사용자 수준 슬래시 명령만) |
| 플러그인 관리, CLI 로그인 터미널 | 호스트의 Mighty Code에서 한다 |
| 그림 미리보기 | 호스트의 그림 캐시 키라 클라이언트에서는 뺀다 |

## 연결이 끊길 때

- 실행 중 요청이 실패하면(네트워크, 호스트 종료, 공유 중지) 그 연결의 실행 창은 모두 오류로 끝나고 "원격 연결이 끊겼습니다…" 줄이 남는다. 연결은 "연결 해제"와 이유를 보인다.
- 클라이언트의 폴링이 하트비트다. 호스트는 30초 동안 폴링이 없는 실행을 멈춘다.
- **연결 해제**는 그 연결의 실행을 멈추고 연결을 남긴다. **새로고침**이나 원격 창에서 보내기로 다시 연결한다.

## 그 컴퓨터의 창 그대로 (원격 미러)

연결한 컴퓨터(호스트)가 공유한 워크스페이스를, 호스트에 이미 열려 있는 에이전트 창(대화, 마이티 보기·진행 상황, 상태)과 함께 이 컴퓨터(클라이언트) 사이드바에 그대로 보여 주고, 여기서 계속 쓰게 한다. 창은 호스트의 것이다. 클라이언트에서 보낸 글은 휴대폰에서 보낸 것처럼 그 호스트 창의 다음 요청이 된다(실행 중이면 호스트의 대기열). 2026-11-13 Mac에 만들었다(`MightyCore/Remote/RemoteMirror.swift`, `RemoteHostService.serveMirror`, `RemoteClientService`의 `mirror…`, 앱의 `AppStore+RemoteMirror.swift`·`RemoteMirrorViews.swift`). 같은 날 Windows에도 같은 와이어로 만들었다([Windows 구현](#windows-구현)). 그래서 Mac 클라이언트가 Windows 호스트를, Windows 클라이언트가 Mac 호스트를 비춘다.

### 화면 (클라이언트)

- **사이드바**: 이 컴퓨터의 워크스페이스 아래에 서버마다 머리글 "원격 · {이름}"과 "주소 · 상태"(연결됨·다시 연결하는 중·연결 끊김·연결 전·저장된 연결 없음), 상태 점(초록·주황·회색)이 있고, 그 아래에 그 서버의 워크스페이스가 있다. 저장한 연결은 워크스페이스가 없어도 머리글이 보이고, 검색 중에는 맞는 워크스페이스가 없는 서버가 빠진다. 머리글의 … 메뉴는 **연결**(=새로고침)·**연결 끊기**·**원격 연결 설정…**. 머리글 도움말은 미러가 멈춘 이유나 연결의 상태 문장. 사이드바 머리글의 "워크스페이스 N"은 이 컴퓨터의 것만 센다. 이전 방식의 원격 워크스페이스도 자기 서버 묶음에 들어가고 호스트 이름 줄을 그대로 단다(미러 워크스페이스에는 그 줄이 없다).
- 미러 워크스페이스는 이름 바꾸기·목록에서 빼기가, 미러 창은 이름 바꾸기가 메뉴에 없다(이름은 호스트의 것). 창 닫기는 호스트의 그 창을 닫는다. **창 추가**의 에이전트 창은 호스트의 그 워크스페이스에 새 창을 만들고, 다음 목록에 나타나면 고른다. 명령 창은 만들 수 없다.
- **창 띠**(`MirrorPaneBanner`, 창 머리 아래): "원격 창 · ● {호스트}에서 실행 중 · {상태}", 호스트 창이 창마다 worktree에서 일하면 "브랜치 ← 기준", 호스트 대기열에 요청이 있으면 "대기 중인 요청 N개"(마우스를 올리면 글). 도움말은 무엇이 호스트로 가고 무엇을 쓸 수 없는지와 최근 80개까지 보인다는 것. **상태 막대**는 "원격 · {호스트}에서 실행 중 · {상태}".
- 미러는 연결이 될 때 저절로 시작한다(설정의 **연결**·**새로고침**, 머리글의 **연결**, 원격 창에서 보내기로 이어진 연결, **보기**). **연결 해제**는 미러를 멈추고 창은 마지막 모습으로 남는다(보내기는 "연결되어 있지 않습니다"로 막는다). 연결을 지우면 그 서버의 미러 워크스페이스와 창이 사라진다. 미러는 저장하지 않으므로 앱을 다시 열면 없다. 연결은 "저장됨"으로 시작하고, 연결하면 다시 나온다.

### 와이어 (프로토콜 2 안의 미러 버전 1)

요청은 모두 기존 `/v2/` 규칙(키, `x-mighty-remote-version: 2`, Tailscale 상대, `Origin` 없음, 초당 횟수, 상대마다 연결 수)을 그대로 거친다. 미러의 답에는 `protocol: 2`와 `mirror: 1`이 있고, 클라이언트는 다른 `mirror` 값을 거절한다. 공유 픽스처는 `native/contracts/fixtures/remote-mirror/`(`endpoints.json`이 경로·본문·답을 묶는다. 기존 `remote-v2/`는 Windows 검사가 파일 목록을 세므로 따로 둔다).

호스트는 **공유 워크스페이스의 이 컴퓨터 에이전트 창(kind `claude`)만** 보인다. 공유하지 않은 워크스페이스의 창은 id를 알아도 404, 명령 창과 이 호스트가 가진 다른 컴퓨터의 창(원격 워크스페이스·미러)은 목록에 없다.

| 경로 | 본문 | 답 |
| --- | --- | --- |
| `GET /v2/mirror[?since=<revision>]` | — | `200 {protocol, mirror, hostName, revision, workspaces:[{id,name,path}], sessions:[요약]}`. `since`가 지금 revision이면 `{…, revision, unchanged: true, workspaces: [], sessions: []}`. 쿼리는 `since` 하나(16진수 64자까지)만 |
| `GET /v2/mirror/sessions/{id}` | — | `200 {protocol, mirror, revision, session: 상세}` |
| `POST /v2/mirror/sessions/{id}/submit` | `{text, attachments?}` | `200 {protocol, outcome}`: 휴대폰 보내기의 답 단어 `started`·`steered`·`queued` |
| `POST …/{id}/stop` | `{}` | `200 {protocol, accepted: true}` |
| `POST …/{id}/permission` | `{requestId, runId, allow, always?}` | 같음 |
| `POST …/{id}/questions` | `{requestId, runId, answers}`(원격 실행의 `questions`와 같은 모양) | 같음 |
| `POST …/{id}/plan` | 원격 실행의 `RemoteWirePlan` `{runId, requestId, outcome, feedback?}` | 같음 |
| `POST …/{id}/close` | `{}` | 같음 |
| `POST /v2/mirror/workspaces/{id}/sessions` | `{provider}` | `201 {protocol, sessionId}` |

- **요약**(`RemoteMirrorSummary`): `id`, `workspaceId`, `title`(200자까지), `kind`(`claude`), `provider`(알려진 id), `model`(200자까지), `status`(`idle`·`running`·`completed`·`error`·`stopped`, 그 밖의 값은 호스트가 `idle`로 보낸다), `revision`(16진수 64자까지), `pending`(기다리는 도구·질문·계획 요청과 Antigravity 카드), `queued`(대기열 수), `branch?`(창의 worktree 브랜치).
- **상세**(`RemoteMirrorSession`): `id`, `workspaceId`, `title`, `titleMode?`, `kind`, `provider`, `model`, `status`, `createdAt`, `settings`(`RunSettings` 그대로), `agentViewMode?`, `mightyStyle?`, `logs`(최신 80개 = 휴대폰의 `MobileSessionDetail.maximumEntries`, 그림 항목은 `system`으로 바꾸고 그림 참조를 뺀다), `hasOlder`, `graphRuns`(최신 20개 = `MobileWire.mightyRuns`, 하위 에이전트의 `outputFile`과 항목의 그림을 뺀다), `todoProgress?`, `backgroundWork?`, `startedAt?`·`finishedAt?`(ISO 8601, 지금 요청의 시계), `permissions`(기다리는 것만, 최신 32개), `queued`(`[{id, text}]` 앞 16개, 글은 500자까지), `branch?`, `base?`.
- **크기**: 상세 JSON은 1.5 MiB(`RemoteProtocol.maximumPollBytes`) 안. 넘으면 오래된 그래프 실행부터 빼고(하나 남김), 다음 오래된 기록부터 빼고(하나 남김, `hasOlder: true`), 그래도 넘으면 글마다 32 KiB로 자르고, 마지막으로 그래프를 비운다(`RemoteMirrorSupport.fitted`).
- **revision**: 창의 revision은 상세가 싣는 것 가운데 바뀔 수 있는 것의 SHA-256 앞 12바이트(16진수 24자). 재료는 상태, 제목, 제공자, 모델, 권한 모드, 사고 강도, 보기 모드, 스타일, 기록 수, 브랜치, 마지막 12개 기록의 `id:글 바이트 수:활동 상태`, 마이티 그래프 digest(휴대폰의 `MobileMightySupport.digest`), 할 일 `id:status`, 백그라운드 작업 `id:status`, 실행 시계, 기다리는 요청 `id:runId:state`, 대기열 id. 목록의 revision은 워크스페이스 `id:name`과 창마다 `id:revision:title:status:pending:queued`의 같은 digest. 클라이언트는 같은지만 보므로, 다른 구현은 재료가 달라도 된다. "상세가 바뀌면 반드시 바뀐다"만 지킨다.
- **오류**: 400 잘못된 본문·쿼리·빈 글, 401 키, 403 공유하지 않은 워크스페이스에 새 창·전체 권한 창에 보내기인데 스위치 꺼짐(`remote.host.error.fullAccessOff`)·`always: true`인데 스위치 꺼짐(`remote.host.error.antigravityAlways`), 404 창 없음·공유 밖·미러 없음(앱이 아직 넘겨주지 않았거나 이전 버전 호스트), 409 앱이 거절(메시지가 이유), 415 JSON 아님, 503 공유가 멈추는 중.
- **본문 한도**: `submit`만 첨부 때문에 12 MiB(`AttachmentSupport.maximumRequestBytes`, `RemoteMirrorRoutes.isSubmit`), 나머지는 512 KiB. 글은 400,000바이트까지, NUL 없음, 첨부는 8개까지.

### 호스트 동작

- 읽기는 메인 액터에서 그 순간의 창으로 한다(`RemoteMirrorBridge` → `AppStore.mirrorSummaries`·`mirrorPane`·`mirrorPaneInfo`). 행동은 휴대폰 m1의 처리(`MobileRemoteBridge`)를 그대로 쓴다: 보내기(`mobileSubmit`, 실행 중이면 대기열), 멈추기, 권한·질문·계획 답, 새 창(`mobileCreateSession`: 워크스페이스의 worktree 모드가 묻지 않고 정한다), 닫기. 그래서 휴대폰에서 되는 것과 안 되는 것이 같다.
- 공유 워크스페이스 목록은 요청마다 저장 상태에서 읽고, 창은 앱의 지금 상태에서 읽는다. 창 128개까지.

### 클라이언트 동작

- 미러 워크스페이스는 `remote: {connectionId, workspaceId, hostName, mirror: true}`인 `Workspace`, 미러 창은 `mirror`(`RemoteMirrorReference`: `connectionId`, `hostSessionId`, `hostWorkspaceId`, `hostName`)가 붙은 `RunSession`이다. 둘 다 id는 `mirror-` + SHA-256(UTF-8 `connectionId` + `"\0"` + 호스트 id)의 앞 16바이트(16진수 32자)라서, 같은 연결의 같은 호스트 창은 늘 같은 id다.
- 저장하지 않는다: `StateRepository.save`가 `AppSnapshot.withoutMirrors()`로 미러 워크스페이스·창·그 배치·모드·활성 창·펼침과 활성 워크스페이스·창 id를 빼고 쓴다. `RunSession.mirror`는 Codable 키에도 없다. 창 128개 한도(`ownSessionCount`)는 이 컴퓨터의 창만 센다.
- 원격 워크스페이스이므로 디스크·프로세스를 읽는 기능(위 [실행](#실행)의 표)은 그대로 꺼지고, 휴대폰에도 비추지 않는다. 창 worktree 띠·병합 같은 git 일도 없다(브랜치는 띠에 글로만).
- 보내기·멈추기·권한/질문/계획 답·새 창·닫기는 위 경로로 간다. 보내기는 입력창의 글과 첨부를 비우고 보내며, 호스트가 거절하면 되돌려 놓고 오류를 보인다. 연결이 끊긴 서버의 창은 보내기 전에 막는다.
- **폴링**: 연결마다 작업 하나가 `GET /v2/mirror?since=`를 묻는다. 그 연결의 창이 실행 중이거나 답을 기다리거나, 사용자가 그 연결의 미러 워크스페이스를 보고 있으면 1초, 아니면 5초. 워크스페이스·창 선택, 보내기, 답, 멈추기, 새 창, 닫기가 기다림을 바로 깨운다. 연결이 끊기면 멈춘다. 상세는 보고 있는 워크스페이스의 창과 답을 기다리는 창 가운데 요약의 revision이 마지막으로 읽은 상세의 revision과 다른 것만, 한 번에 16개까지 읽는다.
- 목록을 받으면 미러 워크스페이스·창을 한 번에 만들고 고치고 지운 뒤 배치를 맞춘다. 상세를 받으면 제목·제공자·모델·상태·설정·스타일·기록·그래프·할 일·백그라운드 작업·실행 시계를 덮어쓰고(보기 모드는 호스트가 보냈을 때만), 기다리는 요청을 그 창의 권한 카드로 둔다.
- **실패**: 429·503·시간 초과·연결 끊김은 10초 동안 1초마다 다시 묻고 머리글에 "다시 연결하는 중". 그래도 안 되면 연결을 끊긴 것으로 바꾸고 미러를 멈춘다. 401이면 연결이 끊긴 것으로 바꾼다(키가 바뀜). 호스트가 답은 하는데 거절하면(이전 버전 호스트의 404 등) 연결은 그대로 두고 미러만 멈추고 머리글 도움말에 이유를 둔다. 404면 설정의 단추가 **열기**로 돌아와 그 호스트에는 이전 방식의 원격 창을 계속 쓴다. 상세가 404·409면(목록과 상세 사이에 닫힘) 그 창만 건너뛰고 다음에 목록 전체를 다시 읽는다.

### 지금 안 되는 것

- 명령 창은 비추지 않고 미러 워크스페이스에 만들 수 없다. Antigravity 권한 카드는 `pending`에만 세고 카드로 오지 않는다(호스트나 휴대폰에서 답한다).
- 이름 바꾸기, 모델·권한 모드·사고 강도 바꾸기는 호스트로 가지 않는다(다음 상세가 호스트의 값으로 되돌린다). 대기열의 글은 보이기만 하고 지우거나 고칠 수 없다.
- 기록은 최신 80개까지(`hasOlder`), 이전 기록을 불러오는 길은 없다. 그림은 빠진다.
- 앱을 열 때 저절로 연결하지 않는다(연결은 "저장됨"으로 시작한다).

### Windows 구현

2026-11-13. 아래 [Windows가 따를 규칙](#windows가-따를-규칙) 1~8을 그대로 따른다. 파일: Core `Remote/RemoteMirror.cs`(와이어·revision·상세 JSON·검사·`WithoutMirrors`), `Remote/RemoteHostService.Mirror.cs`(경로), `Remote/RemoteMirrorDesktopHost.cs`(호스트의 창 읽기와 행동), `Remote/RemoteClientService.Mirror.cs`(요청), `DesktopService.Mirror.cs`(폴링과 상태), 창의 `MainWindow.RemoteMirror.cs`, 웹 셸의 `shell/servers.ts`·`shell/Sidebar.tsx`·`pane/MirrorStrip.tsx`. Mac과 다른 점:

- **호스트**: 읽기와 행동은 `RemoteMirrorDesktopHost`가 창의 스레드에서 한다. 행동은 Windows 휴대폰 라우터(`MobileDesktopRouter`)의 m1 처리를 그대로 부른다(권한·질문·계획 답, 멈추기, 닫기, 새 창). 보내기만 업로드 id 대신 본문의 첨부를 받으므로 `MobileDesktopRouter.SubmitInlineAsync`가 같은 데스크톱 동작과 보내기 자리를 쓴다. 휴대폰 라우터가 없으면(앱이 뜨는 중) 행동은 409 `remote.mirror.error.unavailable`. 보내기의 403은 `RemoteHostService.MirrorRefusal`(`FullAccessRefusal`과 같은 규칙: 전체 권한 창, Windows 호스트의 Claude가 아닌 에이전트 창). 창 revision의 재료에 Mac의 것 말고도 설정 전체, 제목 모드, 기준 브랜치, 백그라운드 작업의 턴 끝, 대기열 글 길이를 넣는다.
- **상세 JSON**: Windows 타입을 Mac의 JSON으로 쓴다(`RemoteMirrorSupport.Write`): `inputJson`→`inputJSON`, 그림·`outputFile` 빼기, 항목·그래프·요청·작업 안의 시각은 `RemoteWireEvents.Normalize`. 세션의 `startedAt`·`finishedAt`·`createdAt`은 Mac의 `ISO8601DateFormatter` 기본형(초 단위)만 읽히므로 초 단위로 쓴다. 읽을 때(`Read`)는 그래프·할 일·백그라운드 작업·항목의 활동이 Windows 타입으로 안 읽히면 그것만 빼고 창은 받는다.
- **클라이언트**: 미러는 `DesktopService`가 들고 있다. 연결의 `Changed`에서 연결됨으로 바뀌면 시작, 끊기면 멈춤, 지워지면 그 미러 워크스페이스·창을 뺀다. 상태 파일에는 `StateStore.SaveAsync`가 `WithoutMirrors()`로 빼고 쓴다. `StateStore.Normalize`는 미러 워크스페이스를 이 PC의 64개와 따로(연결 수 × 64), 미러 창을 128개와 따로(연결 수 × 128) 두고, 미러 창의 글은 이 PC 창의 글 예산을 쓰지 않는다. `PaneLayout.HasRoom`은 이 PC 창만 센다. 미러 창이면 `Launch`·`StopAsync`·`RespondToToolPermission`·`AnswerQuestionnaire`·`AnswerPlan`·`CloseSessionAsync`와 창의 `AddPane`이 호스트로 보낸다. 이름 바꾸기와 목록에서 빼기는 아무것도 하지 않는다. 행동이 호스트에서 거절되면 `MirrorFailed`로 창의 오류 줄에 그 말이 나온다.
- **화면**: 웹 셸이 그린다. 사이드바의 서버 머리글(`ServerHeader`: 점, "원격 · 이름", "주소 · 상태", … 메뉴의 연결·연결 끊기·원격 연결 설정…), 미러 창 띠(`MirrorStrip`), 상태 막대(`status.mirror`), 설정의 **보기**. 미러 워크스페이스·창의 메뉴에는 이름 바꾸기·목록에서 빼기가 없고, 창 추가 메뉴에는 명령 창이 없다. WinUI 자체 화면(웹 셸이 꺼졌을 때)은 상태 막대 글만 미러를 말하고, 사이드바 묶음과 창 띠는 없다.
- **검사**: Core `RemoteMirrorVerification`(픽스처 9개 파일과 목록, 클라이언트 id가 Mac 값과 같음, 한도와 revision, 저장하지 않음과 창 한도, 루프백 호스트 경로와 403·404·409, Windows 클라이언트↔Windows 호스트 전체, 이전 호스트의 404), 웹 `shell/servers.test.ts`·`pane/mirror.test.ts`. Windows 호스트가 쓴 상세를 Mac 클라이언트가 읽는지는 픽스처 왕복으로만 확인했다(Mac 쪽 실기·교차 검사는 아직).

### Windows가 따를 규칙

1. **호스트**: 위 표의 경로·본문·답·상태 코드와 한도(창 128, 기록 80, 그래프 20, 기다리는 요청 32, 대기열 16개·500자, 상세 1.5 MiB, 제목·모델 200자, revision 16진수 64자까지)를 그대로. 공유 워크스페이스의 이 PC 에이전트 창만. `status`는 다섯 단어(그 밖은 `idle`). `since`가 지금 revision이면 `unchanged: true`와 빈 목록. revision은 상세가 바뀌면 반드시 바뀐다.
2. **호스트의 행동**은 Windows 휴대폰 처리와 같은 함수로(보내기·대기열, 멈추기, 답, worktree 모드가 묻지 않는 새 창, 닫기). 전체 권한 창에 보내기와 `always: true`는 스위치가 켜져 있을 때만(같은 403 키). Windows 호스트의 Codex·Antigravity 규칙(`remote.windows.host.error.agentNeedsFullAccess`)도 미러 보내기에 같이 적용한다(같은 이유: 가둘 수 없다).
3. **클라이언트 id**: `mirror-` + hex(SHA-256(UTF-8(`connectionId` + `"\0"` + 호스트 id))의 앞 16바이트), 워크스페이스와 창 모두.
4. **저장하지 않는다**: 상태 파일에 미러 워크스페이스·창·배치·활성 id를 쓰지 않는다. `remote.mirror: true`와 창의 미러 참조는 메모리에만. 창 한도는 이 PC의 창만 센다.
5. **폴링** 1초/5초(위 조건), 선택·행동이 깨운다. 상세는 보이는 워크스페이스의 창과 답을 기다리는 창, revision이 다를 때만, 한 번에 16개.
6. **실패**: 10초 재시도, 401은 끊김, 답한 거절은 연결을 두고 미러만 멈춤, 404면 이전 방식의 **열기**로.
7. **화면**: 서버마다 "원격 · {이름}" 머리글(점, 주소 · 상태 단어, 연결/끊기 메뉴), 미러 창 띠 "원격 창 · {호스트}에서 실행 중 · {상태}"와 대기열 수, 상태 막대 "원격 · …", 설정의 **보기**. 문구는 `remote.mirror.*` 키(로캘에 이미 있다).
8. **픽스처**: Windows Core 검사가 `native/contracts/fixtures/remote-mirror/`를 Mac(`RemoteMirrorWireTests`)처럼 읽는다. 목록·상세는 클라이언트 검사(`RemoteMirrorSupport.valid`와 같은 규칙)를 통과해야 한다.

## 프로토콜과 보안 경계

- 앱 사이 프로토콜은 인증된 HTTP/1.1 요청(`/v2/…`, 헤더 `x-mighty-remote-version: 2`)과 실행별 출력 커서다. 지운 기능의 `/v1/`과는 서로 답하지 않는다.
  - `GET /v2/info` — 호스트 이름, 공유 워크스페이스(id·이름·경로), 호스트의 CLI·모델·지원 설정
  - `POST /v2/runs` — 실행 요청(본문에만 프롬프트), `202 {jobId}`
  - `GET /v2/runs/{id}/events?cursor=N` — 커서 뒤의 이벤트, 버퍼에서 빠진 구간이 있으면 `gap`
  - `POST /v2/runs/{id}/stop|steer|permission|questions|plan` — `steer`는 구버전 클라이언트를 위해 라우트만 남았고, 0.4.35부터 호스트는 언제나 `accepted: false`로 답한다. 구버전 클라이언트는 그 답을 받으면 글을 자기 대기열로 넘긴다.
  - Antigravity 권한 카드: 호스트 실행의 이벤트 `antigravityPermission`이 클라이언트 창에 카드를 띄우고, 허용의 이어 보내기는 `POST /v2/runs`에 `antigravityGrant`를 싣는다. 규칙은 그 창을 실행하는 호스트가 자기 agy 설정에 넣고 지운다(자기가 알린 카드의 규칙만, 그 밖은 403). 카드의 규칙은 한 번만 받는다(같은 카드로 다시 보내면 403). 항상 허용(`temporary: false`)은 호스트의 '원격에서 전체 권한·명령 실행 허용'이 켜져 있을 때만 받고(아니면 403 `remote.host.error.antigravityAlways`), 이번만 허용할 수 있는 명령의 항상 허용은 409(`remote.error.antigravityOnceOnly`). 임시 규칙도 그 호스트의 전역 agy 설정에 들어가므로 있는 동안 그 컴퓨터의 모든 Antigravity 실행에 적용된다. 자세한 것은 [mobile-remote.md](mobile-remote.md)의 "Antigravity 권한 카드"
- 무엇이 Tailscale 주소인지는 한 규칙이다(`TailscaleAddresses.select(interfaces:)`): `utun…` 인터페이스 가운데 Tailscale만 쓰는 IPv6 대역 fd7a:115c:a1e0::/48 주소도 가진 것만 Tailscale이고, 그 인터페이스의 두 대역(IPv4 100.64.0.0/10, IPv6 fd7a:115c:a1e0::/48) 주소가 Tailscale 주소다. 통신사 NAT 망은 Wi-Fi·이더넷에도 100.64/10 주소를 주고, Cloudflare WARP 같은 다른 VPN은 자기 utun에 100.96/12 주소를 두기 때문이다.
- 호스트는 그 주소마다 하나씩, 그 주소에만 바인딩하고, 리스너를 그 주소를 가진 utun 인터페이스에 고정한다(시스템이 그 인터페이스를 알려 주지 않으면 Wi-Fi·이더넷·셀룰러·루프백 인터페이스를 막는다). 연결을 받는 순간 상대 주소가 Tailscale 주소가 아니거나 같은 상대의 열린 연결이 이미 8개면 아무것도 읽지 않고 끊는다. 키가 없거나 틀리면 본문을 한 바이트도 읽지 않고 머리만으로 401을 답한다. 요청마다 상대 주소가 Tailscale 주소인지, `Origin` 헤더가 없는지(브라우저 페이지 차단), 키가 맞는지(SHA-256 뒤 상수 시간 비교), 프로토콜 버전이 맞는지, 초당 요청 수를 넘지 않는지 본다. 상대마다 300번, 키가 맞는 요청만 세는 전체 600번이라 키 없는 기기가 진짜 클라이언트를 밀어낼 수 없다. 클라이언트는 실행마다 초당 10번까지만 묻는다.
- Windows 호스트(`RemoteHostService.cs`)는 요청 경로를 쓰인 그대로 읽는다. 퍼센트 인코딩을 풀지 않고 `#` 조각을 떼어 내지 않으므로, Mac 호스트라면 그 경로로 답했을 요청에 404나 400을 답한다. 달라지는 것은 상태 코드뿐이고, 키·Origin·상대 주소 검사를 건너뛰는 길은 없다.
- 호스트가 자기 CLI·플러그인을 업데이트하거나 Claude 모델 목록을 다시 읽는 동안에는 다른 컴퓨터의 새 실행을 503으로 미룬다.
- 클라이언트의 폴링이 잠깐 실패하면(429·503, 시간 초과·연결 끊김) 10초 동안 1초마다 다시 묻고, 그래도 안 되면 연결이 끊긴 것으로 본다.
- 키는 공유를 시작할 때마다 새로 만드는 256비트 난수(base64url 43자)다. 공유를 멈추면 폐기된다. 키는 `Authorization` 헤더로만 다니고 URL·로그·상태 파일에 들어가지 않는다.
- 클라이언트는 주소를 한 번 풀어(`getaddrinfo`) 모든 결과가 Tailscale 주소이고, 그 IP로 가는 경로의 출발 주소가 이 컴퓨터의 Tailscale(터널) 주소일 때만 연결한다(`TailscaleAddresses.sourceAddress`). 이 경로 검사는 연결할 때만이 아니라 요청마다(실행 시작, 폴링, 답·덧붙이기, 중지, 연결된 상태의 `ensureConnected`) 다시 한다. 경로가 터널을 벗어나면 다시 묻지 않고 그 연결을 끊긴 것으로 바꾸고 실행을 오류로 끝낸다. 이 컴퓨터에 Tailscale 주소가 없으면 연결하지 않는다. 호스트가 공유를 다시 시작해 키가 바뀌었으면(401) 연결을 끊긴 것으로 표시한다. 시스템 프록시·쿠키·캐시를 쓰지 않고, 리디렉션은 따르지 않는다(3xx는 오류).
- 실행할 수 있는 작업 폴더는 공유한 워크스페이스 자신의 폴더뿐이다. 클라이언트는 경로를 보내지 않고 호스트의 워크스페이스 id만 보낸다. 호스트는 그 id를 자기 저장 상태에서 찾아 로컬 워크스페이스인지, 폴더가 있는지 확인한다. 공유 목록은 시작 위치를 정할 뿐 파일 접근을 격리하지 않는다. 전체 접근 권한이나 명령 창은 호스트 사용자 권한으로 폴더 밖에도 닿는다.

| 한도 | 값 |
| --- | --- |
| 공유 워크스페이스 | 64개 |
| 동시 원격 실행 | 호스트 16개, 클라이언트 16개 |
| 저장 연결 | 16개 |
| 요청 본문 | 키가 없으면 0(본문을 읽지 않음). 키가 맞으면 512 KiB, `POST /v2/runs`만 첨부 때문에 12 MiB(본문을 읽기 전에 헤더로 키를 확인) |
| 상대마다 열린 연결 | 8개(받는 순간 확인) |
| 클라이언트 창마다 대기 중인 권한 요청 | 최신 32개 |
| 응답 | 클라이언트가 2 MiB까지 읽음. 폴링 한 번은 이벤트 256개·1.5 MiB까지 |
| 호스트 정보(`/v2/info`)의 모델 목록 | 제공자마다 이 앱이 그 CLI의 목록을 읽을 때 남기는 만큼: 기본 항목과 Claude·Codex·Antigravity 128개, pi 512개, opencode·Hermes 1,024개(`ProviderOptions.listedModelLimit`, `RemoteWorkspaceValidation.bounded`). 호스트는 그만큼만 보내고, 클라이언트는 예전 호스트가 보낸 더 긴 목록을 앞에서부터 그만큼만 남긴다. 전에는 목록 하나가 128개를 넘으면 클라이언트가 호스트 정보 전체를 거절해 연결하지 못했다(opencode는 수백 개를 알린다). 예전 Mac과 Windows 클라이언트는 128개에서 거절하므로, opencode 목록이 긴 호스트에는 업데이트한 클라이언트로 연결한다(Windows도 같은 한도로 자른다: `RemoteWorkspaceValidation.Bounded`) |
| 실행별 이벤트 버퍼 | 4096개·4 MiB. 넘치거나 한 이벤트가 너무 크면 건너뛰고 `gap`으로 알린다. 클라이언트가 쓸 수 없는 이벤트 하나는 그것만 빼고 안내 줄을 남기며, 실행은 이어진다 |
| 연결 시간 | 연결마다 요청·응답 합쳐 15초(첨부 실행 요청은 60초) |
| 하트비트 | 30초 |

- 루프백 주소 허용은 테스트가 코드에서 주입하는 옵션(`allowLoopbackForTests`)이다. 설정·연결 입력·IPC로는 켤 수 없다.
- Tailscale 설치·로그인·tailnet 정책은 앱 밖에서 관리한다. 앱은 `tailscale` 명령을 실행하지 않는다.

## 저장

- 워크스페이스의 `remote` 값은 `{"version": 2, "connectionId", "workspaceId", "hostName"}`이다(`RemoteWorkspaceReference`). 경로는 호스트의 경로이고 이 컴퓨터의 폴더로 쓰지 않는다(`StateRepository.resolveLocalWorkspace`·`ProcessRunner`가 거절).
- 2026-09-28 이전에 저장된 `remote`에는 `version`이 없다. 그런 워크스페이스는 지금처럼 불러올 때 창·배치와 함께 버린다(`StateRepository.keepsWorkspace`). 그 뒤 앱이 저장한 로컬 워크스페이스에는 `remote` 키가 없으므로 원격으로 읽힐 일이 없다. 반대로 2026-09-28부터 이 기능 전까지의 앱(0.4.29까지)은 `remote` 값이 있는 워크스페이스를 버리므로, 버전 2 원격 워크스페이스를 로컬 폴더로 착각하지 않는다.
- 연결 목록(id·이름·주소, 키 없음)은 데이터 폴더의 `remote-connections.json`(권한 600, `version: 2`)에 있다. (Mac) 키는 macOS Keychain(서비스 `dev.mightycode.native.remote`, 계정 = 연결 id, 이 기기에서 잠금 해제 후만)에만 있고, 앱 시작 때는 읽지 않는다. Keychain이 거절하면 키는 앱을 끌 때까지만 메모리에 둔다. 앱이 고정된 서명으로 빌드되므로 "항상 허용"은 업데이트 뒤에도 유지된다. Windows의 키 저장은 아래 절의 **저장(Windows)**.

## Windows와 Mac↔Windows

Windows 앱도 같은 `/v2/` 프로토콜로 공유하고 연결한다. 그래서 Mac→Mac, Mac→Windows, Windows→Mac, Windows→Windows가 모두 된다. 두 앱이 같은 요청·응답·이벤트를 쓰는지는 공유 픽스처 `native/contracts/fixtures/remote-v2/`가 묶는다. Mac이 기준이고, Mac(`RemoteWireFixtureTests`)과 Windows Core(`RemoteWireFixtureVerification`)가 같은 파일을 읽는다.

- **화면**: **설정 → 원격 연결 (Tailscale)** 탭(`MainWindow.RemoteWorkspaces.cs`)이 모바일과 펫 사이에 있다. 공유(주소·연결 키·고르기·포트·전체 권한 스위치), 방화벽 규칙, 다른 컴퓨터 연결, 연결한 컴퓨터와 공유 워크스페이스가 한 상자 안에 구역별 제목으로 나뉜다. 3초마다 상태를 다시 읽고, 공유가 스스로 멈췄으면 탭에 그 사실을 적는다. 사이드바 워크스페이스 머리글의 네트워크 단추와 **창 추가** 메뉴의 **다른 컴퓨터 연결…**도 이 탭을 연다.
- **연결 줄**: 연결한 컴퓨터의 줄은 Mac처럼 이름과 주소만 보인다. 호스트의 OS는 줄이 아니라 원격 워크스페이스(사이드바의 호스트 이름, 머리글의 호스트 배지, 상태 막대 "{호스트} · {OS}에서 실행 · 연결됨/연결 안 됨", 세션 정보의 **실행 컴퓨터**·**실행 컴퓨터 OS**)에 나온다.
- **호스트(Windows)**: `MightyCode.Core/Remote/RemoteHostService.cs`가 Tailscale 주소마다 `RemoteHttpListener`(TcpListener, HTTP/1.1, 연결마다 요청 하나)를 그 주소에만 바인딩한다. 받는 순간 상대를 거르고 상대마다 연결 8개, 키 없는 요청의 본문은 읽지 않음, SHA-256 뒤 상수 시간 비교, 상대마다 초당 300번·키가 맞는 요청 전체 600번은 Mac과 같다. 실행은 호스트의 `RunManager`(그 PC의 CLI·로그인, 창별 MCP·위임·그림 캐시 없음)로 한다. 이 PC에서 다른 컴퓨터의 실행이 도는 동안에는 CLI 업데이트(수동·자동)와 플러그인 변경을 미룬다.
- **요청 경로**: Windows 호스트는 요청 경로를 쓰인 그대로 읽는다. 퍼센트 인코딩을 풀지 않고 `#` 조각을 떼지 않으므로, Mac이라면 열어 줬을 경로에 Windows 호스트는 404나 400을 답한다. 달라지는 것은 상태 코드뿐이며, 키·`Origin`·상대 주소 검사를 건너뛰는 길이 아니다.
- **명령 창**: Windows 호스트에서는 명령마다 시스템 폴더의 `powershell.exe -NoProfile -NonInteractive -Command <글>`을 공유 폴더에서 새로 띄운다(`RemoteCommandShell`, 환경 변수 `NoDefaultCurrentDirectoryInExePath=1`). Mac 호스트는 `$SHELL -l -c`. 창의 빈 화면 안내(`remote.windows.pane.commandNote`)와 상태 막대·세션 정보는 호스트의 OS(`/info`의 `runtime.platform`: `darwin`·`win32`)를 보여 준다.
- **전체 권한 스위치(Windows 호스트)**: Mac 호스트에서는 Seatbelt가 Codex·Antigravity의 읽기 전용·작업 폴더 쓰기 실행을 가둔다. Windows에서는 그 가둠을 확인할 수 없으므로, Windows 호스트는 **원격에서 전체 권한·명령 실행 허용**이 꺼져 있으면 다른 컴퓨터의 Codex·Antigravity 실행을 권한 모드와 상관없이 실행 전에 403(`remote.windows.host.error.agentNeedsFullAccess`: 다른 권한 모드로는 안 되고 스위치를 켜야 한다는 문구)으로 거절한다(`RemoteHostService.FullAccessRefusal`, 실패 시 닫힘). Claude는 권한 요청으로 묻기 때문에 스위치가 꺼져 있어도 지금처럼 실행된다. Mac 호스트의 규칙은 그대로다.
- **이벤트 맞추기**: Windows 실행 이벤트는 Mac 모양으로 바꿔 보낸다. 로그인 만료 사유는 `auth`이고, 권한 요청 입력은 `inputJSON`이며, 시각은 `yyyy-MM-ddTHH:mm:ss.fffZ`로 쓰고, 그림은 뺀다. 그래프는 Mac의 `graph`(ExecutionGraphNode)로 보낸다. Windows 클라이언트가 실행 요청에 `x-mighty-remote-graph: run`을 붙이면 Windows 호스트는 그 실행에 `graph` 대신 Windows의 `graph_run`(MightyGraphRun)을 보내고, Mac 호스트는 이 머리를 무시한다. Windows 클라이언트는 Mac 호스트의 `graph`를 조용히 버리므로, Mac에서 도는 실행의 Windows 마이티 보기는 대화 기록으로 묶은 블록만 보인다.
- **클라이언트(Windows)**: `RemoteClientService`는 요청마다 그 IP로 가는 출발 주소가 이 PC의 Tailscale 주소인지(UDP 소켓 연결, 보내는 것 없음) 다시 본다. 리디렉션을 따르지 않고 프록시·쿠키를 쓰지 않으며, 응답은 2 MiB까지, 15초(첨부 실행 60초) 안에 읽는다. 폴링이 429·503·시간 초과로 실패하면 10초 동안 다시 묻는다. 쓸 수 없는 이벤트 하나는 빼고 안내 줄을 남기며, 기다리는 권한 요청은 창마다 최신 32개만 둔다. 이 PC의 파일·브라우저·터미널·Explorer 열기·스타일·위임은 원격 워크스페이스에서 꺼 두고(워크스페이스의 폴더를 읽는 곳은 모두 `LocalPath`를 거친다), 이 PC의 CLI 업데이트·로그인 대기는 원격 창을 붙잡지 않는다.
- **저장(Windows)**: 연결 목록은 `remote-connections.json`(버전 2, 키 없음, 임시 파일에 쓴 뒤 바꾸는 원자적 쓰기), 키는 `<프로필>\remote-keys\<id의 SHA-256>.bin`에 현재 Windows 사용자 DPAPI(`CryptProtectData`, `ProtectedData`의 CurrentUser와 같음)로 암호화해 둔다. DPAPI가 거절하면 키는 앱을 끌 때까지만 메모리에 있다. 연결 목록 파일을 다른 프로그램이 붙잡고 있어 읽지 못하면 덮어쓰지 않고 잠시 뒤 다시 읽는다. `Workspace.remote`는 Mac과 같은 `{"version": 2, …}`이며 버전이 없거나 다른 값이면 불러올 때 버린다.
- **방화벽**: 휴대폰 직접 연결과 같은 규칙 하나(TCP, 이 앱 실행 파일, 100.64.0.0/10·fd7a:115c:a1e0::/48에서만)가 켜진 포트를 함께 연다. 휴대폰 직접 연결이 켜져 있으면 그 포트, 이 PC가 공유 중이면 공유 포트로, 둘 다 켜져 있으면 두 포트를 한 규칙에 넣는다(`RemoteWorkspaceFeatures.FirewallPorts`). 두 설정 탭(모바일, 원격 연결) 어느 쪽에서든 같은 규칙을 고친다. 원격 연결 탭의 단추는 **방화벽 규칙 추가**·**방화벽 규칙 갱신**(규칙이 있을 때)·**방화벽 규칙 지우기**이고, 모바일 탭은 같은 일을 **규칙 추가**·**규칙 지우기**(와 규칙이 있을 때 **방화벽 규칙 갱신**)로 부른다. 한 번에 하나만 실행한다. 추가·갱신·지우기를 할 때마다 Windows가 관리자 확인 창(UAC)을 한 번 띄운다. 앱은 이 밖에 권한 상승을 요청하지 않는다. 앱이 규칙을 저절로 바꾸지는 않는다: 휴대폰 직접 연결을 끈 채 공유를 이어 가거나 공유를 멈춘 뒤에도 규칙은 이전 포트를 연 채 남을 수 있고, 그때는 **방화벽 규칙 갱신**으로 맞춘다. 두 탭은 규칙의 포트(`netsh … show rule`이 보여 주는 숫자 목록)가 지금 쓰는 포트를 모두 담지 않거나 읽지 못하면 "갱신 필요"라고 적고(`RemoteWorkspaceFeatures.FirewallNeedsUpdate`), 규칙을 지우면 두 포트가 함께 닫힌다고 적는다.
- **키 복사**: 클립보드 기록(Win+V)과 클라우드 클립보드에 남지 않게 복사하고(`ClipboardContentOptions` IsAllowedInHistory·IsRoamable 끔), 클립보드 모니터링 프로그램이 건너뛰게 `ExcludeClipboardContentFromMonitorProcessing` 형식도 붙이고(Mac의 Concealed에 해당; 붙일 수 없으면 이 표시 없이 복사하되 기록·클라우드 제외는 그대로), 60초 뒤에도 그 키가 클립보드에 있으면 지운다. 이 60초 뒤 지우기는 설정 탭을 닫아도 그대로 돈다. 설정 탭은 복사했는지 실패했는지 말하고, 탭을 닫으면 상태를 다시 읽는 타이머를 멈추고 화면에 보이던 키 값과 쥐고 있던 키를 놓는다.
- **기기 확인 남음**: UAC 창과 방화벽 규칙, Mac→Windows와 Windows→Mac 실제 연결은 Windows PC에서 확인해야 한다(실제 기기 연결은 아직 확인하지 못했다. `docs/windows-screen-checklist.md`의 "원격 워크스페이스").

## 검사

`native/macos/Tests/MightyCoreTests/RemoteWorkspaceTests.swift`, `RemoteMirrorTests.swift`. CI의 "Remote workspaces — host and client talk over loopback" 단계가 이 두 묶음을 따로 돌린다.

- 주소 거르기(Tailscale 대역, 정규화·중복 제거, 루프백은 주입할 때만), 주소 입력 해석
- 키 모양과 비교, 첨부 본문 한도가 인증된 실행 요청에만 열리는지, 호스트 이벤트 검사, 페이즈 모델 검사
- 저장: 버전 2만 원격으로 남고 이전 모양·다른 버전·잘못된 값은 버리며, 로컬 워크스페이스는 원격으로 쓰이거나 읽히지 않는다
- 루프백 왕복: 공유·목록·열기, 명령 창 실행과 중지, 가짜 Claude CLI의 스트림이 호스트 파서를 거쳐 클라이언트의 기록·마이티 그래프가 되고 도구 권한 요청에 클라이언트가 답하는 흐름, 틀린 키·옛 프로토콜·브라우저 Origin·한도 초과 본문·공유하지 않은 폴더·지원하지 않는 설정의 거절, 공유 중지 뒤 키 폐기, 하트비트 만료, 호스트가 사라졌을 때의 오류, 리디렉션 거부, 키 없는 연결 파일
- Tailscale 인터페이스 규칙(fd7a 주소가 없는 WARP 같은 utun은 버림), 받는 순간의 상대 거절과 상대마다 연결 수, 키 없는 요청의 본문을 읽지 않음, 실행 중 경로가 터널을 벗어날 때(검사 함수 주입), 전체 권한·명령 스위치, 대기 권한 요청 상한, 쓸 수 없는 이벤트 하나만 건너뛰기
- 원격 미러(`RemoteMirrorTests.swift`): 공유 픽스처 `remote-mirror/`의 해석·검사·왕복, 클라이언트가 거절하는 목록·상세, 클라이언트 id, 창 revision이 움직이는 경우, 상세의 한도(기록 80·그래프 20·그림과 호스트 경로 뺌·기다리는 요청만·대기열 16×500자·1.5 MiB 안), 클라이언트 창 합치기, 미러가 저장되지 않음, 루프백으로 호스트 경로(미러 없음 404, 키, 공유 밖 404·403, `since`, 상세, 보내기·멈추기·답·닫기·새 창, 전체 권한 403, 앱 거절 409)와 클라이언트 서비스 왕복(CI에서는 끔: 연결이 호스트의 CLI 확인을 기다린다)
- Windows: 같은 파일을 읽는 Mac `RemoteWireFixtureTests`(공유 픽스처 `native/contracts/fixtures/remote-v2/`)와 Windows Core 검사 `RemoteWorkspaceStateVerification`·`RemoteWireFixtureVerification`·`RemoteClientVerification`·`RemoteHostVerification`·`RemoteLoopbackVerification`·`RemoteDesktopVerification`(Mac에서 `dotnet run`으로 돌린다. Windows에서만 되는 검사 9개는 Mac에서 실패한다). 실제 WinUI는 GUI 스모크 키 `remoteWorkspaces`가 한 프로세스에서 루프백으로 공유하고 연결해 설정 탭·사이드바·머리글·상태 막대·세션 정보와 원격 명령 창을 확인한다.
