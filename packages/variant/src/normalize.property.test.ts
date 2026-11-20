// Property-style checks over generated parameter sets: the identity must not depend on key
// order, must survive a round trip through its own canonical text, and a secret-shaped value
// must be refused wherever it sits. The generator is seeded, so a failure reproduces.
import { describe, expect, it } from 'vitest';

import { normalizeVariant } from './normalize.js';

function rng(seed: number): () => number {
  let state = seed >>> 0;
  return () => {
    state = (Math.imul(state, 1664525) + 1013904223) >>> 0;
    return state / 4294967296;
  };
}

const KEYS = ['browser', 'locale', 'viewport', 'retries', 'headless', 'x.y', 'b_c', 'ключ', 'a'];
const STRINGS = [
  '',
  'Chromium',
  ' WEBKIT ',
  'p@ss&<>"\'+`\\',
  '\ud83d\ude00',
  'é',
  'x'.repeat(256),
];
const NUMBERS = [0, -0, 1, -1, 1.5, 1e-7, 1e21, 9007199254740991, Number.MIN_VALUE];

function scalar(next: () => number): unknown {
  const pick = Math.floor(next() * 4);
  if (pick === 0) return STRINGS[Math.floor(next() * STRINGS.length)];
  if (pick === 1) return NUMBERS[Math.floor(next() * NUMBERS.length)];
  if (pick === 2) return next() < 0.5;
  return Array.from({ length: Math.floor(next() * 4) }, () => NUMBERS[Math.floor(next() * 4)]);
}

function generated(next: () => number): [string, unknown][] {
  const keys = [...KEYS].sort(() => next() - 0.5).slice(0, 1 + Math.floor(next() * 6));
  return keys.map((key) => [key, scalar(next)]);
}

describe('variant normaliser properties', () => {
  it('is independent of key order and idempotent over its canonical text', () => {
    const next = rng(487);
    let accepted = 0;
    for (let i = 0; i < 2000; i += 1) {
      const entries = generated(next);
      const forward = normalizeVariant(Object.fromEntries(entries));
      const reversed = normalizeVariant(Object.fromEntries([...entries].reverse()));
      // A refusal names a rejected key by its position, so only the reasons are order-free.
      if (forward.ok) {
        expect(reversed).toEqual(forward);
      } else {
        const reasons = (result: typeof forward): string[] =>
          result.ok ? [] : result.problems.map((problem) => problem.reason).sort();
        expect(reasons(reversed)).toEqual(reasons(forward));
      }
      if (forward.ok) {
        accepted += 1;
        const again = normalizeVariant(JSON.parse(forward.paramsJson));
        expect(again.ok && again.hash).toBe(forward.hash);
      }
    }
    expect(accepted).toBeGreaterThan(500);
  });

  it.each([
    ['ghp_abcdefghijklmnopqrstuvwxyz0123456789'],
    ['AKIAIOSFODNN7EXAMPLE'],
    ['-----BEGIN PRIVATE KEY-----'],
  ])('refuses the secret-shaped value %s under any key', (value) => {
    const next = rng(11);
    for (let i = 0; i < 50; i += 1) {
      const entries = generated(next).filter(([key]) => key !== 'browser');
      entries.splice(Math.floor(next() * (entries.length + 1)), 0, ['browser', value]);
      const result = normalizeVariant(Object.fromEntries(entries));
      expect(result.ok).toBe(false);
    }
  });
});
