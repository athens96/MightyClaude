// node --test scripts/tests/check-locales.test.mjs
// 픽스처 트리와 임시 git 저장소로 check-locales.js를 검사한다.
// 실제 locales나 소스를 건드리지 않는다.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const CHECKER = new URL('../check-locales.js', import.meta.url).pathname;
const NODE = process.execPath;

function run(args) {
  return spawnSync(NODE, [CHECKER, ...args], { encoding: 'utf8' });
}

function tmpDir() {
  return fs.mkdtempSync(path.join(os.tmpdir(), 'locale-ck-'));
}

// WinUI csproj에 필요한 Content 연결이 포함된 최소 프로젝트 파일
const CSPROJ = `<Project>
  <ItemGroup>
    <Content Include="../../../locales/ko.json" />
    <Content Include="../../../locales/en.json" />
  </ItemGroup>
</Project>`;

/**
 * 픽스처 디렉터리를 만든다.
 * koJson / enJson 기본값은 빈 객체 — 키가 없으면 검사 1·2·4가 자동 통과.
 * 검사 3(사본 일치)이 통과하려면 macOS core·phone 사본도 원본과 같아야 한다.
 */
function setup(dir, { koJson = {}, enJson = {}, files = {} } = {}) {
  const koContent = JSON.stringify(koJson);
  const enContent = JSON.stringify(enJson);

  // 원본
  fs.mkdirSync(path.join(dir, 'locales'), { recursive: true });
  fs.mkdirSync(path.join(dir, 'docs'), { recursive: true });
  fs.writeFileSync(path.join(dir, 'locales', 'ko.json'), koContent);
  fs.writeFileSync(path.join(dir, 'locales', 'en.json'), enContent);

  // macOS core 사본 (검사 3)
  const macLocDir = path.join(dir, 'native/macos/Sources/MightyCore/Resources/Locales');
  fs.mkdirSync(macLocDir, { recursive: true });
  fs.writeFileSync(path.join(macLocDir, 'ko.json'), koContent);
  fs.writeFileSync(path.join(macLocDir, 'en.json'), enContent);

  // phone 사본 (검사 3)
  const phoneLocDir = path.join(dir, 'mobile/src/locales');
  fs.mkdirSync(phoneLocDir, { recursive: true });
  fs.writeFileSync(path.join(phoneLocDir, 'ko.json'), koContent);
  fs.writeFileSync(path.join(phoneLocDir, 'en.json'), enContent);

  for (const [rel, content] of Object.entries(files)) {
    const abs = path.join(dir, rel);
    fs.mkdirSync(path.dirname(abs), { recursive: true });
    fs.writeFileSync(abs, content);
  }
}

/** WinUI csproj가 필요한 픽스처 (검사 3 통과용) */
function setupWindows(dir, extraFiles = {}) {
  setup(dir, {
    files: {
      'native/windows/MightyClaude.WinUI/MightyClaude.WinUI.csproj': CSPROJ,
      ...extraFiles,
    },
  });
}

/** 임시 git 저장소를 초기화하고 git 실행 함수를 반환한다 */
function gitInit(dir) {
  const g = (...args) =>
    execFileSync('git', ['-C', dir, ...args], { encoding: 'utf8' }).trim();
  g('init');
  g('config', 'user.email', 'test@example.com');
  g('config', 'user.name', 'Test');
  return g;
}

/** 모든 변경을 커밋하고 HEAD SHA를 반환한다 */
function gitCommit(g, message) {
  g('add', '.');
  g('commit', '-m', message);
  return g('rev-parse', 'HEAD');
}

