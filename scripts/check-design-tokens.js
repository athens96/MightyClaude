#!/usr/bin/env node
// Usage: node scripts/check-design-tokens.js           # check the counts against the budget
//        node scripts/check-design-tokens.js --check   # the same
//        node scripts/check-design-tokens.js --write   # lower the budget to today's counts
//
// The colour-literal ratchet for the Windows design conversion
// (.omc/plans/windows-design-conversion.md, stage 1). Every surface, border, ink and
// accent on Windows is meant to read a design-token brush (WinUI DesignBrushes, Core
// DesignTokens). This counts what still bypasses them, per file of the Windows Core
// and WinUI sources:
//
//   Color.FromArgb(…)  ColorHelper.FromArgb(…)  Colors.X (except Colors.Transparent)  "#RRGGBB"
//   0xRRGGBB (a six-digit int is an RGB colour here)  Rgb(.r, .g, .b) calls (StatusLine's ANSI palette)
//   0x00BBGGRR GDI COLORREF inks in CompanionOverlay.cs (not the window-style flags OR-ed with |)
//
// Files on the budget's exempt list (with its reason) are not counted.
// scripts/design-token-budget.json holds the committed count per file. The check fails
// when a file has more than its budget (a new literal), when a file outside the budget
// has any, and when a file has fewer than its budget (the budget is stale: lower it with
// --write, so the count can only go down). --write never raises a count.
// Comments are not counted. Plain node only, no dependencies.

import fs from 'node:fs';
import path from 'node:path';
import process from 'node:process';

const ROOT = path.resolve(import.meta.dirname, '..');
const BUDGET = 'scripts/design-token-budget.json';
const SOURCE_ROOTS = ['native/windows/MightyClaude.WinUI', 'native/windows/MightyClaude.Core'];
const SKIP_DIRECTORIES = new Set(['bin', 'obj']);
const argv = process.argv.slice(2);
const WRITE = argv.includes('--write');

// Each pattern counts in every file unless it names its `files`.
const PATTERNS = [
  { pattern: /\bColor\.FromArgb\b/g },
  { pattern: /\bColorHelper\.FromArgb\b/g },
  { pattern: /\bColors\.(?!Transparent\b)[A-Z]\w*/g },
  { pattern: /"#(?:[0-9A-Fa-f]{8}|[0-9A-Fa-f]{6})"/g },
  { pattern: /\b0x[0-9A-Fa-f]{6}\b/g },
  { pattern: /\bRgb\(\s*[.\d]/g },
  { pattern: /(?<!\|\s*)\b0x00[0-9A-Fa-f]{6}u?\b(?!\s*\|)/g, files: ['native/windows/MightyClaude.WinUI/CompanionOverlay.cs'] },
];

function walk(relativeRoot, out) {
  const absolute = path.join(ROOT, relativeRoot);
  if (!fs.existsSync(absolute)) return out;
  for (const entry of fs.readdirSync(absolute, { withFileTypes: true })) {
    const relative = path.posix.join(relativeRoot, entry.name);
    if (entry.isDirectory()) {
      if (!SKIP_DIRECTORIES.has(entry.name)) walk(relative, out);
    } else if (entry.name.endsWith('.cs')) {
      out.push(relative);
    }
  }
  return out;
}

/// Drops `//` and `/* */` comments while keeping every C# literal that may hold `//`, `/*` or a
/// quote: raw `"""…"""`, verbatim `@"…"` (`""` escapes, also `$@` / `@$`), regular `"…"` and
/// char `'…'` literals. Each is matched from its opening quote, so a comment marker inside one
/// never starts a comment.
const LITERAL_OR_COMMENT =
  /("""[\s\S]*?""")|((?:\$@|@\$?)"(?:[^"]|"")*")|("(?:[^"\\\n]|\\.)*")|('(?:[^'\\\n]|\\.)+')|\/\/[^\n]*|\/\*[\s\S]*?\*\//g;
function stripComments(source) {
  return source.replace(LITERAL_OR_COMMENT, (match, raw, verbatim, plain, char) => raw ?? verbatim ?? plain ?? char ?? '');
}

function count(relative) {
  const source = stripComments(fs.readFileSync(path.join(ROOT, relative), 'utf8'));
  return PATTERNS.filter(({ files }) => !files || files.includes(relative))
    .reduce((total, { pattern }) => total + (source.match(pattern)?.length ?? 0), 0);
}

const budget = JSON.parse(fs.readFileSync(path.join(ROOT, BUDGET), 'utf8'));
const exempt = new Set(Object.keys(budget.exempt ?? {}));
const allowed = budget.files ?? {};
const counts = {};
for (const root of SOURCE_ROOTS) {
  for (const relative of walk(root, []).sort()) {
    if (exempt.has(relative)) continue;
    const value = count(relative);
    if (value > 0) counts[relative] = value;
  }
}

const failures = [];
for (const [file, value] of Object.entries(counts)) {
  const limit = allowed[file] ?? 0;
  if (value > limit) failures.push(`${file}: ${value} colour literals, budget ${limit}. Use a design-token brush (DesignBrushes / DesignTokens) instead.`);
}
const stale = Object.entries(allowed).filter(([file, limit]) => (counts[file] ?? 0) < limit);

const total = Object.values(counts).reduce((sum, value) => sum + value, 0);
const budgetTotal = Object.values(allowed).reduce((sum, value) => sum + value, 0);

if (WRITE) {
  if (failures.length > 0) {
    for (const message of failures) console.error('✗ ' + message);
    console.error('--write never raises the budget.');
    process.exit(1);
  }
  const files = Object.fromEntries(Object.entries(counts).sort(([a], [b]) => a.localeCompare(b)));
  fs.writeFileSync(path.join(ROOT, BUDGET), JSON.stringify({ ...budget, files }, null, 2) + '\n');
  console.log(`design-token budget written: ${total} colour literals in ${Object.keys(files).length} files (was ${budgetTotal}).`);
  process.exit(0);
}

for (const [file, limit] of stale) {
  failures.push(`${file}: ${counts[file] ?? 0} colour literals, budget ${limit}. Lower the budget: node scripts/check-design-tokens.js --write`);
}
console.log(`colour literals: ${total} in ${Object.keys(counts).length} files (budget ${budgetTotal}, exempt ${exempt.size} files).`);
if (failures.length > 0) {
  for (const message of failures) console.error('✗ ' + message);
  process.exit(1);
}
console.log('✓ design-token ratchet holds.');
