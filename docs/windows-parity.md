# Windows 기능 대응 목록

macOS 코드베이스에서 발견한 모든 기능 영역과 Windows 구현 현황.
단계 셀은 `1단계 | 터미널 | 2단계 | 3단계 | 이미 있음 | Windows에 해당 없음 | 배포 인프라` 중 하나.
'확인 필요' 표시는 단계가 판단으로 결정된 행. '보류'는 macOS와의 차이를 사용자가 정해야 하는 행 ('확인 필요'와 다른 표시).

| 기능 영역 | macOS 근거 | Windows 현황 | 단계 | 근거 | 확인 |
|---|---|---|---|---|---|
| 슬래시 명령 완성 | `SlashCommands.swift`, `SlashCommandPalette.swift`, `AppStore+SlashCommands.swift` | `SlashCommands.cs`, `SlashCommandStrings.cs` — Core 구현 완료; `SlashCommandVerification.cs` 8개 테스트 통과 (Mac); WinUI 팔레트 렌더링 대기 | 1단계 | P1 합의 (interview_20260919_235018) | 보류 — Core·문구·검사는 `14d8613`에 들어갔고 Windows CI 성공(`8741a59`); 팔레트 화면(WinUI)이 아직 없다. 기기 미확인 |
| 상태 표시줄 | `StatusLine.swift`, `StatusLineView.swift`, `AppStore+StatusLine.swift` | `StatusLine.cs`, `StatusLineTrust.cs`, `StatusLineStrings.cs` — Core 구현 완료; `StatusLineVerification.cs` 8개 테스트 통과 (Mac); WinUI `MainWindow.StatusLine.cs` — 입력창 아래 렌더링·신뢰 질문·스모크 키 `statusLine` | 1단계 | P1 합의 (interview_20260919_235018) | 완료 — `7de79c0`(구현) · `8741a59`(Windows x64·arm64 성공); 셸은 Claude Code와 같은 순서(Git Bash → PowerShell, `docs/windows-status-line.md`). 기기 미확인 |
| 상태 줄 256색·RGB | `StatusLine.swift` `ANSISegment.Color.palette/.rgb` | 보류 — Windows `AnsiColor`는 macOS의 16색 이름만 지원. `38;5;n` / `38;2;r;g;b`는 기본색으로 표시됨 | 1단계 | 보류 (v4 묶음 전 사용자 판단) | 보류 |
| 상태 줄 레벨 분리 폴백 | `AppStore+StatusLine.swift` `gated ? discovery.user : discovery.preferred` | 보류 — Windows `Discover()`는 우선순위 1개만 반환하므로, 워크스페이스 명령이 거절되면 사용자 설정 명령으로 되돌아가지 않고 상태 줄이 비어 있음 | 1단계 | 보류 (v4 묶음 전 사용자 판단) | 보류 |
| CLI 계정 전환 | `CLIAccounts.swift`, `CLIAccountsSettingsView.swift`, `AppStore+CLIAccounts.swift` | 없음 | 1단계 | P1 합의 (interview_20260919_235018) | |
| 계정 사용량 표시 | `AccountUsageService.swift`, `AccountUsageSnapshot.swift`; docs/session-usage.md | `ActivityUsageVerification.cs` (테스트만); 서비스 미구현 | 1단계 | P1 합의 — "usage display, off by default" | |
| CLI 업데이트 | `CLIUpdateService.swift`, `CLIUpdateSettingsView.swift`, `AppStore+CLIUpdates.swift` | 없음 | 1단계 | P1 합의 (interview_20260919_235018) | |
| 앱 자체 업데이트 | `AppUpdate.swift`, `AppUpdateSettingsView.swift`, `AppStore+AppUpdate.swift` | `latest.json`에 windows.x64/arm64 블록 예약됨; 클라이언트 미구현 | 1단계 | P1 합의 (interview_20260919_235018) | |
| 세션·창 이름 변경 | `AppStore+Rename.swift`, `RenameViews.swift`, `RenameDiagnostics.swift` | 없음 (사이드바 목록은 있음) | 1단계 | macOS 동일 화면 원칙; P1 기본 UX | 확인 필요 |
| Claude 플러그인 목록 | `ClaudePluginService.swift`, `ClaudePluginModels.swift`, `AppStore+Plugins.swift` | 없음 | 1단계 | 슬래시 명령 완성의 전제 조건 | 확인 필요 |
| Codex 플러그인 | `CodexPluginService.swift` | 없음 | 1단계 | Codex 사용 시 플러그인 인식 필요 | 확인 필요 |
| 설정 화면 | `SettingsViews.swift` (계정·업데이트·컴포넌트·스타일 섹션) | `SettingsVerification.cs` (테스트만); 다이얼로그 방식만 있음 | 1단계 | P1 기능(계정·업데이트)이 설정 화면 필요 | 확인 필요 |
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
| Claude 추가 권한 요청의 앱 내 승인 (실행 창·펫 말풍선) | `ToolPermissions.swift`(`can_use_tool` 처리), `ToolPermissionBar.swift`, `AgentCompanion.swift` | 없음 — README 플랫폼 표에 '미지원' | 1단계 | 승인 채널이 없으면 자동 허용 밖의 도구 요청에서 실행이 멈춘다; 핵심 작업 흐름 | 확인 필요 |
| 완료 알림 (시스템 알림) | `AgentCompanion.swift`(`UNUserNotificationCenter`) | 없음 — README 플랫폼 표에 '미지원' | 3단계 | 펫과 같은 파일·같은 활동 신호를 쓴다; Windows는 토스트 알림으로 구현 | 확인 필요 |
| 플러그인 마켓플레이스 (Claude·Codex) | `ClaudePluginService.swift`, `ClaudePluginView.swift`, `CodexPluginService.swift`, `AppStore+Components.swift` | 없음 — README 플랫폼 표에 '미지원' | 2단계 | 플러그인 목록(1단계) 위에 얹는 설치·제거 UI; 컴포넌트 대시보드와 같은 단계 | 확인 필요 |
| 원격 보안 저장소 | `Remote/RemoteKeychain.swift` (macOS Keychain) | `SecretProtector.cs` (DPAPI) — 구현됨 | 이미 있음 | `SecretProtector.cs` 존재 | |
| Windows 빌드·배포 파이프라인 | `scripts/build-macos.sh`, `scripts/install-macos.sh` | `scripts/build-windows.ps1` (기본 빌드만); 서명·MSIX·winget 없음 | 배포 인프라 | v3 이후 코드 서명·패키징·배포 채널 정비 필요 | 확인 필요 |
