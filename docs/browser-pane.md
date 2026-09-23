# 브라우저 창 (1단계)

CEF 엔진을 패키징하고 손으로 구동하는 브라우저 탭. 2단계(에이전트 제어)는 별도 시드.

## 고정 버전

`native/macos/BrowserEngine.lock` 파일이 단일 진실의 원천이다.

```json
{
  "arch": "arm64",
  "cef": {
    "version": "152.0.9+g07f67cd+chromium-152.0.7977.134",
    "url": "https://cef-builds.spotifycdn.com/...",
    "sha256": "f4efc5e8447e37d3dd1a982047310f1af16d08c5e81136d33a92e6cbff2717f7"
  },
  "node": {
    "version": "22.23.2",
    "url": "https://nodejs.org/dist/v22.23.2/node-v22.23.2-darwin-arm64.tar.gz",
    "sha256": "61130f394c1630d211dd50aecc4353d379480f36d3ac913cd85dbba1aed585c6"
  }
}
```

`sha256` 필드는 64자 16진수 문자열(256비트 SHA-256 해시)이다.

## 엔진 내려받기

```sh
scripts/fetch-browser-engine.sh
```

- 기본 캐시 경로: `~/Library/Caches/MightyClaude/browser-engine/<lock-sha16>/`  
  (`MIGHTY_BROWSER_ENGINE_CACHE` 환경 변수로 재정의 가능)
- `BrowserEngine.lock`의 sha256과 일치하는 아카이브가 캐시에 있으면 **"cache hit"** 출력 후 종료
- 불일치 또는 캐시 미스이면 재내려받기
- 각 내려받기에 300초 제한; 초과 시 비정상 종료

엔진 아카이브와 추출 파일은 저장소에 커밋하지 않는다.

## 앱 번들 레이아웃 (MIGHTY_BROWSER_ENGINE=1)

```
MightyClaude.app/
  Contents/
    Frameworks/
      Chromium Embedded Framework.framework/   ← CEF 프레임워크
    Helpers/
      MightyClaude (GPU).app/
      MightyClaude (Plugin).app/
      MightyClaude (Renderer).app/
      MightyClaude Helper.app/
    Resources/
      browser/
        node/
          bin/
            node                               ← Node 런타임
      ThirdPartyLicenses/
        cef-license.txt
        chromium-license.txt
```

헬퍼 앱은 각각 `dev.mightyclaude.native.gpu`, `dev.mightyclaude.native.plugin`, `dev.mightyclaude.native.renderer`, `dev.mightyclaude.native.helper` 번들 ID를 갖는다.

`MIGHTY_BROWSER_ENGINE=1` 없이 빌드하면 이 단계를 건너뛰고, 엔진 없이 열린 브라우저 탭은 크래시 대신 `browser.engine.missing` 메시지를 표시한다.

## 프로필 경로와 잠금 파일 복구

각 워크스페이스는 분리된 CEF 프로필 디렉터리를 갖는다:

```
~/Library/Application Support/MightyClaude/browser-profiles/<workspaceProfileKey>/
```

- 같은 워크스페이스의 브라우저 탭은 프로필을 공유한다
- 로그인·쿠키는 엔진 재시작 후에도 유지된다
- 엔진이 비정상 종료하며 남긴 `SingletonLock` 파일은 다음 기동 시 자동 삭제된다(프로필 데이터는 삭제하지 않음)

## 핀 올리기

1. `BrowserEngine.lock`의 `version`, `url`, `sha256` 세 필드를 새 릴리스 값으로 교체한다
2. `scripts/fetch-browser-engine.sh` 실행 — 새 해시로 캐시가 없으면 내려받는다
3. `scripts/run-browser-probe.sh <outdir>` 로 오프스크린 프로브를 실행해 `probe.json`의 `cef_version`이 새 버전과 일치하는지 확인한다
4. 변경 내용을 커밋하고 빌드·서명 확인

## 오프스크린 프로브

`mighty-browser-probe` 실행 파일이 CEF를 윈도우리스로 초기화하고, 로컬 픽스처 페이지를 서빙한 뒤 `<outdir>/probe.json`을 쓴다:

```json
{
  "cef_version": "152.0.9+...",
  "rendered": true,
  "page_title": "MightyClaude Browser Probe",
  "profile": {
    "persisted_across_restart": true,
    "isolated_between_workspaces": true,
    "stale_lock_recovered": true,
    "data_preserved": true
  }
}
```

`scripts/run-browser-probe.sh <outdir>` 로 구동. 엔진 기동에 120초, 전체에 600초 제한.

## 화면 확인 (기기 미확인)

| 항목 | 확인 내용 | 결과 |
|------|-----------|------|
| 새 브라우저 탭 메뉴 항목 | 새 탭 메뉴에 "새 브라우저 탭" 항목이 표시됨 | 기기 미확인 |
| 브라우저 탭 열기 | 탭이 열리고 주소창·뒤로·앞으로·새로 고침 버튼이 표시됨 | 기기 미확인 |
| 엔진 없는 메시지 | 엔진 미설치 빌드에서 `browser.engine.missing` 메시지가 표시됨 | 기기 미확인 |
| 프로필 격리 | 다른 워크스페이스의 브라우저 탭이 서로 다른 프로필을 사용함 | 기기 미확인 |
