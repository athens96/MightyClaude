import { memo, useEffect, useRef, useState } from 'react';
import { useServices } from '../app/context';
import { Markdown } from '../conversation/MarkdownView';
import type { StructureBoard, StructureCard } from './types';

const Card = memo(function Card({ card }: { card: StructureCard }) {
  const [open, setOpen] = useState(!card.finished);
  return (
    <div className={'structure-card tone-' + card.tone + ' kind-' + card.kind} data-card-id={card.id} data-node-id={card.nodeId}>
      <button className="structure-card-head" onClick={() => setOpen((o) => !o)}>
        <span className="structure-card-title">{card.title}</span>
        <span className={'mighty-pill tone-' + card.tone}>{card.statusWord}</span>
      </button>
      {card.parent && <div className="mighty-muted">{card.parent}</div>}
      {!open && card.resultLine && <div className="structure-line">{card.resultLine}</div>}
      {open && (
        <div className="structure-card-body">
          {card.kind === 'result' || card.kind === 'summary' ? (card.answer ? <Markdown text={card.answer} /> : card.text && <div className="structure-text">{card.text}</div>)
            : card.text && <div className="structure-text">{card.text}</div>}
          {card.kind === 'execution' && card.message && <div className="structure-text">{card.message}</div>}
          {card.activity.length > 0 && <ul className="structure-activity">{card.activity.map((a, i) => <li key={i}>{a}</li>)}</ul>}
          {card.kind !== 'agent' && card.tally && <div className="mighty-muted">{card.tally}</div>}
        </div>
      )}
    </div>
  );
});

/** The 파이프라인 view (UI/MightyStructure.cs): one board at a time, newest by default, ◀ ▶ between them; a column per step. */
export function Structure({ boards }: { boards: readonly StructureBoard[] | null }) {
  const { t } = useServices();
  const list = boards ?? [];
  const [chosen, setChosen] = useState<string | null>(null);
  const seen = useRef(list.length);
  const index = chosen === null ? list.length - 1 : Math.max(0, list.findIndex((b) => b.id === chosen));
  const board = list[index];
  const unseen = chosen !== null && list.length > seen.current;
  useEffect(() => { if (chosen === null) seen.current = list.length; }, [chosen, list.length]);
  if (!board) return <div className="mighty-structure" data-testid="mighty-structure"><div className="mighty-muted mighty-pad">{t('graph.timeline.empty')}</div></div>;
  const go = (at: number) => setChosen(at >= list.length - 1 ? null : list[at]?.id ?? null);
  const key = board.kind === 'pipeline' ? 'graph.structure.board.pipeline' : 'graph.structure.board.request';
  return (
    <div className="mighty-structure" data-testid="mighty-structure" data-board-count={list.length}>
      <nav className="structure-nav">
        <button className="mighty-icon" title={t('graph.structure.board.previous')} disabled={index <= 0} onClick={() => go(index - 1)}>◀</button>
        <span className="structure-position">{t(key, { n: index + 1, count: list.length })}</span>
        <button className="mighty-icon" title={t('graph.structure.board.next')} disabled={index >= list.length - 1} onClick={() => go(index + 1)}>▶</button>
        {board.headline && <span className="structure-headline">{board.headline}</span>}
        {unseen && <button className="mighty-link" onClick={() => setChosen(null)}>{t('graph.structure.board.showNewest')}</button>}
      </nav>
      <div className="structure-columns">
        {board.columns.map((column) => (
          <div key={column.id} className={'structure-column step-' + column.step} data-column-id={column.id}>
            <header className="structure-column-head">
              <span className="structure-column-title">{column.title}</span>
              <span className={'structure-chip step-' + column.step}>{column.stepWord}</span>
              <span className="mighty-muted">{column.count}</span>
            </header>
            <div className="structure-column-body">{column.cards.map((card) => <Card key={card.id} card={card} />)}</div>
          </div>
        ))}
      </div>
    </div>
  );
}
