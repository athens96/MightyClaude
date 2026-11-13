// The dialogs the window's flows ask the page to draw (MainWindow.WebShell.Shell.cs OpenWebDialog): a confirmation (remove
// workspace, delegation, parent close), the rename sheet (spec section 9), the resume sheets (section 3) and the two create
// sheets (WorkspaceCreate.tsx). Each answers with `dialogAnswer`; what the answer does stays the host's flow. All of them are
// the popup standard's alert (A) or form sheet (B) (DialogShell.tsx).
import { useEffect, useRef, useState, type ReactNode } from 'react';
import { useServices, useStoreState } from '../app/context';
import { ProviderMark } from '../conversation/ProviderMark';
import { BetaBadge, MiddlePath } from '../shell/parts';
import { Icon } from '../icons/Icon';
import type { DialogRequest, OpenDialog, ResumeRows } from '../shell/types';
import { GitCreate, NewFolder } from './WorkspaceCreate';
import { Pending } from '../pending/Pending';
import { DialogLoading, DialogShell } from './DialogShell';
import { diffLines } from '../pane/worktree';
import '../pane/worktree.css';

/**
 * A sheet over the window (popup standard kind A or B): its title (and subtitle) in the header, its form in the body, its
 * status and buttons in the footer; it focuses its `data-autofocus` control (else its first), and Esc is its cancel.
 */
export function Sheet({ id, width, label, kind = 'sheet', title, subtitle, icon, status, buttons, children, onCancel }: {
  id: number; width?: number; label: string; kind?: 'alert' | 'sheet'; title?: ReactNode; subtitle?: ReactNode; icon?: ReactNode;
  status?: ReactNode; buttons?: ReactNode; children?: ReactNode; onCancel: () => void;
}) {
  return (
    <DialogShell kind={kind} width={width} label={label} title={title ?? label} subtitle={subtitle} icon={icon} status={status} buttons={buttons} onCancel={onCancel}
      backdrop={{ 'data-testid': 'dialog', 'data-dialog-id': id }}>
      {children}
    </DialogShell>
  );
}

function Confirm({ id, dialog, answer }: { id: number; dialog: Extract<DialogRequest, { type: 'confirm' }>; answer: (value: unknown) => void }) {
  return (
    <Sheet id={id} kind="alert" label={dialog.title} onCancel={() => answer(false)}
      buttons={<>
        <button type="button" className="btn" data-autofocus={dialog.defaultPrimary ? undefined : true} onClick={() => answer(false)}>{dialog.close}</button>
        {dialog.primary && (
          <button type="button" className={'btn ' + (dialog.destructive ? 'danger' : 'primary')} data-testid="dialog-primary" data-autofocus={dialog.defaultPrimary ? true : undefined}
            onClick={() => answer(true)}>{dialog.primary}</button>
        )}
      </>}>
      {dialog.message && <p className="dialog-message selectable">{dialog.message}</p>}
    </Sheet>
  );
}

function Rename({ id, dialog, answer }: { id: number; dialog: Extract<DialogRequest, { type: 'rename' }>; answer: (value: unknown) => void }) {
  const { bridge } = useServices();
  const [name, setName] = useState(dialog.current);
  const [check, setCheck] = useState<{ valid: boolean; messages: string[] }>({ valid: true, messages: [] });
  const field = useRef<HTMLInputElement>(null);
  useEffect(() => { field.current?.select(); }, []);
  // Every rule is RenameSupport's, asked of the host as the name changes.
  useEffect(() => {
    let live = true;
    void bridge.call<{ valid: boolean; messages: string[] }>('renameCheck', { text: name }).then((value) => { if (live && value) setCheck(value); }, () => {});
    return () => { live = false; };
  }, [bridge, name]);
  const save = () => { if (check.valid) answer({ choice: 'save', name }); };
  return (
    <Sheet id={id} width={360} label={dialog.title} onCancel={() => answer({ choice: 'cancel' })}
      status={dialog.automatic && <button type="button" className="btn text" onClick={() => answer({ choice: 'automatic' })}>{dialog.automatic}</button>}
      buttons={<>
        <button type="button" className="btn" onClick={() => answer({ choice: 'cancel' })}>{dialog.cancel}</button>
        <button type="button" className="btn primary" disabled={!check.valid} onClick={save} data-testid="rename-save">{dialog.save}</button>
      </>}>
      <input ref={field} className="dialog-field" data-autofocus placeholder={dialog.field} aria-label={dialog.field} value={name} data-testid="rename-field"
        onChange={(event) => setName(event.target.value)} onKeyDown={(event) => { if (event.key === 'Enter' && !event.nativeEvent.isComposing) { event.preventDefault(); save(); } }} />
      <div className="sheet-caption">{dialog.hint}</div>
      {check.messages.map((message) => <div key={message} className="sheet-caption tone-text-err">{message}</div>)}
    </Sheet>
  );
}

