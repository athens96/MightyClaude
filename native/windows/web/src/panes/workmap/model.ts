// The work map's model as the host builds it (Core WorkMap.cs, docs/work-map.md) and the page's rules over it: the overview's dots,
// the lane chips, the topic filter and what a topic's detail lists. The host omits a null, so a missing value reads as null.

export type Lane = 'mac' | 'windows' | 'phone' | 'ci' | 'etc';

export interface WorkMapCommit {
  sha: string;
  date: string;
  lanes: string[];
  scope?: string | null;
  subject: string;
  files: string[];
  filesCount: number;
}

export interface WorkMapRequest {
  id: string;
  paneId: string;
  text: string;
  date?: string | null;
  files: string[];
  commits: string[];
}

export interface WorkMapTopic {
  title: string;
  summary: string;
  lanes: string[];
  commits: string[];
  requests: string[];
}

export interface WorkMapVersion {
  key: string;
  version?: string | null;
  tag?: string | null;
  date?: string | null;
  status: 'released' | 'releasing' | 'unreleased';
  releaseStep?: string | null;
  rangeEnd: string;
  total: number;
  truncated: boolean;
  counts: Partial<Record<string, number>>;
  commits: WorkMapCommit[];
  topics?: WorkMapTopic[] | null;
  topicsStale: boolean;
  summaryError?: string | null;
  requests: WorkMapRequest[];
}

export interface WorkMapModel {
  available: boolean;
  reason?: 'notGit' | 'remote' | 'gitFailed' | null;
  head?: string | null;
  lanes: string[];
  summarizing?: string | null;
  versions: WorkMapVersion[];
}

/** The lanes of the chips (CI and release included); `etc` has no chip and is always shown. */
export const chipLanes: readonly Lane[] = ['mac', 'windows', 'phone', 'ci'];

/** The design token each lane is drawn in: mac = agent, windows = run, phone = done, ci = wait, etc = stop. */
export const laneInk: Record<string, string> = { mac: 'var(--agent)', windows: 'var(--run)', phone: 'var(--done)', ci: 'var(--wait)', etc: 'var(--stop)' };

/** The overview dot's diameter, 6 + 14·√(n / largest), 0 for none (fixture dotSize). */
export function dotSize(count: number, largest: number): number {
  return count <= 0 || largest <= 0 ? 0 : Math.round(6 + 14 * Math.sqrt(count / largest));
}

/** The largest lane count of any version: the dot of that many is the biggest. */
export function largestCount(versions: readonly WorkMapVersion[]): number {
  return Math.max(0, ...versions.flatMap((version) => Object.values(version.counts).map((n) => n ?? 0)));
}

/** The overview's columns, oldest to newest (the model lists the newest first). */
export function overviewColumns(versions: readonly WorkMapVersion[]): WorkMapVersion[] {
  return [...versions].reverse();
}

/** The name a version is shown by: its version, or for an unnamed unreleased one the caller's word. */
export function versionName(version: WorkMapVersion, next: string): string {
  return version.version ?? (version.key === 'next' ? next : version.key.replace(/^v/, ''));
}

/** The bar of a version card: each lane's share of its commits, in lane order (a commit of two lanes counts in both). */
export function laneShares(counts: Partial<Record<string, number>>, lanes: readonly string[]): { lane: string; share: number }[] {
  const total = lanes.reduce((sum, lane) => sum + (counts[lane] ?? 0), 0);
  return total === 0 ? [] : lanes.filter((lane) => (counts[lane] ?? 0) > 0).map((lane) => ({ lane, share: (counts[lane] ?? 0) / total }));
}

/** The requests a topic lists, in the model's order. */
export function topicRequests(version: WorkMapVersion, topic: WorkMapTopic): WorkMapRequest[] {
  return topic.requests.map((id) => version.requests.find((request) => request.id === id)).filter((request): request is WorkMapRequest => request !== undefined);
}

/** The commits a topic lists, in the topic's order. */
export function topicCommits(version: WorkMapVersion, topic: WorkMapTopic): WorkMapCommit[] {
  return topic.commits.map((sha) => version.commits.find((commit) => commit.sha === sha)).filter((commit): commit is WorkMapCommit => commit !== undefined);
}

/** The files a topic's commits changed, each once, in the order they come (capped at `limit`). */
export function topicFiles(version: WorkMapVersion, topic: WorkMapTopic, limit = 60): string[] {
  const files = new Set<string>();
  for (const commit of topicCommits(version, topic)) for (const file of commit.files) { if (files.size >= limit) return [...files]; files.add(file); }
  return [...files];
}

/** Whether a lane is shown: the chips switch four, `etc` is never off. */
export function laneOn(lane: string, off: ReadonlySet<string>): boolean {
  return !off.has(lane);
}

/** A topic card is dimmed (not hidden) when every platform it touched is switched off. */
export function topicDimmed(topic: WorkMapTopic, off: ReadonlySet<string>): boolean {
  return topic.lanes.length > 0 && topic.lanes.every((lane) => off.has(lane));
}

/** The topic cards of a version: with "요청 있는 주제만" the ones without a request go. */
export function visibleTopics(version: WorkMapVersion, onlyWithRequests: boolean): { topic: WorkMapTopic; index: number }[] {
  return (version.topics ?? []).map((topic, index) => ({ topic, index })).filter(({ topic }) => !onlyWithRequests || topicRequests(version, topic).length > 0);
}

/** "2026-11-02" → "11/02". */
export function shortDate(date: string | null | undefined): string {
  return date && date.length >= 10 ? `${date.slice(5, 7)}/${date.slice(8, 10)}` : '';
}

/** "11/02 20:58" for a request's or commit's time as the host wrote it (the offset it was made in). */
export function shortStamp(stamp: string | null | undefined): string {
  return stamp && stamp.length >= 16 ? `${stamp.slice(5, 7)}/${stamp.slice(8, 10)} ${stamp.slice(11, 16)}` : '';
}

/** The pick after a model arrives: the chosen version if it is still there, else the newest. */
export function pickVersion(model: WorkMapModel, chosen: string | null): string | null {
  return model.versions.some((version) => version.key === chosen) ? chosen : model.versions[0]?.key ?? null;
}
