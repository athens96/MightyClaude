# 브라우저 창 (1단계)

고정한 CEF(Chromium Embedded Framework) 엔진과 고정한 Node 런타임을 내려받아 검증하고 macOS 앱 번들에 담는 단계, 그리고 사용자가 손으로 여는 브라우저 탭. 에이전트 제어(2·3단계)와 CDP 프록시·Playwright MCP는 이 시드 밖이다.

이 단계의 모든 작업은 Command Line Tools만으로 끝난다(Xcode 불필요).

## 1. 고정 버전이 사는 곳

`native/macos/BrowserEngine.lock` 하나가 단일 진실의 원천이다. arm64 전용 컷이며 Intel은 뒤 단계다.

```json
{
  "arch": "arm64",
  "cef": {
    "version": "152.0.9+g07f67cd+chromium-152.0.7977.134",
    "url": "https://cef-builds.spotifycdn.com/…_macosarm64_minimal.tar.bz2",
    "sha256": "f4efc5e8447e37d3dd1a982047310f1af16d08c5e81136d33a92e6cbff2717f7"
  },
  "node": {
    "version": "22.23.2",
    "url": "https://nodejs.org/dist/v22.23.2/node-v22.23.2-darwin-arm64.tar.gz",
    "sha256": "61130f394c1630d211dd50aecc4353d379480f36d3ac913cd85dbba1aed585c6"
  }
}
```

- `version`·`url`·`sha256` 세 필드가 CEF와 Node 각각에 있다. `sha256`은 64자 16진수(SHA-256)여야 한다.
- Node는 메이저 18 이상이어야 한다(현재 LTS인 22를 쓴다).
- 실제 값은 파일을 직접 보라. 위 조각은 형태를 보이려는 것이고, 핀을 올리면 달라진다.

엔진 아카이브와 풀어 놓은 파일은 저장소에 절대 커밋하지 않는다(`.gitignore`).

## 2. 내려받기와 캐시

```sh
scripts/fetch-browser-engine.sh
```

- 캐시는 저장소 **밖**이다. 기본 경로는
  `~/Library/Caches/MightyClaude/browser-engine/<BrowserEngine.lock의 sha256 앞 16자리>/`
  이고 `MIGHTY_BROWSER_ENGINE_CACHE`로 통째로 바꿀 수 있다. 핀이 바뀌면 lock 파일의 해시가 바뀌므로 캐시 디렉터리도 자동으로 갈린다.
- 받은 아카이브의 sha256이 lock 값과 다르면 거부하고 0이 아닌 코드로 끝난다.
- 실행할 때마다 캐시에 있는 두 아카이브를 lock의 sha256과 **다시** 대조한다. 어긋나면 지우고 다시 받는다.
- 두 아카이브가 모두 일치할 때만 `cache hit`이 들어간 줄을 찍는다. 즉 캐시가 더운 두 번째 실행은 아무것도 내려받지 않는다.
- 내려받기 하나당 300초 상한이 있고, 넘으면 어느 단계에서 멈췄는지 이름을 찍고 0이 아닌 코드로 끝난다. 무한정 매달리지 않는다.

엔진은 **빌드 시점에 선택**이다. 엔진이 캐시에 없는 기계에서도 `swift build`와 `scripts/test-native-macos.sh`는 그대로 통과한다. 앱은 실행 시점에 자기 번들 안에서 CEF 프레임워크를 찾고, 없으면 크래시 대신 `browser.engine.missing` 문구를 브라우저 탭에 보여 준다.

## 3. 번들 레이아웃

```sh
MIGHTY_BROWSER_ENGINE=1 scripts/build-macos.sh
```

이 변수 없이 부르면 스크립트는 예전과 완전히 같게 동작한다. 스크래치 경로는 `MIGHTY_BUILD_SCRATCH`로 옮길 수 있고, 기본값은 예전 그대로 `native/macos/.build`다. 확인용 빌드는 워크트리 안에 쓰지 않도록 `MIGHTY_BUILD_SCRATCH=/tmp/mc-swift-browser`를 쓴다.

번들에 들어가는 것:

