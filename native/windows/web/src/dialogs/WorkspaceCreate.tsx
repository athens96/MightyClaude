// The two new ways to add a workspace, drawn by the page (docs/superpowers/specs/2026-10-26-workspace-create-design.md; the
// Mac's WorkspaceCreateViews.swift): a new folder, optionally `git init`, and a workspace from a Git address. The flows open
// them (MainWindow.WorkspaceCreate.cs ShowWebWorkspaceCreateAsync); every rule and every word is the host's: the labels it
// sends, and createCheck, makeFolder, repoName, gitProbe and gitCreate, which answer in WorkspaceCreate.cs's words.
import { useEffect, useId, useRef, useState } from 'react';
import { useServices, useStoreState } from '../app/context';
import { Icon } from '../icons/Icon';
import { MiddlePath } from '../shell/parts';
import type { CreateLabels, DialogRequest } from '../shell/types';
import { Sheet } from './Dialogs';
import { canCreateFromGit, canMakeFolder, fill, nextName, shownProblem, type CreateCheck, type ProbeState } from './workspaceCreate';

type Answer = (value: unknown) => void;

interface MakeFolderAnswer { outcome: 'made' | 'madeButGitInitFailed' | 'failed'; message: string | null; notice: string | null; path: string }
interface ProbeAnswer { reachable: boolean; kind: string | null; message: string | null }
/** `busy`: a clone already runs in this sheet (a second 생성 press); that one's answer counts. */
interface CloneAnswer { outcome: 'done' | 'cancelled' | 'failed' | 'busy'; kind: string | null; message: string | null; path: string | null }

const reason = (error: unknown) => (error instanceof Error ? error.message : String(error));

/**
 * The host's check of a name in a location, asked again as either changes. `current` is the answer for what is typed now
 * (null while it is asked), which the buttons follow; `last` is the latest answer, which the lines under the fields show.
 */
function useCreateCheck(location: string, name: string): { current: CreateCheck | null; last: CreateCheck | null } {
  const { bridge } = useServices();
  const key = location + '\n' + name;
  const [answer, setAnswer] = useState<{ key: string; check: CreateCheck } | null>(null);
  useEffect(() => {
    let live = true;
    void bridge.call<CreateCheck>('createCheck', { location, name }).then((check) => { if (live && check) setAnswer({ key, check }); }, () => {});
    return () => { live = false; };
  }, [bridge, key, location, name]);
  return { current: answer?.key === key ? answer.check : null, last: answer?.check ?? null };
}

function Heading({ title, subtitle }: { title: string; subtitle: string }) {
  return (
    <div className="create-heading">
      <h2 className="sheet-title">{title}</h2>
      <div className="quiet small">{subtitle}</div>
    </div>
  );
}

/** The location: the path, cut in the middle when long, and the folder picker. */
function LocationRow({ labels, location, onPick }: { labels: CreateLabels; location: string; onPick: (path: string) => void }) {
  const { bridge } = useServices();
  const choose = () => void bridge.call<string | null>('pickLocation', { current: location }).then((picked) => { if (picked) onPick(picked); }, () => {});
  return (
    <div className="create-location">
      <MiddlePath path={location} className="create-location-path" />
      <button type="button" className="icon-button" title={labels.choose} aria-label={labels.choose} onClick={choose} data-testid="workspace-create-choose-location">
        <Icon name="folder" size={13} />
      </button>
    </div>
  );
}

/** The path the folder will have, once a name is typed. */
function PathPreview({ labels, name, check }: { labels: CreateLabels; name: string; check: CreateCheck | null }) {
  if (name.trim() === '' || !check) return null;
  return <div className="sheet-caption selectable" data-testid="workspace-create-path">{fill(labels.path, { path: check.target })}</div>;
}

