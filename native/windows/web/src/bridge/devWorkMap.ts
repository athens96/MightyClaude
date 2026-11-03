// The work map's model for `npm run dev`, as the host would build it from a small repository (Core WorkMap.cs): three versions, the
// newest still being released, one of them with topics. Shapes and words only; nothing here is read from a real history.
import type { WorkMapCommit, WorkMapModel, WorkMapRequest, WorkMapVersion } from '../panes/workmap/model';

const commit = (sha: string, lanes: string[], scope: string | null, subject: string, files: string[]): WorkMapCommit => ({
  sha, date: '2026-11-02T20:58:00+09:00', lanes, scope, subject, files, filesCount: files.length,
});

const request = (id: string, text: string, commits: string[]): WorkMapRequest => ({ id: 'dev/' + id, paneId: 'dev', text, date: '2026-11-02T20:58:00+09:00', files: [], commits });

const nextCommits = [
  commit('19c184ab', ['windows'], 'windows', 'Settings in the One UI layout', ['native/windows/web/src/native/Surfaces.tsx', 'native/windows/web/src/native/native.css']),
  commit('739f8792', ['mac'], 'mac', 'Settings in the One UI layout, as Windows draws it', ['native/macos/Sources/MightyClaude/SettingsViews.swift']),
  commit('8e444a8d', ['windows', 'phone'], 'windows, phone', 'an HTML file previews as its page', ['native/windows/web/src/panes/files/HtmlView.tsx', 'mobile/src/components/html-view.tsx']),
  commit('5a129f95', ['mac'], 'mac tests', 'no test blocks the cooperative pool', ['native/macos/Tests/TestBlocking.swift']),
  commit('ec51868a', ['ci'], 'release', '0.4.49: One UI settings, HTML files preview as pages', ['VERSION', 'release/0.4.49-notes.md']),
];

const next: WorkMapVersion = {
  key: 'next', version: '0.4.49', tag: null, date: '2026-11-03', status: 'releasing', releaseStep: 'ci', rangeEnd: 'a'.repeat(40), total: 5, truncated: false,
  counts: { mac: 2, windows: 2, phone: 1, ci: 1 }, commits: nextCommits, topicsStale: true, summaryError: null,
  topics: [
    { title: 'One UI settings', summary: 'Mac and Windows settings in big titles, round cards and large switches.', lanes: ['mac', 'windows'], commits: ['19c184ab', '739f8792'], requests: ['dev/r1', 'dev/r2'] },
    { title: 'HTML files as pages', summary: 'File bubbles, the files pane and the phone show HTML like a browser.', lanes: ['windows', 'phone'], commits: ['8e444a8d'], requests: [] },
    { title: 'Mac test stability', summary: 'Tests no longer block the async pool.', lanes: ['mac'], commits: ['5a129f95'], requests: [] },
    { title: 'Release', summary: '', lanes: ['ci'], commits: ['ec51868a'], requests: [] },
  ],
  requests: [request('r1', 'Make the settings look like One UI, show me a sample first', ['19c184ab']), request('r2', 'Do the same on the Mac and commit', ['739f8792'])],
};

const plain = (key: string, date: string, counts: WorkMapVersion['counts'], total: number): WorkMapVersion => ({
  key, version: key.slice(1), tag: key, date, status: 'released', releaseStep: null, rangeEnd: 'b'.repeat(40), total, truncated: false, counts,
  commits: [commit('0a970bac', ['phone'], 'phone', 'capabilities are asked again whenever the tunnel comes back', ['mobile/src/hooks/use-capabilities.ts'])],
  topics: null, topicsStale: false, summaryError: null, requests: [],
});

export function devWorkMap(): WorkMapModel {
  return {
    available: true, reason: null, head: '4d4d3ba9', lanes: ['mac', 'windows', 'phone', 'ci', 'etc'], summarizing: null,
    versions: [next, plain('v0.4.48', '2026-11-02', { mac: 9, windows: 8, phone: 6, ci: 6 }, 20), plain('v0.4.47', '2026-10-29', { mac: 17, windows: 19, phone: 4, ci: 3, etc: 20 }, 61)],
  };
}
