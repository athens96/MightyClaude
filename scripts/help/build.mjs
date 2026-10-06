#!/usr/bin/env node
// Usage: node scripts/help/build.mjs                    # check, then write docs/help/site/
//        node scripts/help/build.mjs --check            # check only, write nothing
//        node scripts/help/build.mjs --shots <dir>      # also encode the CI screenshots
//        node scripts/help/build.mjs --root <dir>       # another repository root (fixture tests)
//        node scripts/help/build.mjs --out <dir>        # another output folder
//
// Builds the user help site from docs/help/content/<lang>/*.md: one page per language with a
// sidebar (the layout of docs/design-system/index.html), plus a root index.html that picks a
// language from navigator.language. Plain node, no dependencies; images go through `cwebp`
// when it is installed. docs/help/README.md describes the content conventions.
//
// --shots takes the unzipped CI artifact (MightyClaude-macos-help-shots). The capture writes
// <profile>/help-shots/<lang>/<id>-<theme>.png and the artifact keeps the path below the
// runner's temp folder, so these layouts are all accepted, tried in this order:
//   <dir>/<lang>/<id>-<theme>.png
//   <dir>/help-shots/<lang>/<id>-<theme>.png
//   <dir>/mighty-help-<lang>/help-shots/<lang>/<id>-<theme>.png
// A picture missing for a language falls back to the Korean one, then to a placeholder.
// Without --shots, pictures already encoded in the output folder are kept and used.
//
// Checks (any failure exits 1): unknown shot id, unknown ui key (or one with {placeholders}),
// a language whose files, front matter, headings, shots or ui keys differ from ko, a heading
// without an {#id}, and internal links to a missing page or heading.

import fs from 'node:fs';
import path from 'node:path';
import process from 'node:process';
import { spawnSync } from 'node:child_process';

// ── Arguments ──────────────────────────────────────────────────────────────────
const argv = process.argv.slice(2);
function option(name) {
  const index = argv.indexOf(name);
  if (index < 0) return null;
  const value = argv[index + 1];
  if (!value || value.startsWith('--')) fail(`${name} needs a value`);
  return value;
}
function fail(message) {
  console.error(`help: ${message}`);
  process.exit(2);
}
const CHECK_ONLY = argv.includes('--check');
const ROOT = path.resolve(option('--root') ?? path.join(import.meta.dirname, '..', '..'));
const OUT = path.resolve(option('--out') ?? path.join(ROOT, 'docs/help/site'));
const SHOTS_DIR = option('--shots') ? path.resolve(option('--shots')) : null;
if (SHOTS_DIR && !fs.existsSync(SHOTS_DIR)) fail(`--shots folder not found: ${SHOTS_DIR}`);

const CONTENT = path.join(ROOT, 'docs/help/content');
const MASTER = 'ko';
const LANGUAGES = [
  { code: 'ko', name: '한국어', html: 'ko' },
  { code: 'en', name: 'English', html: 'en' },
  { code: 'zh', name: '简体中文', html: 'zh-Hans' },
  { code: 'ja', name: '日本語', html: 'ja' },
];
const PLATFORMS = { mac: 'macOS', windows: 'Windows', phone: 'Phone' };
const CALLOUTS = ['note', 'tip', 'warning'];
const DEFAULT_THEMES = ['light', 'dark'];
const THUMB_WIDTH = 1280;

const errors = [];
const warnings = [];
const rel = (file) => path.relative(ROOT, file);
const error = (where, message) => errors.push(`${where}: ${message}`);

// ── Inputs ─────────────────────────────────────────────────────────────────────
function readJSON(file) {
  try {
    return JSON.parse(fs.readFileSync(file, 'utf8'));
  } catch (cause) {
    fail(`cannot read ${rel(file)}: ${cause.message}`);
  }
}

const shotList = readJSON(path.join(ROOT, 'docs/help/shots.json'));
const SECTIONS = shotList.sections ?? [];
const SHOTS = new Map((shotList.shots ?? []).map((shot) => [shot.id, shot]));
const VERSION = fs.existsSync(path.join(ROOT, 'VERSION'))
  ? fs.readFileSync(path.join(ROOT, 'VERSION'), 'utf8').trim()
  : '';

const present = LANGUAGES.filter((language) => fs.existsSync(path.join(CONTENT, language.code)));
if (!present.some((language) => language.code === MASTER)) fail(`docs/help/content/${MASTER} is missing`);

// ── Markdown: front matter and blocks ──────────────────────────────────────────
// The subset the help pages use: front matter, ## / ### headings with {#id}, paragraphs,
// - and 1. lists (nested by indentation), | tables |, > [!note] callouts, ``` code,
// and ![[shot-id]] on a line of its own.

function parseFrontMatter(text, where) {
  const match = /^---\n([\s\S]*?)\n---\n/.exec(text);
  if (!match) {
    error(where, 'front matter (--- title / order / section ---) is missing');
    return { meta: {}, body: text, offset: 0 };
  }
  const meta = {};
  for (const line of match[1].split('\n')) {
    const pair = /^(\w+):\s*(.*)$/.exec(line);
    if (pair) meta[pair[1]] = pair[2].replace(/^"(.*)"$/, '$1').trim();
  }
  return { meta, body: text.slice(match[0].length), offset: match[0].split('\n').length - 1 };
}

const LIST_ITEM = /^(\s*)([-*]|\d+\.)\s+(.*)$/;
const HEADING = /^(#{1,6})\s+(.*)$/;
const SHOT = /^!\[\[([^\]|]+)(?:\|([^\]]*))?\]\]\s*$/;

function indentOf(line) {
  return /^\s*/.exec(line)[0].length;
}

