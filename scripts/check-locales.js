#!/usr/bin/env node
// 용법: node scripts/check-locales.js                               # 검사하고 docs/i18n.md를 새로 쓴다
//       node scripts/check-locales.js --check                       # 검사만 한다 (docs/i18n.md가 어긋나면 실패)
//       node scripts/check-locales.js --root <dir>                  # 주어진 뿌리로 검사한다 (픽스처 테스트용)
//       node scripts/check-locales.js --touched-since <sha>         # 기준 SHA 이후 접촉된 Windows 파일도 검사한다
//
// locales/ko.json과 locales/en.json이 이 저장소의 단 하나의 원본이고, 세 클라이언트는
// 그 사본을 담는다(docs/i18n.md). 이 검사는 plain node만 쓴다 — 의존성이 없다.
//
//   1. ko와 en의 키 집합이 같다
//   2. 키마다 자리표({name}) 집합이 같다
//   3. 클라이언트 사본이 원본과 바이트까지 같다
//   4. 쓰이지 않는 키가 없다 — 모든 키는 적어도 한 클라이언트 소스에서 불린다
//   5. 없는 키가 없다 — 클라이언트 소스가 부르는 모든 키가 원본에 있다
//
// 그리고 클라이언트마다 남아 있는 한국어 하드코딩 문구의 수를 세어 보고서를 찍고
// docs/i18n.md에 같은 내용을 쓴다. 검사 하나라도 깨지면 종료 코드가 0이 아니다.
// 남은 문구가 몇 개든 그 자체는 실패가 아니다 — 세는 것이지 막는 것이 아니다.
//
// --touched-since <sha>를 넘기면 그 SHA 이후 접촉된(커밋 또는 미커밋) Windows Core·WinUI
// 소스 파일에 한국어 하드코딩 문구가 남아 있으면 실패한다.
// MainWindow.Smoke.cs는 검사 진단용이므로 제외한다.

import fs from 'node:fs';
import path from 'node:path';
import process from 'node:process';
import { execFileSync } from 'node:child_process';

// ── CLI 인수 파싱 ─────────────────────────────────────────────────────────────
const argv = process.argv.slice(2);
const CHECK_ONLY = argv.includes('--check');

const rootIdx = argv.indexOf('--root');
const ROOT = rootIdx >= 0
  ? path.resolve(argv[rootIdx + 1])
  : path.resolve(import.meta.dirname, '..');

