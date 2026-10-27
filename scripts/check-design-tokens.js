#!/usr/bin/env node
// Usage: node scripts/check-design-tokens.js                  # check the three ratchets against their budgets
//        node scripts/check-design-tokens.js --check          # the same
//        node scripts/check-design-tokens.js --write          # lower every budget to today's counts
//        node scripts/check-design-tokens.js --write-budget   # the same (the check-locales.js spelling)
//        node scripts/check-design-tokens.js --root <dir>     # check another root (fixture tests)
//
// Three ratchets that may only go down. Plain node only, no dependencies.
//
// 1. Colour literals (scripts/design-token-budget.json), from the Windows design conversion
//    (.omc/plans/windows-design-conversion.md, stage 1). Every surface, border, ink and
//    accent on Windows is meant to read a design-token brush (WinUI DesignBrushes, Core
//    DesignTokens). This counts what still bypasses them, per file of the Windows Core
//    and WinUI sources:
//
//      Color.FromArgb(…)  ColorHelper.FromArgb(…)  Colors.X (except Colors.Transparent)  "#RRGGBB"
//      0xRRGGBB (a six-digit int is an RGB colour here)  Rgb(.r, .g, .b) calls (StatusLine's ANSI palette)
//      0x00BBGGRR GDI COLORREF inks in CompanionOverlay.cs (not the window-style flags OR-ed with |)
//
//    Files on the budget's exempt list (with its reason) are not counted. The budget holds the
//    committed count per file. The check fails when a file has more than its budget (a new
//    literal), when a file outside the budget has any, and when a file has fewer than its
//    budget (the budget is stale: lower it with --write, so the count can only go down).
//
// 2. Spacing literals (scripts/spacing-literal-budget.json), from the dense spacing work: every
//    gap is meant to come from the spacing scale and every named inset from the contract
//    (native/contracts/fixtures/design-tokens.json metrics.spacing / inset / layout, read by
//    MightyCore DesignMetrics, Core DesignMetrics.cs and the phone theme). This counts the raw
//    numbers still written where a spacing goes, per area. A number counts when its value is
//    2 or more (0 and 1 stay allowed: resets and hairlines); a negative number counts by its size.
//
//      macOS    native/macos/Sources/MightyClaude, native/macos/Sources/MightyCore (.swift)
//               .padding(n)  .padding(.edge, n)  .padding([.edges], n)  spacing: n
//      Windows  native/windows/MightyClaude.WinUI, native/windows/MightyClaude.Core (.cs)
//               each number in new Thickness(…)  Spacing = n  RowSpacing = n  ColumnSpacing = n
//      phone    mobile/src, mobile/app (.ts, .tsx)
//               padding*: n  margin*: n  gap: n  rowGap: n  columnGap: n
//
//    Tests (Tests/, __tests__/, *.test.ts[x]) are never counted. The budget's exemptFiles (path
//    globs: `*` within one path segment, `**` across segments), each with its reason, are not
//    counted either, and a glob that matches no file fails so the list cannot go stale. True
//    geometry (halo centring, a drawn shape) may sit in a region that is not counted:
//
//      // spacing-exempt-begin: <reason>
//      …
//      // spacing-exempt-end
//
//    The count of each area must equal its budget: more is a new literal (use the tokens),
//    fewer means the budget is stale and --write-budget must lower it. --write / --write-budget
//    never raise a count.
//
// 3. Font literals (scripts/font-literal-budget.json), from the text setting (Settings › General › 글자):
//    every word the WinUI app draws states its size at scale 1 as TextSize (and TextLineHeight,
//    TextHeight) and its font as TextFont, which AppText.cs draws at the user's size and font and
//    changes live. This counts, per file of native/windows/MightyClaude.WinUI, what still sets them
//    itself and so would not follow the setting:
//
//      FontSize = … (or +=, *=)   LineHeight = …   FontFamily = …   new FontFamily(…)   FontSizeProperty and the like
//
//    The budget's exemptFiles (globs as above, each with its reason; one that matches nothing fails)
//    are not counted. As with the colours, a file over its budget or outside it fails, and a file
//    under its budget is stale: lower it with --write.
//
// Comments are not counted by any ratchet.
//
// 4. Not a ratchet: the motion tokens' shape (design-tokens.json `motion`) and the pending display
//    cases (fixtures/pending.json) checked against them; see section 4 below.

import fs from 'node:fs';
import path from 'node:path';
import process from 'node:process';

