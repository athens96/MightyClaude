# Mighty Raccoon

Generated with the built-in image generation tool through the `hatch-pet` and `imagegen` skills. Reference: `assets/icons/mightyclaude.png`.

Authoritative visual prompt: preserve the raccoon's face, round glasses, striped tail, blue superhero suit and red cape from the app icon. Show a compact, whole-body standing superhero with red boots, without a computer, laptop, props or chest lettering. Use the icon's clean outlined cartoon style, readable at desktop pet size. The approved canonical reference grounds every state strip. Flat green chroma is removed by the hatch-pet extraction pipeline.

States: idle breathing/blinking; rightward gait; leftward gait; wave; jump; failed/deflated reaction; expectant waiting; focused work; focused review. Every final state was separately generated. An initial mirrored leftward draft was replaced after visual QA found a fragment at a slot boundary. No detached effects, ground shadows, labels or visible layout guides.

Extraction uses the skill's stable-slots method to preserve a shared scale and vertical jump offsets, with chroma distance threshold 205 to remove the visible green fringe. The lower thresholds 96 and 180 were rejected in visual QA. No local drawing or pose synthesis was used.

Format: Codex v1, 8 columns × 9 rows, cells 192×208, full atlas 1536×1872. State frame counts: 6, 8, 8, 4, 5, 8, 6, 6, 6. Unused cells remain transparent. QA artifacts are under `artifacts/pet-run/qa` and validation under `artifacts/pet-run/final`.
