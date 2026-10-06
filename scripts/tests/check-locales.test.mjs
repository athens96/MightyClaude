// node --test scripts/tests/check-locales.test.mjs
// Checks check-locales.js against fixture trees and throwaway git repositories.
// It never touches the real locales or sources.

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

// The smallest WinUI csproj that carries the Content links check 3 looks for
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
// A budget roomy enough that no area can be over it; fitBudget then sets each area to its count.
const ROOMY_BUDGET = {
  areas: { 'macOS app': 100, 'macOS core': 100, 'Windows Core': 100, 'Windows WinUI': 100, phone: 100 },
};
const BUDGET_FILE = 'scripts/korean-literal-budget.json';

/** Sets every area of the fixture's budget to the count the checker reports, so fixtures
 *  about other checks meet the ratchet exactly (a budget above its count fails too). */
function fitBudget(dir) {
  fs.mkdirSync(path.join(dir, 'scripts'), { recursive: true });
  fs.writeFileSync(path.join(dir, BUDGET_FILE), JSON.stringify(ROOMY_BUDGET));
  const report = run(['--root', dir]).stdout;
  const areas = {};
  for (const match of report.matchAll(/^ {2}(.+?) +(\d+)개 \/ 상한/gm)) areas[match[1]] = Number(match[2]);
  fs.writeFileSync(path.join(dir, BUDGET_FILE), JSON.stringify({ areas }));
}

