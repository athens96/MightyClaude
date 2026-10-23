import { describe, expect, it } from 'vitest';
import { argumentQuery, choose, closed, filter, move, paletteDraft, paletteKey, paletteState, query, rows, type SlashCatalog, type SlashCommand } from './slash';

const command = (invocation: string, extra: Partial<SlashCommand> = {}): SlashCommand => ({ invocation, description: '', source: 'App', origin: 'App', ...extra });
const catalog: SlashCatalog = {
  builtins: [command('model', { argument: 'Model' }), command('clear', { action: 'NewConversation' }), command('help', { action: 'Help' })],
  scanned: [command('review', { description: 'Review code' }), command('plugin:arch'), command('model'), command('deep-help')],
  choices: { model: [command('model opus', { action: 'SetModel', actionArg: 'opus' }), command('model sonnet', { action: 'SetModel', actionArg: 'sonnet' })] },
};

describe('slash queries', () => {
  it('reads the name being typed', () => {
    expect(query('/ar')).toBe('ar');
    expect(query('/')).toBe('');
    expect(query('/a b')).toBeNull();
    expect(query('a')).toBeNull();
    expect(query('/' + 'x'.repeat(81))).toBeNull();
  });
  it('reads a built-in argument', () => {
    expect(argumentQuery('/model cla')).toEqual({ command: 'model', query: 'cla' });
    expect(argumentQuery('/model ')).toEqual({ command: 'model', query: '' });
    expect(argumentQuery('/model a b')).toBeNull();
    expect(argumentQuery('/-x a')).toBeNull();
  });
});

describe('ranking', () => {
  it('ranks prefix, after-colon prefix, then substring', () => {
    const list = [command('xarch'), command('plugin:arch'), command('archive'), command('other', { description: 'ARCH notes' })];
    expect(filter(list, 'ARCH').map((c) => c.invocation)).toEqual(['archive', 'plugin:arch', 'xarch', 'other']);
  });
  it('lets a built-in shadow a scanned command and caps rows', () => {
    expect(rows(catalog, 'claude', '/m').map((c) => c.invocation)).toEqual(['model']);
    expect(rows(catalog, 'claude', '/help').map((c) => c.invocation)).toEqual(['help', 'deep-help']);
    const many: SlashCatalog = { ...catalog, scanned: Array.from({ length: 80 }, (_, i) => command('c' + i)) };
    expect(paletteState(many, 'claude', '/', null).commands).toHaveLength(60);
  });
  it('lists argument choices filtered with the name kept', () => {
    expect(rows(catalog, 'claude', '/model so').map((c) => c.invocation)).toEqual(['model sonnet']);
    expect(rows(catalog, 'claude', '/clear x')).toEqual([]);
  });
});

describe('palette state', () => {
  it('never opens on a shell or a dismissed draft', () => {
    expect(paletteDraft('shell', '/m', null)).toBeNull();
    expect(paletteDraft('claude', '/m', '/m')).toBeNull();
    expect(paletteState(catalog, 'claude', 'hello', null)).toBe(closed);
  });
  it('wraps the highlight and keeps it over the same rows', () => {
    const open = paletteState(catalog, 'claude', '/', null);
    expect(move(open, -1).index).toBe(open.commands.length - 1);
    expect(move(move(open, -1), 1).index).toBe(0);
    const moved = move(open, 1);
    expect(paletteState(catalog, 'claude', '/', null, moved).index).toBe(1);
    expect(paletteState(catalog, 'claude', '/h', null, moved).index).toBe(0);
  });
  it('chooses as the host does', () => {
    expect(choose(command('review'))).toEqual({ effect: 'insert', draft: '/review ' });
    expect(choose(command('model', { argument: 'Model' }))).toEqual({ effect: 'argument', draft: '/model ' });
    expect(choose(command('model opus', { action: 'SetModel', actionArg: 'opus' }))).toEqual({ effect: 'action', draft: '', action: 'SetModel', actionArg: 'opus' });
  });
  it('maps keys', () => {
    expect(paletteKey('Tab', false)).toBe('choose');
    expect(paletteKey('Enter', true)).toBeNull();
    expect(paletteKey('Escape', false)).toBe('dismiss');
    expect(paletteKey('ArrowUp', false)).toBe('up');
  });
});
