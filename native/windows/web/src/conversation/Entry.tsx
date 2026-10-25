import { memo, useEffect, useState } from 'react';
import { useServices } from '../app/context';
import type { AgentActivity, AgentImageRef, LogEntry } from '../store/models';
import { durationLabel, entryTime, lines, providerLabel, toolStateKey } from './format';
import { CodeBlock, Markdown, Questionnaire } from './MarkdownView';
import { ProviderMark } from './ProviderMark';
import { Icon } from '../icons/Icon';
import { parseQuestionnaire } from './questionnaire';

export interface EntryProps {
  entry: LogEntry;
  paneId: string;
  /** The pane's agent, for a reply that does not name its own. */
  provider: string;
  /** A shell pane: its replies are output, drawn as code. */
  shell: boolean;
  /** The tool call is live: the pane runs and the call is running or waiting (only ever true for such an entry). */
  live: boolean;
  /** The tool's output is unfolded. */
  expanded: boolean;
  onToggle: (id: string) => void;
}

/**
 * One log entry as AgentTranscript.Entry draws it in the main pane (cards). Memoized on its props, which for a settled entry
 * never change: only the entry that streams, or a tool whose state or fold changes, draws again.
 */
export const Entry = memo(function Entry(props: EntryProps) {
  const { entry } = props;
  if (entry.activity) return <ToolChip {...props} activity={entry.activity} />;
  if (entry.kind === 'user') return <UserBubble entry={entry} />;
  if (entry.kind === 'assistant' && !props.shell) return <Reply entry={entry} provider={entry.provider ?? props.provider} />;
  if (entry.kind === 'output' || entry.kind === 'assistant') {
    return <div className="entry entry-output" data-entry-kind="output"><CodeBlock text={entry.text} /></div>;
  }
  if (entry.kind === 'image') return <Pictures entry={entry} paneId={props.paneId} />;
  return <StatusLine entry={entry} />;
});

function language(): string {
  return globalThis.document?.documentElement.lang || 'en';
}

/** The request: an ink bubble over the right 85%, its time under the words. */
function UserBubble({ entry }: { entry: LogEntry }) {
  const time = entryTime(entry.timestamp, language());
  return (
    <div className="entry entry-user" data-entry-kind="user">
      <div className="user-bubble">
        <div className="user-text" data-text="">{entry.text}</div>
        {time && <div className="user-time">{time}</div>}
      </div>
    </div>
  );
}

/** A reply: one card with the agent's mark, name and time over the questions it asks or its Markdown. */
function Reply({ entry, provider }: { entry: LogEntry; provider: string }) {
  const time = entryTime(entry.timestamp, language());
  const questions = parseQuestionnaire(entry.text);
  return (
    <div className="entry entry-assistant" data-entry-kind="assistant">
      <div className="reply-card">
        <div className="reply-head">
          <ProviderMark provider={provider} size={13} />
          <span>{providerLabel(provider) + (time ? '  ·  ' + time : '')}</span>
        </div>
        {questions ? <Questionnaire questions={questions} /> : <Markdown text={entry.text} />}
      </div>
    </div>
  );
}

/**
 * A tool call (AgentTranscript.ToolChip): its state square, name, summary in mono, duration, the state word while live or
 * failed, and the link that folds its output in and out.
 */
