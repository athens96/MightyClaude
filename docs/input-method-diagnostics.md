# 한글 입력기 진단

가끔 입력창에서 한글이 음절로 조합되지 않고 자모가 하나씩 입력되는 문제(자소 분리)가 있다. 지금까지 재현된 순간은 모두 실행 중인 앱의 번들 정체성이 바뀐 직후였다(실행 중 번들 교체, 같은 번들 ID의 두 번째 복사본 실행, 새 빌드 설치 직후). 설치 스크립트와 자체 업데이트 도우미는 그 경로를 막았지만(`docs/app-update.md`의 한계 절), 원인이 완전히 잡힌 것은 아니어서 앱 안에 진단을 넣었다.

## 무엇을 기록하나

입력창(`ComposerTextView`)은 키 입력마다 입력 시스템이 부르는 `setMarkedText`·`insertText`·`unmarkText`·`firstRect(forCharacterRange:)`를 시각과 함께 기록한다(최근 400건). 한국어 입력 소스가 선택된 상태에서 **조합 없이 자모 하나가 그대로 입력**되는 일이 5초 안에 두 번 생기면 문제로 판단한다.

판단되면 `<상태 저장 위치>/diagnostics/ime-<시각>.json`에 다음을 저장한다.

- 앱 활성 여부, 활성화 정책, 키 윈도·메인 윈도, 첫 응답자
- 보안 입력(`IsSecureEventInputEnabled`) 여부, 현재 입력 소스·키보드 레이아웃 ID
- `NSTextInputContext.current`가 입력창의 컨텍스트인지, 입력창의 marked/selected range, 입력창이 윈도 안에 있는지
- 최근 입력 이벤트 120건

워크스페이스 메뉴의 "입력기 진단 저장"으로 언제든 수동 저장할 수 있다(Finder에서 파일을 보여준다).

## 다시 연결

문제가 감지되면 입력창 위에 안내와 **입력기 다시 연결** 버튼이 나온다(워크스페이스 메뉴에도 있다). 다시 연결은 marked text를 버리고 입력창의 입력 컨텍스트를 비활성·재활성한 뒤 첫 응답자를 다시 잡고, 입력 소스를 ABC로 잠깐 바꿨다가 원래 소스로 되돌린다. 그래도 안 되면 앱을 종료(⌘Q)하고 다시 열어야 한다.

## 진단 파일로 가리는 것

- `recentEvents`에 `setMarkedText`가 전혀 없고 `insertText`만 자모로 이어지면 입력기가 조합을 시작조차 하지 않은 것이다. 시스템 쪽(입력기 세션·LaunchServices·보안 입력) 문제다.
- `setMarkedText` 뒤에 곧바로 `unmarkText`/`insertText`가 따라오면 앱 안에서 조합이 끊긴 것이다. 그 사이에 어떤 갱신이 끼었는지 이벤트 순서로 볼 수 있다.
- `secureEventInput`이 true면 어떤 프로세스가 보안 키보드 입력을 켜 둔 것이다(터미널의 Secure Keyboard Entry, 비밀번호 필드 등).
- `editorContextIsCurrent`가 false면 입력 시스템이 다른 컨텍스트를 보고 있다.
