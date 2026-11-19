import type { ApiError } from '../../../core/http/api-error';
import { createIntent } from '../../../core/http/intent';

/** One mutation intent: its key is reused only while the same ids, body and batch ETag return. */
interface KeyedIntent {
  readonly key: string;
  readonly signature: string;
}

export interface PendingKeys {
  readonly apply: (signature: string) => string;
  readonly dropApply: () => void;
  readonly queue: (signature: string) => string;
  readonly dropQueue: () => void;
  readonly discard: (id: string) => string;
  readonly dropDiscard: (id: string) => void;
  readonly reset: () => void;
}

export interface PendingRun {
  /** Starts a read, aborting the previous read: reads replace each other. */
  readonly startRead: () => AbortSignal;
  readonly ownsRead: (signal: AbortSignal) => boolean;
  /** Starts a per-call request that no sibling call aborts; `release` forgets it once settled. */
  readonly call: () => AbortSignal;
  readonly release: (signal: AbortSignal) => void;
  readonly stop: () => void;
}

export interface PendingBookkeeping {
  readonly keys: PendingKeys;
  readonly run: PendingRun;
}

export function signatureOf(
  kind: string,
  body: string,
  etag: string,
  ids: readonly string[],
): string {
  return `${kind}|${body}|${etag}|${[...ids].sort().join(',')}`;
}

export function unexpectedFailure(code: string, message: string): ApiError {
  return {
    kind: 'unexpected',
    status: 0,
    code,
    message,
    requestId: null,
    retryable: false,
    retryAfterSeconds: null,
    details: [],
  };
}

function keyedIntent(kind: string, signature: string, current: KeyedIntent | null): KeyedIntent {
  if (current !== null && current.signature === signature) return current;
  return { key: createIntent().idempotencyKey, signature };
}

/**
 * A DOM `AbortError`: our own cancellation, never a server or network outcome. A cancelled
 * mutation has an unknown delivery, so its intent key is kept and the caller retries it.
 */
export function isCancellation(value: unknown): boolean {
  return value instanceof Error && value.name === 'AbortError';
}

/**
 * The store's request bookkeeping.
 *
 * A read is replaced by the next read (`startRead` aborts the previous one), which is what keeps
 * a queue reload from racing its own earlier page. Everything else — a queue submission, an
 * apply, a discard, a checklist-facts page — is a per-call signal (`call`) that only `stop`
 * aborts, so loading a preview fact can never cancel a mutation that may already have been
 * committed: cancelling there would drop the intent key and the retry would mint a second one.
 */
export function createPendingBookkeeping(): PendingBookkeeping {
  let applyIntent: KeyedIntent | null = null;
  let queueIntent: KeyedIntent | null = null;
  const discardKeys = new Map<string, string>();
  let readController: AbortController | null = null;
  const calls = new Map<AbortSignal, AbortController>();
  const run: PendingRun = {
    startRead: () => {
      readController?.abort();
      readController = new AbortController();
      return readController.signal;
    },
    ownsRead: (signal) =>
      readController !== null && readController.signal === signal && !signal.aborted,
    call: () => {
      const controller = new AbortController();
      calls.set(controller.signal, controller);
      return controller.signal;
    },
    release: (signal) => {
      calls.delete(signal);
    },
    stop: () => {
      readController?.abort();
      readController = null;
      for (const controller of calls.values()) controller.abort();
      calls.clear();
    },
  };
  return {
    run,
    keys: {
      apply: (signature) => {
        applyIntent = keyedIntent('apply', signature, applyIntent);
        return applyIntent.key;
      },
      dropApply: () => {
        applyIntent = null;
      },
      queue: (signature) => {
        queueIntent = keyedIntent('queue', signature, queueIntent);
        return queueIntent.key;
      },
      dropQueue: () => {
        queueIntent = null;
      },
      discard: (id) => {
        const existing = discardKeys.get(id);
        if (existing !== undefined) return existing;
        const key = createIntent().idempotencyKey;
        discardKeys.set(id, key);
        return key;
      },
      dropDiscard: (id) => {
        discardKeys.delete(id);
      },
      reset: () => {
        applyIntent = null;
        queueIntent = null;
        discardKeys.clear();
      },
    },
  };
}