/** lines: [{ text, line }] → blocks */
function parseBlocks(lines, where) {
  const blocks = [];
  let index = 0;
  const at = (i) => `${where}:${lines[i].line}`;
  const startsBlock = (text) =>
    HEADING.test(text) || SHOT.test(text.trim()) || /^\s*>/.test(text) || /^\s*\|/.test(text) ||
    /^\s*```/.test(text) || LIST_ITEM.test(text);

  while (index < lines.length) {
    const { text } = lines[index];
    if (!text.trim()) { index++; continue; }

    if (/^\s*```/.test(text)) {
      const start = index++;
      const code = [];
      while (index < lines.length && !/^\s*```/.test(lines[index].text)) code.push(lines[index++].text);
      if (index >= lines.length) error(at(start), 'code block is not closed');
      index++;
      blocks.push({ type: 'code', text: code.join('\n') });
      continue;
    }

    const heading = HEADING.exec(text);
    if (heading) {
      const level = heading[1].length;
      const idMatch = /^(.*?)\s*\{#([a-z0-9][a-z0-9-]*)\}\s*$/.exec(heading[2]);
      if (level !== 2 && level !== 3) error(at(index), `only ## and ### headings are allowed (the page title comes from the front matter)`);
      if (!idMatch) error(at(index), `heading needs an id: "${heading[2]} {#some-id}"`);
      blocks.push({ type: 'heading', level, text: idMatch ? idMatch[1] : heading[2], id: idMatch ? idMatch[2] : '', line: lines[index].line });
      index++;
      continue;
    }

    const shot = SHOT.exec(text.trim());
    if (shot) {
      blocks.push({ type: 'shot', id: shot[1].trim(), caption: shot[2]?.trim() ?? '', line: lines[index].line });
      index++;
      continue;
    }

    if (/^\s*>/.test(text)) {
      const inner = [];
      const start = index;
      while (index < lines.length && /^\s*>/.test(lines[index].text)) {
        inner.push({ text: lines[index].text.replace(/^\s*>\s?/, ''), line: lines[index].line });
        index++;
      }
      const kind = /^\[!(\w+)\]\s*(.*)$/.exec(inner[0].text);
      if (kind && !CALLOUTS.includes(kind[1])) error(at(start), `unknown callout [!${kind[1]}] (use ${CALLOUTS.join(', ')})`);
      if (kind) inner[0] = { ...inner[0], text: kind[2] };
      blocks.push({ type: 'callout', kind: kind ? kind[1] : 'quote', children: parseBlocks(inner, where) });
      continue;
    }

    if (/^\s*\|/.test(text)) {
      const rows = [];
      const start = index;
      while (index < lines.length && /^\s*\|/.test(lines[index].text)) rows.push(lines[index++]);
      const cells = (row) => row.text.trim().replace(/^\||\|$/g, '').split('|').map((cell) => cell.trim());
      if (rows.length < 2 || !/^\s*\|?[\s:|-]+\|?\s*$/.test(rows[1].text)) {
        error(at(start), 'a table needs a header row and a |---| separator row');
        continue;
      }
      const head = cells(rows[0]);
      const body = rows.slice(2).map(cells);
      for (const [i, row] of body.entries()) {
        if (row.length !== head.length) error(`${where}:${rows[i + 2].line}`, `table row has ${row.length} cells, the header has ${head.length}`);
      }
      blocks.push({ type: 'table', head, body, lines: rows.map((row) => row.line) });
      continue;
    }

    const item = LIST_ITEM.exec(text);
    if (item) {
      const baseIndent = item[1].length;
      const ordered = /\d/.test(item[2]);
      const items = [];
      while (index < lines.length) {
        const current = LIST_ITEM.exec(lines[index].text);
        if (!current || current[1].length !== baseIndent || /\d/.test(current[2]) !== ordered) break;
        const contentIndent = current[1].length + current[2].length + 1;
        const childLines = [{ text: current[3], line: lines[index].line }];
        index++;
        while (index < lines.length && lines[index].text.trim() && indentOf(lines[index].text) > baseIndent) {
          const line = lines[index].text;
          childLines.push({ text: line.slice(Math.min(indentOf(line), contentIndent)), line: lines[index].line });
          index++;
        }
        items.push(parseBlocks(childLines, where));
      }
      blocks.push({ type: ordered ? 'ol' : 'ul', items });
      continue;
    }

    const paragraph = [];
    const line = lines[index].line;
    while (index < lines.length && lines[index].text.trim() && (paragraph.length === 0 || !startsBlock(lines[index].text))) {
      paragraph.push(lines[index++].text.trim());
    }
    blocks.push({ type: 'p', text: paragraph.join(' '), line });
  }
  return blocks;
}

// ── Reading one language ───────────────────────────────────────────────────────
function walk(blocks, visit) {
  for (const block of blocks) {
    visit(block);
    if (block.children) walk(block.children, visit);
    if (block.items) for (const item of block.items) walk(item, visit);
  }
}

/** Inline text of a block tree, for collecting ui keys and links before rendering. */
function inlineTexts(blocks) {
  const texts = [];
  walk(blocks, (block) => {
    if (block.type === 'p' || block.type === 'heading') texts.push(block.text);
    if (block.type === 'table') texts.push(...block.head, ...block.body.flat());
  });
  return texts;
}

function readLanguage(code) {
  const folder = path.join(CONTENT, code);
  const files = fs.readdirSync(folder).filter((name) => name.endsWith('.md')).sort();
  const pages = [];
  for (const name of files) {
    const file = path.join(folder, name);
    const where = rel(file);
    const { meta, body, offset } = parseFrontMatter(fs.readFileSync(file, 'utf8').replace(/\r\n/g, '\n'), where);
    const lines = body.split('\n').map((text, i) => ({ text, line: i + 1 + offset }));
    const blocks = parseBlocks(lines, where);
    const slug = name.replace(/\.md$/, '');
    for (const field of ['title', 'order', 'section']) if (!meta[field]) error(where, `front matter needs "${field}"`);
    if (meta.section && meta.section !== slug.replace(/^\d+-/, '')) error(where, `section "${meta.section}" does not match the file name`);
    if (meta.section && !SECTIONS.includes(meta.section)) error(where, `section "${meta.section}" is not in docs/help/shots.json sections`);
    const headings = [];
    const shots = [];
    walk(blocks, (block) => {
      if (block.type === 'heading') headings.push(block);
      if (block.type === 'shot') shots.push(block);
    });
    const seen = new Set();
    for (const heading of headings) {
      if (heading.id && seen.has(heading.id)) error(`${where}:${heading.line}`, `heading id #${heading.id} is used twice`);
      seen.add(heading.id);
    }
    for (const shot of shots) {
      if (!SHOTS.has(shot.id)) error(`${where}:${shot.line}`, `unknown shot id "${shot.id}" (not in docs/help/shots.json)`);
    }
    const texts = inlineTexts(blocks);
    const uiKeys = texts.flatMap((text) => [...text.matchAll(/\{\{ui:([^}]+)\}\}/g)].map((m) => m[1].trim()));
    pages.push({ name, slug, where, meta, blocks, headings, shots, uiKeys, order: Number(meta.order) });
  }
  pages.sort((a, b) => a.order - b.order);
  const orders = new Set();
  for (const page of pages) {
    if (!Number.isFinite(page.order)) error(page.where, `order "${page.meta.order}" is not a number`);
    if (orders.has(page.order)) error(page.where, `order ${page.order} is used twice`);
    orders.add(page.order);
  }
  const siteFile = path.join(folder, '_site.json');
  const site = fs.existsSync(siteFile) ? readJSON(siteFile) : (error(rel(folder), '_site.json is missing'), {});
  return { code, pages, site };
}