function ToolChip({ entry, activity, live, expanded, onToggle }: EntryProps & { activity: AgentActivity }) {
  const { t } = useServices();
  const name = activity.toolName?.trim() ?? '';
  const detail = activity.summary.length === 0 ? (name.length === 0 ? t('transcript.tool.fallback') : '') : activity.summary;
  const duration = durationLabel(activity, t);
  const failed = activity.state === 'error';
  const state = live || failed ? t(toolStateKey(activity.state)) : null;
  const square = activity.state === 'completed' ? 'done' : failed ? 'err' : live && activity.state === 'waiting' ? 'wait' : live && activity.state === 'running' ? 'run' : 'stop';
  const output = activity.output?.trim() ? activity.output : null;
  return (
    <div className="entry entry-tool" data-entry-kind="tool" data-tool-state={activity.state}>
      <div className={'tool-chip' + (failed ? ' failed' : '')}>
        <span className={'tool-square ' + square} aria-hidden="true">
          {square === 'done' ? <Icon name="check" size={11} weight={4} /> : square === 'err' ? <Icon name="x" size={11} weight={4} />
            : square === 'stop' ? <Icon name="minus" size={11} weight={4} /> : <span className="tool-square-dot" />}
        </span>
        {name && <span className="tool-name">{name}</span>}
        {detail && <span className="tool-detail" data-text="">{detail}</span>}
        {duration && <span className="tool-meta">{'· ' + duration}</span>}
        {state && <span className={'tool-meta tool-state ' + (failed ? 'failed' : activity.state === 'waiting' ? 'waiting' : 'running')}>{'· ' + state}</span>}
        {output && (
          <button type="button" className="tool-disclosure" aria-expanded={expanded} onClick={() => onToggle(entry.id)}>
            {t(expanded ? 'transcript.tool.hideDetail' : 'transcript.tool.showDetail')}
          </button>
        )}
      </div>
      {output && expanded && <CodeBlock text={output} />}
    </div>
  );
}

/** A status line: a circled mark and the words, in errText for an error. */
function StatusLine({ entry }: { entry: LogEntry }) {
  const error = entry.kind === 'error';
  return (
    <div className={'entry entry-status' + (error ? ' error' : '')} data-entry-kind={entry.kind}>
      <span className="status-icon" aria-hidden="true" />
      <div className="status-text" data-text="">{entry.text}</div>
    </div>
  );
}

/** The pictures an entry shows, then their source in small grey type. */
function Pictures({ entry, paneId }: { entry: LogEntry; paneId: string }) {
  return (
    <div className="entry entry-image" data-entry-kind="image">
      <div className="thumbnails">
        {(entry.images ?? []).map((image, index) => <Thumbnail key={image.hash + index} image={image} paneId={paneId} />)}
      </div>
      {lines(entry.text).map((line, index) => <div key={index} className="image-caption" data-text="">{line}</div>)}
    </div>
  );
}

/** Pictures already read from the host, by hash (a data URL, or null once the host has none). */
const thumbnails = new Map<string, string | null>();
const maximumThumbnails = 64;

/**
 * A thumbnail at most 480 × 640, never larger than the picture; `images.loading` until it came, `images.missing` if it did not.
 * A click, Enter or Space opens it in the picture viewer (MainWindow.TranscriptImages.cs OpenTranscriptImage).
 */
function Thumbnail({ image, paneId }: { image: AgentImageRef; paneId: string }) {
  const { bridge, store, t } = useServices();
  const [data, setData] = useState<string | null | undefined>(() => thumbnails.get(image.hash));
  useEffect(() => {
    if (data !== undefined) return;
    let live = true;
    bridge.call<string | null>('imageData', { paneId, hash: image.hash })
      .catch(() => null)
      .then((value) => {
        thumbnails.set(image.hash, value ?? null);
        if (thumbnails.size > maximumThumbnails) thumbnails.delete(thumbnails.keys().next().value as string);
        if (live) setData(value ?? null);
      });
    return () => {
      live = false;
    };
  }, [bridge, data, image.hash, paneId]);
  if (data === undefined) return <div className="image-note">{t('images.loading')}</div>;
  if (data === null) return <div className="image-note">{t('images.missing')}</div>;
  const scale = Math.min(1, 480 / Math.max(1, image.width), 640 / Math.max(1, image.height));
  const size = image.width > 0 && image.height > 0 ? { width: Math.round(image.width * scale), height: Math.round(image.height * scale) } : undefined;
  const open = () => store.dispatch({ kind: 'picture', picture: { url: data, alt: image.source, source: { paneId, hash: image.hash } } });
  return (
    <button type="button" className="thumbnail-button" data-testid="thumbnail" title={image.source} aria-label={t('images.entry.one', { source: image.source })} onClick={open}>
      <img className="thumbnail" src={data} alt={image.source} style={size} draggable={false} />
    </button>
  );
}
