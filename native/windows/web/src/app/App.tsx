import { useEffect } from 'react';
import { Shell } from '../shell/Shell';
import { applyText } from '../theme/text';
import { themeName } from '../theme/tokens';
import { useStoreState } from './context';

export function App() {
  const { snapshot, text } = useStoreState();
  useEffect(() => { applyText(document, text); }, [text]);
  const theme = themeName(snapshot?.theme);
  useEffect(() => {
    document.documentElement.dataset.theme = theme;
  }, [theme]);
  return <Shell snapshot={snapshot} />;
}