```
MightyClaude.app/Contents/
  Frameworks/
    Chromium Embedded Framework.framework/    ← CEF 프레임워크
    MightyClaude Helper.app/                  ← 메인 헬퍼
    MightyClaude Helper (GPU).app/
    MightyClaude Helper (Renderer).app/
    MightyClaude Helper (Plugin).app/
  Resources/
    browser/node/bin/node                     ← 고정한 Node 런타임
    ThirdPartyLicenses/
      CEF-LICENSE.txt                         ← CEF 고지
      Chromium-CREDITS.html                   ← Chromium 고지
```

- 헬퍼 앱 네 개는 CEF 프레임워크와 나란히 `Contents/Frameworks/` 안에 있다.
- 번들 ID는 `dev.mightyclaude.native`에서 파생하며 서로 다르다:
  `dev.mightyclaude.native.helper`, `.helper.gpu`, `.helper.renderer`, `.helper.plugin`.
- 고지 파일은 CEF 배포본의 `LICENSE.txt`·`CREDITS.html`을 그대로 옮긴 것이고, 저장소의 `native/licenses/`와 함께 `ThirdPartyLicenses/`에 모인다.
- CEF 안쪽 dylib과 헬퍼 앱은 바깥 번들과 따로 서명한다(`--deep`이 닿지 않는다). 그 뒤 앱 전체를 서명하면
  `codesign --verify --deep --strict <앱>`이 통과한다.

## 4. 프로필 경로와 잠금 파일

워크스페이스마다 영구 CEF 프로필 디렉터리 하나:

```
~/Library/Application Support/MightyClaude/browser-profiles/<workspaceProfileKey>/
```

경로는 `MightyCore`의 `BrowserProfileSupport.profilePath(workspaceProfileKey:)`가 계산한다. Core는 CEF를 import하지 않는다 — 경로만 알고, 엔진 구현은 앱 타깃에 있다.

규칙:

- 같은 워크스페이스의 브라우저 탭들은 이 프로필 하나를 공유한다. 다른 워크스페이스와는 섞이지 않는다.
- 로그인과 쿠키는 엔진을 다시 띄워도 남는다.
- 엔진이 비정상 종료하며 남긴 Chromium 잠금 파일(`SingletonLock` 등)은 다음 기동 때 지운다. **프로필 데이터는 지우지 않는다** — 잠금 파일만 치운다.
- 다른 브라우저(Chrome·Safari 등)의 쿠키나 자격 증명은 어떤 경우에도 읽지 않는다.

1단계 코드에 들어간 것은 경로 계산까지다. 잠금 파일 정리는 CEF 구현과 같은 자리(앱 타깃)에 들어가며, 지속·격리·잠금 복구 세 가지는 아래 화면 확인 표에서 사용자가 눈으로 확인한다(오프스크린 프로브는 2026-09-24 사용자 결정으로 이 단계에서 뺐다).

## 5. 핀 올리는 법

1. CEF는 <https://cef-builds.spotifycdn.com/index.html>의 `macosarm64` minimal 배포본, Node는 <https://nodejs.org/dist/>의 `darwin-arm64` tar.gz에서 새 버전을 고른다. Node 메이저는 18 이상을 지킨다.
2. 받으려는 아카이브의 sha256을 구한다: `shasum -a 256 <아카이브>` (또는 배포처의 체크섬 파일).
3. `native/macos/BrowserEngine.lock`의 해당 블록에서 `version`·`url`·`sha256` **세 개를 함께** 고친다. 하나만 고치면 받기 단계에서 해시 불일치로 거부된다.
4. `scripts/fetch-browser-engine.sh`를 돌린다. lock 해시가 바뀌었으므로 캐시 디렉터리가 새로 생기고 두 아카이브를 새로 받는다. 한 번 더 돌려 `cache hit`이 찍히는지 본다.
5. `MIGHTY_BROWSER_ENGINE=1 MIGHTY_BUILD_SCRATCH=/tmp/mc-swift-browser scripts/build-macos.sh`로 번들을 만들고 `codesign --verify --deep --strict`로 서명을 확인한다.
6. `Contents/Resources/browser/node/bin/node --version`이 lock의 Node 버전과 같은지 본다.
7. lock 파일과 이 문서의 버전 표기를 함께 커밋한다. 캐시 경로는 lock 해시에서 나오므로 따로 손댈 것이 없다.

