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

## macOS와 아직 다른 점 (보류 — docs/windows-parity.md)

1. **256색·24비트 색**: Core의 `AnsiColor`는 macOS의 16색 이름만 가진다. `38;5;n` / `38;2;r;g;b`를 쓰는
   명령은 기본색으로 보인다. macOS는 팔레트와 RGB를 모두 그린다.
2. **레벨 분리 폴백**: macOS는 워크스페이스 명령이 아직 허용되지 않으면 사용자 설정 명령을 대신 실행한다.
   Windows `Discover()`는 우선순위에서 이긴 하나만 돌려주므로, 거절된 동안에는 상태 줄이 비어 있다.

둘 다 화면에 보이는 차이라 임의로 정하지 않고 보류 행으로 남긴다.
