import { useEffect } from 'react';
import { Shell } from '../shell/Shell';
import { themeName } from '../theme/tokens';
import { useStoreState } from './context';

export function App() {
  const { snapshot } = useStoreState();
  const theme = themeName(snapshot?.theme);
  useEffect(() => {
    document.documentElement.dataset.theme = theme;
  }, [theme]);
  return <Shell snapshot={snapshot} />;
}
