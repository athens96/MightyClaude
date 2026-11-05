import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { ServicesContext, type AppServices } from '../app/context';
import { translator } from '../i18n/locale';
import { chipFill, drawsPanel, Guided } from './Guided';
import type { GuidedAction, GuidedPanel } from './types';

const t = translator([{
  'guidedPanel.approvalRequired': 'Approval required', 'guidedPanel.viewContentsButton': 'View contents', 'guidedPanel.installButton': 'Install',
  'guidedPanel.recheckButton': 'Check again', 'guidedPanel.phasesAccessibility': 'Phases', 'guidedPanel.progressAccessibility': 'Running',
}]);
const services = { bridge: { call: () => Promise.resolve(null) }, store: { dispatch: () => {} }, t } as unknown as AppServices;
const render = (panel: GuidedPanel | null) => renderToStaticMarkup(createElement(ServicesContext.Provider, { value: services }, createElement(Guided, { paneId: 'p', panel })));

const action = (over: Partial<GuidedAction> = {}): GuidedAction => ({
  id: 'interview', title: 'Start interview', mark: '?', userInvoked: false, readOnly: false, emphasised: false, tint: 'accent', enabled: true, help: 'Ask questions', ...over,
});
const phases: GuidedPanel['phases'] = [
  { id: 'goal', title: 'Goal', state: 'reached' }, { id: 'interview', title: 'Interview', state: 'current' }, { id: 'seed', title: 'Seed', state: 'ahead' },
];

// The style's guide panel in the web window (MainWindow.WebShell.Guided.cs): the host decides, the page draws.
describe('the guided panel', () => {
  it('draws nothing without a style', () => {
    expect(render(null)).toBe('');
    expect(drawsPanel({ phases: [], actions: [] })).toBe(false);
  });

  it('shows the phase bar with the current phase marked, and the Enter prefix', () => {
    const html = render({ phases, actions: [action()], enter: '/ouroboros:interview' });
    expect(html).toContain('data-testid="guided-phases"');
    expect(html).toMatch(/class="guided-phase current"[^>]*>Interview</);
    expect(html).toMatch(/class="guided-phase reached"[^>]*>Goal</);
    expect(html).toMatch(/class="guided-phase ahead"[^>]*>Seed</);
    expect(html).toContain('/ouroboros:interview');
  });

  it('draws each action chip, emphasised on its tint and off while it needs text', () => {
    const html = render({ phases, actions: [action({ emphasised: true, tint: 'agent-text' }), action({ id: 'auto', title: 'Auto', enabled: false, readOnly: true })] });
    const first = html.match(/<button[^>]*data-testid="style-action-interview"[^>]*>/)?.[0] ?? '';
    expect(first).toContain('emphasised');
    expect(first).toContain('var(--agent-text) 18%');
    const second = html.match(/<button[^>]*data-testid="style-action-auto"[^>]*>/)?.[0] ?? '';
    expect(second).toContain('disabled');
    expect(html).toContain('◉');
  });

  it('shows only the phase bar while a question or a plan waits', () => {
    const html = render({ phases, waiting: true, actions: [action()], guidance: 'Answer the question' });
    expect(html).toContain('data-testid="guided-phases"');
    expect(html).not.toContain('style-action-interview');
    expect(html).not.toContain('Answer the question');
  });

  it('shows what is missing with install and check again', () => {
    const html = render({ phases, setup: { missing: ['Ouroboros plugin'], hint: 'Install it first', installCommand: 'claude plugin install ouroboros' }, actions: [] });
    expect(html).toContain('Ouroboros plugin');
    expect(html).toContain('data-testid="guided-install"');
    expect(html).toContain('data-testid="guided-recheck"');
    expect(html).toContain('claude plugin install ouroboros');
  });

  it('shows a spinner instead of chips while a sequence runs', () => {
    const html = render({ phases, spinner: true, actions: [], guidance: 'Running seed' });
    expect(html).toContain('data-testid="guided-progress"');
    expect(html).toContain('Running seed');
  });

  it('offers the reset chip and the group map', () => {
    const html = render({ phases, actions: [action()], reset: { kind: 'new', title: 'New goal', help: null },
      groups: [{ id: 'a', title: 'A', axis: 'first', help: 'Which?', chosen: true }, { id: 'b', title: 'B', axis: 'second', help: 'B?', chosen: false }], groupQuestion: 'Which?', catalogue: true });
    expect(html).toContain('data-testid="guided-reset"');
    expect(html).toMatch(/class="guided-group chosen"[^>]*data-testid="guided-group-a"/);
    expect(html).toContain('guided-grid');
  });

  it('says a style needs a yes, with the button that opens it', () => {
    const html = render({ approval: { name: 'Paperthin', source: 'Workspace', canView: true } });
    expect(html).toContain('Approval required');
    expect(html).toContain('data-testid="guided-view"');
    expect(render({ approval: { name: 'X', source: null, canView: false } })).not.toContain('guided-view');
  });

  it('washes a quiet chip in the subtle colour', () => {
    expect(chipFill({ emphasised: false, tint: 'accent' })).toBe('var(--subtle)');
  });
});