const touchedSinceIdx = argv.indexOf('--touched-since');
const TOUCHED_SINCE = touchedSinceIdx >= 0 ? argv[touchedSinceIdx + 1] : null;
const LANGUAGES = ['ko', 'en'];
const PLACEHOLDER = /\{([A-Za-z][A-Za-z0-9]*)\}/g;
// `t("key")` / `t('key')` (공통), `L("key")` (Swift), `Locale.Get("key")` (C#)
const REFERENCE = /\b(?:t|L|Locale\.Get)\(\s*["']([A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z][A-Za-z0-9_]*)+)["']/g;
const HANGUL = /[ᄀ-ᇿ㄰-㆏가-힯]/;

/// 다섯 클라이언트. `sources`는 문구를 세고 키 참조를 찾는 곳이고,
/// `copies`는 원본과 바이트까지 같아야 하는 사본이다.
const CLIENTS = [
  {
    label: 'macOS app',
    roots: ['native/macos/Sources/MightyClaude'],
    extensions: ['.swift'],
    copies: [],
  },
  {
    label: 'macOS core',
    roots: ['native/macos/Sources/MightyCore'],
    extensions: ['.swift'],
    copies: ['native/macos/Sources/MightyCore/Resources/Locales'],
  },
  {
    label: 'Windows Core',
    roots: ['native/windows/MightyClaude.Core'],
    extensions: ['.cs'],
    copies: [],
  },
  {
    label: 'Windows WinUI',
    roots: ['native/windows/MightyClaude.WinUI'],
    extensions: ['.cs', '.xaml'],
    // WinUI는 사본을 두지 않고 저장소 뿌리의 파일 자체를 Content로 걸어 옮긴다.
    // 사본이 없으니 어긋날 수도 없다 — 대신 그 연결이 살아 있는지를 본다.
    copies: [],
    linkedIn: 'native/windows/MightyClaude.WinUI/MightyClaude.WinUI.csproj',
  },
  {
    label: 'phone',
    roots: ['mobile/app', 'mobile/src'],
    extensions: ['.ts', '.tsx'],
    copies: ['mobile/src/locales'],
  },
];

// 스타일 매니페스트와 styles/** 안의 글은 데이터다 — 옮기지 않으므로 세지도 않는다.
const SKIP_DIRECTORIES = new Set([
  'node_modules',
  '.build',
  'bin',
  'obj',
  'Resources',
  'locales',
  // 테스트는 제품 화면이 아니다. 그 안의 문구는 남은 문구도, 키 참조도 아니다.
  '__tests__',
  'Tests',
]);

const failures = [];
function fail(message) {
  failures.push(message);
}

function readText(relative) {
  return fs.readFileSync(path.join(ROOT, relative), 'utf8');
}

function exists(relative) {
  return fs.existsSync(path.join(ROOT, relative));
}

function walk(relativeRoot, extensions, out) {
  const absolute = path.join(ROOT, relativeRoot);
  if (!fs.existsSync(absolute)) return out;
  for (const entry of fs.readdirSync(absolute, { withFileTypes: true })) {
    const relative = path.posix.join(relativeRoot, entry.name);
    if (entry.isDirectory()) {
      if (SKIP_DIRECTORIES.has(entry.name)) continue;
      walk(relative, extensions, out);
    } else if (extensions.includes(path.extname(entry.name))) {
      out.push(relative);
    }
  }
  return out;
}

/// 주석은 문구가 아니다. 이 저장소의 주석은 대부분 한국어라, 주석을 지우지 않으면
/// 보고서가 숫자가 아니라 잡음이 된다. 문자열 안의 `//`는 주석이 아니므로
/// 따옴표를 따라가며 지운다.
function stripComments(source) {
  let out = '';
  let index = 0;
  let quote = null;
  while (index < source.length) {
    const character = source[index];
    if (quote) {
      if (character === '\\') {
        out += source.slice(index, index + 2);
        index += 2;
        continue;
      }
      if (character === quote) quote = null;
      out += character;
      index += 1;
      continue;
    }
    if (character === '"' || character === "'" || character === '`') {
      quote = character;
      out += character;
      index += 1;
      continue;
    }
    if (character === '/' && source[index + 1] === '/') {
      while (index < source.length && source[index] !== '\n') index += 1;
      continue;
    }
    if (character === '/' && source[index + 1] === '*') {
      index += 2;
      while (index < source.length && !(source[index] === '*' && source[index + 1] === '/')) {
        if (source[index] === '\n') out += '\n';
        index += 1;
      }
      index += 2;
      continue;
    }
    out += character;
    index += 1;
  }
  return out;
}

/// 남은 한국어 하드코딩 문구를 센다: 한글이 든 문자열 리터럴 하나가 하나,
/// 그리고 JSX가 그대로 그리는 한글 글줄 하나가 하나.
const STRING_LITERAL = /"(?:[^"\\\n]|\\.)*"|'(?:[^'\\\n]|\\.)*'|`(?:[^`\\]|\\.)*`/g;
const JSX_TEXT = /^[^<>{}]*$/;

function countLiterals(relative, source) {
  const stripped = stripComments(source);
  let count = 0;
  for (const match of stripped.match(STRING_LITERAL) ?? []) {
    if (HANGUL.test(match)) count += 1;
  }
  if (relative.endsWith('.tsx')) {
    // JSX 텍스트 노드는 따옴표가 없다. `>한글<` 사이에 놓인 글줄을 따로 센다.
    const withoutStrings = stripped.replace(STRING_LITERAL, '""');
    for (const line of withoutStrings.split('\n')) {
      const trimmed = line.trim();
      if (HANGUL.test(trimmed) && JSX_TEXT.test(trimmed)) count += 1;
    }
  }
  return count;
}

