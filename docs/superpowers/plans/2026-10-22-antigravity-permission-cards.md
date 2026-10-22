# Antigravity 권한 카드 (Claude처럼 묻기)

사용자 요청(2026-10-22): young-dev 워크스페이스의 Antigravity 창에서 권한이 필요한 작업이 제대로 처리되지 않는다. 세션 기록을 보고 **Claude처럼 권한을 묻게** 해 달라. 사용자는 "이번만 허용" 방식으로 **A**를 골랐다.

## 확인한 사실
- young-dev 창(권한 '수정 허용')에서 agy가 `curl … linkedin.com/in/…`를 실행하려다 자동 거절됐고, 회색 안내 한 줄(`run.notice.antigravityDenied`)만 남았다.
- agy 1.4.4의 헤드리스 실행(`-p`)은 실행 도중 권한을 물을 수 없다. stream-json 입력도 사용자 메시지 외의 이벤트(권한 응답 등)를 받지 않는다(`stream input message event %q is not supported yet`).
- 대신 거절은 정확히 보고된다. stream-json 출력의 도구 단계에 `tool_info.parameters`(예: `run_command`의 `CommandLine`)와 `state: "ERROR"`, `error.message: "permission check failed … user denied permission …"`이 오고, 마지막 `result.denied_actions: [{action: "command", display_name: "RunCommand"}]`가 온다. stderr에도 `a tool required the "command" permission that headless mode cannot prompt for`가 찍힌다.
- 허용은 agy 설정의 규칙으로 한다: `~/.gemini/antigravity-cli/settings.json`(Windows는 사용자 폴더의 같은 경로)의 `permissions.allow`에 `command(<명령 앞부분>)`, `url(<도메인>)` 같은 규칙. 지금 파일의 최상위 키는 `colorScheme`, `model`, `permissions`(`allow`), `trustedWorkspaces`다.
- **T1 조사(2026-10-22, agy 1.4.4, 기본 모드 = `--mode` 없음, 새 임시 폴더에서 `agy --print= --input-format stream-json --output-format stream-json --print-timeout 0`; 조사 전후 설정 파일 SHA-1이 같았고 새 폴더가 `trustedWorkspaces`에 더해지지도 않았다)**:
  - 명령: 도구 `run_command`, 매개변수 `CommandLine`. 거절 단계는 `state: "ERROR"`, `tool_info.error: {type: "TOOL_ERROR", message: "permission check failed for unsandboxed \"<명령>\": user denied permission to run command:\n<명령>\nDo not attempt to circumvent this denial … Proceed without performing this action."}`. 결과는 `denied_actions: [{action: "command", display_name: "RunCommand"}]`, stderr는 `jetski: no output produced — a tool required the "command" permission … (e.g. command(<target>)) …`. (agy 1.3.5는 콜론 뒤 명령 없이 `… user denied permission to run command`로 끝났다.)
  - 파일 만들기: 도구 이름이 `write_file`(매개변수 `Path`)이고 거절 문구가 규칙을 그대로 말한다: `… user denied permission for write_file(/private/tmp/…/hello.txt)` — 경로는 심볼릭 링크를 푼 절대 경로다. 결과는 `{action: "write_file", display_name: "WriteFile"}`, stderr 예시는 `write_file(<target>)`.
  - 웹 주소: 헤드리스에서는 `init.tools`에 `read_url_content`가 있어도 모델이 "이 도구가 없다"며 쓰지 않았다. https://example.com 을 읽으라 하면 `curl -s https://example.com | grep -i '<title>'`를 `run_command`로 돌리려다 명령으로 거절됐다. 그래서 young-dev 사례(curl)처럼 주소 거절은 실제로는 명령 거절이다. agy의 주소 규칙 이름은 `url(…)`이 아니라 `read_url(<도메인>)`·`execute_url(<도메인>)`이다(바이너리 문자열).
  - 바이너리 문자열의 규칙 종류: `command(<prefix>)`, `read_file(<절대 경로>)`, `write_file(<절대 경로>)`, `mcp(<server>/<tool>)`, `read_url(<domain>)`, `execute_url(<domain>)`. 거절 문구 틀은 `user denied permission for %s`(그 `%s`가 규칙), 헤드리스 안내는 `%s required the %s %s that headless mode cannot prompt for, so %s auto-denied. Add an allow-rule under permissions.allow in settings.json (e.g. %s)`. 규칙으로 허용할 수 없는 도구도 있다: `the %s tool(s) required approval that headless mode cannot prompt for, so they were auto-denied. Settings allow-rules do not apply; re-run with --dangerously-skip-permissions …`.
  - 한 턴의 거절: 대개 첫 거절에서 턴이 끝난다. 두 명령을 한꺼번에 시키면 첫 명령(`echo alpha`)만 거절되고 다음 명령(`echo beta`)은 그대로 실행됐다. `denied_actions`는 권한 종류마다 한 번 실린다. 거절이 없는 턴에는 `denied_actions`가 없다.
  - 기본 모드의 `init.permission_mode`는 `request-review`다.
  - 확인하지 못한 것(사용자 확인 몫): agy가 `command(<명령 전체>)`를 파이프·따옴표가 든 명령에도 접두어로 맞추는지, 이어 보낸 턴이 규칙으로 실제로 실행되는지. 테스트는 진짜 설정 파일을 건드리지 않고 임시 경로와 가짜 agy로만 했다.