const languages = present.map((language) => ({ ...language, ...readLanguage(language.code) }));
const master = languages.find((language) => language.code === MASTER);

// ── Locales ────────────────────────────────────────────────────────────────────
const locales = Object.fromEntries(LANGUAGES.map(({ code }) => {
  const file = path.join(ROOT, 'locales', `${code}.json`);
  return [code, fs.existsSync(file) ? readJSON(file) : {}];
}));

/** The app's label: the language's own string, then en, then ko (the apps' fallback order). */
function uiLabel(code, key) {
  for (const source of [code, 'en', 'ko']) {
    const value = locales[source]?.[key];
    if (typeof value === 'string' && value.trim()) return value;
  }
  return null;
}

for (const language of languages) {
  for (const page of language.pages) {
    for (const key of new Set(page.uiKeys)) {
      if (!(key in locales[MASTER])) error(page.where, `unknown ui key "${key}" (not in locales/ko.json)`);
      else if (/\{[A-Za-z_]+\}/.test(uiLabel(language.code, key) ?? '')) error(page.where, `ui key "${key}" has a {placeholder}; pick a plain label`);
    }
  }
}

// A Korean particle right after an app label must match the label's last syllable, so a
// renamed label cannot quietly break the sentence (을/를, 이/가, 은/는, 과/와, 으로/로, 이나/나).
const PARTICLES = [['으로', '로'], ['이나', '나'], ['을', '를'], ['이', '가'], ['은', '는'], ['과', '와']];
for (const language of languages.filter((l) => l.code === 'ko')) {
  for (const page of language.pages) {
    for (const text of inlineTexts(page.blocks)) {
      for (const match of text.matchAll(/\{\{ui:([^}]+)\}\}(?:\(\{\{kbd:[^}]+\}\}\))?([가-힣]+)/g)) {
        const label = (uiLabel('ko', match[1].trim()) ?? '').replace(/[…\s.]+$/, '');
        const code = label.codePointAt(label.length - 1) ?? 0;
        if (code < 0xac00 || code > 0xd7a3) continue;
        const final = (code - 0xac00) % 28; // 0: no final consonant, 8: ㄹ
        const pair = PARTICLES.find(([a, b]) => match[2].startsWith(a) || match[2].startsWith(b));
        if (!pair) continue;
        const wantsFirst = pair[0] === '으로' ? final !== 0 && final !== 8 : final !== 0;
        if (wantsFirst !== match[2].startsWith(pair[0])) {
          error(page.where, `particle "${match[2]}" after "${label}" ({{ui:${match[1]}}}) should be "${wantsFirst ? pair[0] : pair[1]}…"`);
        }
      }
    }
  }
}

// ── Every language matches ko ──────────────────────────────────────────────────
const sorted = (list) => [...list].sort().join(', ');
function compareSite(a, b, where) {
  const missing = Object.keys(a).filter((key) => !(key in b));
  const extra = Object.keys(b).filter((key) => !(key in a));
  if (missing.length || extra.length) error(where, `_site.json keys differ from ${MASTER}: missing [${missing.join(', ')}], extra [${extra.join(', ')}]`);
}
for (const language of languages) {
  if (language === master) continue;
  const where = `docs/help/content/${language.code}`;
  compareSite(master.site, language.site, `${where}/_site.json`);
  const ours = language.pages.map((page) => page.name);
  const theirs = master.pages.map((page) => page.name);
  if (sorted(ours) !== sorted(theirs)) {
    error(where, `files differ from ${MASTER}: missing [${theirs.filter((n) => !ours.includes(n)).join(', ')}], extra [${ours.filter((n) => !theirs.includes(n)).join(', ')}]`);
  }
  for (const page of language.pages) {
    const reference = master.pages.find((other) => other.name === page.name);
    if (!reference) continue;
    for (const field of ['order', 'section']) {
      if (page.meta[field] !== reference.meta[field]) error(page.where, `front matter ${field} "${page.meta[field]}" differs from ${MASTER} "${reference.meta[field]}"`);
    }
    const outline = (p) => p.headings.map((h) => `${'#'.repeat(h.level)}${h.id}`).join(' ');
    if (outline(page) !== outline(reference)) {
      error(page.where, `heading structure differs from ${MASTER}:\n    ${MASTER}: ${outline(reference)}\n    ${language.code}: ${outline(page)}`);
    }
    const shotsOf = (p) => p.shots.map((s) => s.id).join(', ');
    if (shotsOf(page) !== shotsOf(reference)) error(page.where, `shots differ from ${MASTER}: [${shotsOf(page)}] vs [${shotsOf(reference)}]`);
    if (sorted(page.uiKeys) !== sorted(reference.uiKeys)) {
      const left = [...reference.uiKeys];
      const extra = [];
      for (const key of page.uiKeys) {
        const i = left.indexOf(key);
        if (i >= 0) left.splice(i, 1); else extra.push(key);
      }
      error(page.where, `ui keys differ from ${MASTER}: missing [${left.join(', ')}], extra [${extra.join(', ')}]`);
    }
  }
}

const usedShots = new Set(master.pages.flatMap((page) => page.shots.map((shot) => shot.id)));
for (const id of SHOTS.keys()) if (!usedShots.has(id)) warnings.push(`shot "${id}" is in shots.json but no page shows it`);

// ── Inline rendering ───────────────────────────────────────────────────────────
const escapeHTML = (text) => String(text)
  .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');

function keycaps(spec) {
  const value = spec.trim();
  let keys;
  if (value.length > 1 && value.includes('+')) keys = value.split('+').map((key) => key.trim()).filter(Boolean);
  else {
    const modifiers = /^[⌘⇧⌥⌃]*/.exec(value)[0];
    keys = [...modifiers, value.slice(modifiers.length)].filter(Boolean);
  }
  return `<span class="keys">${keys.map((key) => `<kbd>${escapeHTML(key)}</kbd>`).join('')}</span>`;
}

function anchor(section, id) {
  return id ? `${section}-${id}` : section;
}

