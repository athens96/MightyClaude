#!/usr/bin/env node
// Usage: node scripts/check-locales.js                               # check, and rewrite docs/i18n.md
//        node scripts/check-locales.js --check                       # check only (fails when docs/i18n.md is out of date)
//        node scripts/check-locales.js --root <dir>                  # check another root (fixture tests)
//        node scripts/check-locales.js --touched-since <sha>         # also check the Windows files touched since <sha>
//        node scripts/check-locales.js --write-budget                # lower the Korean-literal budget to today's counts
//
// locales/ko.json and locales/en.json are the one source in this repository; the three
// clients carry copies of them (docs/i18n.md). Plain node only, no dependencies.
//
//   1. ko and en have the same keys (and every other complete language has exactly en's keys)
//   2. each key has the same placeholders ({name}) in both
//   2b. Translations (zh, ja) only carry keys en has, with en's placeholders per key.
//       Keys they still miss are counted, not failed (stage 2 of the four-language plan makes it strict).
//   3. each client copy is byte-identical to the original
//   4. no unused keys: every key is called from at least one client source
//   5. no missing keys: every key a client source calls is in the original
//   6. the Korean-literal ratchet: the hard-coded Korean literals left in each client
//      (area) must equal that area's count in scripts/korean-literal-budget.json. More is a
//      new literal; fewer means the budget is stale and --write-budget must lower it, so the
//      count can only go down.
//
// The report prints the literals left per client and docs/i18n.md gets the same table.
// Any broken check exits non-zero.
//
// The budget file also says what is not counted, each with its reason:
//   exemptFiles    path globs (`*` within one path segment) of whole files, such as GUI smoke code
//   exemptRegions  files whose `// i18n-exempt-begin: <reason>` … `// i18n-exempt-end` regions
//                  are not counted; a marker in any other file is an error
//   allow          literals that must stay as written (they are matched against CLI output or
//                  parsed back from saved data), per file, with the literal as it appears
//                  between its quotes in the source, Hangul escapes (\uXXXX, \u{XXXX}) decoded
//
// Hangul spelled as a Unicode escape counts like Hangul typed out (decodeHangulEscapes).
// An exemption that no longer matches anything fails, so the list cannot go stale.
// --write-budget lowers each area to its count and never raises one.
//
// With --touched-since <sha>, the Windows Core and WinUI source files touched since that
// SHA (committed or not) must not keep hard-coded Korean literals the budget counts: the
// budget's exempt files and regions and allowed literals stay out, as they do in the ratchet.
// MainWindow.Smoke.cs is a check diagnostic and is left out.

import fs from 'node:fs';
import path from 'node:path';
import process from 'node:process';
import { execFileSync } from 'node:child_process';

// ── Arguments ──────────────────────────────────────────────────────────────────
const argv = process.argv.slice(2);
const CHECK_ONLY = argv.includes('--check');
const WRITE_BUDGET = argv.includes('--write-budget');
const BUDGET = 'scripts/korean-literal-budget.json';

const rootIdx = argv.indexOf('--root');
const ROOT = rootIdx >= 0
  ? path.resolve(argv[rootIdx + 1])
  : path.resolve(import.meta.dirname, '..');

