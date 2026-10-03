/**
 * The one display label for a model: family plus version (`Opus 5.5`, `GPT-6.1 Sol`,
 * `Gemini 3 Pro`). Only the label changes; the id sent to the host is never rewritten.
 * The same rules live in `native/macos/Sources/MightyCore/ModelLabel.swift`, and both
 * are held to `native/contracts/fixtures/model-labels.json`.
 */

const ONE_MILLION = ' (1M)';
const FAMILIES = new Set(['opus', 'sonnet', 'haiku', 'fable']);
const GEMINI_STOP = new Set(['preview', 'exp', 'latest']);
const CACHE_LIMIT = 512;
const cache = new Map<string, string | undefined>();

const isDigits = (token: string): boolean => /^[0-9]+$/.test(token);
const startsWithDigit = (token: string): boolean => /^[0-9]/.test(token);
const capitalized = (word: string): string => word.charAt(0).toUpperCase() + word.slice(1);
/** `v2` stays as written; other words get a capital. */
const isRevision = (token: string): boolean => /^v[0-9]+$/.test(token);
const word = (token: string): string => (isRevision(token) ? token : capitalized(token));

/**
 * One word (the family) and numbers: up to two digits are version parts, eight are a
 * date; a last `v<n>` is a suffix. Anything else is unknown.
 */
function claude(tokens: string[]): string | undefined {
  let family: string | undefined;
  const version: string[] = [];
  let suffix = '';
  for (const [index, token] of tokens.entries()) {
    if (isDigits(token)) {
      if (token.length <= 2) version.push(token);
      else if (token.length !== 8) return undefined;
    } else if (index === tokens.length - 1 && family !== undefined && isRevision(token)) {
      suffix = ` ${token}`;
    } else if (family === undefined && /^[a-z]+$/.test(token)) {
      family = token;
    } else return undefined;
  }
  if (family === undefined || version.length === 0) return undefined;
  return `${capitalized(family)} ${version.join('.')}${suffix}`;
}

function gpt(tokens: string[]): string | undefined {
  const [version, ...rest] = tokens;
  if (version === undefined || !startsWithDigit(version)) return undefined;
  // A trailing release date (`yyyy-mm-dd`) is dropped.
  const words = rest
    .join('-')
    .replace(/(^|-)[0-9]{4}-[0-9]{2}-[0-9]{2}$/, '')
    .split('-')
    .filter((token) => token.length > 0);
  return [`GPT-${version}`, ...words.map(word)].join(' ');
}

function gemini(tokens: string[]): string | undefined {
  const [version, ...rest] = tokens;
  if (version === undefined || !startsWithDigit(version)) return undefined;
  const words: string[] = [];
  for (const token of rest) {
    if (GEMINI_STOP.has(token)) break;
    if (!isDigits(token)) words.push(word(token));
  }
  return ['Gemini', version, ...words].join(' ');
}

function read(id: string): string | undefined {
  let core = id.trim().toLowerCase();
  const oneMillion = core.endsWith('[1m]');
  if (oneMillion) core = core.slice(0, -4);
  const slash = core.lastIndexOf('/');
  if (slash >= 0) core = core.slice(slash + 1);
  const wrapper = core.lastIndexOf('anthropic.');
  if (wrapper >= 0) core = core.slice(wrapper + 'anthropic.'.length);
  const at = core.indexOf('@');
  if (at >= 0) core = core.slice(0, at);
  // Bedrock's revision suffix: `-v<n>:<n>` only.
  const revision = core.lastIndexOf('-v');
  if (revision >= 0) {
    const parts = core.slice(revision + 2).split(':');
    if (parts.length === 2 && parts.every(isDigits)) core = core.slice(0, revision);
  }
  const tokens = core.split('-');
  if (tokens.length < 2 || tokens.some((token) => token.length === 0)) return undefined;
  const [head, ...rest] = tokens;
  const label = head === 'claude' ? claude(rest) : head === 'gpt' ? gpt(rest) : head === 'gemini' ? gemini(rest) : undefined;
  return label === undefined ? undefined : oneMillion ? label + ONE_MILLION : label;
}

/** A full model id read into its label, or undefined for aliases and unknown names. Cached by id. */
export function formatModelId(id: string): string | undefined {
  if (cache.has(id)) return cache.get(id);
  const label = read(id);
  if (cache.size >= CACHE_LIMIT) cache.clear();
  cache.set(id, label);
  return label;
}

/**
 * The label for a model value. A full id is read with `formatModelId`. A Claude family
 * alias (`opus`, `sonnet[1m]`, …) takes its version from `resolved` when that id is the
 * same family, else it stays the bare family name — a version is never invented. Any
 * other value (`default`, `opusplan`, `best`, custom names) keeps its own name
 * (`fallback`, else the value) and adds the model it resolves to after ` · `.
 */
export function modelLabel(model: string, resolved?: string, fallback?: string): string {
  const value = model.trim();
  const direct = formatModelId(value);
  if (direct !== undefined) return direct;
  const resolvedLabel = resolved === undefined ? undefined : formatModelId(resolved);
  let alias = value.toLowerCase();
  const oneMillion = alias.endsWith('[1m]');
  if (oneMillion) alias = alias.slice(0, -4);
  if (FAMILIES.has(alias)) {
    const family = capitalized(alias);
    const suffix = oneMillion ? ONE_MILLION : '';
    if (resolvedLabel === undefined || !resolvedLabel.startsWith(`${family} `)) return family + suffix;
    return resolvedLabel.endsWith(ONE_MILLION) ? resolvedLabel : resolvedLabel + suffix;
  }
  const name = fallback ? fallback : value;
  return resolvedLabel === undefined ? name : `${name} · ${resolvedLabel}`;
}
