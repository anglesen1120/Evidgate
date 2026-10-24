import { type ApplicationConfig, provideZonelessChangeDetection } from '@angular/core';

import { provideDevBackend } from './core/dev-fixtures/dev-backend';
import { provideApi } from './core/http/api-client';

// ADR-0008 settled both integration questions this file used to leave open: change detection is
// zoneless (00 §5.6 W3 — no zone.js dependency, polyfill or import in this workspace, and a spec
// asserts the global is absent), and every API call goes through `HttpClient` (shape A), so the
// SDK client, its interceptors and the XSRF wiring are provided here, once, for the app.
//
// 09.1.1-T12.1: `provideDevBackend()` is the seam the dev fixture backend hangs on, and it is
// registered AFTER `provideApi()` so it can replace the `HttpBackend` that call provides — the
// last provider of a token wins. In a production build this module is `dev-backend.ts` and the call
// adds nothing; the `fixtures` configuration swaps in `dev-backend.fixtures.ts`, which answers from
// `fixtures/web-api/` through the same `HttpClient`, interceptors and `ApiGateway` as the real API.
export const appConfig: ApplicationConfig = {
  providers: [provideZonelessChangeDetection(), provideApi(), provideDevBackend()],
};
