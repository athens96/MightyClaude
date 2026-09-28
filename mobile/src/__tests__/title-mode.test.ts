/**
 * Tests for the titleMode field in MobileSessionSummary and the
 * setAutoTitle / rename client methods that drive the phone rename sheet.
 */

import { createClient } from '@/api/client';
import type { MobileClient, RelayChannel } from '@/api/client';
import type { MobileSessionSummary, MobileState } from '@/api/types';
import type { RelayNotification } from '@/api/relay/transport';

interface Recorded {
  method: 'GET' | 'POST';
  path: string;
  body?: unknown;
}

function fakeChannel(response: { status: number; body: unknown }) {
  const calls: Recorded[] = [];
  const listeners = new Set<(event: RelayNotification) => void>();
  const channel: RelayChannel = {
    request: (method, path, body) => {
      calls.push({ method, path, body });
      return Promise.resolve(response);
    },
    onNotify: (listener) => {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
  };
  return { channel, calls };
}

const ok = { status: 200, body: { protocol: 1, ok: true } };

describe('title-mode: MobileSessionSummary titleMode field', () => {
  it('accepts titleMode "auto" in a session summary', () => {
    const summary: MobileSessionSummary = {
      id: 's1',
      workspaceId: 'w1',
      title: 'Claude',
      kind: 'claude',
      provider: 'claude',
      model: 'claude-sonnet-5',
      status: 'idle',
      revision: 1,
      updatedAt: '2026-09-28T00:00:00Z',
      pendingPermissions: 0,
      pendingQuestions: 0,
      queued: 0,
      terminal: false,
      titleMode: 'auto',
    };
    expect(summary.titleMode).toBe('auto');
  });

  it('accepts titleMode "fixed" in a session summary', () => {
    const summary: MobileSessionSummary = {
      id: 's1',
      workspaceId: 'w1',
      title: 'My Custom Name',
      kind: 'claude',
      provider: 'claude',
      model: 'claude-sonnet-5',
      status: 'idle',
      revision: 1,
      updatedAt: '2026-09-28T00:00:00Z',
      pendingPermissions: 0,
      pendingQuestions: 0,
      queued: 0,
      terminal: false,
      titleMode: 'fixed',
    };
    expect(summary.titleMode).toBe('fixed');
  });

  it('accepts a summary without titleMode (older host)', () => {
    const summary: MobileSessionSummary = {
      id: 's1',
      workspaceId: 'w1',
      title: 'Claude',
      kind: 'claude',
      provider: 'claude',
      model: 'claude-sonnet-5',
      status: 'idle',
      revision: 1,
      updatedAt: '2026-09-28T00:00:00Z',
      pendingPermissions: 0,
      pendingQuestions: 0,
      queued: 0,
      terminal: false,
    };
    expect(summary.titleMode).toBeUndefined();
  });

  it('state payload with titleMode is typed correctly', () => {
    const state: MobileState = {
      protocol: 1,
      revision: 3,
      hostName: 'Mac',
      workspaces: [],
      sessions: [
        {
          id: 's1',
          workspaceId: 'w1',
          title: 'Fix the bug',
          kind: 'claude',
          provider: 'claude',
          model: 'claude-sonnet-5',
          status: 'idle',
          revision: 2,
          updatedAt: '2026-09-28T00:00:00Z',
          pendingPermissions: 0,
          pendingQuestions: 0,
          queued: 0,
          terminal: false,
          titleMode: 'auto',
        },
        {
          id: 's2',
          workspaceId: 'w1',
          title: 'My Project',
          kind: 'claude',
          provider: 'claude',
          model: 'claude-sonnet-5',
          status: 'idle',
          revision: 1,
          updatedAt: '2026-09-28T00:00:00Z',
          pendingPermissions: 0,
          pendingQuestions: 0,
          queued: 0,
          terminal: false,
          titleMode: 'fixed',
        },
      ],
    };
    expect(state.sessions[0]?.titleMode).toBe('auto');
    expect(state.sessions[1]?.titleMode).toBe('fixed');
  });
});

describe('title-mode: client.setAutoTitle sends titleMode "auto" to rename route', () => {
  it('posts to /rename with titleMode "auto" and empty title', async () => {
    const fake = fakeChannel(ok);
    const client = createClient(fake.channel);
    await client.setAutoTitle('s1');
    expect(fake.calls).toHaveLength(1);
    expect(fake.calls[0]).toEqual({
      method: 'POST',
      path: '/m1/sessions/s1/rename',
      body: { title: '', titleMode: 'auto' },
    });
  });

  it('percent-encodes session id in setAutoTitle', async () => {
    const fake = fakeChannel(ok);
    const client = createClient(fake.channel);
    await client.setAutoTitle('a/b c');
    expect(fake.calls[0]?.path).toBe('/m1/sessions/a%2Fb%20c/rename');
  });
});

describe('title-mode: client.rename sends only title (fixed mode)', () => {
  it('posts to /rename with trimmed title and no titleMode', async () => {
    const fake = fakeChannel(ok);
    const client = createClient(fake.channel);
    await client.rename('s1', '  My Name  ');
    expect(fake.calls[0]).toEqual({
      method: 'POST',
      path: '/m1/sessions/s1/rename',
      body: { title: 'My Name' },
    });
  });

  it('rename still rejects an empty or too-long title', async () => {
    const fake = fakeChannel(ok);
    const client = createClient(fake.channel);
    await expect(client.rename('s1', '   ')).rejects.toMatchObject({ status: 400 });
    await expect(client.rename('s1', 'x'.repeat(81))).rejects.toMatchObject({ status: 400 });
    expect(fake.calls).toHaveLength(0);
  });

  it('setAutoTitle does not perform local title validation', async () => {
    const fake = fakeChannel(ok);
    const client = createClient(fake.channel);
    // Should not throw even though '' would fail rename validation
    await expect(client.setAutoTitle('s1')).resolves.not.toThrow();
    expect(fake.calls).toHaveLength(1);
  });
});
