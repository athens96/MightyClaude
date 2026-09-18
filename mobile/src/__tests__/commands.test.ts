import type { MobileCommand } from '@/api/types';
import {
  commandActionOf,
  commandInsertion,
  commandQuery,
  filterCommands,
  isMessageAction,
} from '@/lib/commands';

function command(overrides: Partial<MobileCommand> & { name: string }): MobileCommand {
  return { description: '', source: 'builtin', ...overrides };
}

const commands: MobileCommand[] = [
  command({ name: 'model', description: '모델 바꾸기', action: 'model' }),
  command({ name: 'help', description: '도움말 보기', action: 'help' }),
  command({ name: 'review', description: '변경 사항 살펴보기', source: 'project' }),
  command({ name: 'commit', description: 'model 이 커밋 메시지를 만듭니다', source: 'user' }),
];

describe('commandQuery', () => {
  it('is the text after "/" while no argument has been typed', () => {
    expect(commandQuery('/')).toBe('');
    expect(commandQuery('/mod')).toBe('mod');
    expect(commandQuery('/MOD')).toBe('mod');
  });

  it('is undefined once the message is not a bare command', () => {
    expect(commandQuery('안녕하세요')).toBeUndefined();
    expect(commandQuery(' /model')).toBeUndefined();
    expect(commandQuery('/model src')).toBeUndefined();
    expect(commandQuery('/model\n')).toBeUndefined();
    expect(commandQuery('')).toBeUndefined();
  });
});

describe('filterCommands', () => {
  it('lists everything for an empty query', () => {
    expect(filterCommands(commands, '').map((entry) => entry.name)).toEqual([
      'model',
      'help',
      'review',
      'commit',
    ]);
  });

  it('matches the name first, then the description', () => {
    expect(filterCommands(commands, 'model').map((entry) => entry.name)).toEqual([
      'model',
      'commit',
    ]);
  });

  it('ignores case and answers with nothing when no command matches', () => {
    expect(filterCommands(commands, 'HEL').map((entry) => entry.name)).toEqual(['help']);
    expect(filterCommands(commands, 'zzz')).toEqual([]);
  });
});

describe('commandActionOf / isMessageAction', () => {
  it('reads only the actions the contract lists', () => {
    expect(commandActionOf(command({ name: 'model', action: 'model' }))).toBe('model');
    expect(commandActionOf(command({ name: 'rename', action: 'rename' }))).toBe('rename');
    expect(commandActionOf(command({ name: 'review' }))).toBeUndefined();
    expect(commandActionOf(command({ name: 'x', action: 'explode' }))).toBeUndefined();
  });

  it('separates the three actions that go to POST /command', () => {
    expect(isMessageAction('clear')).toBe(true);
    expect(isMessageAction('usage')).toBe(true);
    expect(isMessageAction('help')).toBe(true);
    expect(isMessageAction('model')).toBe(false);
    expect(isMessageAction('rename')).toBe(false);
  });
});

describe('commandInsertion', () => {
  it('leaves the cursor after "/name " so arguments can follow', () => {
    expect(commandInsertion(command({ name: 'review' }))).toBe('/review ');
  });
});
