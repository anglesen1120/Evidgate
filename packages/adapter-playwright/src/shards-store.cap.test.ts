// `EVIDGATE_SPOOL_MAX_BYTES`: how the env value resolves and the write-time per-run byte cap
// with its exact boundary (see contracts/cli.md).
import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';

import { describe, expect, it } from 'vitest';

import { type UnsignedEnvelope } from './envelope.js';
import {
  DEFAULT_SPOOL_MAX_BYTES,
  EvidgateInvalidSpoolConfigError,
  EvidgateSpoolCapExceededError,
  MAX_SPOOL_MAX_BYTES,
  resolveSpoolMaxBytes,
  SpoolWriter,
} from './spool.js';
import {
  chunks,
  envelope,
  FIXED_PID,
  options,
  producerDir,
  tempSpool,
  useTempSpoolCleanup,
} from './shards-store.testing.js';

useTempSpoolCleanup();

/** The exact UTF-8 bytes one spool line occupies (the cap is measured in these). */
function lineBytes(event: UnsignedEnvelope, sequence: number): number {
  return Buffer.byteLength(`${JSON.stringify({ ...event, sequence })}\n`, 'utf8');
}

/**
 * The run directory's base bytes before the first append: exactly the lock record this
 * writer creates (0600, `{pid, started_at}`), so a cap can be set to the byte.
 */
function lockBytes(pid: number): number {
  const scratch = tempSpool();
  const writer = new SpoolWriter(
    options(scratch, { lock: { pid, isProcessAlive: () => true, probeDelayMs: 0 } }),
  );
  const bytes = writer.runBytesInUse;
  writer.close();
  return bytes;
}

