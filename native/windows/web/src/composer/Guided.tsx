// The chosen style's guide panel under the style row (M/GuidedPanel.swift:94-127, MainWindow.Styles.cs RenderGuidedStyle):
// drawn from what the host decided (MainWindow.WebShell.Guided.cs). Every button goes back through `guidedAction`, which
// runs the WinUI panel's own code; a waiting question or a plan's answers are the cards' (Cards.tsx), so the panel then
// shows only its phase bar.
import type { CSSProperties } from 'react';
import { useServices } from '../app/context';
import { Icon } from '../icons/Icon';
import type { GuidedAction, GuidedPanel as Panel } from './types';

export type GuidedWhat = 'action' | 'group' | 'reset' | 'install' | 'recheck' | 'view';

/** An emphasised chip's wash: its tint at 0.18 (M/GuidedActionChip.swift:46); the others sit on the subtle wash. */
export function chipFill(action: Pick<GuidedAction, 'emphasised' | 'tint'>): string {
  return action.emphasised ? `color-mix(in srgb, var(--${action.tint}) 18%, transparent)` : 'var(--subtle)';
}

/** Whether the panel has anything to draw under the style row. */
export function drawsPanel(panel: Panel | null | undefined): panel is Panel {
  if (!panel) return false;
  if (panel.approval) return true;
  return (panel.phases?.length ?? 0) > 0 || !!panel.setup || (panel.widgets?.length ?? 0) > 0 || (panel.groups?.length ?? 0) > 0
    || !!panel.spinner || (panel.actions?.length ?? 0) > 0 || !!panel.reset || !!panel.guidance;
}

