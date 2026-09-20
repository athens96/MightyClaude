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

## 기기 미확인 항목

- 실제 토스트 알림이 화면에 표시되는지 (기기에서 직접 확인 필요)
- 알림 클릭 시 앱이 앞으로 오고 해당 실행 창이 선택되는지
