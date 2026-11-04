// Delivery under failure: a virtual ten-minute outage with jittered backoff, resume at the
// first unacked sequence after a fatal refusal, the full unsent range in the failure, and a
// cursor that advances only over a contiguous prefix of acks.
import { writeFile } from 'node:fs/promises';
import { join } from 'node:path';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { resetSecrets } from '../redact.js';

import {
  acked,
  baseEnv,
  capture,
  cleanupSpecState,
  envelopeText,
  errorResponse,
  eventIdFor,
  invoke,
  jsonResponse,
  openSpool,
  sendArgv,
  stubFetch,
  tempDir,
  virtualClock,
} from './eventsSend.testing.js';

/**
 * `Promise.withResolvers` is ES2024 and the workspace targets ES2022, so the specs carry
 * their own two-line equivalent (the gates below must resolve from inside a fetch stub).
 */
function deferred(): { readonly promise: Promise<void>; readonly resolve: () => void } {
  let resolve: () => void = () => undefined;
  const promise = new Promise<void>((settle) => {
    resolve = settle;
  });
  return { promise, resolve };
}

describe('events send', () => {
  beforeEach(() => {
    resetSecrets();
  });

  afterEach(async () => {
    await cleanupSpecState();
  });

  it('survives a ten-minute outage with virtual backoff, then delivers (AC1)', async () => {
    const input = await tempDir();
    const spoolDir = await tempDir();
    const file = join(input, 'events.jsonl');
    await writeFile(file, `${[envelopeText(1), envelopeText(2), envelopeText(3)].join('\n')}\n`);
    const clock = virtualClock();
    const startedAt = clock.now();
    // The API is down for ten virtual minutes: a batch that was stored before the ACK was
    // lost is re-sent, and every ack of the successful attempt comes back `duplicate`.
    const stub = stubFetch((call) =>
      clock.now() - startedAt < 600_000
        ? jsonResponse(
            503,
            {
              error: {
                code: 'service_unavailable',
                message: 'maintenance',
                request_id: 'req-503',
                retryable: true,
              },
            },
            { 'retry-after': '5' },
          )
        : acked(call, ['duplicate', 'duplicate', 'duplicate']),
    );
    const out = capture();

    const code = await invoke(sendArgv(['--from', file]), baseEnv(spoolDir), out, {
      fetch: stub.fetchImpl,
      now: clock.now,
      sleep: clock.sleep,
      random: () => 0.5,
    });

    expect(code).toBe(0);
    // Several attempts over the ten virtual minutes; the exact count depends on the
    // jittered backoff (the wait is the later of `Retry-After` and that backoff).
    expect(stub.calls.length).toBeGreaterThan(5);
    // One wait between attempts, never one after the last. `Retry-After: 5` is a FLOOR
    // under the jittered backoff (P3-1: a 0 or past date must not become a hot loop), so
    // each wait is at least 5 s and never past the 30 s backoff cap.
    expect(clock.sleeps).toHaveLength(stub.calls.length - 1);
    expect(clock.sleeps.every((ms) => ms >= 5_000)).toBe(true);
    expect(clock.sleeps.every((ms) => ms <= 30_000)).toBe(true);
    expect(out.stderr.join('')).toBe('');
    expect(out.stdout.join('')).toContain('batches=1 accepted=0 duplicate=3');
    // Every attempt carried the same bytes under the same key (AC5 identity rule).
    expect(new Set(stub.calls.map((call) => call.key)).size).toBe(1);
    expect(new Set(stub.calls.map((call) => call.wire.toString('base64'))).size).toBe(1);
    // `duplicate` counts as delivered: the cursor advanced once, not per attempt.
    const spool = await openSpool(spoolDir);
    expect(await spool.cursor()).toMatchObject({ nextUnackedSequence: 4, ackedCount: 3 });
  });

  it('resumes at the first unacked sequence after a fatal failure (AC2)', async () => {
    const input = await tempDir();
    const spoolDir = await tempDir();
    const total = 1_200;
    const lines = Array.from({ length: total }, (_, index) => envelopeText(index + 1));
    const file = join(input, 'events.jsonl');
    await writeFile(file, `${lines.join('\n')}\n`);
    const clock = virtualClock();
    const deps = { now: clock.now, sleep: clock.sleep, random: () => 0.5 };

    // Run 1: the first two batches are acked, the third is refused permanently.
    let served = 0;
    const firstStub = stubFetch((call) => {
      served += 1;
      return served < 3 ? acked(call) : errorResponse(403, 'unknown_producer', 'req-403');
    });
    const firstOut = capture();
    const firstCode = await invoke(sendArgv(['--from', file]), baseEnv(spoolDir), firstOut, {
      ...deps,
      fetch: firstStub.fetchImpl,
    });

    expect(firstCode).toBe(3);
    expect(firstStub.calls).toHaveLength(3);
    expect(firstOut.stderr.join('')).toContain('evidgate: error unknown_producer:');
    expect(firstOut.stderr.join('')).toContain('evidgate: request_id: req-403');
    const spool = await openSpool(spoolDir);
    // Exactly the contiguous prefix advanced: batches 1..2, never batch 3.
    expect((await spool.cursor()).nextUnackedSequence).toBe(1_001);

    // Run 2 (a fresh process): only the remaining batch goes out, under the same key.
    const secondStub = stubFetch((call) => acked(call));
    const secondOut = capture();
    const secondCode = await invoke(sendArgv([]), baseEnv(spoolDir), secondOut, {
      ...deps,
      fetch: secondStub.fetchImpl,
    });

    expect(secondCode).toBe(0);
    expect(secondStub.calls).toHaveLength(1);
    const thirdKey = firstStub.calls[2]?.key;
    expect(thirdKey).not.toBeNull();
    expect(secondStub.calls[0]?.key).toBe(thirdKey);

    // The union of what was sent is every event exactly once, each under one key.
    const sent = new Map<string, string>();
    for (const call of [...firstStub.calls, ...secondStub.calls]) {
      const key = call.key ?? '';
      for (const event of call.events) {
        const eventId = String(event['event_id']);
        const previous = sent.get(eventId);
        expect(previous === undefined || previous === key).toBe(true);
        sent.set(eventId, key);
      }
    }
    expect(sent.size).toBe(total);
    for (const sequence of [1, 500, 501, 1_000, 1_001, total]) {
      expect(sent.has(eventIdFor(sequence))).toBe(true);
    }
    expect(secondStub.calls[0]?.events).toHaveLength(200);
    expect(secondStub.calls[0]?.events[0]?.['sequence']).toBe(1_001);
    expect((await spool.cursor()).nextUnackedSequence).toBe(total + 1);
  });

  // CR7: the failure names the FULL unsent range read after the send settled, not just the
  // failed batch's range. Batch 1 (1..500) is held open on a 503 while batch 2 (501..1000)
  // fails permanently, so the cursor never moves and 1..1000 is what the operator must
  // re-send.
  it('names the full unsent range after the send settles (CR7)', async () => {
    const input = await tempDir();
    const spoolDir = await tempDir();
    const total = 1_000;
    const file = join(input, 'events.jsonl');
    await writeFile(
      file,
      `${Array.from({ length: total }, (_, index) => envelopeText(index + 1)).join('\n')}\n`,
    );
    const clock = virtualClock();
    // Batch 1 stays in flight until batch 2 has failed, so the permanent 422 is the error
    // that wins and batch 1's own retry loop cannot reach the deadline first.
    const batchTwoFailed = deferred();
    const stub = stubFetch(async (call) => {
      const first = Number(call.events[0]?.['sequence'] ?? 0);
      if (first === 1) {
        await batchTwoFailed.promise;
        return errorResponse(503, 'unavailable', 'req-503');
      }
      batchTwoFailed.resolve();
      return errorResponse(422, 'unknown_producer', 'req-422');
    });
    const out = capture();

    const code = await invoke(sendArgv(['--from', file]), baseEnv(spoolDir), out, {
      fetch: stub.fetchImpl,
      now: clock.now,
      sleep: clock.sleep,
      random: () => 0.5,
    });

    expect(code).toBe(3);
    const stderr = out.stderr.join('');
    expect(stderr).toContain('evidgate: error unknown_producer:');
    expect(stderr).toContain('unsent sequences 1..1000');
    // The failing batch's own range (501..1000) is NOT the range that is reported.
    expect(stderr).not.toContain('501..1000');
  });

  it('advances the cursor only over a contiguous prefix of out-of-order acks', async () => {
    const input = await tempDir();
    const spoolDir = await tempDir();
    const total = 1_200;
    const file = join(input, 'events.jsonl');
    await writeFile(
      file,
      `${Array.from({ length: total }, (_, index) => envelopeText(index + 1)).join('\n')}\n`,
    );
    const gate = deferred();
    const thirdServed = deferred();
    let served = 0;
    const stub = stubFetch(async (call) => {
      served += 1;
      // Batch 1 (the first sequence range) is held in flight while batches 2 and 3 ack.
      if (served === 1) {
        await gate.promise;
      }
      if (served === 3) {
        thirdServed.resolve();
      }
      return acked(call);
    });
    const clock = virtualClock();
    const out = capture();

    const run = invoke(sendArgv(['--from', file]), baseEnv(spoolDir), out, {
      fetch: stub.fetchImpl,
      now: clock.now,
      sleep: clock.sleep,
      random: () => 0.5,
    });
    // All three batches are in flight (maxInFlight is 4); two of them are already served.
    await thirdServed.promise;
    const spool = await openSpool(spoolDir);
    // Nothing may advance while the first batch's ack is missing.
    expect((await spool.cursor()).nextUnackedSequence).toBe(1);

    gate.resolve();
    expect(await run).toBe(0);
    // The completed-but-non-contiguous acks are applied only once the gap fills, and the
    // count is the whole prefix — a per-batch `ack(batch.lastSequence)` cannot do this.
    expect(await spool.cursor()).toMatchObject({
      nextUnackedSequence: total + 1,
      lastAckedSequence: total,
      ackedCount: total,
    });
  });
});
