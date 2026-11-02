import { useEffect, useState } from 'react';
import { useServices } from '../../app/context';
import type { FilePreview } from './types';

/** An HTML file the preview can draw as a page: the host's source preview of a .html or .htm file. */
export function isHtmlPreview(preview: FilePreview | null | undefined): boolean {
  return !!preview && !preview.failure && preview.kind === 'source' && /\.html?$/i.test(preview.name || preview.path);
}

/** The 미리보기 | 소스 pair the head shows for an HTML file (the Markdown pair's look and words). */
export function HtmlToggle({ source, onChoose }: { source: boolean; onChoose: (source: boolean) => void }) {
  const { t } = useServices();
  return (
    <div className="files-segment" role="group" aria-label={t('files.markdown.rendered') + ' / ' + t('files.markdown.source')} data-testid="html-toggle">
      <button type="button" className={source ? '' : 'chosen'} aria-pressed={!source} onClick={() => onChoose(false)}>{t('files.markdown.rendered')}</button>
      <button type="button" className={source ? 'chosen' : ''} aria-pressed={source} onClick={() => onChoose(true)}>{t('files.markdown.source')}</button>
    </div>
  );
}

/**
 * The page itself (Mac: WKWebView with read access to the file's folder). The host serves the file's folder under a name of its
 * own (`htmlFrame`, WebShell.HtmlHostSuffix) and the page shows it in a frame sandboxed to scripts only: with no
 * `allow-same-origin` the page runs in an opaque origin, so it cannot reach this window, its bridge or its storage, nor top-navigate,
 * open popups, submit forms or download. The host also refuses every frame navigation outside the mapped hosts, so links to
 * other sites do nothing.
 */
export function HtmlFrame({ paneId, path, name, reference }: { paneId: string; path: string; name: string; reference: boolean }) {
  const { bridge, t } = useServices();
  const [address, setAddress] = useState<string | null | 'loading'>('loading');
  useEffect(() => {
    let current = true;
    setAddress('loading');
    bridge.call<string | null>('htmlFrame', { paneId, path, ...(reference ? { reference: true } : {}) })
      .then((value) => { if (current) setAddress(value ?? null); }, () => { if (current) setAddress(null); });
    return () => { current = false; };
  }, [bridge, paneId, path, reference]);
  if (address === 'loading') return <div className="files-progress" />;
  if (!address) return <div className="files-placeholder" data-testid="html-failed">{t('files.preview.failed')}</div>;
  return <iframe className="files-html" data-testid="files-html" title={name} src={address} sandbox="allow-scripts" referrerPolicy="no-referrer" />;
}
