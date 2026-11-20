// Temporary: proves the two old copies agree on generated inputs before one is deleted.
import { writeFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';

import { normalizeVariant as cliNormalize } from '../../cli/src/variantHash.js';
import { normalizeVariant as adapterNormalize } from './variant.js';

function rng(seed: number): () => number {
  let s = seed >>> 0;
  return () => {
    s = (Math.imul(s, 1664525) + 1013904223) >>> 0;
    return s / 4294967296;
  };
}

const KEYS = [
  'browser',
  'Browser',
  'locale',
  'viewport',
  'api_token',
  'apiKey',
  'PASSWORD',
  'a',
  'b_c',
  'x.y',
  '9bad',
  'has space',
  '',
  '__proto__',
  'constructor',
  'k\u2028',
  'ключ',
  'name',
  'description',
  'title',
  'device',
  'env',
  'a'.repeat(64),
  'a'.repeat(65),
];
const STRINGS = [
  '',
  'chromium',
  ' Chromium ',
  'WEBKIT',
  'ghp_abcdefghijklmnopqrstuvwxyz0123456789',
  'AKIAIOSFODNN7EXAMPLE',
  'eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.abc',
  '-----BEGIN PRIVATE KEY-----',
  'Bearer abcdef0123456789abcdef',
  'p@ss&<>"\'+`\\',
  '\u0000',
  '\u200b',
  '\ud800',
  '\ud83d\ude00',
  'é',
  'x'.repeat(300),
  'x'.repeat(5000),
  'q8Zr4Lm1Xw9Tn2Vb6Hc3Jd7Fg5Ks0Pa',
  'https://u:p@host/',
  '2024-01-01',
];
const NUMBERS = [
  0,
  -0,
  1,
  -1,
  1.5,
  1e21,
  1e20,
  9007199254740991,
  9007199254740992,
  -9007199254740992,
  1e-7,
  Number.MAX_VALUE,
  Number.MIN_VALUE,
  NaN,
  Infinity,
  -Infinity,
  123456789012345680000,
];

function scalar(r: () => number): unknown {
  const k = Math.floor(r() * 6);
  if (k === 0) return STRINGS[Math.floor(r() * STRINGS.length)];
  if (k === 1) return NUMBERS[Math.floor(r() * NUMBERS.length)];
  if (k === 2) return r() < 0.5;
  if (k === 3) return null;
  if (k === 4) return Math.floor(r() * 1e6);
  return STRINGS[Math.floor(r() * 5)];
}

function value(r: () => number, depth: number): unknown {
  const k = r();
  if (k < 0.65 || depth > 70) return scalar(r);
  if (k < 0.85) return Array.from({ length: Math.floor(r() * 5) }, () => value(r, depth + 1));
  const o: Record<string, unknown> = {};
  for (let i = 0; i < Math.floor(r() * 3); i += 1)
    o[KEYS[Math.floor(r() * KEYS.length)]!] = value(r, depth + 1);
  return o;
}

function params(r: () => number): unknown {
  const k = r();
  if (k < 0.03) return [1, 2];
  if (k < 0.05) return 'str';
  if (k < 0.07) return null;
  const o: Record<string, unknown> = {};
  const n = k < 0.1 ? 40 : Math.floor(r() * 6);
  for (let i = 0; i < n; i += 1) {
    const key = i >= 6 ? `k${i}` : KEYS[Math.floor(r() * KEYS.length)]!;
    Object.defineProperty(o, key, {
      value: value(r, 0),
      enumerable: true,
      configurable: true,
      writable: true,
    });
  }
  return o;
}

function deep(n: number): unknown {
  let v: unknown = 1;
  for (let i = 0; i < n; i += 1) v = { a: v };
  return v;
}

function run(fn: (p: unknown) => unknown, p: unknown): string {
  try {
    const out = fn(p) as { ok: boolean; hash?: string; paramsJson?: string; problems?: unknown };
    return JSON.stringify(out.ok ? [true, out.hash, out.paramsJson] : [false, out.problems]);
  } catch (e) {
    return `THROW ${(e as Error).constructor.name}`;
  }
}

describe('old copies agree', () => {
  it('generated inputs', () => {
    const r = rng(487);
    const inputs: unknown[] = [deep(10), deep(64), deep(65), deep(200), { a: [deep(70)] }];
    for (let i = 0; i < 20000; i += 1) inputs.push(params(r));
    const classes = new Map<string, number>();
    let okCount = 0;
    for (const p of inputs) {
      const a = run(adapterNormalize, p);
      const c = run(cliNormalize, p);
      if (a.startsWith('[true')) okCount += 1;
      if (a !== c)
        classes.set(
          `${a.slice(0, 60)} | ${c.slice(0, 60)}`,
          (classes.get(`${a.slice(0, 60)} | ${c.slice(0, 60)}`) ?? 0) + 1,
        );
    }
    writeFileSync(
      '/tmp/s487-agreement.txt',
      `inputs=${inputs.length} ok=${okCount} disagreements=${[...classes.values()].reduce((x, y) => x + y, 0)}\n${[...classes].map(([k, v]) => `${v} ${k}`).join('\n')}\n`,
    );
    expect([...classes]).toEqual([]);
  });
});
