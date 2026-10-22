# Antigravity 권한 카드 (Claude처럼 묻기)

사용자 요청(2026-10-22): 한 워크스페이스의 Antigravity 창에서 권한이 필요한 작업이 제대로 처리되지 않는다. 세션 기록을 보고 **Claude처럼 권한을 묻게** 해 달라. 사용자는 "이번만 허용" 방식으로 **A**를 골랐다.

## 확인한 사실
- 그 창(권한 '수정 허용')에서 agy가 외부 웹 주소를 읽는 `curl …` 명령을 실행하려다 자동 거절됐고, 회색 안내 한 줄(`run.notice.antigravityDenied`)만 남았다.
- agy 1.4.4의 헤드리스 실행(`-p`)은 실행 도중 권한을 물을 수 없다. stream-json 입력도 사용자 메시지 외의 이벤트(권한 응답 등)를 받지 않는다(`stream input message event %q is not supported yet`).
- 대신 거절은 정확히 보고된다. stream-json 출력의 도구 단계에 `tool_info.parameters`(예: `run_command`의 `CommandLine`)와 `state: "ERROR"`, `error.message: "permission check failed … user denied permission …"`이 오고, 마지막 `result.denied_actions: [{action: "command", display_name: "RunCommand"}]`가 온다. stderr에도 `a tool required the "command" permission that headless mode cannot prompt for`가 찍힌다.
- 허용은 agy 설정의 규칙으로 한다: `~/.gemini/antigravity-cli/settings.json`(Windows는 사용자 폴더의 같은 경로)의 `permissions.allow`에 `command(<명령 앞부분>)`, `url(<도메인>)` 같은 규칙. 지금 파일의 최상위 키는 `colorScheme`, `model`, `permissions`(`allow`), `trustedWorkspaces`다.
- **T1 조사(2026-10-22, agy 1.4.4, 기본 모드 = `--mode` 없음, 새 임시 폴더에서 `agy --print= --input-format stream-json --output-format stream-json --print-timeout 0`; 조사 전후 설정 파일 SHA-1이 같았고 새 폴더가 `trustedWorkspaces`에 더해지지도 않았다)**:
  - 명령: 도구 `run_command`, 매개변수 `CommandLine`. 거절 단계는 `state: "ERROR"`, `tool_info.error: {type: "TOOL_ERROR", message: "permission check failed for unsandboxed \"<명령>\": user denied permission to run command:\n<명령>\nDo not attempt to circumvent this denial … Proceed without performing this action."}`. 결과는 `denied_actions: [{action: "command", display_name: "RunCommand"}]`, stderr는 `jetski: no output produced — a tool required the "command" permission … (e.g. command(<target>)) …`. (agy 1.3.5는 콜론 뒤 명령 없이 `… user denied permission to run command`로 끝났다.)
  - 파일 만들기: 도구 이름이 `write_file`(매개변수 `Path`)이고 거절 문구가 규칙을 그대로 말한다: `… user denied permission for write_file(/private/tmp/…/hello.txt)` — 경로는 심볼릭 링크를 푼 절대 경로다. 결과는 `{action: "write_file", display_name: "WriteFile"}`, stderr 예시는 `write_file(<target>)`.
  - 웹 주소: 헤드리스에서는 `init.tools`에 `read_url_content`가 있어도 모델이 "이 도구가 없다"며 쓰지 않았다. https://example.com 을 읽으라 하면 `curl -s https://example.com | grep -i '<title>'`를 `run_command`로 돌리려다 명령으로 거절됐다. 그래서 처음 사례(curl)처럼 주소 거절은 실제로는 명령 거절이다. agy의 주소 규칙 이름은 `url(…)`이 아니라 `read_url(<도메인>)`·`execute_url(<도메인>)`이다(바이너리 문자열).
  - 바이너리 문자열의 규칙 종류: `command(<prefix>)`, `read_file(<절대 경로>)`, `write_file(<절대 경로>)`, `mcp(<server>/<tool>)`, `read_url(<domain>)`, `execute_url(<domain>)`. 거절 문구 틀은 `user denied permission for %s`(그 `%s`가 규칙), 헤드리스 안내는 `%s required the %s %s that headless mode cannot prompt for, so %s auto-denied. Add an allow-rule under permissions.allow in settings.json (e.g. %s)`. 규칙으로 허용할 수 없는 도구도 있다: `the %s tool(s) required approval that headless mode cannot prompt for, so they were auto-denied. Settings allow-rules do not apply; re-run with --dangerously-skip-permissions …`.
  - 한 턴의 거절: 대개 첫 거절에서 턴이 끝난다. 두 명령을 한꺼번에 시키면 첫 명령(`echo alpha`)만 거절되고 다음 명령(`echo beta`)은 그대로 실행됐다. `denied_actions`는 권한 종류마다 한 번 실린다. 거절이 없는 턴에는 `denied_actions`가 없다.
  - 기본 모드의 `init.permission_mode`는 `request-review`다.
