// 09.1.1-T12.1 (plan 09.1.1 §2 AC10, §10 T12): the `fixtures` configuration's replacement for
// `dev-backend.ts` (`apps/web/angular.json` → `fileReplacements`), so `npm run start:fixtures -w
// apps/web` serves the app from `fixtures/web-api/` with no API running.
//
// It is byte-for-byte the same export name as the production seam on purpose: `app.config.ts`
// imports `provideDevBackend()` from `dev-backend.ts`, and the builder substitutes this module for
// that path at build time. A build that forgot the replacement gets the empty provider set (a
// page whose requests really go to the network), never a half-installed fixture backend.
//
// Dev-only: production code must never import this file. `scripts/check-no-fixture-code.mjs` fails
// the production build on any fixture marker string surviving in `dist/`.

import type { EnvironmentProviders } from '@angular/core';

import { provideFixtureBackend } from './fixture-backend';

/** The `fixtures` configuration: answer every API request from the fixture corpus. */
export function provideDevBackend(): EnvironmentProviders {
  return provideFixtureBackend();
}
