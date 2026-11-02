/**
 * What a rendered HTML preview may do when the page asks to load a URL. The page is the host's file shown from its own text, so it
 * never leaves the file: its own anchors (`about:blank#id`) stay, a tapped web link opens in the phone's browser, and everything
 * else (another scheme, a script's redirect, a frame, a subresource page) is refused.
 */
export type HtmlNavigation = 'stay' | 'browser' | 'refuse';

/** The page's own base: with it a `#anchor` is `about:blank#anchor` on both platforms. */
export const HTML_BASE_URL = 'about:blank';

export function htmlNavigation(url: string, isTopFrame: boolean | undefined): HtmlNavigation {
  if (url === HTML_BASE_URL || url.startsWith(`${HTML_BASE_URL}#`)) return 'stay';
  if (isTopFrame !== false && /^https?:\/\//i.test(url)) return 'browser';
  return 'refuse';
}
