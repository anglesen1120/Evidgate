// 04.3.1-T1.1: per-callback work is O(1) — no whole-suite buffering (04 §8 performance
// row). The reporter's only buffer is the SpoolWriter's pending batch, so this pins that
// bound directly: across 10k appends the pending batch must never exceed the flush
// threshold. A writer (or reporter) that buffered the whole suite would blow past it.
// The reporter itself flushes once per callback (CR2), so a reporter-driven 10k run would
// fsync per test and measure disk, not the buffer bound this row is about.
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { describe, expect, it } from 'vitest';

import { buildEnvelope, EXT_SCHEMA } from './envelope.js';
import { PLACEHOLDER_EXECUTION_KEY } from './identity.js';
import { DEFAULT_FLUSH_THRESHOLD, SpoolWriter } from './spool.js';

const APPEND_COUNT = 10_000;

describe('memory', () => {
  it('never buffers more than the flush threshold (10k appends)', () => {
    const spool = mkdtempSync(join(tmpdir(), 'evidgate-memory-'));
    try {
      const writer = new SpoolWriter({ spoolDir: spool, runId: 'run-1', producerId: 'pw-0' });
      let maxPending = 0;
      for (let index = 0; index < APPEND_COUNT; index += 1) {
        writer.append(
          buildEnvelope({
            producer_id: 'pw-0',
            execution_key: PLACEHOLDER_EXECUTION_KEY,
            attempt_no: 1,
            type: 'test.step',
            ext: { schema: EXT_SCHEMA, title: 'click' },
          }),
        );
        maxPending = Math.max(maxPending, writer.pendingCount);
      }
      writer.close();

      expect(maxPending).toBeLessThanOrEqual(DEFAULT_FLUSH_THRESHOLD);
      expect(writer.nextSequenceNumber).toBe(APPEND_COUNT + 1);
    } finally {
      rmSync(spool, { recursive: true, force: true });
    }
  }, 60_000);
});
