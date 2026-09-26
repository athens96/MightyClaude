#!/usr/bin/env python3
"""
Phone asset generator for MightyClaude mobile app.
Generates all required assets from assets/icons/mightyclaude.png.

Usage (from repo root):
    python3 scripts/generate-phone-assets.py

Requires: Pillow (pip install Pillow)
"""

import hashlib
import json
import sys
from pathlib import Path

try:
    from PIL import Image
except ImportError:
    print("ERROR: Pillow is required. Run: pip install Pillow", file=sys.stderr)
    sys.exit(1)

REPO_ROOT = Path(__file__).parent.parent
SOURCE = REPO_ROOT / "assets" / "icons" / "mightyclaude.png"
OUT_DIR = REPO_ROOT / "mobile" / "assets" / "images"
SOURCES_JSON = OUT_DIR / "asset-sources.json"

BG_COLOR = (13, 13, 15)  # #0d0d0f


def sha256_of(path: Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(65536), b""):
            h.update(chunk)
    return h.hexdigest()


def centered_on_transparent(src: Image.Image, canvas: int, draw_size: int) -> Image.Image:
    """Place src scaled to draw_size, centred on a transparent canvas of size canvas×canvas."""
    scale = draw_size / max(src.width, src.height)
    new_w = round(src.width * scale)
    new_h = round(src.height * scale)
    resized = src.resize((new_w, new_h), Image.LANCZOS)
    bg = Image.new("RGBA", (canvas, canvas), (0, 0, 0, 0))
    x = (canvas - new_w) // 2
    y = (canvas - new_h) // 2
    bg.paste(resized, (x, y), resized)
    return bg


def make_ios_icon(src: Image.Image) -> dict:
    """1024×1024 RGB (no alpha) with #0d0d0f background."""
    bg = Image.new("RGB", (1024, 1024), BG_COLOR)
    fg = centered_on_transparent(src, 1024, 1024)
    bg.paste(fg, (0, 0), fg)
    bg.save(OUT_DIR / "icon.png", "PNG")
    print("  icon.png: 1024×1024 RGB (no alpha)")
    return {"size": [1024, 1024], "mode": "RGB", "note": "iOS icon, no alpha, #0d0d0f background"}


def make_android_foreground(src: Image.Image) -> dict:
    """512×512 RGBA; raccoon inside the 66 % safe zone."""
    safe = round(512 * 0.66)  # 338 px
    img = centered_on_transparent(src, 512, safe)
    img.save(OUT_DIR / "android-icon-foreground.png", "PNG")
    print(f"  android-icon-foreground.png: 512×512 RGBA (raccoon in {safe}px safe zone)")
    return {"size": [512, 512], "mode": "RGBA", "note": "Android adaptive foreground, raccoon in 66% safe zone"}


def make_android_monochrome(src: Image.Image) -> dict:
    """512×512 RGBA white silhouette (monochrome glyph)."""
    # Extract alpha channel from source; fill RGB with white
    _, _, _, alpha = src.split()
    white_glyph = Image.new("RGBA", src.size, (255, 255, 255, 0))
    white_glyph.putalpha(alpha)
    safe = round(512 * 0.66)
    img = centered_on_transparent(white_glyph, 512, safe)
    img.save(OUT_DIR / "android-icon-monochrome.png", "PNG")
    print("  android-icon-monochrome.png: 512×512 RGBA white silhouette")
    return {"size": [512, 512], "mode": "RGBA", "note": "Android monochrome glyph, white silhouette"}


def make_splash(src: Image.Image) -> dict:
    """400×400 RGBA raccoon centred on transparent (expo-splash-screen composites the bg)."""
    img = centered_on_transparent(src, 400, 400)
    img.save(OUT_DIR / "splash-icon.png", "PNG")
    print("  splash-icon.png: 400×400 RGBA (expo-splash-screen adds #0d0d0f bg)")
    return {"size": [400, 400], "mode": "RGBA", "note": "Splash image, expo-splash-screen adds #0d0d0f background"}


def make_favicon(src: Image.Image) -> dict:
    """48×48 RGBA web favicon."""
    img = centered_on_transparent(src, 48, 48)
    img.save(OUT_DIR / "favicon.png", "PNG")
    print("  favicon.png: 48×48 RGBA")
    return {"size": [48, 48], "mode": "RGBA", "note": "Web favicon"}


def main() -> None:
    if not SOURCE.exists():
        print(f"ERROR: source not found: {SOURCE}", file=sys.stderr)
        sys.exit(1)

    OUT_DIR.mkdir(parents=True, exist_ok=True)

    print(f"Source: {SOURCE.relative_to(REPO_ROOT)}")
    source_hash = sha256_of(SOURCE)
    print(f"  sha256: {source_hash[:16]}…")

    src = Image.open(SOURCE).convert("RGBA")
    print(f"  size: {src.size}, mode: {src.mode}")

    print("\nGenerating assets:")
    outputs = {
        "icon.png": make_ios_icon(src),
        "android-icon-foreground.png": make_android_foreground(src),
        "android-icon-monochrome.png": make_android_monochrome(src),
        "splash-icon.png": make_splash(src),
        "favicon.png": make_favicon(src),
    }

    # Record what was written so a test can tell a regenerated file from a
    # hand-edited or stale one.
    for name, info in outputs.items():
        info["sha256"] = sha256_of(OUT_DIR / name)

    manifest = {
        "source": "assets/icons/mightyclaude.png",
        "source_sha256": source_hash,
        "outputs": outputs,
    }
    with open(SOURCES_JSON, "w") as f:
        json.dump(manifest, f, indent=2)
        f.write("\n")
    print(f"\n  asset-sources.json written")
    print("\nDone. All phone assets generated from raccoon artwork.")


if __name__ == "__main__":
    main()
