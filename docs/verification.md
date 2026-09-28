# 실행 창·여러 프로바이더 검증

Mac과 Windows 네이티브 앱을 검증하는 명령이다. 실제 결과와 아직 검증하지 못한 범위는 [네이티브 전환 검증](native-verification.md)에 기록한다.

| 검사 | 명령 | 범위 |
| --- | --- | --- |
| Swift 코어 | `bash scripts/test-native-macos.sh` | 기존 상태 복사·손상 파일 보호, 모델·실행 설정 검증, 세 프로바이더의 스트림 파싱, 실행 취소와 자식 프로세스 정리, Mods 인증 |
| Mac 앱 실행 | `release/native-macos/MightyClaude.app/Contents/MacOS/MightyClaude --smoke-test --smoke-exit --profile <빈 폴더>` | 임시 프로필에서 앱 시작·셸 실행을 확인한다. 실제 AI 요청은 보내지 않는다. |
| C# 코어 | `dotnet run --project native/windows/MightyClaude.Core.Tests --configuration Release` | 같은 계약의 Windows 구현. Windows 전용 Job Object·콘솔 검사는 다른 운영체제에서 생략한다. |
| Windows 앱 실행 | `pwsh ./scripts/build-windows.ps1 -Architecture x64 -Configuration Release` 후 `pwsh ./scripts/test-native-windows.ps1 -Executable <MightyClaude.exe>` | Windows에서만 실행한다. CI가 x64·ARM64 모두 새 프로필로 GUI 스모크를 실행한다. |
| Mod 정적 검사 | 격리된 `CLAUDE_CONFIG_DIR`에서 `claude plugin validate ./mods/mighty-bridge` | Mighty bridge hook 인식. [Mods 분석](claude-mods-analysis.md) |

테스트는 사용자 워크스페이스 상태와 분리된 임시 프로필을 사용한다. CLI 로그인과 제공자 설정은 기존 환경을 사용하며 모델 요청은 보내지 않는다.
