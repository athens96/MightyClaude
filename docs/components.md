# 구성 요소 설치 (설정 → 구성 요소)

앱이 이 Mac에서 필요로 하는 것을 한 곳에서 확인하고 다음 단계를 바로 실행한다. 설정 창을 열면 자동으로 검사하고 **다시 확인**으로 갱신한다.

| 항목 | 검사 | 제공하는 조치 |
|---|---|---|
| Tailscale | Homebrew는 `brew --version`으로 먼저 점검한다(Xcode 라이선스 미동의 등으로 실행이 안 되면 App Store 버튼만 보이고 해결 방법을 안내한다). 앱 번들(`/Applications/Tailscale.app`, `~/Applications`)과 CLI(`PATH`, Homebrew, 앱 내장 CLI)를 찾고 `tailscale status --json`의 `BackendState`로 단계를 나눈다: 없음 → 실행 필요 → 로그인 필요 → 연결 필요 → 연결됨 | **Homebrew로 설치**(`brew install --cask tailscale`, 비대화형·자동 업데이트 끔), **App Store에서 설치**(스토어 페이지 열기), **Tailscale 실행**, **로그인**(`tailscale login`이 출력한 URL을 브라우저로 열기, 실패 시 앱 열기), **연결**(`tailscale up`) |
| Claude Code · Codex · Gemini CLI | 실행기 상태(설치·버전). Claude는 Mods 지원 버전(2.1.271 이상)인지도 본다 | 설치되지 않았으면 **설치 명령 복사**(앱은 새 CLI를 직접 설치하지 않는다), Mods 미지원 버전이면 **CLI 업데이트**(기존 업데이트 기능) |
| 필수 플러그인 | `ComponentCatalog.requiredPlugins`에 등록된 마켓플레이스 플러그인을 해당 CLI가 설치된 경우에만 검사한다 | **플러그인 설치**(기존 Claude/Codex 플러그인 서비스로 사용자 범위 설치) |

현재 필수 플러그인 목록은 비어 있다. Mighty 모드·펫·모바일 리모트가 쓰는 Claude Mod(`mods/mighty-bridge`)는 앱에 내장되어 실행마다 `--plugin-dir`로 연결되므로 에이전트에 따로 설치할 것이 없다. 앞으로 어떤 에이전트에 플러그인이 필요해지면 카탈로그에 한 줄 추가하면 설정에 항목이 나타난다.

조치가 끝나면 상태를 다시 검사하고, 모바일 리모트가 켜져 있으면 Tailscale이 연결되는 즉시 리스너를 연다. 셸을 거치지 않고 실행 파일에 고정 인자만 전달하며, 설치·로그인은 한 번에 하나만 진행한다.
