# 적합성 코퍼스

마이티 스타일 매니페스트(schema v1)의 판정을 고정한 시험 벡터 모음입니다.
`StylesThirdPartyConformanceTests` 가 이 폴더를 읽어 고정된 엔진으로 인증합니다.

- 기준 엔진 태그: `mighty-style-engine-v1`
- 기준 엔진 커밋: `b767328c5e47654fffbde224488e5865a139ed8f`

## 폴더

| 경로 | 뜻 |
| --- | --- |
| `valid/` | 엔진이 오류 없이 받아들여야 하는 매니페스트 |
| `invalid/` | 엔진이 거절해야 하는 매니페스트. 파일 이름 줄기가 곧 기대 오류 코드입니다 |

`invalid/E_RESERVED_ID.json` 처럼 파일 이름이 기대 오류 코드가 되므로,
벡터를 더할 때 파일 이름과 실제 판정이 어긋나면 테스트가 바로 잡아냅니다.

기대 오류 코드는 사람이 추측해 적은 값이 아니라, 위 태그로 고정된 Swift 엔진에
벡터를 넣어 나온 실제 판정을 그대로 기록한 값입니다.

## 코퍼스가 다루지 않는 오류 코드

오류 코드는 모두 47개이고, 그중 45개가 `invalid/` 에 파일로 들어 있습니다.
남은 둘은 정적 파일로 다룰 수 없어 빠졌으며 사유는 다음과 같습니다.

| 코드 | 빠진 사유 | 대신 확인하는 곳 |
| --- | --- | --- |
| `E_TOO_LARGE` | 256 KB 를 넘는 파일이라야 나오는 판정입니다. 그만한 파일을 저장소에 두면 clone 과 diff 가 무거워집니다 | `StylesThirdPartyConformanceTests.tooLargeIsRejectedDynamically` 가 테스트 안에서 데이터를 만들어 확인합니다 |
| `E_ID_COLLISION` | 매니페스트 한 장을 읽어서는 나올 수 없는 판정입니다. 이미 등록된 다른 스타일과 id 가 겹칠 때 앱 등록 단계에서 레지스트리가 내리는 판정이라 디코더로는 닿지 않습니다 | `StyleManifestTests` 가 레지스트리 경로에서 따로 확인합니다 |

`invalidCorpusCoversExpectedErrorCodes` 가 위 둘을 제외한 45개 코드 전부가
코퍼스에 있는지, 그리고 코퍼스에 알 수 없는 코드가 섞이지 않았는지를 검사합니다.
엔진에 코드가 늘면 이 검사가 먼저 실패하므로 코퍼스가 조용히 뒤처지지 않습니다.

## 실행 방법

```
cd native/macos
DEVELOPER_DIR=/Library/Developer/CommandLineTools \
  swift test --filter StylesThirdPartyConformanceTests \
  -Xswiftc -plugin-path \
  -Xswiftc /Library/Developer/CommandLineTools/usr/lib/swift/host/plugins/testing
```

`-plugin-path` 두 줄이 필요한 것은 이 Mac 의 명령줄 도구 모음에서만 생기는 일입니다.
명령줄 도구 모음은 `Testing` 모듈은 찾지만 그 매크로 플러그인이 있는
`host/plugins/testing/` 은 기본 검색 경로에 넣지 않아, 빼면 `TestingMacros` 를 찾지 못해
빌드가 멈춥니다. 전체 Xcode 를 쓰는 환경에서는 두 줄 없이 그냥 돕니다.
