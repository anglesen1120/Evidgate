import { type ApplicationConfig, provideZonelessChangeDetection } from '@angular/core';

import { provideApi } from './core/http/api-client';

// ADR-0008 settled both integration questions this file used to leave open: change detection is
// zoneless (00 §5.6 W3 — no zone.js dependency, polyfill or import in this workspace, and a spec
// asserts the global is absent), and every API call goes through `HttpClient` (shape A), so the
// SDK client, its interceptors and the XSRF wiring are provided here, once, for the app.
export const appConfig: ApplicationConfig = {
  providers: [provideZonelessChangeDetection(), provideApi()],
};
