// 01.3.2-T1.4: typed header-injection hooks for the generated client.
//
// 00 §5.6 W5 gives the web interceptors three headers to attach: `X-Request-Id` on every
// request, `X-XSRF-TOKEN` on cookie-authenticated mutations, and `Idempotency-Key` on
// mutations that create resources or side effects (00 §9.1). The generated client's
// request path calls registered `client.interceptors.request` fns before fetch; this
// module defines the hook surface that module plugs into.

import type { ResolvedRequestOptions } from '../gen/client/index.js';

/** What a header hook is given: the request being built and its resolved options. */
export interface SdkHeaderHookContext {
  /** The fetch `Request` the generated client is about to send. */
  request: Request;
  /** The resolved call options (path params, query, headers, security, url). */
  options: ResolvedRequestOptions;
}

/**
 * Header value provider. Return the header value, `null`/`undefined` to skip the header
 * for this request (e.g. no XSRF token available for a bearer-only call).
 */
export type SdkHeaderHook = (
  context: SdkHeaderHookContext,
) => string | null | undefined | Promise<string | null | undefined>;

/**
 * The injectable header hooks (00 §5.6 W5). All members optional — a consumer provides
 * only the headers it can source:
 *
 * - `xRequestId` → `X-Request-Id`; every response echoes it in `error.request_id`
 *   (00 §9.1). Web: per-request UUID; CLI: invocation id.
 * - `xsrfToken` → `X-XSRF-TOKEN`; required on cookie-authenticated mutations or the edge
 *   answers 403 `csrf_failed` (00 §9.1). Bearer-only clients leave it unset.
 * - `idempotencyKey` → `Idempotency-Key`; required on mutations that create resources or
 *   side effects (00 §9.1). Per-call values passed in `options.headers` always win.
 */
export interface SdkHeaderHooks {
  xRequestId?: SdkHeaderHook;
  xsrfToken?: SdkHeaderHook;
  idempotencyKey?: SdkHeaderHook;
}

// Fixed evaluation order: hooks are deterministic — same request, same headers.
const HOOK_HEADERS = [
  { header: 'X-Request-Id', hook: 'xRequestId' },
  { header: 'X-XSRF-TOKEN', hook: 'xsrfToken' },
  { header: 'Idempotency-Key', hook: 'idempotencyKey' },
] as const;

/**
 * Builds a request interceptor the generated client invokes for every call. Only headers
 * whose hook resolves a non-empty value are set, and a header the caller already set on
 * the request (via `options.headers`) is never overwritten — the hook only fills gaps.
 *
 * ```ts
 * client.interceptors.request.use(
 *   createHeaderHookInterceptor({ xRequestId: () => crypto.randomUUID() }),
 * );
 * ```
 */
export function createHeaderHookInterceptor(
  hooks: SdkHeaderHooks,
): (request: Request, options: ResolvedRequestOptions) => Promise<Request> {
  return async (request, options) => {
    const context: SdkHeaderHookContext = { request, options };
    let headers: Headers | undefined;
    for (const { header, hook } of HOOK_HEADERS) {
      const fn = hooks[hook];
      if (fn === undefined || request.headers.has(header)) {
        continue;
      }
      const value = await fn(context);
      if (value === null || value === undefined) {
        continue;
      }
      (headers ??= new Headers(request.headers)).set(header, value);
    }
    return headers === undefined ? request : new Request(request, { headers });
  };
}