## 6. 화면 확인 (기기 미확인)

엔진을 담은 빌드를 설치한 뒤 사용자가 직접 본다. 결과 칸은 확인 전까지 `기기 미확인`이다.

| 항목 | 확인 내용 | 결과 |
|------|-----------|------|
| 새 탭 메뉴 | 새 탭 메뉴에 "새 브라우저 탭"(`new-browser-tab`)이 에이전트 탭 옆에 보인다 | 기기 미확인 |
| 브라우저 탭 열기 | 탭이 열리고 주소 입력칸·뒤로·앞으로·새로 고침이 모두 보인다 | 기기 미확인 |
| 페이지 이동 | 주소를 넣으면 페이지가 그려지고 뒤로·앞으로·새로 고침이 동작한다 | 기기 미확인 |
| 엔진 없는 빌드 | `MIGHTY_BROWSER_ENGINE` 없이 만든 빌드에서 브라우저 탭이 크래시 대신 `browser.engine.missing` 문구를 보여 준다 | 기기 미확인 |
| 프로필 지속 | 어느 사이트에 로그인하고 앱을 껐다 켜도 로그인이 남아 있다 | 기기 미확인 |
| 프로필 격리 | 워크스페이스 A에서 한 로그인이 워크스페이스 B의 브라우저 탭에는 없다 | 기기 미확인 |
| 같은 워크스페이스 공유 | 같은 워크스페이스에서 브라우저 탭을 둘 열면 쿠키·로그인을 함께 쓴다 | 기기 미확인 |
| 잠금 파일 복구 | 엔진이 비정상 종료한 뒤 다시 열어도 프로필 데이터를 잃지 않고 열린다 | 기기 미확인 |

## 7. CEF 실행 모델 (1b단계)

### 애플리케이션 클래스

CEF는 macOS에서 `NSApplication` 서브클래스를 요구한다. `MightyApplication`이 그 역할이며 `isHandlingSendEvent`·`setHandlingSendEvent`를 구현한다. `Info.plist`의 `NSPrincipalClass`는 `MightyApplication`을 가리킨다.

### 헬퍼 프로세스

Chromium은 GPU·렌더러·플러그인·유틸리티 서브프로세스를 낳는다. 번들의 네 헬퍼 앱은 각자 `cef_execute_process`를 호출해 해당 서브프로세스 역할을 맡는다. CEF 프레임워크는 `dlopen`으로 불러오므로 Swift 패키지는 CEF 헤더 없이 빌드된다.

### 엔진 수명 주기

엔진은 첫 브라우저 탭이 열릴 때 지연 초기화된다. `browser_subprocess_path`는 메인 헬퍼를 가리키고, `root_cache_path`는 `~/Library/Application Support/MightyClaude/browser-profiles`다. 메시지 루프는 main run loop에서 구동한다. 앱이 종료하면 CEF도 정상 종료한다.

### 워크스페이스별 request context

브라우저 탭은 `BrowserProfileSupport.profilePath(workspaceProfileKey:)`가 반환하는 경로를 cache path로 삼는 CEF request context를 쓴다. 같은 워크스페이스의 탭들은 context를 공유하고, 다른 워크스페이스와 격리된다. 엔진 시작 직전에 `BrowserProfileSupport.clearStaleLock(at:)`이 `SingletonLock`·`SingletonSocket`·`SingletonCookie`만 지운다. 나머지 프로필 데이터는 보존된다.

## 8. 이 단계 밖

- 에이전트의 브라우저 제어, CDP 프록시, Playwright MCP — 2·3단계.
- Intel(x86_64) 엔진.
- 오프스크린 CEF 프로브(`mighty-browser-probe`, `scripts/run-browser-probe.sh`, `probe.json`) — 2026-09-24 사용자가 이 단계에서 뺐다. 위 화면 확인 표가 대신한다.
- Windows·모바일·릴레이 변경 없음(공유 로케일 사본 제외).
