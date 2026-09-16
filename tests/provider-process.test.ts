import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises'
import { join } from 'node:path'
import { tmpdir } from 'node:os'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { fallbackProviderCatalog } from '../shared/provider-options'
import type { RunEvent, StartRunRequest } from '../shared/types'
import { RunManager } from '../electron/main/run-manager'
import { readCodexModelCatalog, type ProviderCommand } from '../electron/main/provider-runtime'

const temporary: string[] = []
const managers: RunManager[] = []
afterEach(async () => {
  await Promise.all(managers.splice(0).map((manager) => manager.dispose()))
  await Promise.all(temporary.splice(0).map((directory) => rm(directory, { recursive: true, force: true })))
})
async function fixture(source: string): Promise<{ directory: string; file: string }> {
  const directory = await mkdtemp(join(tmpdir(), 'mighty-provider-test-')); temporary.push(directory)
  const file = join(directory, 'fake-cli.cjs'); await writeFile(file, source)
  return { directory, file }
}
const base: StartRunRequest = { sessionId: 'run', workspaceId: 'workspace', kind: 'claude', model: 'default', provider: 'codex', input: '--dangerously-skip-permissions $(echo unsafe)\n한국어' }

describe('provider native subprocess integration', () => {
  it('runs independent Codex and Gemini processes without Claude or Mods and preserves stdin, resume, attribution and terminal status', async () => {
    const { directory, file } = await fixture(`let input='';process.stdin.setEncoding('utf8');process.stdin.on('data',c=>input+=c);process.stdin.on('end',()=>{const gemini=process.argv.includes('--output-format'); const send=v=>process.stdout.write(JSON.stringify(v)+'\\n'); send(gemini?{type:'init',session_id:'gemini-resume'}:{type:'thread.started',thread_id:'codex-resume'}); const text=JSON.stringify({input,args:process.argv.slice(2)});send(gemini?{type:'message',role:'assistant',content:text,delta:true}:{type:'item.completed',item:{id:'message',type:'agent_message',text}});send(gemini?{type:'result',status:'success'}:{type:'turn.completed'});});`)
    const events: RunEvent[] = []
    const discoverClaude = vi.fn(async () => { throw new Error('Claude discovery must not run') })
    const manager = new RunManager({ pluginDirectory: '/does-not-exist', resolveWorkspace: async () => ({ id: 'workspace', name: 'Fixture', path: directory, createdAt: new Date().toISOString() }), emit: (event) => events.push(event), discoverRuntime: discoverClaude, discoverProvider: async (provider) => ({ provider, binary: process.execPath, argsPrefix: [file], modelCatalog: fallbackProviderCatalog(provider) }) }); managers.push(manager)
    await Promise.all([manager.start({ ...base, sessionId: 'codex-run' }), manager.start({ ...base, provider: 'gemini', sessionId: 'gemini-run' })])
    await vi.waitFor(() => expect(events.filter((event) => event.type === 'status' && event.status !== 'running')).toHaveLength(2), { timeout: 10_000 })
    expect(events.filter((event) => event.type === 'status' && event.status === 'completed'), JSON.stringify(events)).toHaveLength(2)
    expect(discoverClaude).not.toHaveBeenCalled()
    for (const provider of ['codex', 'gemini'] as const) {
      expect(events).toContainEqual({ sessionId: `${provider}-run`, type: 'resume', resumeId: `${provider}-resume` })
      const answer = events.find((event) => event.type === 'log' && event.sessionId === `${provider}-run` && event.entry.kind === 'assistant')
      expect(answer?.type === 'log' && answer.entry.provider).toBe(provider)
      const result = JSON.parse(answer?.type === 'log' ? answer.entry.text : '{}')
      expect(result.input).toBe(base.input)
      expect(result.args).not.toContain(base.input)
    }
  }, 20_000)

  it('marks a JSON protocol failure as error even with exit 0 and can stop a waiting Gemini process', async () => {
    const { directory, file } = await fixture(`if(process.argv.includes('--output-format')){setInterval(()=>{},1000)}else{process.stdin.resume();process.stdin.on('end',()=>{console.log(JSON.stringify({type:'turn.failed',error:{message:'Login required'}}));});}`)
    const events: RunEvent[] = []
    const manager = new RunManager({ pluginDirectory: '', resolveWorkspace: async () => ({ id: 'workspace', name: 'Fixture', path: directory, createdAt: '' }), emit: (event) => events.push(event), discoverProvider: async (provider) => ({ provider, binary: process.execPath, argsPrefix: [file], modelCatalog: fallbackProviderCatalog(provider) }) }); managers.push(manager)
    await manager.start({ ...base, sessionId: 'error-run' })
    await vi.waitFor(() => expect(events).toContainEqual({ sessionId: 'error-run', type: 'status', status: 'error' }))
    await manager.start({ ...base, provider: 'gemini', sessionId: 'stop-run' })
    await manager.stop('stop-run')
    expect(events).toContainEqual({ sessionId: 'stop-run', type: 'status', status: 'stopped' })
    expect(events.some((event) => event.type === 'log' && event.entry.text === 'Login required'), JSON.stringify(events)).toBe(true)
  }, 15_000)

  it('does not launch a local child for a remote workspace', async () => {
    const discoverProvider = vi.fn()
    const manager = new RunManager({ pluginDirectory: '', resolveWorkspace: async () => ({ id: 'workspace', name: 'Remote', path: 'C:\\remote', createdAt: '', remote: { connectionId: 'peer', workspaceId: 'shared', hostName: 'Host' } }), emit: () => undefined, discoverProvider }); managers.push(manager)
    await expect(manager.start(base)).rejects.toThrow('호스트')
    expect(discoverProvider).not.toHaveBeenCalled()
  })
})

