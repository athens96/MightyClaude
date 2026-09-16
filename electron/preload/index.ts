import { contextBridge, ipcRenderer } from 'electron'
import type { DesktopBridge, RunEvent } from '../../shared/types'

const bridge: DesktopBridge = {
  isNative: true,
  loadState: () => ipcRenderer.invoke('mighty:load-state'),
  saveState: (snapshot) => ipcRenderer.invoke('mighty:save-state', snapshot),
  pickWorkspace: () => ipcRenderer.invoke('mighty:pick-workspace'),
  getRuntimeInfo: () => ipcRenderer.invoke('mighty:runtime-info'),
  startRun: (request) => ipcRenderer.invoke('mighty:start-run', request),
  stopRun: (sessionId) => ipcRenderer.invoke('mighty:stop-run', sessionId),
  onRunEvent: (listener) => {
    if (typeof listener !== 'function') throw new TypeError('실행 이벤트 리스너가 필요합니다.')
    const handler = (_event: Electron.IpcRendererEvent, value: RunEvent): void => listener(value)
    ipcRenderer.on('mighty:run-event', handler)
    return () => { ipcRenderer.removeListener('mighty:run-event', handler) }
  },
  onBeforeQuit: (listener) => {
    if (typeof listener !== 'function') throw new TypeError('종료 저장 리스너가 필요합니다.')
    const handler = (_event: Electron.IpcRendererEvent, requestId: string): void => {
      void listener().then(
        () => ipcRenderer.send('mighty:flush-ack', { requestId, ok: true }),
        () => ipcRenderer.send('mighty:flush-ack', { requestId, ok: false }),
      )
    }
    ipcRenderer.on('mighty:flush-request', handler)
    return () => { ipcRenderer.removeListener('mighty:flush-request', handler) }
  },
  windowAction: (action) => ipcRenderer.send('mighty:window-action', action),
  getRemoteState: () => ipcRenderer.invoke('mighty:remote-state'),
  startSharing: (request) => ipcRenderer.invoke('mighty:start-sharing', request),
  stopSharing: () => ipcRenderer.invoke('mighty:stop-sharing'),
  connectRemote: (request) => ipcRenderer.invoke('mighty:connect-remote', request),
  refreshRemote: (id) => ipcRenderer.invoke('mighty:refresh-remote', id),
  disconnectRemote: (id) => ipcRenderer.invoke('mighty:disconnect-remote', id),
  importRemoteWorkspace: (request) => ipcRenderer.invoke('mighty:import-remote-workspace', request),
}

contextBridge.exposeInMainWorld('mightyClaude', Object.freeze(bridge))
