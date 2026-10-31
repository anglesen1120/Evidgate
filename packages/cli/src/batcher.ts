// The batching half of the send path (00 §9.2/§9.4, contracts/schemas/event-envelope.v1.json):
// spool lines become `{"events":[...]}` bodies with a derived `Idempotency-Key`. This module is
// only the surface — the caps live in `batchLimits.ts`, the rejections in `batchErrors.ts`, the
// per-line verdict in `batchEnvelope.ts` and the packing itself in `batchPack.ts`.
export { maxBatchBytes, maxBatchEvents } from './batchLimits.js';
export { EnvelopeInvalidError, OversizedEventError } from './batchErrors.js';
export { createEnvelopeValidator } from './batchEnvelope.js';
export { deriveBatchIdempotencyKey, packBatches } from './batchPack.js';

export type { EnvelopeDetail } from './batchErrors.js';
export type { Batch, PackBatchesOptions } from './batchPack.js';
