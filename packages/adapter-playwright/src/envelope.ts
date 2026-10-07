// 04.3.1-T1.1/T1.2: the 00 §9.4 event envelope v1 as this reporter emits it. The
// envelope is closed (`additionalProperties: false` in contracts/schemas/event-envelope.v1.json),
// so only the members the schema declares are ever set; `sequence` is assigned by the
// spool writer (the per-producer authority), which is why the pre-sequence shape is a
// separate type.
import { uuidv7 } from './uuidv7.js';

/** `schema_version` is a const 1 in the contract. */
export const EVENT_SCHEMA_VERSION = 1 as const;

/**
 * `ext.schema` for every envelope this reporter emits: `<adapter-name>/<protocol-major>`
 * per 00 §9.4 (`"ext": { "schema": "adapter-name/1" }`) and the task file §3. The
 * capability carrier is distinguished by `ext.capability`, not by a different schema.
 */
export const EXT_SCHEMA = 'playwright/1';

/** The event types 00 §9.4 defines; the reporter never invents another (AC2). */
export type EventType =
  'test.started' | 'test.step' | 'test.finished' | 'run.log' | 'producer.heartbeat';

/** `ext` is an open object that requires only `schema` (contracts/schemas/event-envelope.v1.json). */
export interface ExtPayload {
  schema: string;
  [key: string]: unknown;
}

/** An envelope before the spool writer assigns its per-producer `sequence`. */
export interface UnsignedEnvelope {
  schema_version: typeof EVENT_SCHEMA_VERSION;
  event_id: string;
  producer_id: string;
  execution_key: string;
  attempt_no: number;
  type: EventType;
  occurred_at: string;
  status?: string;
  duration_ms?: number;
  ext: ExtPayload;
}

/** A complete 00 §9.4 envelope line. */
export interface EventEnvelope extends UnsignedEnvelope {
  sequence: number;
}

/** Everything the reporter supplies for one envelope; `event_id`/`occurred_at` default. */
export interface EnvelopeInput {
  producer_id: string;
  execution_key: string;
  attempt_no: number;
  type: EventType;
  ext: ExtPayload;
  status?: string;
  duration_ms?: number;
  event_id?: string;
  occurred_at?: string;
}

/**
 * Composes one envelope. Optional members are only set when supplied, so the emitted
 * object never carries an `undefined` member (the schema is closed).
 */
export function buildEnvelope(input: EnvelopeInput): UnsignedEnvelope {
  const envelope: UnsignedEnvelope = {
    schema_version: EVENT_SCHEMA_VERSION,
    event_id: input.event_id ?? uuidv7(),
    producer_id: input.producer_id,
    execution_key: input.execution_key,
    attempt_no: input.attempt_no,
    type: input.type,
    occurred_at: input.occurred_at ?? new Date().toISOString(),
    ext: input.ext,
  };
  if (input.status !== undefined) envelope.status = input.status;
  if (input.duration_ms !== undefined) envelope.duration_ms = input.duration_ms;
  return envelope;
}
