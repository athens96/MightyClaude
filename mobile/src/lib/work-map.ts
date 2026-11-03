import type { WorkMapCommit, WorkMapLane, WorkMapRequest, WorkMapTopic, WorkMapVersion } from '@/api/types';
import { t } from '@/lib/i18n';
import type { Palette } from '@/theme';

/**
 * Display rules of the work map (docs/work-map.md "휴대폰"). The model itself — tags,
 * ranges, lanes, status — is the host's; the phone only decides how it is drawn.
 */

/** The host's lane order; the ratio bar and the lane dots follow it. */
export const LANE_ORDER: readonly WorkMapLane[] = ['mac', 'windows', 'phone', 'ci', 'etc'];

const MINIMUM_DOT = 6;
const DOT_RANGE = 14;

/** A dot's diameter: `6 + 14·√(n / largest)`, rounded; 0 for no commits. */
export function dotSize(count: number, largest: number): number {
  if (count <= 0 || largest <= 0) return 0;
  return Math.round(MINIMUM_DOT + DOT_RANGE * Math.sqrt(count / largest));
}

export function laneLabel(lane: WorkMapLane): string {
  switch (lane) {
    case 'mac':
      return t('workMap.lane.mac');
    case 'windows':
      return t('workMap.lane.windows');
    case 'phone':
      return t('workMap.lane.phone');
    case 'ci':
      return t('workMap.lane.ci');
    default:
      return t('workMap.lane.etc');
  }
}

/** Lane colours come from the theme palette, so they follow light and dark. */
export function laneColor(palette: Palette, lane: WorkMapLane): string {
  switch (lane) {
    case 'mac':
      return palette.blockAgent;
    case 'windows':
      return palette.run;
    case 'phone':
      return palette.done;
    case 'ci':
      return palette.wait;
    default:
      return palette.grey;
  }
}

export interface LaneShare {
  lane: WorkMapLane;
  /** 0–1 of the version's lane-counted commits. */
  share: number;
}

/** The ratio bar's segments: lanes with commits, in lane order. */
export function laneShares(counts: WorkMapVersion['counts']): LaneShare[] {
  const sum = LANE_ORDER.reduce((total, lane) => total + (counts[lane] ?? 0), 0);
  if (sum <= 0) return [];
  return LANE_ORDER.filter((lane) => (counts[lane] ?? 0) > 0).map((lane) => ({
    lane,
    share: (counts[lane] ?? 0) / sum,
  }));
}

/** "2026-11-02" → "11/02". */
export function shortDate(date: string | null): string {
  return date && date.length >= 10 ? `${date.slice(5, 7)}/${date.slice(8, 10)}` : '';
}

/** The version's name; an unnamed unreleased one is "다음 버전". */
export function versionName(version: WorkMapVersion): string {
  return version.version ?? version.tag?.replace(/^v/, '') ?? t('workMap.version.next');
}

/** Under a chip's ratio bar: the date, or "진행 중" until it is released. */
export function chipDate(version: WorkMapVersion): string {
  return version.status === 'released' ? shortDate(version.date) : t('workMap.overview.pending');
}

export function statusLabel(version: WorkMapVersion): string {
  switch (version.status) {
    case 'released':
      return t('workMap.status.released', { date: shortDate(version.date) });
    case 'releasing':
      return t('workMap.status.releasing', { step: version.releaseStep ?? '' });
    default:
      return t('workMap.status.unreleased');
  }
}

/** The topic's own commits, taken from its version. */
export function topicCommits(version: WorkMapVersion, topic: WorkMapTopic): WorkMapCommit[] {
  const wanted = new Set(topic.commits);
  return version.commits.filter((commit) => wanted.has(commit.sha));
}

/** The topic's own requests, taken from its version. */
export function topicRequests(version: WorkMapVersion, topic: WorkMapTopic): WorkMapRequest[] {
  const wanted = new Set(topic.requests);
  return version.requests.filter((request) => wanted.has(request.id));
}

/** "요청 있는 주제만" keeps the topics that name at least one request. */
export function topicsWithRequests(topics: readonly WorkMapTopic[]): WorkMapTopic[] {
  return topics.filter((topic) => topic.requests.length > 0);
}

/** Why the host has no map (`available: false`). */
export function unavailableReason(reason: string | null): string {
  switch (reason) {
    case 'notGit':
      return t('workMap.unavailable.notGit');
    case 'remote':
      return t('workMap.unavailable.remote');
    default:
      return t('workMap.unavailable.gitFailed');
  }
}
