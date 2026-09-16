# MightyClaude 기본 아이콘

사용자가 제공한 안경 쓴 너구리 이미지를 편집한 슈퍼히어로 마스코트입니다.
얼굴·안경·노트북·줄무늬 꼬리와 파란 원형 배경을 유지하고, 파란 슈트·빨간 망토·M 문양을 적용했습니다.

- 생성 방식: 내장 `image_gen` 도구, 첨부 이미지 편집
- 원본 생성 파일: `mightyclaude.png`
- 날짜: 2026-09-16

## 결과와 적용

- `mightyclaude.png`: 생성 도구가 반환한 1254×1254 RGBA 원본. 네 모서리가 완전히 투명함을 확인했다.
- `MightyClaude.icns`: macOS용 16–1024px 표현 10개.
- `MightyClaude.ico`: Windows용 16·24·32·48·64·128·256px 표현 7개.
- `bash scripts/package-icons.sh`: Swift/CoreGraphics와 iconutil을 사용한 크기·포맷 변환. 원본 그림은 변경하지 않는다.
- macOS 기본 앱 아이콘과 사이드바, Windows EXE·창 아이콘과 브랜드 이미지에 연결했다.
- macOS 릴리스 빌드·코드 서명과 실제 화면 표시를 확인했다. Windows 타깃 C# 소스 컴파일은 통과했고 Windows 실제 화면은 이 Mac에서 검증하지 않았다.

## 최종 생성 프롬프트

```text
Use case: precise-object-edit / logo-brand.
Asset type: final square desktop app icon for MightyClaude, 1024x1024 PNG.
Input image 1 is the edit target. Preserve this specific friendly gray raccoon character: big round black glasses, rounded ears, raccoon eye mask, cheerful open-mouth smile, large fluffy striped tail on the right, paws typing on a laptop in the lower left, bold dark cartoon outlines, clean polished flat/vector-like illustration. Preserve the recognizable proportions, centered composition and blue circular technology badge.
Primary change: transform ONLY the raccoon's green hoodie into a Superman-like superhero costume: vivid royal blue fitted superhero suit, bright red flowing cape clearly visible behind the shoulders and curling beside the tail, a simple gold belt where visible, and a bold red-and-gold chest shield with the single capital letter "M" clearly readable for MightyClaude. Make the heroic outfit immediately recognizable while keeping the raccoon adorable and still typing on its laptop. Let the chest shield be visible above the paws/laptop.
Icon refinement: retain the blue circular background with very subtle simplified circuit traces. Simplify tiny laptop code marks to a few colorful bars so the icon stays readable when small. Keep generous safe padding around the complete circular badge, cape, ears, laptop and tail, no cropping. The area outside the blue circular badge must be genuinely transparent, not black, not white, not a checkerboard drawing. Crisp anti-aliased edges, strong silhouette, polished professional mascot app icon. No additional characters, no extra limbs, no title text, no watermark. Only lettering is "M" on the chest. Preserve the original face, spectacles, friendly expression, laptop and tail identity. Output a single finished icon, no mockup or icon grid.
```
