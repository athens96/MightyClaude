import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { RunSession } from '../../store/models';
import { useServices } from '../../app/context';
import { Icon } from '../../icons/Icon';
import type { Translate } from '../../i18n/locale';
import { listenPane } from '../channel';
import {
  chipLanes, dotSize, laneInk, laneShares, largestCount, overviewColumns, pickVersion, shortDate, shortStamp, topicCommits, topicDimmed, topicFiles, topicRequests, versionName, visibleTopics,
  type WorkMapCommit, type WorkMapModel, type WorkMapTopic, type WorkMapVersion,
} from './model';

const withV = (name: string) => (/^\d/.test(name) ? 'v' + name : name);

function LaneDot({ lane, dimmed }: { lane: string; dimmed?: boolean }) {
  const { t } = useServices();
  return <span className={'workmap-dot' + (dimmed ? ' dimmed' : '')} title={t('workMap.lane.' + lane)} style={{ background: laneInk[lane] ?? laneInk.etc }} />;
}

function Dots({ lanes, off }: { lanes: readonly string[]; off: ReadonlySet<string> }) {
  return <span className="workmap-dots">{lanes.map((lane) => <LaneDot key={lane} lane={lane} dimmed={off.has(lane)} />)}</span>;
}

function Bar({ version, lanes, off }: { version: WorkMapVersion; lanes: readonly string[]; off: ReadonlySet<string> }) {
  return (
    <span className="workmap-bar" aria-hidden="true">
      {laneShares(version.counts, lanes).map(({ lane, share }) => (
        <span key={lane} className={off.has(lane) ? 'dimmed' : undefined} style={{ width: `${share * 100}%`, background: laneInk[lane] ?? laneInk.etc }} />
      ))}
    </span>
  );
}

function statusText(version: WorkMapVersion, t: Translate): string {
  if (version.status === 'released') return t('workMap.status.released', { date: shortDate(version.date) });
  if (version.status === 'releasing') return t('workMap.status.releasing', { step: version.releaseStep ?? '' });
  return t('workMap.status.unreleased');
}

const statusTone = { released: 'done', releasing: 'wait', unreleased: 'none' } as const;

/**
 * The work map (docs/work-map.md; Mac WorkMapView.swift): the history of the workspace by version — an overview of versions by
 * platform, the versions newest first with their topics, and the chosen topic's requests, commits and files on the right. The
 * host builds the model (Core WorkMap.cs); the choices here are remembered only while the pane is open.
 */
