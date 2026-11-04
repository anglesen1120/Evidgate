// The presigned PUT's timers: the idle timeout retries a send that stops moving, the upload
// deadline aborts it, and the 30 s per-request timeout of the JSON calls does not apply to
// it. Time comes from the injected seam, so a spec advances the clock and fires what is due
// itself and nothing races the real file read.

import { describe, expect, it, vi } from 'vitest';

import { CliError } from './errors.js';
import { putStallTimeoutMs, uploadFile, type UploadDeps, type UploadTimers } from './upload.js';
import { harness, json, readyBody, uploadUrlBody, useUploadSpec } from './upload.testing.js';

/** The URL of a fetch call, whichever of the three spellings the transport used. */
function fetchUrl(input: string | URL | Request): string {
  return typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
}

/**
 * A timer seam over a virtual clock: `set` records when the timer is due instead of arming
 * anything, and `fireDue()` (called by the spec at the exact point simulated time passes)
 * runs whatever is due, in due order. Nothing here races real file or network I/O, so a
 * spec that drives time through this seam never depends on how fast the runner is.
 */
function virtualTimers(clock: { now: number }): {
  readonly timers: UploadTimers;
  readonly scheduled: Map<number, { readonly ms: number; readonly fn: () => void }>;
  readonly fireDue: () => number;
} {
  const scheduled = new Map<
    number,
    { readonly due: number; readonly ms: number; readonly fn: () => void }
  >();
  let nextId = 1;
  const timers: UploadTimers = {
    set: (milliseconds, fn) => {
      const id = nextId;
      nextId += 1;
      scheduled.set(id, { due: clock.now + milliseconds, ms: milliseconds, fn });
      return id;
    },
    clear: (handle) => {
      scheduled.delete(handle as number);
    },
  };
  const fireDue = (): number => {
    let fired = 0;
    for (;;) {
      const due = [...scheduled.entries()]
        .filter(([, entry]) => entry.due <= clock.now)
        .sort((left, right) => left[1].due - right[1].due)[0];
      if (due === undefined) return fired;
      scheduled.delete(due[0]);
      due[1].fn();
      fired += 1;
    }
  };
  return { timers, scheduled, fireDue };
}

/** Rejects with the signal's reason when the request is aborted: the only non-retryable exit. */
function aborted(signal: AbortSignal): Promise<never> {
  return new Promise<never>((_resolve, reject) => {
    const fail = (): void => {
      const reason: unknown = signal.reason;
      reject(reason instanceof Error ? reason : new Error('the request was aborted'));
    };
    if (signal.aborted) {
      fail();
      return;
    }
    signal.addEventListener('abort', fail, { once: true });
  });
}

/** The method of a fetch call; the SDK passes a Request, the PUT passes `init`. */
function fetchMethod(input: string | URL | Request, init: RequestInit | undefined): string {
  const method = init?.method ?? (input instanceof Request ? input.method : 'GET');
  return method.toUpperCase();
}