function placeholdersOf(value) {
  return [...value.matchAll(PLACEHOLDER)].map((match) => match[1]).sort();
}

function loadCatalogue(relative) {
  let parsed;
  try {
    parsed = JSON.parse(readText(relative));
  } catch (caught) {
    fail(`${relative}을 JSON으로 읽지 못했습니다: ${caught.message}`);
    return null;
  }
  if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed)) {
    fail(`${relative}은 평평한 객체여야 합니다.`);
    return null;
  }
  for (const [key, value] of Object.entries(parsed)) {
    if (typeof value !== 'string') fail(`${relative}의 ${key}가 문자열이 아닙니다.`);
  }
  return parsed;
}

// ── 1. 원본 두 파일 ────────────────────────────────────────────────────────────
const catalogues = {};
for (const language of LANGUAGES) {
  const relative = `locales/${language}.json`;
  if (!exists(relative)) {
    fail(`${relative}이 없습니다.`);
    continue;
  }
  catalogues[language] = loadCatalogue(relative);
}

const koKeys = Object.keys(catalogues.ko ?? {}).sort();
const enKeys = Object.keys(catalogues.en ?? {}).sort();

for (const key of koKeys) {
  if (!enKeys.includes(key)) fail(`en.json에 ${key}가 없습니다.`);
}
for (const key of enKeys) {
  if (!koKeys.includes(key)) fail(`ko.json에 ${key}가 없습니다.`);
}

// ── 2. 키마다 자리표 집합 ──────────────────────────────────────────────────────
for (const key of koKeys) {
  if (!enKeys.includes(key)) continue;
  const inKo = placeholdersOf(catalogues.ko[key]);
  const inEn = placeholdersOf(catalogues.en[key]);
  if (inKo.join(',') !== inEn.join(',')) {
    fail(`${key}의 자리표가 다릅니다: ko {${inKo.join(', ')}} / en {${inEn.join(', ')}}`);
  }
}

// ── 3. 클라이언트 사본이 원본과 같다 ──────────────────────────────────────────
const originals = {};
for (const language of LANGUAGES) {
  const relative = `locales/${language}.json`;
  if (exists(relative)) originals[language] = fs.readFileSync(path.join(ROOT, relative));
}

for (const client of CLIENTS) {
  for (const directory of client.copies) {
    for (const language of LANGUAGES) {
      const relative = path.posix.join(directory, `${language}.json`);
      if (!exists(relative)) {
        fail(`${client.label}의 사본 ${relative}이 없습니다.`);
        continue;
      }
      const copy = fs.readFileSync(path.join(ROOT, relative));
      if (!originals[language] || !copy.equals(originals[language])) {
        fail(`${client.label}의 사본 ${relative}이 locales/${language}.json과 다릅니다.`);
      }
    }
  }
  if (client.linkedIn) {
    if (!exists(client.linkedIn)) {
      fail(`${client.label}의 ${client.linkedIn}이 없습니다.`);
    } else {
      const project = readText(client.linkedIn);
      for (const language of LANGUAGES) {
        const include = `../../../locales/${language}.json`;
        if (!project.includes(include)) {
          fail(`${client.label}의 ${client.linkedIn}이 ${include}을 담지 않습니다.`);
        }
      }
    }
  }
}

// ── 4·5. 쓰이지 않는 키와 없는 키, 그리고 남은 문구 ───────────────────────────
const knownKeys = new Set(koKeys);
const referenced = new Map(); // key -> Set<client label>
const report = [];

