# 실행 창·여러 프로바이더·원격 연결 검증

검증일: 2026-09-16 · 환경: macOS ARM64, Node.js 22.16.0

| 검사 | 결과 |
| --- | --- |
| `npm run check` | TypeScript 검사와 Electron main/preload/renderer 빌드 통과. 단위 테스트 66개 통과, Windows 전용 2개 제외. |
| 브라우저 UI | Playwright + 별도 Chrome 프로필에서 7개 테스트 통과. 프로바이더 전환, 설정 전달·잠금, 원격 가져오기와 모델 출처, 연결 해제 후 11초 경과 시 미연결 유지·수동 재연결 포함. |
| 실제 Electron 앱 | 임시 사용자 프로필에서 Claude·Codex·Gemini 선택, 폴더 선택, 실제 셸 출력, 중지, 재시작 복원 통과. Claude·Codex의 모델·High 등 창별 설정과 테마도 복원됨. 실제 원격 IPC와 공유가 꺼진 원격 화면 확인. |
| 모델 목록 | 설치된 Claude Code 2.1.263 + SDK 0.3.273의 목록과 Codex 0.153.4의 `model/list`를 실제 IPC·화면에서 확인. Gemini 0.43.0 설치 감지. 모델 프롬프트 전송 없음. |
| 원격 실행 | 두 RemoteController를 루프백으로 연결해 실제 RunManager의 셸 출력·중지 확인. 인증·선택 폴더·키 암호화·주소 검증·리디렉션 차단·연결 끊김·작업 만료·서버 종료를 포함한 원격 테스트 13개 통과. |
| Mod 정적 검사 | 설치된 Claude Code 2.1.263의 `plugin validate`에서 Mighty bridge 경고 없이 통과. |
| 패키징 | macOS ARM64 앱 디렉터리 생성 통과. `Resources/mods/mighty-bridge`에 manifest·hooks·타입 파일 포함 확인. |
| 독립 코드 검토 | 실행기·연결·상태 저장·화면 상태를 별도 검토하고 확인된 문제 수정. |

## 검사한 동작

- 워크스페이스 변경과 집중 보기 전환 중 작성 중인 입력 유지
- 실행 창 생성·제거와 활성 창 참조 정리
- 테마·배치·실행 기록의 재시작 복원
- 브라우저 미리보기에서 실제 명령이나 모델을 실행하지 않는 안내
- Mod 이벤트의 실행별 인증, 필드 검증, 전송 크기 제한
- Mod가 원래 `next(event)` 호출과 반환값을 유지하는지 확인
- Claude JSON 응답의 분할 수신·중복 처리와 오류 표시
- 승인된 작업 폴더 사용, 저장 순서 보존
- macOS 자식 프로세스 중지, 부모가 먼저 종료된 경우의 자손 정리
- 취소된 이전 실행의 지연 결과가 같은 창의 새 실행을 덮지 않음
- CLI의 모델명·설명·모델별 effort capability를 화면에 반영
- Haiku의 강도 선택 비활성화와 호환되지 않는 기존 선택 초기화
- 이전 저장 형식의 실행 창에 기본 설정을 추가하고 Vertex `@날짜` 모델 ID 유지
- 서로 다른 실행 창의 설정이 분리된 CLI argv·환경변수로 전달됨. 부모 환경은 변경되지 않음
- 명시 강도의 CLI 옵션·자식 환경·세션 설정 우선순위 일치
- 모델 목록 조회 시간 제한·실패 시 별칭 목록·종료 후 지연 조회 차단
- Codex·Gemini의 stdin 프롬프트, JSONL 응답·재개 ID·오류·중지를 가짜 CLI 프로세스로 검증
- Codex 메타데이터 조회는 초기화와 모델 목록 요청만 전송하며 사용자 턴을 만들지 않음
- 원격·로컬의 같은 경로를 별도 워크스페이스로 유지하고 Windows 원격 경로 복원
- 원격 실행 실패 시 로컬로 실행하지 않음. 동일 실행 창의 로컬·원격 중복 시작과 준비 중 취소 검증
- 연결 키는 워크스페이스 기록에 포함하지 않으며 암호화 불가 시 메모리에만 유지

## 아직 검증하지 않은 범위

- Windows 실기기의 실행·설치. 운영체제 전용 회귀 테스트와 CI 구성을 추가했지만 이 환경에서는 실행하지 않았다.
- 최신 Claude CLI로 실제 모델을 호출하고 Mod 이벤트를 받는 전체 흐름. 앱의 호환 기준은 공개 타입 버전인 2.1.271이며 로컬 CLI는 2.1.263이다.
- Codex·Gemini의 실제 모델 요청과 실제 Tailscale 두 기기 간 연결. 현재 개발 컴퓨터에서 Tailscale CLI를 찾지 못했으며 설치·로그인이나 네트워크 설정을 변경하지 않았다.
- 도구 승인 UI, 대화형 PTY, Git worktree 격리. 현재 골격의 구현 범위 밖이다.
- 배포용 서명·공증·자동 업데이트. 생성한 macOS 앱은 로컬 개발 패키지다.

화면 캡처는 `artifacts/mightyclaude-desktop.png`, `artifacts/mightyclaude-run-settings.png`, `artifacts/mightyclaude-providers.png`, `artifacts/mightyclaude-remote.png`에 있다. 테스트는 사용자 워크스페이스 상태와 분리된 임시 앱 프로필을 사용했다. 실제 CLI 감지와 모델 목록을 검증하려면 `MIGHTY_REQUIRE_MODEL_CATALOG=1 MIGHTY_REQUIRE_PROVIDERS=1 node scripts/smoke-desktop.mjs`로 실행한다. CLI 로그인과 제공자 설정은 기존 환경을 사용하며 모델 요청은 보내지 않는다.