describe('uploadFile', () => {
  const { file, request } = useUploadSpec();

  // F2 (P1): the presigned PUT is bounded by the remaining upload deadline only. It used to
  // carry the 30 s per-request timeout of the JSON calls, which aborts a trace that takes
  // longer than half a minute to send. Time comes from the injected seam, so nothing races
  // the real file read: the spec advances the clock and fires what is due itself.
  it('does_not_abort_a_put_that_takes_longer_than_thirty_seconds', async () => {
    const content = Buffer.from('trace-bytes');
    const { opened: fileHandle, sha256 } = await file('trace.zip', content);
    const clock = { now: 1_000 };
    const { timers, scheduled, fireDue } = virtualTimers(clock);
    const signals: AbortSignal[] = [];
    const deps: UploadDeps = {
      timers,
      sleep: (milliseconds) => {
        clock.now += milliseconds;
        return Promise.resolve();
      },
      now: () => clock.now,
      random: () => 0.5,
      fetch: async (input, init) => {
        const url = fetchUrl(input);
        if (fetchMethod(input, init) !== 'PUT') {
          return url.endsWith('/complete')
            ? json(200, readyBody(content.byteLength))
            : json(201, uploadUrlBody({ bytes: content, sha256 }));
        }
        const request: RequestInit = init ?? {};
        const signal = request.signal as AbortSignal;
        signals.push(signal);
        // 45 simulated seconds per chunk: past the 30 s per-request cap of the API calls.
        for await (const chunk of request.body as AsyncIterable<Uint8Array>) {
          clock.now += 45_000;
          fireDue();
          if (signal.aborted) throw signal.reason;
          if (chunk.byteLength === 0) break;
        }
        return new Response(null, { status: 200 });
      },
    };

    const outcome = await uploadFile(request({ opened: fileHandle, sha256 }, { deps }, undefined));

    expect(outcome).toMatchObject({ state: 'ready', bytes: content.byteLength });
    expect(signals).toHaveLength(1);
    expect(signals[0]?.aborted).toBe(false);
    // Both timers came from the seam and both were cleared: nothing outlives the PUT.
    expect(scheduled.size).toBe(0);
  });

  it('aborts_the_put_at_the_upload_deadline', async () => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    const clock = { now: 1_000 };
    const { timers, scheduled, fireDue } = virtualTimers(clock);
    let abortedAtResponse: boolean | undefined;
    const deps: UploadDeps = {
      timers,
      sleep: (milliseconds) => {
        clock.now += milliseconds;
        return Promise.resolve();
      },
      now: () => clock.now,
      random: () => 0.5,
      fetch: async (input, init) => {
        const url = fetchUrl(input);
        if (fetchMethod(input, init) !== 'PUT') {
          return url.endsWith('/complete')
            ? json(200, readyBody(11))
            : json(201, uploadUrlBody({ bytes: Buffer.from('trace-bytes'), sha256 }));
        }
        const request: RequestInit = init ?? {};
        for await (const chunk of request.body as AsyncIterable<Uint8Array>) {
          // the bytes leave; the store never answers
          if (chunk.byteLength === 0) break;
        }
        // Only the upload deadline can end this request, and the seam is its only clock.
        clock.now += 5_000;
        fireDue();
        const signal = request.signal as AbortSignal;
        abortedAtResponse = signal.aborted;
        if (signal.aborted) throw signal.reason;
        return new Response(null, { status: 200 });
      },
    };

    await expect(
      uploadFile(
        request({ opened: fileHandle, sha256 }, { deps }, undefined, { deadlineMs: 5_000 }),
      ),
    ).rejects.toMatchObject({ code: 'deadline_exceeded' });

    expect(abortedAtResponse).toBe(true);
    expect(scheduled.size).toBe(0);
  });

  // N3 (P3): a store that answers 2xx before it has read the body and then drops the request.
  // The body generator never started, so no `finally` runs — only the stream's `close` can
  // settle `done`. The attempt must end as the abort (retried), never as a hang, and the
  // cleared timers must leave nothing armed.
  it('does_not_hang_when_the_store_answers_before_the_body_is_sent', async () => {
    const content = Buffer.from('trace-bytes');
    const { opened: fileHandle, sha256 } = await file('trace.zip', content);
    const clock = { now: 1_000 };
    const { timers, scheduled, fireDue } = virtualTimers(clock);
    let puts = 0;
    const deps: UploadDeps = {
      timers,
      sleep: (milliseconds) => {
        clock.now += milliseconds;
        return Promise.resolve();
      },
      now: () => clock.now,
      random: () => 0.5,
      fetch: async (input, init) => {
        const url = fetchUrl(input);
        if (fetchMethod(input, init) !== 'PUT') {
          return url.endsWith('/complete')
            ? json(200, readyBody(content.byteLength))
            : json(201, uploadUrlBody({ bytes: content, sha256 }));
        }
        puts += 1;
        const request: RequestInit = init ?? {};
        if (puts === 1) {
          // An early 2xx: the store answers before a single byte and closes the connection,
          // which destroys the request body it never read. `putOnce` hands this fake the
          // Node Readable it built, so the cast names the real shape rather than BodyInit.
          const body = request.body as unknown as { destroy(): void };
          body.destroy();
          // The idle timer then aborts the attempt: the body can never make progress.
          clock.now += putStallTimeoutMs;
          fireDue();
          return new Response(null, { status: 200 });
        }
        for await (const chunk of request.body as AsyncIterable<Uint8Array>) {
          if (chunk.byteLength === 0) break;
        }
        return new Response(null, { status: 200 });
      },
    };

    const outcome = await uploadFile(request({ opened: fileHandle, sha256 }, { deps }, undefined));

    expect(outcome.state).toBe('ready');
    expect(puts).toBe(2);
    // Neither attempt left a timer armed: nothing can keep the process alive.
    expect(scheduled.size).toBe(0);
  });

  // N4 (P3): the idle timer guards the SENDING of the body only. A store that needs longer
  // than the idle budget to verify a large checksum after the last byte is not a stall, and
  // the deadline (30 min here) is what bounds the wait for its answer.
  it('waits_for_a_store_that_answers_long_after_the_last_byte', async () => {
    const content = Buffer.from('trace-bytes');
    const { opened: fileHandle, sha256 } = await file('trace.zip', content);
    const clock = { now: 1_000 };
    const { timers, scheduled, fireDue } = virtualTimers(clock);
    let abortedAfterBody: boolean | undefined;
    const deps: UploadDeps = {
      timers,
      sleep: (milliseconds) => {
        clock.now += milliseconds;
        return Promise.resolve();
      },
      now: () => clock.now,
      random: () => 0.5,
      fetch: async (input, init) => {
        const url = fetchUrl(input);
        if (fetchMethod(input, init) !== 'PUT') {
          return url.endsWith('/complete')
            ? json(200, readyBody(content.byteLength))
            : json(201, uploadUrlBody({ bytes: content, sha256 }));
        }
        const request: RequestInit = init ?? {};
        for await (const chunk of request.body as AsyncIterable<Uint8Array>) {
          if (chunk.byteLength === 0) break;
        }
        // Every byte is in; the store verifies the checksum for 90 s before it answers.
        clock.now += 90_000;
        fireDue();
        abortedAfterBody = (request.signal as AbortSignal).aborted;
        return new Response(null, { status: 200 });
      },
    };

    const outcome = await uploadFile(request({ opened: fileHandle, sha256 }, { deps }, undefined));

    expect(outcome).toMatchObject({ state: 'ready', bytes: content.byteLength });
    expect(abortedAfterBody).toBe(false);
    expect(scheduled.size).toBe(0);
  });

  // F2: a connection that stops moving is caught by the idle timeout (which resets on every
  // body chunk) and retried, so a stalled store never eats the rest of the deadline.
  it('retries_a_put_that_stops_making_progress', async () => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    const clock = { now: 1_000 };
    const scheduled = new Map<number, { readonly ms: number; readonly fn: () => void }>();
    let nextTimer = 1;
    const timers: UploadTimers = {
      set: (milliseconds, fn) => {
        const id = nextTimer;
        nextTimer += 1;
        scheduled.set(id, { ms: milliseconds, fn });
        return id;
      },
      clear: (handle) => {
        scheduled.delete(handle as number);
      },
    };
    let puts = 0;
    const deps: UploadDeps = {
      timers,
      sleep: (milliseconds) => {
        clock.now += milliseconds;
        return Promise.resolve();
      },
      now: () => clock.now,
      random: () => 0.5,
      fetch: async (input, init) => {
        const url = fetchUrl(input);
        if (fetchMethod(input, init) !== 'PUT') {
          return url.endsWith('/complete')
            ? json(200, readyBody(11))
            : json(201, uploadUrlBody({ bytes: Buffer.from('trace-bytes'), sha256 }));
        }
        puts += 1;
        const request: RequestInit = init ?? {};
        const signal = request.signal as AbortSignal;
        if (puts === 1) {
          // The connection stalls: no body progress, no response — only the idle timeout
          // can end this attempt.
          await aborted(signal);
        }
        for await (const chunk of request.body as AsyncIterable<Uint8Array>) {
          // drain the retried body so `body.done` settles
          if (chunk.byteLength === 0) break;
        }
        return new Response(null, { status: 200 });
      },
    };

    const pending = uploadFile(request({ opened: fileHandle, sha256 }, { deps }, undefined));
    await vi.waitFor(() => {
      expect([...scheduled.values()].map((entry) => entry.ms)).toContain(putStallTimeoutMs);
    });
    for (const [id, entry] of [...scheduled]) {
      if (entry.ms === putStallTimeoutMs) {
        scheduled.delete(id);
        entry.fn();
      }
    }

    await expect(pending).resolves.toMatchObject({ state: 'ready' });
    expect(puts).toBe(2);
  });

  // §5 retry rule: a transport failure is retried; the second attempt is the same PUT.
  it('retries_a_transport_failure', async () => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    let puts = 0;
    const store = harness((call) => {
      if (call.method === 'PUT') {
        puts += 1;
        if (puts === 1) throw new TypeError('fetch failed');
        return new Response(null, { status: 200 });
      }
      if (call.url.endsWith('/complete')) return json(200, readyBody(11));
      return json(201, uploadUrlBody({ bytes: Buffer.from('trace-bytes'), sha256 }));
    });

    await uploadFile(request({ opened: fileHandle, sha256 }, store, undefined));

    expect(puts).toBe(2);
    expect(store.sleeps).toHaveLength(1);
  });

  // §9 `deadline_respected`: a store that never succeeds inside the deadline surfaces the
  // last error's code and status, and the journal is not advanced.
  it('stops_at_the_upload_deadline', async () => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    const store = harness(() =>
      json(503, {
        error: { code: 'unavailable', message: 'down', request_id: 'req-3', retryable: true },
      }),
    );

    let thrown: unknown;
    try {
      await uploadFile({
        ...request({ opened: fileHandle, sha256 }, store, undefined),
        deadlineMs: 1_000,
      });
    } catch (error) {
      thrown = error;
    }

    const error = thrown as CliError;
    expect(error.code).toBe('deadline_exceeded');
    expect(error.message).toContain('unavailable (HTTP 503) request_id=req-3');
    // The message never names the URL or the file.
    expect(error.message).not.toContain('http');
    expect(error.message).not.toContain('trace.zip');
  });
});
