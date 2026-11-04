import { useState } from 'react';
import { Icon } from '../icons/Icon';
import type { Translate } from '../i18n/locale';
import type { StepState, StepsModel } from './steps';

const stateKey: Readonly<Record<StepState, string>> = {
  completed: 'graph.steps.status.completed', inProgress: 'graph.steps.status.inProgress', pending: 'graph.steps.status.pending',
};

/** Panes whose 진행 단계 panel is folded: per pane, for as long as the page runs (never saved). */
const folded = new Set<string>();

/**
 * The agent's own step list over the diagram's top-left corner (steps.ts decides when it shows): a header with the count and
 * a fold button, then one row per step. Its own scroll; a press or a wheel in it never pans the diagram.
 */
export function StepsPanel({ paneId, model, t }: { paneId: string; model: StepsModel; t: Translate }) {
  const [collapsed, setCollapsed] = useState(() => folded.has(paneId));
  const toggle = () => {
    const next = !collapsed;
    if (next) folded.add(paneId); else folded.delete(paneId);
    setCollapsed(next);
  };
  const label = t(collapsed ? 'graph.steps.expand' : 'graph.steps.collapse');
  return (
    <section className="mighty-steps" data-testid="mighty-steps" data-collapsed={collapsed ? '' : undefined} aria-label={t('graph.steps.title')}
      onPointerDown={(event) => event.stopPropagation()}>
      <div className="mighty-steps-head">
        <span className="mighty-steps-title" data-testid="mighty-steps-title">{t('graph.steps.title')}</span>
        <span className="mighty-steps-count" data-testid="mighty-steps-count">{t('graph.steps.count', { done: model.done, total: model.total })}</span>
        <button className="mighty-icon mighty-steps-toggle" title={label} aria-label={label} aria-expanded={!collapsed} onClick={toggle}>
          <Icon name={collapsed ? 'chevron-right' : 'chevron-down'} size={12} weight={2} />
        </button>
      </div>
      {!collapsed && (
        <ol className="mighty-steps-list">
          {model.rows.map((row, index) => (
            <li key={index} className={'mighty-step ' + row.state} data-state={row.state} aria-label={row.text + ', ' + t(stateKey[row.state])}>
              <span className={'mighty-step-mark' + (row.state === 'inProgress' && model.running ? ' live' : '')} aria-hidden="true">
                {row.state === 'completed' ? <Icon name="check" size={12} weight={2.5} /> : null}
              </span>
              <span className="mighty-step-text">{row.text}</span>
            </li>
          ))}
        </ol>
      )}
    </section>
  );
}
