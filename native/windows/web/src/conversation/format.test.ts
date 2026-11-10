import { describe, expect, it } from 'vitest';
import { providerLabel } from './format';

describe('providerLabel', () => {
  it('names each agent, pi included', () => {
    expect(['claude', 'codex', 'gemini', 'pi'].map(providerLabel)).toEqual(['Claude', 'Codex', 'Antigravity', 'pi']);
  });
});