for (const client of CLIENTS) {
  let literals = 0;
  let files = 0;
  const keys = new Set();
  const called = new Set();
  for (const root of client.roots) {
    for (const relative of walk(root, client.extensions, [])) {
      const source = readText(relative);
      files += 1;
      literals += countLiterals(relative, source);
      const stripped = stripComments(source);
      for (const match of stripped.matchAll(REFERENCE)) {
        keys.add(match[1]);
        called.add(match[1]);
      }
      // 키를 표에 담아 간접으로 부르는 자리도 참조다: 내용이 키와 정확히 같은
      // 문자열 리터럴을 쓰인 것으로 센다.
      for (const literal of stripped.match(STRING_LITERAL) ?? []) {
        const inner = literal.slice(1, -1);
        if (knownKeys.has(inner)) keys.add(inner);
      }
    }
  }
  for (const key of keys) {
    if (!referenced.has(key)) referenced.set(key, new Set());
    referenced.get(key).add(client.label);
  }
  for (const key of called) {
    if (!knownKeys.has(key)) {
      fail(`${client.label}이 부르는 ${key}가 locales/ko.json에 없습니다.`);
    }
  }
  report.push({ label: client.label, literals, files, moved: keys.size });
}

for (const key of koKeys) {
  if (!referenced.has(key)) {
    fail(`${key}는 어느 클라이언트 소스에서도 불리지 않습니다.`);
  }
}

// ── 접촉된 Windows 파일 검사 (--touched-since) ────────────────────────────────
if (TOUCHED_SINCE) {
  const SMOKE_EXEMPT = 'native/windows/MightyClaude.WinUI/MainWindow.Smoke.cs';
  const windowsClients = CLIENTS.filter(
    (c) => c.label === 'Windows Core' || c.label === 'Windows WinUI',
  );

  const touchedSet = new Set();
  function addDiffLines(output) {
    for (const line of output.split('\n')) {
      const trimmed = line.trim();
      if (trimmed) touchedSet.add(trimmed);
    }
  }
  try {
    addDiffLines(
      execFileSync('git', ['-C', ROOT, 'diff', '--name-only', TOUCHED_SINCE, 'HEAD'], {
        encoding: 'utf8',
      }),
    );
  } catch {}
  try {
    addDiffLines(
      execFileSync('git', ['-C', ROOT, 'diff', '--name-only', '--cached'], {
        encoding: 'utf8',
      }),
    );
  } catch {}
  try {
    addDiffLines(
      execFileSync('git', ['-C', ROOT, 'diff', '--name-only'], { encoding: 'utf8' }),
    );
  } catch {}

  for (const client of windowsClients) {
    for (const root of client.roots) {
      for (const relative of walk(root, client.extensions, [])) {
        if (relative === SMOKE_EXEMPT) continue;
        if (!touchedSet.has(relative)) continue;
        const source = readText(relative);
        const count = countLiterals(relative, source);
        if (count > 0) {
          fail(
            `${client.label}: ${relative}에 한국어 하드코딩 문구가 ${count}개 남아 있습니다.`,
          );
        }
      }
    }
  }
}

// ── 보고서 ─────────────────────────────────────────────────────────────────────
const lines = [];
lines.push('클라이언트별 남은 한국어 하드코딩 문구');
lines.push('');
const width = Math.max(...report.map((row) => row.label.length));
for (const row of report) {
  lines.push(
    `  ${row.label.padEnd(width)}  ${String(row.literals).padStart(5)}개  ` +
      `(소스 ${row.files}개, 옮긴 키 ${row.moved}개)`,
  );
}
lines.push('');
lines.push(`  합계 ${report.reduce((total, row) => total + row.literals, 0)}개, 옮긴 키 ${koKeys.length}개`);
const reportText = lines.join('\n');
console.log(reportText);

