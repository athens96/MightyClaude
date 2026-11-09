import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { ServicesContext, type AppServices } from '../app/context';
import { translator } from '../i18n/locale';
import type { AppSnapshot, LogEntry, ShellBlockRun } from '../store/models';
import { Entry } from './Entry';
import { parseMarkdown } from './markdown';
import { runnableBlocks, shellBlockBusy, shellBlockKey, shellCommand } from './shellBlocks';

describe('shellCommand', () => {
  it('takes the shells by the language\'s first word, in any case, and the PowerShell ones only on Windows', () => {
    for (const language of ['sh', 'bash', 'ZSH', 'shell', 'Bash title="x"', 'ps1', 'powershell', 'PwSh']) expect(shellCommand(language, 'ls')).toBe('ls');
    for (const language of ['ps1', 'powershell', 'pwsh']) expect(shellCommand(language, 'ls', 'mac')).toBeNull();
    expect(shellCommand('bash', 'ls', 'mac')).toBe('ls');
    for (const language of [null, '', 'ts', 'json', 'shellscript', 'cmd', 'text']) expect(shellCommand(language, 'ls')).toBeNull();
  });

  it('runs the block as shown, the trailing newline trimmed, and nothing for a blank one', () => {
    expect(shellCommand('sh', 'npm ci\nnpm test\n\n')).toBe('npm ci\nnpm test');
    expect(shellCommand('sh', '  indented\r\nnext')).toBe('  indented\nnext');
    expect(shellCommand('sh', '  \n ')).toBeNull();
  });

  it('runs a console block\'s prompt lines without their prompt and drops its sample output', () => {
    expect(shellCommand('console', '$ npm ci\nadded 3 packages\n$ npm test\nok')).toBe('npm ci\nnpm test');
    expect(shellCommand('console', 'PS> Get-ChildItem\n> dir\n% ls')).toBe('Get-ChildItem\ndir');
    expect(shellCommand('console', '% ls\n$ pwd', 'mac')).toBe('ls\npwd');
    expect(shellCommand('console', 'PS> dir', 'mac')).toBeNull();
  });

  it('gives a console block with no command line no button', () => {
    expect(shellCommand('console', 'just output\nmore output')).toBeNull();
    expect(shellCommand('console', '$ \n$   ')).toBeNull();
    expect(shellCommand('console', ' $ indented is output')).toBeNull();
  });
});

describe('runnableBlocks', () => {
  const reply = 'Run:\n\n```sh\nnpm ci\n```\n\n```ts\nconst a = 1;\n```\n\n> ```console\n> $ npm test\n> ```\n\n- step\n\n  ```bash\n  ls\n  ```\n';

  it('numbers the runnable blocks of an entry in reading order, into quotes and list items', () => {
    const found = [...runnableBlocks(parseMarkdown(reply)).values()];
    expect(found).toEqual([{ index: 0, command: 'npm ci' }, { index: 1, command: 'npm test' }, { index: 2, command: 'ls' }]);
    expect(shellBlockKey('entry-7', 2)).toBe('entry-7#2');
  });

  it('keeps the keys while the reply streams, and a block gets its key once its fence closes', () => {
    const streaming = parseMarkdown('```sh\nnpm ci\n```\n\n```sh\nnpm te');
    expect([...runnableBlocks(streaming).values()]).toEqual([{ index: 0, command: 'npm ci' }]);
    const closed = parseMarkdown('```sh\nnpm ci\n```\n\n```sh\nnpm test\n```\nDone.');
    expect([...runnableBlocks(closed).values()]).toEqual([{ index: 0, command: 'npm ci' }, { index: 1, command: 'npm test' }]);
    expect([...runnableBlocks(parseMarkdown(reply)).values()].map((block) => block.index)).toEqual([...runnableBlocks(parseMarkdown(reply)).values()].map((block) => block.index));
  });

  it('is busy only for another running block', () => {
    const runs: ShellBlockRun[] = [{ key: 'e#0', command: 'ls', state: 'running', output: '', startedAt: 't' }, { key: 'e#1', command: 'ls', state: 'failed', exitCode: 1, output: '', startedAt: 't' }];
    expect(shellBlockBusy(runs, 'e#0')).toBe(false);
    expect(shellBlockBusy(runs, 'e#1')).toBe(true);
    expect(shellBlockBusy(undefined, 'e#1')).toBe(false);
  });
});

describe('a reply\'s shell block', () => {
  const words = {
    'run.shellBlock.run': 'Run', 'run.shellBlock.help': 'Runs this', 'run.shellBlock.running': 'Running', 'run.shellBlock.stop': 'Stop',
    'run.shellBlock.again': 'Run again', 'run.shellBlock.done': 'Done', 'run.shellBlock.failed': 'Exit code {code}', 'run.shellBlock.stopped': 'Stopped',
    'run.shellBlock.interrupted': 'Interrupted', 'run.shellBlock.busy': 'Busy',
  };
  function render(entry: LogEntry, runs: ShellBlockRun[] | undefined, remote = false): string {
    const snapshot = { workspaces: [{ id: 'w', name: 'w', path: 'C:\\w', createdAt: '', ...(remote ? { remote: { version: 2, connectionId: 'c', workspaceId: 'h', hostName: 'h' } } : {}) }], sessions: [{ id: 'p', workspaceId: 'w', shellRuns: runs }] } as unknown as AppSnapshot;
    const services = { bridge: { call: () => Promise.resolve(null) }, store: { subscribe: () => () => {}, getState: () => ({ snapshot }) }, t: translator([words]) } as unknown as AppServices;
    const element = createElement(Entry, { entry, paneId: 'p', provider: 'claude', shell: false, live: false, expanded: false, onToggle: () => {} });
    return renderToStaticMarkup(createElement(ServicesContext.Provider, { value: services }, element));
  }
  const entry: LogEntry = { id: 'r1', kind: 'assistant', text: '```sh\nnpm test\n```\n\n```ts\nx\n```', timestamp: '' };

  it('shows Run on a reply\'s shell block only', () => {
    const html = render(entry, undefined);
    expect(html).toContain('data-shell-block="r1#0"');
    expect(html).toContain('data-shell-state="idle"');
    expect(html.match(/data-shell-block=/g)?.length).toBe(1);
    expect(html).toContain('>Run<');
    expect(render({ ...entry, kind: 'user' }, undefined)).not.toContain('data-shell-block');
    expect(render(entry, undefined, true)).not.toContain('data-shell-state');
  });

  it('shows a run\'s state and its output under the code', () => {
    const running = render(entry, [{ key: 'r1#0', command: 'npm test', state: 'running', output: 'PASS 3\n', startedAt: 't' }]);
    expect(running).toContain('tool-square run');
    expect(running).toContain('>Stop<');
    expect(running).toContain('data-shell-output="r1#0"');
    expect(running).toContain('PASS 3');
    const failed = render(entry, [{ key: 'r1#0', command: 'npm test', state: 'failed', exitCode: 2, output: '', startedAt: 't' }]);
    expect(failed).toContain('Exit code 2');
    expect(failed).toContain('>Run again<');
    expect(failed).not.toContain('data-shell-output');
    expect(render(entry, [{ key: 'r1#0', command: 'npm test', state: 'interrupted', output: '', startedAt: 't' }])).toContain('Interrupted');
    expect(render(entry, [{ key: 'other#0', command: 'ls', state: 'running', output: '', startedAt: 't' }])).toContain('title="Busy"');
  });
});
