# 폰에서 데스크톱 앱 업데이트

폰이 데스크톱(Mac·Windows) 앱의 업데이트를 보고, 확인하거나 설치를 시작한다. 설치는 데스크톱의 "지금 재시작"과 같은 경로(받기 → 확인 → 설치 → 다시 켜기)이고, 업데이트 파일의 서명·해시·ID 확인은 데스크톱이 지금처럼 한다. 폰은 지시만 한다.

## 프로토콜

- 호스트 hello의 `capabilities`에 `appUpdate`. 없는 호스트에는 폰이 아무것도 띄우지 않는다.
- `GET /m1/state`의 `MobileState.appUpdate`(스스로 업데이트할 수 없는 빌드에서는 빠짐):
  `{ phase: "idle"|"checking"|"upToDate"|"available"|"downloading"|"ready"|"installing"|"failed", current, available?, notes? (≤2,000자), progress? (0…1, downloading), error?, running }`.
  `running`은 지금 실행 중인 요청 수. 값이 바뀌면 state revision이 오른다.
- `POST /m1/app-update` `{ action: "check"|"install", confirm?: Bool }` → 200과 그 뒤의 `MobileAppUpdate`.
  - 잘못된 action: 400 `appUpdate.mobile.badAction`. 업데이트할 수 없는 빌드: 409 `appUpdate.mobile.unavailable`.
  - `install`은 `running > 0`인데 `confirm`이 없으면 409 `appUpdate.mobile.runningRequests`(`{count}`). 아무것도 바꾸지 않는다.
  - `install`: 준비된 업데이트가 있으면 바로 설치하고 앱을 다시 켠다. 없으면 먼저 확인하고(idle·upToDate·failed), 새 버전이 있으면 받고, 준비되면 설치한다. 새 버전이 없으면 요청은 끝난다.
  - `check`: 확인만 한다(데스크톱의 자동 받기 설정은 그대로 따른다).

## 폰 화면

호스트의 세션 목록 위 띠(`phone.appUpdate.*`):

| phase | 띠 |
|---|---|
| available | `phone.appUpdate.available` + **`phone.appUpdate.install`** |
| ready | `phone.appUpdate.ready` + **`phone.appUpdate.install`** |
| checking | `phone.appUpdate.checking` |
| downloading | `phone.appUpdate.downloading` (`{percent}`) |
| installing | `phone.appUpdate.installing` (연결이 끊겼다 다시 붙는다) |
| failed | `phone.appUpdate.failed` + `phone.appUpdate.check` |
| upToDate·idle | 띠 없음 (호스트 메뉴에 `phone.appUpdate.check`) |

설치 단추는 확인을 묻는다: 제목 `phone.appUpdate.confirmTitle`, 본문 `running > 0`이면 `phone.appUpdate.confirmRunning`, 아니면 `phone.appUpdate.confirmIdle`, 단추 `phone.appUpdate.confirm`/`phone.appUpdate.cancel`. 확인하면 `confirm: true`로 보낸다. 노트는 띠를 누르면 펼친다.

## 구현
- Mac: `MobileRemoteModels.swift`(`MobileAppUpdate`, `MobileAppUpdateRequest`, capability), `MobileRemoteService.swift`(`app-update` 경로), `AppStore+PhoneAppUpdate.swift`.
