// The fetch doubles the `gate --wait` split specs share: a 429 that names its own pause, a
// server that never answers, and a body that stalls after the headers so the deadline has to
// end the read rather than the request.
//
// This file is not a spec: the shared Vitest base collects `src/**/*.test.ts` only, and
// tsconfig.json excludes it from the emitting project. tsconfig.spec.json type-checks it.
import { jsonResponse } from './gate.kit.test.js';

export function rateLimited(retryAfterSeconds: string): Response {
  return jsonResponse(
    429,
    {
      error: {
        code: 'rate_limited',
        message: 'slow down',
        request_id: 'req-8',
        retryable: true,
      },
    },
    { 'retry-after': retryAfterSeconds },
  );
}

/** Rejects as soon as the request's abort signal fires: a server that never answers. */
export function hangUntilAborted(
  input: Request | string | URL,
  init: RequestInit | undefined,
  reject: (reason: Error) => void,
): void {
  const signal = init?.signal ?? (input instanceof Request ? input.signal : undefined);
  if (signal === undefined) {
    throw new Error('the request carries no abort signal');
  }
  const abort = (): void => {
    reject(new DOMException('the operation was aborted', 'AbortError'));
  };
  if (signal.aborted) {
    abort();
    return;
  }
  signal.addEventListener('abort', abort);
}

/**
 * A response body that delivers no chunk until the request's abort signal fires. The headers
 * are already in, so the read — not the request — is what the deadline has to end.
 */
export function stallingBody(signal: AbortSignal | undefined): ReadableStream<Uint8Array> {
  return new ReadableStream<Uint8Array>({
    pull: () =>
      new Promise<never>((_resolve, reject) => {
        const abort = (): void => {
          reject(new DOMException('the operation was aborted', 'AbortError'));
        };
        if (signal === undefined) {
          return;
        }
        if (signal.aborted) {
          abort();
          return;
        }
        signal.addEventListener('abort', abort, { once: true });
      }),
  });
}
