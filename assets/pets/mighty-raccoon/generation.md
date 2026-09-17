# Mighty Raccoon

Generated with the built-in image generation tool through the `hatch-pet` and `imagegen` skills. Reference: `assets/icons/mightyclaude.png`.

Authoritative visual prompt: preserve the raccoon's face, round glasses, striped tail, blue superhero suit and red cape from the app icon. Show a compact, whole-body standing superhero with red boots and the icon's red shield with a yellow M on the chest, without a computer, laptop or extra props. Use the icon's clean outlined cartoon style, readable at desktop pet size. The approved canonical reference grounds every state strip. Flat green chroma is removed by the original hatch-pet extraction pipeline.

States: idle breathing/blinking; rightward gait; leftward gait; wave; jump; failed/deflated reaction; expectant waiting; focused work; focused review. Every final state was separately generated. An initial mirrored leftward draft was replaced after visual QA found a fragment at a slot boundary. No detached effects, ground shadows, labels or visible layout guides.

Extraction uses the skill's stable-slots method to preserve a shared scale and vertical jump offsets, with chroma distance threshold 205 to remove the visible green fringe. The lower thresholds 96 and 180 were rejected in visual QA. No local drawing or pose synthesis was used.

Format: Codex v1, 8 columns × 9 rows, cells 192×208, full atlas 1536×1872. State frame counts: 6, 8, 8, 4, 5, 8, 6, 6, 6. Unused cells remain transparent. QA artifacts are under `artifacts/pet-run/qa` and validation under `artifacts/pet-run/final`.

## Chest emblem repair (2026-09-17)

The original generation prompt excluded chest lettering. The repair uses the built-in image generation tool to add the app icon's yellow M / red shield to every exposed chest, preserving the raccoon, poses, cell ordering and transparent background. The crouched jump-preparation frame is repaired separately so its small chest does not lose the emblem.

Repair prompt: add only the icon-matching M shield on the visible blue chest, following the torso's perspective and existing arm occlusion; preserve face, glasses, cape, tail, pose, framing and transparent unused cells. For the crouched frame, place the shield between the arms above the yellow belt buckle.

The hatch-pet atlas composer normalizes the generated atlas's matching aspect ratio to the required dimensions, clears unused cells and fully transparent RGB residue, and encodes lossless WebP. Frame extraction/assembly changes no animation timing. QA assets and validation are under `artifacts/pet-emblem`. GIF previews are composited onto dark and white backgrounds before palette conversion to avoid GIF artifacts from partially transparent edge pixels; the actual WebP retains transparency.
