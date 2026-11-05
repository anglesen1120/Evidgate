// The seams the batcher specs share: deterministic envelope lines, spool events sized to the
// byte limits, and the option and source helpers every pack assertion drives. Nothing here
// touches the network or a real timer, and no fixture uses `Math.random`, because the
// assertions compare exact byte counts, gzip decisions and derived keys.
//
// This file is not a spec: the shared Vitest base collects `src/**/*.test.ts` only. It is
// also excluded from the emitting project in tsconfig.json, so it never reaches `dist`; the
// no-emit tsconfig.spec.json is the project that type-checks it.
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { maxBatchBytes, maxBatchEvents, type PackBatchesOptions } from './batcher.js';
import { eventIdFor } from './qa.spoolSend.testing.js';
import { collect, failureOf } from './spool.testing.js';

import type { SpoolEvent } from './spool.js';

export { collect, eventIdFor };

/**
 * `spool.testing` owns the promise-settling helper; here it is named `failure` because the
 * rejected value is the assertion subject — a call that returns normally is a spec bug.
 */
export const failure = failureOf;

export const repoRoot = join(dirname(fileURLToPath(import.meta.url)), '../../..');
export const contractsDirectory = join(repoRoot, 'contracts');

/** `{"events":[` is 11 bytes, `]}` is 2 — everything a body spends that is not an event line. */
export const bodyOverheadBytes = 13;

interface EnvelopeOverrides {
  readonly eventId?: string;
  readonly type?: string;
  readonly pad?: number;
  /** Overrides the required `producer_id`, to model a line planted in another producer's directory. */
  readonly producerId?: string;
  /** Adds a `run_id` member (not in the schema), to model a line that binds itself to another run. */
  readonly runId?: string;
}

/** One compact 00 §9.4 envelope: a single line, no newline (the reporter writes these). */
export function envelopeText(sequence: number, o: EnvelopeOverrides = {}): string {
  const envelope: Record<string, unknown> = {
    schema_version: 1,
    event_id: o.eventId ?? eventIdFor(sequence),
    producer_id: o.producerId ?? 'pw-shard-0',
    sequence,
    execution_key: '0'.repeat(64),
    attempt_no: 1,
    type: o.type ?? 'run.log',
    occurred_at: '2026-01-01T00:00:00Z',
  };
  if (o.pad !== undefined) {
    envelope['ext'] = { schema: 'x'.repeat(o.pad) };
  }
  if (o.runId !== undefined) {
    envelope['run_id'] = o.runId;
  }
  return JSON.stringify(envelope);
}

/** A spool event as `ProducerSpool.readUnacked()` would hand it over. */
export function spoolEvent(
  sequence: number,
  o: EnvelopeOverrides & { line?: string } = {},
): SpoolEvent {
  const line = o.line ?? envelopeText(sequence, o);
  return {
    sequence,
    eventId: o.eventId ?? eventIdFor(sequence),
    line,
    lineBytes: Buffer.byteLength(line, 'utf8') + 1,
    chunk: '000001.jsonl',
    lineNumber: sequence,
  };
}

/**
 * A JSON envelope whose text is exactly `bytes` UTF-8 bytes: the padding lives in
 * `ext.schema`, so the line stays a single-line JSON object (a pad above the
 * schema's 128-character `maxLength` is only used where validation is a no-op —
 * the byte boundary is a transport property, checked before any schema work).
 */
export function envelopeOfBytes(sequence: number, bytes: number): string {
  const base = envelopeText(sequence);
  // `,"ext":{"schema":""}` — the suffix inserted before the envelope's closing brace.
  const extField = ',"ext":{"schema":""}';
  const padding = bytes - base.length - extField.length;
  if (padding < 0) {
    throw new Error(
      `cannot build a ${bytes}-byte envelope: ${base.length + extField.length} is the minimum`,
    );
  }
  return `${base.slice(0, -1)},"ext":{"schema":"${'x'.repeat(padding)}"}` + '}';
}

/** `validate` stand-in for the size tests: the line is not under test there. */
export const noopValidate = (): void => {};

