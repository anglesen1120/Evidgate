import {
  type ApplicationConfig,
  inject,
  provideAppInitializer,
  provideZonelessChangeDetection,
} from '@angular/core';
import { provideRouter } from '@angular/router';

import { routes } from './app.routes';
import { provideDevBackend } from './core/dev-fixtures/dev-backend';
import { provideApi } from './core/http/api-client';
import { ThemeService } from './shell/layout/theme.service';

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
// 09.1.1-T4.1: `provideRouter(routes)` is the addition of the shell story. The route table
// itself is `app.routes.ts`; the guards it names read the session through `SessionStore`, which is
// load-bearing here and not in `provideRouter`'s options: no `withRouterConfig`, no
// `withInMemoryScrolling`, because the shell owns focus and scroll restoration per navigation
// (`shell/layout/shell.component.ts`) and a second mechanism would fight it.
//
// 09.1.1-T4 review L3: `ThemeService` applies the stored `data-theme` in its constructor, and it was
// constructed only when `ShellComponent` was, so the routes outside the shell (`/login`,
// `/unavailable`) ignored a dark-mode choice. The initializer constructs the singleton before the
// first route renders; it changes nothing about persistence, and `ShellComponent` keeps injecting
// the same instance.
export const appConfig: ApplicationConfig = {
  providers: [
    provideZonelessChangeDetection(),
    provideApi(),
    provideDevBackend(),
    provideRouter(routes),
    provideAppInitializer(() => {
      inject(ThemeService);
    }),
  ],
};
