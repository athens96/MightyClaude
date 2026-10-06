// node --test scripts/tests/help-build.test.mjs
// Checks scripts/help/build.mjs against the real help content (--check) and against
// throwaway fixture trees. It never writes into the repository.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const BUILDER = new URL('../help/build.mjs', import.meta.url).pathname;
const NODE = process.execPath;

function run(args) {
  return spawnSync(NODE, [BUILDER, ...args], { encoding: 'utf8' });
}

function tmpDir() {
  return fs.mkdtempSync(path.join(os.tmpdir(), 'help-build-'));
}

const SHOTS = {
  version: 1,
  sections: ['getting-started', 'layout'],
  shots: [
    { id: 'welcome', section: 'getting-started', screen: 'welcome', window: { width: 1360, height: 860 } },
    { id: 'pet', section: 'layout', screen: 'companion-pet', themes: ['dark'] },
  ],
};
const LOCALE = { ko: { 'menu.openProject': '프로젝트 폴더 열기…', 'menu.settings': '설정…', 'pane.count': '{count}개' },
  en: { 'menu.openProject': 'Open Project Folder…', 'menu.settings': 'Settings…', 'pane.count': '{count} panes' } };
const SITE = { title: 'T', contents: 'C', placeholder: 'soon', note: 'Note', warning: 'Warning', tip: 'Tip' };

const PAGE_KO = {
  'getting-started.md': `---
title: 시작하기
order: 1
section: getting-started
---
첫 화면입니다.

## 폴더 열기 {#open}
1. {{ui:menu.openProject}}를 누르거나 {{kbd:⌘O}}를 누릅니다.
2. 폴더를 고릅니다.

![[welcome]]

> [!note]
> [화면 구성](layout.md#tabs)을 보세요.
`,
  'layout.md': `---
title: 화면 구성
order: 2
section: layout
---
## 탭 {#tabs}
| 키 | 동작 |
|---|---|
| {{kbd:⌘,}} | {{ui:menu.settings}} |

![[pet]]
`,
};
const PAGE_EN = {
  'getting-started.md': `---
title: Getting started
order: 1
section: getting-started
---
The first window.

## Open a folder {#open}
1. Click {{ui:menu.openProject}} or press {{kbd:⌘O}}.
2. Pick a folder.

![[welcome]]

> [!note]
> See [the layout](layout.md#tabs).
`,
  'layout.md': `---
title: Layout
order: 2
section: layout
---
## Tabs {#tabs}
| Keys | Action |
|---|---|
| {{kbd:⌘,}} | {{ui:menu.settings}} |

![[pet]]
`,
};

/** A fixture repository root: docs/help (shots.json, content/ko, content/en) and locales. */
function setup(dir, { ko = PAGE_KO, en = PAGE_EN } = {}) {
  const write = (rel, content) => {
    fs.mkdirSync(path.dirname(path.join(dir, rel)), { recursive: true });
    fs.writeFileSync(path.join(dir, rel), typeof content === 'string' ? content : JSON.stringify(content));
  };
  write('docs/help/shots.json', SHOTS);
  write('locales/ko.json', LOCALE.ko);
  write('locales/en.json', LOCALE.en);
  write('VERSION', '9.9.9\n');
  for (const [lang, pages] of [['ko', ko], ['en', en]]) {
    if (!pages) continue;
    write(`docs/help/content/${lang}/_site.json`, SITE);
    for (const [name, text] of Object.entries(pages)) write(`docs/help/content/${lang}/${name}`, text);
  }
}

