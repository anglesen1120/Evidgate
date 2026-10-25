// 09.1.1-T4.1 (plan 09.1.1 §10 T4; hand-over from 09.1.1-T7, S64): the review-only route table.
//
// This spec imports `dev-routes.fixtures.ts` explicitly rather than the seam `dev-routes.ts`,
// because the unit-test build uses the `development` configuration, whose `fileReplacements` already
// swap the seam for this file. What that means for the assertions: the empty production table is
// NOT what runs here — "production contains no gallery" is the production build's subject
// (`scripts/check-no-fixture-code.mjs` and the bundle grep), and the seam's emptiness is one
// `return []` in a file with no import.

import { describe, expect, it } from 'vitest';

import { UiGalleryComponent } from '../../shared/ui/gallery';
// eslint-disable-next-line evidgate/no-feature-cross-import -- verifies the dev-only route's real component identity; production core never imports this page.
import { SecretsPage } from '../../features/settings/secrets/secrets.page';
import { ShellComponent } from '../../shell/layout/shell.component';
import { authGuard, orgScopeGuard } from '../guards';
import { devRoutes } from './dev-routes.fixtures';

describe('dev routes (review only)', () => {
  it('mounts the shared UI gallery on one lazy route', () => {
    const table = devRoutes();

    expect(table).toHaveLength(2);
    expect(table[0]?.path).toBe('dev/ui-gallery');
    // A lazy boundary, so the production graph never reaches the gallery through this file.
    expect(table[0]?.component).toBeUndefined();
    expect(table[0]?.loadComponent).toBeTypeOf('function');
  });

  it('loads the gallery component itself, not a placeholder', async () => {
    const loadComponent = devRoutes()[0]?.loadComponent;
    if (loadComponent === undefined) {
      throw new Error('the dev routes table mounts no lazy component');
    }

    expect(await loadComponent()).toBe(UiGalleryComponent);
  });

  it('mounts the real Secrets page under the normal authenticated org shell, review-only', async () => {
    const route = devRoutes().find((entry) => entry.path === 'dev/settings-secrets');
    expect(route?.component).toBe(ShellComponent);
    const orgRoute = route?.children?.[0];
    expect(orgRoute?.path).toBe('orgs/:orgId');
    expect(orgRoute?.canMatch?.slice(0, 2)).toEqual([authGuard, orgScopeGuard]);
    // The remaining gates are the org entry and the Secrets tab read grants; phase is deliberately
    // not overridden globally. The browser suite also proves both refusal paths.
    expect(orgRoute?.canMatch).toHaveLength(4);
    const loadComponent = orgRoute?.loadComponent;
    if (loadComponent === undefined) throw new Error('no lazy Secrets review route');
    expect(await loadComponent()).toBe(SecretsPage);
  });
});
