# Codex 승인 요청과 GitLab 인증

로컬 Codex 창의 권한 메뉴에서 **승인 요청**을 선택하면 작업 폴더 쓰기 권한으로 실행하고, 추가 권한이 필요한 명령은 앱에 **이번만 허용 / 거부** 카드로 표시한다. Codex CLI 안정 버전 0.153.4 이상에서 제공한다. 기존 창의 권한 설정은 자동 변경하지 않는다.

이 모드는 `codex app-server --listen stdio://`와 `approval_policy="on-request"`, `approvals_reviewer="user"`를 사용한다. 기존 `codex exec` 경로의 `approval_policy="never"`로는 승인 요청에 응답할 수 없었다. 네트워크 기본 차단은 유지하며, 선택적으로 네트워크 허용을 켤 수 있다. **네트워크 허용만으로 macOS 키체인 접근까지 허용되지는 않는다.** 원격 컴퓨터의 세션에서는 이 승인 연결을 제공하지 않는다.

카드는 전체 명령, 작업 폴더, 네트워크 조건 또는 전체 파일 변경을 보여 준다. 허용은 표시된 요청 한 번에만 적용한다. 세션 전체 승인·정책 저장 승인·추가 권한 묶음·터미널 stdin 승인은 지원하지 않는다. 실행이 종료되거나 요청이 취소되면 이전 카드로 승인할 수 없다. 출력 유실·프로토콜 오류·초기화 시간 초과는 실행 오류로 종료한다.

2026-09-22 읽기 전용 진단 결과:

| 검사 | 결과 |
| --- | --- |
| 샌드박스 안 GitLab DNS | 실패 |
| 샌드박스 안 `glab auth status` | 키체인 접근 실패 |
| 일반 권한 `glab auth status` | 기존 인증 성공 |
| 일반 권한 GitLab 프로젝트 API 조회 | 성공 |
| 일반 권한 `git ls-remote origin HEAD` | 사용자명 확보 실패 |
| 일회성 glab credential helper를 사용한 동일 Git 조회 | 성공 |

해당 환경의 일반 Git helper는 `osxkeychain`이고 glab OAuth 인증과 연결되어 있지 않았다. 기존 glab 인증과 저장소 접근 권한은 정상이라 새 PAT 발급이 필요하지 않았다. 다음은 해당 저장소에서 성공한 **설정을 저장하지 않는 조회 명령**이다. 경로는 실제 glab 설치 위치에 맞춘다.

```sh
git -c credential.helper= \
  -c 'credential.https://gitlab.com.helper=!/opt/homebrew/bin/glab auth git-credential' \
  ls-remote --exit-code origin HEAD
```

Codex에 Git 작업을 요청할 때 기존 glab credential helper를 사용하고, 샌드박스 밖 실행이 필요하면 승인을 요청하도록 지시할 수 있다. 토큰을 프롬프트에 붙여 넣을 필요는 없다. 이번 진단은 로그인·Git 설정 변경·fetch·push·MR 수정을 수행하지 않았다. 인증 결과는 검사 시점에 한하며 만료·권한 변경 후에는 다시 확인해야 한다.

참고: [Codex app-server 승인 프로토콜](https://learn.chatgpt.com/docs/app-server), [glab 인증 저장 방식](https://docs.gitlab.com/cli/authentication/), [GitLab OAuth와 Git credential helper](https://docs.gitlab.com/api/oauth2/).
