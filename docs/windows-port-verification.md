# Windows 이식 검증 기록

2026-10-04. macOS 소스 기준점은 `088fdccc9920ef27f80af724264e8ba9b122f78e`이며, [작업 브랜치](https://github.com/athens96/MightyClaude/tree/windows/macos-feature-parity)는 `windows/macos-feature-parity`다. 최종 검증·배포 소스는 `4301339b11945986c7e3774eb3ccccc75433226a`다. 영역별 원본 파일과 Windows 대응 구현은 [기능 대응 목록](windows-parity.md)에 기록했다. 브랜치와 CI 배포 파일을 업로드했으며 main 병합·정식 릴리스는 하지 않았다.

## 구현 범위

Claude·Codex·Gemini 실행과 권한 요청, 계정·모델 재조회 및 Bedrock 설정 초기화, 플러그인 마켓플레이스와 업데이트, 단계별 모델과 추론 강도 설정을 연결했다. 터미널, 그래프·타임라인과 세션 기록, 결과 파일·이미지·브라우저 뷰어, 입력 대기열·질문·다음 작업 버튼, 스타일·컴포넌트, 모바일 원격 연결·화면 공유·입력 제어, 펫과 알림, 대시보드·설정·사이드바를 대조하고 누락된 실행 경로를 보완했다.

Windows 구현은 WinUI 네이티브 입력 컨트롤, ConPTY, WebView2, Windows Graphics Capture를 사용한다. macOS의 Keychain·Homebrew·화면 캡처·렌더러는 각 Windows 대응 경로로 바꿨다. 화면 공유의 Windows 코덱은 현재 H.264/SDR이며, 이미지 코덱과 SVG 표현에는 운영체제 렌더러 차이가 있다. 세부 범위는 [모바일 원격](windows-mobile-remote.md), [펫](windows-companion.md), [화면 점검표](windows-screen-checklist.md)를 따른다.

## 자동 검사와 화면 확인

| 검사 | 결과 |
|---|---|
| 로컬 공통 C# 검사 | 469개 통과, Windows 전용 4개 제외 |
| 공유 번역 키·변경 파일 검사 | 통과 |
| macOS에서 WinUI C# 교차 컴파일 | 추가 C# 오류·경고 없음. Windows 전용 XAML 생성은 이 검사로 증명하지 않음 |
| Windows x64·ARM64 Core·빌드·네이티브 GUI 검사 | 두 아키텍처 모두 통과 — `4301339` [CI 실행](https://github.com/athens96/MightyClaude/actions/runs/37182771471) |
| 배포 파일 독립 검증 | 두 ZIP의 SHA-256·CRC, 실행 파일 아키텍처, 소스 커밋·런타임 구성 일치 |
| 공통 스타일 엔진 고정 검사 | `74cd28c` 통과 |

최종 Windows 두 환경에서 입력·복사 경로, 스타일 승인, 다음 작업 버튼, 자동·수동 업데이트 중 초안과 대기열 보존, 펫, 합성 장면의 H.264 화면·제어 왕복, ConPTY, 워크스페이스별 펼침·선택·창 추가, 사용자 모델 등록·삭제·저장, 여덟 설정 카테고리와 두 테마 검사가 통과했다. 슬래시 명령, 세션 이력, 그래프·타임라인, 파일·브라우저, 창 재배치와 초안 보존도 포함한다. 각 아키텍처의 검사 JSON을 독립 검증해 필수 37개 그룹의 불리언 조건 123개가 모두 참임을 확인했다. 각각 화면 문자열 1,627개에서 번역 키 노출은 없었다.

그래프의 네이티브 문서·부모 컨트롤 유지, 닫힌 창의 지연 로드 콜백 차단과 좁은 상단 도구 모음의 겹침 방지를 실제 WinUI에서 검증했다. 최종 CI는 아키텍처별 PNG 21장을 보관한다. 독립 디자인 검토에서는 두 아키텍처의 최종 그래프·320px 도구 모음·타임라인·파일·다크/라이트 전체 화면 12장을 확인했고, 앞선 동일 디자인 빌드의 모델 설정·다음 작업·하단 버튼도 대조했다. 세션 이력 선택창은 동작 검사를 통과했지만 별도 PNG 검토는 하지 않았다.

실행 검사는 새 프로필, 가짜 CLI·모델 카탈로그·임시 워크스페이스와 로컬 암호화/미디어 연결을 사용하며 `aiRequestSent: false`다. 실제 사용자 계정으로 모델을 호출하거나 사용자 플러그인을 업데이트한 결과는 아니다. 완료 알림의 실제 전송은 두 CI 환경 모두 `IsSupported: false`로 건너뛰었다. GUI 증거 아티팩트에는 검사 JSON, 로그와 `smoke*.png`를 보관하며 변동하는 WebView 프로필은 제외한다.

## 배포 파일

[최종 CI의 Artifacts](https://github.com/athens96/MightyClaude/actions/runs/37182771471)에서 다음 이름의 배포 아티팩트를 받을 수 있다. GitHub 로그인이 필요할 수 있다. `MightyClaude-windows-*-smoke`는 검증 자료이며 실행 배포본은 아래 두 개다.

| 아티팩트 | 내부 배포 ZIP | ZIP 크기 |
|---|---|---|
| `MightyClaude-native-windows-x64` | `MightyClaude-windows-x64.zip` | 117,968,419바이트 |
| `MightyClaude-native-windows-arm64` | `MightyClaude-windows-arm64.zip` | 114,677,322바이트 |

아래 SHA-256은 GitHub가 감싸서 내려주는 아티팩트 ZIP이 아니라 내부 배포 ZIP의 값이며, 함께 제공한 `.sha256` 파일과 일치한다.

```text
2a7cd9858ebda1dc432df1922bbacc3a2d962adfb50613b751c18408f640cad0  MightyClaude-windows-x64.zip
8cdce41db420ba972400376511f1698c124b6011e0f9d8d0f25df0660d117a43  MightyClaude-windows-arm64.zip
```

Windows 아키텍처에 맞는 ZIP 전체를 풀고 `native-windows-x64/MightyClaude.exe` 또는 `native-windows-arm64/MightyClaude.exe`를 실행한다. EXE 하나만 옮기면 안 된다. .NET 10.0.12와 Windows App SDK 구성 요소는 포함되어 있다. WebView2Loader는 포함되지만 WebView2 Evergreen 브라우저 런타임 자체를 담은 배포본은 아니므로 터미널·브라우저 등 해당 기능에는 Windows에 설치된 WebView2 런타임이 필요하다.

현재 파일은 서명 없는 자체 포함 폴더 배포본(`signed: false`, `unpackaged-self-contained-folder`)이다. 앱 업데이트 공개 키와 매니페스트 주소도 이 CI 빌드에 설정하지 않아 `updateChecks: false`, `updateManifestUrl: null`이다. 앱 자체 업데이트 구현은 포함되어 있지만 이 배포본에서는 확인이 비활성화된다. 이는 CLI·플러그인 자동 업데이트와 별개의 설정이다. Authenticode·MSIX/스토어·정식 앱 업데이트 배포 설정은 남아 있다.

## macOS 회귀 검사

공유 번역 파일 외 macOS 실행 소스는 변경하지 않았다. `74cd28c` [macOS CI](https://github.com/athens96/MightyClaude/actions/runs/37181760242)에서는 앱 빌드와 GUI 검사가 통과했지만 Swift 테스트 두 건에서 네 개의 검증 실패가 있었다.

- `realPTYCooperativeProcessStopsOnSIGINTQuickly`: 종료 상태·신호와 제한 시간 검사 실패.
- `shutdownEndsFourFloodingRunsWithinTheQuitDeadline`: 종료에 8.29초가 걸려 6초 제한 초과. [직전 실행](https://github.com/athens96/MightyClaude/actions/runs/37180823350)에서도 같은 검사에서 9.27초가 기록됐다.

이 기록은 macOS 전체 검사가 통과했다는 뜻이 아니다. 위 실행 코드와 테스트는 이번 Windows 이식에서 수정하지 않았다.

## 실제 기기에서 남은 확인

- Windows 한국어 IME 조합, Alt-Tab 복귀 후 포커스, 물리 키보드의 Enter·복사·붙여넣기.
- 실제 Claude·Codex·Gemini 계정과 Bedrock/SSO 모델 호출, Git 자격 증명, 설치된 CLI·플러그인 업데이트.
- 실제 휴대폰과 릴레이, 다중 모니터·DPI·캡처 권한·UAC 보안 화면에서의 동작.
- 실제 Windows 완료 알림 전송과 활성화.
- 서명 인증서를 사용하는 설치 패키지·스토어와 정식 앱 업데이트 배포 설정.

자동 검사 성공이 위 항목의 실기 확인을 대신하지 않는다.