function withFixture(options, body) {
  const dir = tmpDir();
  try {
    setup(dir, options);
    body(dir);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
}

test('the real help content passes --check', () => {
  const r = run(['--check']);
  assert.equal(r.status, 0, r.stderr + r.stdout);
  assert.match(r.stdout, /help: ok/);
});

test('a consistent fixture passes --check and writes nothing', () => {
  withFixture({}, (dir) => {
    const r = run(['--root', dir, '--check']);
    assert.equal(r.status, 0, r.stderr + r.stdout);
    assert.equal(fs.existsSync(path.join(dir, 'docs/help/site')), false);
  });
});

test('an unknown shot id fails', () => {
  withFixture({ ko: { ...PAGE_KO, 'layout.md': PAGE_KO['layout.md'].replace('![[pet]]', '![[nope]]') } }, (dir) => {
    const r = run(['--root', dir, '--check']);
    assert.notEqual(r.status, 0);
    assert.match(r.stderr, /unknown shot id "nope"/);
  });
});

test('an unknown ui key fails', () => {
  withFixture({ ko: { ...PAGE_KO, 'layout.md': PAGE_KO['layout.md'].replace('menu.settings', 'menu.missing') } }, (dir) => {
    const r = run(['--root', dir, '--check']);
    assert.notEqual(r.status, 0);
    assert.match(r.stderr, /unknown ui key "menu\.missing"/);
  });
});

test('a ui key with a placeholder fails', () => {
  withFixture({ ko: { ...PAGE_KO, 'layout.md': PAGE_KO['layout.md'].replace('menu.settings', 'pane.count') } }, (dir) => {
    const r = run(['--root', dir, '--check']);
    assert.notEqual(r.status, 0);
    assert.match(r.stderr, /pane\.count.*placeholder/);
  });
});

test('a language missing a file fails', () => {
  withFixture({ en: { 'getting-started.md': PAGE_EN['getting-started.md'] } }, (dir) => {
    const r = run(['--root', dir, '--check']);
    assert.notEqual(r.status, 0);
    assert.match(r.stderr, /files differ from ko: missing \[layout\.md\]/);
  });
});

test('a heading structure that differs from ko fails', () => {
  const changed = PAGE_EN['layout.md'].replace('## Tabs {#tabs}', '## Tabs {#tabs}\n### Extra {#extra}');
  withFixture({ en: { ...PAGE_EN, 'layout.md': changed } }, (dir) => {
    const r = run(['--root', dir, '--check']);
    assert.notEqual(r.status, 0);
    assert.match(r.stderr, /heading structure differs from ko/);
  });
});

test('different ui references in a translation fail', () => {
  const changed = PAGE_EN['layout.md'].replace('{{ui:menu.settings}}', 'Settings');
  withFixture({ en: { ...PAGE_EN, 'layout.md': changed } }, (dir) => {
    const r = run(['--root', dir, '--check']);
    assert.notEqual(r.status, 0);
    assert.match(r.stderr, /ui keys differ from ko: missing \[menu\.settings\]/);
  });
});

test('a heading without an id fails', () => {
  withFixture({ ko: { ...PAGE_KO, 'layout.md': PAGE_KO['layout.md'].replace(' {#tabs}', '') } }, (dir) => {
    const r = run(['--root', dir, '--check']);
    assert.notEqual(r.status, 0);
    assert.match(r.stderr, /heading needs an id/);
  });
});

test('broken internal links fail', () => {
  const missingHeading = PAGE_KO['getting-started.md'].replace('layout.md#tabs', 'layout.md#nowhere');
  withFixture({ ko: { ...PAGE_KO, 'getting-started.md': missingHeading } }, (dir) => {
    const r = run(['--root', dir, '--check']);
    assert.notEqual(r.status, 0);
    assert.match(r.stderr, /broken link "layout\.md#nowhere"/);
  });
  const missingPage = PAGE_KO['getting-started.md'].replace('layout.md#tabs', 'faq.md');
  withFixture({ ko: { ...PAGE_KO, 'getting-started.md': missingPage } }, (dir) => {
    const r = run(['--root', dir, '--check']);
    assert.notEqual(r.status, 0);
    assert.match(r.stderr, /broken link "faq\.md"/);
  });
});

test('a full build without shots writes pages, placeholders and the language redirect', () => {
  withFixture({}, (dir) => {
    const r = run(['--root', dir]);
    assert.equal(r.status, 0, r.stderr + r.stdout);
    const ko = fs.readFileSync(path.join(dir, 'docs/help/site/ko/index.html'), 'utf8');
    assert.match(ko, /<span class="ui" title="menu.openProject">프로젝트 폴더 열기…<\/span>/);
    assert.match(ko, /<kbd>⌘<\/kbd><kbd>O<\/kbd>/);
    assert.match(ko, /class="ph t-light"[^>]*><b>welcome<\/b>/);
    assert.match(ko, /href="#layout-tabs"/);
    assert.match(ko, /id="getting-started-open"/);
    // zh and ja have no content yet: listed, greyed out
    assert.match(ko, /<span lang="zh-Hans" aria-disabled="true"[^>]*>简体中文<\/span>/);
    assert.match(ko, /<a lang="en" hreflang="en" href="..\/en\/index.html">English<\/a>/);
    const en = fs.readFileSync(path.join(dir, 'docs/help/site/en/index.html'), 'utf8');
    assert.match(en, />Open Project Folder…</);
    const root = fs.readFileSync(path.join(dir, 'docs/help/site/index.html'), 'utf8');
    assert.match(root, /var built=\["ko","en"\],pick="en"/);
  });
});

// A 1×1 PNG: the builder encodes it (cwebp) or copies it (no cwebp).
const PNG = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==', 'base64');

test('--shots uses each language picture and falls back to ko, then a placeholder', () => {
  withFixture({}, (dir) => {
    const shots = path.join(dir, 'artifact');
    // The CI artifact layout: mighty-help-<lang>/help-shots/<lang>/<id>-<theme>.png
    for (const [lang, file] of [['ko', 'welcome-light.png'], ['ko', 'welcome-dark.png'], ['en', 'welcome-dark.png']]) {
      const folder = path.join(shots, `mighty-help-${lang}`, 'help-shots', lang);
      fs.mkdirSync(folder, { recursive: true });
      fs.writeFileSync(path.join(folder, file), PNG);
    }
    const r = run(['--root', dir, '--shots', shots]);
    assert.equal(r.status, 0, r.stderr + r.stdout);
    const site = path.join(dir, 'docs/help/site');
    const ext = fs.existsSync(path.join(site, 'ko/img/welcome-light.webp')) ? 'webp' : 'png';
    assert.ok(fs.existsSync(path.join(site, `ko/img/welcome-light.${ext}`)));
    assert.ok(fs.existsSync(path.join(site, `en/img/welcome-dark.${ext}`)));
    const en = fs.readFileSync(path.join(site, 'en/index.html'), 'utf8');
    assert.match(en, new RegExp(`class="t-dark"[^>]*src="img/welcome-dark\\.${ext}"`));
    assert.match(en, new RegExp(`class="t-light"[^>]*src="\\.\\./ko/img/welcome-light\\.${ext}"`));
    // "pet" has no picture anywhere: placeholder in both themes
    assert.match(en, /class="ph t-dark"[^>]*><b>pet<\/b>/);
  });
});

test('a Korean particle that does not fit the app label fails', () => {
  // 설정… ends in 정 (final ㅇ): 을 fits, 를 does not.
  const wrong = PAGE_KO['layout.md'].replace('| {{ui:menu.settings}} |', '| {{ui:menu.settings}}를 엽니다 |');
  withFixture({ ko: { ...PAGE_KO, 'layout.md': wrong } }, (dir) => {
    const r = run(['--root', dir, '--check']);
    assert.notEqual(r.status, 0);
    assert.match(r.stderr, /particle "를" after "설정" .* should be "을…"/);
  });
  const right = PAGE_KO['layout.md'].replace('| {{ui:menu.settings}} |', '| {{ui:menu.settings}}을 엽니다 |');
  withFixture({ ko: { ...PAGE_KO, 'layout.md': right } }, (dir) => {
    const r = run(['--root', dir, '--check']);
    assert.equal(r.status, 0, r.stderr);
  });
});