export function WorkMapView({ session }: { session: RunSession }) {
  const { bridge, t } = useServices();
  const workspaceId = session.workspaceId;
  const [model, setModel] = useState<WorkMapModel | null>(null);
  const [version, setVersion] = useState<string | null>(null);
  const [topic, setTopic] = useState<{ key: string; index: number } | null>(null);
  const [off, setOff] = useState<ReadonlySet<string>>(new Set());
  const [onlyRequests, setOnlyRequests] = useState(false);
  const [commitsOpen, setCommitsOpen] = useState<ReadonlySet<string>>(new Set());
  const [copied, setCopied] = useState<string | null>(null);
  const list = useRef<HTMLDivElement>(null);

  const apply = useCallback((value: WorkMapModel | null | undefined) => {
    if (!value) return;
    setModel(value);
    setVersion((current) => pickVersion(value, current));
  }, []);
  const load = useCallback(() => { void bridge.call<WorkMapModel>('workMapState', { workspaceId }).then(apply, () => {}); }, [bridge, workspaceId, apply]);

  useEffect(() => {
    load();
    return listenPane('workmap', session.id, (message) => apply(message.state));
  }, [load, session.id, apply]);

  const columns = useMemo(() => (model ? overviewColumns(model.versions) : []), [model]);
  const largest = useMemo(() => (model ? largestCount(model.versions) : 0), [model]);

  if (!model) return <div className="workmap" data-testid="workmap" />;
  if (!model.available) {
    return <div className="workmap workmap-unavailable" data-testid="workmap"><p>{t('workMap.unavailable.' + (model.reason ?? 'gitFailed'))}</p></div>;
  }

  const next = t('workMap.version.next');
  const lanes = model.lanes;
  const chosen = model.versions.find((candidate) => candidate.key === version);
  const chosenTopic = topic ? model.versions.find((candidate) => candidate.key === topic.key)?.topics?.[topic.index] : undefined;
  const topicVersion = topic ? model.versions.find((candidate) => candidate.key === topic.key) : undefined;
  const busy = model.summarizing != null;
  const first = columns[0];
  const last = columns[columns.length - 1];

  const choose = (key: string) => {
    setVersion(key);
    list.current?.querySelector(`[data-version-key="${CSS.escape(key)}"]`)?.scrollIntoView({ block: 'nearest' });
  };
  const toggleLane = (lane: string) => setOff((current) => { const copy = new Set(current); if (!copy.delete(lane)) copy.add(lane); return copy; });
  const toggleCommits = (key: string) => setCommitsOpen((current) => { const copy = new Set(current); if (!copy.delete(key)) copy.add(key); return copy; });
  const summarize = (key: string) => {
    void bridge.call('workMapSummarize', { workspaceId, key }).then(load, load);
    // The host marks the version as running at once; the page shows it before the answer so a second press finds the buttons off.
    setModel((current) => (current ? { ...current, summarizing: key } : current));
  };
  const copy = (sha: string) => {
    void navigator.clipboard?.writeText(sha).catch(() => {});
    setCopied(sha);
    window.setTimeout(() => setCopied((current) => (current === sha ? null : current)), 1200);
  };

  return (
    <div className="workmap" data-testid="workmap">
      <div className="workmap-head">
        <h2>{t('workMap.title')}</h2>
        {first && last && <span className="workmap-cap">{t('workMap.range', { from: withV(versionName(first, next)), to: withV(versionName(last, next)) })}</span>}
        <button type="button" className="icon-button" aria-label={t('files.tree.refresh')} title={t('files.tree.refresh')} onClick={load}><Icon name="refresh-cw" size={13} weight={1.75} /></button>
        <span className="workmap-spacer" />
        {chipLanes.map((lane) => (
          <button key={lane} type="button" className={'workmap-chip' + (off.has(lane) ? '' : ' on')} aria-pressed={!off.has(lane)} data-lane={lane} onClick={() => toggleLane(lane)}>
            <LaneDot lane={lane} />{t('workMap.lane.' + lane)}
          </button>
        ))}
        <button type="button" className={'workmap-chip' + (onlyRequests ? ' on' : '')} aria-pressed={onlyRequests} data-testid="workmap-only-requests" onClick={() => setOnlyRequests((value) => !value)}>
          {t('workMap.filter.withRequests')}
        </button>
      </div>

      <div className="workmap-overview" data-testid="workmap-overview">
        <div className="workmap-grid" style={{ gridTemplateColumns: `max-content repeat(${columns.length}, 72px)` }}>
          <span />
          {columns.map((column) => (
            <button key={column.key} type="button" className={'workmap-col-head' + (column.key === version ? ' on' : '')} data-version-key={column.key} onClick={() => choose(column.key)}>{versionName(column, next)}</button>
          ))}
          {lanes.map((lane) => [
            <span key={lane} className="workmap-lane-label"><LaneDot lane={lane} />{t('workMap.lane.' + lane)}</span>,
            ...columns.map((column) => {
              const count = column.counts[lane] ?? 0;
              const size = dotSize(count, largest);
              return (
                <button key={lane + column.key} type="button" className={'workmap-cell' + (column.key === version ? ' on' : '')} tabIndex={-1} title={`${versionName(column, next)} · ${t('workMap.lane.' + lane)} · ${t('workMap.commits', { count })}`} onClick={() => choose(column.key)}>
                  {size > 0 && <span className={'workmap-bubble' + (column.status === 'unreleased' || column.status === 'releasing' ? ' pending' : '') + (off.has(lane) ? ' dimmed' : '')} style={{ width: size, height: size, background: laneInk[lane] ?? laneInk.etc }} />}
                </button>
              );
            }),
          ])}
          <span />
          {columns.map((column) => <span key={column.key} className="workmap-foot">{column.status === 'released' ? shortDate(column.date) : t('workMap.overview.pending')}</span>)}
        </div>
      </div>

      <div className="workmap-body">
        <div className="workmap-list" ref={list} data-testid="workmap-versions">
          {model.versions.map((entry) => {
            const topics = visibleTopics(entry, onlyRequests);
            const running = model.summarizing === entry.key;
            const showCommits = commitsOpen.has(entry.key);
            return (
              <section key={entry.key} className={'workmap-version' + (entry.key === version ? ' selected' : '')} data-version-key={entry.key} data-testid="workmap-version">
                <header className="workmap-version-head" onClick={() => choose(entry.key)}>
                  <span className="workmap-version-name">{versionName(entry, next)}</span>
                  <span className={'workmap-status ' + statusTone[entry.status]}>{statusText(entry, t)}</span>
                  <span className="workmap-meta">{t('workMap.commits', { count: entry.total })}{entry.topics ? ' · ' + t('workMap.topicsCount', { count: entry.topics.length }) : ''}</span>
                  <span className="workmap-spacer" />
                  <Bar version={entry} lanes={lanes} off={off} />
                </header>
                {entry.truncated && <div className="workmap-note">{t('workMap.truncated', { count: entry.commits.length })}</div>}
                {entry.topics ? (
                  <>
                    <div className="workmap-topics">
                      {topics.map(({ topic: card, index }) => {
                        const requests = topicRequests(entry, card).length;
                        return (
                          <button key={index} type="button" className={'workmap-topic' + (topic?.key === entry.key && topic.index === index ? ' selected' : '') + (topicDimmed(card, off) ? ' dimmed' : '')}
                            data-testid="workmap-topic" onClick={() => { setTopic({ key: entry.key, index }); setVersion(entry.key); }}>
                            <span className="workmap-topic-title">{card.title}</span>
                            {card.summary && <span className="workmap-topic-summary">{card.summary}</span>}
                            <span className="workmap-topic-foot">
                              <Dots lanes={card.lanes} off={off} />
                              <span>{t('workMap.commits', { count: card.commits.length })}{requests > 0 ? ' · ' + t('workMap.requestsCount', { count: requests }) : ''}</span>
                            </span>
                          </button>
                        );
                      })}
                    </div>
                    {(entry.topicsStale || running || entry.summaryError) && (
                      <div className="workmap-actions">
                        {entry.topicsStale && !running && <span className="workmap-stale">{t('workMap.stale')}</span>}
                        {running && <span className="workmap-running"><Icon name="loader-circle" size={13} weight={2} className="workmap-spin" />{t('workMap.summarizing')}</span>}
                        {entry.topicsStale && !running && <button type="button" className="workmap-button" disabled={busy} onClick={() => summarize(entry.key)}>{t('workMap.summarizeAgain')}</button>}
                        {entry.summaryError && !running && <span className="workmap-error" role="alert">{entry.summaryError}</span>}
                      </div>
                    )}
                  </>
                ) : (
                  <div className="workmap-actions">
                    {running ? (
                      <span className="workmap-running"><Icon name="loader-circle" size={13} weight={2} className="workmap-spin" />{t('workMap.summarizing')}</span>
                    ) : (
                      <>
                        <span className="workmap-quiet">{t('workMap.noTopics')} · {t('workMap.commits', { count: entry.total })}</span>
                        <button type="button" className="workmap-button" disabled={busy} data-testid="workmap-summarize" onClick={() => summarize(entry.key)}>{t('workMap.summarize')}</button>
                      </>
                    )}
                    <button type="button" className="workmap-button" onClick={() => toggleCommits(entry.key)}>{t(showCommits ? 'workMap.hideCommits' : 'workMap.showCommits')}</button>
                    {entry.summaryError && !running && <span className="workmap-error" role="alert">{entry.summaryError}</span>}
                  </div>
                )}
                {!entry.topics && showCommits && <CommitList commits={entry.commits} copied={copied} onCopy={copy} />}
              </section>
            );
          })}
        </div>

        <aside className="workmap-detail" data-testid="workmap-detail">
          {chosenTopic && topicVersion ? (
            <TopicDetail version={topicVersion} topic={chosenTopic} versionLabel={versionName(topicVersion, next)} copied={copied} onCopy={copy} off={off}
              onFile={(path) => void bridge.call('workMapOpenFile', { workspaceId, path }).catch(() => {})} />
          ) : (
            <div className="workmap-quiet">{chosen ? t('workMap.pickTopic') : ''}</div>
          )}
        </aside>
      </div>
    </div>
  );
}

