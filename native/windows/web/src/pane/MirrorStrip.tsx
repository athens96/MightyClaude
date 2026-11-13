// The strip over another computer's pane (the remote mirror, pane/mirror.ts): "원격 창 · ● {host}에서 실행 중 · {state}", the host
// pane's branch and the requests queued there. Every word is the host's model (MainWindow.RemoteMirror.cs WebMirrorStrip).
import { Icon } from '../icons/Icon';
import type { MirrorStrip as MirrorStripWords } from '../shell/types';
import { mirrorStrip } from './mirror';
import './worktree.css';

export function MirrorStrip({ id, strip }: { id: string; strip: MirrorStripWords }) {
  const model = mirrorStrip(strip);
  return (
    <div className="worktree-bar mirror-strip" data-testid={'pane-mirror-banner-' + id} title={model.help} aria-label={model.label} role="note">
      <Icon name="monitor" size={12} weight={2.2} />
      <span className="worktree-bar-branch">{model.badge}</span>
      <span className={'mirror-strip-dot tone-bg-' + model.tone} aria-hidden="true" />
      <span className="mirror-strip-state" data-testid={'pane-mirror-state-' + id}>{model.state}</span>
      {model.branch && <span className="mirror-strip-branch"><Icon name="git-branch" size={11} weight={2.2} />{model.branch}</span>}
      <span className="spacer" />
      {model.queued && <span className="tone-text-wait mirror-strip-queued" title={model.queued.help}>{model.queued.text}</span>}
    </div>
  );
}
