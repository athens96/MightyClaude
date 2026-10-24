import { memo, useCallback, useEffect, useRef, useState, type KeyboardEvent } from 'react';
import type { RunSession } from '../../store/models';
import { useServices } from '../../app/context';
import { Markdown } from '../../conversation/MarkdownView';
import { MiddlePath } from '../../shell/parts';
import { lineNumbers, sourceSpans, treeKey } from './tree';
import type { FilePreview, FileRow, FilesModel } from './types';

/** The symbols of the tree and the head (WinUI FileSymbol): folder, document, picture, page with lines. */
function Symbol({ row, symbol }: { row?: FileRow; symbol?: string }) {
  const kind = row?.dir ? 'folder' : symbol ?? row?.symbol ?? 'unsupported';
  return (
    <svg className={'files-symbol files-symbol-' + kind} viewBox="0 0 14 14" aria-hidden="true">
      {kind === 'folder' && <path d="M1.5 3.5h4l1.2 1.3h5.8v6.7h-11z" />}
      {kind === 'image' && <><rect x="1.5" y="2.5" width="11" height="9" rx="1" /><path d="M2.5 10.5l3-3 2 2 1.5-1.5 2.5 2.5" /></>}
      {kind === 'markdown' && <><path d="M3 1.5h5.5l2.5 2.5v8.5H3z" /><path d="M5 7h4M5 9h4" /></>}
      {kind === 'source' && <><path d="M3 1.5h5.5l2.5 2.5v8.5H3z" /><path d="M6 6.5 4.8 8 6 9.5M8 6.5 9.2 8 8 9.5" /></>}
      {kind === 'unsupported' && <path d="M3 1.5h5.5l2.5 2.5v8.5H3z" />}
    </svg>
  );
}

const Tree = memo(function Tree({ model, onRow, onKey }: { model: FilesModel; onRow: (row: FileRow) => void; onKey: (event: KeyboardEvent<HTMLDivElement>) => void }) {
  const list = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const row = list.current?.querySelector<HTMLElement>('[aria-selected="true"]');
    row?.scrollIntoView({ block: 'nearest' });
  }, [model.selected]);
  return (
    <div className="files-tree" role="tree" tabIndex={0} ref={list} data-testid="files-tree" aria-activedescendant={model.selected ? 'files-row-' + model.selected : undefined} onKeyDown={onKey}>
      {model.rows.map((row) => (
        <div key={row.path} className="files-row-group">
          <div id={'files-row-' + row.path} role="treeitem" className={'files-row' + (row.noise ? ' noise' : '')} aria-selected={row.path === model.selected} aria-expanded={row.dir && !model.filtering ? row.expanded : undefined}
            data-path={row.path} title={row.path} style={{ paddingLeft: 'calc(var(--space-xs) + ' + (8 + row.depth * 14) + 'px)' }} onClick={() => onRow(row)}>
            <span className="files-chevron">{row.dir && !model.filtering ? (row.expanded ? '▾' : '▸') : ''}</span>
            <Symbol row={row} />
            <span className="files-name">{row.name}</span>
            {row.symlink && <span className="files-link" aria-hidden="true">↗</span>}
            {model.filtering && row.path !== row.name && <MiddlePath className="files-row-path" path={row.path} />}
          </div>
          {row.caption && !model.filtering && (
            <div className={'files-caption' + (row.reports ? ' reports' : '')} style={{ paddingLeft: 8 + (row.depth + 1) * 14 + 29 + 'px' }}>{row.caption}</div>
          )}
        </div>
      ))}
      {model.notes.map((note) => <div key={note} className="files-note">{note}</div>)}
    </div>
  );
});

export function SourceView({ preview }: { preview: FilePreview }) {
  const text = preview.text ?? '';
  return (
    <div className={'files-source' + (preview.wraps ? ' wraps' : '')} data-testid="files-source">
      <pre className="files-lines" aria-hidden="true">{lineNumbers(preview.lines ?? 1)}</pre>
      <pre className="files-code">
        {sourceSpans(text, preview.tokens).map((span, index) => (span.kind ? <span key={index} className={'syntax-' + span.kind}>{span.text}</span> : span.text))}
      </pre>
    </div>
  );
}

/** Fit (never larger than 1:1), zoom out, 1:1 and zoom in (M/FilePaneView.swift:202-216, 327-352); a CSS zoom of the host's picture. */
export function ImageView({ preview, zoom }: { preview: FilePreview; zoom: number | null }) {
  const image = preview.image;
  if (!image) return null;
  const ratio = image.bitmap ? window.devicePixelRatio || 1 : 1;
  const width = image.width / ratio, height = image.height / ratio;
  const style = zoom === null ? { maxWidth: '100%', maxHeight: '100%', width, height: 'auto' } : { width: width * zoom, height: height * zoom, maxWidth: 'none' };
  return (
    <div className={'files-image' + (zoom === null ? ' fit' : '')} data-testid="files-image">
      <img src={image.url} alt={preview.name} style={style} draggable={false} />
    </div>
  );
}