/** ctx: { language, page, where } */
function inline(text, ctx) {
  const slots = [];
  const put = (html) => `\u0000${slots.push(html) - 1}\u0000`;
  let s = text;
  s = s.replace(/`([^`]+)`/g, (_, code) => put(`<code>${escapeHTML(code)}</code>`));
  s = s.replace(/\{\{platform:(\w+)\}\}([\s\S]*?)\{\{\/platform\}\}/g, (_, platform, inner) => {
    if (!PLATFORMS[platform]) error(ctx.where, `unknown platform "${platform}" (use ${Object.keys(PLATFORMS).join(', ')})`);
    return put(`<span class="plat-wrap"><span class="plat">${escapeHTML(PLATFORMS[platform] ?? platform)}</span> ${inline(inner, ctx)}</span>`);
  });
  s = s.replace(/\{\{platform:(\w+)\}\}/g, (_, platform) => {
    if (!PLATFORMS[platform]) error(ctx.where, `unknown platform "${platform}" (use ${Object.keys(PLATFORMS).join(', ')})`);
    return put(`<span class="plat">${escapeHTML(PLATFORMS[platform] ?? platform)}</span>`);
  });
  s = s.replace(/\{\{ui:([^}]+)\}\}/g, (_, key) => {
    const label = uiLabel(ctx.language.code, key.trim());
    return put(`<span class="ui" title="${escapeHTML(key.trim())}">${escapeHTML((label ?? key).replace(/\s*\n\s*/g, ' '))}</span>`);
  });
  s = s.replace(/\{\{kbd:([^}]+)\}\}/g, (_, spec) => put(keycaps(spec)));
  for (const leftover of s.matchAll(/\{\{[^}]*\}\}/g)) error(ctx.where, `unknown token ${leftover[0]}`);
  s = escapeHTML(s);
  s = s.replace(/\[([^\]]+)\]\(([^)\s]+)\)/g, (_, label, href) => {
    const target = href.replace(/&amp;/g, '&');
    if (/^(https?:|mailto:)/.test(target)) return put(`<a href="${escapeHTML(target)}" target="_blank" rel="noopener">${label}</a>`);
    return put(`<a href="#${escapeHTML(resolveLink(target, ctx))}">${label}</a>`);
  });
  s = s.replace(/\*\*(.+?)\*\*/g, '<b>$1</b>');
  while (/\u0000\d+\u0000/.test(s)) s = s.replace(/\u0000(\d+)\u0000/g, (_, i) => slots[Number(i)]);
  return s;
}

/** page.md, page.md#id or #id → the anchor on the language's single page. */
function resolveLink(target, ctx) {
  const match = /^(?:([\w-]+)\.md)?(?:#([a-z0-9-]+))?$/.exec(target);
  if (!match || (!match[1] && !match[2])) {
    error(ctx.where, `link "${target}" must be another help page (page.md, page.md#id or #id) or an http(s) URL`);
    return '';
  }
  const page = match[1] ? ctx.language.pages.find((p) => p.slug === match[1]) : ctx.page;
  if (!page) {
    error(ctx.where, `broken link "${target}": no page ${match[1]}.md in ${ctx.language.code}`);
    return '';
  }
  const section = page.meta.section;
  if (match[2] && !page.headings.some((heading) => heading.id === match[2])) {
    error(ctx.where, `broken link "${target}": ${page.name} has no heading {#${match[2]}}`);
  }
  return anchor(section, match[2]);
}

// ── Pictures ───────────────────────────────────────────────────────────────────
const CWEBP = (() => {
  const found = spawnSync('cwebp', ['-version'], { encoding: 'utf8' });
  return found.status === 0 ? 'cwebp' : null;
})();

function sourcePicture(code, file) {
  if (!SHOTS_DIR) return null;
  const candidates = [
    path.join(SHOTS_DIR, code, file),
    path.join(SHOTS_DIR, 'help-shots', code, file),
    path.join(SHOTS_DIR, `mighty-help-${code}`, 'help-shots', code, file),
  ];
  return candidates.find((candidate) => fs.existsSync(candidate)) ?? null;
}

function pngWidth(file) {
  const header = Buffer.alloc(24);
  const fd = fs.openSync(file, 'r');
  try { fs.readSync(fd, header, 0, 24, 0); } finally { fs.closeSync(fd); }
  return header.toString('ascii', 12, 16) === 'IHDR' ? header.readUInt32BE(16) : 0;
}

function newer(output, input) {
  return fs.existsSync(output) && fs.statSync(output).mtimeMs >= fs.statSync(input).mtimeMs;
}

function encode(input, output, args) {
  if (newer(output, input)) return true;
  fs.mkdirSync(path.dirname(output), { recursive: true });
  const result = spawnSync(CWEBP, ['-quiet', ...args, input, '-o', output], { encoding: 'utf8' });
  if (result.status !== 0) {
    error(rel(input), `cwebp failed: ${(result.stderr || '').trim()}`);
    return false;
  }
  return true;
}

let encoded = 0;
let placeholders = 0;
/**
 * The picture of one shot in one theme for one language: { full, thumb } relative to the
 * language page, or null for a placeholder. Encodes into <out>/<lang>/img/ when --shots
 * has the PNG; falls back to the Korean picture.
 */
function picture(code, id, theme) {
  const base = `${id}-${theme}`;
  for (const from of code === MASTER ? [MASTER] : [code, MASTER]) {
    const prefix = from === code ? 'img/' : `../${from}/img/`;
    const folder = path.join(OUT, from, 'img');
    const source = sourcePicture(from, `${base}.png`);
    if (source && !CHECK_ONLY) {
      if (CWEBP) {
        const full = path.join(folder, `${base}.webp`);
        if (!encode(source, full, ['-q', '90', '-m', '6', '-sharp_yuv'])) continue;
        let thumb = full;
        if (pngWidth(source) > THUMB_WIDTH) {
          thumb = path.join(folder, 'thumbs', `${base}.webp`);
          if (!encode(source, thumb, ['-q', '85', '-m', '6', '-sharp_yuv', '-resize', String(THUMB_WIDTH), '0'])) thumb = full;
        }
        encoded++;
        return { full: prefix + path.relative(folder, full), thumb: prefix + path.relative(folder, thumb) };
      }
      const copy = path.join(folder, `${base}.png`);
      fs.mkdirSync(folder, { recursive: true });
      if (!newer(copy, source)) fs.copyFileSync(source, copy);
      encoded++;
      return { full: `${prefix}${base}.png`, thumb: `${prefix}${base}.png` };
    }
    // Pictures a previous run encoded stay in use.
    for (const ext of ['webp', 'png']) {
      const full = path.join(folder, `${base}.${ext}`);
      if (fs.existsSync(full)) {
        const thumb = path.join(folder, 'thumbs', `${base}.${ext}`);
        const thumbPath = fs.existsSync(thumb) ? thumb : full;
        return { full: prefix + path.relative(folder, full), thumb: prefix + path.relative(folder, thumbPath) };
      }
    }
  }
  placeholders++;
  return null;
}

