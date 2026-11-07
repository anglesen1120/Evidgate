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
import { RunListPage } from '../../features/runs/list/run-list.page';
// eslint-disable-next-line evidgate/no-feature-cross-import -- verifies the review-only route loads the real product detail page.
import { RunDetailPage } from '../../features/runs/detail/run-detail.page';
// eslint-disable-next-line evidgate/no-feature-cross-import -- verifies the dev-only route's real component identity; production core never imports this page.
import { PolicyPage } from '../../features/settings/policy/policy.page';
// eslint-disable-next-line evidgate/no-feature-cross-import -- verifies the dev-only route's real component identity; production core never imports this page.
import { SecretsPage } from '../../features/settings/secrets/secrets.page';
// eslint-disable-next-line evidgate/no-feature-cross-import -- verifies the real review-only component; production core never imports it.
import { ApiTokensPage } from '../../features/settings/api-tokens/api-tokens.page';
// eslint-disable-next-line evidgate/no-feature-cross-import -- verifies the review-only route loads the real composition screen.
import { SuiteCompositionComponent } from '../../features/suites/composition/suite-composition.component';
import { ShellComponent } from '../../shell/layout/shell.component';
import { authGuard, orgScopeGuard, projectScopeGuard } from '../guards';
import { devRoutes } from './dev-routes.fixtures';

describe('dev routes (review only)', () => {
  it('mounts the shared UI gallery on one lazy route', () => {
    const table = devRoutes();

    expect(table).toHaveLength(7);
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

  it('mounts the real API tokens page behind authenticated org and read gates, review-only', async () => {
    const route = devRoutes().find((entry) => entry.path === 'dev/settings-api-tokens');
    expect(route?.component).toBe(ShellComponent);
    const org = route?.children?.[0];
    expect(org?.path).toBe('orgs/:orgId');
    expect(org?.canMatch?.slice(0, 2)).toEqual([authGuard, orgScopeGuard]);
    expect(org?.canMatch).toHaveLength(4);
    const load = org?.loadComponent;
    if (!load) throw new Error('No API tokens review route');
    expect(await load()).toBe(ApiTokensPage);
  });

  it('mounts the real Policy page under the normal authenticated org shell, review-only', async () => {
    const route = devRoutes().find((entry) => entry.path === 'dev/settings-policy');
    expect(route?.component).toBe(ShellComponent);
    const orgRoute = route?.children?.[0];
    expect(orgRoute?.path).toBe('orgs/:orgId');
    expect(orgRoute?.canMatch?.slice(0, 2)).toEqual([authGuard, orgScopeGuard]);
    // The remaining gates are the org entry and the Policy tab read grants; phase is deliberately
    // not overridden globally. The browser suite also proves both refusal paths.
    expect(orgRoute?.canMatch).toHaveLength(4);
    const loadComponent = orgRoute?.loadComponent;
    if (loadComponent === undefined) throw new Error('no lazy Policy review route');
    expect(await loadComponent()).toBe(PolicyPage);
  });

  it('mounts the real Run list page under the project scope shell, review-only', async () => {
    const route = devRoutes().find((entry) => entry.path === 'dev/run-list');
    expect(route?.component).toBe(ShellComponent);
    // The org entry keeps exactly the two gates the product shell puts on `orgs/:orgId`.
    const orgRoute = route?.children?.[0];
    expect(orgRoute?.path).toBe('orgs/:orgId');
    expect(orgRoute?.canMatch).toEqual([authGuard, orgScopeGuard]);
    // The project address is nested, not one flat `orgs/:orgId/projects/:projectId` child:
    // `projectScopeGuard` reads its project id from `segments[1]` of the segments its route still
    // has to consume, which is the product `projects/:projectId` shape. The second gate is the
    // entry's own `run:read`; the comment states it because a guard closure has no identity.
    const projectRoute = orgRoute?.children?.[0];
    expect(projectRoute?.path).toBe('projects/:projectId');
    expect(projectRoute?.canMatch?.[0]).toBe(projectScopeGuard);
    expect(projectRoute?.canMatch).toHaveLength(2);
    expect(projectRoute?.canMatch?.[1]).toBeTypeOf('function');
    // Phase is deliberately not overridden: the product route keeps `phaseGuard`.
    const loadComponent = projectRoute?.children?.find((entry) => entry.path === '')?.loadComponent;
    if (loadComponent === undefined) throw new Error('no lazy Run list review route');
    expect(await loadComponent()).toBe(RunListPage);
  });
  it('mounts detail deep links and relative row links under the same project permission gates', async () => {
    for (const path of ['dev/run-list', 'dev/run-detail']) {
      const route = devRoutes().find((entry) => entry.path === path);
      expect(route?.component).toBe(ShellComponent);
      const org = route?.children?.[0];
      expect(org?.canMatch).toEqual([authGuard, orgScopeGuard]);
      const project = org?.children?.[0];
      expect(project?.path).toBe('projects/:projectId');
      expect(project?.canMatch?.[0]).toBe(projectScopeGuard);
      expect(project?.canMatch).toHaveLength(2);
      const parent = path === 'dev/run-detail' ? project?.children?.[0] : project;
      expect(parent?.data?.['pageHeadingLevel']).toBe(1);
      const load = parent?.children?.find((entry) => entry.path === ':runId')?.loadComponent;
      if (!load) throw new Error('no lazy run detail review route');
      expect(await load()).toBe(RunDetailPage);
    }
  });
  it('mounts the real composition screen on its pinned revision deep link, review-only', async () => {
    const route = devRoutes().find((entry) => entry.path === 'dev/suite-composition');
    expect(route?.component).toBe(ShellComponent);
    const org = route?.children?.[0];
    expect(org?.path).toBe('orgs/:orgId');
    expect(org?.canMatch).toEqual([authGuard, orgScopeGuard]);
    const project = org?.children?.[0];
    expect(project?.path).toBe('projects/:projectId');
    expect(project?.canMatch?.[0]).toBe(projectScopeGuard);
    // The second gate is the entry's own `suite:read`; a guard closure has no identity, so the
    // count is what a pin can assert.
    expect(project?.canMatch).toHaveLength(2);
    // The product `suites` segment is kept, so the address the spec opens is the one the app's own
    // deep link would produce; only the phase gate differs.
    const suites = project?.children?.[0];
    expect(suites?.path).toBe('suites');
    const leaf = suites?.children?.find(
      (entry) => entry.path === 'revisions/:revisionId',
    )?.loadComponent;
    if (leaf === undefined) throw new Error('no lazy suite composition review route');
    expect(await leaf()).toBe(SuiteCompositionComponent);
  });
});
