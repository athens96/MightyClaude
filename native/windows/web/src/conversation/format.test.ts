import { describe, expect, it } from 'vitest';
import { providerIsBeta, providerLabel } from './format';

const agents = ['claude', 'codex', 'gemini', 'pi', 'opencode', 'hermes'];

describe('providerLabel', () => {
  it('names each agent, pi, opencode and Hermes included', () => {
    expect(agents.map(providerLabel)).toEqual(['Claude', 'Codex', 'Antigravity', 'pi', 'opencode', 'Hermes']);
  });
});

describe('providerIsBeta', () => {
  it('puts the beta capsule on every agent but Claude', () => {
    expect(agents.filter(providerIsBeta)).toEqual(['codex', 'gemini', 'pi', 'opencode', 'hermes']);
    expect(providerIsBeta('rovo')).toBe(false);
  });
});