// ── Block rendering ────────────────────────────────────────────────────────────
function renderShot(block, ctx) {
  const shot = SHOTS.get(block.id);
  if (!shot) return '';
  const themes = shot.themes?.length ? shot.themes : DEFAULT_THEMES;
  const caption = block.caption ? inline(block.caption, ctx) : '';
  const alt = escapeHTML(block.caption || ctx.heading || ctx.page.meta.title);
  const ratio = shot.window ? `${shot.window.width}/${shot.window.height}` : '4/3';
  const variants = DEFAULT_THEMES.map((theme) => {
    const source = themes.includes(theme) ? theme : themes[0];
    const found = picture(ctx.language.code, shot.id, source);
    const cls = `t-${theme}`;
    if (!found) {
      return `<div class="ph ${cls}" style="aspect-ratio:${ratio}"><b>${escapeHTML(shot.id)}</b><small>${escapeHTML(ctx.language.site.placeholder ?? '')}</small></div>`;
    }
    return `<img class="${cls}" loading="lazy" src="${escapeHTML(found.thumb)}" data-full="${escapeHTML(found.full)}" data-cap="${alt}" alt="${alt}">`;
  }).join('');
  return `<figure class="shot${shot.window ? '' : ' free'}" data-shot="${escapeHTML(shot.id)}">${variants}${caption ? `<figcaption>${caption}</figcaption>` : ''}</figure>`;
}

function renderBlocks(blocks, ctx, { tight = false } = {}) {
  return blocks.map((block, index) => {
    switch (block.type) {
      case 'heading': {
        ctx.heading = block.text.replace(/\{\{[^}]*\}\}/g, '').trim();
        const tag = block.level === 2 ? 'h3' : 'h4';
        const id = anchor(ctx.page.meta.section, block.id);
        return `<${tag} id="${id}" class="${block.level === 2 ? 'sub' : 'minor'}"><a class="hash" href="#${id}">#</a>${inline(block.text, { ...ctx, where: `${ctx.where}:${block.line}` })}</${tag}>`;
      }
      case 'p': {
        const html = inline(block.text, { ...ctx, where: `${ctx.where}:${block.line}` });
        return tight && index === 0 ? html : `<p>${html}</p>`;
      }
      case 'ul':
      case 'ol':
        return `<${block.type}>${block.items.map((item) => `<li>${renderBlocks(item, ctx, { tight: true })}</li>`).join('')}</${block.type}>`;
      case 'callout': {
        const label = block.kind === 'quote' ? '' : `<b class="callout-label">${escapeHTML(ctx.language.site[block.kind] ?? block.kind)}</b>`;
        return `<div class="callout ${block.kind}">${label}${renderBlocks(block.children, ctx)}</div>`;
      }
      case 'table': {
        const cell = (text, row) => inline(text, { ...ctx, where: `${ctx.where}:${block.lines[row]}` });
        return `<div class="tbl"><table><thead><tr>${block.head.map((text) => `<th>${cell(text, 0)}</th>`).join('')}</tr></thead><tbody>${
          block.body.map((row, r) => `<tr>${row.map((text) => `<td>${cell(text, r + 2)}</td>`).join('')}</tr>`).join('')
        }</tbody></table></div>`;
      }
      case 'code':
        return `<pre><code>${escapeHTML(block.text)}</code></pre>`;
      case 'shot':
        return renderShot(block, { ...ctx, where: `${ctx.where}:${block.line}` });
      default:
        return '';
    }
  }).join('\n');
}

// ── Page ───────────────────────────────────────────────────────────────────────
const GLYPH = '<svg class="glyph" width="18" height="18" viewBox="0 0 16 16" aria-hidden="true"><g class="spin"><path d="M8 1.6V5.6M8 10.4V14.4M1.6 8H5.6M10.4 8H14.4" stroke="#fff" stroke-width="2" stroke-linecap="round" fill="none"/><path d="M4.6 4.6L6.1 6.1M9.9 9.9L11.4 11.4M4.6 11.4L6.1 9.9M9.9 6.1L11.4 4.6" stroke="#fff" stroke-opacity=".7" stroke-width="2" stroke-linecap="round" fill="none"/></g></svg>';
const SUN = '<svg class="sun" viewBox="0 0 24 24" width="15" height="15"><circle cx="12" cy="12" r="4.2" fill="none" stroke="currentColor" stroke-width="1.8"/><path d="M12 2v2.5M12 19.5V22M2 12h2.5M19.5 12H22M4.9 4.9l1.8 1.8M17.3 17.3l1.8 1.8M4.9 19.1l1.8-1.8M17.3 6.7l1.8-1.8" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"/></svg>';
const MOON = '<svg class="moon" viewBox="0 0 24 24" width="15" height="15"><path d="M20 14.5A8 8 0 0 1 9.5 4a8 8 0 1 0 10.5 10.5Z" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linejoin="round"/></svg>';

const FONTS = {
  ko: '"Apple SD Gothic Neo","Malgun Gothic"',
  en: '"Apple SD Gothic Neo","Malgun Gothic"',
  zh: '"PingFang SC","Microsoft YaHei","Noto Sans SC"',
  ja: '"Hiragino Sans","Hiragino Kaku Gothic ProN","Yu Gothic UI","Meiryo"',
};

