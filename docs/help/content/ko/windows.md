---
title: Windows에서 다른 점
order: 11
section: windows
---
이 도움말은 Mac 화면을 기준으로 씁니다. Windows 앱도 같은 기능을 같은 이름으로 제공하며, 여기에는 다른 부분만 모았습니다.

> [!note]
> Windows 앱은 {{ui:badge.beta}}입니다. 창 제목에 베타 표시가 붙습니다.

## 설치 {#install}
1. [다운로드 페이지](https://pub-fd035e0a9ad7411f8d8d8963cc2b9702.r2.dev/mightyclaude/index.html)에서 내 PC에 맞는 zip을 받습니다. 대부분의 PC는 x64, Snapdragon 같은 ARM PC는 ARM64입니다.
2. zip을 폴더째 풉니다.
3. 폴더 안의 `MightyClaude.exe`를 실행합니다. 함께 들어 있는 파일도 필요하므로 exe만 다른 곳으로 옮기지 마세요.

Windows 10(버전 2004) 이상 또는 Windows 11에서 동작합니다. 필요한 .NET 런타임은 zip에 들어 있습니다.

> [!warning]
> 이 앱은 코드 서명이 없어 Windows가 처음 실행을 막을 수 있습니다. 앱이 Visual C++ 런타임을 찾지 못한다고 하면 Microsoft Visual C++ 재배포 가능 패키지(v14)를 설치하세요. 터미널과 브라우저 창에는 Microsoft Edge WebView2 런타임이 필요합니다.

업데이트는 Mac과 같이 {{ui:menu.settings}} → {{ui:settings.nav.about}}에서 받습니다.

## 단축키 {#shortcuts}
{{kbd:⌘}} 대신 {{kbd:Ctrl}}를 씁니다. 사이드바 접기와 원격 화면 멈추기만 키가 다릅니다.

| Mac | Windows | 동작 |
|---|---|---|
| {{kbd:⌘O}} | {{kbd:Ctrl+O}} | {{ui:menu.openProject}} |
| {{kbd:⌘N}} | {{kbd:Ctrl+N}} | {{ui:menu.newClaudePane}} |
| {{kbd:⌘T}} | {{kbd:Ctrl+T}} | {{ui:menu.addTerminalPane}} |
| {{kbd:⇧⌘E}} | {{kbd:Ctrl+Shift+E}} | {{ui:menu.showFiles}} |
| {{kbd:⌘K}} | {{kbd:Ctrl+K}} | {{ui:menu.searchWorkspaces}} |
| {{kbd:⌃⌘S}} | {{kbd:Ctrl+B}} | {{ui:sidebar.collapse}} / {{ui:sidebar.expand}} |
| {{kbd:⌘,}} | {{kbd:Ctrl+,}} | {{ui:menu.settings}} |
| {{kbd:⌘=}} / {{kbd:⌘-}} / {{kbd:⌘0}} | {{kbd:Ctrl+=}} / {{kbd:Ctrl+-}} / {{kbd:Ctrl+0}} | {{ui:menu.textBigger}} / {{ui:menu.textSmaller}} / {{ui:menu.textActualSize}} |
| {{kbd:⌘Enter}} | {{kbd:Ctrl+Enter}} | 새 worktree에서 바로 실행, 또는 실행 중인 Claude에 바로 전달 |
| {{kbd:⌃⌥⌘K}} | {{kbd:Ctrl+Alt+Esc}} | 휴대폰의 원격 화면 세션을 모두 멈춤([화면 보기](phone.md#screen)) |

Windows에는 메뉴 막대가 없습니다. 같은 기능은 사이드바와 {{ui:workspace.addPane}} 메뉴에 있습니다.

## 다른 점 {#differences}
| 기능 | Windows에서는 |
|---|---|
| 필요한 도구 | [{{ui:setup.title}}](getting-started.md#setup) 설치는 별도의 PowerShell 창에서 진행되고, winget과 각 CLI의 공식 PowerShell 설치 스크립트를 씁니다. Homebrew, Bun, gstack은 목록에 없습니다. {{ui:setup.button.neverShow}}는 레지스트리 `HKEY_CURRENT_USER\Software\MightyClaudeNative`의 `setup.neverShow` 값(REG_DWORD)으로 저장됩니다. |
| CLI 로그인 | 앱 안의 터미널이 아니라 별도의 Windows 터미널 창에서 진행됩니다. 로그인이 끝나면 Mac처럼 실패한 요청을 저절로 다시 보냅니다. |
| Antigravity | PowerShell 설치 프로그램으로 `%LOCALAPPDATA%\agy\bin`에 설치하고, 기록은 `%USERPROFILE%\.gemini\antigravity-cli`에 남습니다([Antigravity](agent-pane.md#antigravity)). |
| 터미널 창 | Windows PowerShell을 씁니다. |
| 브라우저 창 | Microsoft Edge WebView2로 엽니다. Mac처럼 {{ui:settings.display.browserToggle}}를 켜고 앱을 다시 열어야 합니다. |
| 완료 알림 | {{ui:windows.notifications.toggleLabel}}으로 켭니다. |
| 화면 보기 | 휴대폰 원격의 화면 보기는 됩니다. 화질 옵션은 Mac보다 적습니다. 모든 원격 화면 세션은 {{kbd:Ctrl+Alt+Esc}}로 바로 멈춥니다. |
| 펫 말풍선 | 승인과 질문 말풍선에 기호 단추가 하나 더 있습니다. 승인에서는 요청 전체를 보는 창을, 질문에서는 답을 직접 써서 보내는 창을 엽니다. |
| 언어 | 바꾼 언어는 앱을 다시 열면 적용됩니다. |
| 파일 위치 | {{ui:menu.showInFinder}} 대신 {{ui:menu.showInExplorer}}가 있습니다. |
| 마이티 오케스트레이션 | 기본으로 켜져 있고 설정의 스위치로 끕니다. 이 스위치는 레지스트리 값이기도 합니다([켜고 끄기](delegation.md#enable)). worktree는 `%USERPROFILE%\.mightyclaude\worktrees`에 생기고, 안에 정션이나 심볼릭 링크가 있으면 자동으로 정리하지 않습니다([창 닫기와 정리](delegation.md#close)). {{ui:queue.worktree}} 버튼을 오른쪽 클릭하면 다른 에이전트의 자식에서 요청을 실행합니다([새 worktree에서 실행하기](delegation.md#worktree)). |
| 글자 크기와 글꼴 | {{ui:settings.nav.general}} › {{ui:settings.text.sectionTitle}}에서 Mac처럼 크기(85~150%)와 {{ui:settings.text.uiFontLabel}}, {{ui:settings.text.transcriptFontLabel}}, {{ui:settings.text.codeFontLabel}}을 고르고, 열린 창이 모두 바로 따라갑니다. {{kbd:Ctrl+=}} / {{kbd:Ctrl+-}} / {{kbd:Ctrl+0}}도 되지만, 터미널이나 브라우저 창에 커서가 있으면 이 키는 그 창이 씁니다. 값은 레지스트리 `HKEY_CURRENT_USER\Software\MightyClaudeNative`에 저장됩니다. 확인 창 본문처럼 Windows 기본 컨트롤이 정하는 글자는 앱을 다시 열면 바뀌고, 펫 말풍선은 아직 따라가지 않습니다. |
| Tailscale로 직접 연결 | 휴대폰이 Windows PC에도 [직접 연결](phone.md#direct)합니다. Windows 방화벽 규칙은 {{ui:settings.nav.mobile}} 탭의 {{ui:mobileRemote.direct.windows.firewall.addButton}}로 더하고, Windows의 관리자 확인을 한 번 거칩니다. 앱이 관리자 권한을 묻는 곳은 여기뿐입니다. |

> [!warning]
> Tailscale로 직접 연결은 기본으로 켜져 있어서, 앱이 처음 휴대폰 연결을 기다릴 때 Windows Defender 방화벽이 이 앱의 일부 기능을 차단했다는 자체 알림을 띄울 수 있습니다. **액세스 허용**을 고르세요(Windows가 이 앱에 더 넓은 자체 규칙을 더합니다). **취소**를 골랐다면 Windows가 `MightyClaude.exe`를 막는 규칙을 더했을 수 있고, 그 규칙이 앱의 규칙보다 우선합니다. Windows Defender 방화벽 → 고급 설정 → 인바운드 규칙에서 Mighty Claude를 막는 규칙을 지우세요.

> [!note]
> Windows 앱의 일부 기능은 아직 실제 기기에서 충분히 확인되지 않았습니다. 이상한 점이 있으면 알려 주세요.
