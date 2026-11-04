// The run-directory cap (`EVIDGATE_SPOOL_MAX_BYTES`): intake refuses before the first append
// once the cap would be crossed, the streaming check cuts an endless `--from -` short, the
// pre-append total covers every producer at once, and a send refuses a run already above it.
import { mkdir, readdir, writeFile } from 'node:fs/promises';
import { join } from 'node:path';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { resetSecrets } from '../redact.js';
import { listProducers } from '../spool.js';

import {
  acked,
  baseEnv,
  capture,
  cleanupSpecState,
  envelopeText,
  errorResponse,
  invoke,
  openSpool,
  otherProducer,
  producer,
  runId,
  sendArgv,
  stubFetch,
  tempDir,
  virtualClock,
} from './eventsSend.testing.js';

describe('events send', () => {
  beforeEach(() => {
    resetSecrets();
  });

  afterEach(async () => {
    await cleanupSpecState();
  });

  it('refuses an intake that would cross the cap, naming run and range (AC3)', async () => {
    const input = await tempDir();
    const spoolDir = await tempDir();
    const file = join(input, 'events.jsonl');
    await writeFile(file, `${[envelopeText(1), envelopeText(2)].join('\n')}\n`);
    const stub = stubFetch((call) => acked(call));
    const out = capture();

    const code = await invoke(
      sendArgv(['--from', file]),
      baseEnv(spoolDir, { EVIDGATE_SPOOL_MAX_BYTES: '1' }),
      out,
      { fetch: stub.fetchImpl },
    );

    expect(code).toBe(3);
    expect(stub.calls).toHaveLength(0);
    const stderr = out.stderr.join('');
    expect(stderr).toContain('evidgate: error spool_cap_exceeded:');
    expect(stderr).toContain(`run ${runId}`);
    // CR2: the cap is refused while the first line is still the last one read, so the
    // range names the producer and the sequence that could not be spooled.
    expect(stderr).toContain('unsent sequences 1..1');
    expect(stderr).toContain('EVIDGATE_SPOOL_MAX_BYTES');
    // The refusal happens before the first append, so the run directory is never created:
    // no chunk, no cursor, not even the producer directory.
    await expect(readdir(join(spoolDir, runId))).rejects.toThrow(/ENOENT/);
    expect(await listProducers(spoolDir, runId)).toEqual([]);
  });

  // CR2/PF3/SC2: the byte total is kept while the input streams, so an endless `--from -`
  // is refused after the cap's worth of lines instead of being buffered whole (the old
  // code held every line of every producer in memory before the first cap check).
  it('refuses a --from stream while it is still being read (CR2)', async () => {
    const spoolDir = await tempDir();
    const capBytes = 4_096;
    let yielded = 0;
    // An endless stdin: every pull is a new envelope. The intake must stop reading it
    // once the cap is crossed, so `yielded` stays tiny.
    const endless: AsyncIterable<Uint8Array> = {
      [Symbol.asyncIterator]: () => ({
        next: (): Promise<IteratorResult<Uint8Array>> => {
          yielded += 1;
          return Promise.resolve({
            done: false,
            value: Buffer.from(`${envelopeText(yielded)}\n`, 'utf8'),
          });
        },
      }),
    };

    const out = capture();
    const code = await invoke(
      sendArgv(['--from', '-']),
      baseEnv(spoolDir, { EVIDGATE_SPOOL_MAX_BYTES: String(capBytes) }),
      out,
      { stdin: endless },
    );

    expect(code).toBe(3);
    // One envelope is ~240 bytes, so ~16 fit in 4 KiB: an endless stream must be cut off
    // after a few lines, never after the whole input.
    expect(yielded).toBeLessThan(64);
    const stderr = out.stderr.join('');
    expect(stderr).toContain('evidgate: error spool_cap_exceeded:');
    expect(stderr).toContain(`run ${runId}`);
    expect(stderr).toContain('EVIDGATE_SPOOL_MAX_BYTES');
    // Nothing was created at all: the refusal happens before the first append.
    await expect(readdir(join(spoolDir, runId))).rejects.toThrow(/ENOENT/);
  });

  // CR3: the cap precheck covers the TOTAL of every producer before the first append, so a
  // refused intake leaves no producer half-written (the per-producer `append` would have
  // kept the first producer's lines and then failed on the second).
  it('refuses an intake whose producers together cross the cap, writing nothing (CR3)', async () => {
    const input = await tempDir();
    const spoolDir = await tempDir();
    const file = join(input, 'events.jsonl');
    const first = envelopeText(1);
    const second = envelopeText(1, { producerId: otherProducer });
    await writeFile(file, `${[first, second].join('\n')}\n`);
    const lineBytes = Buffer.byteLength(first, 'utf8') + 1;
    // Each line alone would fit; only the two together cross the cap.
    const capBytes = lineBytes * 2 - 1;
    const stub = stubFetch((call) => acked(call));
    const out = capture();

    const code = await invoke(
      sendArgv(['--from', file]),
      baseEnv(spoolDir, {
        EVIDGATE_SPOOL_MAX_BYTES: String(capBytes),
      }),
      out,
      { fetch: stub.fetchImpl },
    );

    expect(code).toBe(3);
    expect(stub.calls).toHaveLength(0);
    expect(out.stderr.join('')).toContain('evidgate: error spool_cap_exceeded:');
    // Producer A's line would have been durable under the old per-producer cap check.
    await expect(readdir(join(spoolDir, runId))).rejects.toThrow(/ENOENT/);
  });

  // CR3, second half: the pre-append check must do real work, not merely repeat the
  // streaming one. The run directory is pre-seeded and then GROWS by 200 bytes while the
  // stream is being read, so every read-time total still fits and only the pre-append
  // total can refuse — with the same error and still nothing written.
  it('refuses an intake when the run directory grows past the cap mid-read (CR3)', async () => {
    const spoolDir = await tempDir();
    const seededBytes = 64;
    const growth = 200;
    const slack = 100;
    const seededProducer = 'seeded-writer';
    // A producer directory the current intake does not route to, so only its size counts.
    await mkdir(join(spoolDir, runId, seededProducer), { recursive: true, mode: 0o700 });
    await writeFile(
      join(spoolDir, runId, seededProducer, '000001.jsonl'),
      's'.repeat(seededBytes),
      {
        mode: 0o600,
      },
    );

    const lines = [envelopeText(1), envelopeText(2)];
    const totalBytes = lines.reduce((sum, line) => sum + Buffer.byteLength(line, 'utf8') + 1, 0);
    // Slack absorbs nothing: the 200-byte growth exceeds it.
    const capBytes = seededBytes + totalBytes + slack;

    let pulled = 0;
    const growing: AsyncIterable<Uint8Array> = {
      [Symbol.asyncIterator]: () => ({
        next: async (): Promise<IteratorResult<Uint8Array>> => {
          pulled += 1;
          const line = lines[pulled - 1];
          if (line !== undefined) {
            return { done: false, value: Buffer.from(`${line}\n`, 'utf8') };
          }
          // A concurrent writer grows the run directory after the last line was read.
          await writeFile(
            join(spoolDir, runId, seededProducer, '000002.jsonl'),
            'g'.repeat(growth),
            { mode: 0o600 },
          );
          return { done: true, value: undefined };
        },
      }),
    };
    const stub = stubFetch((call) => acked(call));
    const out = capture();

    const code = await invoke(
      sendArgv(['--from', '-']),
      baseEnv(spoolDir, { EVIDGATE_SPOOL_MAX_BYTES: String(capBytes) }),
      out,
      { fetch: stub.fetchImpl, stdin: growing },
    );

    expect(code).toBe(3);
    expect(pulled).toBe(lines.length + 1);
    expect(stub.calls).toHaveLength(0);
    const stderr = out.stderr.join('');
    expect(stderr).toContain('evidgate: error spool_cap_exceeded:');
    expect(stderr).toContain(`run ${runId}`);
    expect(stderr).toContain('EVIDGATE_SPOOL_MAX_BYTES');
    // The pre-append refusal leaves no producer directory behind for the input's producer.
    await expect(readdir(join(spoolDir, runId, producer))).rejects.toThrow(/ENOENT/);
  });

  it('stops before sending when the run directory is already above the cap (AC3)', async () => {
    const input = await tempDir();
    const spoolDir = await tempDir();
    const file = join(input, 'events.jsonl');
    await writeFile(file, `${[envelopeText(1), envelopeText(2)].join('\n')}\n`);
    const clock = virtualClock();

    // Run 1 spools the events; the send is refused permanently, so 1..2 stay unacked.
    const failing = stubFetch(() => errorResponse(400, 'invalid_request', 'req-400'));
    const firstCode = await invoke(sendArgv(['--from', file]), baseEnv(spoolDir), capture(), {
      fetch: failing.fetchImpl,
      now: clock.now,
      sleep: clock.sleep,
      random: () => 0.5,
    });
    expect(firstCode).toBe(3);
    expect((await openSpool(spoolDir).then((spool) => spool.cursor())).nextUnackedSequence).toBe(1);

    // Run 2 with a cap the existing directory already exceeds: no request at all.
    const stub = stubFetch((call) => acked(call));
    const out = capture();
    const code = await invoke(
      sendArgv([]),
      baseEnv(spoolDir, { EVIDGATE_SPOOL_MAX_BYTES: '64' }),
      out,
      { fetch: stub.fetchImpl, now: clock.now, sleep: clock.sleep, random: () => 0.5 },
    );

    expect(code).toBe(3);
    expect(stub.calls).toHaveLength(0);
    const stderr = out.stderr.join('');
    expect(stderr).toContain('evidgate: error spool_cap_exceeded:');
    expect(stderr).toContain(`run ${runId}`);
    expect(stderr).toContain('unsent sequences 1..2');
    expect(stderr).toContain('64 bytes');
  });
});
