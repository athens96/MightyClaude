// The strip over a mirrored pane (MirrorStrip.tsx; M/RemoteMirrorViews.swift MirrorPaneBanner): what it says and what its hover says.
import type { MirrorStrip } from '../shell/types';

export interface MirrorStripModel {
  badge: string;
  state: string;
  tone: string;
  branch?: string;
  queued?: { text: string; help?: string };
  help: string;
  /** The words a screen reader hears for the whole strip. */
  label: string;
}

export function mirrorStrip(strip: MirrorStrip): MirrorStripModel {
  const queued = strip.queued ? { text: strip.queued, help: strip.queuedHelp ?? undefined } : undefined;
  const branch = strip.branch ?? undefined;
  return {
    badge: strip.badge, state: strip.state, tone: strip.tone, branch, queued, help: strip.help,
    label: [strip.badge, strip.state, branch, queued?.text].filter((part): part is string => !!part).join(' · '),
  };
}
