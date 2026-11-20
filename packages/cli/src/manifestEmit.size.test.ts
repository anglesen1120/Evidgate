import { afterEach, describe, expect, it, vi } from 'vitest';

import {
  buildManifest,
  manifestMaxBytes,
  plannedExecutionsBytes,
  type PlannedExecution,
} from './manifestEmit.js';

const caseId = (n: number): string => `33333333-3333-4333-8333-${String(n).padStart(12, '0')}`;

/** A full case x variant grid: every case carries every variant, as the manifest requires. */
function members(
  cases: number,
  variants: number,
  value: (n: number) => string,
): PlannedExecution[] {
  return Array.from({ length: cases * variants }, (_unused, n) => ({
    caseRevisionId: caseId(Math.floor(n / variants) + 1),
    params: { k: value(n % variants) },
  }));
}

const revision = '11111111-1111-4111-8111-111111111111';
const byteLength = (text: string): number => Buffer.byteLength(text, 'utf8');

afterEach(() => {
  vi.restoreAllMocks();
});

describe('plannedExecutionsBytes', () => {
  const shapes: readonly { name: string; plan: PlannedExecution[]; envs: string[] }[] = [
    { name: 'ascii, one env', plan: members(3, 3, (n) => `v${n}`), envs: ['default'] },
    {
      name: 'multi-byte params, two envs',
      plan: members(5, 2, (n) => `日本語😀é${n}`),
      envs: ['a', 'b'],
    },
    { name: 'escapes in params', plan: members(2, 1, () => '"\\\n\u0001'), envs: ['x'] },
    {
      name: 'multi-byte and escaped envs',
      plan: members(2, 2, (n) => `p${n}`),
      envs: ['é', '日"本', 'z'],
    },
    {
      name: 'one member, empty params',
      plan: [{ caseRevisionId: caseId(1), params: {} }],
      envs: ['e'],
    },
  ];

  it.each(shapes)('is exact for the planned array and never above the document: $name', (shape) => {
    const text = buildManifest(revision, shape.plan, { targetEnvs: shape.envs });
    const document = JSON.parse(text) as { planned_executions: unknown[] };
    const estimate = plannedExecutionsBytes(shape.plan, shape.envs);

    expect(estimate).toBe(byteLength(JSON.stringify(document.planned_executions)));
    expect(estimate).toBeLessThanOrEqual(byteLength(text));
  });

  it('is zero-entry safe', () => {
    expect(plannedExecutionsBytes([], ['default'])).toBe(2);
  });
});

describe('buildManifest size precheck', () => {
  /** Pads one member's param so the planned array is exactly `target` bytes. */
  function planOfSize(target: number): PlannedExecution[] {
    const plan = members(1, 10, (n) => `x${n}`);
    const base = plannedExecutionsBytes(plan, ['default']);
    plan[0] = { caseRevisionId: caseId(1), params: { k: 'y'.repeat(2 + target - base) } };
    return plan;
  }

  it('builds at exactly the cap and refuses one byte over, before building the document', () => {
    const atCap = planOfSize(manifestMaxBytes);
    expect(plannedExecutionsBytes(atCap, ['default'])).toBe(manifestMaxBytes);
    expect(buildManifest(revision, atCap).length).toBeGreaterThan(manifestMaxBytes);

    const stringify = vi.spyOn(JSON, 'stringify');
    const over = planOfSize(manifestMaxBytes + 1);
    stringify.mockClear();
    let thrown: unknown;
    try {
      buildManifest(revision, over);
    } catch (error) {
      thrown = error;
    }

    expect(thrown).toMatchObject({ code: 'manifest_invalid' });
    const builtDocument = stringify.mock.calls.some(
      ([value]) => typeof value === 'object' && value !== null && 'planned_executions' in value,
    );
    expect(builtDocument).toBe(false);
  });

  it('refuses a plan that is over the cap only because of its target environments', () => {
    const plan = members(10_000, 1, () => 'v');
    const envs = Array.from({ length: 32 }, (_unused, n) => `environment-${n}`);

    expect(plannedExecutionsBytes(plan, envs)).toBeGreaterThan(manifestMaxBytes);
    expect(() => buildManifest(revision, plan, { targetEnvs: envs })).toThrowError(
      /transport limit/,
    );
  });
});
