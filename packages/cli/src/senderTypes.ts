// The shapes the send path passes around: the injected effects, what a send needs, what one
// delivered batch contributed, and one dispatched job. They live apart from the orchestrator
// so the retry/attempt/ack modules can type against them without importing it back.
import type { ResolvedConfig } from './config.js';

/** The effects the send needs, all injected so a spec is deterministic (no real I/O). */
export interface SendDeps {
  readonly fetch: typeof fetch;
  /** Virtual sleep; a spec advances its clock here instead of waiting. */
  readonly sleep: (milliseconds: number, signal?: AbortSignal) => Promise<void>;
  /** Clock in milliseconds; it drives the one deadline (a spec advances it in `sleep`). */
  readonly now: () => number;
  /** Uniform sample in `[0, 1]` for full jitter; a spec fixes it to 0 or 1. */
  readonly random: () => number;
}

/** Everything a send needs besides the batches and the injected effects. */
export interface SendOptions {
  /** The resolved `EVIDGATE_*` configuration (`createApiClient` reads `apiUrl`). */
  readonly config: ResolvedConfig;
  /** The CI producer token; registered for redaction by `createApiClient`. */
  readonly token: string;
  /** The run whose producer slot receives the batches. */
  readonly runId: string;
  /** The one deadline for the whole send, in milliseconds. */
  readonly deadlineMs: number;
  /** Lower the concurrency for a spec; never raises it above `maxInFlight`. */
  readonly maxInFlight?: number;
}

/** What one delivered batch contributed (the `duplicate` count is delivered, too). */
export interface BatchAckSummary {
  readonly accepted: number;
  readonly duplicates: number;
}

export interface SendFailedErrorOptions {
  /**
   * The server's `error.code` verbatim, or `deadline_exceeded`/`transport_failed`/
   * `ack_mismatch` when no server code applies.
   */
  readonly code: string;
  /** The server's `request_id` verbatim, when the answer carried one. */
  readonly requestId?: string;
  /** The HTTP status, when an answer was received (absent for a transport failure). */
  readonly status?: number;
  readonly firstSequence: number;
  readonly lastSequence: number;
}

/** One dispatched batch; `settled` lets the dispatch loop drop it without a `finally`. */
export interface Job {
  settled: boolean;
  promise: Promise<void>;
}
