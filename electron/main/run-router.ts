import type { RunEvent, StartRunRequest, Workspace } from '../../shared/types'
import { isIdentifier, validateStartRequest } from './validation'

interface RunRouterOptions {
  resolveWorkspace(id: string): Promise<Workspace>
  startLocal(request: StartRunRequest): Promise<void>
  startRemote(request: StartRunRequest, workspace: Workspace): Promise<void>
  stopLocal(id: string): Promise<void>
  stopRemote(id: string): Promise<void>
  emit(event: RunEvent): void
}

interface Reservation { route?: 'local' | 'remote'; cancelled: boolean }

/** A pane has one owner, including while its workspace is being resolved. */
export class RunRouter {
  private readonly active = new Map<string, Reservation>()
  constructor(private readonly options: RunRouterOptions) {}

  receive(event: RunEvent): void {
    if (event.type === 'status' && ['completed', 'stopped', 'error'].includes(event.status)) this.active.delete(event.sessionId)
    this.options.emit(event)
  }

  async start(value: unknown): Promise<void> {
    const request = validateStartRequest(value)
    if (this.active.has(request.sessionId)) throw new Error('이 실행 창은 이미 실행 중입니다.')
    if (this.active.size >= 16) throw new Error('동시에 실행할 수 있는 창은 16개입니다.')
    const reservation: Reservation = { cancelled: false }
    this.active.set(request.sessionId, reservation)
    try {
      const workspace = await this.options.resolveWorkspace(request.workspaceId)
      if (reservation.cancelled) return
      reservation.route = workspace.remote ? 'remote' : 'local'
      if (reservation.route === 'remote') await this.options.startRemote(request, workspace)
      else await this.options.startLocal(request)
    } catch (error) {
      if (this.active.get(request.sessionId) === reservation) this.active.delete(request.sessionId)
      if (!reservation.cancelled) throw error
    }
  }

  async stop(id: unknown): Promise<void> {
    if (!isIdentifier(id)) throw new Error('실행 창 ID가 올바르지 않습니다.')
    const reservation = this.active.get(id)
    if (!reservation) return
    if (reservation.route === 'local') return this.options.stopLocal(id)
    if (reservation.route === 'remote') return this.options.stopRemote(id)
    // No manager owns the request yet. Mark this particular reservation so a
    // late resolution cannot start it or remove a later run in the same pane.
    reservation.cancelled = true
    this.receive({ type: 'status', sessionId: id, status: 'stopped' })
  }
}
