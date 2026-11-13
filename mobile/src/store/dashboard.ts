import { create } from 'zustand';

/** What a tab-level host poll last said, for the banner and the pull-to-refresh spinner. */
export interface HostPollStatus {
  loading: boolean;
  error?: string;
  needsRepair: boolean;
}

/** How each paired host's background poll is doing ("현황" shows every host at once). */
interface DashboardStore {
  polls: Record<string, HostPollStatus>;
  setPoll: (hostId: string, status: HostPollStatus | undefined) => void;
}

export const useDashboardStore = create<DashboardStore>((set) => ({
  polls: {},
  setPoll: (hostId, status) =>
    set((prev) => {
      const current = prev.polls[hostId];
      if (status === undefined) {
        if (current === undefined) return prev;
        const polls = { ...prev.polls };
        delete polls[hostId];
        return { polls };
      }
      if (
        current &&
        current.loading === status.loading &&
        current.error === status.error &&
        current.needsRepair === status.needsRepair
      ) {
        return prev;
      }
      return { polls: { ...prev.polls, [hostId]: status } };
    }),
}));

/** The live poll's `refresh`, per host; functions stay out of the store's state. */
const refreshers = new Map<string, () => void>();

export function registerHostRefresh(hostId: string, refresh: () => void): () => void {
  refreshers.set(hostId, refresh);
  return () => {
    if (refreshers.get(hostId) === refresh) refreshers.delete(hostId);
  };
}

/** Asks the tab-level poll of one host to read the host again now. */
export function refreshHostPoll(hostId: string | undefined): void {
  if (hostId) refreshers.get(hostId)?.();
}
