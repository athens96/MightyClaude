import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { ServicesContext, type AppServices } from '../app/context';
import { fixtureEntries } from '../bridge/devHost';
import { translator } from '../i18n/locale';
import type { LogEntry } from '../store/models';
import { Entry } from './Entry';

const services = {
  bridge: { call: () => Promise.resolve(null) },
  store: {},
  t: translator([{ 'transcript.tool.showDetail': 'Show details', 'transcript.tool.hideDetail': 'Hide details', 'transcript.tool.failed': 'Failed', 'transcript.tool.fallback': 'Task', 'run.activity.durationSeconds': '{seconds}s' }]),
} as unknown as AppServices;

function render(entry: LogEntry, over: { live?: boolean; expanded?: boolean; shell?: boolean } = {}): string {
  const element = createElement(Entry, { entry, paneId: 'p', provider: 'claude', shell: over.shell ?? false, live: over.live ?? false, expanded: over.expanded ?? false, onToggle: () => {} });
  return renderToStaticMarkup(createElement(ServicesContext.Provider, { value: services }, element));
}

const fixture = fixtureEntries('r');
const byId = (suffix: string) => fixture.find((entry) => entry.id === 'r-' + suffix)!;

// The views drawn to markup without a browser: what the web smoke counts in the app, checked here per entry.
describe('Entry', () => {
  it('draws a request as the user bubble', () => {
    expect(render(byId('u'))).toContain('data-entry-kind="user"');
  });

  it('draws a reply as a card with Markdown: heading, table, task list, quote and code', () => {
    const html = render(byId('a'));
    expect(html).toContain('data-entry-kind="assistant"');
    expect(html).toContain('data-md="table"');
    expect(html).toContain('md-task checked');
    expect(html).toContain('md-quote');
    expect(html).toContain('data-code-block');
    expect(html).toContain('data-url="https://example.com"');
    expect(html).toContain('>Claude');
  });

  it('draws tool calls with their duration, failure and folded output', () => {
    const done = render(byId('t1'));
    expect(done).toContain('data-entry-kind="tool"');
    expect(done).toContain('tool-square done');
    expect(done).toContain('· 1.2s');
    expect(done).toContain('Show details');
    expect(done).not.toContain('data-code-block');
    expect(render(byId('t1'), { expanded: true })).toContain('data-code-block');
    const failed = render(byId('t2'));
    expect(failed).toContain('tool-chip failed');
    expect(failed).toContain('· Failed');
  });

  it('names a tool with neither name nor summary by the fallback word', () => {
    const entry: LogEntry = { id: 'x', kind: 'assistant', text: 'x', timestamp: '', activity: { id: 'a', provider: 'claude', kind: 'tool', state: 'completed', summary: '' } };
    expect(render(entry)).toContain('>Task<');
  });

  it('draws output, and a shell pane\'s reply, as code', () => {
    expect(render(byId('o'))).toContain('data-code-block');
    expect(render({ ...byId('a'), id: 'shell' }, { shell: true })).toContain('data-entry-kind="output"');
  });

  it('draws notes and errors as status lines', () => {
    expect(render(byId('s'))).toContain('data-entry-kind="system"');
    expect(render(byId('e'))).toContain('entry-status error');
  });

  it('draws a questionnaire reply read only', () => {
    const text = JSON.stringify({ questions: [{ header: 'Scope', question: 'Which?', multiSelect: true, options: [{ label: 'A', description: '' }, { label: 'B', description: 'b' }] }] });
    const html = render({ id: 'q', kind: 'assistant', text, timestamp: '' });
    expect(html).toContain('data-questionnaire');
    expect(html).toContain('questionnaire-options multiple');
  });
});
