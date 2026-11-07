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
//
// Two entry points share that classification: `call` resolves the body, and `callWithResponse`
// also hands back the response's status and headers (09.7.1-T4: the policy list's `ETag` is the
// head revision the next publish quotes in `If-Match`, and a publish's `201`/`200` is the
// "appended" versus "already stored" answer). `call` is `callWithResponse` without them, so a
// caller sees one classification table, not two.

import { inject, Injectable } from '@angular/core';
import type { Client } from '@evidgate/sdk';

import { ApiRequestError } from './api-error';
import { API_CLIENT } from './api-client';
import { mapApiError, mapNetworkError, mapTimeoutError } from './error-mapper';
import { QuotaExceededRelay } from './quota-exceeded-relay';
import { UnauthenticatedRelay } from './unauthenticated-relay';

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
 * Per-call switches that do not change the classification. `reportQuota` is `false` for the
 * usage-summary read itself: its own refusal must not ask for the reload the refusal triggers,
 * or a failing read would keep asking for the next one.
 */
export interface CallOptions {
  readonly reportQuota?: boolean;
}

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
   * The session hook (09.1.1-T5.2): a `401` from ANY operation is reported here before the
   * `ApiRequestError` reaches the caller, so one registration in `SessionStore` covers every data
   * service. The relay holds no handler until the store registers one, which keeps the gateway
   * independent of the state layer.
   */
  private readonly unauthenticated = inject(UnauthenticatedRelay);

  /**
   * The quota hook: an operation refused as `429 quota_exceeded` is reported here before the
   * refusal reaches the caller, so `QuotaStore` refreshes the counters once for every write the
   * server turned away. Registered handlers hold no reference to the gateway.
   */
  private readonly quotaExceeded = inject(QuotaExceededRelay);

  /**
   * Runs one SDK operation and resolves its 2xx body.
   *
   * It is exactly {@link ApiGateway.callWithResponse} without the response: the body when the
   * call succeeded, and the same typed refusal, cancellation, deadline or transport failure
   * otherwise. A caller that also needs the status or a response header (`ETag` for the next
   * `If-Match`, or a `201` that means "appended" rather than "already stored") calls that method
   * directly; nothing else about the classification changes.
   */
  async call<TResult extends SdkResult>(
    operation: (client: Client) => Promise<TResult>,
    options: CallOptions = {},
  ): Promise<SdkSuccess<TResult>> {
    const { data } = await this.callWithResponse(operation, options);
    return data;
  }

  /**
   * The same run, plus the success response's status and headers.
   *
   * - an `AbortError` — rejected by the operation or handed back as `result.error` — is
   *   re-thrown unchanged, because the caller aborted it on purpose; never an `ApiRequestError`.
   *   This applies even when `result` also carries a response: an abort during the SDK's body
   *   read leaves a partial body, so the cancellation wins over the success check;
   * - a `TimeoutError` (the adapter's deadline, `http-client-fetch.ts`) with no response throws
   *   `ApiRequestError` from `mapTimeoutError`, i.e. `kind: 'timeout'`, `status: 0`, retryable
   *   only with the same `Idempotency-Key`, carrying the underlying error as its `cause`. A
   *   response whose body merely resembles one is a refusal and keeps its status mapping;
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
   *
   * A success answers the body together with the response's status and headers; the headers are
   * the only place an `ETag` exists, so a caller that stores one for a later `If-Match` reads it
   * here rather than from the body.
   */
  async callWithResponse<TResult extends SdkResult>(
    operation: (client: Client) => Promise<TResult>,
    options: CallOptions = {},
  ): Promise<{ data: SdkSuccess<TResult>; status: number; headers: Headers }> {
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

    const { response } = result;
    if (response === undefined) {
      // The adapter's deadline is a typed outcome only as a missing response: a server that
      // answers 4xx/5xx with a body that happens to carry `name: 'TimeoutError'` is a refusal,
      // so it keeps its status, code and request id through `mapApiError` below.
      if (isTimeoutError(result.error)) {
        throw new ApiRequestError(mapTimeoutError(), { cause: result.error });
      }
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
      const refusal = new ApiRequestError(
        mapApiError({ status: response.status, body: result.error, headers: response.headers }),
      );
      // 09.1.1-T5.2: a refusal that says the session is gone is reported before it is thrown, so the
      // store can offer a sign-in that returns to this route. Reported for every operation, because
      // any read can be the first one to notice an expired session.
      if (refusal.error.kind === 'unauthenticated') {
        this.unauthenticated.notify(null);
      }
      // A write refused as over quota is the moment the counters the badge shows are stale. The
      // summary read opts out (its own refusal must not ask for the reload it triggers), and a
      // `rate_limited` burst keeps its own code, so only a real quota refusal reports.
      if (
        options.reportQuota !== false &&
        refusal.error.status === 429 &&
        refusal.error.code === 'quota_exceeded'
      ) {
        this.quotaExceeded.notify();
      }
      throw refusal;
    }

    // The SDK's `data` is present on a 2xx; a no-content operation carries the SDK's empty JSON
    // object, which its declared `unknown` type accepts.
    return {
      data: result.data as SdkSuccess<TResult>,
      status: response.status,
      headers: response.headers,
    };
  }
}
