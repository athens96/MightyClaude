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
    <Content Include="../../../locales/zh.json" />
    <Content Include="../../../locales/ja.json" />
  </ItemGroup>
</Project>`;

/**
 * 픽스처 디렉터리를 만든다.
 * koJson / enJson 기본값은 빈 객체 — 키가 없으면 검사 1·2·4가 자동 통과.
 * 검사 3(사본 일치)이 통과하려면 macOS core·phone 사본도 원본과 같아야 한다.
 */
function setup(dir, { koJson = {}, enJson = {}, zhJson = {}, jaJson = {}, files = {} } = {}) {
  const contents = {
    ko: JSON.stringify(koJson),
    en: JSON.stringify(enJson),
    zh: JSON.stringify(zhJson),
    ja: JSON.stringify(jaJson),
  };

  // The originals, then the macOS core and phone copies (check 3), all four languages.
  fs.mkdirSync(path.join(dir, 'docs'), { recursive: true });
  for (const directory of [
    'locales',
    'native/macos/Sources/MightyCore/Resources/Locales',
    'mobile/src/locales',
  ]) {
    fs.mkdirSync(path.join(dir, directory), { recursive: true });
    for (const [language, content] of Object.entries(contents)) {
      fs.writeFileSync(path.join(dir, directory, `${language}.json`), content);
    }
  }

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

// ── 검사 7: 커밋 없이 staged만 있는 Korean 파일도 실패한다 (touched set 합집합 증명) ──
// git diff --name-only 는 unstaged, git diff --name-only --cached 는 staged를 잡는다.
// 이 검사는 staged(커밋 안 된 인덱스) 변경이 touched set에 포함됨을 증명한다.
test('staged 미커밋 Korean 파일이 실패한다 — touched set은 커밋과 미커밋의 합집합이다', () => {
  const dir = tmpDir();
  try {
    setupWindows(dir, {
      'native/windows/MightyClaude.Core/MyClass.cs': '// clean\n',
    });
    const g = gitInit(dir);
    const sha = gitCommit(g, 'baseline');

    // 베이스라인 이후 한국어 추가 — git add로 스테이지만 하고 커밋하지 않는다
    fs.writeFileSync(
      path.join(dir, 'native/windows/MightyClaude.Core/MyClass.cs'),
      'string msg = "안녕하세요";\n',
    );
    execFileSync('git', ['-C', dir, 'add', 'native/windows/MightyClaude.Core/MyClass.cs'], {
      encoding: 'utf8',
    });

    const r = run(['--root', dir, '--touched-since', sha]);
    assert.notEqual(r.status, 0, '스테이지된 Korean 파일은 실패해야 한다');
    assert.match(r.stderr + r.stdout, /MyClass\.cs/);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

// ── 검사 8: --root는 git 질의를 픽스처로 제한한다 ────────────────────────────────
// --root 없이 실행하면 현재 디렉터리(실제 저장소)의 git을 쓴다.
// --root <픽스처>를 쓰면 모든 git -C ROOT 호출이 픽스처에 묶인다.
// 실제 저장소 작업 트리가 더럽더라도 깨끗한 픽스처는 항상 통과한다.
test('--root는 git 질의를 픽스처로 제한한다 — 실제 저장소가 더럽더라도 픽스처가 통과한다', () => {
  const dir = tmpDir();
  try {
    // 픽스처에는 Korean 파일도 없고 없는 키도 없다
    setupWindows(dir);
    const g = gitInit(dir);
    const sha = gitCommit(g, 'baseline');

    // 실제 저장소(process.cwd())는 이 시점에 더럽거나 커밋이 있을 수 있다.
    // --root <픽스처>로 실행하면 checker는 픽스처 내부만 걷고
    // git -C <픽스처> 로 diff를 구하므로 실제 저장소 상태는 무관하다.
    const r = run(['--root', dir, '--touched-since', sha]);
    assert.equal(
      r.status,
      0,
      `깨끗한 픽스처는 실제 저장소 상태와 무관하게 통과해야 한다\nstderr: ${r.stderr}`,
    );
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

// ── MESSAGE_OK 검사: 누락된 키 진단 메시지에 클라이언트명·파일경로·키·한글이 있다 ──────
// L(), Locale.Get(), t() 세 호출 형식 각각에 대해 확인한다.

test('MESSAGE_OK — L()로 부른 없는 키: 진단에 클라이언트명·파일경로·키·한글이 있다', () => {
  const dir = tmpDir();
  try {
    setup(dir, {
      files: {
        'native/macos/Sources/MightyClaude/View.swift': 'L("msg.l.gone")\n',
        'native/windows/MightyClaude.WinUI/MightyClaude.WinUI.csproj': CSPROJ,
      },
    });
    const r = run(['--root', dir]);
    assert.notEqual(r.status, 0);
    const out = r.stderr + r.stdout;
    assert.match(out, /macOS app/, '클라이언트명이 출력에 없다');
    assert.match(out, /native\/macos\/Sources\/MightyClaude\/View\.swift/, '파일 경로가 출력에 없다');
    assert.match(out, /msg\.l\.gone/, '누락된 키가 출력에 없다');
    assert.ok(/[가-힣]/.test(out), '한글 문자가 출력에 없다');
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('MESSAGE_OK — Locale.Get()로 부른 없는 키: 진단에 클라이언트명·파일경로·키·한글이 있다', () => {
  const dir = tmpDir();
  try {
    setup(dir, {
      files: {
        'native/windows/MightyClaude.Core/MyClass.cs': 'Locale.Get("win.get.gone");\n',
        'native/windows/MightyClaude.WinUI/MightyClaude.WinUI.csproj': CSPROJ,
      },
    });
    const r = run(['--root', dir]);
    assert.notEqual(r.status, 0);
    const out = r.stderr + r.stdout;
    assert.match(out, /Windows Core/, '클라이언트명이 출력에 없다');
    assert.match(out, /native\/windows\/MightyClaude\.Core\/MyClass\.cs/, '파일 경로가 출력에 없다');
    assert.match(out, /win\.get\.gone/, '누락된 키가 출력에 없다');
    assert.ok(/[가-힣]/.test(out), '한글 문자가 출력에 없다');
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('MESSAGE_OK — t()로 부른 없는 키: 진단에 클라이언트명·파일경로·키·한글이 있다', () => {
  const dir = tmpDir();
  try {
    setup(dir, {
      files: {
        'mobile/src/App.tsx': 't("app.t.gone")\n',
        'native/windows/MightyClaude.WinUI/MightyClaude.WinUI.csproj': CSPROJ,
      },
    });
    const r = run(['--root', dir]);
    assert.notEqual(r.status, 0);
    const out = r.stderr + r.stdout;
    assert.match(out, /phone/, '클라이언트명이 출력에 없다');
    assert.match(out, /mobile\/src\/App\.tsx/, '파일 경로가 출력에 없다');
    assert.match(out, /app\.t\.gone/, '누락된 키가 출력에 없다');
    assert.ok(/[가-힣]/.test(out), '한글 문자가 출력에 없다');
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

// ── 검사 9: CRLF로 받은 docs/i18n.md도 --check를 통과한다 ─────────────────────
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

// ── Translations (zh, ja): a subset of en's keys with en's placeholders ──────────
// Keys used by a client source so check 4 (unused keys) passes.
const USED = {
  'native/macos/Sources/MightyClaude/View.swift': 'L("a.one")\nL("a.two")\n',
  'native/windows/MightyClaude.WinUI/MightyClaude.WinUI.csproj': CSPROJ,
};
const KO = { 'a.one': '하나 {n}', 'a.two': '둘' };
const EN = { 'a.one': 'One {n}', 'a.two': 'Two' };

test('a translation that misses keys passes and the missing keys are counted', () => {
  const dir = tmpDir();
  try {
    setup(dir, { koJson: KO, enJson: EN, zhJson: { 'a.one': '一 {n}' }, files: USED });
    const r = run(['--root', dir]);
    assert.equal(r.status, 0, `missing translation keys must not fail\nstderr: ${r.stderr}`);
    assert.match(r.stdout, /zh {2}번역 1개, 빠진 키 1개/);
    assert.match(r.stdout, /ja {2}번역 0개, 빠진 키 2개/);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('a translation key that en has not got fails', () => {
  const dir = tmpDir();
  try {
    setup(dir, { koJson: KO, enJson: EN, jaJson: { 'a.three': '三' }, files: USED });
    const r = run(['--root', dir]);
    assert.notEqual(r.status, 0, 'a key en has not got must fail');
    assert.match(r.stderr, /ja\.json의 a\.three/);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('a translation whose placeholders differ from en fails', () => {
  const dir = tmpDir();
  try {
    setup(dir, { koJson: KO, enJson: EN, zhJson: { 'a.one': '一 {count}' }, files: USED });
    const r = run(['--root', dir]);
    assert.notEqual(r.status, 0, 'a placeholder mismatch must fail');
    assert.match(r.stderr, /a\.one의 자리표가 다릅니다: zh \{count\} \/ en \{n\}/);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('a translation copy that differs from the original fails', () => {
  const dir = tmpDir();
  try {
    setup(dir, { koJson: KO, enJson: EN, files: USED });
    fs.writeFileSync(path.join(dir, 'mobile/src/locales/zh.json'), '{"a.two":"二"}');
    const r = run(['--root', dir]);
    assert.notEqual(r.status, 0, 'a differing zh copy must fail');
    assert.match(r.stderr, /mobile\/src\/locales\/zh\.json이 locales\/zh\.json과 다릅니다/);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});
