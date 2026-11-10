# Mighty Code로 식별자 바꾸기

앱 이름은 0.4.53에서 Mighty Code로 바뀌었고(보이는 이름, 문서, 배포물), 식별자는 0.4.54에서 바뀐다. 0.4.53 이하는 번들 ID와 실행 파일 이름이 같은 업데이트만 받아들였으므로, 0.4.53이 다리 역할을 한다.

| 버전 | 하는 일 |
|---|---|
| 0.4.53 (다리) | 식별자는 옛 것 그대로. 맥 업데이트가 다음 버전의 번들 ID `dev.mightycode.native`(`AppUpdateService.successorBundleIdentifiers`)를 받아들이고, 이름이 다른 새 앱을 옛 앱 옆에 설치한 뒤 옛 앱을 지운다(`installScript(… replacing:)`). Windows 업데이트는 `MightyCode.exe`가 든 패키지를 받아 그 실행 파일을 도우미로 시작한다. |
| 0.4.54 (전환) | 아래 표대로 바꾸고, 첫 실행 때 옛 데이터를 옮긴다. |

## 0.4.54에서 바뀐 것

| 식별자 | 0.4.53까지 | 0.4.54부터 | 옮기는 방법 |
|---|---|---|---|
| Mac 번들 ID | `dev.mightyclaude.native` 와 보조 ID들 | `dev.mightycode.*` | 첫 실행 때 옛 설정 도메인(`persistentDomain(forName: "dev.mightyclaude.native")`)을 새 설정에 한 번 복사(`RenameMigration`). 원격 워크스페이스 키는 처음 읽을 때 옛 키체인 서비스 `dev.mightyclaude.native.remote`에서 새 서비스로 옮긴다(`RemoteKeychain`, 허용 창 한 번). 화면 기록·손쉬운 사용·마이크·카메라 권한과 Claude 사용량을 읽는 `Claude Code-credentials` 허용은 다시 받는다 |
| Mac 앱 파일·실행 파일 | `MightyClaude.app`, `MightyClaude` | `MightyCode.app`, `MightyCode` (Finder와 Dock에는 `LSHasLocalizedDisplayName`로 "Mighty Code") | 0.4.53의 업데이트 도우미가 새 이름으로 설치하고 옛 앱을 지운다 |
| Mac 데이터 | `Application Support/MightyClaude Native`, `~/.mightyclaude` | `Application Support/MightyCode`, `~/.mightycode` | 새 폴더가 없을 때 옛 폴더를 옮기고, 위임·워크스페이스 기록의 worktree 경로를 고치고, 각 worktree에서 `git worktree repair`, Claude의 자식 대화 폴더(`~/.claude/projects/…--mightyclaude-worktrees-…`)도 옮긴다(`RenameMigration`) |
| Windows 실행 파일 | `MightyClaude.exe` | `MightyCode.exe` | 업데이트 도우미가 설치 폴더 확인·되돌리기·실행 중인 앱 찾기에서 두 이름을 모두 받는다(`AppUpdateService.InstalledExecutableNames`). 앱 안 업데이트로 온 설치는 폴더 이름(`Programs\MightyClaude`)을 그대로 쓴다 |
| Windows 데이터·레지스트리 | `%APPDATA%\MightyClaudeNative`, `HKCU\Software\MightyClaudeNative`, `%USERPROFILE%\.mightyclaude` | `MightyCode`, `.mightycode` | Windows의 `RenameMigration` (맥과 같은 방식) |
| Windows 바로 가기 | `MightyClaude.exe`를 가리킴 | `MightyCode.exe` | 첫 실행 때 시작 메뉴·바탕화면 바로 가기와 제거 항목의 아이콘을 새 실행 파일로 고친다 |
| 링크 스킴 | `mightyclaude://open`, 페어링 `mightyclaude://pair` | `mightycode://…` | 앱과 폰은 옛 스킴도 계속 받는다(옛 버전이 설치한 `mighty` 명령, 아직 업데이트하지 않은 데스크톱의 페어링 QR) |
| R2 | 버킷 `mightyclaude`, `https://pub-fd035e0a9ad7411f8d8d8963cc2b9702.r2.dev/mightyclaude/` | 버킷 `mightycode`, `https://pub-b705901d18c940159d40f58683d758ad.r2.dev/mightycode/` | `release.py upload`가 새 버킷에 올린 뒤, 같은 서명된 `latest.json`과 새 주소로 보내는 페이지를 옛 버킷에도 쓴다(`LEGACY_BUCKET`). 옛 앱은 거기서 업데이트를 찾는다 |
| 배포 파일 | `MightyClaude-macos.zip` 등 | `MightyCode-…` | manifest가 주소를 가리키므로 옛 앱에도 상관없다 |
| Android·iOS | `xyz.woorizip.mightyclaude` | `xyz.woorizip.mightycode` | 새 앱이 된다. 옛 앱을 지우고 새로 설치한 뒤 다시 페어링한다 |
| Firebase | (옛 프로젝트) | 프로젝트 `mighty-code-15127` | 설정 파일은 git 밖 `~/.config/mightycode/firebase/`(`GOOGLE_SERVICES_JSON`, `GOOGLE_SERVICE_INFO_PLIST`), EAS에는 파일 환경 변수로 둔다. 릴레이의 FCM 서비스 계정 키도 새 프로젝트 것으로 바꾼다(`relay/deploy/oracle/setup.sh`) |
| Expo | `@mightyclaude/mightyclaude-mobile` | `@mighty-code/might-code` (projectId `f559dc82-…`) | |
| 코드 | Swift 타깃 `MightyClaude`, C# `MightyClaude.*` | `MightyCode`, `MightyCode.*` | |

## 그대로 둔 것

기기끼리, 또는 릴레이와 주고받는 규약 이름은 옛 버전과 계속 통해야 하므로 바꾸지 않았다.

- 릴레이 HKDF `mightyclaude-relay-v1`, 푸시 봉인 키 `mightyclaude.push.v1.*`, 로컬 네트워크 발견 `_mightyclaude._tcp`, 업데이트 서명 형식 `mightyclaude-update-v1`/`-v2`, 릴레이 도메인 `mightyclaude.duckdns.org`, 내부 가짜 주소 `*.mightyclaude.invalid`.
- 폰 저장소 키(`mightyclaude.hosts.v2.index` 등), 상태 줄 환경 변수 `CLAUDE_CODE_STATUSLINE_HOST=mightyclaude`(사용자 스크립트가 본다).
- 로컬 서명 인증서 `Mighty Claude Dev`와 키체인 백업 항목, 업데이트 서명 키의 로컬 경로 `~/.config/mightyclaude/update-signing.key`.
- 옛 이름으로 저장된 대화의 알림 머리말(`[Mighty Claude notice …]` 등)을 알아보는 코드.
- 지난 버전의 릴리즈 노트와 작업 기록, 녹화된 대화 fixture.
