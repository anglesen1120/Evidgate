// Every cap, ratio and key tag the batching half of the send path counts with. They live in
// one module so the packer, the validator and the error messages cannot drift apart: a body
// is packed against the same numbers the contract and the server use.

/** Contract cap on the number of events in one batch. */
export const maxBatchEvents = 500;
/** Contract cap on the batch body before compression: 1 MiB (event-envelope.v1 `x-max-bytes`). */
export const maxBatchBytes = 1_048_576;

/** The batch body opener: `{"events":[` — 11 UTF-8 bytes. */
export const batchOpen = '{"events":[';
/** The batch body closer: `]}` — 2 UTF-8 bytes. */
export const batchClose = ']}';
/** One byte between two concatenated lines. */
export const separatorBytes = 1;
/** Bytes a batch body spends on everything that is not an event line. */
export const overheadBytes =
  Buffer.byteLength(batchOpen, 'utf8') + Buffer.byteLength(batchClose, 'utf8');

/** Gzip must at least reach this fraction of the raw body (a 10 % saving). */
export const gzipMinRatio = 0.9;
/** …and save more than this many bytes, or the `Content-Encoding` hop is not worth it. */
export const gzipMinSavingBytes = 1024;
/**
 * The lowest `EVIDGATE__INGESTION__MAXEXPANSIONRATIO` this client can count on. The
 * server refuses a `gzip` body once its decoded size passes `ratio × compressed.Length`
 * (`IngestionLimits.MaxExpansionRatio`, default 20; `EventBodyDecoder.cs` `next > ratioCap`),
 * as a non-retryable 413 `batch_too_large`. A batch that compresses better than that cap
 * must therefore travel raw, which it always can: the raw body is at most 1 MiB.
 */
export const gzipMinServerExpansionRatio = 20;
/** One ratio point of margin, so the decoded size stays *under* the server's comparison. */
export const gzipExpansionHeadroom = 1;

/** Version tag of the idempotency-key pre-image; changing the recipe changes the tag. */
export const batchKeyVersion = 'evidgate.batch-key.v1';
/** Every derived key is `batch:` + 64 hex characters — 70, well inside the 128-char route limit. */
export const batchKeyPrefix = 'batch:';

/** The contract schema the validator compiles (`contracts/schemas/event-envelope.v1.json`). */
export const envelopeSchemaKey = 'event-envelope.v1';
/** The batch member an event line must not carry: a line IS one envelope (00 §9.4). */
export const eventsMember = 'events';
/** Prefix the wrapped (single-item batch) validation puts in front of item error paths. */
export const wrappedItemPrefix = '/events/0';

/** At most this many `details[]` rows are echoed in the message; the rest is counted. */
export const maxSummaryRows = 5;
/**
 * Longest `details[].path` echoed in the message, in Unicode code points. Ajv paths
 * carry member names straight from the spool line, so a hostile 1 MiB member name (or
 * a secret-shaped one) must not flood stderr: the same 200-code-point cap
 * `serverError.ts` applies to server-supplied text (SEC4 analogue).
 */
export const maxEchoedPathLength = 200;

/** The envelope member naming the producer a line belongs to (required by the schema). */
export const producerIdMember = 'producer_id';
/** The member that ties a line to one run; the schema does not require it (yet). */
export const runIdMember = 'run_id';

/** Longest `event_id` echoed in an error message (the schema's `maxLength`). */
export const maxEchoedEventIdLength = 128;
/** Printable ASCII only: an id that is not one stays out of the message and the log. */
export const printableAsciiPattern = /^[\x20-\x7e]+$/;
