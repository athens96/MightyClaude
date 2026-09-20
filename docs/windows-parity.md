# Windows 기능 대응 목록

macOS 코드베이스에서 발견한 모든 기능 영역과 Windows 구현 현황.
단계 셀은 `1단계 | 터미널 | 2단계 | 3단계 | 이미 있음 | Windows에 해당 없음 | 배포 인프라` 중 하나.
'확인 필요' 표시는 단계가 판단으로 결정된 행. '보류'는 macOS와의 차이를 사용자가 정해야 하는 행 ('확인 필요'와 다른 표시).

| 기능 영역 | macOS 근거 | Windows 현황 | 단계 | 근거 | 확인 |
|---|---|---|---|---|---|
| 슬래시 명령 완성 | `SlashCommands.swift`, `SlashCommandPalette.swift`, `AppStore+SlashCommands.swift` | `SlashCommands.cs`(`SlashPalette`·`SlashPaletteState`), `SlashCommandStrings.cs`, WinUI `MainWindow.SlashPalette.cs` — 입력창 위 완성 목록·행·배지·표식·푸터·개수, ↑↓ 이동 / Enter·Tab·클릭 선택 / Esc 닫기; `SlashPaletteVerification.cs` 10개 검사 Mac 통과; 스모크 키 `slashCommandPalette`가 실제 컨트롤을 구동 | 1단계 | P1 합의 (interview_20260919_235018) | 완료 — `9b47538`(Windows x64·arm64 성공). Core `14d8613`·`6114df4`, 화면 `685b3a1`. `OpenPlugins`(`/plugin`·`/plugins`)는 플러그인 화면이 생길 때 돌아온다(`docs/windows-slash-commands.md`). 기기 미확인 |
| 상태 표시줄 | `StatusLine.swift`, `StatusLineView.swift`, `AppStore+StatusLine.swift` | `StatusLine.cs`, `StatusLineTrust.cs`, `StatusLineStrings.cs` — Core 구현 완료; `StatusLineVerification.cs` 8개 테스트 통과 (Mac); WinUI `MainWindow.StatusLine.cs` — 입력창 아래 렌더링·신뢰 질문·스모크 키 `statusLine` | 1단계 | P1 합의 (interview_20260919_235018) | 진행 중 — Core·문구·검사·렌더러는 있으나 실제 실행 창에 연결되지 않았다(`RenderStatusLine`을 스모크만 호출). 명령에 넘기는 JSON도 Claude Code의 형식과 다르다. 앞서 적은 "완료"는 잘못이었다(2026-09-20 점검). 수정 실행 중 |
| 상태 줄 256색·RGB | `StatusLine.swift` `ANSISegment.Color.palette/.rgb` | 보류 — Windows `AnsiColor`는 macOS의 16색 이름만 지원. `38;5;n` / `38;2;r;g;b`는 기본색으로 표시됨 | 1단계 | 보류 (v4 묶음 전 사용자 판단) | 완료 — `ddea1de`. macOS와 같은 파서·xterm 팔레트 |
| 상태 줄 레벨 분리 폴백 | `AppStore+StatusLine.swift` `gated ? discovery.user : discovery.preferred` | 보류 — Windows `Discover()`는 우선순위 1개만 반환하므로, 워크스페이스 명령이 거절되면 사용자 설정 명령으로 되돌아가지 않고 상태 줄이 비어 있음 | 1단계 | 보류 (v4 묶음 전 사용자 판단) | 완료 — `ddea1de`. 워크스페이스 명령이 허락을 기다리는 동안 사용자 수준 명령을 쓴다 |
| CLI 계정 전환 | `CLIAccounts.swift`, `CLIAccountsSettingsView.swift`, `AppStore+CLIAccounts.swift` | 없음 | 1단계 | P1 합의 (interview_20260919_235018) | |
| 계정 사용량 표시 | `AccountUsageService.swift`, `AccountUsageSnapshot.swift`; docs/session-usage.md | `ActivityUsageVerification.cs` (테스트만); 서비스 미구현 | 1단계 | P1 합의 — "usage display, off by default" | |
| CLI 업데이트 | `CLIUpdateService.swift`, `CLIUpdateSettingsView.swift`, `AppStore+CLIUpdates.swift` | `CliUpdateService.cs`, `CliUpdateStrings.cs` — 설치된 CLI만 그 설치 방식(native·winget·npm)으로 업데이트, 상태 `updated`/`current`/`skipped`/`failed`/`cancelled`/`busy`, 동시 1건·취소·종료, 시작 시 자동 업데이트 게이트; 가짜 러너로 `cli update …` 검사 10개 Mac 통과(`CliUpdateVerification.cs`). 규칙은 `docs/windows-cli-update.md` | 1단계 | P1 합의 (interview_20260919_235018) | 진행 중 — 실행기·규칙·검사·섹션은 있으나 업데이트를 시작하는 경로가 없다(`업데이트 하기` 버튼과 앱 시작 시 자동 실행이 없음, 2026-09-20 점검). 수정 실행 중 |
| CLI 업데이트 winget 문구 | `CLIUpdateService.swift`의 Homebrew 문장 2개 | 보류 — Windows에는 Homebrew가 없어 `설치된 winget의 해당 패키지만 업데이트합니다.` · `winget 설치이지만 winget 실행 파일을 찾지 못했습니다.` 두 문장이 macOS 원문 없이 들어간다 | 1단계 | 보류 (v4 묶음 전 사용자 판단) | 확인 필요 — Homebrew를 winget으로 바꾼 OS 이름 치환 문장 2개(`docs/windows-cli-update.md`). 보류가 아니라 문구 확인 항목 |
| 앱 자체 업데이트 | `AppUpdate.swift`, `AppUpdateSettingsView.swift`, `AppStore+AppUpdate.swift` | `latest.json`에 windows.x64/arm64 블록 예약됨; 클라이언트 미구현 | 1단계 | P1 합의 (interview_20260919_235018) | |
| 세션·창 이름 변경 | `AppStore+Rename.swift`, `RenameViews.swift`, `RenameDiagnostics.swift` | 없음 (사이드바 목록은 있음) | 1단계 | macOS 동일 화면 원칙; P1 기본 UX | 확인 필요 |
| Claude 플러그인 목록 | `ClaudePluginService.swift`, `ClaudePluginModels.swift`, `AppStore+Plugins.swift` | 없음 | 1단계 | 슬래시 명령 완성의 전제 조건 | 확인 필요 |
| Codex 플러그인 | `CodexPluginService.swift` | 없음 | 1단계 | Codex 사용 시 플러그인 인식 필요 | 확인 필요 |
| 설정 화면 | `SettingsViews.swift`의 `Form` 본문 (섹션 순서) | `SettingsSections.cs`(Core) — macOS 슬롯 12개를 순서대로 담고 Windows에 있는 것만 보여준다: `화면` → `CLI 업데이트` → `이 PC의 CLI` → `앱 정보`. 테마·완료 알림 스위치는 `화면`, 실행기 목록은 `이 PC의 CLI`로 동작 그대로 이동. WinUI `MainWindow.Settings.cs`는 슬롯별 빌더만 붙인다(`BuilderFor`). 아직 없는 기능(원격 연결·스타일·컴포넌트·모바일 원격·컴패니언·CLI 계정·Claude Mods·앱 업데이트)은 표시하지 않는다. `settings sections …` 검사 5개 Mac 통과(`SettingsSectionsVerification.cs`); 스모크 키 `settingsSections`; 등록 방법은 `docs/windows-settings-groundwork.md` | 1단계 | P1 기능(계정·업데이트)이 설정 화면 필요 | Core·화면 완료 — 순서·누락 섹션·자동 업데이트 스위치 반전/복원은 Mac에서 검증. 기기 미확인 |
| 인터랙티브 터미널 | `LocalTerminalSession.swift`, `LocalTerminalView.swift` (Ghostty/Metal/PTY) | `shell` 실행 창 (요청별 실행, PTY 없음, 상태 비유지) | 터미널 | P1·P2 사이 별도 항목으로 명시 (interview_20260919_235018) | |
| 스타일 엔진 | `Styles/*.swift`, `AppStore+Styles.swift`, `StyleApprovalSheet.swift` | 없음 | 2단계 | P2 합의 (interview_20260919_235018) | |
| 가이드 패널 | `GuidedPanel.swift`, `GuidedActionChip.swift`, `AgentQuestionPanel.swift` | 없음 | 2단계 | P2 합의 (interview_20260919_235018) | |
| 실행 그래프 | `MightyGraph.swift`, `ExecutionGraph.swift`, `MightyGraphView.swift` | 없음 | 2단계 | P2 합의 (interview_20260919_235018) | |
| 컴포넌트 (설치 대시보드) | `Components.swift`, `AppStore+Components.swift`, `ComponentsSettingsView.swift`; docs/components.md | 없음 | 2단계 | 스타일 엔진·플러그인 상태 확인 UI; P2 완성도 | 확인 필요 |
| 워크스페이스 Git 정보 | `WorkspaceGitInfo.swift`, `WorkspaceGitView.swift` | 없음 | 2단계 | 그래프 화면 보조 정보; P2 작업과 연관 | 확인 필요 |
| 컨텍스트 압축 표시 | `ContextCompaction.swift` | 없음 | 2단계 | CLI 이벤트 처리; P2 필기록 완성도 | 확인 필요 |
| 그래프 블록 크기 조정 | `AppStore+GraphResize.swift`, `MightyGraphBlockSize.swift` | 없음 | 2단계 | 실행 그래프 내 UX; P2 작업에 포함 | 확인 필요 |
| 참조 링크 버블 | `ReferenceLinks.swift`, `MightyGraphReferenceBubble.swift` | 없음 | 2단계 | 실행 그래프에서 표시되는 참조 링크 | 확인 필요 |
| 세션 템플릿 | `SessionTemplate.swift` | 없음 | 2단계 | 상태 복구·재개; P2 이후 UX | 확인 필요 |
| 동반 펫 | `CompanionPet.swift`, `CompanionCarousel.swift`, `CompanionBubbleController.swift` | 없음 | 3단계 | P3 합의 (interview_20260919_235018) | |
| 모바일 릴레이 호스트 | `MobileRemoteService.swift`, `RelayChannel.swift`, `MobileDeviceRegistry.swift` | 없음 | 3단계 | P3 합의 (interview_20260919_235018) | |
| 사용자 설문 | `UserQuestionnaire.swift`, `UserQuestionnaireCard.swift`, `QuestionnaireProgress.swift` | 없음 | 3단계 | 온보딩 설문; P3 UX 완성도 | 확인 필요 |
| 한글 폴백 입력 | `HangulComposer.swift`, `HangulFallback.swift`, `InputMethodSymptom.swift` | 해당 없음 (Windows IME 정상) | Windows에 해당 없음 | 명시적 "Never on Windows" (interview_20260919_235018) | |
| 실행 창 레이아웃 | `PaneLayout.swift`, `AppStore+PaneLayouts.swift` | `PaneLayout.cs`, `PaneLayoutVerification.cs` — 구현됨 | 이미 있음 | `PaneLayout.cs` 존재 및 구조 일치 | |
| 에이전트 활동 | `AgentActivity.swift`, `AgentRunTiming.swift` | `AgentActivity.cs`, `AgentRunTiming.cs` — 구현됨 | 이미 있음 | 대응 C# 파일 존재 | |
| 에이전트 필기록 | `AgentTranscriptView.swift`, `AgentTranscriptFormat.swift` | `AgentTranscript.cs` — 구현됨 | 이미 있음 | `AgentTranscript.cs` 존재 | |
| 파일 첨부 | `Attachments.swift`, `ComposerAttachments.swift` | `AttachmentSupport.cs`, `AttachmentInput.cs` — 구현됨 | 이미 있음 | `AttachmentSupport.cs`, `AttachmentInput.cs` 존재 | |
| 원격 워크스페이스 | `Remote/RemoteService.swift`, `TailscaleDiscovery.swift` | `RemoteServer.cs`, `RemoteController.cs`, `RemoteNetwork.cs` — 구현됨 | 이미 있음 | 대응 C# 파일 존재 | |
| 세션 컨텍스트 사용량 | `SessionUsage.swift` | `SessionUsage.cs`; `MainWindow.cs` context 버튼 — 구현됨 | 이미 있음 | `SessionUsage.cs` 및 UI 구현 존재 | |
| 모드 브릿지 | `ModBridge.swift` | `ModBridge.cs` — 구현됨 | 이미 있음 | `ModBridge.cs` 존재 | |
| 프로바이더 카탈로그 | `ProviderService.swift`, `ProviderMark.swift` | `ProviderCatalog.cs` — 구현됨 | 이미 있음 | `ProviderCatalog.cs` 존재 | |
| IME 조합 처리 | `NativeComposerEditor.swift` (macOS AppKit IME) | `MainWindow.cs` `suppressCompositionEnter` 로직 — 구현됨 | 이미 있음 | Enter 억제 코드 `MainWindow.cs:271-279` 존재 | |
| 권한 모드 (모드 선택·자동 허용) | `ToolPermissions.swift`, `ToolPermissionPresentation.swift` | `RunSettings.PermissionMode`; `AutoPermissionVerification.cs` — 구현됨 | 이미 있음 | 권한 모드 열거 및 자동 모드 구현됨. 앱 내 승인 채널은 아래 행으로 분리 | |
| Claude 추가 권한 요청의 앱 내 승인 (실행 창·펫 말풍선) | `ToolPermissions.swift`(`can_use_tool` 처리), `ToolPermissionBar.swift`, `AgentCompanion.swift` | `ToolPermissions.cs`(승인 채널)·`ToolPermissionPresentation.cs`·`ToolPermissionStrings.cs`, WinUI `MainWindow.ToolPermission.cs` — 실행 창의 승인 막대(제목·요약·이유·경로·대기 수, `이번만 허용`/`거부`); `tool permission` 검사 Mac 통과; 스모크 키 `toolPermission`; 설문·펫 말풍선·스타일 자동 허용은 범위 밖(`docs/windows-tool-permissions.md`) | 1단계 | 승인 채널이 없으면 자동 허용 밖의 도구 요청에서 실행이 멈춘다; 핵심 작업 흐름 | 완료 — `374841b`(Windows x64·arm64 성공). Core `0c0db9f`·`5f997ed`, 실행 경로 `68077d9`, 화면 `e30c797`. 기기 미확인 |
| 완료 알림 (시스템 알림) | `AgentCompanion.swift`(`UNUserNotificationCenter`) | `CompletionNotifications.cs`(결정 논리)·`CompletionNotificationStrings.cs`(문구), WinUI `MainWindow.CompletionNotification.cs`(`ICompletionNotifier`·`WindowsAppNotifier`·설정 섹션·스모크); `CompletionNotificationVerification.cs` 7개 검사 Mac 통과; 스모크 키 `completionNotification`; `docs/windows-completion-notification.md` | 1단계 | 시드 롤아웃 순서 3번 — 펫과 같은 완료 신호 기반, 토스트로 구현 | 완료 — `8a2d154`(Windows x64·arm64 성공). Core `3d5437e`, 알림·설정 `95a803a`. CI의 실제 호출은 두 러너 모두 `skipped (IsSupported false)` — 전송·표시는 기기 미확인 |
| 플러그인 마켓플레이스 (Claude·Codex) | `ClaudePluginService.swift`, `ClaudePluginView.swift`, `CodexPluginService.swift`, `AppStore+Components.swift` | 없음 — README 플랫폼 표에 '미지원' | 2단계 | 플러그인 목록(1단계) 위에 얹는 설치·제거 UI; 컴포넌트 대시보드와 같은 단계 | 확인 필요 |
| 원격 보안 저장소 | `Remote/RemoteKeychain.swift` (macOS Keychain) | `SecretProtector.cs` (DPAPI) — 구현됨 | 이미 있음 | `SecretProtector.cs` 존재 | |
| Windows 빌드·배포 파이프라인 | `scripts/build-macos.sh`, `scripts/install-macos.sh` | `scripts/build-windows.ps1` (기본 빌드만); 서명·MSIX·winget 없음 | 배포 인프라 | v3 이후 코드 서명·패키징·배포 채널 정비 필요 | 확인 필요 |
