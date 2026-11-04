// The sender retry edges the unit spec does not pin down: the gzip header decision, a
// Retry-After that meets or exceeds the deadline, an absurd one, a 429 storm, a permanent
// 422, and an ack list in the wrong order. Every clock and every fetch is injected; nothing
// waits on a real timer.
import { describe, expect, it } from 'vitest';

import { jsonResponse } from './commands/cliSpec.testing.js';
import { resolveConfig } from './config.js';
import {
  ackFor,
  ackNothing,
  batchesOf,
  bodyEvents,
  clock,
  depsFor,
  eventsOf,
  moderatelyCompressiblePad,
  runId,
  synth,
} from './qa.spoolSend.testing.js';
import { sendBatches } from './sender.js';

describe('sender: retry edges (04.2.3 AC1, AC5)', () => {
  const config = resolveConfig({
    env: {
      EVIDGATE_API_URL: 'http://127.0.0.1:8080',
      EVIDGATE_PROJECT: 'proj-1',
      EVIDGATE_ENV: 'dev',
    },
  });
  const token = ['dev', 'token', '0123456789'].join('-');

  it('sends no Content-Encoding for a small body and sets gzip only when the batch carries gzipBody', async () => {
    const seen: Array<string | null> = [];
    const fetchImpl: typeof fetch = async (input, init) => {
      const request = input instanceof Request ? input : new Request(input, init);
      seen.push(request.headers.get('content-encoding'));
      return ackFor(await bodyEvents(input, init));
    };
    const small = await batchesOf([synth(1, 0)]);
    const pad = moderatelyCompressiblePad();
    const big = await batchesOf([synth(2, pad.length, pad)]);
    const c = clock();
    await sendBatches(
      eventsOf(small),
      { config, token, runId, deadlineMs: 60_000 },
      depsFor(fetchImpl, c),
      ackNothing,
    );
    await sendBatches(
      eventsOf(big),
      { config, token, runId, deadlineMs: 60_000 },
      depsFor(fetchImpl, c),
      ackNothing,
    );
    expect(seen).toEqual([null, 'gzip']);
  });

  it('gives up when Retry-After equals the remaining deadline: no sleep that reaches the deadline', async () => {
    const c = clock();
    const calls: number[] = [];
    const fetchImpl: typeof fetch = async () => {
      await Promise.resolve();
      calls.push(c.now());
      return jsonResponse(
        429,
        { error: { code: 'rate_limited', message: 'slow', request_id: 'r', retryable: true } },
        { 'retry-after': '5' },
      );
    };
    const batches = await batchesOf([synth(1, 0)]);
    await expect(
      sendBatches(
        eventsOf(batches),
        { config, token, runId, deadlineMs: 5_000 },
        depsFor(fetchImpl, c),
        ackNothing,
      ),
    ).rejects.toMatchObject({ code: 'deadline_exceeded' });
    expect(calls).toHaveLength(1);
    expect(c.sleeps).toEqual([]);
  });

  it('does not wait out an absurd Retry-After: one attempt, deadline_exceeded, zero sleeps', async () => {
    const c = clock();
    let calls = 0;
    const fetchImpl: typeof fetch = async () => {
      await Promise.resolve();
      calls += 1;
      return jsonResponse(
        429,
        { error: { code: 'rate_limited', message: 'slow', request_id: 'r', retryable: true } },
        { 'retry-after': '999999999' },
      );
    };
    const batches = await batchesOf([synth(1, 0)]);
    await expect(
      sendBatches(
        eventsOf(batches),
        { config, token, runId, deadlineMs: 60_000 },
        depsFor(fetchImpl, c),
        ackNothing,
      ),
    ).rejects.toMatchObject({ code: 'deadline_exceeded' });
    expect(calls).toBe(1);
    expect(c.sleeps).toEqual([]);
  });

  it('honours a Retry-After that fits the deadline, then delivers', async () => {
    const c = clock();
    let calls = 0;
    const fetchImpl: typeof fetch = async (input, init) => {
      calls += 1;
      if (calls === 1) {
        return jsonResponse(
          429,
          { error: { code: 'rate_limited', message: 'slow', request_id: 'r', retryable: true } },
          { 'retry-after': '4' },
        );
      }
      return ackFor(await bodyEvents(input, init));
    };
    const batches = await batchesOf([synth(1, 0)]);
    const acked: number[] = [];
    await sendBatches(
      eventsOf(batches),
      { config, token, runId, deadlineMs: 5_000 },
      depsFor(fetchImpl, c),
      async (batch) => {
        await Promise.resolve();
        acked.push(batch.lastSequence);
      },
    );
    expect(c.sleeps).toEqual([4_000]);
    expect(acked).toEqual([1]);
  });

  it('a 429 storm inside a 10 s deadline ends with deadline_exceeded and never sleeps past it', async () => {
    const c = clock();
    const start = c.now();
    let calls = 0;
    const fetchImpl: typeof fetch = async () => {
      await Promise.resolve();
      calls += 1;
      return jsonResponse(
        429,
        { error: { code: 'rate_limited', message: 'slow', request_id: 'r', retryable: true } },
        { 'retry-after': '1' },
      );
    };
    const batches = await batchesOf([synth(1, 0)]);
    await expect(
      sendBatches(
        eventsOf(batches),
        { config, token, runId, deadlineMs: 10_000 },
        depsFor(fetchImpl, c),
        ackNothing,
      ),
    ).rejects.toMatchObject({ code: 'deadline_exceeded' });
    expect(c.now() - start).toBeLessThanOrEqual(10_000);
    expect(c.sleeps.reduce((a, b) => a + b, 0)).toBeLessThanOrEqual(10_000);
    expect(calls).toBeGreaterThan(1);
  });

  it('a permanent 422 is one attempt with the envelope code, never retried', async () => {
    const c = clock();
    let calls = 0;
    const fetchImpl: typeof fetch = async () => {
      await Promise.resolve();
      calls += 1;
      return jsonResponse(422, {
        error: {
          code: 'validation_failed',
          message: 'refused',
          request_id: 'req-x',
          retryable: false,
        },
      });
    };
    const batches = await batchesOf([synth(1, 0)]);
    await expect(
      sendBatches(
        eventsOf(batches),
        { config, token, runId, deadlineMs: 60_000 },
        depsFor(fetchImpl, c),
        ackNothing,
      ),
    ).rejects.toMatchObject({ code: 'validation_failed', status: 422 });
    expect(calls).toBe(1);
    expect(c.sleeps).toEqual([]);
  });

  it('an ack list in the wrong order is refused and the batch is never recorded as acked', async () => {
    const c = clock();
    const fetchImpl: typeof fetch = async (input, init) =>
      ackFor(await bodyEvents(input, init), true);
    const batches = await batchesOf([synth(1, 0), synth(2, 0)]);
    const acked: number[] = [];
    await expect(
      sendBatches(
        eventsOf(batches),
        { config, token, runId, deadlineMs: 60_000 },
        depsFor(fetchImpl, c),
        async (batch) => {
          await Promise.resolve();
          acked.push(batch.lastSequence);
        },
      ),
    ).rejects.toThrow();
    expect(acked).toEqual([]);
  });
});