function CommitList({ commits, copied, onCopy }: { commits: readonly WorkMapCommit[]; copied: string | null; onCopy: (sha: string) => void }) {
  const { t } = useServices();
  return (
    <div className="workmap-commits">
      {commits.map((commit) => (
        <div key={commit.sha} className="workmap-commit">
          <button type="button" className="workmap-sha" title={t('pane.copyButton')} onClick={() => onCopy(commit.sha)}>{copied === commit.sha ? t('workMap.copied') : commit.sha}</button>
          <span className="workmap-commit-text">
            {commit.scope ? <span className="workmap-cap">{commit.scope}</span> : null}{commit.scope ? ' ' : ''}{commit.subject}
          </span>
        </div>
      ))}
    </div>
  );
}

function TopicDetail({ version, topic, versionLabel, copied, onCopy, off, onFile }: {
  version: WorkMapVersion; topic: WorkMapTopic; versionLabel: string; copied: string | null; onCopy: (sha: string) => void; off: ReadonlySet<string>; onFile: (path: string) => void;
}) {
  const { t } = useServices();
  const requests = topicRequests(version, topic);
  const commits = topicCommits(version, topic);
  const files = topicFiles(version, topic);
  return (
    <>
      <div>
        <div className="workmap-detail-line"><Dots lanes={topic.lanes} off={off} /><span className="workmap-meta">{versionLabel}</span></div>
        <h3>{topic.title}</h3>
      </div>
      {topic.summary && <p className="workmap-detail-summary">{topic.summary}</p>}
      <div>
        <h5>{t('workMap.detail.requests')}</h5>
        {requests.length === 0 && <div className="workmap-quiet">{t('workMap.detail.noRequests')}</div>}
        {requests.map((request) => (
          <div key={request.id} className="workmap-request" data-testid="workmap-request">
            <span className="workmap-request-text">{request.text}</span>
            {request.date && <span className="workmap-meta">{shortStamp(request.date)}</span>}
          </div>
        ))}
      </div>
      <div>
        <h5>{t('workMap.detail.commits')} · {commits.length}</h5>
        <CommitList commits={commits} copied={copied} onCopy={onCopy} />
      </div>
      {files.length > 0 && (
        <div>
          <h5>{t('workMap.detail.files')} · {t('workMap.detail.filesCount', { count: files.length })}</h5>
          <div className="workmap-files">
            {files.map((path) => <button key={path} type="button" className="workmap-file" title={path} onClick={() => onFile(path)}>{path}</button>)}
          </div>
        </div>
      )}
    </>
  );
}
