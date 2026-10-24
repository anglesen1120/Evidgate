import { type ApplicationConfig, provideZonelessChangeDetection } from '@angular/core';

// 09.1.1-T2 ADR pending: zoneless change detection is 00 §5.6 W3, which still carries ⚠️
// until docs/adr/0007-web-integration-fit.md confirms it. There is no zone.js dependency,
// polyfill or import anywhere in this workspace — a spec asserts that.
export const appConfig: ApplicationConfig = {
  providers: [provideZonelessChangeDetection()],
};
