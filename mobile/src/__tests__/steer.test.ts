import { busySendChoices } from '@/lib/send';

// What the round buttons offer for a draft while the pane is running. The host says per pane
// whether a follow-up can join the running turn (`canSteer`); only Claude panes can.
describe('busySendChoices', () => {
  it('offers steer and queue when the host says the pane can steer', () => {
    expect(busySendChoices({ submitModes: true, canSteer: true, attachments: 0 })).toEqual([
      'steer',
      'queue',
    ]);
  });

  it('offers only queue when the host says the pane cannot steer (Codex, Antigravity)', () => {
    expect(busySendChoices({ submitModes: true, canSteer: false, attachments: 0 })).toEqual([
      'queue',
    ]);
  });

  it('keeps steering offered when an older host leaves the field out', () => {
    expect(busySendChoices({ submitModes: true, attachments: 0 })).toEqual(['steer', 'queue']);
  });

  it('never offers steer for a draft carrying files', () => {
    expect(busySendChoices({ submitModes: true, canSteer: true, attachments: 2 })).toEqual(['queue']);
    expect(busySendChoices({ submitModes: true, attachments: 1 })).toEqual(['queue']);
  });

  it('leaves the choice to a host without submit modes', () => {
    expect(busySendChoices({ submitModes: false, canSteer: false, attachments: 0 })).toEqual([
      'send',
    ]);
    expect(busySendChoices({ submitModes: false, attachments: 0 })).toEqual(['send']);
  });
});
