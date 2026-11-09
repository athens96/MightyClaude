import { useSyncExternalStore } from 'react';
import { useServices } from '../app/context';
import { Icon } from '../icons/Icon';
import type { ShellBlockRun } from '../store/models';
import { shellBlockBusy } from './shellBlocks';

export interface ShellBlockProps {
  text: string;
  language: string | null;
  paneId: string;
  runKey: string;
  command: string;
}

/**
 * A reply's shell block with its Run link on the language line (spec: run-shell-blocks): the click runs `command` in the agent's
 * terminal pane (MainWindow.ShellBlockRuns.cs), the pane's `shellRuns` record brings its state, and its output stands
 * under the code in the code style. Reads only the pane's records, so a streamed line elsewhere draws nothing here.
 */
export function ShellBlock({ text, language, paneId, runKey, command }: ShellBlockProps) {
  const { store } = useServices();
  const readRuns = () => store.getState().snapshot?.sessions.find((pane) => pane.id === paneId)?.shellRuns;
  // A remote workspace's pane runs on its host: nothing here runs in its folder.
  const readRemote = () => {
    const snapshot = store.getState().snapshot;
    const workspace = snapshot?.sessions.find((pane) => pane.id === paneId)?.workspaceId;
    return snapshot?.workspaces.some((item) => item.id === workspace && item.remote != null) ?? false;
  };
  const runs = useSyncExternalStore(store.subscribe, readRuns, readRuns);
  const remote = useSyncExternalStore(store.subscribe, readRemote, readRemote);
  const record = runs?.find((run) => run.key === runKey);
  const name = language?.split(' ').find((word) => word.length > 0);
  return (
    <>
      <div className="code-block" data-code-block="" data-shell-block={runKey}>
        <div className="code-language shell-block-head">
          <span>{name}</span>
          {!remote && <ShellBlockAction paneId={paneId} runKey={runKey} command={command} record={record} busy={shellBlockBusy(runs, runKey)} />}
        </div>
        <pre data-text="" data-code="">{text.replace(/[\r\n]+$/, '')}</pre>
      </div>
      {record && record.output.length > 0 && (
        <div className="code-block shell-block-output" data-code-block="" data-shell-output={runKey}>
          <pre data-text="" data-code="">{record.output.replace(/[\r\n]+$/, '')}</pre>
        </div>
      )}
    </>
  );
}

/** The language line's trailing side: Run, or the state (the tool row's squares) with Stop or Run again. */
function ShellBlockAction({ paneId, runKey, command, record, busy }: { paneId: string; runKey: string; command: string; record: ShellBlockRun | undefined; busy: boolean }) {
  const { bridge, t } = useServices();
  const run = () => void bridge.call('shellBlockRun', { paneId, key: runKey, command }).catch(() => {});
  const stop = () => void bridge.call('shellBlockStop', { paneId, key: runKey }).catch(() => {});
  const again = (label: string) => (
    <button type="button" className="shell-block-link" disabled={busy} title={busy ? t('run.shellBlock.busy') : t('run.shellBlock.help')} onClick={run}>{label}</button>
  );
  if (!record) {
    return (
      <span className="shell-block-action" data-shell-state="idle">
        <button type="button" className="shell-block-link" disabled={busy} title={busy ? t('run.shellBlock.busy') : t('run.shellBlock.help')} onClick={run}>
          <Icon name="play" size={10} weight={2.5} />
          {t('run.shellBlock.run')}
        </button>
      </span>
    );
  }
  const square = record.state === 'running' ? 'run' : record.state === 'completed' ? 'done' : record.state === 'failed' ? 'err' : 'stop';
  const word = record.state === 'running' ? t('run.shellBlock.running') : record.state === 'completed' ? t('run.shellBlock.done')
    : record.state === 'failed' ? t('run.shellBlock.failed', { code: String(record.exitCode ?? -1) })
      : record.state === 'stopped' ? t('run.shellBlock.stopped') : t('run.shellBlock.interrupted');
  return (
    <span className="shell-block-action" data-shell-state={record.state} role="status">
      <span className={'tool-square ' + square} aria-hidden="true">
        {square === 'done' ? <Icon name="check" size={11} weight={4} /> : square === 'err' ? <Icon name="x" size={11} weight={4} />
          : square === 'stop' ? <Icon name="minus" size={11} weight={4} /> : <span className="tool-square-dot" />}
      </span>
      <span className={'shell-block-state' + (square === 'err' ? ' failed' : square === 'run' ? ' running' : '')}>{word}</span>
      {record.state === 'running'
        ? <button type="button" className="shell-block-link" onClick={stop}>{t('run.shellBlock.stop')}</button>
        : again(t('run.shellBlock.again'))}
    </span>
  );
}
