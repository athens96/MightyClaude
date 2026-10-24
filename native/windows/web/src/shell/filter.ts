// The sidebar's search (MightyClaude.Core/AddPaneMenu.cs Filtered): name or path, ignoring case and surrounding spaces.
export function filterWorkspaces<T extends { name: string; path: string }>(workspaces: readonly T[], search: string): T[] {
  const needle = search.trim().toLowerCase();
  if (needle.length === 0) return [...workspaces];
  return workspaces.filter((w) => w.name.toLowerCase().includes(needle) || w.path.toLowerCase().includes(needle));
}
