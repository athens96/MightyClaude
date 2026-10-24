// The add-pane menu (spec section 3; MainWindow.Navigation.cs NewSessionMenu, DashboardWorkspaces.cs DashboardAddMenu): its
// entries and words come from the host (RemoteWorkspaceFeatures.AddPaneEntries, AddPaneTitle); each runs the window's own add.
import { useCallback } from 'react';
import { useServices } from '../app/context';
import { ProviderMark } from '../conversation/ProviderMark';
import { useMenu, type MenuItem } from './parts';
import type { AddMenu } from './types';

function EntryIcon({ entry, provider }: { entry: string; provider?: string }) {
  if (provider) return <ProviderMark provider={provider} size={12} />;
  const path = entry === 'shell' ? 'M2 3h12v10H2zM4 6l2 2-2 2M8 10h4'
    : entry === 'browser' ? 'M8 2a6 6 0 1 0 0 12A6 6 0 0 0 8 2zM2 8h12M8 2c2 2 2 10 0 12M8 2c-2 2-2 10 0 12'
    : entry === 'files' || entry === 'openProject' ? 'M2 4h4l1 1.5h7V13H2z'
    : entry === 'connect' ? 'M3 4h10v6H3zM6 13h4M8 10v3' : '';
  return path ? <svg width="12" height="12" viewBox="0 0 16 16" aria-hidden="true"><path d={path} fill="none" stroke="currentColor" strokeWidth="1.2" /></svg> : null;
}

/** Opens the add-pane menu under an element: for a workspace (the dashboard's, the sidebar's add row) or a tab group. */
export function useAddMenu(): (element: Element, options?: { workspaceId?: string; groupId?: string }) => void {
  const { bridge } = useServices();
  const menu = useMenu();
  return useCallback((element: Element, options: { workspaceId?: string; groupId?: string } = {}) => {
    void bridge.call<AddMenu>('addMenu', { workspaceId: options.workspaceId }).then((value) => {
      if (!value) return;
      const items: MenuItem[] = value.entries.map((entry) => entry.entry === '-' || entry.label === '' ? { separator: true } : {
        label: entry.label, shortcut: entry.shortcut, disabled: value.disabled && entry.entry !== 'connect', testId: 'add-entry-' + entry.entry,
        icon: <EntryIcon entry={entry.entry} provider={entry.provider} />,
        action: () => void bridge.call('addPaneEntry', { entry: entry.entry, groupId: options.groupId, workspaceId: options.workspaceId ?? value.workspaceId }).catch(() => {}),
      });
      menu.under(element, items);
    }, () => {});
  }, [bridge, menu]);
}