const argv = process.argv.slice(2);
const WRITE = argv.includes('--write') || argv.includes('--write-budget');
const rootIndex = argv.indexOf('--root');
const ROOT = rootIndex >= 0 ? path.resolve(argv[rootIndex + 1]) : path.resolve(import.meta.dirname, '..');

const SKIP_DIRECTORIES = new Set(['bin', 'obj', 'node_modules', '.build', 'Tests', '__tests__']);
const TEST_FILE = /\.test\.[cm]?[jt]sx?$/;

function walk(relativeRoot, extensions, out) {
  const absolute = path.join(ROOT, relativeRoot);
  if (!fs.existsSync(absolute)) return out;
  for (const entry of fs.readdirSync(absolute, { withFileTypes: true })) {
    const relative = path.posix.join(relativeRoot, entry.name);
    if (entry.isDirectory()) {
      if (!SKIP_DIRECTORIES.has(entry.name)) walk(relative, extensions, out);
    } else if (extensions.includes(path.extname(entry.name)) && !TEST_FILE.test(entry.name)) {
      out.push(relative);
    }
  }
  return out;
}

/// Drops `//` and `/* */` comments while keeping every literal that may hold `//`, `/*` or a
/// quote: raw `"""…"""` (C#, Swift), verbatim `@"…"` (`""` escapes, also `$@` / `@$`), regular
/// `"…"`, char / single-quoted `'…'` and template `` `…` `` literals. Each is matched from its
/// opening quote, so a comment marker inside one never starts a comment. A block comment keeps
/// its line breaks so line numbers stay put.
const LITERAL_OR_COMMENT =
  /("""[\s\S]*?""")|((?:\$@|@\$?)"(?:[^"]|"")*")|("(?:[^"\\\n]|\\.)*")|('(?:[^'\\\n]|\\.)+')|(`(?:[^`\\]|\\.)*`)|\/\/[^\n]*|\/\*[\s\S]*?\*\//g;
function stripComments(source) {
  return source.replace(LITERAL_OR_COMMENT, (match, raw, verbatim, plain, char, template) =>
    raw ?? verbatim ?? plain ?? char ?? template ?? match.replace(/[^\n]/g, ''));
}

function readText(relative) {
  return fs.readFileSync(path.join(ROOT, relative), 'utf8');
}

function readBudget(relative) {
  const absolute = path.join(ROOT, relative);
  if (!fs.existsSync(absolute)) return null;
  return JSON.parse(fs.readFileSync(absolute, 'utf8'));
}

const failures = [];
const fail = (message) => failures.push(message);
const sum = (values) => values.reduce((total, value) => total + value, 0);

// ── 1. Colour literals (Windows) ──────────────────────────────────────────────
const COLOUR_BUDGET = 'scripts/design-token-budget.json';
const COLOUR_ROOTS = ['native/windows/MightyClaude.WinUI', 'native/windows/MightyClaude.Core'];