- **보안 리뷰 뒤 조사(2026-10-22, agy 1.4.5)**: 진짜 `~/.gemini`는 건드리지 않았다(조사 전후 `~/.gemini/antigravity-cli/settings.json` SHA-1 `ccab70e295015776f59b0fc58b59158729fb0005`, `~/.gemini/settings.json` `cac307178e4c190991d89c1832e2063e335ad2ae` 그대로). 경우마다 새 임시 HOME(`HOME=$(mktemp -d)`)에 `settings.json`을 두고 `agy -p … --output-format stream-json`으로 돌렸다. 임시 HOME만으로는 로그인이 안 됐다(조용한 로그인 실패 후 브라우저 OAuth 대기). 로그인 정보는 macOS 키체인에 있다(agy 로그: `keyringAuth: loaded token`, `ChainedAuth: authenticated via keyring`). 그래서 임시 HOME에 `Library/Keychains` 심볼릭 링크 하나만 두어 키체인을 찾게 했다(자격 증명 파일은 복사하지도 읽지도 않았다).
  - `command(echo hi)`는 `echo hi there`를 허용하고 `echo hithere`는 거절한다: **단어 단위 접두어**다. 규칙이 없으면 `echo hi there`는 거절된다(대조군).
  - `;`·`&&`·`|`·`&`(백그라운드)·`>`·`$(…)`가 붙은 명령은 `command(echo hi)` 하나로는 거절된다. agy는 명령을 조각마다 나눠 검사한다: `command(echo hi)`+`command(date)`가 함께 있으면 `echo hi && date`가 실행되고, `command(echo hi)`+`command(tr a-z A-Z)`면 `echo hi | tr a-z A-Z`가 실행된다. 명령 전체를 규칙으로 둔 `command(echo hi && date)`·`command(echo hi | tr a-z A-Z)`도 그 명령을 허용한다.
  - 백틱: `command(echo hi)`로 `echo hi \`date\``를 시키자 단계는 `DONE`(출력 없음)인데 결과의 `denied_actions`에 `command`가 실렸다. 단계만 보고는 거절을 알 수 없는 경우가 있다.
  - `*`: `command(echo *)`는 `echo zebra`를 허용하지 않지만(글롭이 아님) `command(*)`는 `date`를 허용했다 — 모든 명령을 여는 와일드카드다.
  - 규칙 안의 `)`: `command(echo 'a)b')`가 `echo 'a)b' more`를 허용했다(마지막 `)`까지를 인수로 읽는다).
  - `write_file(<폴더>)`는 그 폴더 안의 파일(`…/sub/x.txt`)을 허용하고 이름만 같은 옆 파일(`…/subzzz.txt`)은 거절한다: **경로 단위 접두어**다. `write_file(/)`는 아무 파일이나 허용했다. `write_file(~)`·`write_file(~/)`는 홈 폴더 파일을 허용하지 않았다(`~`를 풀지 않는다).
  - 바이너리 문자열: 대화형 확인 문구 `Yes, and always allow for commands that start with '%s' (Persist to settings.json)`가 접두어 뜻을 확인해 준다. `PERMISSION_SETTINGS_SCOPE_PROJECT`와 "Rules that apply only to this project (highest priority)"가 있지만, 그 프로젝트는 워크스페이스 폴더가 아니라 agy의 프로젝트다(`~/.gemini/config/projects/<프로젝트>.json`의 `permissionGrants`, `persistProjectPermissions`, CLI 실행은 따로 고르지 않으면 모두 `default-cli-project`). 워크스페이스 안에 두는 권한 파일은 찾지 못했고, 헤드리스 실행이 그 프로젝트 규칙을 따르는지도 증명하지 못했다. 그래서 이번만 허용은 계속 전역 설정의 `permissions.allow`를 쓴다(임시 규칙이 있는 동안 이 컴퓨터의 모든 Antigravity 실행에 적용됨을 카드에 적는다).
  - 1.4.5 헤드리스의 도구 이름: `run_command`, `write_to_file`, `replace_file_content`, `multi_replace_file_content`, `sed_file`, `notebook_edit`, `view_file`, `list_dir`, `read_url_content`, `call_mcp_tool`, 브라우저 도구들(1.4.4 기록에는 `write_file`).
  - 확인하지 못한 것(사용자 확인 몫): 이어 보낸 턴이 규칙으로 실제로 실행되는지(앱 전체 흐름). 테스트는 진짜 설정 파일을 건드리지 않고 임시 경로와 가짜 agy로만 했다.