function ResumeChoice({ id, dialog, answer }: { id: number; dialog: Extract<DialogRequest, { type: 'resumeChoice' }>; answer: (value: unknown) => void }) {
  const { t } = useServices();
  return (
    <Sheet id={id} width={460} label={dialog.title} icon={<ProviderMark provider={dialog.provider} size={20} />} onCancel={() => answer(null)}
      buttons={<>
        <button type="button" className="btn" onClick={() => answer(null)}>{t('resume.cancel')}</button>
        <button type="button" className="btn" disabled={dialog.checking} onClick={() => answer('resume')} data-testid="resume-choice-resume">{t('resume.choice.resume')}</button>
        <button type="button" className="btn primary" data-autofocus onClick={() => answer('new')} data-testid="resume-choice-new">{t('resume.choice.startNew')}</button>
      </>}>
      {dialog.checking ? <div className="quiet"><Pending busy showsLabel={false} /> {t('resume.loading')}</div> : <p className="dialog-message">{dialog.message}</p>}
    </Sheet>
  );
}

function ResumeList({ id, dialog, answer }: { id: number; dialog: Extract<DialogRequest, { type: 'resumeList' }>; answer: (value: unknown) => void }) {
  const { bridge, t } = useServices();
  const [search, setSearch] = useState('');
  const [all, setAll] = useState(false);
  const [rows, setRows] = useState<ResumeRows | null>(null);
  useEffect(() => {
    let live = true;
    void bridge.call<ResumeRows | null>('resumeRows', { search, all }).then((value) => { if (live) setRows(value ?? { rows: [] }); }, () => {});
    return () => { live = false; };
  }, [bridge, search, all]);
  return (
    <Sheet id={id} width={560} label={dialog.title} icon={<ProviderMark provider={dialog.provider} size={16} />} subtitle={<MiddlePath path={dialog.path} className="mono" />}
      onCancel={() => answer(null)} status={<span className="quiet small">{t('resume.note')}</span>}
      buttons={<button type="button" className="btn" onClick={() => answer(null)}>{t('resume.cancel')}</button>}>
      {/* The Mac's 520-high sheet, less its header and footer. */}
      <div className="resume-list" style={{ height: 520 - 48 - 52 - 16 }}>
        {/* M/ResumeSessionViews.swift: the search field with its magnifier inside. */}
        <label className="resume-search">
          <Icon name="search" size={13} weight={2} />
          <input className="dialog-field sheet-field" type="search" data-autofocus placeholder={t('resume.search')} aria-label={t('resume.search')} value={search} onChange={(event) => setSearch(event.target.value)} />
        </label>
        <div className="resume-rows" data-testid="resume-rows">
          {rows === null ? <DialogLoading />
            : rows.rows.length === 0 ? <div className="resume-empty"><strong>{rows.empty}</strong>{rows.reason && <span className="quiet">{rows.reason}</span>}</div>
            : rows.rows.map((row) => (
              <button key={row.id} type="button" className="resume-row" title={row.help} onClick={() => answer(row.id)}>
                <ProviderMark provider={row.provider} size={14} />
                <span className="resume-row-text">
                  <span className={'resume-row-title' + (row.untitled ? ' quiet' : '')}>{row.title}{row.beta && <BetaBadge />}</span>
                  <span className="quiet small">{row.details}</span>
                  {row.busy && <span className="tone-text-wait small">{t('resume.recentlyModified')}</span>}
                </span>
              </button>
            ))}
        </div>
        <div className="resume-footer">
          <label className="small"><input type="checkbox" checked={all} onChange={(event) => setAll(event.target.checked)} /> {t('resume.showAll')}</label>
          {rows?.hidden && <span className="quiet small">{rows.hidden}</span>}
        </div>
      </div>
    </Sheet>
  );
}

