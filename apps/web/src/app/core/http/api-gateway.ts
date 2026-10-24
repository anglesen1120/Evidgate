// 09.1.1-T3.1 (00 §9.2, 00 §5.6 W5; ADR-0008 decision (a)): the one place where an SDK call
// becomes either its typed 2xx body or a thrown `ApiRequestError`.
//
// The generated SDK never throws on a non-2xx (it resolves `{ error, response }`), so a caller
// that forgets to inspect `error` would treat a 403 or a 404 as success. Every data service goes
// through this gateway instead, and the compiler carries the success type from the operation:
// `gateway.call((client) => listRuns({ client, path: { p } }))` is `Promise<RunPage>` without a
// hand-written wire type anywhere in `apps/web`.

import { inject, Injectable } from '@angular/core';
import type { Client } from '@evidgate/sdk';

import { ApiRequestError } from './api-error';
import { API_CLIENT } from './api-client';
import { mapApiError, mapNetworkError } from './error-mapper';

/**
 * The SDK's resolve-not-throw result (`RequestResult<…, false>`): a 2xx carries `data`, a refusal
 * carries `error`, and either may pair with the raw `request`/`response` (`response` is absent
 * when the request could not even be built). Only the members the gateway reads are named; the
 * payload types stay the SDK's.
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

@Injectable({ providedIn: 'root' })
export class ApiGateway {
  /** The configured SDK client; operations receive it as `{ client: gateway.client }`. */
  readonly client: Client = inject(API_CLIENT);

  /**
   * Runs one SDK operation and resolves its 2xx body.
   *
   * - a non-2xx result, or any result whose `error` is set, throws `ApiRequestError` built from
   *   `mapApiError` (status, `00 §9.2` body, response headers) — a 403/404/412/429 is never
   *   turned into a success value;
   * - a rejected promise (the adapter rethrows a transport failure as `TypeError`) throws
   *   `ApiRequestError` from `mapNetworkError`, i.e. `kind: 'network'`, `status: 0`;
   * - an `AbortError` is re-thrown unchanged, because the caller aborted it on purpose;
   * - a 2xx without a body (a `202`/`204` operation) resolves `undefined`.
   */
  async call<TResult extends SdkResult>(
    operation: (client: Client) => Promise<TResult>,
  ): Promise<SdkSuccess<TResult>> {
    let result: TResult;
    try {
      result = await operation(this.client);
    } catch (cause) {
      // A DOM `AbortError` is cancellation, not a failed call: it is re-thrown as it arrived.
      if (cause instanceof Error && cause.name === 'AbortError') {
        throw cause;
      }
      throw new ApiRequestError(mapNetworkError(cause));
    }

    const response = result.response;
    const isSuccess =
      result.error === undefined &&
      response !== undefined &&
      response.status >= 200 &&
      response.status < 300;
    if (!isSuccess) {
      throw new ApiRequestError(
        mapApiError({
          status: response?.status ?? 0,
          body: result.error,
          headers: response?.headers ?? null,
        }),
      );
    }

    // The SDK's `data` is present on a 2xx except for a no-content operation, whose body is
    // absent by contract and whose declared type is `unknown` — `undefined` is valid there.
    return result.data as SdkSuccess<TResult>;
  }
}