## 정한 것 (사용자 선택 A)
1. **카드**: agy 턴이 거절된 작업을 남기고 끝나면, 그 창에 Claude 권한 카드와 같은 모양의 카드를 띄운다. 카드는 거절된 작업(명령 전체, 주소의 도메인, 그 밖의 작업 이름)을 모두 보여 주고 버튼 셋: **이번만 허용** · **항상 허용** · **거절**. 맥·윈도우·폰(맥·윈도우 호스트 모두) 같은 카드. 권한 '기본'에서도 뜬다(편집 거절도 규칙으로 허용할 수 있으면 같은 카드, 규칙이 없는 작업은 카드에 "권한 모드를 바꾸세요" 안내만).
2. **이번만 허용**: 거절된 작업마다 그것 하나만 허용하는 규칙(명령은 그 명령 전체를 접두어로, 주소는 그 도메인)을 agy 설정의 `permissions.allow`에 잠깐 넣고, **같은 대화를 이어서** 다시 실행한다(이어 보낼 문구: 사용자가 방금 거절된 작업을 허용했으니 이어서 진행하라는 짧은 문장, 4개 언어 문구 키). 그 실행이 끝나면(성공·실패·중지·앱 종료 모두) 앱이 넣은 규칙만 지운다. 같은 규칙을 다른 창이 동시에 쓰면 마지막 사용이 끝날 때 지운다. 앱이 꺼져 규칙이 남았으면 다음 실행 때 지운다(앱이 넣은 규칙 목록을 앱 상태에 기록).
3. **항상 허용**: 같은 규칙을 남긴다(지우지 않음). 이어서 실행하는 것은 이번만 허용과 같다.
4. **거절**: 카드를 닫는다. 이어서 실행하지 않는다.
5. **설정 파일 안전**: 읽을 수 없거나 JSON이 아니면 고치지 않고 카드에 이유를 보인다. 다른 키·순서·값은 그대로 두고 `permissions.allow`만 바꾼다(이미 같은 규칙이 있으면 더하지 않고, 지울 때도 앱이 더한 것만). 원자적으로 쓴다. 전역 설정 외의 파일은 만들지 않는다.
6. **Claude와 다른 점(문서·도움말에 적는다)**: Claude는 실행 전에 멈추고 기다리지만, agy는 그 턴을 끝낸 뒤 묻고 허용하면 이어서 진행한다.
7. 맥이 기준이다. 윈도우 호스트와 폰은 맥과 같은 규칙·문구·동작을 따른다. 원격 작업 공간 창은 그 창을 실행하는 호스트의 설정을 고친다(호스트가 처리).
8. **보안 리뷰 반영(2026-10-22)**: 규칙은 접두어이고 임시 규칙도 있는 동안 이 컴퓨터의 모든 Antigravity 실행에 적용되므로 좁힌다(맥이 기준, 닫힌 목록은 MightyCore `AntigravityRuleSafety`, 윈도우는 같은 것).
   - 규칙 이름은 도구에서 정한다: `run_command`→`command`, `write_file`·`write_to_file`·`replace_file_content`·`multi_replace_file_content`·`sed_file`·`notebook_edit`→`write_file`, `view_file`·`read_file`·`list_dir`→`read_file`, `read_url_content`→`read_url`. 그 밖의 도구와 이름이 맞지 않는 규칙은 규칙 없음(카드는 권한 모드 안내와 거절만).
   - 인수가 비었거나 `/`·`~`·`*`(뒤 `/`·`\`를 떼고 본 것, 드라이브 루트 `C:` 포함)이거나 `*`·`(`·`)`·줄바꿈·NUL을 담은 규칙은 만들지도 받지도 않는다.
   - 위험한 명령은 **이번만**: `;` `&` `|` `>` `<` 백틱 `$(`가 있거나 닫힌 목록의 파괴적 명령(rm, rmdir, unlink, shred, dd, mkfs…, sudo, su, doas, kill, killall, pkill, shutdown, reboot, halt, poweroff, eval, del, erase, rd, format, Remove-Item, taskkill, Stop-Process, Stop-Computer, Restart-Computer, `chmod`/`chown` 재귀, `git push --force|-f|+refspec`, `git reset --hard`, `git clean`, `find -delete`)이 있으면 카드에 항상 허용이 없다(맥·윈도우는 버튼을 끄고 이유를 보이고, 폰은 숨김). 호스트는 그런 규칙의 항상 허용을 409 `remote.error.antigravityOnceOnly`로 거절한다.
   - 단계의 거절은 결과의 `denied_actions`에 그 권한이 있을 때만 센다. `denied_actions`에만 있는 것은 규칙 없는 항목으로 남긴다.
   - 원격 작업 공간(/v2): 호스트가 보고한 카드의 규칙은 한 번만 쓸 수 있다(처음 허용에 캐시에서 지움). `temporary: false`(항상 허용)는 호스트의 '원격에서 전체 권한·명령 실행 허용'이 켜져 있을 때만 받는다(아니면 403 `remote.host.error.antigravityAlways`).
   - 지우기: 실패하면 잠깐씩 늘려 가며 다시 하고, 지운 것이 확인될 때까지 장부에 둔다. 남은 장부 항목은 다음 허용·해제와 실행 때 다시 지운다. 지운 뒤 조금 있다 한 번 더 보고, agy가 다시 써 넣었으면 또 지운다. 앱이 만든 `"permissions": {"allow": []}`·`"allow": []`는 비면 지우고, 앱이 만든 파일이 비면(`{}`) 파일도 지운다. 줄바꿈이 CRLF인 파일에는 CRLF로 넣는다. 끊긴 심볼릭 링크는 바꾸지 않고 거절하고(`unreadable`), UTF-8 BOM으로 시작하는 파일은 맥·윈도우 모두 `notJSON`으로 거절한다.

## 작업
- **T1 맥 + 프로토콜**: 파서(거절된 작업과 대상 모으기), 카드(맥 앱), 설정 규칙 쓰기·지우기(MightyCore, 순수 함수 + 파일 계층), 이어서 실행, 폰 프로토콜(기존 Claude 권한 카드 경로에 맞춘 추가 필드·응답), 문구 4개 언어, 테스트, `docs/mobile-remote.md`.
- **T2 윈도우**(T1 뒤): 같은 것을 윈도우 호스트·WinUI에.
- **T3 폰**(T1 뒤, T2와 병렬): 카드 표시와 응답.
- **T4 문서·도움말**: 도움말 4개 언어(에이전트 창·승인·폰), `docs/` 관련 문서.
- **T5 검증·리뷰·릴리즈**.

## 검사
맥 필터 테스트 + 앱 빌드, 윈도우 Core(기준선) + WinUI 컴파일, 폰 typecheck + jest, locales(두 방식)·디자인 토큰·도움말 검사, 스타일 고정 검사(`OK`, 고정 경로 무변경).