export function Guided({ paneId, panel }: { paneId: string; panel: Panel | null | undefined }) {
  const { bridge, t } = useServices();
  if (!drawsPanel(panel)) return null;
  const run = (what: GuidedWhat, id?: string) => { void bridge.call('guidedAction', { paneId, what, id }).catch(() => {}); };

  if (panel.approval) {
    // A style chosen but not said yes to (M/GuidedActionChip.swift:62-80): its name, its source, and the button that opens its card.
    const { name, source, canView } = panel.approval;
    return (
      <div className="guided-approval" data-testid="guided-approval" role="status">
        <Icon name="shield" size={10} weight={2.4} className="guided-approval-mark" />
        <span className="guided-approval-name">{name}</span>
        {source && <span className="composer-style-badge">{source}</span>}
        <span className="guided-approval-words">{t('guidedPanel.approvalRequired')}</span>
        {canView && <button type="button" className="guided-small" data-testid="guided-view" onClick={() => run('view')}>{t('guidedPanel.viewContentsButton')}</button>}
      </div>
    );
  }

  const chips = (panel.actions ?? []).map((action) => (
    <button key={action.id} type="button" className={'guided-chip' + (action.emphasised ? ' emphasised' : '')} data-testid={'style-action-' + action.id}
      style={{ background: chipFill(action) } as CSSProperties} disabled={!action.enabled} title={action.help} onClick={() => run('action', action.id)}>
      {action.mark && <span className="guided-chip-mark">{action.mark}</span>}
      <span className="guided-chip-title">{action.title}</span>
      {action.userInvoked && <span className="guided-chip-flag" aria-hidden="true">♙</span>}
      {action.readOnly && <span className="guided-chip-flag" aria-hidden="true">◉</span>}
    </button>
  ));
  const reset = panel.reset && (
    <button type="button" className="guided-small" data-testid={panel.reset.kind === 'new' ? 'guided-reset' : 'guided-reset-cancel'} title={panel.reset.help ?? undefined}
      onClick={() => run('reset', panel.reset!.kind === 'new' ? 'new' : 'cancel')}>
      {panel.reset.kind === 'new' && <Icon name="rotate-ccw" size={9} weight={2.4} />}{panel.reset.title}
    </button>
  );

  return (
    <div className="guided" data-testid="guided-panel">
      {panel.phases && panel.phases.length > 0 && (
        // The phase bar (M/GuidedPanel.swift:139-152): the current one accent semibold, those reached ink × 0.75, those ahead ink2 × 0.6.
        <div className="guided-phases" data-testid="guided-phases" aria-label={t('guidedPanel.phasesAccessibility')}>
          {panel.phases.map((phase, index) => (
            <span key={phase.id} className="guided-phase-step">
              <span className={'guided-phase ' + phase.state} data-phase={phase.id}>{phase.title}</span>
              {index < panel.phases!.length - 1 && <Icon name="chevron-right" size={7} weight={2.6} className="guided-phase-chevron" />}
            </span>
          ))}
          {panel.enter && <span className="guided-enter" data-testid="guided-enter" title={panel.enter}>{panel.enter}</span>}
        </div>
      )}
      {!panel.waiting && (
        <>
          {panel.setup && (
            // What the style still needs (M/GuidedPanel.swift:161-178).
            <div className="guided-setup" data-testid="guided-setup">
              {panel.setup.missing.map((line, index) => <div key={index} className="guided-setup-line">{index === 0 && <Icon name="package" size={11} weight={2.2} />}{line}</div>)}
              {panel.setup.hint && <div className="guided-setup-hint">{panel.setup.hint}</div>}
              {panel.setup.installCommand && <code className="guided-setup-command">{panel.setup.installCommand}</code>}
              <div className="guided-buttons">
                {panel.setup.installCommand && <button type="button" className="guided-small" data-testid="guided-install" onClick={() => run('install')}>{t('guidedPanel.installButton')}</button>}
                <button type="button" className="guided-small" data-testid="guided-recheck" onClick={() => run('recheck')}>{t('guidedPanel.recheckButton')}</button>
              </div>
            </div>
          )}
          {panel.widgets && panel.widgets.length > 0 && (
            <div className="guided-state" data-testid="guided-state" aria-label={t('guidedPanel.stateAccessibility')}>
              {panel.widgets.map((widget, index) => {
                switch (widget.kind) {
                  case 'progressBar':
                    return (
                      <div key={index} className="guided-bar" aria-label={t('guidedPanel.stateProgressAccessibility')}>
                        <span className="guided-bar-track"><span className="guided-bar-fill" style={{ width: `${Math.round(widget.fraction * 100)}%` }} /></span>
                        <span className="guided-bar-count">{widget.text}</span>
                      </div>
                    );
                  case 'list':
                    return <div key={index} className="guided-list">{widget.items.map((item, at) => <div key={at}>{item}</div>)}</div>;
                  case 'taskList':
                    return (
                      <div key={index} className="guided-tasks">
                        {widget.tasks.map((task, at) => <div key={at} className={'guided-task' + (task.running ? ' running' : '')}><span className="guided-task-text">{task.text}</span><span className="guided-task-detail">{task.detail}</span></div>)}
                      </div>
                    );
                  default:
                    return <div key={index} className="guided-line">{widget.text}</div>;
                }
              })}
            </div>
          )}
          {panel.groups && panel.groups.length > 0 && (
            // The group map (M/GuidedPanel.swift:222-241): equal tiles, the title in mono over its axis.
            <>
              <div className="guided-groups" data-testid="guided-groups">
                {panel.groups.map((group) => (
                  <button key={group.id} type="button" className={'guided-group' + (group.chosen ? ' chosen' : '')} data-testid={'guided-group-' + group.id} title={group.help} onClick={() => run('group', group.id)}>
                    <span className="guided-group-title">{group.title}</span><span className="guided-group-axis">{group.axis}</span>
                  </button>
                ))}
              </div>
              {panel.groupQuestion && <div className="guided-question">{panel.groupQuestion}</div>}
            </>
          )}
          {panel.spinner
            ? <span className="guided-spinner" data-testid="guided-progress" role="progressbar" aria-label={t('guidedPanel.progressAccessibility')} />
            : (chips.length > 0 || reset) && (panel.catalogue
              ? <div className="guided-catalogue"><div className="guided-grid">{chips}</div>{reset}</div>
              : <div className="guided-chips" data-testid="guided-chips">{chips}{reset}</div>)}
          {panel.guidance && <div className="guided-guidance">{panel.guidance}</div>}
        </>
      )}
    </div>
  );
}
