// 09.1.1-T3.1 (00 §9.2, 00 §5.6 W5; ADR-0008 decision (a)): the one place where an SDK call
// becomes either its typed 2xx body or a thrown `ApiRequestError`.
//
// The generated SDK never throws on a non-2xx and never lets a rejected `fetch` escape: it
// resolves `{ error, request, response }`, with `response` absent when no HTTP response was
// received at all. A caller that forgets to inspect `error` / the absent `response` would treat
// a 403, a 404 or a dropped connection as success. Every data service goes through this gateway
// instead, and the compiler carries the success type from the operation:
// `gateway.call((client) => listRuns({ client, path: { p } }))` is `Promise<RunPage>` without a
// hand-written wire type anywhere in `apps/web`.

import { inject, Injectable } from '@angular/core';
import type { Client } from '@evidgate/sdk';

import { ApiRequestError } from './api-error';
import { API_CLIENT } from './api-client';
import { mapApiError, mapNetworkError, mapTimeoutError } from './error-mapper';

/**
 * The SDK's resolve-not-throw result (`RequestResult<…, false>`): a 2xx carries `data`, a refusal
 * carries `error`, and either may pair with the raw `request`/`response`. `response` is absent
 * when no HTTP response was ever received — the request could not be built, `fetch` rejected, or
 * the caller aborted — so an absent `response` with an `error` is exactly the transport path.
 * Only the members the gateway reads are named; the payload types stay the SDK's.
 */
export type SdkResult = {
  readonly data?: unknown;
  readonly error?: unknown;
  readonly request?: Request;
  readonly response?: Response;
};

/**
 * The 2xx body of one operation: the SDK's own `data` member with `undefined` removed, so a
 * caller of a body-bearing operation never has to narrow a `| undefined` the wire contract does
 * not allow. An operation whose documented body is `unknown` (a `202` acknowledgement) keeps
 * `unknown`.
 */
export type SdkSuccess<TResult extends SdkResult> = Exclude<TResult['data'], undefined>;

/**
 * True for a cancellation, on either path one can arrive by: the adapter rejects with a
 * `DOMException` named `AbortError`, and the SDK hands that same value back as `result.error`.
 * Duck-typed rather than `instanceof DOMException`, because the class is per-realm and the value
 * may be an `Error`, a `DOMException` or a foreign object. The reason is never read, so every
 * abort reason — including a plain string — is still a cancellation.
 */
function isAbortError(value: unknown): boolean {
  return (
    typeof value === 'object' && value !== null && 'name' in value && value.name === 'AbortError'
  );
}

/**
 * True for the adapter's deadline rejection (`http-client-fetch.ts`), on either path it can
 * arrive by: the operation rejects with it, or the SDK hands it back as `result.error` when the
 * deadline fires before any response. Duck-typed for the same reason as {@link isAbortError}.
 * It is never a server outcome, so it becomes the typed `timeout` failure instead of a status
 * mapping.
 */
function isTimeoutError(value: unknown): boolean {
  return (
    typeof value === 'object' && value !== null && 'name' in value && value.name === 'TimeoutError'
  );
}

/**
 * The value a re-throw hands to the caller, typed as `Error` so the cancellation and the
 * programming-error path can `throw` it under `@typescript-eslint/only-throw-error`. The runtime
 * value is untouched: an abort must reach the caller as the exact object it arrived as (a caller
 * compares it by identity or with `instanceof`), and an unexpected error must stay the value the
 * operation produced. Only the static type is narrowed - nothing here claims the value is an
 * `Error` beyond what the throw itself needs.
 */
function asThrown(value: unknown): Error {
  return value as Error;
}

@Injectable({ providedIn: 'root' })
export class ApiGateway {
  /** The configured SDK client; operations receive it as `{ client: gateway.client }`. */
  readonly client: Client = inject(API_CLIENT);

