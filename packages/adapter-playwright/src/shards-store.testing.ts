// The fixtures the `shards.json` record specs share: one temporary spool per spec with its
// cleanup, deterministic envelopes and writer options, and the on-disk readers the
// assertions use. Not a spec: the Vitest base collects `src/**/*.test.ts` only.
import { mkdtempSync, readdirSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, expect } from 'vitest';

import { buildEnvelope, EXT_SCHEMA, type UnsignedEnvelope } from './envelope.js';
import { type SpoolWriterOptions } from './spool.js';

const tracked: string[] = [];

/** Registers the per-spec cleanup of every directory `tempSpool()` handed out. */
export function useTempSpoolCleanup(): void {
  afterEach(() => {
    for (const dir of tracked.splice(0)) rmSync(dir, { recursive: true, force: true });
  });
}

export function tempSpool(): string {
  const dir = mkdtempSync(join(tmpdir(), 'evidgate-shards-'));
  tracked.push(dir);
  return dir;
}

export function envelope(producerId: string): UnsignedEnvelope {
  return buildEnvelope({
    producer_id: producerId,
    execution_key: '0'.repeat(64),
    attempt_no: 1,
    type: 'test.started',
    ext: { schema: EXT_SCHEMA },
  });
}

export function producerDir(spool: string, producerId = 'pw-0'): string {
  return join(spool, 'run-1', producerId);
}

/** Complete lines per chunk of a producer directory, in file-name order. */
export function chunks(spool: string, producerId = 'pw-0'): number[] {
  return readdirSync(producerDir(spool, producerId))
    .filter((name) => /^\d{6}\.jsonl$/.test(name))
    .sort()
    .map((name) => {
      const text = readFileSync(join(producerDir(spool, producerId), name), 'utf8');
      if (text === '') return 0;
      expect(text.endsWith('\n')).toBe(true);
      return text.trim().split('\n').length;
    });
}

/** A pid the liveness seam treats as alive, so two writers in this process can both exist. */
export const FIXED_PID = 1000;

export function options(
  spool: string,
  overrides: Partial<SpoolWriterOptions> = {},
): SpoolWriterOptions {
  return {
    spoolDir: spool,
    runId: 'run-1',
    producerId: 'pw-0',
    lock: { pid: FIXED_PID, isProcessAlive: () => true, probeDelayMs: 0 },
    ...overrides,
  };
}