// Each pattern counts in every file unless it names its `files`.
const COLOUR_PATTERNS = [
  { pattern: /\bColor\.FromArgb\b/g },
  { pattern: /\bColorHelper\.FromArgb\b/g },
  { pattern: /\bColors\.(?!Transparent\b)[A-Z]\w*/g },
  { pattern: /"#(?:[0-9A-Fa-f]{8}|[0-9A-Fa-f]{6})"/g },
  { pattern: /\b0x[0-9A-Fa-f]{6}\b/g },
  { pattern: /\bRgb\(\s*[.\d]/g },
  { pattern: /(?<!\|\s*)\b0x00[0-9A-Fa-f]{6}u?\b(?!\s*\|)/g, files: ['native/windows/MightyClaude.WinUI/CompanionOverlay.cs'] },
];

function countColours(relative) {
  const source = stripComments(readText(relative));
  return COLOUR_PATTERNS.filter(({ files }) => !files || files.includes(relative))
    .reduce((total, { pattern }) => total + (source.match(pattern)?.length ?? 0), 0);
}

const colourBudget = readBudget(COLOUR_BUDGET);
if (!colourBudget) fail(`${COLOUR_BUDGET} is missing.`);
const colourExempt = new Set(Object.keys(colourBudget?.exempt ?? {}));
const colourAllowed = colourBudget?.files ?? {};
const colourCounts = {};
for (const root of COLOUR_ROOTS) {
  for (const relative of walk(root, ['.cs'], []).sort()) {
    if (colourExempt.has(relative)) continue;
    const value = countColours(relative);
    if (value > 0) colourCounts[relative] = value;
  }
}
const colourOver = [];
for (const [file, value] of Object.entries(colourCounts)) {
  const limit = colourAllowed[file] ?? 0;
  if (value > limit) colourOver.push(`${file}: ${value} colour literals, budget ${limit}. Use a design-token brush (DesignBrushes / DesignTokens) instead.`);
}
const colourTotal = sum(Object.values(colourCounts));
const colourBudgetTotal = sum(Object.values(colourAllowed));
failures.push(...colourOver);
if (colourBudget && WRITE && colourOver.length === 0) {
  const files = Object.fromEntries(Object.entries(colourCounts).sort(([a], [b]) => a.localeCompare(b)));
  fs.writeFileSync(path.join(ROOT, COLOUR_BUDGET), JSON.stringify({ ...colourBudget, files }, null, 2) + '\n');
  console.log(`design-token budget written: ${colourTotal} colour literals in ${Object.keys(files).length} files (was ${colourBudgetTotal}).`);
} else if (colourBudget) {
  for (const [file, limit] of Object.entries(colourAllowed)) {
    if ((colourCounts[file] ?? 0) < limit) {
      fail(`${file}: ${colourCounts[file] ?? 0} colour literals, budget ${limit}. Lower the budget: node scripts/check-design-tokens.js --write`);
    }
  }
  console.log(`colour literals: ${colourTotal} in ${Object.keys(colourCounts).length} files (budget ${colourBudgetTotal}, exempt ${colourExempt.size} files).`);
}

// ── 2. Spacing literals (macOS, Windows, phone) ──────────────────────────────
const SPACING_BUDGET = 'scripts/spacing-literal-budget.json';
const NUMBER = String.raw`(-?\d+(?:\.\d+)?)`;
const LITERAL_SPACING = (value) => Math.abs(Number(value)) >= 2;

/// One count per raw number at least 2 where a spacing goes. A pattern's first group is the
/// number; `each` instead lists the numbers inside the match's first group (Thickness arguments).
const SPACING_AREAS = [
  {
    label: 'macOS',
    roots: ['native/macos/Sources/MightyClaude', 'native/macos/Sources/MightyCore'],
    extensions: ['.swift'],
    patterns: [
      { pattern: new RegExp(String.raw`\.padding\(\s*${NUMBER}\s*\)`, 'g') },
      { pattern: new RegExp(String.raw`\.padding\(\s*(?:\.\w+|\[[^\]\n]*\])\s*,\s*${NUMBER}\s*\)`, 'g') },
      { pattern: new RegExp(String.raw`\bspacing:\s*${NUMBER}(?![\w.])`, 'g') },
    ],
  },
  {
    label: 'Windows',
    roots: ['native/windows/MightyClaude.WinUI', 'native/windows/MightyClaude.Core'],
    extensions: ['.cs'],
    patterns: [
      { pattern: /\bnew\s+(?:[\w.]+\.)?Thickness\(([^()]*)\)/g, each: true },
      // Not a `const double Spacing = 18;` declaration: that is a named constant, which is the goal.
      { pattern: new RegExp(String.raw`(?<!\bconst\s+\w+\s+)\b(?:Row|Column)?Spacing\s*=(?!=)\s*${NUMBER}(?![\w.])`, 'g') },
    ],
  },
  {
    label: 'phone',
    roots: ['mobile/src', 'mobile/app'],
    extensions: ['.ts', '.tsx'],
    patterns: [
      { pattern: new RegExp(String.raw`\b(?:padding|margin)[A-Za-z]*\s*:\s*${NUMBER}(?![\w.])`, 'g') },
      { pattern: new RegExp(String.raw`\b(?:gap|rowGap|columnGap)\s*:\s*${NUMBER}(?![\w.])`, 'g') },
    ],
  },
];

function countSpacing(area, source) {
  let total = 0;
  for (const { pattern, each } of area.patterns) {
    for (const match of source.matchAll(pattern)) {
      if (each) {
        // Only bare numbers count: a token expression (DesignMetrics.Spacing.Md) or arithmetic is not one.
        for (const argument of match[1].split(',')) {
          const trimmed = argument.trim();
          if (/^-?\d+(?:\.\d+)?$/.test(trimmed) && LITERAL_SPACING(trimmed)) total += 1;
        }
      } else if (LITERAL_SPACING(match[1])) {
        total += 1;
      }
    }
  }
  return total;
}

const REGION_BEGIN = '// spacing-exempt-begin';
const REGION_END = '// spacing-exempt-end';

/// Blanks the `// spacing-exempt-begin: <reason>` … `// spacing-exempt-end` regions (keeping
/// their line breaks) and returns what is left, with how many regions it found.
function dropExemptRegions(relative, source) {
  if (!source.includes('spacing-exempt-')) return { counted: source, regions: 0 };
  let counted = '';
  let rest = source;
  let regions = 0;
  for (;;) {
    const begin = rest.indexOf(REGION_BEGIN);
    const stray = rest.indexOf(REGION_END);
    if (stray >= 0 && (begin < 0 || stray < begin)) {
      fail(`${relative}: ${REGION_END} has no ${REGION_BEGIN} before it.`);
      break;
    }
    if (begin < 0) break;
    const lineEnd = rest.indexOf('\n', begin);
    const marker = rest.slice(begin, lineEnd < 0 ? rest.length : lineEnd);
    if (!/^\/\/ spacing-exempt-begin: \S/.test(marker)) fail(`${relative}: ${REGION_BEGIN} needs ": <reason>".`);
    const end = rest.indexOf(REGION_END, begin + REGION_BEGIN.length);
    if (end < 0) {
      fail(`${relative}: ${REGION_BEGIN} is never closed by ${REGION_END}.`);
      break;
    }
    counted += rest.slice(0, begin) + rest.slice(begin, end + REGION_END.length).replace(/[^\n]/g, '');
    rest = rest.slice(end + REGION_END.length);
    regions += 1;
  }
  return { counted: counted + rest, regions };
}

function globPattern(glob) {
  const escaped = glob.split(/(\*\*|\*)/).map((part) => {
    if (part === '**') return '.*';
    if (part === '*') return '[^/]*';
    return part.replace(/[.+?^${}()|[\]\\]/g, '\\$&');
  });
  return new RegExp('^' + escaped.join('') + '$');
}

const spacingBudget = readBudget(SPACING_BUDGET);
if (!spacingBudget || typeof spacingBudget.areas !== 'object' || spacingBudget.areas === null) {
  fail(`${SPACING_BUDGET} is missing or has no areas object.`);
} else {
  const exemptGlobs = Object.entries(spacingBudget.exemptFiles ?? {}).map(([glob, reason]) => ({
    glob,
    reason,
    pattern: globPattern(glob),
    matched: 0,
  }));
  const report = [];
  for (const area of SPACING_AREAS) {
    const files = {};
    let exemptLiterals = 0;
    let regions = 0;
    for (const root of area.roots) {
      for (const relative of walk(root, area.extensions, []).sort()) {
        const exemptBy = exemptGlobs.filter((entry) => entry.pattern.test(relative));
        for (const entry of exemptBy) entry.matched += 1;
        const raw = readText(relative).replace(/\r\n/g, '\n');
        const source = stripComments(raw);
        if (exemptBy.length > 0) {
          exemptLiterals += countSpacing(area, source);
          continue;
        }
        // Regions are found on the raw text (their markers are comments), then comments go.
        const split = dropExemptRegions(relative, raw);
        regions += split.regions;
        const value = countSpacing(area, stripComments(split.counted));
        exemptLiterals += countSpacing(area, source) - value;
        if (value > 0) files[relative] = value;
      }
    }
    report.push({ label: area.label, count: sum(Object.values(files)), files, exemptLiterals, regions, budget: spacingBudget.areas[area.label] });
  }

  for (const entry of exemptGlobs) {
    if (entry.matched === 0) fail(`${SPACING_BUDGET}: exemptFiles ${entry.glob} matches no file.`);
    if (!entry.reason) fail(`${SPACING_BUDGET}: exemptFiles ${entry.glob} gives no reason.`);
  }
  for (const label of Object.keys(spacingBudget.areas)) {
    if (!SPACING_AREAS.some((area) => area.label === label)) fail(`${SPACING_BUDGET}: unknown area ${label}.`);
  }

  const before = failures.length;
  const stale = [];
  for (const row of report) {
    if (typeof row.budget !== 'number') {
      if (WRITE) row.budget = row.count; // a new area starts at today's count
      else fail(`${SPACING_BUDGET}: no budget for ${row.label}.`);
    } else if (row.count > row.budget) {
      const top = Object.entries(row.files).sort(([, a], [, b]) => b - a).slice(0, 10)
        .map(([file, value]) => `    ${value}  ${file}`).join('\n');
      fail(`${row.label}: ${row.count} raw spacing literals, budget ${row.budget}. Use the spacing scale or a named inset (DesignMetrics / theme spacing) instead.\n${top}`);
    } else if (row.count < row.budget) {
      stale.push(row);
    }
  }
  if (WRITE && failures.length === before) {
    const areas = Object.fromEntries(report.map((row) => [row.label, Math.min(row.budget, row.count)]));
    fs.writeFileSync(path.join(ROOT, SPACING_BUDGET), JSON.stringify({ ...spacingBudget, areas }, null, 2) + '\n');
    for (const row of report) row.budget = areas[row.label];
    stale.length = 0;
    console.log(`spacing-literal budget written: ${Object.entries(areas).map(([label, value]) => `${label} ${value}`).join(', ')}.`);
  }
  for (const row of stale) {
    fail(`${row.label}: ${row.count} raw spacing literals, budget ${row.budget}. Lower the budget: node scripts/check-design-tokens.js --write-budget`);
  }
  console.log('spacing literals per area:');
  for (const row of report) {
    console.log(`  ${row.label.padEnd(8)} ${row.count} / budget ${row.budget ?? '-'} in ${Object.keys(row.files).length} files (exempt: ${row.exemptLiterals} literals, ${row.regions} regions)`);
  }
}

// ── 3. Font literals (Windows WinUI) ─────────────────────────────────────────
const FONT_BUDGET = 'scripts/font-literal-budget.json';
const FONT_ROOT = 'native/windows/MightyClaude.WinUI';
const FONT_PATTERNS = [
  // An assignment, compound ones too (+=, *=), but not a comparison.
  /\b(?:FontSize|LineHeight|FontFamily)\s*[-+*/]?=(?!=)/g,
  // SetValue, ClearValue or a Setter on the property itself.
  /\b(?:FontSize|LineHeight|FontFamily)Property\b/g,
  /\bnew\s+(?:[\w.]+\.)?FontFamily\s*\(/g,
];

const fontBudget = readBudget(FONT_BUDGET);
if (!fontBudget) {
  fail(`${FONT_BUDGET} is missing.`);
} else {
  const exemptGlobs = Object.entries(fontBudget.exemptFiles ?? {}).map(([glob, reason]) => ({ glob, reason, pattern: globPattern(glob), matched: 0 }));
  const allowed = fontBudget.files ?? {};
  const counts = {};
  for (const relative of walk(FONT_ROOT, ['.cs'], []).sort()) {
    const exemptBy = exemptGlobs.filter((entry) => entry.pattern.test(relative));
    for (const entry of exemptBy) entry.matched += 1;
    if (exemptBy.length > 0) continue;
    const source = stripComments(readText(relative));
    const value = FONT_PATTERNS.reduce((total, pattern) => total + (source.match(pattern)?.length ?? 0), 0);
    if (value > 0) counts[relative] = value;
  }
  for (const entry of exemptGlobs) {
    if (entry.matched === 0) fail(`${FONT_BUDGET}: exemptFiles ${entry.glob} matches no file.`);
    if (!entry.reason) fail(`${FONT_BUDGET}: exemptFiles ${entry.glob} gives no reason.`);
  }
  const over = [];
  for (const [file, value] of Object.entries(counts)) {
    const limit = allowed[file] ?? 0;
    if (value > limit) over.push(`${file}: ${value} font literals, budget ${limit}. Give the size at scale 1 as TextSize (TextLineHeight, TextHeight) and the font as TextFont, so the text setting reaches it (AppText.cs).`);
  }
  failures.push(...over);
  const total = sum(Object.values(counts));
  const budgetTotal = sum(Object.values(allowed));
  if (WRITE && over.length === 0) {
    const files = Object.fromEntries(Object.entries(counts).sort(([a], [b]) => a.localeCompare(b)));
    fs.writeFileSync(path.join(ROOT, FONT_BUDGET), JSON.stringify({ ...fontBudget, files }, null, 2) + '\n');
    console.log(`font-literal budget written: ${total} font literals in ${Object.keys(files).length} files (was ${budgetTotal}).`);
  } else {
    for (const [file, limit] of Object.entries(allowed)) {
      if ((counts[file] ?? 0) < limit) fail(`${file}: ${counts[file] ?? 0} font literals, budget ${limit}. Lower the budget: node scripts/check-design-tokens.js --write`);
    }
    console.log(`font literals (WinUI): ${total} in ${Object.keys(counts).length} files (budget ${budgetTotal}, exempt ${exemptGlobs.length} globs).`);
  }
}

// ── 4. Motion tokens (macOS, Windows, web, phone) ────────────────────────────
// Not a ratchet: the shape of design-tokens.json `motion`, which DesignMetrics (Swift, C#), the
// web theme/tokens and the phone theme mirror key for key. Durations are whole milliseconds;
// `easing` is a cubic-bezier [x1, y1, x2, y2] whose x values lie in [0, 1]. The display cases of
// native/contracts/fixtures/pending.json are computed with pendingShowAfter and pendingMinVisible,
// so they are checked against them here (docs/interaction.md).
const TOKENS_FILE = 'native/contracts/fixtures/design-tokens.json';
const PENDING_FILE = 'native/contracts/fixtures/pending.json';
const MOTION_DURATIONS = ['quick', 'standard', 'scroll', 'pendingShowAfter', 'pendingMinVisible', 'highlight'];
const MOTION_KEYS = [...MOTION_DURATIONS, 'easing'];

/// The pending display rule (docs/interaction.md): shown once pending has lasted showAfter, kept at
/// least minVisible once shown, never shown when it ended by showAfter, and `failed` shows at once.
function pendingVisible({ startedAt, endedAt, now, failed }, showAfter, minVisible) {
  if (failed) return now >= (endedAt ?? startedAt);
  const shownAt = startedAt + showAfter;
  if (now < shownAt) return false;
  if (endedAt === null) return true;
  if (endedAt <= shownAt) return false;
  return now < endedAt || now <= shownAt + minVisible;
}

const tokensPresent = fs.existsSync(path.join(ROOT, TOKENS_FILE));
if (!tokensPresent && rootIndex < 0) fail(`${TOKENS_FILE} is missing.`);
if (tokensPresent) {
  const motion = readBudget(TOKENS_FILE).motion;
  const before = failures.length;
  if (typeof motion !== 'object' || motion === null || Array.isArray(motion)) {
    fail(`${TOKENS_FILE}: no motion object.`);
  } else {
    const keys = Object.keys(motion);
    for (const key of MOTION_KEYS) if (!keys.includes(key)) fail(`${TOKENS_FILE}: motion.${key} is missing.`);
    for (const key of keys) if (!MOTION_KEYS.includes(key)) fail(`${TOKENS_FILE}: motion.${key} is not a motion token (${MOTION_KEYS.join(', ')}).`);
    for (const key of MOTION_DURATIONS) {
      if (key in motion && !(Number.isInteger(motion[key]) && motion[key] >= 0)) fail(`${TOKENS_FILE}: motion.${key} must be whole milliseconds, not ${JSON.stringify(motion[key])}.`);
    }
    const easing = motion.easing;
    if ('easing' in motion) {
      if (!Array.isArray(easing) || easing.length !== 4 || !easing.every((value) => typeof value === 'number' && Number.isFinite(value))) {
        fail(`${TOKENS_FILE}: motion.easing must be four numbers [x1, y1, x2, y2], not ${JSON.stringify(easing)}.`);
      } else if (![easing[0], easing[2]].every((x) => x >= 0 && x <= 1)) {
        fail(`${TOKENS_FILE}: motion.easing x1 and x2 must lie in [0, 1], not ${JSON.stringify(easing)}.`);
      }
    }
  }
  if (failures.length === before && fs.existsSync(path.join(ROOT, PENDING_FILE))) {
    const display = readBudget(PENDING_FILE).display ?? [];
    if (!Array.isArray(display) || display.length === 0) fail(`${PENDING_FILE}: no display cases.`);
    for (const [index, entry] of (Array.isArray(display) ? display : []).entries()) {
      const got = pendingVisible({ ...entry, endedAt: entry.endedAt ?? null }, motion.pendingShowAfter, motion.pendingMinVisible);
      if (got !== entry.expectVisible) fail(`${PENDING_FILE}: display[${index}] (${entry.name ?? 'unnamed'}) expects ${entry.expectVisible}, the rule with the motion tokens gives ${got}.`);
    }
  } else if (failures.length === before && rootIndex < 0) {
    fail(`${PENDING_FILE} is missing.`);
  }
  if (failures.length === before) console.log(`motion tokens: ${MOTION_KEYS.length} keys hold; pending display cases agree with them.`);
}

if (failures.length > 0) {
  for (const message of failures) console.error('✗ ' + message);
  if (WRITE) console.error('--write never raises a budget.');
  process.exit(1);
}
console.log('✓ design-token ratchets hold.');
