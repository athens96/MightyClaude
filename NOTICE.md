# NOTICE · 제3자 저작물 고지

Mighty Claude 자체는 MIT 라이선스로 배포됩니다(`LICENSE`). 아래 항목은 이 저장소가 참고하거나 포함한 제3자 저작물과 그 라이선스입니다.

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

## WebRTC — BSD 3-Clause / MIT

Mac 화면 공유(BETA)의 피어-투-피어 영상 전송은 WebRTC를 사용합니다. 아래 `licenses/` 파일은 각 상위 프로젝트가 배포하는 라이선스 전문을 그대로 받아 둔 것입니다.

### WebRTC 핵심 라이브러리 (macOS XCFramework)

Mac 앱은 미리 컴파일된 WebRTC XCFramework를 포함합니다. 이 바이너리는 Google이 관리하는 WebRTC 오픈소스 프로젝트(https://webrtc.googlesource.com/src)를 빌드한 것이며, 아래 두 배포 방식 중 어느 것을 쓰더라도 같은 핵심 라이브러리 고지가 적용됩니다.

- 프로젝트: WebRTC — https://webrtc.org / https://webrtc.googlesource.com/src
- 저작권: Copyright (c) 2011, The WebRTC project authors. All rights reserved.
- 라이선스: BSD 3-Clause License — 전문은 `licenses/WebRTC-BSD-3-Clause.txt` (상위 저장소의 `LICENSE`와 바이트 단위로 동일)
- Mac 앱 번들: 같은 전문이 `native/licenses/WebRTC-LICENSE`에 있고, 빌드가 앱의 `Contents/Resources/ThirdPartyLicenses`로 복사하므로 바이너리 배포에도 고지가 함께 갑니다

### stasel/WebRTC (macOS 기본 배포 패키지)

- 프로젝트: stasel/WebRTC — https://github.com/stasel/WebRTC
- 라이선스: BSD 3-Clause License (Google WebRTC 고지 포함) — 전문은 `licenses/stasel-WebRTC-BSD-3-Clause.txt`

### livekit/webrtc-xcframework (macOS 대안 배포)

번들된 CEF와 심볼이 충돌하면 stasel/WebRTC 대신 LK- 접두사가 붙은 이 배포를 사용합니다.

- 프로젝트: livekit/webrtc-xcframework — https://github.com/livekit/webrtc-xcframework
- 저작권: Copyright (c) 2021 WebRTC SDKs
- 라이선스: MIT License — 전문은 `licenses/livekit-webrtc-xcframework-MIT.txt`

### react-native-webrtc (Android 앱)

- 프로젝트: react-native-webrtc — https://github.com/react-native-webrtc/react-native-webrtc
- 저작권: Copyright (c) 2017-present React Native WebRTC Community / Copyright (c) 2015-2017 Howard Yang
- 라이선스: MIT License — 전문은 `licenses/react-native-webrtc-MIT.txt`

### @config-plugins/react-native-webrtc (Expo 설정 플러그인)

- 프로젝트: expo/config-plugins — https://github.com/expo/config-plugins (`packages/react-native-webrtc`)
- 라이선스: MIT License (패키지 메타데이터에 선언되어 있고, 상위 저장소에는 별도 전문 파일이 없습니다)

## Zstandard (zstd) — BSD 3-Clause

Mac 화면 공유(BETA)의 클립보드는 휴대폰과 같은 zstd 형식으로 압축합니다. Mac 앱은 zstd 라이브러리를 소스로 빌드해 포함합니다(Swift 패키지 `facebook/zstd` 1.5.7).

- 프로젝트: Zstandard — https://github.com/facebook/zstd
- 저작권: Copyright (c) Meta Platforms, Inc. and affiliates. All rights reserved.
- 라이선스: BSD 3-Clause License(상위 프로젝트는 BSD와 GPLv2 중 선택하게 하며, 이 앱은 BSD를 따릅니다) — 전문은 `licenses/zstd-BSD-3-Clause.txt`, 앱 번들에는 `native/licenses/zstd-LICENSE`가 함께 들어갑니다

## 그 밖의 구성 요소

- macOS 앱과 펫 리소스의 타사 라이선스: `native/licenses/`, `assets/`
- 릴레이 서버(`relay/`)와 모바일 앱(`mobile/`)의 npm 의존성(`ws`, `@noble/curves`, `@noble/hashes`, `@noble/ciphers`, Expo 등)은 각 패키지의 `LICENSE`를 따르며 `package-lock.json`에 버전이 고정되어 있습니다.

---

# NOTICE (English)

Mighty Claude itself is distributed under the MIT License (see `LICENSE`). This file lists third-party works this repository references or includes.

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


## WebRTC — BSD 3-Clause / MIT

The peer-to-peer video transport of the Mac screen-share feature (BETA) uses WebRTC. Each file under `licenses/` below is the upstream project's licence text, copied verbatim.

### WebRTC core library (macOS XCFramework)

The Mac app embeds a pre-compiled WebRTC XCFramework. The binary is built from the Google-maintained WebRTC open-source project at https://webrtc.googlesource.com/src, so the same core notice applies whichever of the two distributions below is used.

- Project: WebRTC — https://webrtc.org / https://webrtc.googlesource.com/src
- Copyright (c) 2011, The WebRTC project authors. All rights reserved.
- License: BSD 3-Clause License — full text in `licenses/WebRTC-BSD-3-Clause.txt` (byte-identical to the upstream `LICENSE`)
- Mac app bundle: the same text is kept at `native/licenses/WebRTC-LICENSE`, which the build copies into the app's `Contents/Resources/ThirdPartyLicenses`, so the notice travels with the binary distribution as well

### stasel/WebRTC (default macOS distribution package)

- Project: stasel/WebRTC — https://github.com/stasel/WebRTC
- License: BSD 3-Clause License (includes the Google WebRTC notice) — full text in `licenses/stasel-WebRTC-BSD-3-Clause.txt`

### livekit/webrtc-xcframework (alternative macOS distribution)

Used in place of stasel/WebRTC — with LK-prefixed symbols — if symbols clash with the bundled CEF.

- Project: livekit/webrtc-xcframework — https://github.com/livekit/webrtc-xcframework
- Copyright (c) 2021 WebRTC SDKs
- License: MIT License — full text in `licenses/livekit-webrtc-xcframework-MIT.txt`

### react-native-webrtc (Android app)

- Project: react-native-webrtc — https://github.com/react-native-webrtc/react-native-webrtc
- Copyright (c) 2017-present React Native WebRTC Community / Copyright (c) 2015-2017 Howard Yang
- License: MIT License — full text in `licenses/react-native-webrtc-MIT.txt`

### @config-plugins/react-native-webrtc (Expo config plugin)

- Project: expo/config-plugins — https://github.com/expo/config-plugins (`packages/react-native-webrtc`)
- License: MIT License (declared in the package metadata; the upstream repository publishes no separate licence text file)

## Zstandard (zstd) — BSD 3-Clause

The Mac screen-share (BETA) clipboard is compressed with zstd, the same format the phone uses. The Mac app builds the zstd library from source and ships it (Swift package `facebook/zstd` 1.5.7).

- Project: Zstandard — https://github.com/facebook/zstd
- Copyright: Copyright (c) Meta Platforms, Inc. and affiliates. All rights reserved.
- License: BSD 3-Clause License (upstream offers BSD or GPLv2; this app follows BSD) — full text in `licenses/zstd-BSD-3-Clause.txt`; the app bundle carries `native/licenses/zstd-LICENSE`

## Provider marks

The Claude, Codex (OpenAI) and Gemini marks drawn in the app come from outline data in `native/macos/Sources/MightyCore/ProviderMark.swift`. The Claude and Gemini outlines follow Simple Icons (https://simpleicons.org, CC0 1.0); the OpenAI outline is the company's published mark. All three are trademarks of their owners (Anthropic, OpenAI, Google) and are used only to identify which company's CLI a pane runs; MightyClaude is not affiliated with or endorsed by them.

The phone app draws the same outlines: the data is copied into `mobile/src/lib/provider-marks.ts` and rendered with `react-native-svg`, under the same terms.