function Unsupported({ preview, reveal }: { preview: FilePreview; reveal: () => void }) {
  const { t } = useServices();
  return (
    <div className="files-unsupported" data-testid="files-unsupported">
      <h2>{t('files.preview.unsupported')}</h2>
      <p className="files-unsupported-name">{preview.name}</p>
      <dl>
        <dt>{t('files.preview.size')}</dt><dd>{preview.size}</dd>
        {preview.modified && <><dt>{t('files.preview.modified')}</dt><dd>{preview.modified}</dd></>}
      </dl>
      {preview.reason && <p className="files-unsupported-reason">{preview.reason}</p>}
      <button type="button" onClick={reveal}>{t('menu.showInExplorer')}</button>
    </div>
  );
}

/**
 * The files pane (M/FilePaneView.swift; WinUI MainWindow.Files.cs): the filter row and the tree on the left, the preview of
 * the chosen file on the right, its head naming it with its tools (encoding, the Markdown switch, the zoom tools, open in its
 * app, show in Explorer). The tree's folders load through the host (WorkspaceFiles.List under the workspace), opened folders
 * and the filter are the workspace's FilePaneTree, kept across reopening as in WinUI.
 */
export function FilesView({ session }: { session: RunSession }) {
  const { bridge, t } = useServices();
  const paneId = session.id;
  const [model, setModel] = useState<FilesModel | null>(null);
  const [filter, setFilter] = useState('');
  const [preview, setPreview] = useState<FilePreview | null>(null);
  const [loading, setLoading] = useState(false);
  const [showSource, setShowSource] = useState(false);
  const [zoom, setZoom] = useState<number | null>(null);
  const request = useRef(0);
  const filterEdit = useRef(0);

  const call = useCallback(<T,>(method: string, args?: object) => bridge.call<T>(method, { paneId, ...args }), [bridge, paneId]);
  const apply = useCallback((value: FilesModel | null | undefined) => { if (value) setModel(value); }, []);

  const show = useCallback(async (path: string, debounce: boolean) => {
    const mine = ++request.current;
    if (debounce) await new Promise((done) => setTimeout(done, 100));
    if (mine !== request.current) return;
    setLoading(true);
    try {
      const value = await call<FilePreview>('filesPreview', { path });
      if (mine !== request.current) return;
      setPreview(value); setZoom(null);
    } catch {
      if (mine === request.current) setPreview({ path, name: '', kind: 'unsupported', failure: t('files.preview.failed'), banners: [] });
    } finally {
      if (mine === request.current) setLoading(false);
    }
  }, [call, t]);

  useEffect(() => {
    let live = true;
    void call<FilesModel>('filesState').then((value) => {
      if (!live || !value) return;
      setModel(value); setFilter(value.filter);
      if (value.preview) void show(value.preview, false);
    }, () => {});
    return () => { live = false; };
  }, [call, show]);

  const choose = (row: FileRow) => {
    void call<FilesModel>('filesSelect', { path: row.path }).then(apply, () => {});
    setModel((current) => (current ? { ...current, selected: row.path } : current));
    if (!row.dir) void show(row.path, true);
  };
  const onRow = (row: FileRow) => {
    if (row.dir) {
      if (model?.filtering) void call<FilesModel>('filesReveal', { path: row.path }).then((value) => { apply(value); if (value) setFilter(value.filter); }, () => {});
      else void call<FilesModel>('filesToggle', { path: row.path }).then(apply, () => {});
      setModel((current) => (current ? { ...current, selected: row.path } : current));
      return;
    }
    choose(row);
  };
  const onKey = (event: KeyboardEvent<HTMLDivElement>) => {
    if (!model) return;
    const action = treeKey(model.rows, model.selected, event.key, model.filtering);
    if (action.kind === 'none') return;
    event.preventDefault();
    const row = model.rows.find((candidate) => candidate.path === action.path);
    if (action.kind === 'select') { if (row) choose(row); else setModel({ ...model, selected: action.path }); }
    else if (action.kind === 'toggle') void call<FilesModel>('filesToggle', { path: action.path }).then(apply, () => {});
    else void call<FilesModel>('filesReveal', { path: action.path }).then((value) => { apply(value); if (value) setFilter(value.filter); }, () => {});
  };
  const onFilter = (text: string) => {
    setFilter(text);
    // The list follows once typing pauses for about 100 ms, not on every key.
    const edit = ++filterEdit.current;
    setTimeout(() => { if (edit === filterEdit.current) void call<FilesModel>('filesFilter', { text }).then(apply, () => {}); }, 100);
  };
  const reveal = () => { if (preview) void call('filesShowInExplorer', { path: preview.path }).catch(() => {}); };
  const open = () => { if (preview) void call('filesOpen', { path: preview.path }).catch(() => {}); };

  if (model && !model.available) return <div className="files-pane files-unavailable" data-testid="files-pane" />;
  const shows = preview && !preview.failure;
  const markdownRendered = preview?.kind === 'markdown' && preview.markdown && !showSource;
  const banners = markdownRendered ? preview.renderedBanners ?? [] : preview?.banners ?? [];
  const image = preview?.kind === 'image' ? preview.image : undefined;
  const step = (factor: number) => setZoom((current) => Math.min(8, Math.max(0.05, (current ?? 1) * factor)));
  return (
    <div className="files-pane" data-testid="files-pane">
      <div className="files-tree-column">
        <div className="files-filter-row">
          <svg className="files-filter-symbol" viewBox="0 0 12 12" aria-hidden="true"><path d="M1.5 3h9M3 6h6M4.5 9h3" /></svg>
          <input className="files-filter" data-testid="files-filter" value={filter} placeholder={t('files.tree.filter')} aria-label={t('files.tree.filter')} spellCheck={false} onChange={(event) => onFilter(event.target.value)} />
          <button type="button" className="files-tool" aria-label={t('files.tree.refresh')} title={t('files.tree.refresh')}
            onClick={() => { void call<FilesModel>('filesRefresh').then(apply, () => {}); if (preview?.path) void show(preview.path, false); }}>
            <svg viewBox="0 0 12 12" aria-hidden="true"><path d="M10 6a4 4 0 1 1-1.2-2.85M10 1.5v2.5H7.5" /></svg>
          </button>
        </div>
        {model && <Tree model={model} onRow={onRow} onKey={onKey} />}
      </div>
      <div className="files-preview-column" data-testid="files-preview" data-preview-path={preview?.path ?? ''} data-preview-kind={preview?.failure ? 'failed' : preview?.kind ?? ''}>
        {shows && (
          <div className="files-preview-head">
            <Symbol symbol={preview.kind} />
            <div className="files-preview-names">
              <span className="files-preview-title">{preview.name}</span>
              <MiddlePath className="files-preview-path" path={preview.path} />
            </div>
            <div className="files-preview-tools">
              {image && <span className="files-pixels">{image.label}</span>}
              {image && (
                <>
                  <button type="button" className="files-tool" disabled={!image.zoomable} aria-label={t('files.image.zoomOut')} title={t('files.image.zoomOut')} onClick={() => step(1 / 1.25)}>−</button>
                  <button type="button" className="files-tool" disabled={!image.zoomable} aria-label={t('files.image.fit')} title={t('files.image.fit')} onClick={() => setZoom(null)}>⤢</button>
                  <button type="button" className="files-tool files-tool-word" disabled={!image.zoomable} aria-label={t('files.image.actualSize')} title={t('files.image.actualSize')} onClick={() => setZoom(1)}>1:1</button>
                  <button type="button" className="files-tool" disabled={!image.zoomable} aria-label={t('files.image.zoomIn')} title={t('files.image.zoomIn')} onClick={() => step(1.25)}>+</button>
                </>
              )}
              {preview.kind === 'markdown' && preview.markdown && (
                <div className="files-segment" role="group">
                  <button type="button" className={showSource ? '' : 'chosen'} aria-pressed={!showSource} onClick={() => setShowSource(false)}>{t('files.markdown.rendered')}</button>
                  <button type="button" className={showSource ? 'chosen' : ''} aria-pressed={showSource} onClick={() => setShowSource(true)}>{t('files.markdown.source')}</button>
                </div>
              )}
              {preview.encoding && <span className="files-encoding" title={t('files.preview.encoding')}>{preview.encoding}</span>}
              <button type="button" className="files-tool" aria-label={t('graph.resultFiles.openFile', { path: preview.name })} title={t('graph.resultFiles.openFile', { path: preview.name })} onClick={open}>
                <svg viewBox="0 0 14 14" aria-hidden="true"><path d="M8 2h4v4M12 2 6.5 7.5M10.5 8.5V12h-8.5V3.5H5.5" /></svg>
              </button>
              <button type="button" className="files-tool" data-testid="files-reveal" aria-label={t('menu.showInExplorer')} title={t('menu.showInExplorer')} onClick={reveal}>
                <svg viewBox="0 0 14 14" aria-hidden="true"><path d="M1.5 3.5h4l1.2 1.3h5.8v6.7h-11z" /></svg>
              </button>
            </div>
          </div>
        )}
        {banners.map((banner) => <div key={banner} className="files-banner">{banner}</div>)}
        <div className="files-preview-body">
          {loading && !preview && <div className="files-progress" />}
          {!preview && !loading && <div className="files-placeholder">{t('files.preview.placeholder')}</div>}
          {preview?.failure && <div className="files-placeholder" data-testid="files-failure">{preview.failure}</div>}
          {shows && preview.kind === 'source' && <SourceView preview={preview} />}
          {shows && preview.kind === 'markdown' && (markdownRendered ? <div className="files-markdown"><Markdown text={preview.text ?? ''} /></div> : <SourceView preview={preview} />)}
          {shows && preview.kind === 'image' && (image ? <ImageView preview={preview} zoom={zoom} /> : <Unsupported preview={preview} reveal={reveal} />)}
          {shows && preview.kind === 'unsupported' && <Unsupported preview={preview} reveal={reveal} />}
        </div>
      </div>
    </div>
  );
}
