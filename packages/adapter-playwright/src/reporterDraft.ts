// One envelope before the spool writer assigns its per-producer `sequence`, composed here so
// every emitter builds the same shape. The producer id is only known once `onBegin` has
// resolved the shard, so it is passed at append time rather than captured by the draft.
import {
  buildEnvelope,
  type EventType,
  type ExtPayload,
  type UnsignedEnvelope,
} from './envelope.js';

export interface EnvelopeDraft {
  type: EventType;
  attempt_no: number;
  execution_key: string;
  ext: ExtPayload;
  status?: string;
  duration_ms?: number;
  /** The artifacts a `test.finished` references; absent unless non-empty. */
  artifact_ids?: string[];
}

/** Composes a draft into an envelope under the run's producer id. */
export type DraftBuilder = (input: EnvelopeDraft) => UnsignedEnvelope;

export function draftEnvelope(
  producerId: string | undefined,
  input: EnvelopeDraft,
): UnsignedEnvelope {
  if (producerId === undefined) {
    throw new Error('@evidgate/playwright-reporter: envelope built before the producer id');
  }
  return buildEnvelope({ producer_id: producerId, ...input });
}
