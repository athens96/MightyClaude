import { describe, expect, it } from 'vitest';
import { mirrorStrip } from './mirror';

describe("a mirrored pane's strip", () => {
  it('says where the pane runs, its branch and the queue there', () => {
    const model = mirrorStrip({ badge: '원격 창', state: 'studio-mac에서 실행 중 · 연결됨', tone: 'done', branch: 'mighty/pane-1 ← main', queued: '대기 중인 요청 2개', queuedHelp: 'one\ntwo', help: 'This pane runs on studio-mac.' });
    expect(model.label).toBe('원격 창 · studio-mac에서 실행 중 · 연결됨 · mighty/pane-1 ← main · 대기 중인 요청 2개');
    expect(model.queued).toEqual({ text: '대기 중인 요청 2개', help: 'one\ntwo' });
    expect(model.branch).toBe('mighty/pane-1 ← main');
    expect(model.tone).toBe('done');
  });

  it('leaves the branch and queue out when the host pane has none', () => {
    const model = mirrorStrip({ badge: '원격 창', state: 'studio-mac에서 실행 중 · 연결 끊김', tone: 'idle', branch: null, queued: null, help: 'h' });
    expect(model.branch).toBeUndefined();
    expect(model.queued).toBeUndefined();
    expect(model.label).toBe('원격 창 · studio-mac에서 실행 중 · 연결 끊김');
  });
});
