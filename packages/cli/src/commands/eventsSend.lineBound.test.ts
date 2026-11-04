// The `--from` line bound is measured in bytes, like the spool reader's own
// `maxSpoolLineBytes`: a line that only looks short in UTF-16 code units, or that grows past
// the bound while it streams, is refused before anything is spooled.
import { readdir } from 'node:fs/promises';
import { join } from 'node:path';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { resetSecrets } from '../redact.js';
import { maxSpoolLineBytes } from '../spool.js';

import {
  acked,
  baseEnv,
  capture,
  cleanupSpecState,
  invoke,
  runId,
  sendArgv,
  stubFetch,
  tempDir,
} from './eventsSend.testing.js';

describe('events send', () => {
  beforeEach(() => {
    resetSecrets();
  });

  afterEach(async () => {
    await cleanupSpecState();
  });

  // N3: the `--from` line bound must be measured in BYTES, like the spool reader's own
  // `maxSpoolLineBytes`. A 3-byte BMP character repeated past the byte cap while staying
  // under it in UTF-16 code units would otherwise be spooled and then be `spool_corrupt`
  // for the reader, blocking that producer.
  it('refuses a multibyte --from line that is over the byte cap (N3)', async () => {
    const spoolDir = await tempDir();
    // 11.2M code units (< the 32 Mi unit count) but 33.6M UTF-8 bytes (> the 32 Mi byte
    // cap). The line has no newline, so it is still the pending partial line.
    const overLong = '\u4e00'.repeat(11_200_000);
    expect(overLong.length).toBeLessThan(maxSpoolLineBytes);
    expect(Buffer.byteLength(overLong, 'utf8')).toBeGreaterThan(maxSpoolLineBytes);
    const bytes = Buffer.from(overLong, 'utf8');
    let pulled = false;
    const overLongStream: AsyncIterable<Uint8Array> = {
      [Symbol.asyncIterator]: () => ({
        next: (): Promise<IteratorResult<Uint8Array>> => {
          const done = pulled;
          pulled = true;
          return Promise.resolve(
            done ? { done: true, value: undefined } : { done: false, value: bytes },
          );
        },
      }),
    };
    const stub = stubFetch((call) => acked(call));
    const out = capture();

    const code = await invoke(sendArgv(['--from', '-']), baseEnv(spoolDir), out, {
      fetch: stub.fetchImpl,
      stdin: overLongStream,
    });

    expect(code).toBe(3);
    expect(stub.calls).toHaveLength(0);
    const stderr = out.stderr.join('');
    expect(stderr).toContain('evidgate: error invalid_spool_input:');
    expect(stderr).toContain(`line 1 is longer than ${maxSpoolLineBytes} bytes`);
    // Refused before the first append: not even the run directory exists.
    await expect(readdir(join(spoolDir, runId))).rejects.toThrow(/ENOENT/);
  });

  /** A stdin of exactly these byte blocks, then end of stream. */
  function blocks(chunks: ReadonlyArray<Uint8Array>): AsyncIterable<Uint8Array> {
    let index = 0;
    return {
      [Symbol.asyncIterator]: () => ({
        next: (): Promise<IteratorResult<Uint8Array>> => {
          const chunk = chunks[index];
          index += 1;
          return Promise.resolve(
            chunk === undefined ? { done: true, value: undefined } : { done: false, value: chunk },
          );
        },
      }),
    };
  }

  // N3 follow-up: a line that GROWS past the bound and completes (its newline arrives)
  // inside the same read block never sits in the pending buffer at the end of that block,
  // so the pending check cannot see it. The completed line is measured before the yield.
  it('refuses an over-cap line that completes after a pending prefix (N3)', async () => {
    const spoolDir = await tempDir();
    // A cap-1 pending prefix passes the pending check, then the rest of the line plus its
    // newline arrives in a second block: only the completed-line check can refuse it.
    const prefix = 'a'.repeat(maxSpoolLineBytes - 1);
    const stub = stubFetch((call) => acked(call));
    const out = capture();

    const code = await invoke(sendArgv(['--from', '-']), baseEnv(spoolDir), out, {
      fetch: stub.fetchImpl,
      stdin: blocks([Buffer.from(prefix, 'utf8'), Buffer.from('ab\n', 'utf8')]),
    });

    expect(code).toBe(3);
    expect(stub.calls).toHaveLength(0);
    const stderr = out.stderr.join('');
    expect(stderr).toContain('evidgate: error invalid_spool_input:');
    expect(stderr).toContain(`line 1 is longer than ${maxSpoolLineBytes} bytes`);
    await expect(readdir(join(spoolDir, runId))).rejects.toThrow(/ENOENT/);
  });

  it('refuses an over-cap line that arrives complete in one block (N3)', async () => {
    const spoolDir = await tempDir();
    const stub = stubFetch((call) => acked(call));
    const out = capture();

    const code = await invoke(sendArgv(['--from', '-']), baseEnv(spoolDir), out, {
      fetch: stub.fetchImpl,
      stdin: blocks([Buffer.from(`${'a'.repeat(maxSpoolLineBytes + 1)}\n`, 'utf8')]),
    });

    expect(code).toBe(3);
    expect(stub.calls).toHaveLength(0);
    const stderr = out.stderr.join('');
    expect(stderr).toContain('evidgate: error invalid_spool_input:');
    expect(stderr).toContain(`line 1 is longer than ${maxSpoolLineBytes} bytes`);
    await expect(readdir(join(spoolDir, runId))).rejects.toThrow(/ENOENT/);
  });
});
