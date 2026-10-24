// 09.1.1-T12.1 (plan 09.1.1 §2 AC10, §10 T12): the composition-root seam the dev fixture backend is
// installed through, and the only fixture-related module production code touches.
//
// This file is the PRODUCTION stand-in: it adds no providers and imports nothing from the fixture
// modules, so a production bundle contains no fixture code and no fixture data. The `fixtures`
// build/serve configuration of `apps/web/angular.json` replaces it (`fileReplacements`) with
// `dev-backend.fixtures.ts`, which returns `provideFixtureBackend()`. Keeping the seam here is what
// makes `app.config.ts` fixture-agnostic: it always calls `provideDevBackend()`, and which backend
// answers is a build configuration decision, not a runtime flag a shipped page could flip.
//
// `apps/web/scripts/check-no-fixture-code.mjs` greps the production output for the fixture
// backend's marker strings, so a future edit that imports the fixture modules here fails the build
// instead of shipping them.

import { makeEnvironmentProviders, type EnvironmentProviders } from '@angular/core';

/** Production: nothing — the real API answers every request. */
export function provideDevBackend(): EnvironmentProviders {
  return makeEnvironmentProviders([]);
}