const CSS = `:root{
 --heading:"Avenir Next","Avenir","Segoe UI Variable Display","Segoe UI",sans-serif;
 --mono:"SF Mono",ui-monospace,Menlo,"Cascadia Mono",Consolas,monospace;
}
html[data-theme=dark]{
 --page:#0B0F19;--card:#151B29;--raised:#1D2435;--sidebar:#0F1420;--line:#283043;
 --ink:#EEF1F7;--ink2:#A9B1C2;--ink3:#8E97AA;--accent:#7FA3FF;--accentSoft:#1A2750;--sInk2:#A9B1C2;
 --run:#2A5FEE;--runSoft:#1A2750;--wait:#FFA81F;--waitSoft:#3A2A0D;--waitText:#FFC45C;--doneText:#5BD49A;--doneSoft:#0F2E22;
 --subtle:rgba(255,255,255,.035);color-scheme:dark;
}
html[data-theme=light]{
 --page:#ECEEF3;--card:#FFFFFF;--raised:#F5F7FB;--sidebar:#E2E6ED;--line:#DEE2EA;
 --ink:#0E1320;--ink2:#5A6377;--ink3:#616A7C;--accent:#2459E6;--accentSoft:#E6EDFF;--sInk2:#4F5869;
 --run:#2A5FEE;--runSoft:#E6EDFF;--wait:#FFA81F;--waitSoft:#FFF3DE;--waitText:#8A5300;--doneText:#06703F;--doneSoft:#E2F6EA;
 --subtle:rgba(0,0,0,.035);color-scheme:light;
}
*{box-sizing:border-box}
html{scroll-behavior:smooth;scroll-padding-top:20px}
body{margin:0;background:var(--page);color:var(--ink);font:14px/1.65 var(--body);-webkit-font-smoothing:antialiased;display:grid;grid-template-columns:260px minmax(0,1fr);min-height:100vh}
code,kbd,pre{font-family:var(--mono);font-size:.86em}
code{color:var(--ink2);background:var(--subtle);border:1px solid var(--line);border-radius:5px;padding:0 4px}
pre{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:12px 14px;overflow-x:auto}
pre code{border:0;background:none;padding:0}
kbd{display:inline-block;min-width:1.7em;text-align:center;font-family:var(--body);font-size:.84em;background:var(--raised);border:1px solid var(--line);border-bottom-width:2px;border-radius:5px;padding:0 5px;color:var(--ink);line-height:1.5}
.keys{display:inline-flex;gap:3px;white-space:nowrap;vertical-align:1px}
a{color:var(--accent)}
html[data-theme=dark] .t-light,html[data-theme=light] .t-dark{display:none!important}
.side{position:sticky;top:0;height:100vh;background:var(--sidebar);border-right:1px solid var(--line);display:flex;flex-direction:column;padding:14px 9px 0}
.brandline{display:flex;gap:10px;align-items:center;padding:8px 10px 10px;background:var(--card);border-radius:10px;box-shadow:0 1px 1px rgba(0,0,0,.06);text-decoration:none;color:var(--ink)}
.brandline .logo{width:26px;height:26px;border-radius:7px;background:var(--run);display:grid;place-items:center;flex:none}
.brandline b{display:block;font-size:13px;font-weight:600}
.brandline small{display:block;color:var(--sInk2);font-size:11px}
.side-label{font-size:10.5px;font-weight:600;color:var(--sInk2);padding:18px 11px 9px}
.side nav{display:flex;flex-direction:column;gap:2px;overflow:auto;flex:1;padding-bottom:12px}
.nav-row{display:flex;gap:9px;align-items:baseline;text-decoration:none;color:var(--ink);padding:6px 9px 7px 10px;border-radius:8px;border:.5px solid transparent;font-size:13px}
.nav-row small{font:11px var(--mono);color:var(--sInk2);flex:none}
.nav-row:hover{background:var(--subtle)}
.nav-group.on>.nav-row{background:var(--card);border-color:rgba(0,0,0,.07);font-weight:600}
.subnav{display:none;flex-direction:column;margin:2px 0 6px 31px;border-left:1px solid var(--line)}
.nav-group.on .subnav{display:flex}
.subnav a{font-size:12px;color:var(--ink2);text-decoration:none;padding:3px 10px}
.subnav a:hover{color:var(--ink)}
.subnav a.on{color:var(--accent);font-weight:600}
.side-foot{border-top:1px solid var(--line);margin:0 -9px;padding:12px 16px 14px;font-size:11px;color:var(--sInk2);display:flex;flex-direction:column;gap:10px}
.foot-row{display:flex;align-items:center;justify-content:space-between}
.langs{display:flex;flex-wrap:wrap;gap:4px}
.langs a,.langs span{font-size:11.5px;padding:2px 8px;border-radius:999px;border:1px solid var(--line);text-decoration:none;color:var(--ink)}
.langs a.on{background:var(--run);border-color:var(--run);color:#fff}
.langs a:hover:not(.on){border-color:var(--accent);color:var(--accent)}
.langs span{opacity:.45;cursor:not-allowed}
.theme-btn{background:none;border:0;color:var(--sInk2);cursor:pointer;padding:4px;border-radius:6px}
.theme-btn:hover{background:var(--subtle);color:var(--ink)}
html[data-theme=dark] .moon,html[data-theme=light] .sun{display:none}
.glyph .spin{transform-origin:8px 8px;animation:spin 3.2s linear infinite}
@keyframes spin{to{transform:rotate(360deg)}}
main{padding:28px 44px 60px;max-width:1060px;width:100%}
.hero{background:var(--card);border-radius:20px;padding:28px 32px 26px;margin-bottom:40px;box-shadow:0 1px 1px rgba(0,0,0,.05)}
.hero h1{font:700 40px/1.08 var(--heading);margin:0 0 12px;letter-spacing:-.4px}
.hero h1 span{color:var(--accent)}
.hero p{color:var(--ink2);font-size:14.5px;margin:0 0 18px;max-width:720px}
.toc{display:grid;grid-template-columns:repeat(auto-fill,minmax(210px,1fr));gap:8px}
.toc a{display:flex;gap:9px;align-items:baseline;background:var(--raised);border-radius:12px;padding:10px 13px;text-decoration:none;color:var(--ink);font-weight:600;font-size:13px}
.toc a small{font:600 11px var(--mono);color:var(--accent)}
.toc a:hover{outline:1.5px solid var(--accent)}
.sec{margin:0 0 64px}
.sec h2{font:700 28px/1.2 var(--heading);margin:0 0 18px;display:flex;align-items:baseline;gap:14px;padding-bottom:12px;border-bottom:1px solid var(--line)}
.num{font:600 13px var(--mono);color:var(--accent);background:var(--accentSoft);border-radius:999px;padding:3px 10px;transform:translateY(-4px)}
.sub{font-size:18px;font-weight:700;margin:34px 0 10px}
.minor{font-size:15px;font-weight:700;margin:24px 0 8px}
.hash{float:left;margin-left:-1.1em;width:1.1em;color:var(--ink3);text-decoration:none;opacity:0}
.sub:hover .hash,.minor:hover .hash{opacity:1}
.sec p,.sec li{max-width:820px}
.sec ol,.sec ul{padding-left:22px}
.sec li{margin:4px 0}
.sec li>ol,.sec li>ul{margin:4px 0}
.ui{font-weight:600;color:var(--ink);background:var(--accentSoft);border-radius:5px;padding:0 5px;white-space:nowrap}
.plat{font-size:10.5px;font-weight:700;color:var(--waitText);background:var(--waitSoft);border-radius:999px;padding:1px 7px;margin-right:2px;white-space:nowrap;vertical-align:1px}
.callout{background:var(--card);border:1px solid var(--line);border-left:3px solid var(--accent);border-radius:0 10px 10px 0;padding:10px 14px;margin:14px 0;max-width:840px}
.callout.warning{border-left-color:var(--wait);background:var(--waitSoft)}
.callout.tip{border-left-color:var(--doneText)}
.callout p{margin:4px 0}
.callout-label{display:block;font-size:11.5px;text-transform:uppercase;letter-spacing:.04em;color:var(--accent)}
.callout.warning .callout-label{color:var(--waitText)}
.callout.tip .callout-label{color:var(--doneText)}
.tbl{overflow-x:auto;border:1px solid var(--line);border-radius:12px;background:var(--card);margin:12px 0;max-width:860px}
table{border-collapse:collapse;width:100%;font-size:13px}
th{text-align:left;font-size:11.5px;font-weight:600;color:var(--ink2);background:var(--raised);padding:8px 12px;border-bottom:1px solid var(--line)}
td{padding:8px 12px;border-bottom:1px solid var(--line);vertical-align:top}
tr:last-child td{border-bottom:0}
.shot{margin:16px 0 22px;max-width:960px}
.shot img{width:100%;height:auto;border-radius:11px;border:1px solid var(--line);display:block;cursor:zoom-in;background:var(--card)}
.shot.free img{width:auto;max-width:100%;max-height:360px}
.shot figcaption{font-size:12px;color:var(--ink2);margin-top:6px}
.ph{display:grid;place-content:center;gap:4px;text-align:center;border-radius:11px;border:1px dashed var(--line);background:repeating-linear-gradient(45deg,var(--raised) 0 8px,var(--card) 8px 16px);color:var(--ink3);min-height:140px}
.ph b{font:600 13px var(--mono);color:var(--ink2)}
.ph small{font-size:11.5px}
.foot{color:var(--ink2);font-size:12px;border-top:1px solid var(--line);padding-top:16px}
.lb{position:fixed;inset:0;background:rgba(5,7,13,.86);display:flex;align-items:center;justify-content:center;z-index:50;backdrop-filter:blur(4px)}
.lb[hidden]{display:none}
.lb figure{margin:0;max-width:94vw;max-height:94vh;display:flex;flex-direction:column;align-items:center}
.lb img{max-width:94vw;max-height:88vh;border-radius:10px;box-shadow:0 20px 60px rgba(0,0,0,.5)}
.lb figcaption{color:#EEF1F7;font-size:12px;margin-top:10px}
.lb button{position:absolute;background:rgba(255,255,255,.1);color:#fff;border:0;border-radius:999px;width:40px;height:40px;font-size:22px;cursor:pointer}
.lb button:hover{background:rgba(255,255,255,.22)}
.lb-x{top:18px;right:18px;font-size:16px!important}.lb-p{left:18px}.lb-n{right:18px}
@media (prefers-reduced-motion:reduce){.glyph .spin{animation:none}html{scroll-behavior:auto}}
@media (max-width:1000px){
 body{grid-template-columns:1fr}.side{position:relative;height:auto;padding-bottom:0}
 .side nav{flex-direction:row;flex-wrap:wrap}.subnav{display:none!important}
 main{padding:20px}
}
@media print{.side,.lb{display:none}body{display:block}.t-dark{display:none!important}.t-light{display:block!important}}`;

