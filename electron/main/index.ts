import { app, BrowserWindow, dialog, ipcMain, safeStorage, type IpcMainEvent, type IpcMainInvokeEvent } from 'electron'
import { randomUUID } from 'node:crypto'
import { realpath } from 'node:fs/promises'
import { basename, dirname, join } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'
import { closeModelCatalogLookups } from './claude-models'
import { closeProviderRuntimeLookups, getProviderRuntimeInfo } from './provider-runtime'
import { RemoteController } from './remote/controller'
import { RunManager } from './run-manager'
import { RunRouter } from './run-router'
import { StateStore } from './state-store'
import { isIdentifier, isRecord } from './validation'
import type { RunEvent } from '../../shared/types'

const moduleDirectory = dirname(fileURLToPath(import.meta.url))
let mainWindow: BrowserWindow | null = null
let runs: RunManager | null = null
let store: StateStore | null = null
let remote: RemoteController | null = null
let quitting = false
let allowQuit = false
let flushRenderer: () => Promise<void> = async () => undefined

function createWindow(): void {
  const rendererFile = join(moduleDirectory, '../renderer/index.html')
  const rendererUrl = !app.isPackaged && process.env.ELECTRON_RENDERER_URL ? process.env.ELECTRON_RENDERER_URL : pathToFileURL(rendererFile).href
  const expectedUrl = new URL(rendererUrl)
  const isAllowedUrl = (value: string): boolean => {
    try {
      const candidate = new URL(value)
      return candidate.protocol === expectedUrl.protocol && candidate.host === expectedUrl.host && candidate.pathname === expectedUrl.pathname
    } catch { return false }
  }
  const window = new BrowserWindow({
    width: 1360, height: 900, minWidth: 900, minHeight: 600, show: false,
    title: 'MightyClaude', backgroundColor: '#17191c',
    ...(process.platform === 'darwin' ? { titleBarStyle: 'hiddenInset' as const, trafficLightPosition: { x: 18, y: 20 } } : { frame: false }),
    webPreferences: {
      preload: join(moduleDirectory, '../preload/index.cjs'),
      contextIsolation: true, nodeIntegration: false, sandbox: true, webSecurity: true,
    },
  })
  mainWindow = window
  store = new StateStore(app.getPath('userData'))
  const pluginDirectory = app.isPackaged ? join(process.resourcesPath, 'mods', 'mighty-bridge') : join(app.getAppPath(), 'mods', 'mighty-bridge')
  let router: RunRouter
  const emit = (event: RunEvent): void => router.receive(event)
  runs = new RunManager({
    pluginDirectory,
    resolveWorkspace: (id) => store!.resolveWorkspace(id),
    emit,
  })
  const resolveSharedWorkspace = async (id: string) => {
    const snapshot = await store!.load()
    if (!snapshot.workspaces.some((workspace) => workspace.id === id && !workspace.remote)) throw new Error('이 컴퓨터에 등록된 로컬 워크스페이스가 아닙니다.')
    return store!.resolveWorkspace(id)
  }
  const canEncrypt = safeStorage.isEncryptionAvailable() && (process.platform !== 'linux' || safeStorage.getSelectedStorageBackend() !== 'basic_text')
  remote = new RemoteController({
    directory: join(app.getPath('userData'), 'remote'),
    appVersion: app.getVersion(),
    resolveWorkspace: resolveSharedWorkspace,
    listWorkspaces: async () => (await store!.load()).workspaces.filter((workspace) => !workspace.remote),
    getRuntimeInfo: () => getProviderRuntimeInfo(app.getVersion()),
    createRunManager: (send: (event: RunEvent) => void) => new RunManager({ pluginDirectory, resolveWorkspace: resolveSharedWorkspace, emit: send }),
    emit,
    ...(canEncrypt ? { encrypt: (value: string) => safeStorage.encryptString(value), decrypt: (value: Buffer) => safeStorage.decryptString(value) } : {}),
  })
  router = new RunRouter({
    resolveWorkspace: (id) => store!.getWorkspace(id),
    startLocal: (request) => runs!.start(request),
    startRemote: (request, workspace) => remote!.startRun(request, workspace),
    stopLocal: (id) => runs!.stop(id),
    stopRemote: (id) => remote!.stopRun(id),
    emit: (event) => { if (!window.isDestroyed() && !window.webContents.isDestroyed()) window.webContents.send('mighty:run-event', event) },
  })

  function assertSender(event: IpcMainInvokeEvent | IpcMainEvent): void {
    if (window.isDestroyed() || event.sender !== window.webContents || event.senderFrame !== window.webContents.mainFrame || !isAllowedUrl(event.senderFrame.url)) {
      throw new Error('허용되지 않은 앱 요청입니다.')
    }
  }
  const handle = (channel: string, handler: (value: unknown) => unknown): void => {
    ipcMain.handle(channel, (event, value: unknown) => { assertSender(event); return handler(value) })
  }
  handle('mighty:load-state', () => store!.load())
  handle('mighty:save-state', (value) => store!.save(value))
  let picking: Promise<unknown> | null = null
  handle('mighty:pick-workspace', () => {
    if (picking) return picking
    picking = (async () => {
      const result = await dialog.showOpenDialog(window, { title: '워크스페이스 폴더 선택', properties: ['openDirectory', 'createDirectory'] })
      if (result.canceled || !result.filePaths[0]) return null
      const path = await realpath(result.filePaths[0])
      return store!.approveWorkspace({ id: randomUUID(), path, name: basename(path) || path, createdAt: new Date().toISOString() })
    })().finally(() => { picking = null })
    return picking
  })
  handle('mighty:runtime-info', () => getProviderRuntimeInfo(app.getVersion()))
  handle('mighty:start-run', async (value) => {
    if (quitting) throw new Error('앱이 종료 중입니다.')
    return router.start(value)
  })
  handle('mighty:stop-run', (value) => {
    return router.stop(value)
  })
  handle('mighty:remote-state', () => remote!.getState())
  handle('mighty:start-sharing', (value) => {
    if (quitting) throw new Error('앱이 종료 중입니다.')
    return remote!.startSharing(value as Parameters<RemoteController['startSharing']>[0])
  })
  handle('mighty:stop-sharing', () => remote!.stopSharing())
  handle('mighty:connect-remote', (value) => {
    if (quitting) throw new Error('앱이 종료 중입니다.')
    return remote!.connectRemote(value as Parameters<RemoteController['connectRemote']>[0])
  })
  handle('mighty:refresh-remote', (value) => {
    if (quitting) throw new Error('앱이 종료 중입니다.')
    if (!isIdentifier(value)) throw new Error('연결 ID가 올바르지 않습니다.')
    return remote!.refreshRemote(value)
  })
  handle('mighty:disconnect-remote', (value) => {
    if (!isIdentifier(value)) throw new Error('연결 ID가 올바르지 않습니다.')
    return remote!.disconnectRemote(value)
  })
  handle('mighty:import-remote-workspace', async (value) => {
    if (quitting) throw new Error('앱이 종료 중입니다.')
    if (!isRecord(value) || !isIdentifier(value.connectionId) || !isIdentifier(value.workspaceId)) throw new Error('원격 워크스페이스 요청이 올바르지 않습니다.')
    const peer = await remote!.getRemoteWorkspace(value.connectionId, value.workspaceId)
    const state = await remote!.getState()
    const connection = state.connections.find((entry) => entry.id === value.connectionId)
    return store!.approveRemoteWorkspace(value.connectionId, peer, connection?.name ?? 'Remote')
  })
  flushRenderer = () => new Promise<void>((resolve) => {
    if (window.isDestroyed() || window.webContents.isDestroyed()) return resolve()
    const requestId = randomUUID()
    const finish = (): void => { clearTimeout(timeout); ipcMain.removeListener('mighty:flush-ack', acknowledge); resolve() }
    const acknowledge = (event: IpcMainEvent, value: unknown): void => {
      try { assertSender(event) } catch { return }
      if (isRecord(value) && value.requestId === requestId) finish()
    }
    // A renderer that crashed must not prevent process cleanup or quitting.
    const timeout = setTimeout(finish, 2500)
    ipcMain.on('mighty:flush-ack', acknowledge)
    window.webContents.send('mighty:flush-request', requestId)
  })
  ipcMain.on('mighty:window-action', (event, action: unknown) => {
    try { assertSender(event) } catch { return }
    if (action === 'minimize') window.minimize()
    if (action === 'maximize') { if (window.isMaximized()) window.unmaximize(); else window.maximize() }
    if (action === 'close') window.close()
  })

  window.webContents.setWindowOpenHandler(() => ({ action: 'deny' }))
  window.webContents.on('will-navigate', (event, url) => { if (!isAllowedUrl(url)) event.preventDefault() })
  window.webContents.on('will-redirect', (event, url) => { if (!isAllowedUrl(url)) event.preventDefault() })
  window.webContents.session.setPermissionRequestHandler((contents, permission, callback, details) => {
    callback(permission === 'clipboard-sanitized-write' && contents === window.webContents && details.isMainFrame && isAllowedUrl(details.requestingUrl))
  })
  window.webContents.session.setPermissionCheckHandler((contents, permission, _origin, details) => {
    return permission === 'clipboard-sanitized-write' && contents === window.webContents && details.isMainFrame && isAllowedUrl(details.requestingUrl ?? '')
  })
  window.on('ready-to-show', () => window.show())
  window.on('close', (event) => {
    if (!allowQuit) { event.preventDefault(); app.quit() }
  })
  window.on('closed', () => { mainWindow = null })
  void window.loadURL(rendererUrl)
}

app.on('before-quit', (event) => {
  if (allowQuit) return
  event.preventDefault()
  if (quitting) return
  quitting = true
  void (async () => {
    await Promise.allSettled([runs?.dispose(), remote?.dispose(), closeModelCatalogLookups(), closeProviderRuntimeLookups()])
    await flushRenderer()
    await store?.flush().catch(() => undefined)
    allowQuit = true
    app.quit()
  })()
})
app.on('window-all-closed', () => app.quit())
app.on('activate', () => { if (mainWindow) mainWindow.show() })
app.whenReady().then(createWindow).catch((error: unknown) => {
  dialog.showErrorBox('MightyClaude 시작 오류', error instanceof Error ? error.message : String(error))
  allowQuit = true
  app.quit()
})
