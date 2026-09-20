# Windows 완료 알림

## macOS와의 OS 이름 대체

`CompletionNotificationStrings.ToggleLabel`은 `"작업 완료 시 Windows 알림"`으로,
macOS의 `"작업 완료 시 Mac 알림"`에서 `Mac`을 `Windows`로 교체한다.
이것이 이 구현에서 유일하게 허용된 OS 이름 대체이다.
(`StringsVerification.CompletionNotificationStringsMatchMacOS`가 이 값을 검사에 포함한다.)

## OS-bound 메커니즘

macOS는 `UNUserNotificationCenter`를 사용한다. Windows는
Windows App SDK의 `AppNotificationManager`(토스트 알림)를 사용한다.

이유: WinUI 3 앱에서 시스템 알림을 보내는 표준 방법이며,
`IsSupported()` 확인 → 등록 → 전송의 순서로 안전하게 처리할 수 있다.
패키지되지 않은(unpackaged) 앱에서 `IsSupported()`가 `false`를 반환하면
상태 텍스트가 `"검증 모드"`로 표시되고 실행은 정상 진행된다.

## 알림 내용

- 제목: `"MightyClaude · 작업 완료"` (고정)
- 본문: `"{runTitle}의 작업이 완료되었습니다."` (`{runTitle}`은 세션 제목)
- 세션 ID는 `launch` 파라미터로 전달되며, 알림 클릭 시 해당 실행 창을 선택한다
- 프롬프트, 도구 인자, 출력, 프로젝트 경로는 포함되지 않는다

## CI 스모크의 실제 호출 1회

GUI 스모크 실행은 실제 알림기를 픽스처 제목(`Smoke fixture`)으로 한 번 호출하고,
결과 JSON의 `completionNotification` 키에 결과를 기록한다.

- 보냄: `{"status":"sent"}`
- 건너뜀: `{"status":"skipped","reason":"..."}` — `IsSupported`가 `false`이거나
  러너에서 등록이 불가능한 경우. 건너뜀은 실패가 아니다.
- `IsSupported`가 `true`인 뒤에 발생한 예외는 스모크 실패로 전파된다.

기록 형태와 호출 횟수는 Core의 `CompletionNotificationSmoke`·
`CompletionNotificationSmokeOutcome`에 있어 Mac에서 검사한다
(`completion notification smoke …` 4개 검사). 스모크는 CLI를 실행하지 않고
저장 상태를 바꾸지 않으며, 저장 상태 버전은 계속 1이다.
토스트가 실제로 보였는지는 주장하지 않는다 — 아래 기기 미확인 항목으로 남는다.

## 기기 미확인 항목

- 실제 토스트 알림이 화면에 표시되는지 (기기에서 직접 확인 필요)
- 알림 클릭 시 앱이 앞으로 오고 해당 실행 창이 선택되는지

## CI에서 실제로 일어난 일 (2026-09-20, `8a2d154`)

GUI 스모크는 실제 알림 호출을 한 번 시도하고 결과를 `completionNotification`에 남긴다. GitHub의 두 러너(`windows-2025` x64, `windows-11-arm`)에서는 둘 다 `skipped (IsSupported false)`였다. 즉 CI는 "지원 여부를 먼저 묻고, 지원하지 않으면 실행을 막지 않고 건너뛴다"는 경로만 확인했고, 알림을 실제로 보내는 경로는 확인하지 못했다. 러너가 관리자 권한으로 도는 점이 원인일 가능성이 있으나 확인하지 않았다. 실제 전송과 화면 표시는 Windows 기기에서 확인할 항목이다(`docs/windows-screen-checklist.md`, 기기 미확인). 이 결과는 스모크가 통과해도 공개 알림(notice)으로 남으므로 로그 없이 읽을 수 있다.
