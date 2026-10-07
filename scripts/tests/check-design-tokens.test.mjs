// node --test scripts/tests/check-design-tokens.test.mjs
// Checks the spacing-literal ratchet of check-design-tokens.js against fixture trees.
// It never touches the real sources or budgets.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const CHECKER = new URL('../check-design-tokens.js', import.meta.url).pathname;
const NODE = process.execPath;
const SPACING_BUDGET = 'scripts/spacing-literal-budget.json';

function run(dir, ...args) {
  return spawnSync(NODE, [CHECKER, '--root', dir, ...args], { encoding: 'utf8' });
}

/** A fixture root with an empty colour budget, the given spacing budget and source files. */
function fixture({ areas = { macOS: 0, Windows: 0, phone: 0 }, exemptFiles, files = {} } = {}) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'spacing-ck-'));
  const write = (relative, content) => {
    fs.mkdirSync(path.join(dir, path.dirname(relative)), { recursive: true });
    fs.writeFileSync(path.join(dir, relative), content);
  };
  write('scripts/design-token-budget.json', JSON.stringify({ exempt: {}, files: {} }));
  write(SPACING_BUDGET, JSON.stringify({ areas, ...(exemptFiles ? { exemptFiles } : {}) }));
  for (const [relative, content] of Object.entries(files)) write(relative, content);
  return dir;
}

function areas(dir) {
  return JSON.parse(fs.readFileSync(path.join(dir, SPACING_BUDGET), 'utf8')).areas;
}

const MAC = 'native/macos/Sources/MightyClaude/View.swift';
const WIN = 'native/windows/MightyClaude.WinUI/MainWindow.View.cs';
const PHONE = 'mobile/src/components/view.tsx';

test('a count above the budget fails', () => {
  const dir = fixture({
    areas: { macOS: 1, Windows: 0, phone: 0 },
    files: { [MAC]: 'VStack(spacing: 4) { Text("a").padding(8) }\n' },
  });
  try {
    const r = run(dir);
    assert.notEqual(r.status, 0);
    assert.match(r.stderr, /macOS: 2 raw spacing literals, budget 1/);
    assert.match(r.stderr, /View\.swift/);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('a count below the budget fails without --write-budget, and --write-budget lowers it', () => {
  const dir = fixture({
    areas: { macOS: 0, Windows: 5, phone: 3 },
    files: {
      [WIN]: 'var a = new Thickness(8, 0, 4, 0); var b = new StackPanel { Spacing = 6 };\n',
      [PHONE]: 'const s = { paddingLeft: 12 };\n',
    },
  });
  try {
    const stale = run(dir);
    assert.notEqual(stale.status, 0);
    assert.match(stale.stderr, /Windows: 3 raw spacing literals, budget 5\. Lower the budget/);
    assert.match(stale.stderr, /phone: 1 raw spacing literals, budget 3/);
    assert.deepEqual(areas(dir), { macOS: 0, Windows: 5, phone: 3 }, 'a plain check never writes');

    const write = run(dir, '--write-budget');
    assert.equal(write.status, 0, write.stderr);
    assert.deepEqual(areas(dir), { macOS: 0, Windows: 3, phone: 1 });
    assert.equal(run(dir).status, 0);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('--write-budget never raises a budget', () => {
  const dir = fixture({ files: { [PHONE]: 'const s = { gap: 8, marginTop: -4 };\n' } });
  try {
    const r = run(dir, '--write-budget');
    assert.notEqual(r.status, 0);
    assert.match(r.stderr, /phone: 2 raw spacing literals, budget 0/);
    assert.deepEqual(areas(dir), { macOS: 0, Windows: 0, phone: 0 });
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('exempt regions are not counted', () => {
  const source = [
    'HStack(spacing: 6) {',
    '    // spacing-exempt-begin: halo centring',
    '    Circle().padding(.horizontal, -5)',
    '    Circle().padding(12)',
    '    // spacing-exempt-end',
    '}',
    '',
  ].join('\n');
  const dir = fixture({ areas: { macOS: 1, Windows: 0, phone: 0 }, files: { [MAC]: source } });
  try {
    const r = run(dir);
    assert.equal(r.status, 0, r.stderr);
    assert.match(r.stdout, /macOS +1 \/ budget 1 in 1 files \(exempt: 2 literals, 1 regions\)/);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('an exempt region needs a reason and an end', () => {
  const dir = fixture({
    files: {
      [MAC]: '// spacing-exempt-begin\nText("a").padding(4)\n// spacing-exempt-end\n',
      [PHONE]: '// spacing-exempt-begin: drawn shape\nconst s = { padding: 4 };\n',
    },
  });
  try {
    const r = run(dir);
    assert.notEqual(r.status, 0);
    assert.match(r.stderr, /View\.swift: \/\/ spacing-exempt-begin needs ": <reason>"/);
    assert.match(r.stderr, /view\.tsx: \/\/ spacing-exempt-begin is never closed/);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('0, 1, tokens, comments, tests and exempt files are not counted', () => {
  const dir = fixture({
    exemptFiles: { 'native/macos/Sources/MightyClaude/*Diagnostics*.swift': 'smoke fixtures' },
    files: {
      [MAC]: [
        'VStack(spacing: 0) { Text("a").padding(.top, 1).padding(DesignMetrics.Spacing.md) }',
        '// .padding(12) in a comment',
        'HStack(spacing: DesignMetrics.Spacing.sm) {}',
        '',
      ].join('\n'),
      'native/macos/Sources/MightyClaude/LayoutDiagnostics.swift': 'Text("a").padding(20)\n',
      'native/macos/Tests/MightyCoreTests/ViewTests.swift': 'Text("a").padding(20)\n',
      [WIN]: 'var a = new Thickness(0, 1, DesignMetrics.Spacing.Md, 0); const double Spacing = 18;\n',
      'mobile/src/__tests__/view.test.tsx': 'const s = { padding: 20 };\n',
    },
  });
  try {
    const r = run(dir);
    assert.equal(r.status, 0, r.stderr);
    assert.match(r.stdout, /macOS +0 \/ budget 0 in 0 files \(exempt: 1 literals, 0 regions\)/);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('an exempt-file glob that matches nothing fails', () => {
  const dir = fixture({ exemptFiles: { 'mobile/src/lib/gone.ts': 'removed long ago' } });
  try {
    const r = run(dir);
    assert.notEqual(r.status, 0);
    assert.match(r.stderr, /exemptFiles mobile\/src\/lib\/gone\.ts matches no file/);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});