describe('Codex metadata protocol', () => {
  it('only initializes and lists models, handles pagination, then closes the metadata child', async () => {
    const { directory, file } = await fixture(`const fs=require('node:fs'),rl=require('node:readline').createInterface({input:process.stdin});fs.writeFileSync(__dirname+'/pid',String(process.pid));rl.on('line',line=>{const request=JSON.parse(line);fs.appendFileSync(__dirname+'/requests',line+'\\n');if(request.method==='initialize')console.log(JSON.stringify({id:request.id,result:{}}));if(request.method==='model/list')console.log(JSON.stringify({id:request.id,result:{data:[{model:request.params.cursor?'company/second':'company/first',displayName:'Provider model',supportedReasoningEfforts:[{reasoningEffort:'low'}]}],nextCursor:request.params.cursor?null:'page-two'}}));});`)
    const command: ProviderCommand = { provider: 'codex', binary: process.execPath, argsPrefix: [file] }
    const catalog = await readCodexModelCatalog(command)
    expect(catalog.source).toBe('cli')
    expect(catalog.models.map((model) => model.value)).toEqual(['default', 'company/first', 'company/second'])
    const requests = (await readFile(join(directory, 'requests'), 'utf8')).trim().split('\n').map((line) => JSON.parse(line))
    expect(requests.map((request) => request.method)).toEqual(['initialize', 'initialized', 'model/list', 'model/list'])
    expect(requests.some((request) => /thread|turn|login|auth/.test(request.method))).toBe(false)
    const pid = Number(await readFile(join(directory, 'pid'), 'utf8'))
    await vi.waitFor(() => expect(() => process.kill(pid, 0)).toThrow(), { timeout: 3000 })
  }, 12_000)

  it('times out safely and rejects oversized metadata without running a user turn', async () => {
    for (const source of ['setInterval(()=>{},1000)', "process.stdout.write('x'.repeat(1024*1024+1));setInterval(()=>{},1000)"]) {
      const { file } = await fixture(source)
      const catalog = await readCodexModelCatalog({ provider: 'codex', binary: process.execPath, argsPrefix: [file] }, 300)
      expect(catalog.source).toBe('fallback')
    }
  }, 6000)

  it('cancels active metadata and refuses new lookups after shutdown', async () => {
    vi.resetModules()
    const runtime = await import('../electron/main/provider-runtime')
    const { file } = await fixture('setInterval(()=>{},1000)')
    const command: ProviderCommand = { provider: 'codex', binary: process.execPath, argsPrefix: [file] }
    const pending = runtime.readCodexModelCatalog(command, 30_000)
    await runtime.closeProviderRuntimeLookups()
    expect((await pending).source).toBe('fallback')
    expect((await runtime.readCodexModelCatalog(command)).source).toBe('fallback')
    expect((await runtime.getProviderRuntimeInfo('test')).providers?.every((provider) => !provider.available)).toBe(true)
  })
})