describe('EVIDGATE_SPOOL_MAX_BYTES write-time cap', () => {
  it('resolves the env value with the CLI rules', () => {
    expect(resolveSpoolMaxBytes({})).toBe(DEFAULT_SPOOL_MAX_BYTES);
    expect(DEFAULT_SPOOL_MAX_BYTES).toBe(268_435_456);
    expect(resolveSpoolMaxBytes({ EVIDGATE_SPOOL_MAX_BYTES: '' })).toBe(DEFAULT_SPOOL_MAX_BYTES);
    expect(resolveSpoolMaxBytes({ EVIDGATE_SPOOL_MAX_BYTES: '1' })).toBe(1);
    expect(resolveSpoolMaxBytes({ EVIDGATE_SPOOL_MAX_BYTES: ' 2048 ' })).toBe(2048);
    expect(resolveSpoolMaxBytes({ EVIDGATE_SPOOL_MAX_BYTES: '1048576' })).toBe(1_048_576);
    expect(resolveSpoolMaxBytes({ EVIDGATE_SPOOL_MAX_BYTES: String(MAX_SPOOL_MAX_BYTES) })).toBe(
      MAX_SPOOL_MAX_BYTES,
    );
    expect(MAX_SPOOL_MAX_BYTES).toBe(2 ** 40);
  });

  it('refuses a malformed env value without echoing it', () => {
    for (const value of ['lots', '0', '-5', '1.5', '1MiB', String(MAX_SPOOL_MAX_BYTES + 1)]) {
      let error: unknown;
      try {
        resolveSpoolMaxBytes({ EVIDGATE_SPOOL_MAX_BYTES: value });
      } catch (thrown) {
        error = thrown;
      }
      expect(error).toBeInstanceOf(EvidgateInvalidSpoolConfigError);
      const invalid = error as EvidgateInvalidSpoolConfigError;
      expect(invalid.code).toBe('invalid_config');
      expect(invalid.message).toContain('EVIDGATE_SPOOL_MAX_BYTES');
      expect(invalid.message).not.toContain(value);
    }
  });

  it('reads the env once in the constructor and lets an explicit cap win', () => {
    const spool = tempSpool();
    const fromEnv = new SpoolWriter(
      options(spool, { env: { EVIDGATE_SPOOL_MAX_BYTES: ' 4096 ' } }),
    );
    expect(fromEnv.maxRunBytes).toBe(4096);
    fromEnv.close();

    const explicit = new SpoolWriter(
      options(spool, { env: { EVIDGATE_SPOOL_MAX_BYTES: '4096' }, maxRunBytes: 8192 }),
    );
    expect(explicit.maxRunBytes).toBe(8192);
    explicit.close();
  });

  it('refuses a writer whose env cap is malformed, before touching the directory', () => {
    const spool = tempSpool();
    const malformed = { env: { EVIDGATE_SPOOL_MAX_BYTES: 'lots' } };
    expect(() => new SpoolWriter(options(spool, malformed))).toThrow(
      EvidgateInvalidSpoolConfigError,
    );
    expect(readdirSync(spool)).toEqual([]);
  });

  it('passes a batch that lands exactly on the cap and refuses the next byte', () => {
    const spool = tempSpool();
    const base = lockBytes(FIXED_PID);
    const event = envelope('pw-0');
    const cap = base + lineBytes(event, 1);
    const writer = new SpoolWriter(options(spool, { maxRunBytes: cap }));

    writer.append(event);
    expect(() => writer.flush()).not.toThrow();
    expect(writer.runBytesInUse).toBe(cap);

    writer.append(event);
    let error: unknown;
    try {
      writer.flush();
    } catch (thrown) {
      error = thrown;
    }
    expect(error).toBeInstanceOf(EvidgateSpoolCapExceededError);
    const exceeded = error as EvidgateSpoolCapExceededError;
    expect(exceeded.code).toBe('spool_cap_exceeded');
    expect(exceeded.runId).toBe('run-1');
    expect(exceeded.producerId).toBe('pw-0');
    expect(exceeded.capBytes).toBe(cap);
    expect(exceeded.usedBytes).toBe(cap);
    expect(exceeded.requiredBytes).toBe(lineBytes(event, 2));
    expect(exceeded.message).toContain('EVIDGATE_SPOOL_MAX_BYTES');

    // Never a partial line, never a silent drop: one complete line, the refused one queued.
    const chunk = readFileSync(join(producerDir(spool), '000001.jsonl'), 'utf8');
    expect(chunk.endsWith('\n')).toBe(true);
    expect(chunk.trim().split('\n')).toHaveLength(1);
    expect(writer.pendingCount).toBe(1);
    expect(writer.runBytesInUse).toBe(cap);
  });

  it('writes no line at all when a multi-line batch cannot fit', () => {
    const spool = tempSpool();
    const base = lockBytes(FIXED_PID);
    const event = envelope('pw-0');
    const cap = base + lineBytes(event, 1);
    const writer = new SpoolWriter(options(spool, { maxRunBytes: cap }));

    writer.appendMany([event, event, event]);
    expect(() => writer.flush()).toThrow(EvidgateSpoolCapExceededError);
    // All-or-nothing: the check runs before the first byte, so the (eagerly created) chunk
    // holds no complete and no partial line, and the whole batch stays queued.
    expect(chunks(spool)).toEqual([0]);
    expect(writer.pendingCount).toBe(3);
  });

  it('counts every producer of the run, not just its own directory', () => {
    const spool = tempSpool();
    const base = lockBytes(FIXED_PID);
    const event = envelope('pw-0');
    const cap = base + 2 * lineBytes(event, 1);

    const first = new SpoolWriter(options(spool, { maxRunBytes: cap }));
    first.appendMany([event, event]);
    first.flush();
    expect(first.runBytesInUse).toBe(cap);

    // The second producer's own base already includes the first producer's chunk bytes, so
    // its first byte is refused: the cap is per run directory (see contracts/cli.md).
    const second = new SpoolWriter(options(spool, { producerId: 'pw-1', maxRunBytes: cap }));
    expect(second.runBytesInUse).toBeGreaterThan(cap);
    second.append(envelope('pw-1'));
    expect(() => second.flush()).toThrow(EvidgateSpoolCapExceededError);
    expect(chunks(spool, 'pw-1')).toEqual([0]);

    first.close();
    // The refused envelope stays queued, so closing the second producer retries it and fails
    // the same way: the caller always learns about the cap (never a silent drop).
    expect(() => second.close()).toThrow(EvidgateSpoolCapExceededError);
  });
});