/** One envelope line whose `ext.schema` carries `payload` verbatim (the schema landing spot for padding). */
export function paddedLine(sequence: number, payload: string): string {
  return `${envelopeText(sequence).slice(0, -1)},"ext":{"schema":${JSON.stringify(payload)}}` + '}';
}

/**
 * A payload gzip shrinks by roughly 4:1 — enough for the 1 KiB saving floor while staying
 * well under the server's 20:1 expansion cap. Deterministic: a repeated phrase plus the
 * fixed-PRNG bytes; no `Math.random`.
 */
export function moderatelyCompressibleText(): string {
  return `${'abcdefgh;'.repeat(10_000)}${pseudoRandomBytes(30_000).toString('base64')}`;
}

/** One envelope line plus a hostile extra member `name` (an `additional_properties` rejection). */
export function withExtraMember(sequence: number, name: string): string {
  const envelope = JSON.parse(envelopeText(sequence)) as Record<string, unknown>;
  return JSON.stringify({ ...envelope, [name]: 1 });
}

/**
 * An `AsyncIterable` over a hand-rolled iterator. Deliberately NOT an async
 * generator: an async generator that only yields plain values (no `await`, no
 * `yield*` of an async iterable) is exactly what `@typescript-eslint/require-await`
 * reports, and the CLI lint gate runs with `--max-warnings 0`.
 */
function asyncSourceOf(iterate: () => Iterator<SpoolEvent>): AsyncIterable<SpoolEvent> {
  return {
    [Symbol.asyncIterator]: (): AsyncIterator<SpoolEvent> => {
      const iterator = iterate();
      return {
        next: (): Promise<IteratorResult<SpoolEvent>> => Promise.resolve(iterator.next()),
      };
    },
  };
}

export function sourceOf(events: readonly SpoolEvent[]): AsyncIterable<SpoolEvent> {
  return asyncSourceOf(() => events[Symbol.iterator]());
}

/** A source that records how many events were actually pulled from it. */
export function countingSource(events: readonly SpoolEvent[]): {
  readonly pulled: () => number;
  readonly iterable: AsyncIterable<SpoolEvent>;
} {
  const state = { pulled: 0 };
  const iterable = asyncSourceOf(() => {
    const iterator = events[Symbol.iterator]();
    return {
      next: (): IteratorResult<SpoolEvent> => {
        const result = iterator.next();
        if (!result.done) {
          state.pulled += 1;
        }
        return result;
      },
    };
  });
  return { pulled: () => state.pulled, iterable };
}

export function options(overrides: Partial<PackBatchesOptions> = {}): PackBatchesOptions {
  return {
    runId: '0197c9a4-0f3a-7c2e-9b1d-4f8e2a6c5d10',
    producerId: 'pw-shard-0',
    validate: overrides.validate ?? noopValidate,
    // Spelled out rather than spread: `exactOptionalPropertyTypes` and the pack
    // defaults then never depend on how an absent override is modelled.
    maxEvents: overrides.maxEvents ?? maxBatchEvents,
    maxBytes: overrides.maxBytes ?? maxBatchBytes,
  };
}

/** The value `fn` threw, or `undefined` when it returned normally. */
export function thrownBy(fn: () => void): unknown {
  try {
    fn();
    return undefined;
  } catch (error) {
    return error;
  }
}

/** The bodies a batch would send, as text: `{"events":[<line>,…]}`. */
export function expectedBody(events: readonly SpoolEvent[]): string {
  return `{"events":[${events.map((event) => event.line).join(',')}]}`;
}

/** A deterministic PRNG (xorshift32) — no `Math.random` in a reproducible test. */
export function pseudoRandomBytes(length: number, seed = 0x9e3779b9): Buffer {
  let state = seed >>> 0;
  const bytes = Buffer.alloc(length);
  for (let index = 0; index < length; index += 1) {
    state ^= state << 13;
    state >>>= 0;
    state ^= state >>> 17;
    state ^= state << 5;
    state >>>= 0;
    bytes[index] = state & 0xff;
  }
  return bytes;
}