const touchedSinceIdx = argv.indexOf('--touched-since');
const TOUCHED_SINCE = touchedSinceIdx >= 0 ? argv[touchedSinceIdx + 1] : null;
// Complete languages carry every key; translations may miss some, which then read English.
// scripts/locale-languages.json says which is which ({"complete": [...], "translations": [...]});
// without it ko and en are complete and zh and ja are translations.
const LANGUAGE_CONFIG = (() => {
  const file = path.join(ROOT, 'scripts/locale-languages.json');
  if (!fs.existsSync(file)) return { complete: ['ko', 'en'], translations: ['zh', 'ja'] };
  const value = JSON.parse(fs.readFileSync(file, 'utf8'));
  return { complete: value.complete ?? ['ko', 'en'], translations: value.translations ?? [] };
})();
const COMPLETE_LANGUAGES = LANGUAGE_CONFIG.complete;
const TRANSLATIONS = LANGUAGE_CONFIG.translations;
const LANGUAGES = [...COMPLETE_LANGUAGES, ...TRANSLATIONS];
const PLACEHOLDER = /\{([A-Za-z][A-Za-z0-9]*)\}/g;
// `t("key")` / `t('key')` (phone), `L("key")` (Swift), `Locale.Get("key")` (C#)
const REFERENCE = /\b(?:t|L|Locale\.Get)\(\s*["']([A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z][A-Za-z0-9_]*)+)["']/g;
const HANGUL = /[ᄀ-ᇿ㄰-㆏가-힯]/;
const HANGUL_CODE = (code) => (code >= 0x1100 && code <= 0x11ff) || (code >= 0x3130 && code <= 0x318f) || (code >= 0xac00 && code <= 0xd7af);
// `\uAC00` (C#, TS) and `\u{AC00}` (Swift, TS) spelled in a source. An even run of backslashes
// before it is an escaped backslash, not an escape, and is kept as it is.
const UNICODE_ESCAPE = /(?<!\\)((?:\\\\)*)\\u(?:\{([0-9A-Fa-f]{1,6})\}|([0-9A-Fa-f]{4}))/g;

/// Hangul written as a Unicode escape is still Korean in the app: it is decoded before
/// anything is counted, so escaping cannot hide a literal from the ratchet. Other escapes
/// stay as written.
function decodeHangulEscapes(source) {
  return source.replace(UNICODE_ESCAPE, (match, backslashes, braced, plain) => {
    const code = parseInt(braced ?? plain, 16);
    return HANGUL_CODE(code) ? backslashes + String.fromCodePoint(code) : match;
  });
}

/// The five clients (the budget's areas). `roots` is where literals are counted and key
/// references are found; `copies` are the copies that must be byte-identical to the original.
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
    // WinUI keeps no copy: it ships the root files themselves as Content. With no copy
    // there is nothing to drift, so the check looks at that link instead.
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

// Text in style manifests and styles/** is data: it is not moved, so it is not counted.
const SKIP_DIRECTORIES = new Set([
  'node_modules',
  '.build',
  'bin',
  'obj',
  'Resources',
  'locales',
  // Tests are not product UI: their literals are neither left-over copy nor key references.
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

/// Comments are not copy, and many comments here are Korean: left in, they would turn the
/// report into noise. A `//` inside a string is not a comment, so quotes are followed.
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
    // A `"""` block (Swift, C# raw strings) runs to the next `"""`, whatever quotes it holds.
    if (source.startsWith('"""', index)) {
      const close = source.indexOf('"""', index + 3);
      const end = close < 0 ? source.length : close + 3;
      out += source.slice(index, end);
      index = end;
      continue;
    }
    // A C# verbatim string (@"…", @$"…") has no backslash escapes; "" is its quote.
    if (character === '@' && (source[index + 1] === '"' || (source[index + 1] === '$' && source[index + 2] === '"'))) {
      let end = source.indexOf('"', index) + 1;
      while (end < source.length) {
        if (source[end] === '"') {
          if (source[end + 1] === '"') { end += 2; continue; }
          end += 1;
          break;
        }
        end += 1;
      }
      out += source.slice(index, end);
      index = end;
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

/// Counts the hard-coded Korean literals left: one per string literal that holds Hangul,
/// one per line of Hangul text that JSX draws as it is, and, as a last resort, one per
/// run of Hangul the literal pattern cannot see: text nested in an interpolation
/// (Swift `"\(c ? "가" : "나")"`, C# `$"{(c ? "가" : "나")}"`) or inside a Swift `"""` block.
const STRING_LITERAL = /"(?:[^"\\\n]|\\.)*"|'(?:[^'\\\n]|\\.)*'|`(?:[^`\\]|\\.)*`/g;
const JSX_TEXT = /^[^<>{}]*$/;

function countLiterals(relative, source) {
  const stripped = stripComments(source);
  let count = 0;
  for (const match of stripped.match(STRING_LITERAL) ?? []) {
    if (HANGUL.test(match)) count += 1;
  }
  if (relative.endsWith('.tsx')) {
    // JSX text nodes have no quotes: count the lines of Hangul that sit between `>` and `<`.
    const withoutStrings = stripped.replace(STRING_LITERAL, '""');
    for (const line of withoutStrings.split('\n')) {
      const trimmed = line.trim();
      if (HANGUL.test(trimmed) && JSX_TEXT.test(trimmed)) count += 1;
    }
  }
  // Whatever Hangul is left once every matched literal is blanked sits outside a literal
  // the pattern recognised. Each quote-free run of it on a line counts once.
  const residue = stripped.replace(STRING_LITERAL, '""');
  for (const line of residue.split('\n')) {
    if (!HANGUL.test(line)) continue;
    if (relative.endsWith('.tsx') && JSX_TEXT.test(line.trim())) continue; // a JSX line, counted above
    count += line.split(/["'`]/).filter((segment) => HANGUL.test(segment)).length;
  }
  return count;
}

// ── The Korean-literal budget and its exemptions ──────────────────────────────
const REGION_BEGIN = '// i18n-exempt-begin';
const REGION_END = '// i18n-exempt-end';

function loadBudget() {
  if (!exists(BUDGET)) {
    fail(`${BUDGET}이 없습니다. 한국어 하드코딩 문구의 상한이 필요합니다.`);
    return null;
  }
  try {
    const parsed = JSON.parse(readText(BUDGET));
    if (parsed === null || typeof parsed !== 'object' || typeof parsed.areas !== 'object' || parsed.areas === null) {
      fail(`${BUDGET}에 areas 객체가 없습니다.`);
      return null;
    }
    return parsed;
  } catch (caught) {
    fail(`${BUDGET}을 JSON으로 읽지 못했습니다: ${caught.message}`);
    return null;
  }
}

const budget = loadBudget();
const exemptGlobs = Object.entries(budget?.exemptFiles ?? {}).map(([glob, reason]) => ({
  glob,
  reason,
  pattern: new RegExp('^' + glob.split('*').map((part) => part.replace(/[.+?^${}()|[\]\\]/g, '\\$&')).join('[^/]*') + '$'),
  matched: 0,
  files: [], // { relative, count } of each matched file that holds Korean
}));
const exemptRegionFiles = new Map(Object.entries(budget?.exemptRegions ?? {}).map(([file, reason]) => [file, { reason, regions: 0 }]));
const allowEntries = (budget?.allow ?? []).map((entry) => ({
  ...entry,
  literals: Array.isArray(entry.literals) ? entry.literals : [entry.literal],
  found: new Set(),
}));
for (const entry of allowEntries) {
  if (!entry.file || !entry.reason || entry.literals.some((literal) => typeof literal !== 'string')) {
    fail(`${BUDGET}의 allow 항목에는 file, literal(s), reason이 모두 있어야 합니다: ${JSON.stringify(entry)}`);
  }
}

/// Splits a source into what is counted and what an exempt region holds. Markers are
/// only honoured in files the budget lists under exemptRegions, and must pair up.
/// With `record` false (a second look at a file) nothing is credited and nothing fails again.
function splitRegions(relative, source, record = true) {
  const failHere = record ? fail : () => {};
  if (!source.includes('i18n-exempt-')) return { counted: source, exempted: '' };
  const listed = exemptRegionFiles.get(relative);
  if (!listed) {
    failHere(`${relative}에 i18n-exempt 표시가 있지만 ${BUDGET}의 exemptRegions에 없습니다.`);
    return { counted: source, exempted: '' };
  }
  let counted = '';
  let exempted = '';
  let rest = source;
  for (;;) {
    const begin = rest.indexOf(REGION_BEGIN);
    const stray = rest.indexOf(REGION_END);
    if (stray >= 0 && (begin < 0 || stray < begin)) {
      failHere(`${relative}의 ${REGION_END}에 짝이 되는 ${REGION_BEGIN}이 없습니다.`);
      break;
    }
    if (begin < 0) break;
    const lineEnd = rest.indexOf('\n', begin);
    const marker = rest.slice(begin, lineEnd < 0 ? rest.length : lineEnd);
    if (!/^\/\/ i18n-exempt-begin: \S/.test(marker)) {
      failHere(`${relative}의 ${REGION_BEGIN} 뒤에 ": <이유>"가 없습니다.`);
    }
    const end = rest.indexOf(REGION_END, begin + REGION_BEGIN.length);
    if (end < 0) {
      failHere(`${relative}의 ${REGION_BEGIN}이 ${REGION_END}로 닫히지 않습니다.`);
      break;
    }
    counted += rest.slice(0, begin);
    // Keep the region's line breaks on the counted side so line numbers stay put.
    const region = rest.slice(begin, end + REGION_END.length);
    exempted += region + '\n';
    counted += region.replace(/[^\n]/g, '');
    rest = rest.slice(end + REGION_END.length);
    if (record) listed.regions += 1;
  }
  return { counted: counted + rest, exempted };
}

/// The literals of one file, sorted into counted, exempt (a smoke file or region) and
/// allowed (an allow-list entry for this file). `record` false classifies without crediting
/// the exemptions and allow entries, for a second look at a file (--touched-since).
function classifyLiterals(relative, rawSource, record = true) {
  const source = decodeHangulEscapes(rawSource);
  // Every glob that matches is credited, so none of them looks stale while another covers the file.
  const exemptBy = exemptGlobs.filter((entry) => entry.pattern.test(relative));
  if (record) for (const entry of exemptBy) entry.matched += 1;
  if (exemptBy.length > 0) {
    const exempt = countLiterals(relative, source);
    if (record && exempt > 0) for (const entry of exemptBy) entry.files.push({ relative, count: exempt });
    return { counted: 0, exempt, allowed: 0 };
  }
  const { counted: countedSource, exempted } = splitRegions(relative, source, record);
  const exempt = exempted ? countLiterals(relative, exempted) : 0;
  const entries = allowEntries.filter((entry) => entry.file === relative);
  if (entries.length === 0) return { counted: countLiterals(relative, countedSource), exempt, allowed: 0 };
  let allowed = 0;
  // Comments are stripped first, so an allowed literal quoted in a comment is not the one blanked.
  let remaining = stripComments(countedSource);
  for (const match of remaining.match(STRING_LITERAL) ?? []) {
    if (!HANGUL.test(match)) continue;
    const inner = match.slice(1, -1);
    const entry = entries.find((candidate) => candidate.literals.includes(inner));
    if (!entry) continue;
    if (record) entry.found.add(inner);
    allowed += 1;
    remaining = remaining.replace(match, '""');
  }
  return { counted: countLiterals(relative, remaining), exempt, allowed };
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

// ── 1. The originals ──────────────────────────────────────────────────────────
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

// ── 2. Placeholders per key ───────────────────────────────────────────────────
for (const key of koKeys) {
  if (!enKeys.includes(key)) continue;
  const inKo = placeholdersOf(catalogues.ko[key]);
  const inEn = placeholdersOf(catalogues.en[key]);
  if (inKo.join(',') !== inEn.join(',')) {
    fail(`${key}의 자리표가 다릅니다: ko {${inKo.join(', ')}} / en {${inEn.join(', ')}}`);
  }
}

// ── 2a. Other complete languages: exactly en's keys, with en's placeholders ────
for (const language of COMPLETE_LANGUAGES.filter((language) => language !== 'ko' && language !== 'en')) {
  const catalogue = catalogues[language] ?? {};
  for (const key of enKeys) {
    if (!(key in catalogue)) fail(`${language}.json에 ${key}가 없습니다.`);
  }
  for (const key of Object.keys(catalogue).sort()) {
    if (!enKeys.includes(key)) {
      fail(`${language}.json의 ${key}가 en.json에 없습니다.`);
      continue;
    }
    const inLanguage = placeholdersOf(catalogue[key]);
    const inEn = placeholdersOf(catalogues.en[key]);
    if (inLanguage.join(',') !== inEn.join(',')) {
      fail(`${key}의 자리표가 다릅니다: ${language} {${inLanguage.join(', ')}} / en {${inEn.join(', ')}}`);
    }
  }
}

// ── 2b. Translations: a subset of en's keys, with en's placeholders ───────────
const translationProgress = [];
for (const language of TRANSLATIONS) {
  const catalogue = catalogues[language];
  if (!catalogue) continue;
  for (const key of Object.keys(catalogue).sort()) {
    if (!enKeys.includes(key)) {
      fail(`${language}.json의 ${key}가 en.json에 없습니다.`);
      continue;
    }
    const inTranslation = placeholdersOf(catalogue[key]);
    const inEn = placeholdersOf(catalogues.en[key]);
    if (inTranslation.join(',') !== inEn.join(',')) {
      fail(`${key}의 자리표가 다릅니다: ${language} {${inTranslation.join(', ')}} / en {${inEn.join(', ')}}`);
    }
  }
  const missing = enKeys.filter((key) => !(key in catalogue)).length;
  translationProgress.push({ language, translated: enKeys.length - missing, missing });
}

// ── 3. Client copies match the original ───────────────────────────────────────
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

// ── 4·5. Unused and missing keys, and the literals left ───────────────────────
const knownKeys = new Set(koKeys);
const referenced = new Map(); // key -> Set<client label>
const report = [];

for (const client of CLIENTS) {
  let literals = 0;
  let exempt = 0;
  let allowed = 0;
  let files = 0;
  const keys = new Set();
  const calledFrom = new Map(); // key -> first file path that references it
  for (const root of client.roots) {
    for (const relative of walk(root, client.extensions, [])) {
      const source = readText(relative);
      files += 1;
      const classified = classifyLiterals(relative, source);
      literals += classified.counted;
      exempt += classified.exempt;
      allowed += classified.allowed;
      const stripped = stripComments(source);
      for (const match of stripped.matchAll(REFERENCE)) {
        keys.add(match[1]);
        if (!calledFrom.has(match[1])) calledFrom.set(match[1], relative);
      }
      // A key kept in a table and looked up indirectly is a reference too: a string
      // literal whose content is exactly a key counts as a use.
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
  for (const [key, filePath] of calledFrom) {
    if (!knownKeys.has(key)) {
      fail(`${client.label}: ${filePath}에서 부르는 ${key}가 locales/ko.json에 없습니다.`);
    }
  }
  const limit = budget?.areas?.[client.label];
  report.push({ label: client.label, literals, exempt, allowed, files, moved: keys.size, budget: limit });
}

for (const key of koKeys) {
  if (!referenced.has(key)) {
    fail(`${key}는 어느 클라이언트 소스에서도 불리지 않습니다.`);
  }
}

// ── 6. The Korean-literal budget ──────────────────────────────────────────────
const staleAreas = [];
if (budget) {
  for (const entry of exemptGlobs) {
    if (entry.matched === 0) fail(`${BUDGET}의 exemptFiles ${entry.glob}에 맞는 파일이 없습니다.`);
    if (!entry.reason) fail(`${BUDGET}의 exemptFiles ${entry.glob}에 이유가 없습니다.`);
  }
  for (const [file, entry] of exemptRegionFiles) {
    if (entry.regions === 0) fail(`${BUDGET}의 exemptRegions ${file}에 i18n-exempt 구역이 없습니다.`);
    if (!entry.reason) fail(`${BUDGET}의 exemptRegions ${file}에 이유가 없습니다.`);
  }
  for (const entry of allowEntries) {
    for (const literal of entry.literals) {
      if (!entry.found.has(literal)) fail(`${BUDGET}의 allow 항목 "${literal}"이 ${entry.file}에 없습니다.`);
    }
  }
  for (const label of Object.keys(budget.areas)) {
    if (!CLIENTS.some((client) => client.label === label)) fail(`${BUDGET}의 areas에 모르는 영역 ${label}이 있습니다.`);
  }
  for (const row of report) {
    if (typeof row.budget !== 'number') {
      fail(`${BUDGET}의 areas에 ${row.label}의 상한이 없습니다.`);
    } else if (row.literals > row.budget) {
      fail(`${row.label}: 한국어 하드코딩 문구가 ${row.literals}개로 상한 ${row.budget}개를 넘습니다. L()/Locale.Get()/t() 키로 옮기세요.`);
    } else if (row.literals < row.budget) {
      staleAreas.push(row);
    }
  }
  if (WRITE_BUDGET && failures.length === 0) {
    const areas = Object.fromEntries(report.map((row) => [row.label, Math.min(row.budget, row.literals)]));
    fs.writeFileSync(path.join(ROOT, BUDGET), JSON.stringify({ ...budget, areas }, null, 2) + '\n');
    for (const row of report) row.budget = areas[row.label];
    staleAreas.length = 0;
    console.log(`${BUDGET}의 상한을 지금 수로 낮췄습니다.`);
  }
  // The ratchet stays tight: a budget above its count is stale, so the next move up is caught.
  for (const row of staleAreas) {
    fail(`${row.label}: 한국어 하드코딩 문구가 ${row.literals}개로 상한 ${row.budget}개보다 적습니다. node scripts/check-locales.js --write-budget을 돌려 상한을 낮추세요.`);
  }
}

// ── Touched Windows files (--touched-since) ───────────────────────────────────
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
        // What the budget counts: exempt smoke files and regions and allowed literals stay out.
        const count = classifyLiterals(relative, readText(relative), false).counted;
        if (count > 0) {
          fail(
            `${client.label}: ${relative}에 한국어 하드코딩 문구가 ${count}개 남아 있습니다.`,
          );
        }
      }
    }
  }
}

// ── Report ─────────────────────────────────────────────────────────────────────
const lines = [];
lines.push('클라이언트별 남은 한국어 하드코딩 문구');
lines.push('');
const width = Math.max(...report.map((row) => row.label.length));
for (const row of report) {
  lines.push(
    `  ${row.label.padEnd(width)}  ${String(row.literals).padStart(5)}개 / 상한 ${String(row.budget ?? '-').padStart(4)}  ` +
      `(예외 ${row.exempt}개, 허용 ${row.allowed}개, 소스 ${row.files}개, 옮긴 키 ${row.moved}개)`,
  );
}
lines.push('');
lines.push(`  합계 ${report.reduce((total, row) => total + row.literals, 0)}개, 옮긴 키 ${koKeys.length}개`);
lines.push('');
lines.push('번역 진행 (빠진 키는 영어로 보인다 — 세는 것이지 막는 것이 아니다)');
lines.push('');
for (const row of translationProgress) {
  lines.push(`  ${row.language}  번역 ${row.translated}개, 빠진 키 ${row.missing}개`);
}
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
  out.push('`locales/ko.json`, `locales/en.json`, `locales/zh.json`(간체 중국어), `locales/ja.json`이');
  out.push('저장소의 단 하나의 원본이다. 평평한 의미 키(`phone.pair.connect` 처럼)에서 문구로 가는');
  out.push('객체이고, 자리표는 `{name}` 꼴이다.');
  out.push('');
  out.push('- ko와 en은 완전한 언어다. 두 파일의 키 집합과 키마다의 자리표 집합은 같다. 한국어 값은');
  out.push('  옮기기 전의 리터럴과 바이트까지 같다.');
  out.push('- zh와 ja는 번역이다. 담은 키는 모두 en에 있어야 하고, 키마다의 자리표 집합은 en과 같다.');
  out.push('  아직 빠진 키는 실패가 아니라 수로 센다.');
  out.push('');
  out.push('클라이언트는 그 사본을 담는다.');
  out.push('');
  out.push('| 클라이언트 | 사본 |');
  out.push('| --- | --- |');
  out.push('| macOS | `native/macos/Sources/MightyCore/Resources/Locales` (`Package.swift`의 `.copy`) |');
  out.push('| Windows | `MightyClaude.WinUI.csproj`의 `Content` 연결 — 뿌리의 파일 자체를 옮기므로 어긋날 수 없다 |');
  out.push('| 전화기 | `mobile/src/locales` (`mobile/src/lib/i18n.ts`가 import) |');
  out.push('');
  out.push('`node scripts/check-locales.js`가 네 언어 모두 사본이 원본과 바이트까지 같은지, 키 집합과 자리표가');
  out.push('맞는지, 쓰이지 않는 키와 없는 키가 없는지를 보고, 하나라도 깨지면 0이 아닌 코드로 끝난다.');
  out.push('');
  out.push('## 언어 규칙');
  out.push('');
  out.push('맥과 윈도우는 설정에서 시스템·한국어·English·简体中文·日本語 가운데 고르고, 다음 실행 때');
  out.push('적용된다. 언어 이름은 그 언어 자신으로 쓴다(모든 언어 파일에서 같은 값). 전화기는 고르는');
  out.push('자리 없이 OS 언어를 따른다.');
  out.push('');
  out.push('- 시스템: OS가 선호하는 언어 목록을 차례로 보고, 앱이 가졌고 키가 하나라도 든 첫 언어를');
  out.push('  고른다. 아직 키가 없는(번역 전) 언어는 건너뛰고 다음 선호 언어로 간다. `zh*`는 zh,');
  out.push('  `ja*`는 ja, `ko*`는 ko, `en*`는 en이고, 하나도 없으면 en이다. 윈도우는 표시 언어를 먼저');
  out.push('  보고 그다음 Windows 언어 목록을 본다. 전화기는 Intl이 알려 주는 첫 언어 하나만 본다.');
  out.push('- 번체 중국어(`zh-Hant`, `zh-TW`, `zh-HK`)는 지금 간체 zh로 읽는다.');
  out.push('- 번역이 들어오기 전에 설정에서 中文이나 日本語를 직접 고르면 앱 문구는 영어로 보이고,');
  out.push('  시스템 메뉴(맥의 앱·편집·윈도우 메뉴)와 날짜·숫자 형식은 고른 언어를 따른다.');
  out.push('- 빠진 키: ko는 ko만, en은 en → ko, zh와 ja는 자기 → en → ko 차례로 찾는다. 어디에도');
  out.push('  없는 키는 키 자신을 돌려주어 아무것도 깨지지 않는다.');
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
  out.push('## 번역 진행');
  out.push('');
  out.push('세는 것이지 막는 것이 아니다 — 빠진 키는 영어 값으로 보인다.');
  out.push('');
  out.push('| 언어 | 번역한 키 | 빠진 키 |');
  out.push('| --- | --- | --- |');
  for (const row of translationProgress) {
    out.push(`| ${row.language} | ${row.translated} | ${row.missing} |`);
  }
  out.push('');
  out.push('## 클라이언트별 남은 한국어 하드코딩 문구');
  out.push('');
  out.push('`scripts/korean-literal-budget.json`이 영역마다 상한을 둔다. 남은 문구가 상한을 넘으면 새 문구라서,');
  out.push('상한보다 적으면 상한이 낡아서 검사가 실패한다. 상한은 `node scripts/check-locales.js --write-budget`으로');
  out.push('낮추기만 한다. 아래 예외와 허용 문구는 세지 않는다.');
  out.push('');
  out.push('문자열 리터럴 하나가 하나다. 리터럴 패턴이 보지 못하는 한글, 곧 보간 안에 든 글(Swift');
  out.push('`"\\(c ? "가" : "나")"`, C# `$"{(c ? "가" : "나")}"`)과 Swift `"""` 블록 안의 글도 따옴표 없는 덩어리마다');
  out.push('하나로 센다. 2-1 단계에서 이 마지막 계산을 더하면서 다른 영역의 상한도 실제 수로 다시 맞췄다: macOS core 872→874,');
  out.push('Windows WinUI 189→190, 전화기 202→219. Windows Core는 SlashCommandStrings가 공유 키로 옮겨 161→137로 내려갔다.');
  out.push('');
  out.push('| 클라이언트 | 남은 문구 | 상한 | 예외 | 허용 | 소스 파일 | 옮긴 키 |');
  out.push('| --- | --- | --- | --- | --- | --- | --- |');
  for (const row of report) {
    out.push(`| ${row.label} | ${row.literals} | ${row.budget ?? '-'} | ${row.exempt} | ${row.allowed} | ${row.files} | ${row.moved} |`);
  }
  out.push(`| **합계** | **${report.reduce((total, row) => total + row.literals, 0)}** | | | | | **${koKeys.length}** |`);
  out.push('');
  out.push('### 세지 않는 것');
  out.push('');
  out.push('| 대상 | 이유 |');
  out.push('| --- | --- |');
  for (const entry of exemptGlobs) out.push(`| \`${entry.glob}\` (파일 전체) | ${entry.reason.replace(/\|/g, '\\|')} |`);
  for (const [file, entry] of exemptRegionFiles) out.push(`| \`${file}\`의 i18n-exempt 구역 | ${entry.reason.replace(/\|/g, '\\|')} |`);
  out.push('');
  out.push('### 그대로 두는 문구 (허용 목록)');
  out.push('');
  out.push('CLI 출력이나 저장된 데이터와 맞춰 보는 문자열이다. 옮기면 동작이 바뀐다.');
  out.push('');
  out.push('| 파일 | 문구 | 이유 |');
  out.push('| --- | --- | --- |');
  for (const entry of allowEntries) {
    const shown = entry.literals.map((literal) => `\`${literal.replace(/\|/g, '\\|')}\``).join(', ');
    out.push(`| \`${entry.file}\` | ${shown} | ${entry.reason.replace(/\|/g, '\\|')} |`);
  }
  out.push('');
  out.push('## 호스트가 만드는 문구');
  out.push('');
  out.push('모바일 리모트에서 휴대폰이 보여 주는 호스트 오류(실행 창을 찾을 수 없음, 대기열이 가득 참 같은 것)는');
  out.push('호스트(Mac 또는 Windows)가 자기 언어로 만들어 문장째 보낸다. 그래서 휴대폰의 언어가 아니라 호스트의');
  out.push('언어를 따른다.');
  out.push('');
  out.push('## 남은 일');
  out.push('');
  out.push('- 호스트 오류에 바뀌지 않는 오류 코드를 붙여 보내고, 휴대폰이 그 코드를 자기 언어로 옮긴다.');
  out.push('- 언어를 바꾸면 다음 실행 때 적용된다. 그 자리에서 다시 그리는 일은 하지 않았다.');
  out.push('- zh와 ja의 빠진 키를 번역하고, 그 뒤 번역도 키가 모두 있어야 통과하도록 검사를 엄격하게 한다.');
  out.push('- 번체 중국어(`zh-Hant`, `zh-TW`, `zh-HK`)는 지금 간체로 읽는다. 따로 둘지는 나중에 정한다.');
  out.push('- 표의 남은 문구를 단계마다 한 켜씩 옮긴다. 매니페스트 글과 `styles/**`는 데이터이므로');
  out.push('  옮기지 않는다.');
  // Exemptions whose reason names v7 are frozen code (the style engine) waiting for the next engine version.
  const v7 = exemptGlobs.filter((entry) => /\bv7\b/.test(entry.reason)).flatMap((entry) => entry.files);
  if (v7.length > 0) {
    const total = v7.reduce((sum, file) => sum + file.count, 0);
    out.push(`- v7 작업: 고정된 스타일 엔진(v6)의 코드에 남은 한국어 문구 ${total}개. 엔진이 v6으로 고정되어 지금은`);
    out.push('  옮기지 않고 예외로 둔다. 스타일 엔진 v7에서 `L()` 키로 옮긴다.');
    for (const file of [...v7].sort((a, b) => a.relative.localeCompare(b.relative))) {
      out.push(`  - \`${file.relative}\` ${file.count}개`);
    }
  }
  out.push('');
  return out.join('\n');
}

const DOC = 'docs/i18n.md';
const generated = documentation();
// The Windows runner may read a copy checked out with autocrlf. The report is always
// written with LF, so line endings are normalised before comparing.
const current = exists(DOC) ? readText(DOC).replace(/\r\n/g, '\n') : null;
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
