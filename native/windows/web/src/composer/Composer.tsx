import { useCallback, useEffect, useLayoutEffect, useRef, useState, type ClipboardEvent, type DragEvent, type KeyboardEvent } from 'react';
import { useServices, useStoreState } from '../app/context';
import { ComposerCard } from '../cards/Cards';
import type { RunSession } from '../store/models';
import { attachmentRefusal, byteLabel, toBase64 } from './attachments';
import { enterAction, maximumDraft } from './keys';
import { choose, closed, move, paletteDraft, paletteKey, paletteState, safeIndex, type PaletteState, type SlashCatalog, type SlashCommand } from './slash';
import { StyleRow, Toolbar } from './Toolbar';
import { Icon } from '../icons/Icon';
import type { AttachmentPreview, ComposerState, SendResult } from './types';
import { usePageOverlay } from '../shell/overlay';
import { newClientId, newPendingRequest } from '../pending/requests';
import { Motion } from '../theme/tokens';

/** The editor grows to six lines, then scrolls (MainWindow.cs: ComposerLine + 5 × ComposerLineStep). */
const maxLines = 6;
/** The scanned catalogue is read again after this long (SlashCatalogCache, 30 s). */
const catalogLifetime = 30_000;
/** Typing is mirrored to the host once it rests this long (natural submit B4); a send, a pane switch or closing writes at once. */
const draftRest = 300;

/**
 * The agent pane's composer (spec §3). The draft lives here and is mirrored to the host once typing rests (`updateDraft`);
 * what may happen to it (send, queue, stop, a block) is the host's composer state, pushed when it changes. Enter empties the
 * editor at once and the views draw the request with 전송 중 before the host answers (natural submit B1, src/pending).
 */
