# Windows 이름 변경 규칙

macOS `AppStore+Rename.swift` · `RenameViews.swift`의 Windows 대응.

## OS-bound 결정

| 항목 | macOS | Windows | 이유 |
|---|---|---|---
| 대화상자 종류 | AppKit sheet (`.sheet(item:)`) | WinUI ContentDialog | WinUI에 sheet가 없음 |
| 저장 단축키 | Enter (`.defaultAction`) | Enter (ContentDialog 기본) | 같은 동작 |
| 취소 단축키 | Esc (`.cancelAction`) | Esc (ContentDialog 기본) | 같은 동작 |
| 이름 필드 레이블 | `TextField("이름", …)`의 자리 표시자 | `TextBox.Header = "이름"` | WinUI TextBox에는 자리 표시자 레이블이 없어 Header가 같은 자리를 차지 |
| 오류 문구 색 | `.foregroundStyle(.red)` | `SolidColorBrush(Colors.Red)` | 같은 색 |
| 저장 비활성화 | `Button("저장").disabled(!validName)` | `ContentDialog.IsPrimaryButtonEnabled` | 같은 동작 |

## 검증 규칙 (Core)

`RenameSupport.DisplayName`:
1. 앞뒤 공백·줄바꿈 제거
2. 빈 문자열 거부
3. 텍스트 요소(자소 묶음) 기준 120자 초과 거부 — UTF-16 단위 아님, Korean·emoji 포함
4. 제어 문자(Unicode Cc 범주) 거부 — 중간 줄바꿈 포함

## 문구 대응

| Windows 상수 | macOS 원문 |
|---|---|
| `RenameStrings.MenuEntry` | `"이름 변경…"` (WorkspaceView.swift:155·187, SessionPaneView.swift:198, PaneDockDrag.swift:198) |
| `RenameStrings.HeadingWorkspace` | `"워크스페이스 이름 변경"` (RenameViews.swift) |
| `RenameStrings.HeadingSession` | `"실행 창 이름 변경"` (RenameViews.swift) |
| `RenameStrings.ErrorTooLong` | `"이름은 120자 이내로 입력하세요."` (RenameViews.swift) |
| `RenameStrings.ErrorControlCharacter` | `"이름은 줄바꿈 없이 입력하세요."` (RenameViews.swift) |
| `RenameStrings.ErrorNotFound` | `"대상을 찾을 수 없습니다. 창을 닫고 다시 시도하세요."` (RenameViews.swift) |

## 저장된 제목 클램프

`StateStore.Normalize`는 예전에 `Wire.Clean(title, 120)`으로 UTF-16 단위 120개에서 잘랐다.
이모지 120자(= UTF-16 240단위) 이름은 검증을 통과하지만 재시작 후 60자로 잘리고
서로게이트 쌍이 갈라질 수 있어, `RenameSupport.ClampTitle`이 텍스트 요소 기준 120개로 자른다.
검증 규칙과 같은 단위이므로 유효한 이름은 언제나 온전히 살아남는다.

## Core.Tests 검사 (이름이 `rename`으로 시작)

16개 — 검증 규칙 4개(허용·빈 문자열·120자·제어 문자), 스냅샷 저장 2개, 새 출력 생존 1개,
재시작 생존 1개, 거절 2개, `DesktopService` 연결 1개, macOS 문구 일치 1개,
대화상자 문구·저장 버튼 2개, 이모지 제목 클램프 2개.

## WinUI 연결

`MainWindow.Navigation.cs` — `WorkspaceMenu`·`SessionMenu`가 `RenameStrings.MenuEntry`를 띄우고
`AskName`이 `RenameSupport.Messages`·`IsValid`·`DisplayName`만 사용한다.
저장은 `DesktopService.RenameWorkspaceAsync`·`RenameSessionAsync`를 거치므로
사이드바·탭·창이 같은 스냅샷에서 다시 그려진다. WinUI에는 새 문구 리터럴이 없다.
