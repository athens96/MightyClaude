defaults write dev.mightyclaude.native delegation.enabled -bool true

# 에이전트 위임 실제 실행 확인표 (Mac)

맨 위 한 줄은 숨은 위임 스위치 `delegation.enabled`를 켠다. 기본값은 꺼짐이고 설정 화면은 없다. 앱을 끈 채로 터미널에서 실행한다. 스위치가 켜져 있으면 Claude 창에서 새 실행을 시작할 때마다 위임 MCP 서버 `mighty-delegation`이 붙는다. 도구는 `delegate`·`list_children`·`child_status`·`merge`·`follow_up`·`discard` 여섯 개다. 끄려면 `defaults delete dev.mightyclaude.native delegation.enabled`를 실행한다. 꺼도 서버만 떨어진다. 이미 있는 자식 창은 트리·카드·정리가 그대로 동작한다.

macOS CI는 가짜 Claude로 같은 경로를 확인한다. 헤드리스 시나리오 S1–S3과 `--delegation-smoke-test`를 돌리고, S1·S2가 남긴 기록을 기록 스크립트 `scripts/delegation-record.py`로 판정한다. 이 확인표는 실제 Claude CLI와 모델로 한 번 돌려 보는 실행이다. 통과하면 스위치 기본값은 사용자가 켠다.

## 준비

시도마다 새 폴더를 하나 쓴다. 다시 하는 시도는 `run-2`, `run-3` 폴더를 쓴다. 연습 저장소와 앱 프로필이 그 폴더 안에 있으므로 평소 작업 공간과 섞이지 않는다. 기록 스크립트도 그 시도만 판정한다.

```sh
RUN=~/mighty-delegation-practice/run-1
mkdir -p "$RUN/repo" && cd "$RUN/repo"
git init -b main && echo "# practice" > README.md && git add README.md && git commit -m "start"
```

- [ ] 디스크 여유가 10 GB 이상이다. 모자라면 `delegate`가 `low_disk`로 거절한다.
- [ ] Claude 설정의 허용 목록에 `mcp__mighty-delegation__…` 도구가 없다. 있으면 6단계에서 승인 카드가 뜨지 않는다.
- [ ] 빌드 번호를 적었다: `plutil -extract CFBundleVersion raw /Applications/MightyClaude.app/Contents/Info.plist`

앱을 ⌘Q로 끝낸 뒤 이 시도의 프로필로 연다. 4단계에서 다시 열 때도 같은 명령을 쓴다.

```sh
open -a /Applications/MightyClaude.app --args --profile "$RUN/profile"
```

- [ ] `프로젝트 폴더 열기…`로 `$RUN/repo`를 작업 공간에 추가하고 Claude 창을 하나 열었다. 이 창이 부모다.
- [ ] 부모 창 입력창의 권한 메뉴에서 `Auto mode`(auto)를 골랐다.

## 고정 부모 프롬프트

아래 글을 고치지 않고 부모 창에 붙여 넣는다.

```text
이 폴더는 버려도 되는 연습용 git 저장소(throwaway practice repo)다. 일을 Mighty 자식 창(Mighty child pane) 두 개에 나눠 맡겨라. 자식 창은 mighty-delegation 도구의 delegate로만 연다. 네 하위 에이전트 도구(Task·Agent)로 대신하지 말고, 이 저장소의 파일을 직접 고치거나 커밋하지 마라.

1. delegate를 두 번 불러라. 두 번 모두 mode는 auto(auto mode)다.
   - 자식 1의 task: hello.txt에 "hello from child 1" 한 줄을 쓰고 커밋하라.
   - 자식 2의 task: count.txt에 1부터 5까지 한 줄에 하나씩 쓰고 커밋하라. 커밋한 다음에는 보고서를 쓰기 전에 AskUserQuestion 도구로 "보고해도 될까요?"라고 물어라. 보고서는 답을 받은 뒤에 써라.
2. 두 자식의 id를 알려 주고 이번 답을 끝내라. 자식을 기다리며 실행을 이어가지 마라.
3. "[Mighty Claude notice"로 시작하는 알림을 받으면 child_status로 그 자식의 보고를 읽고 한 줄로 요약하라.
4. 내가 시키기 전에는 merge, follow_up, discard를 부르지 마라.
```

## 단계

자식 창에 승인 카드가 뜨면 그 창에서 사람이 답한다. 부모는 자식의 승인에 답하지 못한다. 자식 2의 질문만 3단계까지 답하지 않고 둔다.

