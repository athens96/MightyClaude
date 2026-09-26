import * as crypto from 'crypto';
import * as fs from 'fs';
import * as path from 'path';

// PNG color type constants
const PNG_COLOR_RGB = 2; // no alpha
const PNG_COLOR_RGBA = 6; // has alpha

interface PngInfo {
  width: number;
  height: number;
  colorType: number;
}

function readPngInfo(filePath: string): PngInfo {
  const buf = Buffer.alloc(26);
  const fd = fs.openSync(filePath, 'r');
  fs.readSync(fd, buf, 0, 26, 0);
  fs.closeSync(fd);
  // PNG layout: 8-byte signature, then IHDR chunk:
  //   4 bytes length, 4 bytes "IHDR", 4 bytes width, 4 bytes height,
  //   1 byte bit depth, 1 byte color type
  return {
    width: buf.readUInt32BE(16),
    height: buf.readUInt32BE(20),
    colorType: buf[25] as number,
  };
}

const REPO_ROOT = path.resolve(__dirname, '../../../');
const ASSETS_DIR = path.join(REPO_ROOT, 'mobile', 'assets', 'images');
const SOURCE_PNG = path.join(REPO_ROOT, 'assets', 'icons', 'mightyclaude.png');

describe('phone assets generated from raccoon artwork', () => {
  it('asset-sources.json records the raccoon artwork as source', () => {
    const manifest = JSON.parse(
      fs.readFileSync(path.join(ASSETS_DIR, 'asset-sources.json'), 'utf8'),
    );
    expect(manifest.source).toBe('assets/icons/mightyclaude.png');
    expect(typeof manifest.source_sha256).toBe('string');
    expect(manifest.source_sha256).toHaveLength(64);
  });

  it('source sha256 in asset-sources.json matches actual mightyclaude.png', () => {
    const manifest = JSON.parse(
      fs.readFileSync(path.join(ASSETS_DIR, 'asset-sources.json'), 'utf8'),
    );
    const actual = crypto.createHash('sha256').update(fs.readFileSync(SOURCE_PNG)).digest('hex');
    expect(actual).toBe(manifest.source_sha256);
  });

  it('iOS icon.png is 1024×1024 with no alpha channel', () => {
    const info = readPngInfo(path.join(ASSETS_DIR, 'icon.png'));
    expect(info.width).toBe(1024);
    expect(info.height).toBe(1024);
    expect(info.colorType).toBe(PNG_COLOR_RGB);
  });

  it('android-icon-foreground.png is 512×512 RGBA', () => {
    const info = readPngInfo(path.join(ASSETS_DIR, 'android-icon-foreground.png'));
    expect(info.width).toBe(512);
    expect(info.height).toBe(512);
    expect(info.colorType).toBe(PNG_COLOR_RGBA);
  });

  it('android-icon-monochrome.png is 512×512 RGBA', () => {
    const info = readPngInfo(path.join(ASSETS_DIR, 'android-icon-monochrome.png'));
    expect(info.width).toBe(512);
    expect(info.height).toBe(512);
    expect(info.colorType).toBe(PNG_COLOR_RGBA);
  });

  it('splash-icon.png exists', () => {
    expect(fs.existsSync(path.join(ASSETS_DIR, 'splash-icon.png'))).toBe(true);
  });

  it('favicon.png is 48×48', () => {
    const info = readPngInfo(path.join(ASSETS_DIR, 'favicon.png'));
    expect(info.width).toBe(48);
    expect(info.height).toBe(48);
  });

  it('every output on disk is the file the generator wrote', () => {
    const manifest = JSON.parse(
      fs.readFileSync(path.join(ASSETS_DIR, 'asset-sources.json'), 'utf8'),
    );
    const names = Object.keys(manifest.outputs).sort();
    expect(names).toEqual(
      ['android-icon-foreground.png', 'android-icon-monochrome.png', 'favicon.png', 'icon.png', 'splash-icon.png'],
    );
    for (const name of names) {
      const actual = crypto.createHash('sha256').update(fs.readFileSync(path.join(ASSETS_DIR, name))).digest('hex');
      expect({ name, sha256: actual }).toEqual({ name, sha256: manifest.outputs[name].sha256 });
    }
  });

  it('no stock Expo template image is left in the assets', () => {
    // The images the Expo template shipped with, before the raccoon replaced them.
    const stock = new Set([
      '7a667804bb80a6a424a5daf18a2599c4f32237cf06fe78fc0de45dbb09e0eccf', // icon.png
      'fb139c2dee362ebf2070e23b96da6fc0d43f8492de38b8af1fd7223e19b5861d', // android-icon-background.png
      '9e3d0315a33c6799de601dd34cd8bf8cc3a8d16f3bf75592baec2ceb7240b391', // android-icon-foreground.png
      '6371fc2c12e33ad2215a86c281db3d682a81bebe7c957a842c13b8bf00cceb83', // android-icon-monochrome.png
      'a4e030697a7571b3e95d31860e4da55d2f98e5e861e2b55e414f45a8556828ba', // favicon.png
      '27b060a757a29038c9618586baa0ba3894dbe60d084c6059df497ff429c2d92f', // splash-icon.png
    ]);
    const left = fs
      .readdirSync(ASSETS_DIR)
      .filter((name) => name.endsWith('.png'))
      .filter((name) => stock.has(crypto.createHash('sha256').update(fs.readFileSync(path.join(ASSETS_DIR, name))).digest('hex')));
    expect(left).toEqual([]);
  });

  it('app.json points only at generated images on the dark background', () => {
    const expo = JSON.parse(fs.readFileSync(path.join(REPO_ROOT, 'mobile', 'app.json'), 'utf8')).expo;
    const splash = expo.plugins.find((p: unknown) => Array.isArray(p) && p[0] === 'expo-splash-screen')[1];
    const referenced: string[] = [
      expo.icon,
      expo.android.adaptiveIcon.foregroundImage,
      expo.android.adaptiveIcon.monochromeImage,
      expo.web.favicon,
      splash.image,
    ].map((ref: string) => path.basename(ref));
    const manifest = JSON.parse(fs.readFileSync(path.join(ASSETS_DIR, 'asset-sources.json'), 'utf8'));
    for (const name of referenced) expect(Object.keys(manifest.outputs)).toContain(name);
    expect(expo.android.adaptiveIcon.backgroundColor.toLowerCase()).toBe('#0d0d0f');
    expect(splash.backgroundColor.toLowerCase()).toBe('#0d0d0f');
  });
});
