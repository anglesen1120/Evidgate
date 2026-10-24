// 09.1.1-T3.2 (00 §5.6 W5, 00 §9.1; ADR-0008 decision (a) and rules 1-3): the single SDK client
// this app calls, and the providers that make it behave like every other Angular request.
//
// Shape A of the ADR: one `@hey-api/client-fetch` client whose `fetch` is the Angular
// `HttpClient` adapter, so the functional interceptors (X-Request-Id/traceparent,
// Idempotency-Key) and Angular's own XSRF interceptor see every SDK call. `baseUrl` is the
// relative `/v1` — the API shares the page origin (rule 1), which is also what T38's `__Host-`
// session cookie requires. The SDK's own header hooks are deliberately not registered: two
// implementations of the same header duty would drift.

import {
  HttpClient,
  provideHttpClient,
  withInterceptors,
  withXsrfConfiguration,
} from '@angular/common/http';
import {
  inject,
  InjectionToken,
  makeEnvironmentProviders,
  type EnvironmentProviders,
} from '@angular/core';
import { createClient, createConfig, type Client } from '@evidgate/sdk';

import { API_BASE_PATH } from './api-origin';
import { createHttpClientFetch } from './http-client-fetch';
import { idempotencyInterceptor } from './idempotency.interceptor';
import { requestIdInterceptor } from './request-id.interceptor';

/**
 * XSRF double-submit names (T38): the API sets the readable `XSRF-TOKEN` cookie and keeps the
 * session cookie `HttpOnly`; Angular's interceptor copies the cookie into the `X-XSRF-TOKEN`
 * header on same-origin mutations and skips GET/HEAD by itself. The names are 00 §9.1's, not
 * Angular's defaults assumed: pinning them makes a server-side rename a review fact instead of a
 * 403 `csrf_failed` at runtime.
 */
export const XSRF_COOKIE_NAME = 'XSRF-TOKEN';
export const XSRF_HEADER_NAME = 'X-XSRF-TOKEN';

/**
 * The configured SDK client. `provideApi()` is its only provider, so an app that injects it
 * without those providers fails on this token instead of on a half-wired `HttpClient`.
 */
export const API_CLIENT = new InjectionToken<Client>('evidgate.api-client');

/**
 * `HttpClient` with the API interceptors and XSRF wiring, plus the SDK `API_CLIENT` built on the
 * Angular adapter. Angular registers its own XSRF interceptor first: `provideHttpClient()` pushes
 * `xsrfInterceptorFn` into `HTTP_INTERCEPTOR_FNS` before the `withInterceptors()` features and the
 * chain runs in provider order, so it sees the request ahead of request id and idempotency. The
 * order changes nothing on the wire — `xsrfInterceptorFn` returns early for GET/HEAD and for an
 * origin that is not the page's, so only same-origin `/v1` mutations gain `X-XSRF-TOKEN`. A spec
 * adds `provideHttpClientTesting()` after this call to reach the real stack.
 *
 * `baseUrl` stays `/v1` in the application (the API shares the page origin, rule 1) and exists as
 * a parameter only because the SDK builds `new Request(baseUrl + path)`: a browser resolves the
 * relative URL against the document base, while Node's `Request` — the one `ng test` runs on —
 * refuses a relative URL, so specs pass `location.origin + API_BASE_PATH`. Same origin, same
 * interceptors, same XSRF: nothing about the request changes but its spelling.
 */
export function provideApi(baseUrl: string = API_BASE_PATH): EnvironmentProviders {
  return makeEnvironmentProviders([
    provideHttpClient(
      withInterceptors([requestIdInterceptor, idempotencyInterceptor]),
      withXsrfConfiguration({ cookieName: XSRF_COOKIE_NAME, headerName: XSRF_HEADER_NAME }),
    ),
    {
      provide: API_CLIENT,
      useFactory: (): Client =>
        createClient(
          createConfig({
            baseUrl,
            credentials: 'same-origin',
            fetch: createHttpClientFetch(inject(HttpClient)),
          }),
        ),
    },
  ]);
}
