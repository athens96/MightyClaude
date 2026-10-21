# Paperthin·Superpowers 스타일 완전 삭제 (v7 고정)

사용자 결정(2026-10-21): 클러드 마이티 스타일 중 Paperthin과 Superpowers는 파이프라인이 어색하다 → **완전히 삭제**하고, 설정 → 필요한 도구에서도 두 항목을 뺀다. 번들 매니페스트는 v6 고정 범위 안이므로 이 작업은 새 고정 `mighty-style-engine-v7`로 끝난다(태그는 사용자 허락됨).

남는 내장 스타일: Ouroboros, 클러드 플랜. 제삼자 스타일(oh-my-claudecode, gstack)은 그대로.

## 정한 것
1. **번들·골든 삭제**: `Resources/Styles/paperthin.json`, `superpowers.json`, `styles/golden/paperthin.panel.json`, `superpowers.panel.json`.
2. **내장 기능 `paperthin.casebook` 삭제**: 케이스북 읽기(`StyleCasebook` 등), 첨부·추천 경로, 앱의 `styleCasebooks`. 스키마의 `capabilities` 필드와 `E_CAPABILITY_*` 오류 코드는 남긴다(어휘가 비어 어떤 이름을 선언해도 알 수 없는 기능 오류가 된다). 도달할 수 없게 되는 코드가 있으면 계약 문서와 코퍼스 README에 그렇다고 적고, 코퍼스는 남은 어휘로 그 코드를 내는 사례로 바꾸거나 지운다.
3. **옛 폰 레거시 페이로드 `paperthin` 삭제**: `MobilePaperthin*` 모델, `MobileLegacyStyleAdapter`의 paperthin 경로, 와이어 어휘 `["cli","ouroboros","paperthin"]` → `["cli","ouroboros"]`. 맥·윈도우·폰 모두.
4. **예약 id**: `paperthin`은 예약 id로 남긴다(남이 그 이름을 주장해 옛 실행 창에 다시 붙는 것을 막는 안전 장치). `superpowers`는 원래 예약 아님 — 그대로.
5. **이미 이 스타일로 열린 창**: 데이터 이전 없음. §3.4대로 레지스트리에 id가 없으면 일반 CLI로 보인다.
6. **필요한 도구**: `superpowers`, `paperthin` 항목과 그 문구 키 삭제(맥·윈도우 공유 카탈로그 두 사본). `node`는 남기고 설명에서 Paperthin 언급을 뺀다.
7. **테스트**: Paperthin·Superpowers 전용 테스트는 지운다. 그 스타일을 예시로 쓰던 다른 테스트는 Ouroboros·클러드 플랜이나 테스트 안의 매니페스트로 재조준한다(검사 강도는 줄이지 않는다 — 상태 소스(v5) 검사는 상태 소스를 선언한 픽스처 매니페스트로).
8. **문서**: `docs/paperthin-mode.md`, `docs/superpowers-mode.md` 삭제, 계약 문서 §1.15 삭제와 v7 고정 이력, 도움말 4개 언어, 그 밖의 언급.
9. **고정 v7**: `scripts/check-style-freeze.sh`·`style-freeze.yml`의 기본 태그를 v7로. 마지막 엔진 커밋 P 위에 `styles/FREEZE`(P의 SHA)만 더한 커밋 T, 태그 v7 = T. P·T·태그를 한 번에 `--atomic`으로 푸시(대기 파일 방식은 번들 삭제를 허용하지 않으므로 쓰지 않는다). `docs/styles-followups.md` 머리에 두 SHA.

## 작업
- **T1 필요한 도구**: 카탈로그 두 사본, 맥 `SetupCatalog.swift`, 윈도우 `SetupCatalog.cs`, 문구 키(4개 언어 × 3 사본), 맥·윈도우 설정 테스트, 윈도우 스모크의 해당 행.
- **T2 맥 엔진·앱·계약**: 번들·골든·기능·레거시·맥 앱·맥 테스트·코퍼스·`docs/mighty-styles.md`·고정 스크립트/워크플로 기본 태그. 문구 파일은 손대지 않고, 쓰지 않게 된 키 목록만 보고.
- **T3 윈도우**(T2 뒤): Core 스타일·폰 호스트·WinUI·Core.Tests.
- **T4 폰**(T2 뒤, T3과 병렬): `lib/styles.ts`, `lib/mighty.ts`, `api/types.ts`, `sheets.tsx`, 테스트.
- **T5 문구·문서·도움말**: 남은 문구 키 삭제(3 사본 바이트 일치), 문서·도움말.
- **T6 검증·리뷰·고정**: 아래 검사 전부, 전체 리뷰, v7 고정, 푸시, CI.

## 검사
- 맥: `DEVELOPER_DIR=/Library/Developer/CommandLineTools bash scripts/test-native-macos.sh --scratch-path /tmp/mc-rmstyles --filter '<앵커 필터>'` (전체 실행 금지 — 반드시 `--filter`), 앱 빌드.
- 윈도우: `bash ~/Work/MightyClaude-wincatch/.superpowers/sdd/bin/core-checks.sh <repo>` → `RESULT: BASELINE`, `bash scripts/check-winui-compile.sh`.
- 폰: `cd mobile && npm run typecheck && npx jest`.
- 공통: `node scripts/check-locales.js`, `node scripts/check-design-tokens.js --check`, `node scripts/help/build.mjs --check`, `bash scripts/check-style-freeze.sh`(T 이후 0).
- 잔존 검색: `grep -rniE 'paperthin|superpowers|casebook'` — 남는 것은 예약 id, 고정 이력·결정 기록, 사용자가 쓰는 superpowers 플러그인 문서(`docs/superpowers/`)뿐.