/** "새 폴더 만들기": a name, a location and an optional `git init`; the flow then adds the folder as Ctrl+O adds one. */
export function NewFolder({ id, dialog, answer }: { id: number; dialog: Extract<DialogRequest, { type: 'newFolder' }>; answer: Answer }) {
  const { bridge } = useServices();
  const { labels } = dialog;
  const fieldId = useId();
  const [name, setName] = useState('');
  const [location, setLocation] = useState(dialog.location);
  const [gitInit, setGitInit] = useState(false);
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<string | null>(null);
  const { current, last } = useCreateCheck(location, name);
  const nameField = useRef<HTMLInputElement>(null);
  useEffect(() => { setFailure(null); }, [name, location]);
  // The disabled fieldset drops focus while the folder is made: it comes back to the name, so Enter and Esc work again.
  const wasBusy = useRef(false);
  useEffect(() => { if (wasBusy.current && !busy) nameField.current?.focus(); wasBusy.current = busy; }, [busy]);
  const ready = canMakeFolder(current, busy);
  const create = async () => {
    if (!ready) return;
    setBusy(true);
    setFailure(null);
    try {
      const made = await bridge.call<MakeFolderAnswer>('makeFolder', { location, name, gitInit });
      if (made.outcome !== 'failed') { answer({ choice: 'done', path: made.path, location, notice: made.notice ?? undefined }); return; }
      // Core always words a failure; the check's own line stands in should it ever not.
      setFailure(made.message || last?.message || made.outcome);
    } catch (error) {
      setFailure(reason(error));
    }
    setBusy(false);
  };
  // While the folder is being made it exists already: no "already exists" line then.
  const problem = failure ?? shownProblem(last, name, busy);
  return (
    <Sheet id={id} width={460} label={dialog.title} onCancel={() => { if (!busy) answer({ choice: 'cancel' }); }}>
      <Heading title={dialog.title} subtitle={dialog.subtitle} />
      <fieldset className="create-grid" disabled={busy}>
        <label className="create-label" htmlFor={fieldId}>{labels.name}</label>
        <input ref={nameField} id={fieldId} className="sheet-field" data-autofocus placeholder={labels.name} value={name} data-testid="workspace-new-folder-name"
          onChange={(event) => setName(event.target.value)}
          onKeyDown={(event) => { if (event.key === 'Enter' && !event.nativeEvent.isComposing) { event.preventDefault(); void create(); } }} />
        <span className="create-label">{labels.location}</span>
        <LocationRow labels={labels} location={location} onPick={setLocation} />
        <span />
        <label className="create-check"><input type="checkbox" checked={gitInit} onChange={(event) => setGitInit(event.target.checked)} data-testid="workspace-new-folder-git-init" /> {labels.gitInit}</label>
      </fieldset>
      <PathPreview labels={labels} name={name} check={last} />
      {problem && <div className="sheet-caption tone-text-err" data-testid="workspace-create-problem">{problem}</div>}
      <div className="sheet-buttons">
        {busy && <span className="spinner" />}
        <span className="spacer" />
        <button type="button" className="plain-button" disabled={busy} onClick={() => answer({ choice: 'cancel' })}>{labels.cancel}</button>
        <button type="button" className="accent-button" disabled={!ready} onClick={() => void create()} data-testid="workspace-new-folder-create">{labels.create}</button>
      </div>
    </Sheet>
  );
}

/** A failed check or clone in the host's words; with Git missing, the way to 필요한 도구. */
function GitError({ kind, message, labels, onOpenSetup, testId }: { kind: string | null; message: string; labels: CreateLabels; onOpenSetup: () => void; testId: string }) {
  return (
    <div className="create-error">
      <span className="tone-text-err selectable" data-testid={testId}>{'✕ ' + message}</span>
      {kind === 'gitMissing' && <button type="button" className="plain-button" onClick={onOpenSetup} data-testid="workspace-git-open-setup">{labels.openSetup}</button>}
    </div>
  );
}

/**
 * "Git으로 생성": the address is checked with this computer's own credentials once typing stops for 0.6 s (or at once from
 * 연결 확인), the folder name follows it until it is typed in, and 생성 clones with progress and cancel.
 */