## 정한 것 (사용자 선택 A)
1. **카드**: agy 턴이 거절된 작업을 남기고 끝나면, 그 창에 Claude 권한 카드와 같은 모양의 카드를 띄운다. 카드는 거절된 작업(명령 전체, 주소의 도메인, 그 밖의 작업 이름)을 모두 보여 주고 버튼 셋: **이번만 허용** · **항상 허용** · **거절**. 맥·윈도우·폰(맥·윈도우 호스트 모두) 같은 카드. 권한 '기본'에서도 뜬다(편집 거절도 규칙으로 허용할 수 있으면 같은 카드, 규칙이 없는 작업은 카드에 "권한 모드를 바꾸세요" 안내만).
2. **이번만 허용**: 거절된 작업마다 그것 하나만 허용하는 규칙(명령은 그 명령 전체를 접두어로, 주소는 그 도메인)을 agy 설정의 `permissions.allow`에 잠깐 넣고, **같은 대화를 이어서** 다시 실행한다(이어 보낼 문구: 사용자가 방금 거절된 작업을 허용했으니 이어서 진행하라는 짧은 문장, 4개 언어 문구 키). 그 실행이 끝나면(성공·실패·중지·앱 종료 모두) 앱이 넣은 규칙만 지운다. 같은 규칙을 다른 창이 동시에 쓰면 마지막 사용이 끝날 때 지운다. 앱이 꺼져 규칙이 남았으면 다음 실행 때 지운다(앱이 넣은 규칙 목록을 앱 상태에 기록).
3. **항상 허용**: 같은 규칙을 남긴다(지우지 않음). 이어서 실행하는 것은 이번만 허용과 같다.
4. **거절**: 카드를 닫는다. 이어서 실행하지 않는다.
5. **설정 파일 안전**: 읽을 수 없거나 JSON이 아니면 고치지 않고 카드에 이유를 보인다. 다른 키·순서·값은 그대로 두고 `permissions.allow`만 바꾼다(이미 같은 규칙이 있으면 더하지 않고, 지울 때도 앱이 더한 것만). 원자적으로 쓴다. 전역 설정 외의 파일은 만들지 않는다.
6. **Claude와 다른 점(문서·도움말에 적는다)**: Claude는 실행 전에 멈추고 기다리지만, agy는 그 턴을 끝낸 뒤 묻고 허용하면 이어서 진행한다.
7. 맥이 기준이다. 윈도우 호스트와 폰은 맥과 같은 규칙·문구·동작을 따른다. 원격 작업 공간 창은 그 창을 실행하는 호스트의 설정을 고친다(호스트가 처리).

## 작업
- **T1 맥 + 프로토콜**: 파서(거절된 작업과 대상 모으기), 카드(맥 앱), 설정 규칙 쓰기·지우기(MightyCore, 순수 함수 + 파일 계층), 이어서 실행, 폰 프로토콜(기존 Claude 권한 카드 경로에 맞춘 추가 필드·응답), 문구 4개 언어, 테스트, `docs/mobile-remote.md`.
- **T2 윈도우**(T1 뒤): 같은 것을 윈도우 호스트·WinUI에.
- **T3 폰**(T1 뒤, T2와 병렬): 카드 표시와 응답.
- **T4 문서·도움말**: 도움말 4개 언어(에이전트 창·승인·폰), `docs/` 관련 문서.
- **T5 검증·리뷰·릴리즈**.

## 검사
맥 필터 테스트 + 앱 빌드, 윈도우 Core(기준선) + WinUI 컴파일, 폰 typecheck + jest, locales(두 방식)·디자인 토큰·도움말 검사, 스타일 고정 검사(`OK`, 고정 경로 무변경).