  /**
   * Runs one SDK operation and resolves its 2xx body.
   *
   * - an `AbortError` — rejected by the operation or handed back as `result.error` — is
   *   re-thrown unchanged, because the caller aborted it on purpose; never an `ApiRequestError`.
   *   This applies even when `result` also carries a response: an abort during the SDK's body
   *   read leaves a partial body, so the cancellation wins over the success check;
   * - a `TimeoutError` (the adapter's deadline, `http-client-fetch.ts`) throws `ApiRequestError`
   *   from `mapTimeoutError`, i.e. `kind: 'timeout'`, `status: 0`, retryable only with the same
   *   `Idempotency-Key`, carrying the underlying error as its `cause`;
   * - a non-2xx result, or any result whose `error` is set, throws `ApiRequestError` built from
   *   `mapApiError` (status, `00 §9.2` body, response headers) — a 403/404/412/429 is never
   *   turned into a success value;
   * - a result with no `response` (the SDK caught what `fetch` rejected with) throws
   *   `ApiRequestError` from `mapNetworkError`, i.e. `kind: 'network'`, `status: 0`, carrying the
   *   underlying error as its `cause`, but only when that error is a `TypeError` (the shape
   *   `fetch` and the adapter reject a transport failure with). Any other value that arrives as
   *   `result.error` without a response — a plain `Error` thrown by `newUuid`, say — is a
   *   programming error and is re-thrown unchanged;
   * - a rejected operation with a `TypeError` throws the same `network` error; any other
   *   non-abort, non-timeout rejection is a programming error and is re-thrown unchanged;
   * - a 2xx without a body (a `202`/`204` operation) resolves the SDK's empty-data value, `{}`
   *   for a JSON response — never `undefined`.
   */
  async call<TResult extends SdkResult>(
    operation: (client: Client) => Promise<TResult>,
  ): Promise<SdkSuccess<TResult>> {
    let result: TResult;
    try {
      result = await operation(this.client);
    } catch (cause) {
      // A DOM `AbortError` is cancellation, not a failed call: it is re-thrown as it arrived.
      if (isAbortError(cause)) {
        throw asThrown(cause);
      }
      // The adapter's deadline is a typed outcome, not a server or network decision: the caller
      // learns that no answer arrived in time and may retry the same intent.
      if (isTimeoutError(cause)) {
        throw new ApiRequestError(mapTimeoutError(), { cause });
      }
      // `fetch` rejects a request that never reached the server with a `TypeError`, and the
      // adapter keeps that shape. Anything else that escapes an operation is a programming
      // error: it is re-thrown unchanged rather than reported as a server or network outcome.
      if (cause instanceof TypeError) {
        throw new ApiRequestError(mapNetworkError(), { cause });
      }
      throw asThrown(cause);
    }

    // A cancellation wins over every other outcome, the success check included: an abort that
    // fires while the SDK reads the body leaves a response with a partial body, and reporting
    // that as a success (or as `unexpected`) would hide the cancellation.
    if (isAbortError(result.error)) {
      throw asThrown(result.error);
    }
    if (isTimeoutError(result.error)) {
      throw new ApiRequestError(mapTimeoutError(), { cause: result.error });
    }

    const { response } = result;
    if (response === undefined) {
      // No HTTP response: the SDK caught a rejection (a transport failure) and resolved it as
      // `error`. An absent `error` cannot be classified either, so it takes the same transport
      // failure rather than being thrown as `undefined`.
      if (result.error === undefined || result.error instanceof TypeError) {
        throw new ApiRequestError(mapNetworkError(), { cause: result.error });
      }
      // Anything else is not a transport failure the UI has a state for: re-throw it unchanged
      // instead of mislabelling a programming error as a retryable `network` failure.
      throw asThrown(result.error);
    }

    const isSuccess = result.error === undefined && response.status >= 200 && response.status < 300;
    if (!isSuccess) {
      throw new ApiRequestError(
        mapApiError({ status: response.status, body: result.error, headers: response.headers }),
      );
    }

    // The SDK's `data` is present on a 2xx; a no-content operation carries the SDK's empty JSON
    // object, which its declared `unknown` type accepts.
    return result.data as SdkSuccess<TResult>;
  }
}
