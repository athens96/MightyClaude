# Windows 동반 펫

Mac 기준은 `AgentCompanion.swift`, `AgentCompanionViews.swift`, `CompanionBubbleController.swift`, `MightyCore/ResizeEdges.swift`다. Windows 구현은 `Companion.cs`, `CompanionBubbleLayout.cs`, `CompanionOverlay.cs`, `MainWindow.Companion*.cs`에 있다.

## 크기와 입력

- 기본 너비 258, 너비 범위 220–640, 저장한 높이 범위 100–480을 논리 좌표로 사용한다. 높이가 없으면 실제 Windows 글꼴을 `DrawTextW(DT_CALCRECT)`로 측정한다. 기본 작업 카드의 요청과 현재 작업은 각각 최대 두 줄이다.
- 위쪽·양옆의 6포인트 띠, 위쪽 모서리, 오른쪽 아래 손잡이로 크기를 조절한다. 위쪽을 끌 때는 펫이 있는 아래쪽을 유지하고, 왼쪽을 끌 때는 오른쪽을 유지한다. 화면 배율에 따른 픽셀 반올림도 반대편 좌표를 유지하도록 계산한다.
- 옆으로만 끌면 자동 높이가 유지된다. 수직 드래그는 높이를 저장한다. 크기 조절 영역을 두 번 클릭하면 저장한 너비와 높이를 지워 기본값과 자동 높이로 돌아간다. 캡처가 끊기거나 카드가 사라지면 진행 중인 드래그를 취소하고 원래 크기를 복원한다.
- 승인·질문 미리보기는 내용에 맞춘 높이를 사용하며 양옆으로만 조절한다. 자세한 권한 내용과 입력 가능한 질문지는 사용자가 명시적으로 버튼을 누른 뒤 별도 WinUI 창에서 열린다.
- 펫 창은 `WS_EX_NOACTIVATE`와 `MA_NOACTIVATE`를 사용한다. 표시·끌기·크기 조절·자체 문맥 메뉴는 현재 입력 앱의 포커스를 가져오지 않는다. 숨긴 펫은 애니메이션을 그리지 않으며, 상태 팝업 갱신은 계속된다.

## 키보드와 접근성

사이드바의 펫 표시 버튼과 에이전트 상태 버튼은 표준 WinUI 버튼이다. 상태 팝업의 각 에이전트도 키보드 초점과 UI Automation Invoke를 제공하며, 접근성 이름에 제목·상태·워크스페이스·프로바이더·시간·요청·현재 작업을 포함한다. 항목을 열면 원래 실행 창으로 이동한다. 실행 창과 별도 검토 창의 승인, 거부, 선택지, 직접 입력은 표준 WinUI 컨트롤로 조작한다.

투명한 Win32 펫 오버레이의 GDI 텍스트에는 개별 UI Automation 텍스트 피어가 없다. 작업 내용을 키보드나 스크린 리더로 읽는 경로는 위 상태 팝업과 실행 창이다. 따라서 Mac SwiftUI 접근성 트리와 동일하다고 주장하지 않는다.

## 검증 범위

`CompanionLayoutVerification.EdgesAndAnchors`는 기본값, 범위, 가장자리 판정, 승인 카드의 제한, 여러 배율에서 반대편 좌표 유지 규칙을 검사한다. Windows의 `companion` 스모크는 실제 atlas 디코딩과 프레임 자르기, 비활성 창, 표시된 카드에 묶인 클릭, 문맥 메뉴, 실제 글자 측정과 가장자리 크기 조절·취소·초기화, 상태 팝업의 키보드 초점과 UI Automation 호출을 검사한다.

설정 스크린샷은 별도 프로필의 내장 펫 목록과 디코딩된 미리보기가 준비된 뒤 캡처한다. 테스트는 사용자의 Codex 펫 폴더를 읽거나 실제 펫 타이머를 시작하지 않는다. 실제 키보드의 한국어 IME, 물리적 포인터·터치패드, 서로 다른 배율의 여러 모니터에서의 드래그는 자동 스모크 결과와 구분하여 실제 기기에서 확인해야 한다.

Win32 동작 근거: [DrawTextW의 DT_CALCRECT](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-drawtextw), [WM_SETCURSOR](https://learn.microsoft.com/en-us/windows/win32/menurc/wm-setcursor), [WM_LBUTTONDBLCLK](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-lbuttondblclk), [WM_MOUSEHWHEEL](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-mousehwheel).
