# Windows — Claude 추가 권한 요청의 앱 내 승인

macOS(`ToolPermissions.swift`, `ToolPermissionBar.swift`)와 같은 화면·문구·동작을 Windows에 옮긴 기록이다. 코드는 `MightyClaude.Core/ToolPermissions.cs`·`ToolPermissionPresentation.cs`·`ToolPermissionStrings.cs`, 화면은 `MightyClaude.WinUI/MainWindow.ToolPermission.cs`다.

## 닫힌 쪽으로 실패한다

- 승인 채널은 Claude Code의 SDK stdio 제어 프로토콜을 쓴다. `initialize` 응답이 성공이 아니거나 15초 안에 오지 않으면 채널을 닫고 실행을 멈춘다. 프롬프트는 초기화가 성공한 뒤에만 보낸다.
- `can_use_tool` 요청만 받는다. 다른 종류의 제어 요청에는 오류로 답한다. 식별자가 형식에 맞지 않거나 요청이 깨져 있으면 채널을 닫는다.
- 한 실행에서 대기 중인 요청은 16개까지다. 그 뒤의 요청은 거부한다.
- 도구 입력이 64 KiB 표시 한도를 넘으면 거부한다. 전체를 보여 줄 수 없는 요청은 허용할 수 없다.
- 허용 응답은 받은 입력을 그대로 돌려준다(`behavior: allow`, `updatedInput`, `toolUseID`). 설정 변경, 영구 규칙, 모드 변경은 어떤 응답에도 담지 않는다.
- 실행을 멈추면 대기 중인 요청을 모두 취소로 끝낸다.
- 요청은 저장 상태(`AppSnapshot`)에 쓰지 않고, 입력을 로그에 남기지 않고, 원격 호스트로 넘기지 않는다.

## 승인 요청을 켜는 실행 경로

Claude를 `--permission-prompts host`와 `--permission-prompt-tool stdio`로 시작하는 경로는 하나뿐이다: 승인 막대를 보여 줄 수 있는 실행 창에서 시작한 Claude 실행(`RunManager`에 승인 구독자가 있을 때). Codex·Gemini 실행, 원격 워크스페이스, 그 밖의 경로는 지금처럼 `--permission-prompts none`이다. `Core.Tests`의 `AutoPermissionVerification`이 구독자 없는 경로가 `none`으로 남는지 확인한다.

## 이번 범위 밖 — 거부만 할 수 있는 요청

아래 요청은 macOS와 같은 문장("이 요청은 현재 승인 화면에서 허용할 수 없습니다. 거부하거나 실행을 중지하세요.")과 함께 `거부`만 보인다.

- `AskUserQuestion` 설문: Windows에는 설문 화면이 아직 없다(패리티 표 '사용자 설문', 3단계).
- 펫 말풍선의 승인: 동반 펫이 아직 없다(3단계). 승인은 실행 창의 막대에서만 한다.
- 스타일의 자동 허용(`autoAllow`): 스타일 엔진이 아직 없다(2단계). 모든 요청이 막대로 온다.

## OS에 묶인 선택

- 포커스와 초안 복원: 스모크 검사는 시작할 때의 입력창 초안과 포커스를 기억했다가 끝에 되돌린다. 같은 실행 창을 뒤의 분할·탭 검사가 이어서 쓰기 때문이다.
- 보이지 않는 제어 문자: 입력 표시에서 U+202E 같은 방향 제어 문자는 이스케이프(`\u202e`)로 보여 준다. 화면에 보이는 것과 실제로 허용하는 내용이 달라지지 않게 하려는 것이며 macOS의 표시 규칙과 같다.
