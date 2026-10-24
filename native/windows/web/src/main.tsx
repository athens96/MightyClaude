import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './app/App';
import { ServicesContext } from './app/context';
import { Bridge, hostPort } from './bridge/bridge';
import { devHost } from './bridge/devHost';
import type { LocaleInfo } from './bridge/protocol';
import { loadTranslator } from './i18n/locale';
import { Store } from './store/store';
import { installTokens } from './theme/tokens';
import './styles.css';
import './conversation/conversation.css';
import './composer/composer.css';
import './composer/toolbar.css';
import './cards/cards.css';
import './mighty/mighty.css';
import './shell/shell.css';

async function start(bridge: Bridge): Promise<void> {
  installTokens(document);
  const store = new Store();
  bridge.onPush((message) => store.dispatch(message));
  window.addEventListener('error', (event) => void bridge.call('log', { level: 'error', message: String(event.message) }).catch(() => {}));
  const locale = await bridge.call<LocaleInfo>('getLocale');
  document.documentElement.lang = locale.language;
  const t = await loadTranslator(locale.language);
  const element = document.getElementById('root');
  if (!element) throw new Error('#root is missing');
  createRoot(element).render(
    <StrictMode>
      <ServicesContext.Provider value={{ bridge, store, t }}>
        <App />
      </ServicesContext.Provider>
    </StrictMode>,
  );
  // The host sends the whole state only after this, so nothing pushed before the store listens is lost.
  await bridge.call('ready');
}

const bridge = new Bridge(hostPort() ?? devHost());
void start(bridge).catch((error: unknown) => bridge.call('log', { level: 'error', message: 'start failed: ' + String(error) }).catch(() => {}));
