---
title: Windows에서 다른 점
order: 9
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
{{kbd:⌘}} 대신 {{kbd:Ctrl}}를 씁니다. 사이드바 접기만 키가 다릅니다.

| Mac | Windows | 동작 |
|---|---|---|
| {{kbd:⌘O}} | {{kbd:Ctrl+O}} | {{ui:menu.openProject}} |
| {{kbd:⌘N}} | {{kbd:Ctrl+N}} | {{ui:menu.newClaudePane}} |
| {{kbd:⌘T}} | {{kbd:Ctrl+T}} | {{ui:menu.addTerminalPane}} |
| {{kbd:⇧⌘E}} | {{kbd:Ctrl+Shift+E}} | {{ui:menu.showFiles}} |
| {{kbd:⌘K}} | {{kbd:Ctrl+K}} | {{ui:menu.searchWorkspaces}} |
| {{kbd:⌃⌘S}} | {{kbd:Ctrl+B}} | {{ui:sidebar.collapse}} / {{ui:sidebar.expand}} |
| {{kbd:⌘,}} | {{kbd:Ctrl+,}} | {{ui:menu.settings}} |
| {{kbd:⌘Enter}} | {{kbd:Ctrl+Enter}} | 실행 중인 Claude에 바로 전달 |

Windows에는 메뉴 막대가 없습니다. 같은 기능은 사이드바와 {{ui:workspace.addPane}} 메뉴에 있습니다.

## 다른 점 {#differences}
| 기능 | Windows에서는 |
|---|---|
| CLI 로그인 | 앱 안의 터미널이 아니라 별도의 Windows 터미널 창에서 진행됩니다. |
| 터미널 창 | Windows PowerShell을 씁니다. |
| 브라우저 창 | Microsoft Edge WebView2로 엽니다. Mac처럼 {{ui:settings.display.browserToggle}}를 켜고 앱을 다시 열어야 합니다. |
| 완료 알림 | {{ui:windows.notifications.toggleLabel}}으로 켭니다. |
| 화면 보기 | 휴대폰 원격의 화면 보기는 됩니다. 화질 옵션은 Mac보다 적습니다. |
| 언어 | 바꾼 언어는 앱을 다시 열면 적용됩니다. |
| 파일 위치 | {{ui:menu.showInFinder}} 대신 {{ui:menu.showInExplorer}}가 있습니다. |

> [!note]
> Windows 앱의 일부 기능은 아직 실제 기기에서 충분히 확인되지 않았습니다. 이상한 점이 있으면 알려 주세요.
