import { memo } from 'react';
import type { RunSession } from '../store/models';
import { useServices } from '../app/context';
import { Conversation } from '../conversation/Conversation';

/** The selected pane: its title and state over its conversation (the header and the composer come later in phase 1). */
export const PaneView = memo(function PaneView({ session }: { session: RunSession }) {
  const { t } = useServices();
  return (
    <section className="pane" data-session-id={session.id}>
      <header className="pane-header">
        <h1 className="pane-title" data-testid="pane-title">{session.title}</h1>
        <span className={'pane-status status-' + session.status}>{t('session.state.' + session.status)}</span>
      </header>
      <Conversation key={session.id} session={session} />
    </section>
  );
});
