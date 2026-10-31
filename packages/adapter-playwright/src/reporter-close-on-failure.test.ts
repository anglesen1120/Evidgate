// A failure buffered by an earlier callback must not skip the spool close. `close()` is what
// flushes the queued `run_end`, writes `shards.json` and releases `writer.lock`, so skipping
// it leaves a failed run's producer record unfinalised and its lock held for the next process.
import { existsSync, mkdtempSync, readFileSync, readdirSync, rmSync, statSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import type { FullResult } from '@playwright/test/reporter';
import { afterEach, describe, expect, it, vi } from 'vitest';

import type { EventEnvelope } from './envelope.js';
import { EvidgateReporter, type EvidgateReporterOptions } from './reporter.js';
import { SHARDS_FILE, SpoolWriter } from './spool.js';
import { WRITER_LOCK_FILE } from './writerLock.js';

const tempDirs: string[] = [];

afterEach(() => {
  vi.restoreAllMocks();
  for (const dir of tempDirs.splice(0)) rmSync(dir, { recursive: true, force: true });
});

function makeReporter(overrides: Partial<EvidgateReporterOptions> = {}): {
  reporter: EvidgateReporter;
  spool: string;
} {
  const spool = mkdtempSync(join(tmpdir(), 'evidgate-end-close-'));
  tempDirs.push(spool);
  const reporter = new EvidgateReporter({
    spool_dir: spool,
    run_id: 'run-1',
    producer_id: 'pw-0',
    engineVersion: '1.63.0',
    ...overrides,
  });
  return { reporter, spool };
}

function readEnvelopes(directory: string): EventEnvelope[] {
  const envelopes: EventEnvelope[] = [];
  for (const file of readdirSync(directory)
    .filter((entry) => entry.endsWith('.jsonl'))
    .sort()) {
    for (const line of readFileSync(join(directory, file), 'utf8').split('\n')) {
      if (line.length > 0) envelopes.push(JSON.parse(line) as EventEnvelope);
    }
  }
  return envelopes;
}

describe('onEnd after a buffered failure', () => {
  it('still closes the writer and fails a passed run', async () => {
    const { reporter, spool } = makeReporter();
    // The buffering seam: onBegin's own work fails once, which `guard` buffers instead of
    // throwing, so onEnd runs with a failure already recorded and a live writer.
    const appendSpy = vi.spyOn(SpoolWriter.prototype, 'appendMany').mockImplementationOnce(() => {
      throw new Error('the spool refused the begin markers');
    });
    const closeSpy = vi.spyOn(SpoolWriter.prototype, 'close');
    expect(() => reporter.onBegin()).not.toThrow();
    expect(appendSpy).toHaveBeenCalledTimes(1);

    const result: FullResult = { status: 'passed', startTime: new Date(), duration: 100 };

    await expect(reporter.onEnd(result)).resolves.toEqual({ status: 'failed' });

    expect(closeSpy).toHaveBeenCalledTimes(1);
    const directory = join(spool, 'run-1', 'pw-0');
    // The end marker was still queued when onEnd ran: only the close can flush it.
    expect(readEnvelopes(directory).map((item) => item.ext.marker)).toContain('run_end');
    expect(statSync(join(directory, SHARDS_FILE)).isFile()).toBe(true);
    expect(existsSync(join(directory, WRITER_LOCK_FILE))).toBe(false);
  });
});