/** Where a new agent pane works (MainWindow.PaneWorktree.cs AskPaneWorktreeChoice): its own worktree, always for this workspace, or the folder. */
function PaneWorktreeChoice({ id, dialog, answer }: { id: number; dialog: Extract<DialogRequest, { type: 'paneWorktreeChoice' }>; answer: (value: unknown) => void }) {
  return (
    <Sheet id={id} width={520} label={dialog.title} icon={<Icon name="git-branch" size={18} />} onCancel={() => answer(null)}
      buttons={<>
        <button type="button" className="btn" onClick={() => answer(null)}>{dialog.cancel}</button>
        <button type="button" className="btn" onClick={() => answer('same')} data-testid="pane-worktree-same" data-autofocus={dialog.canWorktree ? undefined : true}>{dialog.sameFolder}</button>
        {dialog.canWorktree && <button type="button" className="btn" onClick={() => answer('always')} data-testid="pane-worktree-always">{dialog.always}</button>}
        {dialog.canWorktree && <button type="button" className="btn primary" data-autofocus onClick={() => answer('worktree')} data-testid="pane-worktree-new">{dialog.worktree}</button>}
      </>}>
      <p className="dialog-message">{dialog.message}</p>
    </Sheet>
  );
}

/** A one-line worktree setting: the setup command or the files copied into new worktrees. */
function PaneWorktreeText({ id, dialog, answer }: { id: number; dialog: Extract<DialogRequest, { type: 'paneWorktreeText' }>; answer: (value: unknown) => void }) {
  const [text, setText] = useState(dialog.current);
  return (
    <Sheet id={id} width={460} label={dialog.title} onCancel={() => answer(null)}
      buttons={<>
        <button type="button" className="btn" onClick={() => answer(null)}>{dialog.cancel}</button>
        <button type="button" className="btn primary" onClick={() => answer(text)} data-testid="pane-worktree-text-save">{dialog.save}</button>
      </>}>
      <p className="dialog-message">{dialog.message}</p>
      <input className="dialog-field" data-autofocus placeholder={dialog.placeholder} aria-label={dialog.title} value={text} data-testid="pane-worktree-text"
        onChange={(event) => setText(event.target.value)} onKeyDown={(event) => { if (event.key === 'Enter' && !event.nativeEvent.isComposing) { event.preventDefault(); answer(text); } }} />
    </Sheet>
  );
}