1. **위임한다.** 고정 부모 프롬프트를 보낸다.
   - [ ] `delegate` 호출에 승인 카드가 뜨지 않는다(auto).
   - [ ] 부모 옆에 자식 창 두 개가 열리고, 사이드바에서 부모 아래 한 칸 들여 보인다.
   - [ ] 두 자식은 `Auto mode`이고, 각자 `mighty/<자식 id>` 브랜치의 worktree(`~/.mightyclaude/worktrees/<자식 id>`)에서 실행된다.
   - [ ] 부모 실행이 정상으로 끝난다.
2. **자식 1의 알림이 오게 둔다.** 자식 2가 질문하면 사이드바에서 `응답 대기`로 보인다. 그 질문("보고해도 될까요?")에는 아직 답하지 않는다.
   - [ ] 자식 1이 `보고함`이 되고, `[Mighty Claude notice …] Your child … reported revision 1.`이 부모 대화에 한 번 들어간다. 부모가 쉬고 있었으면 이 알림으로 새 실행이 시작되고, 실행 중이었으면 그 실행으로 바로 전달된다.
   - [ ] 부모가 `child_status`로 자식 1의 보고를 읽어 요약하고 실행을 정상으로 끝낸다. `merge`는 부르지 않는다.
3. **자식 2가 보고하기 전에 부모를 멈춘다.** 부모에게 `자식 2를 기다리는 동안 Bash로 sleep 120 을 실행해.`를 보낸다. 그 명령이 도는 동안 입력창의 `실행 중지`를 누른다. 승인 카드가 뜨면 카드가 떠 있는 동안 누른다. 실행이 멈추면 자식 2 창의 질문에 답한다.
   - [ ] 자식 2가 `보고함`이 되어도 부모에서 실행이 시작되지 않는다.
   - [ ] 부모 입력창 위 대기 목록 맨 위에 그 알림 행이 핀 표시(`보류됨`)와 함께 생긴다. 이 행에는 지우기 단추가 없다. 다음 단계는 이 행이 생긴 뒤에 한다.
4. **⌘Q로 끝내고 다시 연다.** 다시 열 때는 준비의 `open` 명령을 쓴다.
   - [ ] 어느 창도 저절로 실행을 시작하지 않는다.
   - [ ] 부모의 보류 행이 그대로 있고, 사이드바의 두 자식은 `보고함`이다.
5. **보낸다.** 부모에게 `보류된 알림이 있으면 child_status로 읽고 한 줄로 요약해. 아직 병합하지 마.`를 보낸다.
   - [ ] 자식 2의 보류 알림이 내 글 앞에 붙어 실행 하나로 들어가고, 보류 행이 사라진다.
6. **부모를 accept edits로 바꾸고 merge 도구로 자식 1을 병합하게 한다.** 부모 실행이 끝나면 입력창 권한 메뉴에서 `Accept file edits`(accept edits, `acceptEdits`)를 고른다. 그다음 `자식 1을 merge 도구로 병합해. expected_head에는 child_status가 준 자식 1의 head를 써. 자식 2는 병합하지 마.`를 보낸다. `merge` 호출의 승인 카드가 뜨면 허용한다.
   - [ ] `merge` 호출에 승인 카드가 뜨고, 허용하기 전에는 아무것도 병합되지 않는다.
   - [ ] 자식 1이 `병합됨`이 된다. `git -C "$RUN/repo" log --oneline --graph main`에 병합 커밋이 없고, main이 자식 1의 커밋까지 앞으로 감겼다(fast-forward).
7. **자식 2를 카드에서 폐기한다.** 사이드바에서 자식 2 카드의 `폐기…`를 누르고, 확인 창에서 `폐기`를 누른다.
   - [ ] 확인 창이 안에 든 worktree를 밝힌다(`안에 들어 있는 다른 worktree는 없습니다.`).
   - [ ] 자식 2의 창이 닫히고, 브랜치 `mighty/<자식 2 id>`와 그 worktree가 사라진다. 자식 1의 브랜치·worktree와 main의 병합은 그대로다.
8. **기록한다.** 창은 하나도 닫지 않는다. MightyClaude 저장소 체크아웃에서 기록 스크립트를 돌린다.

```sh
python3 scripts/delegation-record.py --profile "$RUN/profile" --repo "$RUN/repo" --claude-dir ~/.claude
```

