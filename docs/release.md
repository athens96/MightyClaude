# 릴리스 순서 (Windows PC에서 한 단계씩)

`scripts/release.py`가 릴리스를 열 단계로 나눠 한 번에 하나씩 돌린다. 단계마다 앞 단계가 끝났는지 확인하고(`--force`로 건너뛸 수 있지만 `push`·`upload`·`github`에는 쓸 수 없다), 진행 상태는 `release/<버전>/state.json`에 남긴다(`release/`는 git에 올라가지 않는다). 밖으로 내보내는 단계(`push`, `upload`, `github`)는 `--yes`가 없으면 할 일만 보여 주고 멈춘다.

```
check → version → push → ci → download → manifest → page → upload → github → verify
```

`python scripts/release.py status`가 끝난 단계와 다음 단계를 보여 주고, `python scripts/release.py next [옵션]`은 다음 단계를 돌린다(내보내는 단계는 여전히 `--yes`가 있어야 한다).

## 준비물

| 무엇 | 어디에 쓰나 |
|---|---|
| main 브랜치, 깨끗한 작업 트리 | `check`, `version` |
| `gh auth login` | `ci`, `download`, `github`, `verify` |
| `wrangler` (npm 전역 설치)와 그 OAuth 로그인 | `upload` — 스크립트는 로그인 상태를 묻지 않으므로 올리기 전에 `wrangler login`이 되어 있어야 한다 |
| `pip install cryptography` | 서명 확인 (로컬 서명을 고르면 서명에도) |
| main에 있는 `.github/workflows/release-manifest.yml`과 환경 `release`의 시크릿 `MIGHTY_UPDATE_SIGNING_KEY` | `manifest` — CI가 서명한다. 개인 키는 이 시크릿에만 있고 어느 PC에도 없다. `workflow_dispatch` 워크플로라 기본 브랜치(main)에 있어야 부를 수 있다. 설정은 아래 [서명 키와 GitHub 설정](#서명-키와-github-설정) |
| (대안) 업데이트 서명 키 파일 경로 | 키 파일이 있을 때만: `check --sign-key`나 `manifest --sign-key`로 주면 CI 대신 로컬에서 서명한다. 스크립트는 경로만 넘기고 내용은 `make-update-manifest.py`만 읽는다 |
| (폰 앱을 바꿨다면) EAS `preview` 프로필로 만든 APK | `manifest --apk` |

## 단계

```bash
# 1. 준비 확인 (main·깨끗함·origin/main·gh·wrangler·cryptography·서명 워크플로·버전). 노트 템플릿을 만든다.
python scripts/release.py check --version 0.4.48
#    → release/0.4.48-notes.md            GitHub 릴리스 본문 (한국어·English, 필요하면 简体中文·日本語)
#    → release/0.4.48/update-notes.md     앱의 업데이트 안내에 보일 짧은 한국어 목록
#    "(채워 주세요)"를 지우고 내용을 쓴다. {{…}} 자리는 그대로 둔다 — manifest·github 단계가
#    매번 release/0.4.48/release-notes.md로 채워 낸다(템플릿 파일은 바뀌지 않는다).

# 2. VERSION을 올리고 로컬에 릴리스 커밋 "(release) 0.4.48: <요약>"을 만든다. push는 하지 않는다.
python scripts/release.py version --summary "<한 줄 요약>" [--co-author "Claude Opus 5.5 <noreply@anthropic.com>"]

# 3. git push origin main
python scripts/release.py push --yes

# 4. 그 커밋의 native-macos·native-windows 실행(push 이벤트)이 끝날 때까지 1분마다 본다.
python scripts/release.py ci
#    실패하면 고친 커밋을 main에 올리고 그 커밋으로 다시:
python scripts/release.py ci --commit <고친 커밋 sha>

# 5. 아티팩트 세 개를 release/0.4.48/로 받고 Windows .sha256을 맞춰 본다.
python scripts/release.py download

# 6. CI(release-manifest.yml)에 서명을 맡겨 latest.json을 받고, 공개 키로 서명·버전·build·notes·url·sha256·크기를 확인한다.
python scripts/release.py manifest [--apk <APK 경로>]

# 7. 공개 내려받기 페이지를 받아 버전만 바꾼 release/0.4.48/index.html을 만들고 차이를 보여 준다.
python scripts/release.py page

# 8. R2에 올린다. 먼저 --dry-run으로 명령을 보고, 그다음 --yes.
python scripts/release.py upload --dry-run
python scripts/release.py upload --yes

# 9. GitHub 릴리스 v0.4.48 (Latest)
python scripts/release.py github --yes

# 10. 공개 latest.json·페이지·파일 크기·GitHub 첨부를 확인한다.
python scripts/release.py verify
```

## 단계별 세부

### check

`--version`으로 정한 버전을 `release/.current`에 적어 두므로, 이후 단계는 버전을 다시 주지 않아도 된다. 버전은 `x.y.z`이어야 하고 공개 `latest.json`의 버전보다 커야 한다. origin/main보다 뒤처져 있거나 push 안 된 로컬 커밋이 있어도 멈춘다. wrangler는 설치만 보고 로그인은 확인하지 않는다(`wrangler whoami`를 부르지 않는다) — `upload` 전에 `wrangler login`이 되어 있어야 한다. 서명은 기본이 CI라 `gh workflow view release-manifest.yml`로 워크플로가 보이는지 확인하고, `--sign-key`를 주면 대신 그 파일이 있는지 본다. 확인하지 못한 항목이 있으면 끝나지 않은 것으로 남고, 고친 뒤 다시 돌리면 된다. 노트 파일이 이미 있으면 건드리지 않는다.

### version

`VERSION`만 바꾸는 커밋 하나를 만든다(이전 릴리스 커밋과 같다). 이미 `VERSION`이 그 버전이고 마지막 커밋이 `(release) <버전>:`이면 커밋을 새로 만들지 않는다.

### ci

빌드 번호(`Mac build`, `latest.json`의 `build`)는 빌드한 커밋의 `git rev-list --count`이다. CI(`fetch-depth: 0`)와 `scripts/build-macos.sh`가 같은 값을 `CFBundleVersion`에 넣는다. 릴리스 커밋 뒤에 CI만 고친 커밋으로 패키지를 만들 때는(`0.4.47`은 릴리스 커밋 `e2899123`(1346)이 아니라 그 뒤 `73879aeb`(1348)로 나갔다) `ci --commit <sha>`로 그 커밋을 고른다. 그 커밋은 릴리스 커밋 뒤에 있어야 하고 `VERSION`이 같아야 한다. 이후 build 번호, GitHub 태그 대상이 모두 이 커밋을 따른다.

`ci`는 먼저 `git fetch origin main`을 하고, 고른 커밋이 origin/main에 없으면 바로 멈춘다("먼저 main에 push하세요") — 기능 브랜치 커밋을 태그하거나 오지 않을 CI를 180분 기다리지 않는다. `ci`가 다른 커밋이나 다른 실행을 고르면 `download` 이후 단계가 모두 다시 해야 하는 것으로 돌아간다.

### download

`gh run download`로 `MightyClaude-native-macos`, `MightyClaude-native-windows-x64`, `MightyClaude-native-windows-arm64`를 받는다. 아티팩트마다 어느 실행에서 받았는지 `state.json`에 적어 두고, 파일이 있고 같은 실행에서 받은 것일 때만 다시 받지 않는다(`--redownload`로 강제로 다시 받기). 실행이 바뀌었으면(`ci --commit`) 새로 받는다. 300 MiB(`wrangler r2 object put` 한도)보다 큰 파일이 있으면 여기서 멈춘다 — 크기 게이트(480 MiB)는 통과해도 이 스크립트로는 올릴 수 없다. 서명된 `latest.json`은 `manifest` 단계가 따로 받는다.

### manifest

업데이트 서명 개인 키는 GitHub 환경 `release`의 시크릿 `MIGHTY_UPDATE_SIGNING_KEY`에만 있으므로, 기본으로 CI가 서명한다. 이것은 아무것도 내보내지 않으므로 `--yes`가 필요 없고, 부르는 명령을 먼저 보여 준다.

```bash
gh workflow run release-manifest.yml --ref main -f version=<버전> \
  -f macos_run=<ci의 macOS 실행 id> -f windows_run=<ci의 Windows 실행 id> \
  -f notes_b64=<update-notes.md의 UTF-8 base64> -f request=<버전>-<무작위 8자>
```

1. 실행 id는 `ci` 단계가 `state.json`의 `runs`에 적어 둔 값이다. 노트는 `release/<버전>/update-notes.md`(BOM은 뺀다)를 base64로 넘긴다 — 여러 줄, 따옴표, `$`가 `-f` 인자와 워크플로 셸을 그대로 지나가게 하려는 것이다. build 번호는 넘기지 않는다: 워크플로가 빌드 커밋에서 직접 센다.
2. 워크플로의 실행 이름이 `Release manifest <버전> [<request>]`라서, `gh run list --workflow release-manifest.yml --event workflow_dispatch --branch main`에서 main 실행 중 이번 `request` 값이 든 것만 고른다(전에 부른 실행이나 다른 사람이 부른 실행을 잡지 않는다). 같은 값의 실행이 둘 이상이면 멈춘다. 끝날 때까지 보고, 실행 id와 그 커밋을 `state.json`의 `manifestRun`·`manifestRunSha`에 적는다. 실패하면 `gh run view <id> --log-failed`를 알려 주고 멈춘다.
3. 워크플로(ubuntu, 잡은 `github.ref == refs/heads/main`일 때만, 환경 `release`)는 아무것도 받기 전에 두 실행의 출처를 `gh api repos/<저장소>/actions/runs/<id>`로 확인한다: 워크플로 파일이 `native-macos.yml`·`native-windows.yml`, 이벤트 `push`, 브랜치 `main`, 이 저장소(포크 아님), 완료·성공, 두 실행이 같은 커밋. 그 커밋이 디스패치한 main 커밋의 조상인지, 그 커밋의 `VERSION`이 입력 버전인지 보고, build 번호를 `git rev-list --count <커밋>`으로 센다(`ci` 단계와 같은 셈). 그다음 `run-id`로 아티팩트 세 개를 받아 Windows `.sha256`을 맞춰 보고, 해시를 고정한 `scripts/requirements-sign.txt`의 `cryptography`로(`MIGHTY_SIGNER=cryptography`) 서명한다. 시크릿은 `$RUNNER_TEMP`에 umask 077로 쓰고(비었으면 실패) `always()` 단계에서 지운다. 만든 봉투를 이 스크립트의 `verify_envelope`로 공개 키와 패키지에 대해 확인한 뒤 `latest.json`·`latest.unsigned.json`을 아티팩트 `MightyClaude-release-manifest`로 남긴다. 액션은 모두 커밋 SHA로 고정했다.
4. 스크립트는 그 아티팩트를 `gh run download <id> -n MightyClaude-release-manifest`로 `release/<버전>/`에 받아, 공개 키(`C02DD3qWG+A5gJdYwpaEnvpzYugo354CLtUtU+oXmZ4=`, 앱과 `.github/workflows/native-windows.yml`의 값)로 다시 확인한다. 버전·build(워크플로가 센 값이 `ci`가 적은 값과 같아야 한다)·notes·세 패키지의 url/sha256/크기가 **이 PC가 받은** 파일·노트와 같지 않으면 멈춘다. `upload`도 올리기 전에 같은 확인을 다시 한다.

`manifest`를 다시 돌리면 매번 새로 부른다 — `ci`를 다시 해 실행이 바뀌었으면 새 실행 id로 서명한다.

- `--sign-key <경로>`(또는 `check --sign-key`로 기억한 경로): 키 파일이 있을 때만 쓰는 대안. CI를 부르지 않고 `make-update-manifest.py`로 로컬에서 서명한다(swiftc가 없는 Windows에서는 `cryptography`). 확인은 같다. **새 키를 만들어 쓰지 않는다** — 설치된 앱은 지금 공개 키로만 확인하므로 업데이트가 멈춘다.

- `--apk <경로>`: 그 APK를 `release/<버전>/MightyClaude-<버전>.apk`로 복사해 올리기·GitHub 첨부에 넣는다. APK 버전이 릴리스 버전과 다르면 `--apk-version`. 빼려면 `--no-apk`.
- `--apk`가 없으면 폰 APK(`mightyclaude/android/MightyClaude-android.apk`)는 그대로 두고, 노트의 Android 줄은 공개 페이지의 이전 APK 버전과 그 GitHub 릴리스 링크로 채운다. 노트에 폰 APK가 그대로라고 적어 둔다.
- 릴리스 노트 템플릿(`release/<버전>-notes.md`)은 고치지 않고, `{{DATE}}`·`{{MAC_BUILD}}`·`{{ANDROID}}`·SHA-256·크기 표를 지금 상태로 채운 본문을 `release/<버전>/release-notes.md`에 새로 쓴다. `github`가 같은 일을 다시 해서 그 파일을 쓰므로, 다시 받거나 다시 서명해도 본문의 값이 옛것으로 남지 않는다.
- 노트 파일은 BOM이 있어도 읽는다. `update-notes.md`가 비었거나 채워지지 않았으면 멈춘다.

`ci`(실행이 바뀐 경우), `download`, `manifest`, `page`를 다시 돌리면 그 뒤 단계는 다시 해야 하는 것으로 돌아간다(새 서명, 새 파일).

### page

공개 페이지(`…/mightyclaude/`)를 받아 `<p>버전 X</p>`와 `href="X/` 두 곳만 새 버전으로 바꾼다. 새 APK가 있으면 APK 링크의 `download="MightyClaude-X.apk"` 이름도 바꾼다. 이전 버전을 찾지 못하면 멈춘다. 차이(diff)를 보고 이상하면 올리지 않는다.

### upload

`wrangler r2 object put mightyclaude/<키> --file … --content-type … [--cache-control …] --remote`를 아래 순서로 부른다. 앞의 파일이 다 올라가기 전에는 그것을 가리키는 것이 공개되지 않는다. 폰 APK는 고정 키라 옛 APK를 덮어쓰므로, 업데이트(`latest.json`)가 공개된 뒤에 올린다. R2가 가끔 500을 주므로 한 객체에 세 번까지(5초, 10초 쉬고) 다시 시도하고, 세 번 다 실패하면 그 자리에서 멈춘다(뒤의 객체는 보내지 않는다). `upload --yes`를 다시 돌리면 처음부터 다시 올린다(같은 내용을 덮어쓴다).

아무것도 보내기 전에 확인한다:

- 올릴 모든 파일(패키지 다섯 개, APK)의 sha256이 `download`·`manifest` 때 적어 둔 값과 같은지, 300 MiB를 넘지 않는지. 패키지 기록이 없으면 멈춘다.
- `latest.json`이 공개 키로 서명되어 있고 파일과 맞는지.
- 공개 `latest.json`: 더 새 버전이면 멈춘다. 같은 버전인데 다른 패키지(sha256)를 가리키면 `--force-overwrite-live` 없이는 멈춘다. 읽지 못하면(네트워크) 버전 되돌림을 막을 수 없으므로 `--allow-unreadable-live` 없이는 멈춘다.

| 순서 | 키 | Content-Type | Cache-Control |
|---|---|---|---|
| 1 | `mightyclaude/<버전>/MightyClaude-macos.zip` | `application/zip` | — |
| 2–3 | `mightyclaude/<버전>/MightyClaude-windows-{x64,arm64}.zip` | `application/zip` | — |
| 4–5 | `mightyclaude/<버전>/MightyClaude-windows-{x64,arm64}-setup.exe` | `application/vnd.microsoft.portable-executable` | — |
| 6 | `mightyclaude/latest.json` | `application/json` | `no-cache` |
| 7 | `mightyclaude/android/MightyClaude-android.apk` (APK가 있을 때만) | `application/vnd.android.package-archive` | `no-cache` |
| 8–9 | `mightyclaude/index.html`, `mightyclaude/` | `text/html; charset=utf-8` | `no-cache` |

페이지를 `mightyclaude/` 키에도 쓰는 까닭은 공개 버킷 주소가 `index.html`을 붙여 주지 않기 때문이다(`scripts/help/upload.sh`와 같다).

### github

노트를 지금 상태로 다시 채워(`release/<버전>/release-notes.md`) `gh release create v<버전> --draft --target <빌드 커밋> --title "Mighty Claude <버전>" --notes-file release/<버전>/release-notes.md`에 패키지 다섯 개와(있으면) `MightyClaude-<APK 버전>.apk`를 붙여 초안으로 만들고, 첨부가 다 붙은 뒤 `gh release edit v<버전> --draft=false --latest`로 공개한다. 그래서 파일 없는 릴리스가 공개되는 순간이 없다.

- 노트에 `(채워 주세요)`, `(fill in)`, `{{…}}`가 남아 있거나 첫 줄의 Android 버전이 APK 유무와 맞지 않으면 멈춘다. 첨부할 파일의 sha256이 기록과 다르면 멈춘다.
- 같은 태그의 릴리스가 이미 있으면 새로 만들지 않고, 빠진 첨부만 `gh release upload`로 붙이고, 초안이면 공개한다. 할 일이 있는데 `--yes`가 없으면 명령만 보여 주고 멈춘다.

### verify

공개 `latest.json`을 다시 받아 서명·버전·파일과의 일치를 확인하고, 각 패키지(와 APK) 주소의 `Content-Length`가 받은 파일 크기와 같은지, 페이지가 새 버전인지, GitHub 릴리스가 초안이 아니고 첨부가 다 있는지 본다. 주소 하나를 읽지 못해도 나머지를 계속 보고, 실패한 항목을 모아 마지막에 알려 준다.

## 서명 키와 GitHub 설정

업데이트 서명 개인 키(공개 키 `C02DD3qWG+A5gJdYwpaEnvpzYugo354CLtUtU+oXmZ4=`의 짝, 0.4.48부터)는 GitHub 환경 `release`의 시크릿 `MIGHTY_UPDATE_SIGNING_KEY`와 소유자의 오프라인 백업에 있다. 0.4.47까지의 키는 잃었다([app-update.md](app-update.md#서명)). 설치된 앱은 이 공개 키로만 `latest.json`을 확인하므로, 키를 새로 만들면 기존 앱이 업데이트를 받지 못한다. 그래서 키를 지키는 설정이 릴리스의 일부다. 저장소 Settings에서 한 번:

1. **Environments → New environment `release`.**
   - Deployment branches and tags: **Selected branches and tags**, 규칙 `main` 하나만.
   - (선택) Required reviewers에 본인을 넣으면 실행마다 승인을 한 번 누른 뒤에야 시크릿이 풀린다.
2. **`release` 환경에 시크릿 `MIGHTY_UPDATE_SIGNING_KEY`를 둔다.** 저장소 시크릿의 값은 GitHub에서 읽어 낼 수 없으므로 옮기려면 원래 키 파일 내용(한 줄 base64 시드)이 필요하다. 값을 넣은 뒤 저장소 시크릿(Settings → Secrets and variables → Actions → Repository secrets)의 `MIGHTY_UPDATE_SIGNING_KEY`는 지운다. 그 사본이 없으면 저장소 시크릿을 그대로 두고, `release` 환경에는 빈 값이나 다른 값을 넣지 않는다(환경 시크릿이 없으면 잡은 저장소 시크릿을 쓴다). 이 상태에서는 이 워크플로 자체는 `main`·`release` 환경으로만 돌지만, 쓰기 권한이 있는 사람이 다른 브랜치에 올린 워크플로는 여전히 저장소 시크릿을 읽을 수 있다 — 환경 시크릿으로 옮겨야 막힌다.
3. **다른 워크플로가 시크릿을 쓰지 않게 둔다.** `native-macos.yml`의 옛 `sign-manifest` 잡은 지웠다(어느 브랜치 push든 시크릿을 받을 수 있었다). 이제 `secrets.MIGHTY_UPDATE_SIGNING_KEY`를 부르는 곳은 `release-manifest.yml`의 서명 단계 하나다.
4. (권장) Settings → Actions → General: 포크의 pull request 워크플로에 승인 요구, 기본 `GITHUB_TOKEN` 권한은 읽기 전용.
5. **키를 다시 손에 넣게 되면 오프라인 암호화 백업을 만든다** — 예: 암호로 잠근 `age`/`gpg` 파일을 USB 두 개에 따로, 암호는 비밀번호 관리자에. 백업 없이 시크릿이 지워지면(저장소 삭제·이전 등) 기존 앱은 수동 재설치 말고는 업데이트할 길이 없다.

## 그 밖

- 도움말 사이트를 바꿨다면 따로 올린다: `node scripts/help/build.mjs --shots <dir>` 후 `scripts/help/upload.sh --dry-run`, 그다음 `scripts/help/upload.sh`.
- 테스트: `python -m unittest scripts/tests/test_release.py` (네트워크·실제 키 없이 페이지 치환, 매니페스트 서명 확인, CI 서명 호출·실행 찾기·받기(가짜 gh와 일회용 키), 단계 순서, wrangler 명령을 본다).