// ── docs/i18n.md ───────────────────────────────────────────────────────────────
function documentation() {
  const out = [];
  out.push('# 다국어 (i18n)');
  out.push('');
  out.push('이 문서의 본문은 `node scripts/check-locales.js`가 생성한다. 손으로 고치지 않는다.');
  out.push('');
  out.push('## 하나의 원본');
  out.push('');
  out.push('`locales/ko.json`과 `locales/en.json`이 저장소의 단 하나의 원본이다. 평평한 의미 키');
  out.push('(`phone.pair.connect` 처럼)에서 문구로 가는 객체이고, 자리표는 `{name}` 꼴이다.');
  out.push('두 파일의 키 집합과 키마다의 자리표 집합은 같다. 한국어 값은 옮기기 전의 리터럴과');
  out.push('바이트까지 같다.');
  out.push('');
  out.push('클라이언트는 그 사본을 담는다.');
  out.push('');
  out.push('| 클라이언트 | 사본 |');
  out.push('| --- | --- |');
  out.push('| macOS | `native/macos/Sources/MightyCore/Resources/Locales` (`Package.swift`의 `.copy`) |');
  out.push('| Windows | `MightyClaude.WinUI.csproj`의 `Content` 연결 — 뿌리의 파일 자체를 옮기므로 어긋날 수 없다 |');
  out.push('| 전화기 | `mobile/src/locales` (`mobile/src/lib/i18n.ts`가 import) |');
  out.push('');
  out.push('`node scripts/check-locales.js`가 사본이 원본과 바이트까지 같은지, 키 집합과 자리표가');
  out.push('맞는지, 쓰이지 않는 키와 없는 키가 없는지를 보고, 하나라도 깨지면 0이 아닌 코드로 끝난다.');
  out.push('');
  out.push('## 언어 규칙');
  out.push('');
  out.push('전화기는 OS 언어를 따르고 고르는 자리가 없다. OS 언어가 한국어면 한국어를, 그 밖이면');
  out.push('영어를 읽는다. 고른 언어에 없는 키는 한국어 값으로 내려오고, 두 언어에 다 없는 키는');
  out.push('키 자신을 돌려주어 아무것도 깨지지 않는다.');
  out.push('');
  out.push('## 영어 초안 — 화면 확인이 필요한 줄');
  out.push('');
  out.push('아래 영어는 에이전트가 쓴 초안이다. 화면 확인 때 사람이 한 줄씩 본다.');
  out.push('');
  out.push('| 키 | 한국어 | 영어 (초안) |');
  out.push('| --- | --- | --- |');
  for (const key of koKeys) {
    const korean = (catalogues.ko?.[key] ?? '').replace(/\|/g, '\\|');
    const english = (catalogues.en?.[key] ?? '').replace(/\|/g, '\\|');
    out.push(`| \`${key}\` | ${korean} | ${english} |`);
  }
  out.push('');
  out.push('## 클라이언트별 남은 한국어 하드코딩 문구');
  out.push('');
  out.push('세는 것이지 막는 것이 아니다 — 수가 몇이든 검사는 통과한다.');
  out.push('');
  out.push('| 클라이언트 | 남은 문구 | 소스 파일 | 옮긴 키 |');
  out.push('| --- | --- | --- | --- |');
  for (const row of report) {
    out.push(`| ${row.label} | ${row.literals} | ${row.files} | ${row.moved} |`);
  }
  out.push(`| **합계** | **${report.reduce((total, row) => total + row.literals, 0)}** | | **${koKeys.length}** |`);
  out.push('');
  out.push('## 남은 일');
  out.push('');
  out.push('- 언어를 바꾸면 다음 실행 때 적용된다. 그 자리에서 다시 그리는 일은 하지 않았다.');
  out.push('- 표의 남은 문구를 단계마다 한 켜씩 옮긴다. 매니페스트 글과 `styles/**`는 데이터이므로');
  out.push('  옮기지 않는다.');
  out.push('');
  return out.join('\n');
}

const DOC = 'docs/i18n.md';
const generated = documentation();
const current = exists(DOC) ? readText(DOC) : null;
if (CHECK_ONLY) {
  if (current !== generated) fail(`${DOC}가 생성한 내용과 다릅니다. \`node scripts/check-locales.js\`를 다시 돌리세요.`);
} else if (current !== generated) {
  fs.mkdirSync(path.join(ROOT, path.dirname(DOC)), { recursive: true });
  fs.writeFileSync(path.join(ROOT, DOC), generated);
  console.log(`\n${DOC}를 새로 썼습니다.`);
}

if (failures.length > 0) {
  console.error('');
  for (const message of failures) console.error(`  ✗ ${message}`);
  console.error(`\n${failures.length}개의 검사가 깨졌습니다.`);
  process.exit(1);
}
console.log('\n✓ locales 검사를 모두 통과했습니다.');
