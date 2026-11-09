# Mighty Code로 식별자 바꾸기

앱 이름은 0.4.53에서 Mighty Code로 바뀌었다(보이는 이름, 문서, 배포물). 식별자는 두 버전에 걸쳐 바꾼다. 지금 설치된 앱은 번들 ID와 실행 파일 이름이 같은 업데이트만 받아들이므로, 옛 이름의 마지막 버전이 새 이름의 첫 버전을 받아들이도록 먼저 배포한다.

| 버전 | 하는 일 |
|---|---|
| 0.4.53 (다리) | 식별자는 그대로. 업데이트가 다음 버전의 번들 ID `dev.mightycode.native`(`AppUpdateService.successorBundleIdentifiers`)와 `Mighty Code.app` 이름, Windows의 `MightyCode.exe`(`AppUpdateService.SuccessorExecutableName`)를 받아들인다. 맥은 새 이름의 앱을 옛 앱 옆에 설치하고 옛 앱을 지운다(`installScript(… replacing:)`). |
| 0.4.54 (전환) | 아래 표의 식별자를 모두 바꾸고, 첫 실행 때 옛 데이터를 옮긴다. |

## 0.4.54에서 바꾸는 것

| 식별자 | 지금 | 새 값 | 옮기는 방법 |
|---|---|---|---|
| Mac 번들 ID | `dev.mightyclaude.native` (보조: `.helper`, `.remote`, `dev.mightyclaude.screen-share`, `.pane-tab`, `.devicelock`, `.mobile`, 테스트용 접두사) | `dev.mightycode.*` | 첫 실행 때 옛 UserDefaults 도메인(`persistentDomain(forName: "dev.mightyclaude.native")`)을 새 도메인에 복사. 원격 워크스페이스 키(키체인 서비스 `dev.mightyclaude.native.remote`)는 옛 서비스에서 읽어 새 서비스에 다시 쓴다(허용 창 한 번). Claude 계정 사용량을 읽는 `Claude Code-credentials` 항목도 새 앱에 다시 허용을 묻는다. 화면 기록·손쉬운 사용·마이크 권한은 다시 받는다 |
| Mac 앱 파일·실행 파일 | `MightyClaude.app`, `Contents/MacOS/MightyClaude` | `Mighty Code.app`, `MightyCode` | 0.4.53의 업데이트 도우미가 옮긴다. 0.4.54의 도우미는 다음 업데이트부터 같은 이름을 쓴다 |
| Mac 데이터 | `~/Library/Application Support/MightyClaude Native`, `~/.mightyclaude` | `…/MightyCode`, `~/.mightycode` | 새 폴더가 없고 옛 폴더가 있으면 옮긴다(`rename`, 같은 볼륨). 위임 기록의 worktree 경로와 git의 worktree 등록(`git worktree repair`)도 고친다 |
| Windows 실행 파일 | `MightyClaude.exe` | `MightyCode.exe` | 0.4.53이 새 실행 파일을 도우미로 시작한다. 설치 폴더 이름(`Programs\MightyClaude`)은 기존 설치에서는 그대로 두고 새 설치만 `Programs\MightyCode` |
| Windows 데이터·레지스트리 | `%APPDATA%\MightyClaudeNative`, `HKCU\Software\MightyClaudeNative` | `MightyCode` | 맥과 같은 방식으로 옮긴다 |
| Windows 바로 가기 | `{app}\MightyClaude.exe`를 가리킴 | `MightyCode.exe` | 앱 안 업데이트는 바로 가기를 만들지 않으므로, 첫 실행 때 시작 메뉴·바탕화면 바로 가기와 제거 항목의 아이콘 경로를 새 실행 파일로 고친다 |
| 업데이트 경로 | R2 `mightyclaude/` | `mightycode/` | 0.4.54부터 새 경로에 올리고, 옛 앱이 보는 `mightyclaude/` 의 manifest도 한동안 같이 올린다(release.py). manifest 서명 형식 이름 `mightyclaude-update-v1`은 옛 앱이 읽으므로 그대로 둔다 |
| 배포 파일 이름 | `MightyClaude-macos.zip` 등 | `MightyCode-…` | manifest가 주소를 가리키므로 옛 앱에도 상관없다 |
| Android 패키지·스킴 | `xyz.woorizip.mightyclaude`, `mightyclaude` | `xyz.woorizip.mightycode`, `mightycode` | 새 앱이 된다. 옛 앱을 지우고 새로 설치한 뒤 다시 페어링한다(옮길 방법이 없다) |
| 코드 이름 | Swift 타깃 `MightyClaude`, C# `MightyClaude.Core`·`.WinUI`·`.Core.Tests`, 경로 | `MightyCode` | 기계적으로 바꾼다 |

## 0.4.54의 업데이트 도우미가 지킬 것

- Windows: 기존 설치 폴더와 백업 폴더에는 `MightyClaude.exe`가 있다. 설치 트리 확인(`VerifyInstallTree`), 실행 중인 앱 찾기, 되돌리기가 두 이름을 모두 받아야 한다.
- 맥: 옛 앱이 남긴 `MightyClaude.app.update-old`를 복구할 수 있어야 한다.

## 그대로 두는 것

- 로컬 서명 인증서 `Mighty Claude Dev`와 키체인의 백업 항목 이름(이 맥의 키체인에 있다).
- 옛 이름으로 저장된 대화의 알림 머리말(`[Mighty Claude notice …]` 등)을 알아보는 코드.
- 지난 버전의 릴리즈 노트와 작업 기록, 녹화된 대화 fixture.
