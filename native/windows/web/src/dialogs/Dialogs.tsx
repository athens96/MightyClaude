// The dialogs the window's flows ask the page to draw (MainWindow.WebShell.Shell.cs OpenWebDialog): a confirmation (remove
// workspace, delegation, parent close), the rename sheet (spec section 9), the resume sheets (section 3) and the two create
// sheets (WorkspaceCreate.tsx). Each answers with `dialogAnswer`; what the answer does stays the host's flow.
import { useEffect, useRef, useState, type KeyboardEvent, type ReactNode } from 'react';
import { useServices, useStoreState } from '../app/context';
import { ProviderMark } from '../conversation/ProviderMark';
import { BetaBadge, MiddlePath } from '../shell/parts';
import { Icon } from '../icons/Icon';
import type { DialogRequest, OpenDialog, ResumeRows } from '../shell/types';
import { GitCreate, NewFolder } from './WorkspaceCreate';
import { Pending } from '../pending/Pending';

/** A sheet over the window: focuses its `data-autofocus` control (else its first), and Esc is its cancel. */
export function Sheet({ id, width, label, children, onCancel }: { id: number; width: number; label: string; children: ReactNode; onCancel: () => void }) {
  const element = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const focusable = element.current?.querySelector<HTMLElement>('[data-autofocus]') ?? element.current?.querySelector<HTMLElement>('button, input');
    focusable?.focus();
  }, []);
  const key = (event: KeyboardEvent) => { if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); onCancel(); } };
  return (
    <div className="sheet-backdrop" data-testid="dialog" data-dialog-id={id}>
      <div className="sheet" role="dialog" aria-modal="true" aria-label={label} style={{ width }} ref={element} onKeyDown={key}>{children}</div>
    </div>
  );
}

function Confirm({ id, dialog, answer }: { id: number; dialog: Extract<DialogRequest, { type: 'confirm' }>; answer: (value: unknown) => void }) {
  return (
    <Sheet id={id} width={400} label={dialog.title} onCancel={() => answer(false)}>
      <h2 className="sheet-title">{dialog.title}</h2>
      <p className="sheet-message selectable">{dialog.message}</p>
      <div className="sheet-buttons">
        <span className="spacer" />
        <button type="button" className="plain-button" data-autofocus={dialog.defaultPrimary ? undefined : true} onClick={() => answer(false)}>{dialog.close}</button>
        {dialog.primary && <button type="button" className="accent-button" data-testid="dialog-primary" data-autofocus={dialog.defaultPrimary ? true : undefined} onClick={() => answer(true)}>{dialog.primary}</button>}
      </div>
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
    <Sheet id={id} width={360} label={dialog.title} onCancel={() => answer({ choice: 'cancel' })}>
      <h2 className="sheet-title">{dialog.title}</h2>
      <input ref={field} className="sheet-field" data-autofocus placeholder={dialog.field} aria-label={dialog.field} value={name} data-testid="rename-field"
        onChange={(event) => setName(event.target.value)} onKeyDown={(event) => { if (event.key === 'Enter' && !event.nativeEvent.isComposing) { event.preventDefault(); save(); } }} />
      <div className="sheet-caption">{dialog.hint}</div>
      {check.messages.map((message) => <div key={message} className="sheet-caption tone-text-err">{message}</div>)}
      <div className="sheet-buttons">
        {dialog.automatic && <button type="button" className="plain-button" onClick={() => answer({ choice: 'automatic' })}>{dialog.automatic}</button>}
        <span className="spacer" />
        <button type="button" className="plain-button" onClick={() => answer({ choice: 'cancel' })}>{dialog.cancel}</button>
        <button type="button" className="accent-button" disabled={!check.valid} onClick={save} data-testid="rename-save">{dialog.save}</button>
      </div>
    </Sheet>
  );
}

function ResumeChoice({ id, dialog, answer }: { id: number; dialog: Extract<DialogRequest, { type: 'resumeChoice' }>; answer: (value: unknown) => void }) {
  const { t } = useServices();
  return (
    <Sheet id={id} width={400} label={dialog.title} onCancel={() => answer(null)}>
      <div className="resume-choice-top">
        <ProviderMark provider={dialog.provider} size={20} />
        <div>
          <h2 className="sheet-title">{dialog.title}</h2>
          {dialog.checking ? <div className="quiet"><Pending busy showsLabel={false} /> {t('resume.loading')}</div> : <p className="sheet-message quiet">{dialog.message}</p>}
        </div>
      </div>
      <div className="sheet-buttons">
        <button type="button" className="plain-button" onClick={() => answer(null)}>{t('resume.cancel')}</button>
        <span className="spacer" />
        <button type="button" className="plain-button" disabled={dialog.checking} onClick={() => answer('resume')} data-testid="resume-choice-resume">{t('resume.choice.resume')}</button>
        <button type="button" className="accent-button" data-autofocus onClick={() => answer('new')} data-testid="resume-choice-new">{t('resume.choice.startNew')}</button>
      </div>
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
    <Sheet id={id} width={560} label={dialog.title} onCancel={() => answer(null)}>
      <div className="resume-list" style={{ height: 520 - 40 }}>
        <div>
          <div className="resume-title"><ProviderMark provider={dialog.provider} size={16} /><h2 className="sheet-title">{dialog.title}</h2></div>
          <MiddlePath path={dialog.path} className="mono quiet" />
        </div>
        {/* M/ResumeSessionViews.swift: the search field with its magnifier inside. */}
        <label className="resume-search">
          <Icon name="search" size={13} weight={2} />
          <input className="sheet-field" type="search" data-autofocus placeholder={t('resume.search')} aria-label={t('resume.search')} value={search} onChange={(event) => setSearch(event.target.value)} />
        </label>
        <div className="resume-rows" data-testid="resume-rows">
          {rows === null ? <div className="resume-empty"><Pending busy showsLabel={false} />{t('resume.loading')}</div>
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
        <div className="sheet-buttons">
          <span className="quiet small">{t('resume.note')}</span>
          <span className="spacer" />
          <button type="button" className="plain-button" onClick={() => answer(null)}>{t('resume.cancel')}</button>
        </div>
      </div>
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
  }
}

/** The newest dialog over the window; the others wait under it. */
export function Dialogs() {
  const dialogs = useStoreState().dialogs;
  const top = dialogs[dialogs.length - 1];
  return top ? <DialogView key={top.id} open={top} /> : null;
}