function setup(dir, { koJson = {}, enJson = {}, zhJson = {}, jaJson = {}, files = {}, budget = 'fit' } = {}) {
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

  if (budget && budget !== 'fit') {
    fs.mkdirSync(path.join(dir, 'scripts'), { recursive: true });
    fs.writeFileSync(path.join(dir, BUDGET_FILE), JSON.stringify(budget));
  }

  for (const [rel, content] of Object.entries(files)) {
    const abs = path.join(dir, rel);
    fs.mkdirSync(path.dirname(abs), { recursive: true });
    fs.writeFileSync(abs, content);
  }
  if (budget === 'fit') fitBudget(dir);
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

// ── Check 1: a missing key called with L("key") fails ─────────────────────────
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

// ── Check 2: a missing key called with Locale.Get("key") fails ────────────────
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

// ── Check 3: a missing key called with t("key") fails ─────────────────────────
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

// ── Check 4: a touched Windows file with a Korean literal fails ───────────────
test('접촉된 Windows 파일에 한국어 리터럴이 있으면 실패한다', () => {
  const dir = tmpDir();
  try {
    setupWindows(dir, {
      'native/windows/MightyClaude.Core/MyClass.cs': '// clean\n',
    });
    const g = gitInit(dir);
    const sha = gitCommit(g, 'baseline');

    // Korean added after the baseline (uncommitted, unstaged)
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

// ── Check 5: an untouched Windows file with Korean passes ─────────────────────
test('접촉되지 않은 Windows 파일에 한국어가 있어도 통과한다', () => {
  const dir = tmpDir();
  try {
    // A file that has had Korean since the baseline
    setupWindows(dir, {
      'native/windows/MightyClaude.Core/MyClass.cs': 'string msg = "안녕하세요";\n',
    });
    const g = gitInit(dir);
    const sha = gitCommit(g, 'baseline');

    // The file is not touched after the baseline
    const r = run(['--root', dir, '--touched-since', sha]);
    assert.equal(r.status, 0, `미접촉 파일은 통과해야 한다\nstderr: ${r.stderr}`);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

// ── Check 6: a touched MainWindow.Smoke.cs with Korean passes ─────────────────
test('접촉된 MainWindow.Smoke.cs에 한국어가 있어도 통과한다', () => {
  const dir = tmpDir();
  try {
    setupWindows(dir, {
      'native/windows/MightyClaude.WinUI/MainWindow.Smoke.cs': '// smoke\n',
    });
    const g = gitInit(dir);
    const sha = gitCommit(g, 'baseline');

    // Korean added to Smoke.cs after the baseline (uncommitted, unstaged)
    fs.writeFileSync(
      path.join(dir, 'native/windows/MightyClaude.WinUI/MainWindow.Smoke.cs'),
      'string msg = "안녕하세요";\n',
    );
    fitBudget(dir); // the new literal is on the WinUI budget; this test is about the touched-file rule

    const r = run(['--root', dir, '--touched-since', sha]);
    assert.equal(r.status, 0, `Smoke.cs는 면제이므로 통과해야 한다\nstderr: ${r.stderr}`);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

// ── Check 7: a staged, uncommitted Korean file fails too (the touched set is a union) ──
// git diff --name-only catches unstaged changes and git diff --name-only --cached staged ones.
// This proves a staged (uncommitted index) change is part of the touched set.
test('staged 미커밋 Korean 파일이 실패한다 — touched set은 커밋과 미커밋의 합집합이다', () => {
  const dir = tmpDir();
  try {
    setupWindows(dir, {
      'native/windows/MightyClaude.Core/MyClass.cs': '// clean\n',
    });
    const g = gitInit(dir);
    const sha = gitCommit(g, 'baseline');

    // Korean added after the baseline, staged with git add but not committed
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

// ── Check 8: --root keeps the git queries inside the fixture ──────────────────
// Without --root the checker uses the git of the current directory (the real repository).
// With --root <fixture> every git -C ROOT call is bound to the fixture, so a clean
// fixture passes however dirty the real working tree is.
test('--root는 git 질의를 픽스처로 제한한다 — 실제 저장소가 더럽더라도 픽스처가 통과한다', () => {
  const dir = tmpDir();
  try {
    // The fixture has no Korean file and no missing key
    setupWindows(dir);
    const g = gitInit(dir);
    const sha = gitCommit(g, 'baseline');

    // The real repository (process.cwd()) may be dirty or have new commits by now.
    // With --root <fixture> the checker walks only the fixture and asks
    // git -C <fixture> for the diff, so the real repository does not matter.
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

// ── MESSAGE_OK: a missing-key message names the client, the file, the key, in Korean ──
// Checked for each of the three call forms, L(), Locale.Get() and t().

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

// ── Check 9: a docs/i18n.md checked out with CRLF passes --check ──────────────
// The Windows runner checks the repository out with autocrlf. The report is always
// written with LF, so treating a copy that differs only in line endings as out of
// date would turn CI red for that alone.
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

// ── The Korean-literal budget (scripts/korean-literal-budget.json) ──────────────
const APP = 'native/macos/Sources/MightyClaude';
const ZERO_AREAS = { 'macOS app': 0, 'macOS core': 0, 'Windows Core': 0, 'Windows WinUI': 0, phone: 0 };
const budgetFor = (macApp, extra = {}) => ({ areas: { ...ZERO_AREAS, 'macOS app': macApp }, ...extra });
const readBudget = (dir) => JSON.parse(fs.readFileSync(path.join(dir, BUDGET_FILE), 'utf8'));

test('an area over its budget fails', () => {
  const dir = tmpDir();
  try {
    setup(dir, { budget: budgetFor(1), files: { ...USED, [`${APP}/A.swift`]: 'let a = "하나"\nlet b = "둘"\n' }, koJson: KO, enJson: EN });
    const r = run(['--root', dir]);
    assert.notEqual(r.status, 0, 'two literals over a budget of one must fail');
    assert.match(r.stderr, /macOS app: 한국어 하드코딩 문구가 2개로 상한 1개를 넘습니다/);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('an area at its budget passes, and one under it fails until the budget is lowered', () => {
  const dir = tmpDir();
  try {
    setup(dir, { budget: budgetFor(1), files: { ...USED, [`${APP}/A.swift`]: 'let a = "하나"\n' }, koJson: KO, enJson: EN });
    const exact = run(['--root', dir]);
    assert.equal(exact.status, 0, `a count equal to its budget must pass\nstderr: ${exact.stderr}`);

    fs.writeFileSync(path.join(dir, BUDGET_FILE), JSON.stringify(budgetFor(3)));
    const stale = run(['--root', dir]);
    assert.notEqual(stale.status, 0, 'a budget above its count must fail');
    assert.match(stale.stderr, /macOS app: 한국어 하드코딩 문구가 1개로 상한 3개보다 적습니다\. .*--write-budget/);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('--write-budget lowers an area to its count', () => {
  const dir = tmpDir();
  try {
    setup(dir, { budget: budgetFor(3), files: { ...USED, [`${APP}/A.swift`]: 'let a = "하나"\n' }, koJson: KO, enJson: EN });
    const r = run(['--root', dir, '--write-budget']);
    assert.equal(r.status, 0, `--write-budget must pass\nstderr: ${r.stderr}`);
    assert.equal(readBudget(dir).areas['macOS app'], 1);
    assert.equal(readBudget(dir).areas.phone, 0, 'every area is lowered to its own count');
    assert.equal(run(['--root', dir]).status, 0, 'the lowered budget must pass');
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('--write-budget never raises an area', () => {
  const dir = tmpDir();
  try {
    setup(dir, { budget: budgetFor(0), files: { ...USED, [`${APP}/A.swift`]: 'let a = "하나"\n' }, koJson: KO, enJson: EN });
    const r = run(['--root', dir, '--write-budget']);
    assert.notEqual(r.status, 0, 'a count over the budget must still fail');
    assert.equal(readBudget(dir).areas['macOS app'], 0, 'the budget must not be raised');
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('a missing budget file fails', () => {
  const dir = tmpDir();
  try {
    setup(dir, { budget: null, files: USED, koJson: KO, enJson: EN });
    const r = run(['--root', dir]);
    assert.notEqual(r.status, 0, 'no budget must fail');
    assert.match(r.stderr, /korean-literal-budget\.json이 없습니다/);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('exempt files are not counted, and an exemption that matches nothing fails', () => {
  const dir = tmpDir();
  try {
    const exemptFiles = { [`${APP}/*Diagnostics.swift`]: 'smoke harness' };
    setup(dir, { budget: budgetFor(0, { exemptFiles }), files: { ...USED, [`${APP}/GraphDiagnostics.swift`]: 'let a = "검증"\n' }, koJson: KO, enJson: EN });
    const passed = run(['--root', dir]);
    assert.equal(passed.status, 0, `an exempt file must not count\nstderr: ${passed.stderr}`);
    assert.match(passed.stdout, /macOS app +0개 \/ 상한 +0 +\(예외 1개/);

    fs.rmSync(path.join(dir, `${APP}/GraphDiagnostics.swift`));
    const stale = run(['--root', dir]);
    assert.notEqual(stale.status, 0, 'a stale exemption must fail');
    assert.match(stale.stderr, /exemptFiles .*Diagnostics\.swift에 맞는 파일이 없습니다/);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('an exempt region is not counted only in a file the budget lists', () => {
  const dir = tmpDir();
  const source = 'let ui = L("a.one")\n// i18n-exempt-begin: smoke fixture\nlet fixture = "검증"\n// i18n-exempt-end\n';
  try {
    setup(dir, {
      budget: budgetFor(0, { exemptRegions: { [`${APP}/Store.swift`]: 'smoke' } }),
      files: { ...USED, [`${APP}/Store.swift`]: source },
      koJson: KO,
      enJson: EN,
    });
    const passed = run(['--root', dir]);
    assert.equal(passed.status, 0, `a listed region must not count\nstderr: ${passed.stderr}`);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
  const unlisted = tmpDir();
  try {
    setup(unlisted, { budget: budgetFor(5), files: { ...USED, [`${APP}/Store.swift`]: source }, koJson: KO, enJson: EN });
    const r = run(['--root', unlisted]);
    assert.notEqual(r.status, 0, 'a region in an unlisted file must fail');
    assert.match(r.stderr, /Store\.swift에 i18n-exempt 표시가 있지만/);
  } finally {
    fs.rmSync(unlisted, { recursive: true, force: true });
  }
});

test('an unclosed exempt region fails', () => {
  const dir = tmpDir();
  try {
    setup(dir, {
      budget: budgetFor(5, { exemptRegions: { [`${APP}/Store.swift`]: 'smoke' } }),
      files: { ...USED, [`${APP}/Store.swift`]: '// i18n-exempt-begin: smoke\nlet fixture = "검증"\n' },
      koJson: KO,
      enJson: EN,
    });
    const r = run(['--root', dir]);
    assert.notEqual(r.status, 0, 'an unclosed region must fail');
    assert.match(r.stderr, /i18n-exempt-begin이 \/\/ i18n-exempt-end로 닫히지 않습니다/);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('an allowed literal is not counted, and an allow entry that matches nothing fails', () => {
  const dir = tmpDir();
  try {
    const allow = [{ file: `${APP}/Motion.swift`, literals: ['읽', '검색'], reason: 'matched against summaries' }];
    setup(dir, {
      budget: budgetFor(0, { allow }),
      files: { ...USED, [`${APP}/Motion.swift`]: 'let words = ["read", "읽", "검색"]\n' },
      koJson: KO,
      enJson: EN,
    });
    const passed = run(['--root', dir]);
    assert.equal(passed.status, 0, `allowed literals must not count\nstderr: ${passed.stderr}`);
    assert.match(passed.stdout, /허용 2개/);

    fs.writeFileSync(path.join(dir, `${APP}/Motion.swift`), 'let words = ["read", "읽"]\n');
    const stale = run(['--root', dir]);
    assert.notEqual(stale.status, 0, 'a stale allow entry must fail');
    assert.match(stale.stderr, /allow 항목 "검색"이 .*Motion\.swift에 없습니다/);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('an allowed literal quoted in a comment above it is still the code literal that is allowed', () => {
  const dir = tmpDir();
  try {
    const allow = [{ file: `${APP}/Marker.swift`, literal: '첨부: ', reason: 'saved-data marker' }];
    setup(dir, {
      budget: budgetFor(0, { allow }),
      files: { ...USED, [`${APP}/Marker.swift`]: '// one "첨부: " line per attachment\nlet typed = text.hasPrefix("첨부: ")\n' },
      koJson: KO,
      enJson: EN,
    });
    const result = run(['--root', dir]);
    assert.equal(result.status, 0, `the code literal must be the one allowed\nstderr: ${result.stderr}`);
    assert.match(result.stdout, /허용 1개/);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

// ── The last-resort count: Hangul the literal pattern cannot see ───────────────
test('Hangul nested in an interpolation or a """ block counts toward its area', () => {
  const dir = tmpDir();
  try {
    setup(dir, {
      budget: budgetFor(0),
      files: {
        ...USED,
        [`${APP}/Nested.swift`]: 'let a = "\\(flag ? "가" : "나")"\nlet b = """\n    블록 글\n    """\n',
        'native/windows/MightyClaude.Core/Nested.cs': 'var c = $"{(flag ? "가" : "나")}";\n',
      },
      koJson: KO,
      enJson: EN,
    });
    const r = run(['--root', dir]);
    assert.notEqual(r.status, 0, 'nested Hangul must count');
    assert.match(r.stderr, /macOS app: 한국어 하드코딩 문구가 3개로 상한 0개를 넘습니다/);
    assert.match(r.stderr, /Windows Core: 한국어 하드코딩 문구가 2개로 상한 0개를 넘습니다/);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('a C# verbatim string or raw block does not turn later comments into literals', () => {
  const dir = tmpDir();
  try {
    const source = 'var path = @"C:\\temp\\";\n// 주석은 세지 않는다\nvar raw = """\n  {"a": "b"}\n  """;\n// 주석도 세지 않는다\n';
    setup(dir, { budget: budgetFor(0), files: { ...USED, 'native/windows/MightyClaude.Core/Verbatim.cs': source }, koJson: KO, enJson: EN });
    const r = run(['--root', dir]);
    assert.equal(r.status, 0, `comments after a verbatim string must not count\nstdout: ${r.stdout}\nstderr: ${r.stderr}`);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('every exempt glob that matches a file is credited', () => {
  const dir = tmpDir();
  try {
    const exemptFiles = { [`${APP}/*Diagnostics.swift`]: 'smoke harness', [`${APP}/Graph*.swift`]: 'graph smoke' };
    setup(dir, { budget: budgetFor(0, { exemptFiles }), files: { ...USED, [`${APP}/GraphDiagnostics.swift`]: 'let a = "검증"\n' }, koJson: KO, enJson: EN });
    const r = run(['--root', dir]);
    assert.equal(r.status, 0, `both globs match, so neither is stale\nstderr: ${r.stderr}`);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('a touched Windows file passes when its Korean is allowed or its file is exempt, and fails on any other literal', () => {
  const dir = tmpDir();
  try {
    const CORE = 'native/windows/MightyClaude.Core';
    const WINUI = 'native/windows/MightyClaude.WinUI';
    const extra = {
      allow: [{ file: `${CORE}/Names.cs`, literal: '사용자 설정', reason: 'a saved fingerprint input' }],
      exemptFiles: { [`${WINUI}/MainWindow.OtherSmoke.cs`]: 'smoke fixtures' },
    };
    setup(dir, {
      budget: { areas: ZERO_AREAS, ...extra },
      files: {
        [`${WINUI}/MightyClaude.WinUI.csproj`]: CSPROJ,
        [`${CORE}/Names.cs`]: 'const string Source = "사용자 설정";\n',
        [`${WINUI}/MainWindow.OtherSmoke.cs`]: 'string sample = "한글 입력";\n',
      },
    });
    const g = gitInit(dir);
    const sha = gitCommit(g, 'baseline');

    // Both files are touched after the baseline but keep only what the budget leaves out.
    fs.appendFileSync(path.join(dir, `${CORE}/Names.cs`), '// touched\n');
    fs.appendFileSync(path.join(dir, `${WINUI}/MainWindow.OtherSmoke.cs`), 'string more = "두 번째";\n');
    const passed = run(['--root', dir, '--touched-since', sha]);
    assert.equal(passed.status, 0, `allowed and exempt Korean must not fail a touched file\nstderr: ${passed.stderr}`);

    // A literal the allow entry does not name still fails the touched file (and the ratchet).
    fs.appendFileSync(path.join(dir, `${CORE}/Names.cs`), 'string label = "프로젝트 설정";\n');
    const failed = run(['--root', dir, '--touched-since', sha]);
    assert.notEqual(failed.status, 0, 'an unlisted literal in a touched file must fail');
    assert.match(failed.stderr, /Windows Core: native\/windows\/MightyClaude\.Core\/Names\.cs에 한국어 하드코딩 문구가 1개 남아 있습니다/);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('Hangul spelled as a Unicode escape counts like Hangul typed out, in C#, Swift and TypeScript', () => {
  const dir = tmpDir();
  try {
    setup(dir, {
      budget: { areas: ROOMY_BUDGET.areas },
      files: {
        ...USED,
        // Counted: \uXXXX (C#), \u{XXXX} (Swift), both forms in TypeScript, and a run of escapes in one literal.
        'native/windows/MightyClaude.Core/Escaped.cs': 'var a = "\\uD55C\\uAE00";\nvar b = "plain \\uAC00 text";\n',
        [`${APP}/Escaped.swift`]: 'let a = "\\u{D55C}\\u{AE00}"\n',
        'mobile/src/escaped.ts': "const a = '\\uAC00';\nconst b = `\\u{AC00}`;\n",
        // Not counted: an escaped backslash before u, and escapes outside Hangul (é, a surrogate range).
        'native/windows/MightyClaude.WinUI/Plain.cs': 'var a = "\\\\uAC00";\nvar b = "caf\\u00e9";\nvar c = "[\\uD800-\\uDBFF]";\n',
      },
      koJson: KO,
      enJson: EN,
    });
    const report = run(['--root', dir]).stdout;
    const count = (label) => Number(report.match(new RegExp(`^ {2}${label} +(\\d+)개`, 'm'))[1]);
    assert.equal(count('Windows Core'), 2, report);
    assert.equal(count('macOS app'), 1, report);
    assert.equal(count('phone'), 2, report);
    assert.equal(count('Windows WinUI'), 0, report);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('an allow entry names an escaped literal decoded, and a stale one still fails', () => {
  const dir = tmpDir();
  try {
    const marker = 'native/windows/MightyClaude.Core/Marker.cs';
    const allow = [{ file: marker, literal: '첨부: ', reason: 'saved-data marker' }];
    setup(dir, {
      budget: { areas: ZERO_AREAS, allow },
      files: { ...USED, [marker]: 'const string Marker = "\\uCCA8\\uBD80: ";\n' },
      koJson: KO,
      enJson: EN,
    });
    const passed = run(['--root', dir]);
    assert.equal(passed.status, 0, `the decoded allow entry must match the escaped literal\nstderr: ${passed.stderr}`);
    assert.match(passed.stdout, /Windows Core +0개 .*허용 1개/);

    fs.writeFileSync(path.join(dir, marker), 'const string Marker = "\\uCCA8: ";\n');
    const stale = run(['--root', dir]);
    assert.notEqual(stale.status, 0, 'a changed escaped literal is a new literal and leaves the allow entry stale');
    assert.match(stale.stderr, /allow 항목 "첨부: "이 .*Marker\.cs에 없습니다/);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});
