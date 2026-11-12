// The progress panel's background section (macOS MightyGraphProgressPanel backgroundSection, Core FlowSteps.BackgroundOf): under the steps, headed
// 백그라운드 with running/total, a line per command or sub-agent with its mark and time, a count of the rest, and nothing while the panel is folded.
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import ko from '@locales/ko.json';
import { ServicesContext, type AppServices } from '../app/context';
import { translator } from '../i18n/locale';
import { ProgressPanel } from './FlowParts';
import type { FlowProgressCopy } from './types';

const t = translator([ko as Record<string, string>]);
const services = { bridge: { call: () => Promise.resolve(null) }, store: {}, t } as unknown as AppServices;

function progress(extra: Partial<FlowProgressCopy> = {}): FlowProgressCopy {
  return {
    title: '진행 상황', heading: '요청 1', request: '배포해 줘', stateWord: '완료', status: 'completed', count: '1/1',
    rows: [{ index: 0, nodeId: 'n', text: '1. 빌드', state: 'done', startedAt: 0, endedAt: 5_000 }], hidden: 0, running: false,
    requestNodeId: 'r', requestHelp: '이 요청으로 이동', ...extra,
  };
}

const render = (copy: FlowProgressCopy, paneId = 'p') =>
  renderToStaticMarkup(createElement(ServicesContext.Provider, { value: services }, createElement(ProgressPanel, { paneId, progress: copy, onReveal: () => {} })));

describe('the progress panel lists the background work', () => {
  const background: FlowProgressCopy['background'] = [
    { id: 'wait', text: 'Wait for the deploy', agent: false, state: 'current', startedAt: Date.now() - 3_000 },
    { id: 'review', text: 'Review the diff', agent: true, state: 'failed', startedAt: 4_000, endedAt: 12_000 },
    { id: 'build', text: 'Build the app', agent: false, state: 'done', startedAt: 2_000, endedAt: 9_000 },
  ];

  it('draws its heading with running/total and a line per piece of work under the steps', () => {
    const html = render(progress({ background, backgroundHidden: 2 }));
    expect(html).toContain('data-testid="mighty-progress-background"');
    expect(html).toContain('>백그라운드<');
    // One of the three lines runs; two more are left out of the lines but counted.
    expect(html).toContain('data-testid="mighty-progress-background-count">1/5<');
    expect(html.indexOf('mighty-progress-row-0')).toBeLessThan(html.indexOf('mighty-progress-background'));
    expect(html.indexOf('mighty-progress-background-wait')).toBeLessThan(html.indexOf('mighty-progress-background-review'));
    expect(html.indexOf('mighty-progress-background-review')).toBeLessThan(html.indexOf('mighty-progress-background-build'));
    expect(html).toContain('flow-outline-row state-current');
    expect(html).toContain('flow-outline-row state-failed');
    expect(html).toContain('>Wait for the deploy<');
    // An ended one says how long it took.
    expect(html).toContain('>8초<');
    expect(html).toContain('data-testid="mighty-progress-background-more">외 2개<');
  });

  it('draws no section for a request without background work, and no count line when nothing is left out', () => {
    expect(render(progress())).not.toContain('mighty-progress-background');
    const html = render(progress({ background, backgroundHidden: 0 }));
    expect(html).toContain('>1/3<');
    expect(html).not.toContain('mighty-progress-background-more');
  });
});