const SCRIPT = `(function(){
 var root=document.documentElement;
 try{var saved=localStorage.getItem("mc-help-theme");if(saved)root.dataset.theme=saved;else if(matchMedia("(prefers-color-scheme: light)").matches)root.dataset.theme="light";}catch(e){}
 if(location.hash==="#light"||location.hash==="#dark")root.dataset.theme=location.hash.slice(1);
 document.getElementById("theme").addEventListener("click",function(){
  root.dataset.theme=root.dataset.theme==="dark"?"light":"dark";
  try{localStorage.setItem("mc-help-theme",root.dataset.theme);}catch(e){}
 });
 // Another language opens at the same place: heading ids are shared by every language.
 document.querySelectorAll(".langs a").forEach(function(a){a.addEventListener("click",function(){
  if(location.hash&&location.hash.length>1)a.href=a.getAttribute("href").split("#")[0]+location.hash;
 });});
 var lb=document.getElementById("lb"),img=lb.querySelector("img"),cap=lb.querySelector("figcaption"),list=[],idx=0;
 function items(){return Array.prototype.filter.call(document.querySelectorAll("img[data-full]"),function(el){return el.offsetParent!==null;});}
 function show(i){if(!list.length)return;idx=(i+list.length)%list.length;var el=list[idx];img.src=el.dataset.full;img.alt=el.alt;cap.textContent=el.dataset.cap||el.alt;lb.hidden=false;}
 document.addEventListener("click",function(ev){
  var el=ev.target.closest("img[data-full]");if(!el||lb.contains(el))return;ev.preventDefault();list=items();show(list.indexOf(el));
 });
 lb.addEventListener("click",function(ev){if(ev.target===lb||ev.target.classList.contains("lb-x"))lb.hidden=true;});
 lb.querySelector(".lb-p").addEventListener("click",function(ev){ev.stopPropagation();show(idx-1);});
 lb.querySelector(".lb-n").addEventListener("click",function(ev){ev.stopPropagation();show(idx+1);});
 document.addEventListener("keydown",function(ev){if(lb.hidden)return;if(ev.key==="Escape")lb.hidden=true;if(ev.key==="ArrowLeft")show(idx-1);if(ev.key==="ArrowRight")show(idx+1);});
 var groups=document.querySelectorAll(".nav-group"),subs=document.querySelectorAll(".subnav a");
 var obs=new IntersectionObserver(function(es){es.forEach(function(e){if(!e.isIntersecting)return;
  var id=e.target.id,sec=e.target.closest(".sec");
  groups.forEach(function(g){g.classList.toggle("on",!!sec&&g.dataset.sec===sec.id);});
  if(e.target.tagName!=="SECTION")subs.forEach(function(a){a.classList.toggle("on",a.getAttribute("href")==="#"+id);});
 });},{rootMargin:"-20% 0px -70% 0px"});
 document.querySelectorAll(".sec,.sec h3[id]").forEach(function(s){obs.observe(s);});
})();`;

function languageLinks(current) {
  return LANGUAGES.map((language) => {
    if (!languages.some((built) => built.code === language.code)) {
      return `<span lang="${language.html}" aria-disabled="true" title="${escapeHTML(current.site.notTranslated ?? '')}">${escapeHTML(language.name)}</span>`;
    }
    const on = language.code === current.code;
    return `<a lang="${language.html}" hreflang="${language.html}" href="${on ? 'index.html' : `../${language.code}/index.html`}"${on ? ' class="on" aria-current="true"' : ''}>${escapeHTML(language.name)}</a>`;
  }).join('');
}

