# Windows Status Line — Implementation Notes

## Shell choice

On Windows the status line command runs in **the shell Claude Code itself uses**: Git Bash when it is installed, PowerShell when it is not (`StatusLineSupport.Shell`). macOS and Linux keep `/bin/sh -c <command>`.

- Git Bash is looked up in `CLAUDE_CODE_GIT_BASH_PATH`, then `%ProgramFiles%\Git\bin\bash.exe`, `%ProgramFiles(x86)%\Git\bin\bash.exe`, `%LocalAppData%\Programs\Git\bin\bash.exe`, and is started as `bash.exe -c <command>`.
- Without Git Bash the command runs as `powershell.exe -NoProfile -NonInteractive -Command <command>`.
- The working directory is the pane's folder (`cwd` of the payload), as in the CLI.

Reason: the feature's promise is that a `statusLine` which works in the CLI works in the app. The official page ([Customize your status line → Windows configuration](https://code.claude.com/docs/en/statusline)) says the CLI routes the command through Git Bash when present and PowerShell otherwise, with forward-slash paths and `~` expanding to the Windows home folder. The first version used `cmd.exe /d /s /c`, which broke exactly the documented examples (`~/.claude/statusline.sh`, `powershell -NoProfile -File C:/Users/…/statusline.ps1` under Git Bash quoting). Shell selection is an OS-bound mechanism; screens, copy, trust rule and limits are unchanged.

## Security decisions

- Process group kill uses the Windows Job Object via `ChildProcess.cs` — equivalent to macOS `killpg`.
- The 8-second timeout (parameterised) kills the entire job object, so background grandchildren cannot orphan.
- `CLAUDE_CODE_STATUSLINE_HOST=mightyclaude` is injected on every run so the command can detect the host.
- No token, credential or pairing key appears in the JSON payload; only the session fields macOS sends.

## WinUI 렌더링

`MainWindow.StatusLine.cs`의 `PaneView.RenderStatusLine(config, untrusted, result, padding)`이 입력창 아래
(`composer` StackPanel의 마지막 슬롯) 에 상태 줄을 그린다. 이 레이어는 Core가 만든 상태를 그리고 답만
되돌려 주며, 탐색·신뢰·실행은 모두 Core에 있다.

- 한 줄당 `TextBlock` 하나, 구간마다 `Run` 하나 — 전경색·굵기·기울임·밑줄을 그대로 적용하고 `StatusLineSupport.MaximumLines`(6)줄까지만 그린다.
- 워크스페이스에서 온 명령은 실행 전에 `StatusLineStrings.TrustPromptTemplate` 질문과 `TrustAllow` / `TrustDeny` 두 버튼을 먼저 보여 준다.
- "이 워크스페이스에서 허용"은 `StatusLineTrust.Trust()`로 지문을 `AppSnapshot.TrustedStatusLines`에 적는다 (Version 1 유지).
- 접근성 이름은 `StatusLineStrings.AccessibilityLabel`("상태 줄").
- `--smoke-test`의 `statusLine` 키가 질문 문구·두 버튼·허용 후 재질문·6줄 제한·색과 굵기를 확인한다. 실제 CLI는 실행하지 않는다.

## macOS와 맞춘 점

1. **256색·24비트 색**: `AnsiColor`가 macOS `ANSISegment.Color`처럼 `Standard`(16색)·`Palette`(0-255)·`Rgb`
   세 종류를 모두 표현한다. `ApplySgr`는 macOS `ANSIText.apply`를 그대로 옮겨 `38;5;n` / `38;2;r;g;b`와
   콜론 형식(`38:5:n`, `38:2::r:g:b`)을 전경·배경 모두에서 파싱하고, 망가지거나 잘린 확장 색은 macOS처럼
   색을 지운다. `AnsiPalette.ToRgb`가 표준 xterm 256색 표(0-15 고정색, 16-231 6×6×6 큐브, 232-255 회색조)를
   순수 함수로 제공하고, WinUI `Terminal()`이 팔레트·RGB 구간을 이 값으로 그린다.
2. **레벨 분리 폴백**: `StatusLineSupport.Discover()`가 이제 `StatusLineDiscovery(Workspace, User)`로 두
   수준을 각각 돌려준다. `StatusLineTrust.Resolve()`가 macOS `AppStore+StatusLine.swift`의
   `gated ? discovery.user : discovery.preferred`를 그대로 옮겨, 워크스페이스 명령이 아직 허용되지
   않은 동안에도 사용자 설정 명령이 있으면 그 명령을 대신 실행한다. 워크스페이스 명령 자체는 허용되기
   전에는 절대 실행되지 않는다.
