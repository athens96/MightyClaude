// The 알림 대기 block: a result whose request awaits its background work's notices (Core MightyGraphBlock.Awaiting) is drawn as its own
// kind, in the wait tone with an hourglass and its work's rows under the answer so far, and is the plain result again once they are in.
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import ko from '@locales/ko.json';
import { ServicesContext, type AppServices } from '../app/context';
import { translator } from '../i18n/locale';
import { Block, type BlockProps } from './Block';
import type { MightyBlockCopy } from './types';

const t = translator([ko as Record<string, string>]);
const services = { bridge: { call: () => Promise.resolve(null) }, store: {}, t } as unknown as AppServices;

function copy(awaiting: MightyBlockCopy['awaiting']): MightyBlockCopy {
  return awaiting
    ? { kind: 'result', title: t('graph.block.awaitingNotice'), state: t('graph.state.awaitingNotice'), help: '', indicator: 'none', outline: 'marching', status: 'waiting', tone: 'wait', fromRecord: false, awaiting }
    : { kind: 'result', title: t('graph.block.result'), state: t('graph.state.completed'), help: '', indicator: 'none', outline: 'none', status: 'completed', tone: 'done', fromRecord: false };
}

function render(blockCopy: MightyBlockCopy): string {
  const props: BlockProps = {
    paneId: 'p', provider: 'claude', id: '5:r:result', kind: 'result', x: 0, y: 0, w: 360, h: 240, copy: blockCopy,
    extra: { finalOutput: 'Started the build.' } as unknown as BlockProps['extra'], visible: true, selected: false, expanded: false, running: true,
    actions: {} as BlockProps['actions'],
  };
  return renderToStaticMarkup(createElement(ServicesContext.Provider, { value: services }, createElement(Block, props)));
}

describe('the 알림 대기 block', () => {
  const rows = {
    tasks: [
      { title: 'npm run build', status: 'completed', word: t('plan.background.noticeDelivered') },
      { title: 'docs-surveyor', status: 'running', word: t('plan.background.awaitingNotice') },
    ],
    progress: t('graph.block.awaitingNotice.progress', { delivered: 1, total: 2 }),
  };

  it('reads 알림 대기 중 with the task words and the progress line', () => {
    expect([t('graph.block.awaitingNotice'), rows.tasks[0]!.word, rows.tasks[1]!.word, rows.progress]).toEqual(['알림 대기 중', '알림 전달됨', '끝나면 알림', '알림 1/2']);
  });

  it('is drawn as its own kind, waiting and marching, with the answer so far and then the work rows', () => {
    const html = render(copy(rows));
    expect(html).toContain('class="mighty-block kind-awaitingNotice tone-wait halo"');
    expect(html).toContain('data-kind="awaitingNotice"');
    expect(html).toContain('mighty-symbol kind-awaitingNotice');
    expect(html).toContain('data-outline="marching"');
    expect(html).toContain('>알림 대기 중<');
    expect(html.indexOf('Started the build.')).toBeGreaterThan(0);
    expect(html.indexOf('data-testid="mighty-awaiting"')).toBeGreaterThan(html.indexOf('Started the build.'));
    expect(html).toContain('data-status="completed"');
    expect(html).toContain('>끝나면 알림<');
    expect(html).toContain('data-testid="mighty-awaiting-progress">알림 1/2<');
  });

  it('is the plain result again once every notice is in', () => {
    const html = render(copy(undefined));
    expect(html).toContain('class="mighty-block kind-result tone-done"');
    expect(html).toContain('data-kind="result"');
    expect(html).not.toContain('mighty-awaiting');
    expect(html).toContain('>최종 결과<');
  });
});