function renderLanguage(language) {
  const site = language.site;
  const sections = language.pages.map((page) => {
    const ctx = { language, page, where: page.where, heading: '' };
    const number = String(page.order).padStart(2, '0');
    const body = renderBlocks(page.blocks, ctx);
    const title = inline(page.meta.title, ctx);
    const subnav = page.headings.filter((heading) => heading.level === 2)
      .map((heading) => `<a href="#${anchor(page.meta.section, heading.id)}">${inline(heading.text, ctx)}</a>`).join('');
    return {
      html: `<section id="${page.meta.section}" class="sec">\n<h2><span class="num">${number}</span>${title}</h2>\n${body}\n</section>`,
      nav: `<div class="nav-group" data-sec="${page.meta.section}"><a href="#${page.meta.section}" class="nav-row"><small>${number}</small><span>${title}</span></a><div class="subnav">${subnav}</div></div>`,
      toc: `<a href="#${page.meta.section}"><small>${number}</small>${title}</a>`,
    };
  });
  const body = `--body:-apple-system,BlinkMacSystemFont,"SF Pro Text","Segoe UI Variable Text","Segoe UI",${FONTS[language.code]},sans-serif`;
  return `<!doctype html>
<html lang="${language.html}" data-theme="dark">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>${escapeHTML(site.title ?? 'Mighty Claude')}</title>
<style>:root{${body}}
${CSS}</style>
</head>
<body>
<aside class="side">
 <a class="brandline" href="#top"><span class="logo">${GLYPH}</span><div><b>Mighty Claude</b><small>${escapeHTML(site.brandSub ?? '')}</small></div></a>
 <div class="side-label">${escapeHTML(site.contents ?? '')}</div>
 <nav>${sections.map((s) => s.nav).join('')}</nav>
 <div class="side-foot">
  <div class="langs" role="navigation" aria-label="${escapeHTML(site.language ?? '')}">${languageLinks(language)}</div>
  <div class="foot-row"><span>${escapeHTML(site.appVersion ?? '')} ${escapeHTML(VERSION)}</span><button id="theme" class="theme-btn" aria-label="${escapeHTML(site.theme ?? '')}" title="${escapeHTML(site.theme ?? '')}">${SUN}${MOON}</button></div>
 </div>
</aside>
<main id="top">
<header class="hero">
 <h1>Mighty Claude <span>${escapeHTML(site.heroTitle ?? '')}</span></h1>
 <p>${inline(site.intro ?? '', { language, page: language.pages[0], where: `docs/help/content/${language.code}/_site.json` })}</p>
 <nav class="toc">${sections.map((s) => s.toc).join('')}</nav>
</header>
${sections.map((s) => s.html).join('\n')}
<footer class="foot">${inline(site.footer ?? '', { language, page: language.pages[0], where: `docs/help/content/${language.code}/_site.json` })}</footer>
</main>
<div id="lb" class="lb" hidden><button class="lb-x" aria-label="${escapeHTML(site.close ?? '')}">✕</button><button class="lb-p" aria-label="${escapeHTML(site.prev ?? '')}">‹</button><figure><img alt=""><figcaption></figcaption></figure><button class="lb-n" aria-label="${escapeHTML(site.next ?? '')}">›</button></div>
<script>${SCRIPT}</script>
</body>
</html>
`;
}

function renderRedirect() {
  const built = languages.map((language) => language.code);
  const fallback = built.includes('en') ? 'en' : MASTER;
  const links = languages.map((language) => `<a href="${language.code}/index.html" hreflang="${language.html}" lang="${language.html}">${escapeHTML(language.name)}</a>`).join(' · ');
  return `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Mighty Claude Help</title>
<script>(function(){
 var built=${JSON.stringify(built)},pick="${fallback}";
 var wanted=(navigator.languages&&navigator.languages.length?navigator.languages:[navigator.language||""]);
 for(var i=0;i<wanted.length;i++){var code=String(wanted[i]).toLowerCase().split("-")[0];if(built.indexOf(code)>=0){pick=code;break;}}
 location.replace(pick+"/index.html"+location.hash);
})();</script>
<style>body{font:15px/1.6 -apple-system,BlinkMacSystemFont,"Segoe UI",sans-serif;background:#0B0F19;color:#EEF1F7;display:grid;place-items:center;min-height:100vh;margin:0}a{color:#7FA3FF}</style>
</head>
<body><p>Mighty Claude Help: ${links}</p></body>
</html>
`;
}

// ── Output ─────────────────────────────────────────────────────────────────────
const pages = new Map();
for (const language of languages) pages.set(language.code, renderLanguage(language));

for (const warning of warnings) console.log(`warning: ${warning}`);
if (errors.length) {
  console.error(`help: ${errors.length} problem${errors.length === 1 ? '' : 's'}`);
  for (const message of errors) console.error(`  ${message}`);
  process.exit(1);
}

const summary = languages.map((language) => `${language.code} ${language.pages.length} pages`).join(', ');
const missing = LANGUAGES.filter((language) => !languages.some((built) => built.code === language.code)).map((language) => language.code);
if (CHECK_ONLY) {
  console.log(`help: ok (${summary}${missing.length ? `; not written yet: ${missing.join(', ')}` : ''})`);
  process.exit(0);
}

fs.mkdirSync(OUT, { recursive: true });
for (const [code, html] of pages) {
  fs.mkdirSync(path.join(OUT, code), { recursive: true });
  fs.writeFileSync(path.join(OUT, code, 'index.html'), html);
}
fs.writeFileSync(path.join(OUT, 'index.html'), renderRedirect());
// Pictures of shots that left shots.json would otherwise be uploaded forever.
for (const { code } of languages) {
  for (const folder of [path.join(OUT, code, 'img'), path.join(OUT, code, 'img', 'thumbs')]) {
    if (!fs.existsSync(folder)) continue;
    for (const name of fs.readdirSync(folder)) {
      const id = /^(.+)-(light|dark)\.(webp|png)$/.exec(name)?.[1];
      if (id && !SHOTS.has(id)) fs.rmSync(path.join(folder, name));
    }
  }
}
if (!SHOTS_DIR) console.log('help: no --shots folder; pictures not encoded before are placeholders');
else if (!CWEBP) console.log('help: cwebp not found; the PNGs are copied as they are');
console.log(`help: wrote ${rel(OUT)} (${summary}; ${encoded} pictures, ${placeholders} placeholders${missing.length ? `; not written yet: ${missing.join(', ')}` : ''})`);
