# Windows 이름 변경 규칙

macOS `AppStore+Rename.swift` · `RenameViews.swift`의 Windows 대응.

## OS-bound 결정

| 항목 | macOS | Windows | 이유 |
|---|---|---|---
| 대화상자 종류 | AppKit sheet (`.sheet(item:)`) | WinUI ContentDialog | WinUI에 sheet가 없음 |
| 저장 단축키 | Enter (`.defaultAction`) | Enter (ContentDialog 기본) | 같은 동작 |
| 취소 단축키 | Esc (`.cancelAction`) | Esc (ContentDialog 기본) | 같은 동작 |

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