/** The files a worktree pane changed against its base, and the picked file's diff (paneWorktreeDiff). */
function PaneWorktreeChanges({ id, dialog, answer }: { id: number; dialog: Extract<DialogRequest, { type: 'paneWorktreeChanges' }>; answer: (value: unknown) => void }) {
  const { bridge } = useServices();
  const [picked, setPicked] = useState<string | null>(dialog.files[0]?.path ?? null);
  const [diff, setDiff] = useState<string | null>(null);
  useEffect(() => {
    let live = true; setDiff(null);
    if (picked) void bridge.call<string | null>('paneWorktreeDiff', { id: dialog.paneId, path: picked }).then((value) => { if (live) setDiff(value ?? ''); }, () => {});
    return () => { live = false; };
  }, [bridge, dialog.paneId, picked]);
  return (
    <Sheet id={id} width={880} label={dialog.title} onCancel={() => answer(null)} buttons={<button type="button" className="btn" data-autofocus onClick={() => answer(null)}>OK</button>}>
      {dialog.files.length === 0 ? <p className="dialog-message">{dialog.none}</p> : (
        <div className="worktree-changes">
          <ul>{dialog.files.map((file) => (
            <li key={file.path}><button type="button" className={file.path === picked ? 'active' : ''} title={file.path} onClick={() => setPicked(file.path)}>{file.status + '  ' + file.path}</button></li>
          ))}</ul>
          {picked ? diff === null ? <Pending busy showsLabel={false} /> : (
            <pre className="worktree-diff selectable">{diffLines(diff).map((line, index) => <div key={index} className={line.kind}>{line.text || ' '}</div>)}</pre>
          ) : <p className="quiet">{dialog.pick}</p>}
        </div>
      )}
    </Sheet>
  );
}

/** A workspace's pane worktrees: open, kept and found ones, each with its actions (MainWindow.PaneWorktree.cs ShowWorktreeList). */
function PaneWorktreeList({ id, dialog, answer }: { id: number; dialog: Extract<DialogRequest, { type: 'paneWorktreeList' }>; answer: (value: unknown) => void }) {
  return (
    <Sheet id={id} width={640} label={dialog.title} onCancel={() => answer(null)} buttons={<button type="button" className="btn" data-autofocus onClick={() => answer(null)}>{dialog.close}</button>}>
      {dialog.rows.length === 0 ? <p className="dialog-message">{dialog.empty}</p> : dialog.rows.map((row) => (
        <div key={row.id} className="worktree-list-row" data-testid={'worktree-row-' + row.id}>
          <Icon name="git-branch" size={14} />
          <div className="text">
            <span><strong>{row.branch}</strong> · {row.originText}</span>
            <span className="quiet small">{row.counts}</span>
            <span className="quiet small" title={row.path}>{row.path}</span>
          </div>
          {row.actions.map((item) => (
            <button key={item.action} type="button" className={'btn small' + (item.action === 'delete' ? ' danger' : '')} onClick={() => answer({ id: row.id, action: item.action })}>{item.label}</button>
          ))}
        </div>
      ))}
    </Sheet>
  );
}

function DialogView({ open }: { open: OpenDialog }) {
  const { bridge, store } = useServices();
  const answer = (value: unknown) => {
    store.dispatch({ kind: 'dialogClose', id: open.id });
    void bridge.call('dialogAnswer', { id: open.id, answer: value }).catch(() => {});
  };
  const { dialog } = open;
  switch (dialog.type) {
    case 'confirm': return <Confirm id={open.id} dialog={dialog} answer={answer} />;
    case 'rename': return <Rename id={open.id} dialog={dialog} answer={answer} />;
    case 'resumeChoice': return <ResumeChoice id={open.id} dialog={dialog} answer={answer} />;
    case 'resumeList': return <ResumeList id={open.id} dialog={dialog} answer={answer} />;
    case 'newFolder': return <NewFolder id={open.id} dialog={dialog} answer={answer} />;
    case 'gitCreate': return <GitCreate id={open.id} dialog={dialog} answer={answer} />;
    case 'paneWorktreeChoice': return <PaneWorktreeChoice id={open.id} dialog={dialog} answer={answer} />;
    case 'paneWorktreeText': return <PaneWorktreeText id={open.id} dialog={dialog} answer={answer} />;
    case 'paneWorktreeChanges': return <PaneWorktreeChanges id={open.id} dialog={dialog} answer={answer} />;
    case 'paneWorktreeList': return <PaneWorktreeList id={open.id} dialog={dialog} answer={answer} />;
  }
}

/** The newest dialog over the window; the others wait under it. */
export function Dialogs() {
  const dialogs = useStoreState().dialogs;
  const top = dialogs[dialogs.length - 1];
  return top ? <DialogView key={top.id} open={top} /> : null;
}
