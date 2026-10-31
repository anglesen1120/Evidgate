// 04.2.3-T1.2 / ruling T42: the batcher's per-line envelope validator against the
// `fixtures/contracts/vectors/event-envelope.v1` corpus — the same files the
// schema set is checked against elsewhere. This file deliberately does NOT repeat
// parity.test.ts (the corpus through `ContractSchemaSet.validate`, verdict sidecar
// by sidecar) or schemaRegistry.test.ts (the loader). What it adds is the layer
// the batcher introduced:
//
//   * one spool line is ONE envelope, so a corpus vector shaped like a request
//     body (`{"events":[…]}`) is refused as a line, not accepted as a batch;
//   * a rejected line maps to the corpus's own `path`/`reason` rows — the send
//     path and the corpus must not disagree about what is invalid;
//   * the 1 MiB cap is a property of the travelling body, so the two corpus
//     vectors that exist for it (`acc-05-max-bytes-exact`, at the cap;
//     `rej-16-over-max-bytes`, one byte over) are schema-valid envelopes that
//     `packBatches` refuses on size — never envelope rejections;
//   * packed bodies are the exact corpus bytes, never a re-serialisation.
import { readdirSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

import {
  EnvelopeInvalidError,
  OversizedEventError,
  createEnvelopeValidator,
  maxBatchBytes,
  packBatches,
  type Batch,
} from './batcher.js';

import type { SpoolEvent } from './spool.js';

const repoRoot = join(dirname(fileURLToPath(import.meta.url)), '../../..');
const corpusDirectory = join(repoRoot, 'fixtures/contracts/vectors/event-envelope.v1');
const contractsDirectory = join(repoRoot, 'contracts');

/** `{"events":[` + `]}`: what a batch body spends besides its event lines. */
const bodyOverheadBytes = 13;

interface ExpectedError {
  readonly index: number | null;
  readonly path: string;
  readonly reason: string;
}

interface Verdict {
  readonly schema: string;
  readonly valid: boolean;
  readonly errors?: readonly ExpectedError[];
  readonly comment?: string;
}

interface Vector {
  readonly name: string;
  readonly text: string;
  readonly verdict: Verdict;
}

/** Every corpus vector with its verdict sidecar, ordinally sorted. */
function loadVectors(): Vector[] {
  return readdirSync(corpusDirectory)
    .filter((file) => file.endsWith('.json') && !file.endsWith('.verdict.json'))
    .sort()
    .map((file) => ({
      name: file,
      text: readFileSync(join(corpusDirectory, file), 'utf8'),
      verdict: JSON.parse(
        readFileSync(join(corpusDirectory, file.replace(/\.json$/, '.verdict.json')), 'utf8'),
      ) as Verdict,
    }));
}

/** What a corpus document is: a line, a request body, or not JSON at all. */
type Shape = 'invalid-json' | 'not-an-object' | 'batch' | 'event';

function shapeOf(text: string): Shape {
  let document: unknown;
  try {
    document = JSON.parse(text);
  } catch {
    return 'invalid-json';
  }
  if (typeof document !== 'object' || document === null || Array.isArray(document)) {
    return 'not-an-object';
  }
  return 'events' in document ? 'batch' : 'event';
}

/** The value a validator call threw, or `undefined` when it accepted the line. */
function thrownBy(fn: () => void): unknown {
  try {
    fn();
    return undefined;
  } catch (error) {
    return error;
  }
}

/** The `path|reason` rows of a rejection, sorted, without the batch `index`. */
function rejectionRows(error: unknown): string[] {
  expect(error).toBeInstanceOf(EnvelopeInvalidError);
  return (error as EnvelopeInvalidError).details
    .map((detail) => `${detail.path}|${detail.reason}`)
    .sort();
}

function spoolEventOf(text: string, sequence: number): SpoolEvent {
  let eventId = `00000000-0000-4000-8000-${sequence.toString(16).padStart(12, '0')}`;
  try {
    const document = JSON.parse(text) as Record<string, unknown>;
    if (typeof document['event_id'] === 'string') {
      eventId = document['event_id'];
    }
  } catch {
    // Not JSON: the id only feeds the fixture's identity fields.
  }
  return {
    sequence,
    eventId,
    line: text,
    lineBytes: Buffer.byteLength(text, 'utf8') + 1,
    chunk: '000001.jsonl',
    lineNumber: sequence,
  };
}

/**
 * Hand-rolled rather than an async generator: one that only yields plain values
 * is what `@typescript-eslint/require-await` reports under the CLI lint gate.
 */
function sourceOf(events: readonly SpoolEvent[]): AsyncIterable<SpoolEvent> {
  return {
    [Symbol.asyncIterator]: (): AsyncIterator<SpoolEvent> => {
      const iterator = events[Symbol.iterator]();
      return {
        next: (): Promise<IteratorResult<SpoolEvent>> => Promise.resolve(iterator.next()),
      };
    },
  };
}

/** Settles to the thrown value, so a test can assert on the error's fields. */
async function failure(promise: Promise<unknown>): Promise<unknown> {
  return promise.then(
    () => undefined,
    (error: unknown) => error,
  );
}

const packOptions = {
  runId: '0197c9a4-0f3a-7c2e-9b1d-4f8e2a6c5d10',
  producerId: 'pw-shard-0',
} as const;

const validate = createEnvelopeValidator(contractsDirectory);
const vectors = loadVectors();

/** `acc-05-max-bytes-exact` and `rej-16-over-max-bytes` are the cap pair. */
const atCap = vectors.find((vector) => vector.name.startsWith('acc-05'));
const overCap = vectors.find((vector) => vector.name.startsWith('rej-16'));

describe('envelope validator vs fixtures/contracts (T42)', () => {
  it('enumerates the corpus this file relies on', () => {
    expect(vectors.length).toBeGreaterThanOrEqual(15);

    const shapes = vectors.map((vector) => shapeOf(vector.text));
    expect(shapes).toContain('invalid-json');
    expect(shapes).toContain('batch');
    expect(shapes.filter((shape) => shape === 'event').length).toBeGreaterThanOrEqual(10);
    expect(vectors.every((vector) => vector.verdict.schema === 'event-envelope.v1')).toBe(true);
    expect(atCap).toBeDefined();
    expect(overCap).toBeDefined();
  });

  for (const vector of vectors) {
    it(`line verdict: ${vector.name}`, () => {
      const shape = shapeOf(vector.text);
      const expectedRows = (vector.verdict.errors ?? [])
        .map((error) => `${error.path}|${error.reason}`)
        .sort();

      if (shape === 'invalid-json') {
        // A truncated or unparseable line reports the registry's own token.
        expect(rejectionRows(thrownBy(() => validate(vector.text, 1)))).toEqual(['|invalid_json']);
        return;
      }

      if (shape === 'batch') {
        // A request body is not a spool line: its `events` member is an
        // unexpected member of the event object, so it can never be packed into
        // `{"events":[{"events":[…]}]}`.
        const rows = rejectionRows(thrownBy(() => validate(vector.text, 1)));
        expect(rows).toContain('/events|additional_properties');
        expect(vector.verdict.schema).toBe('event-envelope.v1');
        return;
      }

      if (shape === 'not-an-object') {
        // No such vector today; a scalar line still fails as a non-object event.
        expect(rejectionRows(thrownBy(() => validate(vector.text, 1)))).toEqual(['|type']);
        return;
      }

      // A single event line: the section the transport cap does not decide. A
      // body over the cap is shape-valid, and `packBatches` is what refuses it
      // (asserted below).
      if (expectedRows.length === 1 && expectedRows[0] === '|body_too_large') {
        expect(() => validate(vector.text, 1)).not.toThrow();
        return;
      }

      if (vector.verdict.valid) {
        expect(() => validate(vector.text, 1)).not.toThrow();
        return;
      }

      expect(expectedRows.length).toBeGreaterThan(0);
      expect(rejectionRows(thrownBy(() => validate(vector.text, 1)))).toEqual(expectedRows);
    });
  }

  it('names the sequence and event_id of a rejected corpus line, with no payload echo', () => {
    const vector = vectors.find((candidate) => candidate.name.startsWith('rej-05'));
    expect(vector).toBeDefined();

    const error = thrownBy(() => validate(vector!.text, 12)) as EnvelopeInvalidError;

    expect(error).toBeInstanceOf(EnvelopeInvalidError);
    expect(error.sequence).toBe(12);
    expect(error.eventId).toBe('not-a-uuid');
    expect(error.details).toEqual([{ path: '/event_id', reason: 'format' }]);
    // The event's `execution_key` and `artifact_ids` never reach the message.
    expect(error.message).not.toContain('9f86d081884c7d659a2feaa0c55ad015');
    expect(error.message).not.toContain('0197c9a4-0f3b-7d21-8a2c-5e9f3b7d6e21');
  });

  it('validates the corpus through the packaged schemas, not the corpus path', () => {
    const resolved = createEnvelopeValidator();
    const accepted = vectors.filter(
      (vector) => vector.verdict.valid && shapeOf(vector.text) === 'event',
    );
    expect(accepted.length).toBeGreaterThan(0);

    for (const vector of accepted) {
      expect(() => resolved(vector.text, 1)).not.toThrow();
    }
  });

  it('refuses the corpus cap pair on body size, naming the event', async () => {
    for (const vector of [atCap, overCap]) {
      expect(vector).toBeDefined();
      const event = spoolEventOf(vector!.text, 9);
      const error = await failure(
        packBatches(sourceOf([event]), { ...packOptions, validate }).next(),
      );

      expect(error).toBeInstanceOf(OversizedEventError);
      const oversized = error as OversizedEventError;
      expect(oversized.sequence).toBe(9);
      expect(oversized.eventId).toBe(event.eventId);
      expect(oversized.bytes).toBe(Buffer.byteLength(vector!.text, 'utf8') + bodyOverheadBytes);
      expect(oversized.bytes).toBeGreaterThan(maxBatchBytes);
    }
  });

  it('packs the accepted single-event vectors byte-for-byte', async () => {
    const packable = vectors.filter(
      (vector) =>
        vector.verdict.valid &&
        shapeOf(vector.text) === 'event' &&
        Buffer.byteLength(vector.text, 'utf8') + bodyOverheadBytes <= maxBatchBytes,
    );
    expect(packable.length).toBeGreaterThanOrEqual(2);

    const events = packable.map((vector, index) => spoolEventOf(vector.text, index + 1));
    const batches: Batch[] = [];
    for await (const batch of packBatches(sourceOf(events), { ...packOptions, validate })) {
      batches.push(batch);
    }

    expect(batches).toHaveLength(1);
    const batch = batches[0]!;
    expect(batch.events).toHaveLength(packable.length);
    expect(batch.firstSequence).toBe(1);
    expect(batch.lastSequence).toBe(packable.length);
    expect(batch.body.toString('utf8')).toBe(
      `{"events":[${packable.map((vector) => vector.text).join(',')}]}`,
    );
    expect(batch.body.byteLength).toBeLessThanOrEqual(maxBatchBytes);
  });
});
