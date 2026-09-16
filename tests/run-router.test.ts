import { describe, expect, it, vi } from 'vitest'
import { RunRouter } from '../electron/main/run-router'
import type { RunEvent, StartRunRequest, Workspace } from '../shared/types'

const local: Workspace = { id: 'local-workspace', name: 'Local', path: '/tmp/local', createdAt: '2026-09-16T00:00:00Z' }
const remote: Workspace = { ...local, id: 'remote-workspace', path: 'C:\\Projects\\remote', remote: { connectionId: 'connection-1', workspaceId: 'peer-1', hostName: 'Peer' } }
const request = (workspace: Workspace): StartRunRequest => ({ sessionId: 'shared-pane', workspaceId: workspace.id, kind: 'shell', model: 'default', input: 'echo ROUTER_FIXTURE' })

function fixture(resolveWorkspace = async (id: string) => id === local.id ? local : remote) {
  const options = { resolveWorkspace, startLocal: vi.fn(async () => undefined), startRemote: vi.fn(async () => undefined), stopLocal: vi.fn(async () => undefined), stopRemote: vi.fn(async () => undefined), emit: vi.fn<(event: RunEvent) => void>() }
  return { router: new RunRouter(options), options }
}

describe('native run routing', () => {
  it.each([['local first', local, remote], ['remote first', remote, local]] as const)('%s prevents one pane from starting on both computers', async (_name, first, second) => {
    const { router, options } = fixture()
    await router.start(request(first))
    await expect(router.start(request(second))).rejects.toThrow('이미 실행 중')
    await router.stop('shared-pane')
    expect(options.startLocal).toHaveBeenCalledTimes(first.remote ? 0 : 1)
    expect(options.startRemote).toHaveBeenCalledTimes(first.remote ? 1 : 0)
    expect(options.stopLocal).toHaveBeenCalledTimes(first.remote ? 0 : 1)
    expect(options.stopRemote).toHaveBeenCalledTimes(first.remote ? 1 : 0)
    router.receive({ type: 'status', sessionId: 'shared-pane', status: 'stopped' })
    await router.start(request(second))
    expect(options.startLocal).toHaveBeenCalledTimes(1)
    expect(options.startRemote).toHaveBeenCalledTimes(1)
  })

  it('reserves before workspace lookup and cancels only the pending run', async () => {
    let release!: (workspace: Workspace) => void
    const pending = new Promise<Workspace>((resolve) => { release = resolve })
    const resolver = vi.fn().mockReturnValueOnce(pending).mockResolvedValue(remote)
    const { router, options } = fixture(resolver)
    const first = router.start(request(local))
    await expect(router.start(request(remote))).rejects.toThrow('이미 실행 중')
    await router.stop('shared-pane')
    expect(options.emit).toHaveBeenCalledWith({ type: 'status', sessionId: 'shared-pane', status: 'stopped' })
    await router.start(request(remote))
    release(local)
    await first
    expect(options.startLocal).not.toHaveBeenCalled()
    expect(options.startRemote).toHaveBeenCalledOnce()
    await expect(router.start(request(local))).rejects.toThrow('이미 실행 중')
  })

  it('releases a rejected start without falling back to the local computer', async () => {
    const { router, options } = fixture()
    options.startRemote.mockRejectedValueOnce(new Error('Peer offline'))
    await expect(router.start(request(remote))).rejects.toThrow('Peer offline')
    expect(options.startLocal).not.toHaveBeenCalled()
    await router.start(request(remote))
    expect(options.startRemote).toHaveBeenCalledTimes(2)
  })
})
