import { Linking, StyleSheet } from 'react-native';
import { WebView, type WebViewNavigation } from 'react-native-webview';
import { HTML_BASE_URL, htmlNavigation } from '@/lib/html-preview';
import { usePalette } from '@/theme';

/**
 * An .html file drawn as a web page from its own text, as the Mac's reference bubble draws it. It never
 * navigates away from the file: a web link opens in the phone's browser, any other load is refused, and the
 * page reads no local file.
 */
export function HtmlView({ html }: { html: string }) {
  const palette = usePalette();

  const allow = (request: WebViewNavigation & { isTopFrame?: boolean }) => {
    const verdict = htmlNavigation(request.url, request.isTopFrame);
    if (verdict === 'browser') void Linking.openURL(request.url).catch(() => undefined);
    return verdict === 'stay';
  };

  return (
    <WebView
      source={{ html, baseUrl: HTML_BASE_URL }}
      originWhitelist={['*']}
      onShouldStartLoadWithRequest={allow}
      setSupportMultipleWindows={false}
      allowFileAccess={false}
      allowFileAccessFromFileURLs={false}
      allowUniversalAccessFromFileURLs={false}
      allowsLinkPreview={false}
      mixedContentMode="never"
      style={[styles.fill, { backgroundColor: palette.background }]}
    />
  );
}

const styles = StyleSheet.create({ fill: { flex: 1 } });