export function Composer({ session }: { session: RunSession }) {
  const { bridge, store, t } = useServices();
  const stored = useStoreState();
  const state: ComposerState | undefined = stored.composers[session.id];
  const fill = stored.fills[session.id];
  const [text, setText] = useState(() => session.draft ?? '');
  const [error, setError] = useState<string | null>(null);
  const [preview, setPreview] = useState<AttachmentPreview | null>(null);
  const [dropping, setDropping] = useState(false);
  /** What the last send did (SendResult.outcome), on the card for the web smoke. */
  const [outcome, setOutcome] = useState<string | null>(null);
  const editor = useRef<HTMLTextAreaElement>(null);
  const sending = useRef(false);
  /** The draft not yet written to the host, and its timer. */
  const draftTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const unsaved = useRef<string | null>(null);
  /** The editor was typed in since the last send (a failed send gives its words back only to an editor left alone). */
  const edited = useRef(false);
  /** The queue row flashes when a request joins it (B3). */
  const [flashing, setFlashing] = useState(false);
  const flashTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const [catalog, setCatalog] = useState<{ value: SlashCatalog; read: number } | null>(null);
  const [dismissedFor, setDismissedFor] = useState<string | null>(null);
  const [palette, setPalette] = useState<PaletteState>(closed);
  usePageOverlay(palette.commands.length > 0 || preview !== null);
  const reading = useRef(false);
  const [showUsage, setShowUsage] = useState(false);
  const usageShown = useCallback(() => setShowUsage(false), []);
  const paneId = session.id;

  useEffect(() => {
    bridge.call<ComposerState>('composerState', { paneId }).then((value) => { if (value) store.dispatch({ kind: 'composer', state: value }); }, () => {});
  }, [bridge, store, paneId]);

  useLayoutEffect(() => {
    const element = editor.current;
    if (!element) return;
    const line = parseFloat(getComputedStyle(element).lineHeight) || 18;
    element.style.height = 'auto';
    element.style.height = Math.min(element.scrollHeight, line * maxLines + 4) + 'px';
  }, [text]);

  // The palette follows the draft; the host's rows are read when it first opens and again once they are 30 s old.
  const wantsPalette = paletteDraft(session.kind, text, dismissedFor) !== null;
  useEffect(() => {
    if (!wantsPalette || reading.current || (catalog && Date.now() - catalog.read < catalogLifetime)) return;
    reading.current = true;
    bridge.call<SlashCatalog>('slashCommands', { paneId }).then((value) => { if (value) setCatalog({ value, read: Date.now() }); }, () => {}).finally(() => { reading.current = false; });
  }, [bridge, paneId, wantsPalette, catalog]);
  useEffect(() => {
    setPalette((previous) => (catalog ? paletteState(catalog.value, session.kind, text, dismissedFor, previous) : closed));
  }, [catalog, session.kind, text, dismissedFor]);

  const writeDraft = useCallback((draft: string) => {
    void bridge.call('updateDraft', { paneId, text: draft }).catch(() => {});
  }, [bridge, paneId]);
  /** Drops the draft write still waiting: a send sets the host's draft itself, and a late write would put the sent words back. */
  const cancelDraft = useCallback(() => {
    if (draftTimer.current !== null) clearTimeout(draftTimer.current);
    draftTimer.current = null;
    unsaved.current = null;
  }, []);
  const flushDraft = useCallback(() => {
    const draft = unsaved.current;
    cancelDraft();
    if (draft !== null) writeDraft(draft);
  }, [cancelDraft, writeDraft]);
  // A pane closed or switched away writes what was typed.
  useEffect(() => () => flushDraft(), [flushDraft]);
  useEffect(() => () => { if (flashTimer.current !== null) clearTimeout(flashTimer.current); }, []);

  const change = useCallback((value: string) => {
    const draft = value.slice(0, maximumDraft);
    setText(draft);
    edited.current = true;
    unsaved.current = draft;
    if (draftTimer.current !== null) clearTimeout(draftTimer.current);
    draftTimer.current = setTimeout(flushDraft, draftRest);
  }, [flushDraft]);

  const flashQueue = useCallback(() => {
    setFlashing(true);
    if (flashTimer.current !== null) clearTimeout(flashTimer.current);
    flashTimer.current = setTimeout(() => setFlashing(false), Motion.highlight);
  }, []);

  // A card filled the draft (a next action, MainWindow.WebShell.Cards.cs): its words replace the editor's, and it takes the keyboard.
  const filled = useRef(fill?.seq ?? 0);
  useEffect(() => {
    if (!fill || fill.seq === filled.current) return;
    filled.current = fill.seq;
    change(fill.text);
    const element = editor.current;
    if (element) { element.focus(); requestAnimationFrame(() => element.setSelectionRange(element.value.length, element.value.length)); }
  }, [fill, change]);

  const send = useCallback(async (second: boolean) => {
    if (sending.current) return;
    sending.current = true;
    setError(null);
    const sent = editor.current?.value ?? text;
    const attachments = state?.attachments ?? [];
    // The words go with the send, which sets the host's draft itself: the write still waiting is dropped, then the editor empties.
    cancelDraft();
    setText('');
    edited.current = false;
    // The request is drawn at once when it will start now: not for a queue (the queue row says it), a Ctrl+Enter child (its own
    // pane), a slash command (the host may answer it itself) or a blocked composer.
    const clientId = newClientId();
    const words = sent.trim().length > 0 ? sent.trim() : attachments.map((a) => a.name).join(', ');
    const drawn = !second && state?.primary === 'send' && !state.blocked && words.length > 0 && !sent.trimStart().startsWith('/');
    if (drawn) {
      store.dispatch({ kind: 'pending', action: 'add', paneId, request: newPendingRequest(session, clientId, words, Date.now()) });
    }
    /** A send that did not go gives its words back, only to an editor still empty and not typed in since. */
    const restore = (draft: string) => {
      if ((editor.current?.value ?? '') !== '' || edited.current || draft.length === 0) return;
      setText(draft);
      writeDraft(draft);
    };
    try {
      const result = await bridge.call<SendResult>('send', { paneId, text: sent, second, attachments: attachments.map((a) => a.id), clientId });
      store.dispatch({ kind: 'composer', state: result.state });
      const message = result.outcome === 'notStarted' ? t('remote.error.startFailed') : result.state.blocked ?? t('composer.blocked.title');
      store.dispatch({ kind: 'pending', action: 'answer', paneId, clientId, outcome: result.outcome, entryId: result.entryId, runId: result.runId, message, at: Date.now() });
      // The host's editor after the send: empty when it went, the words back when it did not (notStarted, blocked, none), or
      // what a built-in command left there.
      restore(result.draft);
      if (result.outcome === 'queued') flashQueue();
      setOutcome(result.outcome);
      // A failure says itself once: in the request's block when one was drawn, else in the composer's error row.
      if (result.outcome === 'notStarted' && !drawn) setError(t('remote.error.startFailed'));
    } catch (failure) {
      const message = failure instanceof Error ? failure.message : String(failure);
      store.dispatch({ kind: 'pending', action: 'fail', paneId, clientId, message, at: Date.now() });
      restore(sent);
      setOutcome('error');
      if (!drawn) setError(message);
    } finally {
      sending.current = false;
    }
  }, [bridge, store, t, paneId, text, state, session, cancelDraft, writeDraft, flashQueue]);

  const call = useCallback((method: string, args: Record<string, unknown> = {}) => {
    setError(null);
    void bridge.call(method, { paneId, ...args }).catch((failure: unknown) => setError(failure instanceof Error ? failure.message : String(failure)));
  }, [bridge, paneId]);

  const primary = useCallback(() => {
    if (!state) return;
    if (state.primary === 'stop') call('stop');
    else void send(false);
  }, [state, call, send]);

  const pick = async (command: SlashCommand) => {
    const choice = choose(command);
    if (choice.effect !== 'action') {
      setDismissedFor(choice.effect === 'argument' ? null : choice.draft);
      change(choice.draft);
      editor.current?.focus();
      return;
    }
    setDismissedFor(null);
    // The usage popover is the page's own (the context ring's); every other action is the host's, notes included.
    if (choice.action === 'ShowUsage') { change(''); setShowUsage(true); return; }
    try {
      const draft = await bridge.call<string>('slashAction', { paneId, action: choice.action, arg: choice.actionArg });
      setText(draft ?? '');
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : String(failure));
    }
    editor.current?.focus();
  };

  const keyDown = (event: KeyboardEvent<HTMLTextAreaElement>) => {
    if (palette.commands.length > 0 && !event.nativeEvent.isComposing && event.keyCode !== 229) {
      const key = paletteKey(event.key, event.shiftKey);
      if (key) {
        event.preventDefault();
        if (key === 'up' || key === 'down') setPalette(move(palette, key === 'up' ? -1 : 1));
        else if (key === 'dismiss') setDismissedFor(text);
        else if (!event.repeat) { const chosen = palette.commands[safeIndex(palette)]; if (chosen) void pick(chosen); }
        return;
      }
    }
    const action = enterAction({ ...event, isComposing: event.nativeEvent.isComposing });
    if (action === 'pass') return;
    event.preventDefault();
    if (action === 'swallow') return;
    void send(action === 'second');
  };

  const addFiles = async (files: File[]) => {
    if (files.length === 0 || !state) return;
    if (!state.attachmentsAllowed) return setError(t('wire.startRun.attachmentAiOnly'));
    const refusal = attachmentRefusal(files, state.attachments.length, state.attachments.reduce((sum, a) => sum + a.size, 0));
    if (refusal) return setError(t(refusal));
    try {
      const read = await Promise.all(files.map(async (file) => ({ name: file.name || 'clipboard-image.png', data: toBase64(await file.arrayBuffer()) })));
      await bridge.call('addAttachments', { paneId, files: read });
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : t('composer.attachment.readIncomplete'));
    }
  };

  const paste = (event: ClipboardEvent<HTMLTextAreaElement>) => {
    const files = [...event.clipboardData.files];
    if (files.length === 0) return;
    event.preventDefault();
    void addFiles(files);
  };

  const drop = (event: DragEvent<HTMLDivElement>) => {
    setDropping(false);
    if (event.dataTransfer.files.length === 0) return;
    event.preventDefault();
    void addFiles([...event.dataTransfer.files]);
  };

  const openPreview = async (id: string) => setPreview(await bridge.call<AttachmentPreview | null>('attachmentPreview', { paneId, id }));

  const queue = state?.queue;
  const queued = (queue?.held.length ?? 0) + (queue?.items.length ?? 0);
  const placeholder = t(state?.placeholderKey ?? (session.kind === 'shell' ? 'composer.placeholder.shell' : 'composer.placeholder.idle'));
  const primaryName = state?.primary === 'queue' ? t('queue.add') : state?.primary === 'stop' ? t('composer.stop.name') : t('composer.send.name');
  const primaryTip = state?.primary === 'queue' ? t('queue.addHint') : state?.primary === 'stop' ? t('composer.stop.tooltip') : t('composer.send.tooltip');

  return (
    <div className="composer-region">
      {state?.blocked && (
        <div className="composer-row composer-blocked" data-testid="composer-blocked" role="status">
          <strong>{t('composer.blocked.title')}</strong>
          <span>{state.blocked}</span>
          {state.install && <button type="button" className="composer-text-button" onClick={() => call('installCli')}>{t('setup.blocked.install')}</button>}
        </div>
      )}
      {state?.hold && <div className="composer-row composer-hold" role="status">{t('settings.cliUpdate.backgroundUpdateQueued', { provider: state.hold })}</div>}
      {state?.attachmentsLoading && !state.busy && <div className="composer-row" role="status">{t('composer.hint.attachmentsLoading')}</div>}
      {(state?.attachmentError ?? error) && (
        <div className="composer-row composer-error" data-testid="composer-error" role="alert">
          <span>{state?.attachmentError ?? error}</span>
          <button type="button" className="composer-icon-button" aria-label={t('composer.attachment.dismissError')} onClick={() => { setError(null); if (state?.attachmentError) call('dismissAttachmentError'); }}>×</button>
        </div>
      )}
      {palette.commands.length > 0 && (
        <div className="slash-palette" data-testid="slash-palette" role="listbox" data-page-overlay="">
          <div className="slash-rows">
            {palette.commands.map((command, index) => (
              <div
                key={command.invocation}
                role="option"
                aria-selected={index === safeIndex(palette)}
                className={'slash-row' + (index === safeIndex(palette) ? ' highlighted' : '')}
                ref={index === safeIndex(palette) ? (row) => row?.scrollIntoView({ block: 'nearest' }) : undefined}
                onMouseEnter={() => setPalette({ ...palette, index })}
                onMouseDown={(event) => { event.preventDefault(); void pick(command); }}
              >
                <span className="slash-name">/{command.invocation}</span>
                <span className="slash-source">{command.source}</span>
                <span className="slash-description">{command.description}</span>
              </div>
            ))}
          </div>
          <div className="slash-count">{t('slash.palette.count', { count: palette.commands.length })}</div>
        </div>
      )}
      <div
        className={'composer-card' + (dropping ? ' dropping' : '')}
        data-testid="composer"
        data-last-outcome={outcome ?? undefined}
        onDragOver={(event) => { if (event.dataTransfer.types.includes('Files') && state?.attachmentsAllowed) { event.preventDefault(); setDropping(true); } }}
        onDragLeave={(event) => { if (!event.currentTarget.contains(event.relatedTarget as Node | null)) setDropping(false); }}
        onDrop={drop}
      >
        {state?.style && <StyleRow paneId={paneId} style={state.style} />}
        {state && state.attachments.length > 0 && (
          <div className="composer-attachments">
            {state.attachments.map((file) => (
              <div className="composer-chip" key={file.id} data-attachment-id={file.id}>
                <button type="button" className="composer-chip-face" title={`${file.name} · ${byteLabel(file.size)}`} aria-label={t('composer.attachment.preview', { name: file.name })} onClick={() => void openPreview(file.id)}>
                  <span className="composer-chip-mark">{file.mediaType.startsWith('image/') ? '▣' : '▤'}</span>
                  <span className="composer-chip-words"><span className="composer-chip-name">{file.name}</span><span className="composer-chip-size">{byteLabel(file.size)}</span></span>
                </button>
                <button type="button" className="composer-icon-button" aria-label={t('composer.attachment.remove', { name: file.name })} onClick={() => call('removeAttachment', { id: file.id })}>×</button>
              </div>
            ))}
          </div>
        )}
        {queue && queued > 0 && (
          <div className={'composer-queue' + (flashing ? ' flash' : '')} data-testid="composer-queue">
            <div className="composer-queue-header">
              <Icon name="rotate-ccw-clock" size={11} weight={2.4} className="composer-queue-clock" />
              <span>{t(state.busy ? 'queue.waitingBusy' : 'queue.waiting', { count: queued })}</span>
              {queue.runNext && <button type="button" className="composer-text-button composer-queue-run" title={t('queue.runNextHelp')} onClick={() => call('queueRunNext')}>
                <Icon name="play" size={9} weight={1} fill />{t('queue.runNext')}</button>}
            </div>
            {queue.waitsOnBackground && <div className="composer-queue-notice">{t(queue.waitsOnBackground)}</div>}
            {queue.held.map((item, index) => (
              <div className="composer-queue-item" key={item.id} data-queue-held={item.id}>
                <span className="composer-queue-number">{index + 1}</span>
                <span className="composer-queue-text">{item.text}</span>
                <span className="composer-queue-pin" title={t('queue.heldHelp')} aria-label={t('queue.held')}><Icon name="pin" size={10} weight={2.2} fill /></span>
              </div>
            ))}
            {queue.items.map((item, index) => (
              <div className="composer-queue-item" key={item.id} data-queue-item={item.id}>
                <span className="composer-queue-number">{queue.held.length + index + 1}</span>
                <span className="composer-queue-text">
                  {item.text.length === 0 ? t('queue.attachmentsOnly') : item.text}
                  {item.attachments.length > 0 && <span className="composer-queue-files">{item.attachments.join(', ')}</span>}
                </span>
                <button type="button" className="composer-icon-button" aria-label={t('queue.remove')} onClick={() => call('queueRemove', { itemId: item.id })}>×</button>
              </div>
            ))}
          </div>
        )}
        <textarea
          ref={editor}
          className="composer-input"
          data-testid="composer-input"
          rows={1}
          value={text}
          maxLength={maximumDraft}
          placeholder={placeholder}
          aria-label={t('composer.input.name')}
          title={t('composer.inputShortcuts')}
          onChange={(event) => change(event.target.value)}
          onKeyDown={keyDown}
          onPaste={paste}
        />
        <ComposerCard paneId={paneId} kind="login" />
        <div className="composer-toolbar">
          {state?.attachmentsAllowed !== false && (
            <button type="button" className="composer-pill compact composer-attach" aria-label={t('composer.attach.name')} title={t('composer.attach.name')} disabled={state?.attachmentsLoading} onClick={() => call('pickAttachments')}><Icon name="paperclip" size={14} weight={1.75} /></button>
          )}
          {state?.pills ? <Toolbar paneId={paneId} pills={state.pills} busy={state.busy} showUsage={showUsage} onUsageShown={usageShown} resumed={state.resumed} />
            : <span className="composer-shell-title">{t('composer.shell.title')}</span>}
          {state?.toggles && (
            <button type="button" className={'composer-toggle' + (state.toggles.background ? ' on' : '')} aria-pressed={state.toggles.background} title={t(state.toggles.background ? 'composer.backgroundWork.hide' : 'composer.backgroundWork.show')}
              aria-label={t('composer.backgroundWork.name')} onClick={() => call('composerToggle', { which: 'background' })}><Icon name="layers" size={13} weight={1.75} fill={state.toggles.background} /></button>
          )}
          {state?.toggles && state.toggles.statusLine !== null && (
            <button type="button" className={'composer-toggle' + (state.toggles.statusLine ? ' on' : '')} aria-pressed={state.toggles.statusLine} title={t(state.toggles.statusLine ? 'composer.statusLine.hide' : 'composer.statusLine.show')}
              aria-label={t('composer.statusLine.name')} onClick={() => call('composerToggle', { which: 'statusLine' })}><Icon name="panel-bottom" size={13} weight={1.75} /></button>
          )}
          {state?.primary === 'queue' && (
            <button type="button" className="composer-stop-small" aria-label={t('composer.stop.name')} disabled={state.stopping} onClick={() => call('stop')}><Icon name="square" size={9} weight={0} fill /></button>
          )}
          {state?.worktree && (
            <button type="button" className="composer-worktree" data-testid="composer-worktree" aria-label={t('queue.worktree')} title={t(state.worktreeMenuOnly ? 'queue.worktreeMenuHintMac' : 'queue.worktreeHint')} disabled={!state.worktreeEnabled} onClick={() => void send(true)}><Icon name="split" size={13} weight={2.2} /></button>
          )}
          <button
            type="button"
            className={'composer-primary mode-' + (state?.primary ?? 'send')}
            data-testid="composer-primary"
            aria-label={primaryName}
            title={primaryTip}
            disabled={!state?.primaryEnabled}
            onClick={primary}
          >
            {state?.primary === 'stop' ? <Icon name="square" size={11} weight={0} fill /> : state?.primary === 'queue' ? <Icon name="list-plus" size={14} weight={2.2} /> : <Icon name="arrow-up" size={15} weight={2.4} />}
          </button>
        </div>
        <ComposerCard paneId={paneId} kind="statusLine" />
      </div>
      {preview && (
        <div className="composer-preview" role="dialog" data-page-overlay="" aria-label={preview.name} onClick={() => setPreview(null)}>
          <div className="composer-preview-card" onClick={(event) => event.stopPropagation()}>
            <div className="composer-preview-title">{preview.name}</div>
            <div className="composer-preview-meta">{preview.mediaType} · {preview.size.toLocaleString()} bytes</div>
            {preview.image ? <img src={preview.image} alt={preview.name} /> : preview.text !== undefined ? <pre>{preview.text}</pre> : <p>{t('composer.attachment.sentAsFile')}</p>}
            <button type="button" className="composer-text-button" onClick={() => setPreview(null)}>{t('settings.closeButton')}</button>
          </div>
        </div>
      )}
    </div>
  );
}
