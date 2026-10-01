import { create } from 'zustand';

/** What a tab-level host poll last said, for the banner and the pull-to-refresh spinner. */
export interface HostPollStatus {
  loading: boolean;
  error?: string;
  needsRepair: boolean;
}

/**
 * Which paired host the "현황" tab shows, and how each host's background poll is doing.
 * Kept for as long as the app runs; a host that is no longer paired falls back to the
 * first one (`resolveSelectedHost`).
 */
interface DashboardStore {
  selectedHostId?: string;
  polls: Record<string, HostPollStatus>;
  selectHost: (hostId: string) => void;
  setPoll: (hostId: string, status: HostPollStatus | undefined) => void;
}

export const useDashboardStore = create<DashboardStore>((set) => ({
  selectedHostId: undefined,
  polls: {},
  selectHost: (hostId) => set({ selectedHostId: hostId }),
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