// ── 검사 1: L("key")로 부른 없는 키가 실패한다 ─────────────────────────────────
test('L("key")로 부른 없는 키가 실패한다', () => {
  const dir = tmpDir();
  try {
    setup(dir, {
      files: {
        'native/macos/Sources/MightyClaude/View.swift': 'L("phone.missing.key")\n',
        'native/windows/MightyClaude.WinUI/MightyClaude.WinUI.csproj': CSPROJ,
      },
    });
    const r = run(['--root', dir]);
    assert.notEqual(r.status, 0, 'L()로 부른 없는 키는 실패해야 한다');
    assert.match(r.stderr + r.stdout, /phone\.missing\.key/);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

// ── 검사 2: Locale.Get("key")로 부른 없는 키가 실패한다 ───────────────────────
test('Locale.Get("key")로 부른 없는 키가 실패한다', () => {
  const dir = tmpDir();
  try {
    setup(dir, {
      files: {
        'native/windows/MightyClaude.Core/MyClass.cs':
          'Locale.Get("win.missing.key");\n',
        'native/windows/MightyClaude.WinUI/MightyClaude.WinUI.csproj': CSPROJ,
      },
    });
    const r = run(['--root', dir]);
    assert.notEqual(r.status, 0, 'Locale.Get()로 부른 없는 키는 실패해야 한다');
    assert.match(r.stderr + r.stdout, /win\.missing\.key/);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

// ── 검사 3: t("key")로 부른 없는 키가 실패한다 ────────────────────────────────
test('t("key")로 부른 없는 키가 실패한다', () => {
  const dir = tmpDir();
  try {
    setup(dir, {
      files: {
        'mobile/src/App.tsx': 't("phone.missing.key")\n',
        'native/windows/MightyClaude.WinUI/MightyClaude.WinUI.csproj': CSPROJ,
      },
    });
    const r = run(['--root', dir]);
    assert.notEqual(r.status, 0, 't()로 부른 없는 키는 실패해야 한다');
    assert.match(r.stderr + r.stdout, /phone\.missing\.key/);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

// ── 검사 4: 접촉된 Windows 파일에 한국어 리터럴이 있으면 실패한다 ───────────────
test('접촉된 Windows 파일에 한국어 리터럴이 있으면 실패한다', () => {
  const dir = tmpDir();
  try {
    setupWindows(dir, {
      'native/windows/MightyClaude.Core/MyClass.cs': '// clean\n',
    });
    const g = gitInit(dir);
    const sha = gitCommit(g, 'baseline');

    // 베이스라인 이후 한국어 추가 (미커밋, unstaged)
    fs.writeFileSync(
      path.join(dir, 'native/windows/MightyClaude.Core/MyClass.cs'),
      'string msg = "안녕하세요";\n',
    );

    const r = run(['--root', dir, '--touched-since', sha]);
    assert.notEqual(r.status, 0, '한국어가 있는 접촉 파일은 실패해야 한다');
    assert.match(r.stderr + r.stdout, /MyClass\.cs/);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

// ── 검사 5: 접촉되지 않은 Windows 파일에 한국어가 있어도 통과한다 ────────────────
test('접촉되지 않은 Windows 파일에 한국어가 있어도 통과한다', () => {
  const dir = tmpDir();
  try {
    // 베이스라인부터 한국어가 있는 파일
    setupWindows(dir, {
      'native/windows/MightyClaude.Core/MyClass.cs': 'string msg = "안녕하세요";\n',
    });
    const g = gitInit(dir);
    const sha = gitCommit(g, 'baseline');

    // 베이스라인 이후 그 파일을 건드리지 않는다
    const r = run(['--root', dir, '--touched-since', sha]);
    assert.equal(r.status, 0, `미접촉 파일은 통과해야 한다\nstderr: ${r.stderr}`);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

// ── 검사 6: 접촉된 MainWindow.Smoke.cs에 한국어가 있어도 통과한다 ─────────────
test('접촉된 MainWindow.Smoke.cs에 한국어가 있어도 통과한다', () => {
  const dir = tmpDir();
  try {
    setupWindows(dir, {
      'native/windows/MightyClaude.WinUI/MainWindow.Smoke.cs': '// smoke\n',
    });
    const g = gitInit(dir);
    const sha = gitCommit(g, 'baseline');

    // 베이스라인 이후 Smoke.cs에 한국어 추가 (미커밋, unstaged)
    fs.writeFileSync(
      path.join(dir, 'native/windows/MightyClaude.WinUI/MainWindow.Smoke.cs'),
      'string msg = "안녕하세요";\n',
    );

    const r = run(['--root', dir, '--touched-since', sha]);
    assert.equal(r.status, 0, `Smoke.cs는 면제이므로 통과해야 한다\nstderr: ${r.stderr}`);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

// ── 검사 7: CRLF로 받은 docs/i18n.md도 --check를 통과한다 ─────────────────────
// Windows 러너는 autocrlf로 저장소를 받는다. 보고서는 늘 LF로 만들므로 줄끝만
// 달라진 사본을 어긋난 것으로 보면 CI가 이 한 가지로만 붉어진다.
test('CRLF로 받은 docs/i18n.md도 --check를 통과한다', () => {
  const dir = tmpDir();
  try {
    setupWindows(dir);
    const written = run(['--root', dir]);
    assert.equal(written.status, 0, `보고서를 먼저 써야 한다\nstderr: ${written.stderr}`);

    const doc = path.join(dir, 'docs', 'i18n.md');
    const lf = fs.readFileSync(doc, 'utf8');
    assert.ok(!lf.includes('\r\n'), '생성한 보고서는 LF여야 한다');
    fs.writeFileSync(doc, lf.replace(/\n/g, '\r\n'));

    const r = run(['--root', dir, '--check']);
    assert.equal(r.status, 0, `CRLF 사본은 통과해야 한다\nstdout: ${r.stdout}\nstderr: ${r.stderr}`);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});
