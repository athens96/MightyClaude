# NOTICE · 제3자 저작물 고지

MightyClaude 자체는 MIT 라이선스로 배포됩니다(`LICENSE`). 아래 항목은 이 저장소가 참고하거나 포함한 제3자 저작물과 그 라이선스입니다.

## Paseo — Apache License 2.0

모바일 리모트의 **릴레이 연결 방식**(호스트와 클라이언트가 릴레이 서버에 바깥으로 접속하고, 릴레이는 암호문만 전달하며, 페어링 정보를 QR/링크로 교환하는 구조와 종단 간 암호화 핸드셰이크의 설계)은 Paseo 프로젝트를 참고해 설계했습니다.

- 프로젝트: Paseo — https://github.com/getpaseo/paseo
- 저작권: Copyright (c) 2025-present Mohamed Boudra
- 라이선스: Apache License, Version 2.0 — 전문은 `licenses/Apache-2.0.txt`, 원문은 http://www.apache.org/licenses/LICENSE-2.0

이 저장소의 `relay/`, `native/macos/Sources/MightyCore/Remote/RelayChannel.swift`, `native/macos/Sources/MightyCore/Remote/MobileRemoteService.swift`, `mobile/src/api/relay/`는 `docs/relay.md`의 규격을 바탕으로 새로 작성한 구현이며, Paseo의 소스 파일을 복사하지 않았습니다. 그럼에도 설계의 출처를 밝히기 위해 Apache License 2.0 §4의 취지에 따라 위 고지를 둡니다. 향후 Paseo의 코드를 직접 가져오는 경우에는 해당 파일에 원저작권 표시와 라이선스 헤더를 유지하고 이 문서에 파일 목록을 추가해야 합니다.

## Paperthin — MIT License

마이티 모드의 Paperthin 스타일은 Paperthin 프로젝트의 스킬 카탈로그를 화면으로 옮긴 것입니다. 앱에 내장된 스킬 이름·이모지·한 줄 요약·호출자 구분(`native/macos/Sources/MightyCore/PaperthinCatalog.swift`)은 Paperthin의 한국어 README 색인에서 가져왔습니다. 스킬 자체는 포함하지 않으며, 사용자가 Paperthin의 설치 명령으로 직접 설치합니다.

- 프로젝트: Paperthin — https://github.com/LilMGenius/paperthin
- 라이선스: MIT License (Copyright (c) Paperthin contributors)

## 그 밖의 구성 요소

- macOS 앱과 펫 리소스의 타사 라이선스: `native/licenses/`, `assets/`
- 릴레이 서버(`relay/`)와 모바일 앱(`mobile/`)의 npm 의존성(`ws`, `@noble/curves`, `@noble/hashes`, `@noble/ciphers`, Expo 등)은 각 패키지의 `LICENSE`를 따르며 `package-lock.json`에 버전이 고정되어 있습니다.

---

# NOTICE (English)

MightyClaude itself is distributed under the MIT License (see `LICENSE`). This file lists third-party works this repository references or includes.

## Paseo — Apache License 2.0

The relay-based mobile remote design (host and client both dialing out to a relay that forwards only ciphertext, pairing exchanged as a QR/link, and the shape of the end-to-end encryption handshake) was designed with reference to the Paseo project.

- Project: Paseo — https://github.com/getpaseo/paseo
- Copyright (c) 2025-present Mohamed Boudra
- License: Apache License, Version 2.0 — full text in `licenses/Apache-2.0.txt` and at http://www.apache.org/licenses/LICENSE-2.0

The implementations in `relay/`, `native/macos/Sources/MightyCore/Remote/RelayChannel.swift`, `native/macos/Sources/MightyCore/Remote/MobileRemoteService.swift` and `mobile/src/api/relay/` were written from the specification in `docs/relay.md`; no Paseo source files were copied. This notice is kept in the spirit of Apache License 2.0 §4 to credit the origin of the design. If Paseo code is ever incorporated directly, keep the original copyright and license headers in those files and list them here.

## Paperthin — MIT License

The Paperthin style of Mighty mode presents the Paperthin project's skill catalogue. The skill names, emoji, one-line summaries and invocation flags embedded in `native/macos/Sources/MightyCore/PaperthinCatalog.swift` come from Paperthin's Korean README index. The skills themselves are not bundled; users install them with Paperthin's own command.

- Project: Paperthin — https://github.com/LilMGenius/paperthin
- License: MIT License (Copyright (c) Paperthin contributors)


## Provider marks

The Claude, Codex (OpenAI) and Gemini marks drawn in the app come from outline data in `native/macos/Sources/MightyCore/ProviderMark.swift`. The Claude and Gemini outlines follow Simple Icons (https://simpleicons.org, CC0 1.0); the OpenAI outline is the company's published mark. All three are trademarks of their owners (Anthropic, OpenAI, Google) and are used only to identify which company's CLI a pane runs; MightyClaude is not affiliated with or endorsed by them.