export function GitCreate({ id, dialog, answer }: { id: number; dialog: Extract<DialogRequest, { type: 'gitCreate' }>; answer: Answer }) {
  const { bridge } = useServices();
  const { labels } = dialog;
  const urlId = useId();
  const nameId = useId();
  const progress = useStoreState().gitProgress?.[id];
  const [url, setUrl] = useState('');
  const [name, setName] = useState('');
  const [edited, setEdited] = useState(false);
  const [location, setLocation] = useState(dialog.location);
  const [probe, setProbe] = useState<ProbeState>({ state: 'idle' });
  const [probeToken, setProbeToken] = useState(0);
  const probeNow = useRef(false);
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<{ kind: string | null; message: string } | null>(null);
  const { current, last } = useCreateCheck(location, name);
  // The disabled fieldset drops focus as the clone starts: Cancel takes it, so Esc (Sheet's key) still stops the clone.
  const cancelButton = useRef<HTMLButtonElement>(null);
  useEffect(() => { if (busy) cancelButton.current?.focus(); }, [busy]);

  // The name follows the address until it is typed in.
  useEffect(() => {
    let live = true;
    void bridge.call<string | null>('repoName', { url }).then((suggested) => { if (live) setName((typed) => nextName(typed, edited, suggested ?? null)); }, () => {});
    return () => { live = false; };
  }, [bridge, url, edited]);

  // A new address drops the check on the old one at once (Enter must not clone what the check has not seen), then checks it.
  useEffect(() => {
    setProbe({ state: 'idle' });
    const address = url.trim();
    const immediate = probeNow.current;
    probeNow.current = false;
    if (address === '') return;
    let live = true;
    const timer = setTimeout(() => {
      setProbe({ state: 'checking' });
      void bridge.call<ProbeAnswer>('gitProbe', { url: address }).then((found) => {
        // A check the host stopped for a newer one answers `cancelled`: the newer one's answer counts.
        if (!live || found.kind === 'cancelled') return;
        setProbe(found.reachable ? { state: 'reachable' } : { state: 'failed', kind: found.kind ?? 'other', message: found.message ?? '' });
      }, (error: unknown) => { if (live) setProbe({ state: 'failed', kind: 'other', message: reason(error) }); });
    }, immediate ? 0 : 600);
    return () => { live = false; clearTimeout(timer); };
  }, [bridge, url, probeToken]);

  useEffect(() => { setFailure(null); }, [url, name, location]);

  const ready = canCreateFromGit(current, probe, busy);
  const openSetup = () => answer({ choice: 'openSetup' });
  const create = async () => {
    if (!ready) return;
    setBusy(true);
    setFailure(null);
    try {
      const cloned = await bridge.call<CloneAnswer | null>('gitCreate', { dialogId: id, url, location, name });
      // A clone already runs in this sheet: its own answer ends the busy state, not this press.
      if (cloned?.outcome === 'busy') return;
      if (cloned?.outcome === 'done' && cloned.path) { answer({ choice: 'done', path: cloned.path, location }); return; }
      // No answer means the host no longer holds this sheet open: back to rest, with nothing to show.
      if (cloned?.outcome === 'failed') setFailure({ kind: cloned.kind, message: cloned.message || last?.message || cloned.outcome });
    } catch (error) {
      setFailure({ kind: null, message: reason(error) });
    }
    setBusy(false);
  };
  // Cancel stops a running clone (which removes what it wrote) and keeps the sheet; at rest it closes the sheet.
  const cancel = () => {
    if (busy) void bridge.call('gitCreateCancel', { dialogId: id }).catch(() => {});
    else answer({ choice: 'cancel' });
  };
  // git makes the target at once: no "already exists" line while the clone runs.
  const problem = shownProblem(last, name, busy);
  return (
    <Sheet id={id} width={460} label={dialog.title} onCancel={cancel}>
      <Heading title={dialog.title} subtitle={dialog.subtitle} />
      <fieldset className="create-grid" disabled={busy}>
        <label className="create-label" htmlFor={urlId}>{labels.url}</label>
        <input id={urlId} className="sheet-field" data-autofocus placeholder={labels.urlPlaceholder} value={url} spellCheck={false} autoComplete="off" data-testid="workspace-git-url"
          onChange={(event) => setUrl(event.target.value)}
          onKeyDown={(event) => { if (event.key === 'Enter' && !event.nativeEvent.isComposing) { event.preventDefault(); void create(); } }} />
        <span />
        <div className="create-probe">
          {probe.state === 'checking' && <span className="quiet" data-testid="workspace-git-probe"><span className="spinner" /> {labels.checking}</span>}
          {probe.state === 'reachable' && <span className="tone-text-done" data-testid="workspace-git-probe">{'✓ ' + labels.reachable}</span>}
          {probe.state === 'failed' && <GitError kind={probe.kind ?? null} message={probe.message ?? ''} labels={labels} onOpenSetup={openSetup} testId="workspace-git-probe" />}
          <span className="spacer" />
          <button type="button" className="link-button" disabled={url.trim() === ''} data-testid="workspace-git-test"
            onClick={() => { probeNow.current = true; setProbeToken((token) => token + 1); }}>{labels.test}</button>
        </div>
        <span className="create-label">{labels.location}</span>
        <LocationRow labels={labels} location={location} onPick={setLocation} />
        <label className="create-label" htmlFor={nameId}>{labels.name}</label>
        <input id={nameId} className="sheet-field" placeholder={labels.name} value={name} data-testid="workspace-git-name"
          onChange={(event) => { setName(event.target.value); setEdited(true); }}
          onKeyDown={(event) => { if (event.key === 'Enter' && !event.nativeEvent.isComposing) { event.preventDefault(); void create(); } }} />
      </fieldset>
      <PathPreview labels={labels} name={name} check={last} />
      {problem && <div className="sheet-caption tone-text-err" data-testid="workspace-create-problem">{problem}</div>}
      {failure && <GitError kind={failure.kind} message={failure.message} labels={labels} onOpenSetup={openSetup} testId="workspace-git-clone-error" />}
      {busy && (
        <div className="create-progress" data-testid="workspace-git-progress">
          <progress max={1} value={progress?.fraction ?? 0} />
          <span className="quiet small">{progress?.text ?? labels.starting}</span>
        </div>
      )}
      <div className="sheet-buttons">
        <span className="spacer" />
        <button ref={cancelButton} type="button" className="plain-button" onClick={cancel} data-testid="workspace-git-cancel">{labels.cancel}</button>
        <button type="button" className="accent-button" disabled={!ready} onClick={() => void create()} data-testid="workspace-git-create">{labels.create}</button>
      </div>
    </Sheet>
  );
}