스크립트는 파일을 읽기만 한다. 규칙 16개를 한 번씩 나열한 뒤 `Verdict: passed`, `Verdict: failed`(어긴 규칙 이름과 함께), `Verdict: invalid` 가운데 하나로 끝난다. `--claude-dir`에는 Claude가 대화 기록 `projects/`를 두는 폴더를 준다. Claude를 `CLAUDE_CONFIG_DIR`로 다른 폴더에서 쓰면 그 폴더를 준다.

## 기록 항목

아래 항목은 개인 기록에만 남긴다. 공개 저장소에 남기는 것은 아래 "공개 기록"을 따른다.

- 날짜, 빌드 번호, `claude --version`, 시도 번호(1–3)
- 1–7단계 확인 칸마다 맞았는지. 어긋났으면 본 것을 한 줄로 적는다.
- 기록 스크립트의 출력 전체(규칙 16개와 `Verdict` 줄)
- `git -C "$RUN/repo" log --oneline --graph --all`과 `git -C "$RUN/repo" worktree list`의 출력
- 다시 한 시도라면 앞 시도의 모델 실수
- 실패라면 어긴 앱 규칙과 그 단계

## 통과 규칙

- **통과**: 1–7단계의 확인 칸이 모두 맞고 기록 스크립트가 `Verdict: passed`다.
- **앱 규칙 위반은 그 자리에서 실패다.** 다시 돌리지 않는다. 앱이 맡은 동작이 어긋난 경우가 여기에 든다. 예를 들면 다음과 같다.
  - 기록 스크립트가 `failed`다.
  - 알림이 두 번 오거나 오지 않는다.
  - 멈춘 뒤나 다시 연 뒤 보류 알림이 저절로 전달되거나, 다시 연 앱이 스스로 실행을 시작한다.
  - 보류 행을 지울 수 있거나, 보류 알림이 내 글 뒤에 오거나, 한 번 보낸 것이 실행 둘로 나뉜다.
  - 자식이 요청한 모드보다 넓은 모드로 시작한다.
  - accept edits에서 `merge`가 승인 카드 없이 실행되거나, 도구 병합이 앞으로 감기(fast-forward)가 아니다.
  - 거절된 호출이 무언가를 바꾼다.
  - 앱이 병합되지 않은 작업을 지운다. 폐기 뒤에 자식 2의 브랜치나 worktree가 남는다.
- **모델 실수는 2번까지 다시 돌릴 수 있다**(모두 3번). 다시 돌릴 때는 새 폴더(`run-2`, `run-3`)와 새 프로필로 처음부터 한다. 모델이 고정 프롬프트나 지시를 따르지 않은 경우가 여기에 든다. 예를 들면 다음과 같다.
  - 부모가 자식 창을 쓰지 않고 직접 하거나 자기 하위 에이전트를 쓴다.
  - 시키기 전에 `merge`나 `follow_up`을 부른다.
  - 자식이 커밋하지 않거나 보고서를 쓰지 않는다.
  - 자식 2가 묻지 않고 먼저 보고해서 3단계를 할 수 없다.
  - `expected_head`를 틀리게 넣어 `merge`가 거절된다.

  API 오류나 사용량 한도로 단계가 끊긴 시도도 모델 실수로 센다. 세 번째 시도도 모델 실수로 끝나면 실패다.
- 기록 스크립트가 `invalid`면 판정하지 않는다. 사람이 창을 먼저 닫는 등 순서를 놓쳐서 생겼으면 모델 실수처럼 다시 돌린다. 앱이 창이나 파일을 잃어서 생겼으면 앱 규칙 위반이다.

## 공개 기록

이 저장소는 공개 저장소다. 실제 실행 결과는 통과인지 실패인지, 날짜, 빌드 번호 세 가지만 남긴다. 대화 기록, 프로필, 기록 스크립트 출력, 경로, 세션·자식 id, 화면 캡처는 올리지 않는다. 결과는 이 절 아래에 `YYYY-MM-DD · build NNN · 통과` 또는 `YYYY-MM-DD · build NNN · 실패` 형식의 한 줄로 더한다.

## 끝나고

기록을 저장한 뒤 자식 1 창을 닫으면, 정리 조건이 맞을 때 병합된 자식이 정리된다. 앱이 worktree를 지우고 부모 쪽에서 `git branch -d`를 실행한다(`--force`는 쓰지 않는다). 앱을 ⌘Q로 끝내고 평소처럼 다시 열면 원래 프로필로 돌아온다. 그다음에는 `$RUN` 폴더를 지워도 된다.
